using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

public sealed record DiskBackupJob
{
    /// <summary>Random-access (<see cref="IDiskReader"/>) or streaming (<see cref="ISequentialDiskReader"/>) source.</summary>
    public required IDiskSource Reader { get; init; }

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

/// <summary>
/// Counters of a disk backup. The categories <see cref="UnchangedBytes"/>, <see cref="ZeroBytes"/>,
/// <see cref="DedupedBytes"/> and <see cref="NewDataBytes"/> add up to <see cref="ProcessedBytes"/>.
/// </summary>
public sealed record DiskBackupCounters
{
    public long CapacityBytes { get; init; }

    /// <summary>Part of the disk that has been dealt with (read, or skipped as unchanged).</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>Bytes received from the source.</summary>
    public long ReadBytes { get; init; }

    /// <summary>Not processed because change tracking reports no change since the previous restore point.</summary>
    public long UnchangedBytes { get; init; }

    /// <summary>All-zero chunks (not stored).</summary>
    public long ZeroBytes { get; init; }

    /// <summary>Data that was already in the repository (including chunks identical to the previous restore point).</summary>
    public long DedupedBytes { get; init; }

    /// <summary>Uncompressed size of the new chunks.</summary>
    public long NewDataBytes { get; init; }

    /// <summary>Bytes written to the repository (compressed new chunks).</summary>
    public long StoredBytes { get; init; }

    public long ZeroChunks { get; init; }

    public long DedupedChunks { get; init; }

    public long NewChunks { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Share of time the source kept the pipeline busy (0..1): reading or waiting for data.</summary>
    public double SourceLoad { get; init; }

    /// <summary>Share of time spent hashing and compressing (0..1).</summary>
    public double ProcessingLoad { get; init; }

    /// <summary>Share of time spent writing to the repository (0..1).</summary>
    public double TargetLoad { get; init; }
}

public readonly record struct DiskBackupProgress(long ProcessedChunks, long TotalChunks, DiskBackupCounters Counters)
{
    public long ReadBytes => Counters.ReadBytes;

    public long StoredBytes => Counters.StoredBytes;
}

public sealed record DiskBackupResult(ChunkIndex Index, DiskBackupStats Stats, DiskBackupCounters Counters);

/// <summary>
/// Reads the chunks of a disk that need reading (all of them, or only CBT-dirty ones), drops all-zero chunks,
/// hashes, deduplicates and stores the rest. Unchanged chunks keep the entries of the base index, so the result
/// is always a complete index (forever-incremental).
/// Random-access sources are read chunk by chunk in parallel. Streaming sources are read once from start to end;
/// chunks that CBT reports as unchanged are skipped after transfer, the others are processed in parallel.
/// The time spent reading, processing (hash, compression) and writing is measured to find the bottleneck.
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
        // A full read of a disk whose allocated areas are known reads only the chunks that touch them; the others stay zero.
        var allocated = incremental ? null : job.Changes.Allocated;
        var work = incremental
            ? ExtentMath.DirtyChunks(job.Changes.Extents, capacity, chunkSize)
            : allocated is not null ? ExtentMath.DirtyChunks(allocated, capacity, chunkSize) : ExtentMath.AllChunks(capacity, chunkSize);
        var state = new RunState(job, index, incremental ? job.BaseIndex!.Entries : null, capacity);
        if (allocated is not null)
        {
            var readBytes = work.Sum(chunk => (long)ExtentMath.ChunkLength(chunk, capacity, chunkSize));
            state.ZeroBytes = capacity - readBytes;
            state.ZeroChunks = ExtentMath.ChunkCount(capacity, chunkSize) - work.Length;
        }

        switch (job.Reader)
        {
            case IDiskReader reader:
                await RunRandomAccessAsync(reader, state, work, cancellationToken).ConfigureAwait(false);
                break;
            case ISequentialDiskReader sequential:
                await RunSequentialAsync(sequential, state, work, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Unsupported disk source {job.Reader.GetType().Name}.");
        }

        var counters = state.Snapshot(capacity);
        var stats = new DiskBackupStats
        {
            Mode = incremental ? DiskBackupMode.Cbt : DiskBackupMode.Full,
            FullReason = incremental ? null : job.Changes.FullReason ?? "no usable base",
            ReadBytes = counters.ReadBytes,
            ChangedChunks = work.Length,
            NewChunks = counters.NewChunks,
            NewStoredBytes = counters.StoredBytes,
            ZeroChunks = counters.ZeroChunks,
            UnchangedChunks = state.IdenticalToBase,
            Transport = job.Reader.TransportName,
            DedupedChunks = counters.DedupedChunks,
            NewDataBytes = counters.NewDataBytes,
            DurationSeconds = Math.Round(counters.Elapsed.TotalSeconds, 3),
        };
        return new DiskBackupResult(index, stats, counters);
    }

    private static async Task RunRandomAccessAsync(IDiskReader reader, RunState state, long[] work, CancellationToken cancellationToken)
    {
        var capacity = reader.Capacity;
        var chunkSize = state.Job.ChunkSize;
        var parallelism = Math.Max(1, Math.Min(state.Job.Parallelism, Math.Max(1, reader.MaxConcurrentRequests)));
        state.SourceConcurrency = parallelism;
        state.WorkerConcurrency = parallelism;
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
                    var start = Stopwatch.GetTimestamp();
                    await ReadFullyAsync(reader, chunk * (long)chunkSize, data, ct).ConfigureAwait(false);
                    Interlocked.Add(ref state.SourceTicks, Stopwatch.GetTimestamp() - start);
                    Interlocked.Add(ref state.ReadBytes, length);
                    await ProcessChunkAsync(state, chunk, data, ct).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var done = Interlocked.Increment(ref state.ProcessedWork);
                if (state.Job.Progress is not null && (done % reportEvery == 0 || done == work.Length))
                {
                    // Incremental reads skip unchanged chunks: the covered part of the disk grows with the dirty chunks.
                    var processed = work.Length == 0 ? capacity : (long)(capacity * (done / (double)work.Length));
                    state.Job.Progress.Report(new DiskBackupProgress(done, work.Length, state.Snapshot(processed)));
                }
            }).ConfigureAwait(false);
    }

    private static async Task RunSequentialAsync(ISequentialDiskReader reader, RunState state, long[] work, CancellationToken cancellationToken)
    {
        var capacity = reader.Capacity;
        var chunkSize = state.Job.ChunkSize;
        var totalChunks = ExtentMath.ChunkCount(capacity, chunkSize);
        var wanted = new bool[totalChunks];
        foreach (var chunk in work)
        {
            wanted[chunk] = true;
        }

        var seen = new bool[totalChunks];
        var parallelism = Math.Max(1, state.Job.Parallelism);
        state.SourceConcurrency = 1;
        state.WorkerConcurrency = parallelism;
        var queue = Channel.CreateBounded<PendingChunk>(new BoundedChannelOptions(parallelism * 2) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reportEvery = Math.Max(1L, totalChunks / 200);

        var workers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var pending in queue.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            await ProcessChunkAsync(state, pending.Chunk, pending.Buffer.AsMemory(0, pending.Length), cts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(pending.Buffer);
                        }
                    }
                }
                catch
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            },
            CancellationToken.None)).ToList();

        // The producer assembles chunks from the stream. A chunk is complete once the stream moves past its end.
        long current = -1;
        byte[]? buffer = null;
        long lastEnd = 0;

        // Time outside the sink is time the source needed to deliver the next data.
        var sourceSince = Stopwatch.GetTimestamp();

        async ValueTask FlushAsync(CancellationToken ct)
        {
            if (current < 0)
            {
                return;
            }

            if (buffer is not null)
            {
                var pending = new PendingChunk(current, buffer, ExtentMath.ChunkLength(current, capacity, chunkSize));
                buffer = null;
                await queue.Writer.WriteAsync(pending, ct).ConfigureAwait(false);
            }

            var processed = current + 1;
            if (state.Job.Progress is not null && (processed % reportEvery == 0 || processed == totalChunks))
            {
                state.Job.Progress.Report(new DiskBackupProgress(processed, totalChunks, state.Snapshot(Math.Min(capacity, processed * (long)chunkSize))));
            }

            current = -1;
        }

        async ValueTask SinkAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            Interlocked.Add(ref state.SourceTicks, Stopwatch.GetTimestamp() - sourceSince);
            if (offset < lastEnd || offset + data.Length > capacity)
            {
                throw new InvalidDataException($"The disk stream delivered {data.Length} bytes at offset {offset}, expected an offset >= {lastEnd} within {capacity} bytes.");
            }

            lastEnd = offset + data.Length;
            Interlocked.Add(ref state.ReadBytes, data.Length);
            while (!data.IsEmpty)
            {
                var chunk = offset / chunkSize;
                if (chunk != current)
                {
                    await FlushAsync(ct).ConfigureAwait(false);
                    current = chunk;
                    seen[chunk] = true;
                    if (wanted[chunk])
                    {
                        buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                        Array.Clear(buffer, 0, ExtentMath.ChunkLength(chunk, capacity, chunkSize));
                    }
                }

                var within = (int)(offset - (chunk * (long)chunkSize));
                var take = Math.Min(data.Length, ExtentMath.ChunkLength(chunk, capacity, chunkSize) - within);
                if (buffer is not null)
                {
                    data[..take].CopyTo(buffer.AsMemory(within));
                }

                offset += take;
                data = data[take..];
            }

            sourceSince = Stopwatch.GetTimestamp();
        }

        try
        {
            await reader.StreamAsync(SinkAsync, cts.Token).ConfigureAwait(false);
            Interlocked.Add(ref state.SourceTicks, Stopwatch.GetTimestamp() - sourceSince);
            await FlushAsync(cts.Token).ConfigureAwait(false);
            queue.Writer.Complete();
        }
        catch (Exception ex)
        {
            queue.Writer.TryComplete(ex);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // Inspected below.
            }

            // A failing worker cancels the stream; report the worker's error rather than the cancellation.
            var workerError = workers.Where(t => t.IsFaulted).Select(t => t.Exception!.GetBaseException()).FirstOrDefault(e => e is not OperationCanceledException);
            if (ex is OperationCanceledException && workerError is not null && !cancellationToken.IsCancellationRequested)
            {
                ExceptionDispatchInfo.Throw(workerError);
            }

            throw;
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        // Wanted chunks the stream never touched are zeros (unallocated in a sparse export).
        foreach (var chunk in work)
        {
            if (!seen[chunk])
            {
                state.Index.Entries[chunk] = ChunkId.Zero;
                state.ZeroChunks++;
                state.ZeroBytes += ExtentMath.ChunkLength(chunk, capacity, chunkSize);
            }
        }

        state.Job.Progress?.Report(new DiskBackupProgress(totalChunks, totalChunks, state.Snapshot(capacity)));
    }

    private static async ValueTask ProcessChunkAsync(RunState state, long chunk, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        if (!data.Span.ContainsAnyExcept((byte)0))
        {
            state.Index.Entries[chunk] = ChunkId.Zero;
            Interlocked.Increment(ref state.ZeroChunks);
            Interlocked.Add(ref state.ZeroBytes, data.Length);
            Interlocked.Add(ref state.ProcessingTicks, Stopwatch.GetTimestamp() - start);
            return;
        }

        var id = ChunkId.Compute(data.Span);
        state.Index.Entries[chunk] = id;
        var hashed = Stopwatch.GetTimestamp();
        Interlocked.Add(ref state.ProcessingTicks, hashed - start);
        if (state.BaseEntries is not null && state.BaseEntries[chunk] == id)
        {
            // CBT reported a change, but the content is identical (e.g. rewritten with the same data).
            Interlocked.Increment(ref state.IdenticalToBase);
            Interlocked.Increment(ref state.DedupedChunks);
            Interlocked.Add(ref state.DedupedBytes, data.Length);
            return;
        }

        var put = await state.Job.Writer.PutChunkAsync(id, data, cancellationToken).ConfigureAwait(false);
        var written = Stopwatch.GetTimestamp();
        Interlocked.Add(ref state.ProcessingTicks, put.CompressTicks);
        Interlocked.Add(ref state.TargetTicks, Math.Max(0, written - hashed - put.CompressTicks));
        if (put.Outcome == ChunkPutOutcome.Stored)
        {
            Interlocked.Increment(ref state.NewChunks);
            Interlocked.Add(ref state.NewDataBytes, data.Length);
            Interlocked.Add(ref state.StoredBytes, put.StoredBytes);
        }
        else
        {
            Interlocked.Increment(ref state.DedupedChunks);
            Interlocked.Add(ref state.DedupedBytes, data.Length);
        }
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

    private readonly record struct PendingChunk(long Chunk, byte[] Buffer, int Length);

    private sealed class RunState(DiskBackupJob job, ChunkIndex index, ChunkId[]? baseEntries, long capacity)
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public long ReadBytes;
        public long ZeroBytes;
        public long ZeroChunks;
        public long DedupedBytes;
        public long DedupedChunks;
        public long IdenticalToBase;
        public long NewChunks;
        public long NewDataBytes;
        public long StoredBytes;
        public long ProcessedWork;
        public long SourceTicks;
        public long ProcessingTicks;
        public long TargetTicks;

        public int SourceConcurrency { get; set; } = 1;

        public int WorkerConcurrency { get; set; } = 1;

        public DiskBackupJob Job { get; } = job;

        public ChunkIndex Index { get; } = index;

        public ChunkId[]? BaseEntries { get; } = baseEntries;

        public DiskBackupCounters Snapshot(long processedBytes)
        {
            var elapsedTicks = Math.Max(1, Stopwatch.GetTimestamp() - _started);
            var zero = Interlocked.Read(ref ZeroBytes);
            var deduped = Interlocked.Read(ref DedupedBytes);
            var newData = Interlocked.Read(ref NewDataBytes);
            processedBytes = Math.Max(processedBytes, zero + deduped + newData);
            return new DiskBackupCounters
            {
                CapacityBytes = capacity,
                ProcessedBytes = processedBytes,
                ReadBytes = Interlocked.Read(ref ReadBytes),
                UnchangedBytes = Math.Max(0, processedBytes - zero - deduped - newData),
                ZeroBytes = zero,
                DedupedBytes = deduped,
                NewDataBytes = newData,
                StoredBytes = Interlocked.Read(ref StoredBytes),
                ZeroChunks = Interlocked.Read(ref ZeroChunks),
                DedupedChunks = Interlocked.Read(ref DedupedChunks),
                NewChunks = Interlocked.Read(ref NewChunks),
                Elapsed = Stopwatch.GetElapsedTime(_started),
                SourceLoad = Load(Interlocked.Read(ref SourceTicks), elapsedTicks, SourceConcurrency),
                ProcessingLoad = Load(Interlocked.Read(ref ProcessingTicks), elapsedTicks, WorkerConcurrency),
                TargetLoad = Load(Interlocked.Read(ref TargetTicks), elapsedTicks, WorkerConcurrency),
            };
        }

        private static double Load(long busyTicks, long elapsedTicks, int concurrency) =>
            Math.Round(Math.Clamp(busyTicks / ((double)elapsedTicks * Math.Max(1, concurrency)), 0, 1), 3);
    }
}
