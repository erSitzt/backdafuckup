using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.FileLevel;

/// <summary>
/// A restore point disk as a read-only, seekable stream for file system parsers. Zero chunks read as zeros, other
/// chunks come through the shared <see cref="ChunkCache"/>.
/// </summary>
/// <remarks>
/// Synchronous and not thread-safe (it has a position): every parser instance gets its own stream, and reads run on
/// file-level worker threads, never on request threads.
/// </remarks>
public sealed class RestorePointDiskStream : Stream
{
    private readonly ChunkIndex _index;
    private readonly IChunkReader _chunks;
    private readonly ChunkCache _cache;
    private long _position;

    public RestorePointDiskStream(ChunkIndex index, IChunkReader chunks, ChunkCache cache)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _index.DiskSize;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = (int)Math.Min(buffer.Length, Math.Max(0, Length - _position));
        var done = 0;
        while (done < total)
        {
            var chunk = _position / _index.ChunkSize;
            var inChunk = (int)(_position % _index.ChunkSize);
            var chunkLength = ExtentMath.ChunkLength(chunk, _index.DiskSize, _index.ChunkSize);
            var take = Math.Min(chunkLength - inChunk, total - done);
            var id = _index.Entries[chunk];
            if (id.IsZero)
            {
                buffer.Slice(done, take).Clear();
            }
            else
            {
                var data = _cache.Get(id, _chunks, _index.ChunkSize);
                if (data.Length < inChunk + take)
                {
                    throw new InvalidDataException($"Chunk {id} holds {data.Length} bytes, but the disk index needs {inChunk + take}.");
                }

                data.Data.AsSpan(inChunk, take).CopyTo(buffer[done..]);
            }

            done += take;
            _position += take;
        }

        return total;
    }

    /// <summary>Synchronous on purpose (see the class remarks).</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
