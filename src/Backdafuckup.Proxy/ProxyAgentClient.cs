using System.Collections.Concurrent;
using System.Net.WebSockets;
using Backdafuckup.Nbd;

namespace Backdafuckup.Proxy;

/// <summary>Where the proxy agent connects to and as whom.</summary>
/// <param name="ServerUrl">The backup server, e.g. https://backup01.corp.example:9555/.</param>
public sealed record ProxyAgentSettings(Uri ServerUrl, Guid ProxyId, string AgentKey);

/// <summary>What the agent tells the server about the machine it runs on.</summary>
public sealed record ProxyHostInfo(string HostName, string? SmbiosUuid, string? BiosSerial, IReadOnlyList<string> Addresses, string AgentVersion, string? FilesHash);

/// <summary>
/// The proxy agent's side of the control connection: connects out to the server (and again after a break), reports
/// itself and its devices, and carries out the server's commands. All decisions stay on the server.
/// </summary>
/// <param name="connect">Opens a WebSocket to a URL with a bearer token (TLS pinning is the caller's business).</param>
/// <param name="iscsi">The iSCSI initiator for SAN access; null: none.</param>
/// <param name="sanCheck">The SAN self-check (null: SAN access is offered only with an initiator and passes no check).</param>
/// <param name="update">Installs the server's program file (a self-update) and restarts the agent; null: the agent does not update itself.</param>
public sealed class ProxyAgentClient(
    ProxyAgentSettings settings, ProxyHostInfo host, IBlockDevices devices, Func<Uri, string, CancellationToken, Task<WebSocket>> connect, Action<string> log,
    IIscsiInitiator? iscsi = null, Func<CancellationToken, Task<string?>>? sanCheck = null, Func<ProxyAgentMessage, Task>? update = null)
{
    private readonly ConcurrentDictionary<string, OpenHandle> _handles = new(StringComparer.Ordinal);

    /// <summary>Runs until cancelled; a lost connection is opened again after a growing pause (1 s to 30 s).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pause = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
                pause = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or InvalidDataException or OperationCanceledException)
            {
                log($"Connection to {settings.ServerUrl} lost: {ex.Message}. Connecting again in {pause.TotalSeconds:0} s.");
            }

            try
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            pause = TimeSpan.FromSeconds(Math.Min(30, pause.TotalSeconds * 2));
        }

        foreach (var handle in _handles.Keys.ToList())
        {
            await CloseAsync(handle, deleteDevice: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One control connection, until the server or the network ends it.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var socket = await connect(new Uri(settings.ServerUrl, ProxyAgentProtocol.ControlPath(settings.ProxyId)), settings.AgentKey, cancellationToken).ConfigureAwait(false);
        using var send = new SemaphoreSlim(1, 1);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var initiator = iscsi is null ? null : await iscsi.InitiatorNameAsync(cancellationToken).ConfigureAwait(false);
        var sanRefusal = iscsi is null || initiator is null ? "no iSCSI initiator (open-iscsi) is installed"
            : sanCheck is null ? null : await sanCheck(cancellationToken).ConfigureAwait(false);
        if (sanRefusal is not null && iscsi is not null)
        {
            log($"No SAN access: {sanRefusal}");
        }

        await ProxyAgentProtocol.SendAsync(socket, send, new ProxyAgentMessage
        {
            Type = ProxyMessageTypes.Hello,
            ProtocolVersion = ProxyAgentProtocol.Version,
            AgentVersion = host.AgentVersion,
            FilesHash = host.FilesHash,
            HostName = host.HostName,
            SmbiosUuid = host.SmbiosUuid,
            BiosSerial = host.BiosSerial,
            Addresses = host.Addresses,
            Capabilities = sanRefusal is null
                ? [ProxyAgentProtocol.CapabilityHotAdd, ProxyAgentProtocol.CapabilityNbd, ProxyAgentProtocol.CapabilitySan]
                : [ProxyAgentProtocol.CapabilityHotAdd, ProxyAgentProtocol.CapabilityNbd],
            Devices = await devices.ListAsync(cancellationToken).ConfigureAwait(false),
            InitiatorName = initiator,
            SanRefusal = sanRefusal,
        }, cancellationToken).ConfigureAwait(false);
        log($"Connected to {settings.ServerUrl} as proxy {settings.ProxyId}.");

        var running = new List<Task>();
        try
        {
            while (await ProxyAgentProtocol.ReceiveAsync(socket, session.Token).ConfigureAwait(false) is { } command)
            {
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(() => HandleAsync(socket, send, command, session.Token), CancellationToken.None));
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
            {
                // The connection ended while commands ran.
            }
        }
    }

    private async Task HandleAsync(WebSocket socket, SemaphoreSlim send, ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        ProxyAgentMessage reply;
        try
        {
            reply = command.Type switch
            {
                ProxyMessageTypes.Rescan => await RescanAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.WaitDevice => await WaitDeviceAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.Open => await OpenAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.Tunnel => await TunnelAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.Release => await ReleaseAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.IscsiLogin => await IscsiLoginAsync(command, cancellationToken).ConfigureAwait(false),
                ProxyMessageTypes.Update => Update(command),
                _ => Error(command, $"unknown command '{command.Type}'"),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or WebSocketException or HttpRequestException or TimeoutException)
        {
            reply = Error(command, ex.Message);
        }

        try
        {
            await ProxyAgentProtocol.SendAsync(socket, send, reply, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The server is gone; it repeats what it still needs after reconnecting.
        }
    }

    /// <summary>Starts the self-update the server offered (when no disk is open) and replies at once; the update ends with a restart.</summary>
    private ProxyAgentMessage Update(ProxyAgentMessage command)
    {
        if (update is null)
        {
            return Error(command, $"{host.HostName}: this agent does not update itself (not installed as {ProxySelfUpdate.ProgramName})");
        }

        if (!_handles.IsEmpty)
        {
            return Error(command, $"{host.HostName}: {_handles.Count} disk(s) open; the update waits for the next connection");
        }

        if (command.Sha256 is not { Length: 64 } || command.Path is not { Length: > 0 } || command.SizeBytes is not > 0)
        {
            return Error(command, $"{host.HostName}: the update offer names no file, size and SHA-256");
        }

        log($"Updating the agent to version {command.AgentVersion} ({command.SizeBytes} bytes from {command.Path}).");
        _ = Task.Run(() => update(command));
        return Reply(command);
    }

    /// <summary>Logs in to the array's target (once) and asks its sessions for new LUNs; replies with the devices.</summary>
    private async Task<ProxyAgentMessage> IscsiLoginAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        if (iscsi is null)
        {
            return Error(command, $"{host.HostName} has no iSCSI initiator");
        }

        await iscsi.LoginAsync(command.Portals ?? [], command.TargetIqn ?? throw new InvalidOperationException("iscsi-login without a target"), cancellationToken).ConfigureAwait(false);
        await iscsi.RescanAsync(cancellationToken).ConfigureAwait(false);
        log($"Logged in to {command.TargetIqn} at {string.Join(", ", command.Portals ?? [])}.");
        return Reply(command) with { Devices = await devices.ListAsync(cancellationToken).ConfigureAwait(false) };
    }

    private async Task<ProxyAgentMessage> RescanAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        await devices.RescanAsync(cancellationToken).ConfigureAwait(false);
        return Reply(command) with { Devices = await devices.ListAsync(cancellationToken).ConfigureAwait(false) };
    }

    private async Task<ProxyAgentMessage> WaitDeviceAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        var serial = DeviceSerials.Normalize(command.Serial ?? throw new InvalidOperationException("wait-device without a serial"));
        var deadline = DateTime.UtcNow.AddMilliseconds(command.TimeoutMs ?? 30_000);
        IReadOnlyList<ProxyDevice> seen = [];
        var rescanned = false;
        while (true)
        {
            seen = await devices.ListAsync(cancellationToken).ConfigureAwait(false);
            if (seen.FirstOrDefault(d => d.Serial == serial) is { } device)
            {
                if (command.SizeBytes is { } size && device.SizeBytes != size)
                {
                    return Error(command, $"device {device.Path} (serial {serial}) has {device.SizeBytes} bytes, not the expected {size}");
                }

                return Reply(command) with { Device = device };
            }

            if (DateTime.UtcNow >= deadline)
            {
                return Error(command,
                    $"no device with serial {serial} appeared within {(command.TimeoutMs ?? 30_000) / 1000} s on {host.HostName}; devices: " +
                    string.Join(", ", seen.Select(d => $"{d.Name} ({d.Serial ?? "no serial"})")) +
                    ". Is disk.EnableUUID=TRUE set in the proxy VM's configuration (it needs a power cycle)?");
            }

            if (!rescanned)
            {
                await devices.RescanAsync(cancellationToken).ConfigureAwait(false);
                rescanned = true;
                continue;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ProxyAgentMessage> OpenAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        var handle = command.Handle ?? throw new InvalidOperationException("open without a handle");
        var device = (await devices.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Path == command.Path)
            ?? throw new InvalidOperationException($"no device {command.Path} on {host.HostName}");
        var opened = await devices.OpenAsync(device, command.Writable == true, cancellationToken).ConfigureAwait(false);
        if (!_handles.TryAdd(handle, new OpenHandle(opened)))
        {
            opened.Dispose();
            throw new InvalidOperationException($"handle {handle} is open already");
        }

        log($"Opened {device.Path} ({device.SizeBytes} bytes, serial {device.Serial}) {(command.Writable == true ? "writable" : "read-only")} as {handle}.");
        return Reply(command) with { Device = device };
    }

    private async Task<ProxyAgentMessage> TunnelAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        var handle = command.Handle is { } h && _handles.TryGetValue(h, out var open) ? open : throw new InvalidOperationException($"no open handle {command.Handle}");
        var socket = await connect(new Uri(settings.ServerUrl, ProxyAgentProtocol.TunnelPath(settings.ProxyId, command.StreamId ?? Guid.Empty)), command.Token ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        handle.Streams.Add(Task.Run(
            async () =>
            {
                await using var stream = WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: true);
                try
                {
                    await NbdServer.ServeAsync(stream, handle.Device, new NbdServerOptions(), handle.Stop.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or NbdException or ObjectDisposedException)
                {
                    // The server closed the stream, or the handle was released.
                }
            },
            CancellationToken.None));
        return Reply(command);
    }

    private async Task<ProxyAgentMessage> ReleaseAsync(ProxyAgentMessage command, CancellationToken cancellationToken)
    {
        await CloseAsync(command.Handle ?? string.Empty, command.DeleteDevice == true, cancellationToken).ConfigureAwait(false);
        return Reply(command);
    }

    private async Task CloseAsync(string handle, bool deleteDevice, CancellationToken cancellationToken)
    {
        if (!_handles.TryRemove(handle, out var open))
        {
            return;
        }

        await open.Stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(open.Streams).ConfigureAwait(false);
        }
        finally
        {
            if (!open.Device.ReadOnly)
            {
                await open.Device.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            open.Device.Dispose();
            open.Stop.Dispose();
        }

        if (deleteDevice)
        {
            await devices.DeleteAsync(open.Device.Device, cancellationToken).ConfigureAwait(false);
        }

        log($"Released {open.Device.Device.Path} ({handle}){(deleteDevice ? " and removed it from the kernel" : string.Empty)}.");
    }

    private static ProxyAgentMessage Reply(ProxyAgentMessage command) => new() { Type = ProxyMessageTypes.Reply, Id = command.Id };

    private static ProxyAgentMessage Error(ProxyAgentMessage command, string error) => Reply(command) with { Error = error };

    private sealed class OpenHandle(IOpenDevice device)
    {
        public IOpenDevice Device { get; } = device;

        public ConcurrentBag<Task> Streams { get; } = [];

        public CancellationTokenSource Stop { get; } = new();
    }
}
