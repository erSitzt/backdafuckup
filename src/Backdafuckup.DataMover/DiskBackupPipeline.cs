using System.Buffers;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

public sealed record DiskBackupJob
{
    public required IDiskReader Reader { get; init; }

    public required IRepositoryWriter Writer { get; init; }

    public required int ChunkSize { get; init; }

    /// <summary>Changed extents since the base, or a full read.</summary>
    public required ChangedExtents Changes { get; init; }

    /// <summary>Index of the same disk in the base restore point (required for incremental reads).</summary>
    public ChunkIndex? BaseIndex { get; init; }

    /// <summary>Chunks processed in parallel (read + hash + compress).</summary>
    public int Parallelism { get; init; } = 8;

    public IProgress<DiskBackupProgress>? Progress { get; init; }
}

public readonly record struct DiskBackupProgress(long ProcessedChunks, long TotalChunks, long ReadBytes, long StoredBytes);

public sealed record DiskBackupResult(ChunkIndex Index, DiskBackupStats Stats);

/// <summary>
/// Reads the chunks of a disk that need reading (all of them, or only CBT-dirty ones), drops all-zero chunks,
/// hashes, deduplicates and stores the rest. Unchanged chunks keep the entries of the base index, so the result
/// is always a complete index (forever-incremental).
/// </summary>
public static class DiskBackupPipeline
{
    public static async Task<DiskBackupResult> RunAsync(DiskBackupJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var capacity = job.Reader.Capacity;
        var chunkSize = job.ChunkSize;

        var incremental = !job.Changes.IsFull
            && job.BaseIndex is { } baseIndex
            && baseIndex.DiskSize == capacity
            && baseIndex.ChunkSize == chunkSize;

        var index = incremental
            ? new ChunkIndex(chunkSize, capacity, (ChunkId[])job.BaseIndex!.Entries.Clone())
            : ChunkIndex.CreateEmpty(chunkSize, capacity);
        var work = incremental
            ? ExtentMath.DirtyChunks(job.Changes.Extents, capacity, chunkSize)
            : ExtentMath.AllChunks(capacity, chunkSize);
        var baseEntries = incremental ? job.BaseIndex!.Entries : null;

        long readBytes = 0, newChunks = 0, storedBytes = 0, zeroChunks = 0, unchanged = 0, processed = 0;
        var parallelism = Math.Max(1, Math.Min(job.Parallelism, Math.Max(1, job.Reader.MaxConcurrentRequests)));
        var reportEvery = Math.Max(1, work.Length / 200);

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (chunk, ct) =>
            {
                var length = ExtentMath.ChunkLength(chunk, capacity, chunkSize);
                var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                try
                {
                    var data = buffer.AsMemory(0, length);
                    await ReadFullyAsync(job.Reader, chunk * (long)chunkSize, data, ct).ConfigureAwait(false);
                    Interlocked.Add(ref readBytes, length);

                    if (!data.Span.ContainsAnyExcept((byte)0))
                    {
                        index.Entries[chunk] = ChunkId.Zero;
                        Interlocked.Increment(ref zeroChunks);
                    }
                    else
                    {
                        var id = ChunkId.Compute(data.Span);
                        index.Entries[chunk] = id;
                        if (baseEntries is not null && baseEntries[chunk] == id)
                        {
                            // CBT reported a change, but the content is identical (e.g. rewritten with the same data).
                            Interlocked.Increment(ref unchanged);
                        }
                        else
                        {
                            var put = await job.Writer.PutChunkAsync(id, data, ct).ConfigureAwait(false);
                            if (put.Outcome == ChunkPutOutcome.Stored)
                            {
                                Interlocked.Increment(ref newChunks);
                                Interlocked.Add(ref storedBytes, put.StoredBytes);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var done = Interlocked.Increment(ref processed);
                if (job.Progress is not null && (done % reportEvery == 0 || done == work.Length))
                {
                    job.Progress.Report(new DiskBackupProgress(done, work.Length, Interlocked.Read(ref readBytes), Interlocked.Read(ref storedBytes)));
                }
            }).ConfigureAwait(false);

        var stats = new DiskBackupStats
        {
            Mode = incremental ? DiskBackupMode.Cbt : DiskBackupMode.Full,
            FullReason = incremental ? null : job.Changes.FullReason ?? "no usable base",
            ReadBytes = readBytes,
            ChangedChunks = work.Length,
            NewChunks = newChunks,
            NewStoredBytes = storedBytes,
            ZeroChunks = zeroChunks,
            UnchangedChunks = unchanged,
        };
        return new DiskBackupResult(index, stats);
    }

    private static async ValueTask ReadFullyAsync(IDiskReader reader, long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var max = reader.MaxRequestBytes > 0 ? reader.MaxRequestBytes : destination.Length;
        var position = 0;
        while (position < destination.Length)
        {
            var length = Math.Min(max, destination.Length - position);
            await reader.ReadAsync(offset + position, destination.Slice(position, length), cancellationToken).ConfigureAwait(false);
            position += length;
        }
    }
}
