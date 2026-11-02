using Backdafuckup.Core.Repository;
using Backdafuckup.FileLevel;

namespace Backdafuckup.FileLevel.Tests;

[Trait("Category", "Unit")]
public sealed class ChunkCacheTests
{
    [Fact]
    public async Task Concurrent_requests_for_one_chunk_load_it_once()
    {
        var reader = new CountingReader(TimeSpan.FromMilliseconds(200));
        using var cache = new ChunkCache(1 << 20, 4);
        var id = ChunkId.Compute([1, 2, 3]);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => cache.Get(id, reader, 1024), TestContext.Current.CancellationToken)));
        Assert.Equal(1, reader.Reads);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public void Least_recently_used_chunks_are_evicted_by_bytes()
    {
        var reader = new CountingReader(TimeSpan.Zero);
        using var cache = new ChunkCache(3 * 1024, 1);
        var ids = Enumerable.Range(0, 5).Select(i => ChunkId.Compute([(byte)i])).ToArray();
        foreach (var id in ids)
        {
            cache.Get(id, reader, 1024);
        }

        Assert.True(cache.Bytes <= 3 * 1024);
        cache.Get(ids[4], reader, 1024);
        Assert.Equal(5, reader.Reads);
        cache.Get(ids[0], reader, 1024);
        Assert.Equal(6, reader.Reads);
    }

    [Fact]
    public void A_failed_load_is_not_cached()
    {
        var reader = new CountingReader(TimeSpan.Zero) { FailNext = true };
        using var cache = new ChunkCache(1 << 20, 1);
        var id = ChunkId.Compute([9]);
        Assert.Throws<IOException>(() => cache.Get(id, reader, 16));
        Assert.Equal(16, cache.Get(id, reader, 16).Length);
    }

    private sealed class CountingReader(TimeSpan delay) : IChunkReader
    {
        private int _reads;

        public int Reads => _reads;

        public bool FailNext { get; set; }

        public ValueTask<int> ReadAsync(ChunkId id, Memory<byte> destination, CancellationToken cancellationToken) => ValueTask.FromResult(Read(id, destination.Span));

        public int Read(ChunkId id, Span<byte> destination)
        {
            Interlocked.Increment(ref _reads);
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("disk error");
            }

            Thread.Sleep(delay);
            destination.Fill(7);
            return destination.Length;
        }
    }
}
