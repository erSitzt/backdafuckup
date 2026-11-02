namespace Backdafuckup.Core.Extents;

/// <summary>A byte range on a virtual disk.</summary>
public readonly record struct DiskExtent(long Offset, long Length)
{
    public long End => Offset + Length;

    public override string ToString() => $"[{Offset}..{End})";
}

public static class ExtentMath
{
    /// <summary>Sorts extents, drops empty ones and merges overlapping or adjacent extents.</summary>
    public static IReadOnlyList<DiskExtent> Normalize(IEnumerable<DiskExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        var sorted = extents.Where(e => e.Length > 0).OrderBy(e => e.Offset).ToList();
        var result = new List<DiskExtent>(sorted.Count);
        foreach (var extent in sorted)
        {
            if (extent.Offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(extents), "Extent offsets must not be negative.");
            }

            if (result.Count > 0 && result[^1].End >= extent.Offset)
            {
                var last = result[^1];
                result[^1] = new DiskExtent(last.Offset, Math.Max(last.End, extent.End) - last.Offset);
            }
            else
            {
                result.Add(extent);
            }
        }

        return result;
    }

    /// <summary>Number of fixed-size chunks covering a disk (the last chunk may be short).</summary>
    public static long ChunkCount(long diskSize, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return (diskSize + chunkSize - 1) / chunkSize;
    }

    /// <summary>Length of chunk <paramref name="index"/> (only the last chunk can be shorter than chunkSize).</summary>
    public static int ChunkLength(long index, long diskSize, int chunkSize)
    {
        var offset = index * chunkSize;
        if (index < 0 || offset >= diskSize)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return (int)Math.Min(chunkSize, diskSize - offset);
    }

    /// <summary>
    /// Returns the sorted, distinct indices of all chunks touched by the given extents. Extents beyond the
    /// disk end are clipped.
    /// </summary>
    public static long[] DirtyChunks(IEnumerable<DiskExtent> extents, long diskSize, int chunkSize)
    {
        var chunkCount = ChunkCount(diskSize, chunkSize);
        var result = new List<long>();
        long next = 0; // first chunk index not yet emitted
        foreach (var extent in Normalize(extents))
        {
            if (extent.Offset >= diskSize)
            {
                break;
            }

            var end = Math.Min(extent.End, diskSize);
            var first = Math.Max(extent.Offset / chunkSize, next);
            var last = Math.Min((end - 1) / chunkSize, chunkCount - 1);
            for (var i = first; i <= last; i++)
            {
                result.Add(i);
            }

            next = Math.Max(next, last + 1);
        }

        return [.. result];
    }

    /// <summary>All chunk indices of a disk (used for full backups).</summary>
    public static long[] AllChunks(long diskSize, int chunkSize)
    {
        var count = ChunkCount(diskSize, chunkSize);
        var result = new long[count];
        for (long i = 0; i < count; i++)
        {
            result[i] = i;
        }

        return result;
    }
}
