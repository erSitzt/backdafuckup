using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Server.Agents;

namespace Backdafuckup.Server.Movers;

/// <summary>
/// The server's side of the channels mover pods connect to: one per volume and run under
/// <c>/api/agent/movers/{id}</c>, opened with a one-time token. A backup channel takes the volume the mover sends and
/// hands it to the backup pipeline as a sequential disk; a restore channel serves a restore point's disk and waits for
/// the mover's report.
/// </summary>
public sealed class MoverChannels(AgentSettingsStore settings, ILogger<MoverChannels> logger) : IMoverChannels
{
    private readonly ConcurrentDictionary<Guid, MoverChannel> _open = new();

    /// <summary>How long a channel waits for its mover: scheduling the pod, pulling the image, attaching the volume.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMinutes(15);

    internal ILogger Logger => logger;

    public async Task<IMoverBackupChannel> OpenBackupAsync(string description, long capacity, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        var (id, endpoint) = await NewEndpointAsync(cancellationToken).ConfigureAwait(false);
        return Register(new BackupChannel(this, id, endpoint, description, capacity), connectTimeout);
    }

    public async Task<IMoverRestoreChannel> OpenRestoreAsync(string description, IDiskImageSource image, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (id, endpoint) = await NewEndpointAsync(cancellationToken).ConfigureAwait(false);
        return Register(new RestoreChannel(this, id, endpoint, description, image), connectTimeout);
    }

    /// <summary>The open channel with this id, if the request carries its token; null for anything else.</summary>
    internal MoverChannel? Find(Guid id, string? authorization)
    {
        const string Scheme = "Bearer ";
        if (!_open.TryGetValue(id, out var channel) || authorization is null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, channel.TokenBytes) ? channel : null;
    }

    internal void Close(Guid id) => _open.TryRemove(id, out _);

    private T Register<T>(T channel, TimeSpan? connectTimeout)
        where T : MoverChannel
    {
        _open[channel.Id] = channel;
        channel.StartConnectTimer(connectTimeout ?? ConnectTimeout);
        return channel;
    }

    private async Task<(Guid Id, MoverEndpoint Endpoint)> NewEndpointAsync(CancellationToken cancellationToken)
    {
        var url = await settings.GetServerUrlAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The address under which the cluster's pods reach the backup server is not set (Guest agents → Settings → server address). The mover pods use it like the guest agents.");
        var root = new Uri(url.TrimEnd('/') + "/");
        var id = Guid.NewGuid();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (id, new MoverEndpoint(new Uri(root, $"api/agent/movers/{id:D}/data"), new Uri(root, $"api/agent/movers/{id:D}/result"), token, settings.ServerPins()));
    }
}

/// <summary>One channel: its token, its single connection, and the timer that fails it when its mover never connects.</summary>
internal abstract class MoverChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description) : IAsyncDisposable
{
    private readonly CancellationTokenSource _connectTimer = new();
    private int _claimed;
    private int _disposed;

    public Guid Id => id;

    public MoverEndpoint Endpoint => endpoint;

    public string Description => description;

    internal byte[] TokenBytes { get; } = Encoding.UTF8.GetBytes(endpoint.Token);

    protected MoverChannels Owner => owner;

    /// <summary>The first connection of the mover takes the channel; a second one (a restarted pod) is refused.</summary>
    internal bool TryClaim()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0)
        {
            return false;
        }

        _connectTimer.Cancel();
        return true;
    }

    internal void StartConnectTimer(TimeSpan timeout) => _ = WaitForConnectionAsync(timeout);

    /// <summary>The mover's report at <see cref="MoverEndpoint.ResultUrl"/>.</summary>
    internal abstract void Report(MoverResult result);

    public void Abort(Exception error) => Fail(error);

    protected abstract void Fail(Exception error);

    /// <summary>Ends a channel nobody waits for any more.</summary>
    protected abstract void Cancel();

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            owner.Close(id);
            _connectTimer.Cancel();
            Cancel();
        }

        return ValueTask.CompletedTask;
    }

    private async Task WaitForConnectionAsync(TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, _connectTimer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Connected or closed.
        }

        if (Volatile.Read(ref _claimed) == 0)
        {
            Owner.Logger.LogWarning("The mover for {Volume} did not connect to {Url} within {Timeout}.", description, endpoint.DataUrl, timeout);
            Fail(new TimeoutException(
                $"The mover for {description} did not connect to {endpoint.DataUrl} within {timeout.TotalMinutes:0} minutes: its pod did not start (image pull, volume attach) or cannot reach the backup server."));
        }
    }
}

/// <summary>Takes the volume a mover sends (PUT) and feeds it to the backup pipeline's sink.</summary>
internal sealed class BackupChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description, long capacity)
    : MoverChannel(owner, id, endpoint, description), IMoverBackupChannel
{
    private readonly TaskCompletionSource<(DiskDataSink Sink, CancellationToken Token)> _reader = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<long> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _streaming;

    public long Capacity => capacity;

    public string TransportName => "mover";

    public async Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Interlocked.Exchange(ref _streaming, 1) != 0)
        {
            throw new InvalidOperationException($"The volume {Description} is streamed once.");
        }

        _reader.TrySetResult((sink, cancellationToken));
        await _done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the mover's request body into the pipeline; returns the data bytes.</summary>
    internal async Task<long> ReceiveAsync(Stream body, CancellationToken requestAborted)
    {
        var (sink, streamToken) = await _reader.Task.WaitAsync(requestAborted).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, streamToken);
        try
        {
            var sent = await MoverStream.ReadHeaderAsync(body, linked.Token).ConfigureAwait(false);
            if (sent != capacity)
            {
                throw new InvalidDataException($"The mover's device for {Description} has {sent} bytes, the volume {capacity}.");
            }

            var total = await MoverStream.ReadRecordsAsync(body, capacity, sink, linked.Token).ConfigureAwait(false);
            _done.TrySetResult(total);
            return total;
        }
        catch (Exception ex)
        {
            Fail(ex is OperationCanceledException && requestAborted.IsCancellationRequested && !streamToken.IsCancellationRequested
                ? new IOException($"The connection of the mover for {Description} broke off.", ex)
                : ex is EndOfStreamException or IOException
                    ? new IOException($"Receiving {Description} from its mover failed: {ex.Message}", ex)
                    : ex);
            throw;
        }
    }

    internal override void Report(MoverResult result)
    {
        if (result.Error is { } error)
        {
            Fail(new IOException($"The mover for {Description} failed: {error}"));
        }
    }

    protected override void Fail(Exception error) => _done.TrySetException(error);

    protected override void Cancel() => _done.TrySetCanceled();
}

/// <summary>Serves a restore point's disk to a mover (GET) and completes with the mover's report.</summary>
internal sealed class RestoreChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description, IDiskImageSource image)
    : MoverChannel(owner, id, endpoint, description), IMoverRestoreChannel
{
    /// <summary>Bytes per record: reads of the image run this far ahead in parallel.</summary>
    private const int PieceBytes = 4 * 1024 * 1024;
    private const int ReadAhead = 8;
    private static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<long> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _served = -1;

    public Task<long> Completion => _completion.Task;

    /// <summary>Writes the image's data regions to the response; zero regions are left out (the new volume reads zeros there).</summary>
    internal async Task ServeAsync(Stream response, CancellationToken cancellationToken)
    {
        try
        {
            await MoverStream.WriteHeaderAsync(response, image.Capacity, cancellationToken).ConfigureAwait(false);
            var total = await WriteDataAsync(response, cancellationToken).ConfigureAwait(false);
            await MoverStream.WriteEndAsync(response, total, cancellationToken).ConfigureAwait(false);
            await response.FlushAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _served, total);
        }
        catch (Exception ex)
        {
            // A mover that stops reading usually reports why (a volume too small, a write error): its report comes first.
            _ = FailUnlessReportedAsync(new IOException($"Sending {Description} to its mover failed: {ex.Message}", ex));
            throw;
        }
    }

    private async Task FailUnlessReportedAsync(Exception error)
    {
        await Task.WhenAny(_completion.Task, Task.Delay(ReportGrace)).ConfigureAwait(false);
        Fail(error);
    }

    internal override void Report(MoverResult result)
    {
        var served = Interlocked.Read(ref _served);
        if (result.Error is { } error)
        {
            Fail(new IOException($"The mover for {Description} failed: {error}"));
        }
        else if (served < 0)
        {
            Fail(new InvalidDataException($"The mover for {Description} reports success before the server sent the whole image."));
        }
        else if (result.DataBytes != served)
        {
            Fail(new InvalidDataException($"The mover for {Description} wrote {result.DataBytes} bytes of data, the server sent {served}."));
        }
        else
        {
            _completion.TrySetResult(served);
        }
    }

    protected override void Fail(Exception error) => _completion.TrySetException(error);

    protected override void Cancel() => _completion.TrySetCanceled();

    internal static IEnumerable<(long Offset, int Length)> Pieces(IEnumerable<DiskExtent> extents)
    {
        foreach (var extent in extents)
        {
            for (var position = extent.Offset; position < extent.End;)
            {
                var length = (int)Math.Min(PieceBytes - (position % PieceBytes), extent.End - position);
                yield return (position, length);
                position += length;
            }
        }
    }

    private async Task<long> WriteDataAsync(Stream response, CancellationToken cancellationToken)
    {
        var window = new Queue<(long Offset, int Length, byte[] Buffer, Task Read)>();
        long total = 0;
        using var pieces = Pieces(image.DataExtents).GetEnumerator();
        try
        {
            while (true)
            {
                while (window.Count < ReadAhead && pieces.MoveNext())
                {
                    var (offset, length) = pieces.Current;
                    var buffer = ArrayPool<byte>.Shared.Rent(length);
                    window.Enqueue((offset, length, buffer, image.ReadAsync(offset, buffer.AsMemory(0, length), cancellationToken).AsTask()));
                }

                if (!window.TryDequeue(out var next))
                {
                    return total;
                }

                try
                {
                    await next.Read.ConfigureAwait(false);
                    await MoverStream.WriteRecordAsync(response, next.Offset, next.Buffer.AsMemory(0, next.Length), cancellationToken).ConfigureAwait(false);
                    total += next.Length;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(next.Buffer);
                }
            }
        }
        finally
        {
            // Reads still running use their buffers: wait for them before returning the buffers.
            foreach (var pending in window)
            {
                try
                {
                    await pending.Read.ConfigureAwait(false);
                }
                catch
                {
                    // The first error is already on its way.
                }

                ArrayPool<byte>.Shared.Return(pending.Buffer);
            }
        }
    }
}
