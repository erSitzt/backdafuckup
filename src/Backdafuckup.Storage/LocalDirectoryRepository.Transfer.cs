using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>Restore points out of the repository and into it as files and stored chunks (tape copies).</summary>
public sealed partial class LocalDirectoryRepository : IRepositoryTransfer
{
    public async Task<IReadOnlyList<RepositoryFile>> ReadRestorePointFilesAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var directory = GetRestorePointDirectory(restorePointId);
        var result = new List<RepositoryFile> { new(ManifestFileName, await File.ReadAllBytesAsync(Path.Combine(directory, ManifestFileName), cancellationToken).ConfigureAwait(false)) };
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name is ManifestFileName or DeletedManifestFileName || name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new RepositoryFile(name, await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<IReadOnlyList<ChunkId>> ListRestorePointChunksAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        await using var reader = await OpenRestorePointAsync(restorePointId, cancellationToken).ConfigureAwait(false);
        var ids = new List<ChunkId>();
        var seen = new HashSet<ChunkId>();
        await VisitChunksAsync(reader, id =>
        {
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }, new HashSet<ChunkId>(), cancellationToken).ConfigureAwait(false);
        return ids;
    }

    public IRestorePointImport BeginImport(Guid restorePointId, RestorePointManifest original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Chunking.ChunkSize != _descriptor.ChunkSize)
        {
            throw new InvalidOperationException(
                $"The restore point was written with chunks of {original.Chunking.ChunkSize / 1024} KiB; the repository '{Root}' uses {_descriptor.ChunkSize / 1024} KiB. " +
                "Bring it into a repository with the same chunk size.");
        }

        return new Import(this, (Writer)BeginWrite(restorePointId, original.Vm.VmId, null), original);
    }

    /// <summary>
    /// Every non-zero chunk a restore point needs: its disks and memory image, and its file trees with their listings.
    /// Listings in <paramref name="walkedListings"/> are not walked again (GC shares the set across restore points).
    /// </summary>
    internal async Task VisitChunksAsync(IRestorePointReader reader, Action<ChunkId> visit, HashSet<ChunkId> walkedListings, CancellationToken cancellationToken)
    {
        foreach (var disk in reader.Manifest.AllImages)
        {
            var index = await reader.ReadDiskIndexAsync(disk.Key, cancellationToken).ConfigureAwait(false);
            foreach (var entry in index.Entries)
            {
                if (!entry.IsZero)
                {
                    visit(entry);
                }
            }
        }

        foreach (var tree in reader.Manifest.FileTrees)
        {
            var root = await reader.ReadFileTreeIndexAsync(tree.Key, cancellationToken).ConfigureAwait(false);
            FileTreeWalker.Visit(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), Chunks, _descriptor.ChunkSize, visit, walkedListings);
        }
    }

    /// <summary>The files a manifest refers to: indexes of disks, memory image and file trees, and its blobs.</summary>
    private static IEnumerable<string> ReferencedFiles(RestorePointManifest manifest) =>
        manifest.AllImages.Select(d => d.Index.File)
            .Concat(manifest.FileTrees.Select(t => t.Index.File))
            .Concat(new[] { manifest.Guest, manifest.VssDocuments, manifest.SqlSnapshot, manifest.Metadata, manifest.MemoryMetadata }.OfType<ManifestBlob>().Select(b => b.File));

    private sealed class Import(LocalDirectoryRepository repository, Writer writer, RestorePointManifest original) : IRestorePointImport
    {
        public Guid RestorePointId => writer.RestorePointId;

        public ValueTask<ChunkPutResult> PutEncodedChunkAsync(ChunkId id, ReadOnlyMemory<byte> stored, CancellationToken cancellationToken) =>
            writer.PutEncodedChunkAsync(id, stored, cancellationToken);

        public Task WriteFileAsync(string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            writer.WriteRawFileAsync(name, content, cancellationToken);

        public Task<ManifestInfo> CommitAsync(Func<RestorePointManifest, RestorePointManifest>? adjust, CancellationToken cancellationToken)
        {
            var missing = ReferencedFiles(original).Where(f => !writer.HasFile(f)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException($"The restore point being brought into '{repository.Root}' lacks {string.Join(", ", missing)}.");
            }

            var manifest = original with { RestorePointId = writer.RestorePointId, RepositoryId = repository.Info.RepositoryId };
            manifest = (adjust?.Invoke(manifest) ?? manifest) with { RestorePointId = writer.RestorePointId, RepositoryId = repository.Info.RepositoryId };
            return writer.CommitAsync(manifest, cancellationToken);
        }

        public ValueTask DisposeAsync() => writer.DisposeAsync();
    }
}
