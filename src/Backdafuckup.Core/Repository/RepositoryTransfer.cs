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
}

/// <summary>
/// One restore point coming in: stored chunks, then its files, then <see cref="CommitAsync"/> writes the manifest (the
/// commit point). Disposing without commit discards it.
/// </summary>
public interface IRestorePointImport : IAsyncDisposable
{
    Guid RestorePointId { get; }

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
