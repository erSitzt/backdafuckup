using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Testing;

/// <summary>A disk image in memory; like a restore point, its data extents are the chunks that are not all zeros.</summary>
public sealed class ByteDiskImage(byte[] content, int chunkSize = 1024 * 1024) : IDiskImageSource
{
    public long Capacity => content.LongLength;

    public int ChunkSize => chunkSize;

    public IReadOnlyList<DiskExtent> DataExtents { get; } = [.. ExtentMath.Normalize(Enumerable.Range(0, (int)((content.LongLength + chunkSize - 1) / chunkSize))
        .Select(i => new DiskExtent(i * (long)chunkSize, Math.Min(chunkSize, content.LongLength - (i * (long)chunkSize))))
        .Where(e => content.AsSpan((int)e.Offset, (int)e.Length).ContainsAnyExcept((byte)0)))];

    public Stream OpenSequential(int prefetchChunks = 16, int workers = 8) => new MemoryStream(content, writable: false);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        content.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
        return ValueTask.CompletedTask;
    }
}
