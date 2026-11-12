using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
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

    public async Task<IMoverRestoreChannel> OpenRestoreAsync(
        string description, IDiskImageSource image, CancellationToken cancellationToken, TimeSpan? connectTimeout = null, IProgress<long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (id, endpoint) = await NewEndpointAsync(cancellationToken).ConfigureAwait(false);
        return Register(new RestoreChannel(this, id, endpoint, description, image, progress), connectTimeout);
    }

    public async Task<IMoverFilesChannel> OpenFilesAsync(string description, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
    {
        var (id, endpoint) = await NewEndpointAsync(cancellationToken).ConfigureAwait(false);
        return Register(new FilesChannel(this, id, endpoint, description), connectTimeout);
    }

    public async Task<IMoverTreeChannel> OpenTreeBackupAsync(string description, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
    {
        var (id, endpoint) = await NewEndpointAsync(cancellationToken).ConfigureAwait(false);
        return Register(new TreeBackupChannel(this, id, endpoint, description), connectTimeout);
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
    private static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(30);

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
            var brokeOff = ex is OperationCanceledException && requestAborted.IsCancellationRequested && !streamToken.IsCancellationRequested;
            if (brokeOff || ex is EndOfStreamException or IOException)
            {
                // A mover that stops sending was usually stopped (its pod deleted or failed): the watch of the pod or the
                // mover's report says why, and comes first.
                _ = FailUnlessReportedAsync(brokeOff
                    ? new IOException($"The connection of the mover for {Description} broke off.", ex)
                    : new IOException($"Receiving {Description} from its mover failed: {ex.Message}", ex));
            }
            else
            {
                Fail(ex);
            }

            throw;
        }
    }

    private async Task FailUnlessReportedAsync(Exception error)
    {
        await Task.WhenAny(_done.Task, Task.Delay(ReportGrace)).ConfigureAwait(false);
        Fail(error);
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

/// <summary>Takes the file tree a mover sends (PUT) and hands it to its consumer (the file tree builder).</summary>
internal sealed class TreeBackupChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description)
    : MoverChannel(owner, id, endpoint, description), IMoverTreeChannel
{
    private static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<(Func<Stream, CancellationToken, Task> Consume, CancellationToken Token)> _consumer =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _consuming;

    public async Task ConsumeAsync(Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (Interlocked.Exchange(ref _consuming, 1) != 0)
        {
            throw new InvalidOperationException($"The file tree of {Description} is consumed once.");
        }

        _consumer.TrySetResult((consume, cancellationToken));
        await _done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands the mover's request body to the consumer.</summary>
    internal async Task ReceiveAsync(Stream body, CancellationToken requestAborted)
    {
        var (consume, consumerToken) = await _consumer.Task.WaitAsync(requestAborted).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, consumerToken);
        try
        {
            await consume(body, linked.Token).ConfigureAwait(false);
            _done.TrySetResult();
        }
        catch (Exception ex)
        {
            var brokeOff = ex is OperationCanceledException && requestAborted.IsCancellationRequested && !consumerToken.IsCancellationRequested;
            if (brokeOff || ex is EndOfStreamException or IOException)
            {
                // A mover that stops sending was usually stopped: the watch of its pod or its report says why, and comes first.
                _ = FailUnlessReportedAsync(brokeOff
                    ? new IOException($"The connection of the mover for {Description} broke off.", ex)
                    : new IOException($"Receiving the files of {Description} from its mover failed: {ex.Message}", ex));
            }
            else
            {
                Fail(ex);
            }

            throw;
        }
    }

    private async Task FailUnlessReportedAsync(Exception error)
    {
        await Task.WhenAny(_done.Task, Task.Delay(ReportGrace)).ConfigureAwait(false);
        Fail(error);
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
internal sealed class RestoreChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description, IDiskImageSource image, IProgress<long>? progress = null)
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
            progress?.Report(image.Capacity);
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
                    progress?.Report(next.Offset + next.Length);
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

/// <summary>
/// Serves the files the server writes to <see cref="Content"/> to a mover (GET) through a pipe, which holds the writer
/// back while the mover is behind; completes with the mover's report of what it put in place.
/// </summary>
internal sealed class FilesChannel : MoverChannel, IMoverFilesChannel
{
    private static readonly TimeSpan ReportGrace = TimeSpan.FromSeconds(30);

    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 8 * 1024 * 1024, resumeWriterThreshold: 2 * 1024 * 1024, useSynchronizationContext: false));
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<MoverFileReport> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _served = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _failed = new();
    private Exception? _error;

    public FilesChannel(MoverChannels owner, Guid id, MoverEndpoint endpoint, string description)
        : base(owner, id, endpoint, description)
    {
        Content = new ContentStream(this);
        _ = _connected.Task.ContinueWith(t => t.Exception, TaskScheduler.Default);
        _ = _completion.Task.ContinueWith(t => t.Exception, TaskScheduler.Default);
    }

    public Task Connected => _connected.Task;

    public Stream Content { get; }

    public bool IsUsable => !_failed.IsCancellationRequested;

    public Task<MoverFileReport> Completion => _completion.Task;

    /// <summary>Copies what the server writes to the mover's response until the writer closes the stream.</summary>
    internal async Task ServeAsync(Stream response, CancellationToken cancellationToken)
    {
        _connected.TrySetResult();
        try
        {
            await _pipe.Reader.CopyToAsync(response, cancellationToken).ConfigureAwait(false);
            await response.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _pipe.Reader.CompleteAsync().ConfigureAwait(false);
            _served.TrySetResult();
        }
        catch (Exception ex)
        {
            await _pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);

            // A mover that stops reading usually reports why: its report comes first.
            _ = FailUnlessReportedAsync(new IOException($"Sending the files for {Description} to its mover failed: {ex.Message}", ex));
            throw;
        }
    }

    internal override void Report(MoverResult result)
    {
        if (result.Error is { } error)
        {
            Fail(new IOException($"The mover for {Description} failed: {error}"));
        }
        else if (result.Files is not { } files)
        {
            Fail(new InvalidDataException($"The mover for {Description} reported no files; is it an older version of the mover image?"));
        }
        else
        {
            // The mover may read the end of the stream before the server has noted it sent everything.
            _ = CompleteWhenServedAsync(files);
        }
    }

    private async Task CompleteWhenServedAsync(MoverFileReport files)
    {
        if (await Task.WhenAny(_served.Task, Task.Delay(ReportGrace)).ConfigureAwait(false) == _served.Task)
        {
            _completion.TrySetResult(files);
        }
        else
        {
            Fail(new InvalidDataException($"The mover for {Description} reports success, but the server has not sent all files."));
        }
    }

    protected override void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _error, error, null);
        _connected.TrySetException(error);
        _completion.TrySetException(error);
        Stop();
    }

    protected override void Cancel()
    {
        Interlocked.CompareExchange(ref _error, new OperationCanceledException($"The file restore into {Description} was ended."), null);
        _connected.TrySetCanceled();
        _completion.TrySetCanceled();
        Stop();
    }

    /// <summary>Releases a writer waiting for the mover and ends the copy to the mover (both thread-safe).</summary>
    private void Stop()
    {
        _failed.Cancel();
        try
        {
            _pipe.Reader.CancelPendingRead();
        }
        catch (InvalidOperationException)
        {
            // The copy has ended.
        }
    }

    private async Task FailUnlessReportedAsync(Exception error)
    {
        await Task.WhenAny(_completion.Task, Task.Delay(ReportGrace)).ConfigureAwait(false);
        Fail(error);
    }

    /// <summary>Blocking writes into the pipe; each write waits while the mover is behind and fails once the channel has.</summary>
    private sealed class ContentStream(FilesChannel channel) : Stream
    {
        private int _closed;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Volatile.Read(ref _closed) != 0 || !channel.IsUsable)
            {
                throw Broken();
            }

            channel._pipe.Writer.Write(buffer);
            FlushResult result;
            try
            {
                result = channel._pipe.Writer.FlushAsync(channel._failed.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw Broken();
            }

            if (result.IsCompleted || result.IsCanceled)
            {
                throw Broken();
            }
        }

        public override void WriteByte(byte value) => Write([value]);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                channel._pipe.Writer.Complete();
            }

            base.Dispose(disposing);
        }

        private IOException Broken() =>
            channel._error is { } error
                ? new IOException(error.Message, error)
                : new IOException($"The stream to the mover for {channel.Description} is closed.");
    }
}
