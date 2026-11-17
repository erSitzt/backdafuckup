namespace Backdafuckup.Core.Repository;

/// <summary>A file of a restore point (its manifest, an index, a blob), byte for byte.</summary>
public sealed record RepositoryFile(string Name, byte[] Content);

/// <summary>
/// Copies restore points out of a repository and into one, as files and stored chunks: for tape, which keeps them
/// byte for byte and brings them back under a new id.
/// </summary>
public interface IRepositoryTransfer
{
    /// <summary>The restore point's files: its manifest first, then its indexes and blobs.</summary>
    Task<IReadOnlyList<RepositoryFile>> ReadRestorePointFilesAsync(Guid restorePointId, CancellationToken cancellationToken);

    /// <summary>Every chunk the restore point needs (disks, memory image, file trees with their listings), each once, zero chunks left out.</summary>
    Task<IReadOnlyList<ChunkId>> ListRestorePointChunksAsync(Guid restorePointId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts bringing a restore point in under <paramref name="restorePointId"/>. <paramref name="original"/> is the
    /// manifest it was copied with; its chunk size must be this repository's.
    /// </summary>
    IRestorePointImport BeginImport(Guid restorePointId, RestorePointManifest original);

    /// <summary>A SQL Server backup object's files: its manifest first, then its file indexes.</summary>
    Task<IReadOnlyList<RepositoryFile>> ReadSqlBackupFilesAsync(Guid sqlBackupId, CancellationToken cancellationToken);

    /// <summary>Every chunk a SQL Server backup object needs, each once, zero chunks left out.</summary>
    Task<IReadOnlyList<ChunkId>> ListSqlBackupChunksAsync(Guid sqlBackupId, CancellationToken cancellationToken);

    /// <summary>Starts bringing a SQL Server backup object in under <paramref name="sqlBackupId"/>.</summary>
    ISqlBackupImport BeginSqlImport(Guid sqlBackupId, SqlBackupManifest original);

    /// <summary>Whether a committed restore point with this id is here (a copy that a move left, for example).</summary>
    bool ContainsRestorePoint(Guid restorePointId);

    /// <summary>Whether a committed SQL Server backup object with this id is here.</summary>
    bool ContainsSqlBackup(Guid sqlBackupId);

    /// <summary>
    /// Deletes a restore point unless it is pinned, or unless garbage collection or packing holds the repository for
    /// longer than <paramref name="wait"/> (then <see cref="DeleteOutcome.Busy"/>: try again later). Its chunks are left
    /// for garbage collection.
    /// </summary>
    Task<DeleteOutcome> TryDeleteRestorePointAsync(Guid restorePointId, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Deletes a SQL Server backup object, like <see cref="TryDeleteRestorePointAsync"/>.</summary>
    Task<DeleteOutcome> TryDeleteSqlBackupAsync(Guid sqlBackupId, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>What is going on in the repository now: before it is closed for maintenance.</summary>
    RepositoryActivity Activity { get; }
}

/// <summary>The result of <see cref="IRepositoryTransfer.TryDeleteRestorePointAsync"/>.</summary>
public enum DeleteOutcome
{
    Deleted,
    Pinned,
    Busy,
    NotFound,
}

/// <summary>Readers holding pins, writers not yet committed or discarded, and whether garbage collection or packing runs.</summary>
public readonly record struct RepositoryActivity(int Pins, int Writers, bool Collecting)
{
    public bool Idle => Pins == 0 && Writers == 0 && !Collecting;

    public override string ToString() =>
        Idle ? "idle" : string.Join(", ", new[]
        {
            Pins > 0 ? $"{Pins} restore point(s) or SQL Server backup(s) in use" : null,
            Writers > 0 ? $"{Writers} backup(s) writing" : null,
            Collecting ? "garbage collection or packing" : null,
        }.OfType<string>());
}

/// <summary>One SQL Server backup object coming in, like <see cref="IRestorePointImport"/>.</summary>
public interface ISqlBackupImport : IAsyncDisposable
{
    Guid SqlBackupId { get; }

    /// <summary>Like <see cref="IRestorePointImport.TryReuseChunk"/>.</summary>
    bool TryReuseChunk(ChunkId id);

    ValueTask<ChunkPutResult> PutEncodedChunkAsync(ChunkId id, ReadOnlyMemory<byte> stored, CancellationToken cancellationToken);

    Task WriteFileAsync(string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    Task<SqlBackupInfo> CommitAsync(Func<SqlBackupManifest, SqlBackupManifest>? adjust, CancellationToken cancellationToken);
}

/// <summary>
/// One restore point coming in: stored chunks, then its files, then <see cref="CommitAsync"/> writes the manifest (the
/// commit point). Disposing without commit discards it.
/// </summary>
public interface IRestorePointImport : IAsyncDisposable
{
    Guid RestorePointId { get; }

    /// <summary>
    /// Whether this repository already holds the chunk. If so, it is kept for this import (garbage collection running at
    /// the same time does not remove it) and need not be read from the source and put.
    /// </summary>
    bool TryReuseChunk(ChunkId id);

    /// <summary>Stores a chunk's record unchanged after decoding it and checking its hash (<see cref="ChunkCorruptException"/> if damaged).</summary>
    ValueTask<ChunkPutResult> PutEncodedChunkAsync(ChunkId id, ReadOnlyMemory<byte> stored, CancellationToken cancellationToken);

    /// <summary>Writes one of the restore point's files (an index or a blob, not the manifest) as it was.</summary>
    Task WriteFileAsync(string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the original manifest under the new id and this repository's id, after <paramref name="adjust"/> (e.g. the
    /// job it belongs to now). Every file the manifest refers to must have been written.
    /// </summary>
    Task<ManifestInfo> CommitAsync(Func<RestorePointManifest, RestorePointManifest>? adjust, CancellationToken cancellationToken);
}
