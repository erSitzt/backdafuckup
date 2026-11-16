using System.Collections.Concurrent;
using System.Net.WebSockets;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Agent;

/// <summary>
/// The server's side of a connected tape agent: a <see cref="ITapeHost"/> whose devices answer through the agent's
/// control WebSocket. Requests carry an id, so commands for several drives run side by side.
/// </summary>
public sealed class TapeAgentConnection(WebSocket socket, string name, TapeAgentMessage hello) : ITapeHost
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<(TapeAgentMessage Header, byte[] Data)>> _pending = new();
    private long _next;
    private volatile string? _lost;

    /// <summary>For messages: "tape agent lab-01".</summary>
    public string Name => name;

    public string? HostName => hello.HostName;

    public string? AgentVersion => hello.AgentVersion;

    /// <summary>What the agent reported last (at hello or the last scan).</summary>
    public IReadOnlyList<ScsiDeviceInfo> Devices { get; private set; } = hello.Devices ?? [];

    public bool Connected => _lost is null;

    /// <summary>Reads the agent's answers until the connection ends; then every open request fails.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await TapeAgentProtocol.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false) is { } message)
            {
                if (_pending.TryRemove(message.Header.Id, out var waiting))
                {
                    waiting.TrySetResult(message);
                }
            }

            _lost = $"The connection to the {name} was closed.";
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidDataException or OperationCanceledException)
        {
            _lost = $"The connection to the {name} was lost: {ex.Message}";
        }
        finally
        {
            _lost ??= $"The connection to the {name} ended.";
            foreach (var (id, waiting) in _pending)
            {
                if (_pending.TryRemove(id, out _))
                {
                    waiting.TrySetException(new ScsiTransportException(_lost));
                }
            }
        }
    }

    public async Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var (reply, _) = await RequestAsync(new TapeAgentMessage { Type = TapeAgentMessageTypes.Discover }, default, RequestTimeout, cancellationToken).ConfigureAwait(false);
        Devices = Expect(reply, TapeAgentMessageTypes.Devices).Devices ?? [];
        return Devices;
    }

    public async Task<IScsiDevice> OpenAsync(string serial, CancellationToken cancellationToken)
    {
        var (reply, _) = await RequestAsync(new TapeAgentMessage { Type = TapeAgentMessageTypes.Open, Serial = serial }, default, RequestTimeout, cancellationToken).ConfigureAwait(false);
        var opened = Expect(reply, TapeAgentMessageTypes.Opened);
        return new AgentScsiDevice(this, opened.Handle, $"{name}: {opened.Path}", opened.MaxTransfer);
    }

    /// <summary>The agent runs posted commands in order and in chains (protocol 2).</summary>
    public bool CanPost => hello.Protocol >= 2;

    /// <summary>Sends a request and returns its answer to come, once the request (and its data) is on the connection.</summary>
    internal async Task<Task<(TapeAgentMessage Header, byte[] Data)>> PostAsync(TapeAgentMessage message, ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_lost is { } lost)
        {
            throw new ScsiTransportException(lost);
        }

        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<(TapeAgentMessage, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await TapeAgentProtocol.SendAsync(socket, _send, message with { Id = id }, data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new ScsiTransportException($"The connection to the {name} was lost: {ex.Message}", ex);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        if (_lost is { } gone && _pending.TryRemove(id, out _))
        {
            throw new ScsiTransportException(gone);
        }

        return AwaitAsync(id, answer.Task, message, timeout);
    }

    private async Task<(TapeAgentMessage Header, byte[] Data)> AwaitAsync(long id, Task<(TapeAgentMessage, byte[])> answer, TapeAgentMessage message, TimeSpan timeout)
    {
        try
        {
            return await answer.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ScsiTransportException($"The {name} did not answer {message.Name ?? message.Type} within {timeout.TotalMinutes:0.#} minutes.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    internal async Task<(TapeAgentMessage Header, byte[] Data)> RequestAsync(TapeAgentMessage message, ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_lost is { } lost)
        {
            throw new ScsiTransportException(lost);
        }

        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<(TapeAgentMessage, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await TapeAgentProtocol.SendAsync(socket, _send, message with { Id = id }, data, cancellationToken).ConfigureAwait(false);
            if (_lost is { } gone && _pending.TryRemove(id, out _))
            {
                throw new ScsiTransportException(gone);
            }

            return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ScsiTransportException($"The {name} did not answer {message.Name ?? message.Type} within {timeout.TotalMinutes:0.#} minutes.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new ScsiTransportException($"The connection to the {name} was lost: {ex.Message}", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private TapeAgentMessage Expect(TapeAgentMessage reply, string type) => reply.Type == type
        ? reply
        : reply.Type == TapeAgentMessageTypes.Error
            ? throw (reply.Transport ? new ScsiTransportException($"{name}: {reply.Error}") : new IOException($"{name}: {reply.Error}"))
            : throw new InvalidDataException($"The {name} answered '{reply.Type}' instead of '{type}'.");

    /// <summary>A device opened on the agent; commands go through the control connection.</summary>
    private sealed class AgentScsiDevice(TapeAgentConnection connection, long handle, string path, int maxTransfer) : IPipelinedScsiDevice
    {
        private const int MaxData = 1024 * 1024;
        private int _closed;

        public string Path => path;

        public int MaxTransferLength => maxTransfer is > 0 and < MaxData ? maxTransfer : MaxData;

        public bool CanPost => connection.CanPost;

        public async ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken)
        {
            var (reply, data) = await connection.RequestAsync(
                Message(command, null), command.Direction == ScsiDataDirection.Out ? command.Buffer : ReadOnlyMemory<byte>.Empty, command.Timeout + TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return Result(command, reply, data);
        }

        public async ValueTask<Task<ScsiResult>> PostAsync(ScsiCommand command, long chain, CancellationToken cancellationToken)
        {
            var answer = await connection.PostAsync(
                Message(command, chain), command.Direction == ScsiDataDirection.Out ? command.Buffer : ReadOnlyMemory<byte>.Empty, command.Timeout + TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return ResultAsync(command, answer);
        }

        private async Task<ScsiResult> ResultAsync(ScsiCommand command, Task<(TapeAgentMessage Header, byte[] Data)> answer)
        {
            var (reply, data) = await answer.ConfigureAwait(false);
            return Result(command, reply, data);
        }

        private TapeAgentMessage Message(ScsiCommand command, long? chain)
        {
            if (command.Buffer.Length > MaxData)
            {
                throw new ArgumentException($"{command.Name} on {path}: a tape agent takes at most {MaxData / 1024} KiB per command, not {command.Buffer.Length / 1024} KiB.");
            }

            return new TapeAgentMessage
            {
                Type = TapeAgentMessageTypes.Scsi,
                Handle = handle,
                Name = command.Name,
                Cdb = command.Cdb,
                Direction = command.Direction,
                Length = command.Buffer.Length,
                TimeoutMs = (long)command.Timeout.TotalMilliseconds,
                Sensitive = command.SensitiveData,
                Chain = chain,
            };
        }

        private ScsiResult Result(ScsiCommand command, TapeAgentMessage reply, byte[] data)
        {
            if (reply.Type == TapeAgentMessageTypes.Error)
            {
                throw reply.Skipped
                    ? new ScsiChainSkippedException($"{command.Name} on {path}: {reply.Error}")
                    : reply.Transport
                        ? new ScsiTransportException($"{command.Name} on {path}: {reply.Error}")
                        : new IOException($"{command.Name} on {path}: {reply.Error}");
            }

            if (command.Direction == ScsiDataDirection.In)
            {
                data.AsSpan(0, Math.Min(data.Length, command.Buffer.Length)).CopyTo(command.Buffer.Span);
            }

            return new ScsiResult(reply.Status, reply.Sense is { Length: > 0 } sense ? SenseData.Parse(sense) : null, reply.Residual);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0 || !connection.Connected)
            {
                return;
            }

            try
            {
                await connection.RequestAsync(new TapeAgentMessage { Type = TapeAgentMessageTypes.Close, Handle = handle }, default, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // The agent closes its devices when the connection ends.
            }
        }
    }
}
