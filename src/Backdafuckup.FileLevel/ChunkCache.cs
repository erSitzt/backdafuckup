using Backdafuckup.Core.Repository;

namespace Backdafuckup.FileLevel;

/// <summary>
/// Decompressed chunks shared by all file-level restore readers. Chunks are content-addressed, so one entry serves
/// every restore point and repository that contains the same data. Limited in bytes (the chunk size differs per
/// repository); a chunk is loaded only once even when several readers ask for it at the same time.
/// </summary>
/// <remarks>Synchronous by design: file system parsers read synchronously on dedicated worker threads.</remarks>
public sealed class ChunkCache : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ChunkId, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<ChunkId, Lazy<CachedChunk>> _loading = [];
    private readonly SemaphoreSlim _decoders;
    private long _bytes;
    private long _hits;
    private long _misses;

    public ChunkCache(long maxBytes, int maxConcurrentDecodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentDecodes);
        MaxBytes = maxBytes;
        _decoders = new SemaphoreSlim(maxConcurrentDecodes);
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

    /// <summary>The decompressed and verified content of <paramref name="id"/> (never the zero chunk).</summary>
    public CachedChunk Get(ChunkId id, IChunkReader reader, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (id.IsZero)
        {
            throw new ArgumentException("The zero chunk is not stored; callers fill zeros.", nameof(id));
        }

        Lazy<CachedChunk> loader;
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Chunk;
            }

            if (!_loading.TryGetValue(id, out loader!))
            {
                loader = new Lazy<CachedChunk>(() => Load(id, reader, chunkSize), LazyThreadSafetyMode.ExecutionAndPublication);
                _loading[id] = loader;
            }
        }

        CachedChunk chunk;
        try
        {
            chunk = loader.Value;
        }
        finally
        {
            lock (_lock)
            {
                if (_loading.TryGetValue(id, out var current) && ReferenceEquals(current, loader))
                {
                    _loading.Remove(id);
                }
            }
        }

        lock (_lock)
        {
            if (!_entries.ContainsKey(id))
            {
                _entries[id] = _lru.AddFirst(new Entry(id, chunk));
                _bytes += chunk.Data.Length;
                while (_bytes > MaxBytes && _lru.Last is { } last && !ReferenceEquals(last.Value.Chunk, chunk))
                {
                    _lru.RemoveLast();
                    _entries.Remove(last.Value.Id);
                    _bytes -= last.Value.Chunk.Data.Length;
                }
            }
        }

        return chunk;
    }

    public void Dispose() => _decoders.Dispose();

    private CachedChunk Load(ChunkId id, IChunkReader reader, int chunkSize)
    {
        Interlocked.Increment(ref _misses);
        var data = new byte[chunkSize];
        _decoders.Wait();
        try
        {
            var length = reader.Read(id, data);
            return new CachedChunk(data, length);
        }
        finally
        {
            _decoders.Release();
        }
    }

    private sealed record Entry(ChunkId Id, CachedChunk Chunk);
}

/// <summary>A decompressed chunk; <see cref="Length"/> bytes of <see cref="Data"/> are valid. Never modified.</summary>
public sealed record CachedChunk(byte[] Data, int Length);
