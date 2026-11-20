using System.Collections.Concurrent;
using System.Net.WebSockets;
using Backdafuckup.Nbd;

namespace Backdafuckup.Proxy;

/// <summary>Where the proxy agent connects to and as whom.</summary>
/// <param name="ServerUrl">The backup server, e.g. https://backup01.corp.example:9555/.</param>
public sealed record ProxyAgentSettings(Uri ServerUrl, Guid ProxyId, string AgentKey);

/// <summary>What the agent tells the server about the machine it runs on.</summary>
/// <param name="Platform">Where the agent runs (<see cref="Core.Proxies.ProxyPlatforms"/>); null: this process's.</param>
/// <param name="HotAdd">Whether it offers HotAdd: only the Linux agent, which may run in a VM.</param>
public sealed record ProxyHostInfo(
    string HostName, string? SmbiosUuid, string? BiosSerial, IReadOnlyList<string> Addresses, string AgentVersion, string? FilesHash, string? Platform = null, bool HotAdd = true);

/// <summary>
/// The proxy agent's side of the control connection: connects out to the server (and again after a break), reports
/// itself and its devices, and carries out the server's commands. All decisions stay on the server.
/// </summary>
/// <param name="connect">Opens a WebSocket to a URL with a bearer token (TLS pinning is the caller's business).</param>
/// <param name="iscsi">The iSCSI initiator for SAN access; null: none.</param>
/// <param name="sanCheck">The SAN self-check (null: SAN access is offered only with an initiator and passes no check).</param>
/// <param name="update">Installs the server's program file (a self-update) and restarts the agent; null: the agent does not update itself.</param>
/// <param name="prepareSan">
/// Makes the settings SAN access needs (<see cref="SanPreparation"/>), so that a proxy works without anyone on it; run
/// once for each new set of missing settings, never while a disk is open. Null: the agent changes nothing.
/// </param>
public sealed class ProxyAgentClient(
    ProxyAgentSettings settings, ProxyHostInfo host, IBlockDevices devices, Func<Uri, string, CancellationToken, Task<WebSocket>> connect, Action<string> log,
    IIscsiInitiator? iscsi = null, Func<CancellationToken, Task<IReadOnlyList<string>>>? sanCheck = null, Func<ProxyAgentMessage, Task>? update = null,
    Func<CancellationToken, Task>? prepareSan = null)
{
    private readonly ConcurrentDictionary<string, OpenHandle> _handles = new(StringComparer.Ordinal);

    /// <summary>The missing settings the last preparation ran for (or left behind): not tried again until they change.</summary>
    private string? _preparedFor;

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
        var (initiator, sanRefusal) = await EnsureSanAsync(cancellationToken).ConfigureAwait(false);
        if (sanRefusal is not null && iscsi is not null)
        {
            log($"No SAN access: {sanRefusal.Replace("\n", "; ", StringComparison.Ordinal)}" +
                (prepareSan is null ? " (backdafuckup-proxy prepare-san makes these settings)." : " (left after the agent's own preparation; see above)."));
        }

        await ProxyAgentProtocol.SendAsync(socket, send, new ProxyAgentMessage
        {
            Type = ProxyMessageTypes.Hello,
            ProtocolVersion = ProxyAgentProtocol.Version,
            AgentVersion = host.AgentVersion,
            Platform = host.Platform ?? Core.Proxies.ProxyPlatforms.Current,
            FilesHash = host.FilesHash,
            HostName = host.HostName,
            SmbiosUuid = host.SmbiosUuid,
            BiosSerial = host.BiosSerial,
            Addresses = host.Addresses,
            Capabilities =
            [
                .. host.HotAdd ? [ProxyAgentProtocol.CapabilityHotAdd] : Array.Empty<string>(),
                ProxyAgentProtocol.CapabilityNbd,
                .. sanRefusal is null ? [ProxyAgentProtocol.CapabilitySan] : Array.Empty<string>(),
            ],
            Devices = await devices.ListAsync(cancellationToken).ConfigureAwait(false),
            InitiatorName = initiator,
            SanRefusal = sanRefusal,
        }, cancellationToken).ConfigureAwait(false);
        log($"Connected to {settings.ServerUrl} as proxy {settings.ProxyId}.");

        var running = new List<Task>();
        running.Add(Task.Run(() => WatchSanAsync(initiator, sanRefusal, session), CancellationToken.None));
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

    /// <summary>How often the SAN state (initiator name, self-check) is read again while connected.</summary>
    internal static TimeSpan SanRecheckInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The SAN state, after the machine was prepared for SAN access if something is missing that no preparation has
    /// run for yet (an admin installed open-iscsi, say). Never while a disk is open.
    /// </summary>
    private async Task<(string? Initiator, string? Refusal)> EnsureSanAsync(CancellationToken cancellationToken)
    {
        var state = await SanStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Refusal is null || prepareSan is null || iscsi is null || state.Refusal == _preparedFor || !_handles.IsEmpty)
        {
            return state;
        }

        _preparedFor = state.Refusal;
        log($"No SAN access yet ({state.Refusal.Replace("\n", "; ", StringComparison.Ordinal)}): preparing this machine (prepare-san).");
        try
        {
            await prepareSan(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"prepare-san failed: {ex.Message}");
        }

        state = await SanStateAsync(cancellationToken).ConfigureAwait(false);
        _preparedFor = state.Refusal;
        return state;
    }

    /// <summary>The initiator name and every reason why the proxy offers no SAN access, one per line (null: it does).</summary>
    private async Task<(string? Initiator, string? Refusal)> SanStateAsync(CancellationToken cancellationToken)
    {
        if (iscsi is null)
        {
            return (null, "no iSCSI initiator (open-iscsi) is installed");
        }

        var problems = new List<string>();
        var initiator = await iscsi.InitiatorNameAsync(cancellationToken).ConfigureAwait(false);
        if (initiator is null)
        {
            problems.Add(await iscsi.InitiatorProblemAsync(cancellationToken).ConfigureAwait(false) ?? "no iSCSI initiator name");
        }

        if (sanCheck is not null)
        {
            problems.AddRange(await sanCheck(cancellationToken).ConfigureAwait(false));
        }

        return (initiator, problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// Reads the SAN state again every so often; when it changed (open-iscsi installed, the LVM settings made) and no disk
    /// is open, the connection is ended so that the next one tells the server (the console shows it within a minute).
    /// </summary>
    private async Task WatchSanAsync(string? initiator, string? refusal, CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                await Task.Delay(SanRecheckInterval, session.Token).ConfigureAwait(false);
                var (now, nowRefusal) = await EnsureSanAsync(session.Token).ConfigureAwait(false);
                if ((now, nowRefusal) != (initiator, refusal) && _handles.IsEmpty)
                {
                    log($"SAN access changed ({nowRefusal?.Replace("\n", "; ", StringComparison.Ordinal) ?? $"available, initiator {now}"}); connecting again to tell the server.");
                    await session.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
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
                ProxyMessageTypes.IscsiProbe => iscsi is null
                    ? Error(command, $"{host.HostName} has no iSCSI initiator")
                    : Reply(command) with { PortalReach = await iscsi.ProbeAsync(command.Portals ?? [], cancellationToken).ConfigureAwait(false), Addresses = NetworkAddresses.Ipv4() },
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
            return Error(command, $"{host.HostName}: this agent does not update itself (not installed as {ProxySelfUpdate.ProgramFile(host.Platform ?? Core.Proxies.ProxyPlatforms.Current)})");
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
