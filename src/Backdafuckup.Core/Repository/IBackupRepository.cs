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

/// <param name="Outcome">Whether the chunk was new or already present.</param>
/// <param name="StoredBytes">Bytes written to the repository (after compression).</param>
/// <param name="CompressTicks">Time spent compressing, in <see cref="System.Diagnostics.Stopwatch"/> ticks (for load statistics).</param>
public readonly record struct ChunkPutResult(ChunkPutOutcome Outcome, long StoredBytes, long CompressTicks = 0);

public sealed record ManifestInfo(Guid RestorePointId, string Path, RestorePointManifest Manifest);

public enum VerifyDepth
{
    /// <summary>Index hashes and existence/header of every referenced chunk.</summary>
    Quick,

    /// <summary>Decompress and hash every referenced chunk.</summary>
    Full,
}

public sealed record VerifyResult(bool Ok, long CheckedChunks, IReadOnlyList<string> Problems);

/// <param name="ScannedChunks">Chunks looked at (loose files and pack records).</param>
/// <param name="DeletedChunks">Chunks no longer referenced that were removed (loose files deleted, records dropped from packs).</param>
/// <param name="FreedBytes">Space given back to the volume.</param>
/// <param name="MarkedChunks">Chunks referenced by restore points and SQL backups.</param>
/// <param name="PacksDeleted">Packs deleted (unreferenced or repacked).</param>
/// <param name="BytesRepacked">Bytes of still referenced chunks copied out of repacked packs.</param>
/// <param name="UnusedBytes">Bytes of unreferenced chunks left in packs that were not worth repacking yet.</param>
public sealed record GcResult(long ScannedChunks, long DeletedChunks, long FreedBytes, long MarkedChunks, long PacksDeleted = 0, long BytesRepacked = 0, long UnusedBytes = 0);

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

    /// <summary>
    /// Synchronous variant for synchronous consumers (file system parsers for file-level restore). It blocks only on
    /// its own file read, never on work queued to the thread pool, so it is safe on dedicated worker threads.
    /// </summary>
    int Read(ChunkId id, Span<byte> destination);
}

public interface IRestorePointReader : IAsyncDisposable
{
    RestorePointManifest Manifest { get; }

    IChunkReader Chunks { get; }

    Task<ChunkIndex> ReadDiskIndexAsync(string diskKey, CancellationToken cancellationToken);

    /// <summary>The root listing's chunks of file tree <paramref name="key"/> (see <see cref="RestorePointManifest.FileTrees"/>).</summary>
    Task<ChunkIndex> ReadFileTreeIndexAsync(string key, CancellationToken cancellationToken);

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

    /// <summary>Writes the root listing's chunks of a file tree (its chunks are put before).</summary>
    Task<ManifestIndexRef> WriteFileTreeIndexAsync(string key, ChunkIndex root, CancellationToken cancellationToken);

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

    /// <summary>Starts a new SQL backup object (log, full or system backups of one instance) for <paramref name="vmId"/>.</summary>
    ISqlBackupWriter BeginSqlBackup(Guid sqlBackupId, string vmId);

    IAsyncEnumerable<SqlBackupInfo> ListSqlBackupsAsync(CancellationToken cancellationToken);

    Task<ISqlBackupReader> OpenSqlBackupAsync(Guid sqlBackupId, CancellationToken cancellationToken);

    /// <summary>Prevents deletion of a SQL backup object while held (source of a restore).</summary>
    IRestorePointPin PinSqlBackup(Guid sqlBackupId);

    /// <summary>Deletes a SQL backup object (manifest, indexes). Chunks are reclaimed by GC. Fails while pinned.</summary>
    Task DeleteSqlBackupAsync(Guid sqlBackupId, CancellationToken cancellationToken);

    Task<VerifyResult> VerifySqlBackupAsync(Guid sqlBackupId, VerifyDepth depth, IProgress<string>? progress, CancellationToken cancellationToken);

    StorageStats GetStorageStats();
}

public sealed class RestorePointPinnedException(Guid restorePointId)
    : InvalidOperationException($"Restore point {restorePointId} is in use and cannot be deleted.");

public sealed class SqlBackupPinnedException(Guid sqlBackupId)
    : InvalidOperationException($"SQL backup {sqlBackupId} is in use by a restore and cannot be deleted.")
{
    public Guid SqlBackupId { get; } = sqlBackupId;
}

public sealed class ChunkCorruptException(ChunkId id, string reason)
    : IOException($"Chunk {id} is corrupt: {reason}")
{
    public ChunkId ChunkId { get; } = id;

    public string Reason { get; } = reason;
}

/// <param name="repository">The repository's location, named in the message when known.</param>
/// <param name="detail">Where it was expected, e.g. a pack that is gone.</param>
public sealed class ChunkMissingException(ChunkId id, string? repository = null, string? detail = null)
    : IOException((repository is null ? $"Chunk {id} is missing from the repository" : $"Chunk {id} is missing from the repository '{repository}'") + (detail is null ? "." : $" ({detail})."))
{
    public ChunkId ChunkId { get; } = id;
}

/// <summary>
/// Reads many chunks in the order they are stored (pack by pack), as stored records without decompressing them: for
/// copies (tape), verify and packing. Zero ids are never part of a plan.
/// </summary>
public interface IChunkLocator
{
    /// <summary>Plans reading <paramref name="ids"/> (duplicates and zero ids are dropped); missing chunks are listed.</summary>
    ChunkReadPlan PlanRead(IEnumerable<ChunkId> ids);

    /// <summary>
    /// Reads the planned chunks in plan order. Each <see cref="StoredChunk.Record"/> is only valid until the next item
    /// is requested.
    /// </summary>
    IAsyncEnumerable<StoredChunk> ReadStoredAsync(ChunkReadPlan plan, CancellationToken cancellationToken);
}

/// <param name="Id">The chunk.</param>
/// <param name="Source">Index into <see cref="ChunkReadPlan.Sources"/>.</param>
/// <param name="Offset">Offset of the chunk's stored record in its source (0 for a loose chunk file).</param>
/// <param name="StoredLength">Length of the stored record (header and compressed payload).</param>
/// <param name="RawLength">Length of the chunk's content.</param>
public readonly record struct PlannedChunk(ChunkId Id, int Source, long Offset, int StoredLength, int RawLength);

/// <param name="Chunks">Chunks in storage order: grouped by source, by offset within a source.</param>
/// <param name="Sources">Where the chunks lie (pack paths; loose chunks have a source of their own each).</param>
/// <param name="Missing">Requested chunks the repository does not have.</param>
public sealed record ChunkReadPlan(IReadOnlyList<PlannedChunk> Chunks, IReadOnlyList<string> Sources, IReadOnlyList<ChunkId> Missing)
{
    public long StoredBytes { get; } = Chunks.Sum(c => (long)c.StoredLength);

    public long RawBytes { get; } = Chunks.Sum(c => (long)c.RawLength);
}

/// <param name="Id">The chunk.</param>
/// <param name="Record">Its stored record (16-byte header and payload, see the repository's chunk codec).</param>
/// <param name="RawLength">Length of the chunk's content.</param>
public readonly record struct StoredChunk(ChunkId Id, ReadOnlyMemory<byte> Record, int RawLength);
