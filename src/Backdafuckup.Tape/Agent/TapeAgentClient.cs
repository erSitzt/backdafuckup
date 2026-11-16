using System.Collections.Concurrent;
using System.Net.WebSockets;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Agent;

/// <summary>
/// The tape agent's side of the control connection: reports the tape devices of its host and runs the SCSI commands the
/// server sends, one at a time per device. It keeps the connection up, reconnecting after a pause that grows to 30 s.
/// Devices it opened are closed when the connection ends.
/// </summary>
public sealed class TapeAgentClient(ITapeHost host, Func<CancellationToken, Task<WebSocket>> connect, string server, string agentVersion, Action<string> log)
{
    private const int MaxData = 1024 * 1024;

    public TimeSpan MaxPause { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Runs until stopped; connection errors are logged and retried.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pause = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                using var socket = await connect(cancellationToken).ConfigureAwait(false);
                connected = true;
                await SessionAsync(socket, cancellationToken).ConfigureAwait(false);
                log($"The backup server {server} closed the connection. Again in {pause.TotalSeconds:0} s.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"{(connected ? "The connection to" : "Cannot connect to")} the backup server {server}: {ex.Message}. Again in {pause.TotalSeconds:0} s.");
            }

            try
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pause = connected ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(MaxPause.TotalSeconds, pause.TotalSeconds * 2));
        }
    }

    /// <summary>One connection: hello, then requests until the server closes it.</summary>
    public async Task SessionAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var send = new SemaphoreSlim(1, 1);
        var handles = new ConcurrentDictionary<long, OpenDevice>();
        var requests = new ConcurrentDictionary<long, Task>();
        long nextHandle = 0;
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var devices = await host.DiscoverAsync(session.Token).ConfigureAwait(false);
            await TapeAgentProtocol.SendAsync(
                socket,
                send,
                new TapeAgentMessage
                {
                    Type = TapeAgentMessageTypes.Hello, Protocol = TapeAgentProtocol.Version, AgentVersion = agentVersion, HostName = Environment.MachineName, Devices = devices,
                },
                default,
                session.Token).ConfigureAwait(false);
            log($"Connected to the backup server {server}; reported {devices.Count} device(s): " +
                (devices.Count == 0 ? "none" : string.Join(", ", devices.Select(d => $"{d.Path} {d.Vendor} {d.Model} serial {d.Serial ?? "(open elsewhere)"}"))) + ".");

            long sequence = 0;
            while (await TapeAgentProtocol.ReceiveAsync(socket, session.Token).ConfigureAwait(false) is { } message)
            {
                var key = Interlocked.Increment(ref sequence);
                var task = Task.Run(() => HandleAsync(message.Header, message.Data), CancellationToken.None);
                requests[key] = task;
                _ = task.ContinueWith(_ => requests.TryRemove(key, out var _), TaskScheduler.Default);
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(requests.Values).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Requests answer for themselves; a hanging command does not keep the devices open.
            }

            foreach (var (_, open) in handles)
            {
                await open.Device.DisposeAsync().ConfigureAwait(false);
            }
        }

        async Task Reply(TapeAgentMessage reply, ReadOnlyMemory<byte> data = default)
        {
            try
            {
                await TapeAgentProtocol.SendAsync(socket, send, reply, data, session.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection is gone; the server fails the request itself.
            }
        }

        async Task HandleAsync(TapeAgentMessage request, byte[] data)
        {
            try
            {
                switch (request.Type)
                {
                    case TapeAgentMessageTypes.Discover:
                        var found = await host.DiscoverAsync(session.Token).ConfigureAwait(false);
                        await Reply(new TapeAgentMessage { Type = TapeAgentMessageTypes.Devices, Id = request.Id, Devices = found }).ConfigureAwait(false);
                        break;

                    case TapeAgentMessageTypes.Open:
                        var device = await host.OpenAsync(request.Serial ?? string.Empty, session.Token).ConfigureAwait(false);
                        var handle = Interlocked.Increment(ref nextHandle);
                        handles[handle] = new OpenDevice(device, new SemaphoreSlim(1, 1));
                        log($"Opened {device.Path} (serial {request.Serial}) for the backup server.");
                        await Reply(new TapeAgentMessage
                        {
                            Type = TapeAgentMessageTypes.Opened, Id = request.Id, Handle = handle, Path = device.Path, MaxTransfer = Math.Min(device.MaxTransferLength, MaxData),
                        }).ConfigureAwait(false);
                        break;

                    case TapeAgentMessageTypes.Close:
                        if (handles.TryRemove(request.Handle, out var closing))
                        {
                            await closing.Gate.WaitAsync(session.Token).ConfigureAwait(false);
                            await closing.Device.DisposeAsync().ConfigureAwait(false);
                        }

                        await Reply(new TapeAgentMessage { Type = TapeAgentMessageTypes.Closed, Id = request.Id }).ConfigureAwait(false);
                        break;

                    case TapeAgentMessageTypes.Scsi:
                        await ScsiAsync(request, data).ConfigureAwait(false);
                        break;

                    default:
                        await Reply(Error(request, $"The tape agent does not know the request '{request.Type}'; update it to the server's version.")).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
            }
            catch (ScsiTransportException ex)
            {
                await Reply(Error(request, ex.Message) with { Transport = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Reply(Error(request, ex.Message)).ConfigureAwait(false);
            }
        }

        async Task ScsiAsync(TapeAgentMessage request, byte[] data)
        {
            if (!handles.TryGetValue(request.Handle, out var open))
            {
                await Reply(Error(request, $"No device is open with handle {request.Handle} on the tape agent {Environment.MachineName}.")).ConfigureAwait(false);
                return;
            }

            if (request.Cdb is not { Length: > 0 and <= 32 } cdb || request.Length is < 0 or > MaxData || (request.Direction == ScsiDataDirection.Out && data.Length != request.Length))
            {
                await Reply(Error(request, "The SCSI command is malformed (CDB, length or data).")).ConfigureAwait(false);
                return;
            }

            var buffer = request.Direction switch
            {
                ScsiDataDirection.Out => data,
                ScsiDataDirection.In => new byte[request.Length],
                _ => [],
            };
            var command = new ScsiCommand
            {
                Name = request.Name ?? "SCSI command",
                Cdb = cdb,
                Direction = request.Direction,
                Buffer = buffer,
                Timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs, 1000, (long)TimeSpan.FromHours(4).TotalMilliseconds)),
                SensitiveData = request.Sensitive,
            };

            ScsiResult result;
            await open.Gate.WaitAsync(session.Token).ConfigureAwait(false);
            try
            {
                result = await open.Device.ExecuteAsync(command, session.Token).ConfigureAwait(false);
            }
            finally
            {
                open.Gate.Release();
            }

            var back = request.Direction == ScsiDataDirection.In ? buffer.AsMemory(0, result.Transferred(command)) : ReadOnlyMemory<byte>.Empty;
            await Reply(
                new TapeAgentMessage { Type = TapeAgentMessageTypes.Result, Id = request.Id, Status = result.Status, Sense = result.Sense?.Raw, Residual = result.Residual },
                back).ConfigureAwait(false);
        }
    }

    private static TapeAgentMessage Error(TapeAgentMessage request, string error) => new() { Type = TapeAgentMessageTypes.Error, Id = request.Id, Error = error };

    private sealed record OpenDevice(IScsiDevice Device, SemaphoreSlim Gate);
}
