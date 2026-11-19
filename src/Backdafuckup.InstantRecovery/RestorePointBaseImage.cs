using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.InstantRecovery;

/// <summary>
/// A disk of a restore point as the base of an instant recovery's disk file: zero chunks read as zeros, other chunks come
/// through the shared <see cref="AsyncChunkCache"/>. A read that continues where the previous one ended counts as
/// sequential (a guest booting, a Storage vMotion copying the disk); then the next chunks are loaded ahead.
/// </summary>
public sealed class RestorePointBaseImage : IBaseImage
{
    private readonly ChunkIndex _index;
    private readonly IChunkReader _chunks;
    private readonly AsyncChunkCache _cache;
    private readonly int _readAhead;
    private long _nextOffset = -1;

    public RestorePointBaseImage(ChunkIndex index, IChunkReader chunks, AsyncChunkCache cache, int readAheadChunks = 8)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        ArgumentOutOfRangeException.ThrowIfNegative(readAheadChunks);
        _readAhead = readAheadChunks;
        long allocated = 0;
        for (long chunk = 0; chunk < index.Entries.LongLength; chunk++)
        {
            if (!index.Entries[chunk].IsZero)
            {
                allocated += ExtentMath.ChunkLength(chunk, index.DiskSize, index.ChunkSize);
            }
        }

        AllocatedBytes = allocated;
    }

    public long Size => _index.DiskSize;

    public long AllocatedBytes { get; }

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"A read of {destination.Length} bytes at {offset} is outside the disk of {Size} bytes.");
        }

        var sequential = Interlocked.Exchange(ref _nextOffset, offset + destination.Length) == offset;
        var size = _index.ChunkSize;
        var position = 0;
        while (position < destination.Length)
        {
            var absolute = offset + position;
            var chunk = absolute / size;
            var inChunk = (int)(absolute % size);
            var length = ExtentMath.ChunkLength(chunk, Size, size);
            var take = Math.Min(length - inChunk, destination.Length - position);
            var id = _index.Entries[chunk];
            if (id.IsZero)
            {
                destination.Span.Slice(position, take).Clear();
            }
            else
            {
                var block = await _cache.GetAsync(id, _chunks, size, cancellationToken).ConfigureAwait(false);
                if (block.Length < inChunk + take)
                {
                    throw new InvalidDataException($"Chunk {id} (disk offset {chunk * (long)size}) holds {block.Length} bytes, but the disk index needs {inChunk + take}.");
                }

                block.Data.AsSpan(inChunk, take).CopyTo(destination.Span[position..]);
            }

            position += take;
        }

        if (sequential && _readAhead > 0)
        {
            var next = ((offset + destination.Length - 1) / size) + 1;
            for (var chunk = next; chunk < next + _readAhead && chunk < _index.Entries.LongLength; chunk++)
            {
                _cache.Prefetch(_index.Entries[chunk], _chunks, size);
            }
        }
    }
}
