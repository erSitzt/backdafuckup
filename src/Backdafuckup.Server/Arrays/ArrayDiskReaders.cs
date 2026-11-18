using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Lvhd;

namespace Backdafuckup.Server.Arrays;

/// <summary>A snapshot LUN read through the proxy as a block source for the LVM and VHD readers (requests split to the transport's limit).</summary>
internal sealed class DiskReaderBlockSource(IDiskReader reader) : IBlockSource
{
    public long Size => reader.Capacity;

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var max = Math.Max(4096, reader.MaxRequestBytes);
        for (var done = 0; done < destination.Length; done += max)
        {
            var piece = destination.Slice(done, Math.Min(max, destination.Length - done));
            await reader.ReadAsync(offset + done, piece, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A disk of a VM read through its VHD chain on the snapshot LUN. The LUN's reader belongs to the batch.</summary>
internal sealed class LvhdDiskReader(VhdChainReader chain, IDiskReader lun) : IDiskReader
{
    public long Capacity => chain.Capacity;

    public string TransportName => "array-lvhd";

    public int MaxRequestBytes => Math.Max(lun.MaxRequestBytes, chain.BlockSize);

    public int MaxConcurrentRequests => lun.MaxConcurrentRequests;

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => chain.ReadAsync(offset, destination, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// The check of an experimental reader on every run: chunks that change tracking reports unchanged since the previous
/// restore point must read exactly as they were stored then. Any difference means the reader (or the change tracking) is
/// wrong, and the restore point must not be trusted.
/// </summary>
internal static class UnchangedRegionCheck
{
    public const string Method = "cbt-unchanged";

    /// <summary>Compares up to <paramref name="regions"/> unchanged, stored chunks spread over the disk; null if there are none to compare.</summary>
    public static async Task<DiskVerification?> RunAsync(IDiskReader reader, ChangedExtents changes, ChunkIndex baseIndex, int regions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(baseIndex);
        if (changes.IsFull || baseIndex.DiskSize != reader.Capacity)
        {
            return null;
        }

        var size = baseIndex.ChunkSize;
        var changed = ExtentMath.Normalize([.. changes.Extents]);
        var candidates = new List<long>();
        for (long chunk = 0; chunk < baseIndex.Entries.LongLength; chunk++)
        {
            var start = chunk * size;
            var length = Math.Min(size, baseIndex.DiskSize - start);
            if (!baseIndex.Entries[chunk].IsZero && !Overlaps(changed, start, length))
            {
                candidates.Add(chunk);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Evenly spread over the stored, unchanged chunks.
        var picked = Enumerable.Range(0, Math.Min(regions, candidates.Count)).Select(i => candidates[(int)((long)i * candidates.Count / Math.Min(regions, candidates.Count))]).Distinct().ToList();
        var buffer = new byte[size];
        var mismatches = 0;
        foreach (var chunk in picked)
        {
            var start = chunk * size;
            var length = (int)Math.Min(size, baseIndex.DiskSize - start);
            var data = buffer.AsMemory(0, length);
            for (var done = 0; done < length;)
            {
                var piece = Math.Min(reader.MaxRequestBytes, length - done);
                await reader.ReadAsync(start + done, data.Slice(done, piece), cancellationToken).ConfigureAwait(false);
                done += piece;
            }

            var id = data.Span.IndexOfAnyExcept((byte)0) < 0 ? ChunkId.Zero : ChunkId.Compute(data.Span);
            if (id != baseIndex.Entries[chunk])
            {
                mismatches++;
            }
        }

        return new DiskVerification(Method, picked.Count, mismatches);
    }

    private static bool Overlaps(IReadOnlyList<DiskExtent> sorted, long start, long length)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var e = sorted[mid];
            if (e.Offset + e.Length <= start)
            {
                lo = mid + 1;
            }
            else if (e.Offset >= start + length)
            {
                hi = mid - 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
