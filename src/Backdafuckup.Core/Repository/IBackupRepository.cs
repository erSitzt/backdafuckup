namespace Backdafuckup.Core.Repository;

/// <summary>A complete chunk index of one disk in one restore point.</summary>
public sealed class ChunkIndex
{
    public ChunkIndex(int chunkSize, long diskSize, ChunkId[] entries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfNegative(diskSize);
        ArgumentNullException.ThrowIfNull(entries);
        var expected = Extents.ExtentMath.ChunkCount(diskSize, chunkSize);
        if (entries.LongLength != expected)
        {
            throw new ArgumentException($"Index has {entries.LongLength} entries, expected {expected}.", nameof(entries));
        }

        ChunkSize = chunkSize;
        DiskSize = diskSize;
        Entries = entries;
    }

    public int ChunkSize { get; }

    public long DiskSize { get; }

    public ChunkId[] Entries { get; }

    public static ChunkIndex CreateEmpty(int chunkSize, long diskSize) =>
        new(chunkSize, diskSize, new ChunkId[Extents.ExtentMath.ChunkCount(diskSize, chunkSize)]);
}

public sealed record RepositoryInfo(Guid RepositoryId, string Location, int ChunkSize, string Hash, string Compression, int CompressionLevel);

public enum ChunkPutOutcome
{
    Stored,
    Deduplicated,
}

public readonly record struct ChunkPutResult(ChunkPutOutcome Outcome, long StoredBytes);

public sealed record ManifestInfo(Guid RestorePointId, string Path, RestorePointManifest Manifest);

public enum VerifyDepth
{
    /// <summary>Index hashes and existence/header of every referenced chunk.</summary>
    Quick,

    /// <summary>Decompress and hash every referenced chunk.</summary>
    Full,
}

public sealed record VerifyResult(bool Ok, long CheckedChunks, IReadOnlyList<string> Problems);

public sealed record GcResult(long ScannedChunks, long DeletedChunks, long FreedBytes, long MarkedChunks);

public sealed record StorageStats(long TotalBytes, long FreeBytes);

/// <summary>Prevents deletion of a restore point while held (base of a running backup or source of a restore).</summary>
public interface IRestorePointPin : IDisposable
{
    Guid RestorePointId { get; }
}

/// <summary>Reads chunk content. Every read decompresses and verifies the SHA-256 of the content.</summary>
public interface IChunkReader
{
    /// <summary>Reads chunk <paramref name="id"/> into <paramref name="destination"/> and returns its length.</summary>
    ValueTask<int> ReadAsync(ChunkId id, Memory<byte> destination, CancellationToken cancellationToken);
}

public interface IRestorePointReader : IAsyncDisposable
{
    RestorePointManifest Manifest { get; }

    IChunkReader Chunks { get; }

    Task<ChunkIndex> ReadDiskIndexAsync(string diskKey, CancellationToken cancellationToken);

    Task<Stream> OpenBlobAsync(ManifestBlob blob, CancellationToken cancellationToken);
}

/// <summary>
/// Writes one restore point. Chunks first, then indexes and blobs, then <see cref="CommitAsync"/> writes the
/// manifest (the commit point). Disposing without commit discards the partial restore point.
/// </summary>
public interface IRepositoryWriter : IAsyncDisposable
{
    Guid RestorePointId { get; }

    ValueTask<ChunkPutResult> PutChunkAsync(ChunkId id, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken);

    Task<ManifestIndexRef> WriteDiskIndexAsync(string diskKey, ChunkIndex index, CancellationToken cancellationToken);

    Task<ManifestBlob> WriteBlobAsync(string name, string format, Stream content, CancellationToken cancellationToken);

    Task<ManifestInfo> CommitAsync(RestorePointManifest manifest, CancellationToken cancellationToken);
}

public interface IBackupRepository : IAsyncDisposable
{
    RepositoryInfo Info { get; }

    IChunkReader Chunks { get; }

    /// <summary>Starts a new restore point for <paramref name="vmId"/>. The base pin (if any) stays held by the caller.</summary>
    IRepositoryWriter BeginWrite(Guid restorePointId, string vmId, IRestorePointPin? basePin);

    IAsyncEnumerable<ManifestInfo> ListRestorePointsAsync(CancellationToken cancellationToken);

    Task<IRestorePointReader> OpenRestorePointAsync(Guid restorePointId, CancellationToken cancellationToken);

    IRestorePointPin Pin(Guid restorePointId);

    /// <summary>Deletes a restore point (manifest, indexes, blobs). Chunks are reclaimed by GC. Fails while pinned.</summary>
    Task DeleteRestorePointAsync(Guid restorePointId, CancellationToken cancellationToken);

    Task<GcResult> CollectGarbageAsync(IProgress<string>? progress, CancellationToken cancellationToken);

    Task<VerifyResult> VerifyAsync(Guid restorePointId, VerifyDepth depth, IProgress<string>? progress, CancellationToken cancellationToken);

    StorageStats GetStorageStats();
}

public sealed class RestorePointPinnedException(Guid restorePointId)
    : InvalidOperationException($"Restore point {restorePointId} is in use and cannot be deleted.");

public sealed class ChunkCorruptException(ChunkId id, string reason)
    : IOException($"Chunk {id} is corrupt: {reason}")
{
    public ChunkId ChunkId { get; } = id;
}

public sealed class ChunkMissingException(ChunkId id)
    : IOException($"Chunk {id} is missing from the repository.")
{
    public ChunkId ChunkId { get; } = id;
}
