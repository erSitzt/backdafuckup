using Backdafuckup.Core.Repository;

namespace Backdafuckup.InstantRecovery;

/// <summary>A decompressed, verified chunk: <see cref="Length"/> bytes of <see cref="Data"/> are valid.</summary>
public sealed record CachedBlock(byte[] Data, int Length);

/// <summary>
/// Decompressed chunks for the disks of running instant recoveries. Asynchronous, unlike the file-level cache: NFS reads
/// arrive on the thread pool and must not block it. Limited in bytes, least recently used out first; a chunk is loaded
/// once even when several reads ask for it at the same time, and a load runs to its end even when the read that started
/// it is cancelled (another read, or the read-ahead, usually wants it too).
/// </summary>
public sealed class AsyncChunkCache : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ChunkId, LinkedListNode<(ChunkId Id, CachedBlock Block)>> _entries = [];
    private readonly LinkedList<(ChunkId Id, CachedBlock Block)> _lru = new();
    private readonly Dictionary<ChunkId, Task<CachedBlock>> _loading = [];
    private readonly SemaphoreSlim _loads;
    private readonly SemaphoreSlim _prefetches;
    private readonly CancellationTokenSource _disposed = new();
    private long _bytes;
    private long _hits;
    private long _misses;

    public AsyncChunkCache(long maxBytes, int maxConcurrentLoads, int maxPrefetches = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentLoads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPrefetches);
        MaxBytes = maxBytes;
        _loads = new SemaphoreSlim(maxConcurrentLoads);
        _prefetches = new SemaphoreSlim(maxPrefetches);
    }

    public long MaxBytes { get; }

    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>The content of <paramref name="id"/> (never the zero chunk: callers fill zeros).</summary>
    public async ValueTask<CachedBlock> GetAsync(ChunkId id, IChunkReader reader, int chunkSize, CancellationToken cancellationToken)
    {
        if (id.IsZero)
        {
            throw new ArgumentException("The zero chunk is not stored; callers fill zeros.", nameof(id));
        }

        Task<CachedBlock> load;
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Block;
            }

            load = StartLoad(id, reader, chunkSize);
        }

        return await load.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads <paramref name="id"/> in the background unless it is cached or loading; dropped when too many are under way.</summary>
    public void Prefetch(ChunkId id, IChunkReader reader, int chunkSize)
    {
        if (id.IsZero || _disposed.IsCancellationRequested)
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.ContainsKey(id) || _loading.ContainsKey(id) || !_prefetches.Wait(0))
            {
                return;
            }

            var load = StartLoad(id, reader, chunkSize);
            _ = load.ContinueWith(static (_, state) => ((SemaphoreSlim)state!).Release(), _prefetches, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Under <see cref="_lock"/>: the running load of <paramref name="id"/>, or a new one.</summary>
    private Task<CachedBlock> StartLoad(ChunkId id, IChunkReader reader, int chunkSize)
    {
        if (_loading.TryGetValue(id, out var running))
        {
            return running;
        }

        var load = Task.Run(() => LoadAsync(id, reader, chunkSize));
        _loading[id] = load;
        return load;
    }

    private async Task<CachedBlock> LoadAsync(ChunkId id, IChunkReader reader, int chunkSize)
    {
        try
        {
            Interlocked.Increment(ref _misses);
            await _loads.WaitAsync(_disposed.Token).ConfigureAwait(false);
            CachedBlock block;
            try
            {
                var data = new byte[chunkSize];
                var length = await reader.ReadAsync(id, data, _disposed.Token).ConfigureAwait(false);
                block = new CachedBlock(data, length);
            }
            finally
            {
                _loads.Release();
            }

            lock (_lock)
            {
                if (!_entries.ContainsKey(id))
                {
                    _entries[id] = _lru.AddFirst((id, block));
                    _bytes += block.Data.Length;
                    while (_bytes > MaxBytes && _lru.Last is { } last && last.Value.Id != id)
                    {
                        _lru.RemoveLast();
                        _entries.Remove(last.Value.Id);
                        _bytes -= last.Value.Block.Data.Length;
                    }
                }
            }

            return block;
        }
        finally
        {
            lock (_lock)
            {
                _loading.Remove(id);
            }
        }
    }

    /// <summary>Stops loads that wait for a slot; the semaphores stay usable for loads still finishing.</summary>
    public void Dispose() => _disposed.Cancel();
}
