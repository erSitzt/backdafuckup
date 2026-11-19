using System.Collections.Concurrent;
using System.Security.Cryptography;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.InstantRecovery.Tests;

/// <summary>A restore point disk as the base of an instant recovery disk: data, zeros, the shared cache and read-ahead.</summary>
public sealed class BaseImageTests
{
    private const int ChunkSize = 64 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_across_chunks_with_zero_chunks_and_a_short_last_chunk()
    {
        var (disk, index, chunks) = Build(chunkCount: 6, lastLength: 1000, zero: [1, 4]);
        using var cache = new AsyncChunkCache(16 * ChunkSize, 4);
        var image = new RestorePointBaseImage(index, chunks, cache, readAheadChunks: 0);

        Assert.Equal(disk.Length, image.Size);
        Assert.Equal((3L * ChunkSize) + 1000, image.AllocatedBytes);
        foreach (var (offset, length) in new[] { (0L, disk.Length), (ChunkSize - 10L, 20), (ChunkSize * 3L + 5, ChunkSize * 2), (disk.Length - 1000, 1000), (0L, 0) })
        {
            var buffer = new byte[length];
            await image.ReadAsync(offset, buffer, Ct);
            Assert.True(disk.AsSpan((int)offset, (int)length).SequenceEqual(buffer), $"{length} bytes at {offset}");
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await image.ReadAsync(disk.Length - 10, new byte[11], Ct));
    }

    [Fact]
    public async Task Concurrent_reads_of_one_chunk_load_it_once_and_a_cancelled_reader_does_not_cancel_the_load()
    {
        var (disk, index, chunks) = Build(chunkCount: 2, lastLength: ChunkSize, zero: []);
        chunks.Gate = new TaskCompletionSource();
        using var cache = new AsyncChunkCache(16 * ChunkSize, 4);
        var image = new RestorePointBaseImage(index, chunks, cache, readAheadChunks: 0);

        using var cancel = new CancellationTokenSource();
        var cancelled = image.ReadAsync(0, new byte[100], cancel.Token).AsTask();
        var readers = Enumerable.Range(0, 8).Select(i => ReadAsync(image, i * 1000, 500)).ToList();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        chunks.Gate.SetResult();

        foreach (var (reader, i) in readers.Select((r, i) => (r, i)))
        {
            var data = await reader;
            Assert.True(disk.AsSpan(i * 1000, 500).SequenceEqual(data));
        }

        Assert.Equal(1, chunks.Reads(index.Entries[0]));
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public async Task The_cache_keeps_its_byte_limit_and_a_failed_load_is_tried_again()
    {
        var (_, index, chunks) = Build(chunkCount: 8, lastLength: ChunkSize, zero: []);
        using var cache = new AsyncChunkCache(3 * ChunkSize, 2);
        var image = new RestorePointBaseImage(index, chunks, cache, readAheadChunks: 0);
        for (var chunk = 0; chunk < 8; chunk++)
        {
            await ReadAsync(image, chunk * (long)ChunkSize, 10);
        }

        Assert.True(cache.Bytes <= 3 * ChunkSize, $"{cache.Bytes} bytes cached");
        await ReadAsync(image, 7L * ChunkSize, 10);
        Assert.Equal(1, chunks.Reads(index.Entries[7]));
        await ReadAsync(image, 0, 10);
        Assert.Equal(2, chunks.Reads(index.Entries[0]));

        chunks.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => ReadAsync(image, 2L * ChunkSize, 10));
        await ReadAsync(image, 2L * ChunkSize, 10);
    }

    [Fact]
    public async Task Sequential_reads_load_the_next_chunks_ahead()
    {
        var (_, index, chunks) = Build(chunkCount: 20, lastLength: ChunkSize, zero: [5]);
        using var cache = new AsyncChunkCache(64 * ChunkSize, 4);
        var image = new RestorePointBaseImage(index, chunks, cache, readAheadChunks: 4);

        await ReadAsync(image, 0, ChunkSize);
        await ReadAsync(image, ChunkSize, ChunkSize);
        for (var i = 0; i < 100 && chunks.Reads(index.Entries[4]) == 0; i++)
        {
            await Task.Delay(20, Ct);
        }

        // After chunk 1, chunks 2 to 5 are loaded ahead (5 is zero and skipped), then 6 by the next sequential read.
        Assert.All(new[] { 2, 3, 4 }, c => Assert.Equal(1, chunks.Reads(index.Entries[c])));
        await ReadAsync(image, 2L * ChunkSize, ChunkSize);
        for (var i = 0; i < 100 && chunks.Reads(index.Entries[6]) == 0; i++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(1, chunks.Reads(index.Entries[6]));
        Assert.Equal(0, chunks.Reads(index.Entries[19]));

        // A random read does not trigger the read-ahead.
        await ReadAsync(image, 15L * ChunkSize, 10);
        await Task.Delay(100, Ct);
        Assert.Equal(0, chunks.Reads(index.Entries[16]));
    }

    private static async Task<byte[]> ReadAsync(IBaseImage image, long offset, int length)
    {
        var buffer = new byte[length];
        await image.ReadAsync(offset, buffer, Ct);
        return buffer;
    }

    private static (byte[] Disk, ChunkIndex Index, MemoryChunks Chunks) Build(int chunkCount, int lastLength, int[] zero)
    {
        var size = ((chunkCount - 1) * (long)ChunkSize) + lastLength;
        var disk = new byte[size];
        var entries = new ChunkId[chunkCount];
        var chunks = new MemoryChunks();
        for (var i = 0; i < chunkCount; i++)
        {
            var data = disk.AsSpan(i * ChunkSize, i == chunkCount - 1 ? lastLength : ChunkSize);
            if (zero.Contains(i))
            {
                entries[i] = ChunkId.Zero;
                continue;
            }

            RandomNumberGenerator.Fill(data);
            entries[i] = ChunkId.Compute(data);
            chunks.Add(entries[i], data.ToArray());
        }

        return (disk, new ChunkIndex(ChunkSize, size, entries), chunks);
    }

    private sealed class MemoryChunks : IChunkReader
    {
        private readonly Dictionary<ChunkId, byte[]> _data = [];
        private readonly ConcurrentDictionary<ChunkId, int> _reads = new();

        public TaskCompletionSource? Gate { get; set; }

        public bool FailNext { get; set; }

        public void Add(ChunkId id, byte[] data) => _data[id] = data;

        public int Reads(ChunkId id) => _reads.GetValueOrDefault(id);

        public async ValueTask<int> ReadAsync(ChunkId id, Memory<byte> destination, CancellationToken cancellationToken)
        {
            _reads.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (FailNext)
            {
                FailNext = false;
                throw new IOException($"Chunk {id} cannot be read (test).");
            }

            _data[id].CopyTo(destination);
            return _data[id].Length;
        }

        public int Read(ChunkId id, Span<byte> destination) => throw new NotSupportedException();
    }
}
