using System.Buffers;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

/// <summary>A disk of a restore point exposed as an image: zero chunks read as zeros, others from the chunk store.</summary>
public sealed class RestorePointDiskImage : IDiskImageSource
{
    private readonly IChunkReader _chunks;
    private readonly ChunkIndex _index;

    public RestorePointDiskImage(IChunkReader chunks, ChunkIndex index)
    {
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        DataExtents = BuildDataExtents(index);
    }

    public long Capacity => _index.DiskSize;

    public int ChunkSize => _index.ChunkSize;

    public IReadOnlyList<DiskExtent> DataExtents { get; }

    public static async Task<RestorePointDiskImage> OpenAsync(IRestorePointReader restorePoint, string diskKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restorePoint);
        var index = await restorePoint.ReadDiskIndexAsync(diskKey, cancellationToken).ConfigureAwait(false);
        return new RestorePointDiskImage(restorePoint.Chunks, index);
    }

    public Stream OpenSequential(int prefetchChunks = 16, int workers = 8) =>
        new SequentialImageStream(_chunks, _index, Math.Max(1, prefetchChunks), Math.Max(1, workers));

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            var position = 0;
            while (position < destination.Length)
            {
                var absolute = offset + position;
                var chunk = absolute / ChunkSize;
                var inChunk = (int)(absolute % ChunkSize);
                var length = ExtentMath.ChunkLength(chunk, Capacity, ChunkSize);
                var take = Math.Min(length - inChunk, destination.Length - position);
                var id = _index.Entries[chunk];
                if (id.IsZero)
                {
                    destination.Span.Slice(position, take).Clear();
                }
                else
                {
                    await _chunks.ReadAsync(id, buffer, cancellationToken).ConfigureAwait(false);
                    buffer.AsSpan(inChunk, take).CopyTo(destination.Span[position..]);
                }

                position += take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<DiskExtent> BuildDataExtents(ChunkIndex index)
    {
        var extents = new List<DiskExtent>();
        for (var i = 0; i < index.Entries.Length; i++)
        {
            if (!index.Entries[i].IsZero)
            {
                extents.Add(new DiskExtent(i * (long)index.ChunkSize, ExtentMath.ChunkLength(i, index.DiskSize, index.ChunkSize)));
            }
        }

        return [.. ExtentMath.Normalize(extents)];
    }

    /// <summary>
    /// Forward-only stream over the whole image. Chunks are fetched, decompressed and verified ahead of the reader
    /// by up to <c>workers</c> concurrent tasks within a window of <c>prefetch</c> chunks; output order is preserved.
    /// </summary>
    private sealed class SequentialImageStream : Stream
    {
        private readonly IChunkReader _chunks;
        private readonly ChunkIndex _index;
        private readonly int _prefetch;
        private readonly SemaphoreSlim _workers;
        private readonly Queue<Task<byte[]?>> _window = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly byte[] _zero;
        private long _nextToSchedule;
        private byte[]? _current;
        private int _currentLength;
        private int _currentPosition;
        private long _position;
        private int _disposed;

        public SequentialImageStream(IChunkReader chunks, ChunkIndex index, int prefetch, int workers)
        {
            _chunks = chunks;
            _index = index;
            _prefetch = prefetch;
            _workers = new SemaphoreSlim(workers);
            _zero = new byte[index.ChunkSize];
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _index.DiskSize;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= Length || buffer.IsEmpty)
            {
                return 0;
            }

            if (_current is null || _currentPosition >= _currentLength)
            {
                await AdvanceAsync(cancellationToken).ConfigureAwait(false);
            }

            var take = Math.Min(buffer.Length, _currentLength - _currentPosition);
            _current.AsSpan(_currentPosition, take).CopyTo(buffer.Span);
            _currentPosition += take;
            _position += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cancel.CancelAsync().ConfigureAwait(false);
            while (_window.Count > 0)
            {
                try
                {
                    ReturnBuffer(await _window.Dequeue().ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            }

            ReturnBuffer(_current);
            _current = null;
            _cancel.Dispose();
            _workers.Dispose();
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        private async ValueTask AdvanceAsync(CancellationToken cancellationToken)
        {
            ReturnBuffer(_current);
            _current = null;
            FillWindow();
            var chunk = _position / _index.ChunkSize;
            var data = await _window.Dequeue().WaitAsync(cancellationToken).ConfigureAwait(false);
            _current = data ?? _zero;
            _currentLength = ExtentMath.ChunkLength(chunk, _index.DiskSize, _index.ChunkSize);
            _currentPosition = 0;
            FillWindow();
        }

        private void FillWindow()
        {
            while (_window.Count < _prefetch && _nextToSchedule < _index.Entries.LongLength)
            {
                var chunk = _nextToSchedule++;
                var id = _index.Entries[chunk];
                _window.Enqueue(id.IsZero ? Task.FromResult<byte[]?>(null) : FetchAsync(id));
            }
        }

        private async Task<byte[]?> FetchAsync(Core.Repository.ChunkId id)
        {
            var token = _cancel.Token;
            await _workers.WaitAsync(token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(_index.ChunkSize);
            try
            {
                await _chunks.ReadAsync(id, buffer, token).ConfigureAwait(false);
                return buffer;
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
            finally
            {
                _workers.Release();
            }
        }

        private void ReturnBuffer(byte[]? buffer)
        {
            if (buffer is not null && !ReferenceEquals(buffer, _zero))
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
