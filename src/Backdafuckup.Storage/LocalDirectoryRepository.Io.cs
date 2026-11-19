using System.Buffers;
using System.Text.Json;
using Backdafuckup.Core.Repository;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Storage;

public sealed partial class LocalDirectoryRepository
{
    private sealed class ChunkReader(LocalDirectoryRepository repository) : IChunkReader
    {
        public async ValueTask<int> ReadAsync(ChunkId id, Memory<byte> destination, CancellationToken cancellationToken)
        {
            ThrowIfZero(id);
            if (repository._packs is { } packs)
            {
                for (var attempt = 0; attempt < 2 && packs.TryLocate(id, out var location); attempt++)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)location.StoredLength);
                    try
                    {
                        try
                        {
                            await packs.ReadStoredAsync(location, buffer, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            if (attempt == 0)
                            {
                                continue; // Repacked meanwhile: look it up again.
                            }

                            throw new ChunkMissingException(id, repository.Root, $"pack '{location.Pack.Path}' is gone");
                        }

                        return DecodePacked(id, location, buffer.AsSpan(0, (int)location.StoredLength), destination.Span);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ChunkCorruptException(id, $"{ex.Message} (pack '{location.Pack.Path}', offset {location.Offset})");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                if (!repository._looseMayExist)
                {
                    throw new ChunkMissingException(id, repository.Root);
                }
            }

            using var handle = OpenLoose(id, FileOptions.Asynchronous);
            var length = CheckedLength(id, handle);
            var loose = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = 0;
                while (read < length)
                {
                    var n = await RandomAccess.ReadAsync(handle, loose.AsMemory(read, length - read), read, cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new ChunkCorruptException(id, $"truncated (file '{repository.ChunkPath(id.ToHex())}')");
                    }

                    read += n;
                }

                return ChunkCodec.Decode(id, loose.AsSpan(0, length), destination.Span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(loose);
            }
        }

        public int Read(ChunkId id, Span<byte> destination)
        {
            ThrowIfZero(id);
            if (repository._packs is { } packs)
            {
                for (var attempt = 0; attempt < 2 && packs.TryLocate(id, out var location); attempt++)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent((int)location.StoredLength);
                    try
                    {
                        try
                        {
                            packs.ReadStored(location, buffer);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            if (attempt == 0)
                            {
                                continue; // Repacked meanwhile: look it up again.
                            }

                            throw new ChunkMissingException(id, repository.Root, $"pack '{location.Pack.Path}' is gone");
                        }

                        return DecodePacked(id, location, buffer.AsSpan(0, (int)location.StoredLength), destination);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ChunkCorruptException(id, $"{ex.Message} (pack '{location.Pack.Path}', offset {location.Offset})");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                if (!repository._looseMayExist)
                {
                    throw new ChunkMissingException(id, repository.Root);
                }
            }

            using var handle = OpenLoose(id, FileOptions.None);
            var length = CheckedLength(id, handle);
            var loose = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = 0;
                while (read < length)
                {
                    var n = RandomAccess.Read(handle, loose.AsSpan(read, length - read), read);
                    if (n == 0)
                    {
                        throw new ChunkCorruptException(id, $"truncated (file '{repository.ChunkPath(id.ToHex())}')");
                    }

                    read += n;
                }

                return ChunkCodec.Decode(id, loose.AsSpan(0, length), destination);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(loose);
            }
        }

        private static void ThrowIfZero(ChunkId id)
        {
            if (id.IsZero)
            {
                throw new ArgumentException("The zero chunk is never stored; fill zeros instead.", nameof(id));
            }
        }

        private static int DecodePacked(ChunkId id, PackLocation location, ReadOnlySpan<byte> record, Span<byte> destination)
        {
            try
            {
                return ChunkCodec.Decode(id, record, destination);
            }
            catch (ChunkCorruptException ex)
            {
                throw new ChunkCorruptException(id, $"{ex.Reason} (pack '{location.Pack.Path}', offset {location.Offset})");
            }
        }

        private SafeFileHandle OpenLoose(ChunkId id, FileOptions options)
        {
            try
            {
                return File.OpenHandle(repository.ChunkPath(id.ToHex()), FileMode.Open, FileAccess.Read, FileShare.Read, options);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ChunkMissingException(id, repository.Root);
            }
        }

        private int CheckedLength(ChunkId id, SafeFileHandle handle)
        {
            var length = RandomAccess.GetLength(handle);
            if (length > ChunkCodec.HeaderSize + (long)repository._descriptor.ChunkSize + 1024)
            {
                throw new ChunkCorruptException(id, $"file too large (file '{repository.ChunkPath(id.ToHex())}')");
            }

            return (int)length;
        }
    }

    private sealed class RestorePointReader(LocalDirectoryRepository repository, string directory, RestorePointManifest manifest) : IRestorePointReader
    {
        public RestorePointManifest Manifest { get; } = manifest;

        public IChunkReader Chunks => repository.Chunks;

        public async Task<ChunkIndex> ReadDiskIndexAsync(string diskKey, CancellationToken cancellationToken)
        {
            var disk = Manifest.AllImages.FirstOrDefault(d => d.Key == diskKey)
                ?? throw new KeyNotFoundException($"Restore point {Manifest.RestorePointId} has no disk '{diskKey}'.");
            var raw = ChunkIndexFormat.Decompress(await File.ReadAllBytesAsync(Path.Combine(directory, disk.Index.File), cancellationToken).ConfigureAwait(false));
            if (ChunkIndexFormat.Sha256Hex(raw) != disk.Index.Sha256)
            {
                throw new InvalidDataException($"Index of disk '{diskKey}' in restore point {Manifest.RestorePointId} is corrupt (hash mismatch).");
            }

            var index = ChunkIndexFormat.Deserialize(raw);
            if (index.DiskSize != disk.CapacityBytes || index.ChunkSize != Manifest.Chunking.ChunkSize)
            {
                throw new InvalidDataException($"Index of disk '{diskKey}' does not match the manifest.");
            }

            return index;
        }

        public async Task<ChunkIndex> ReadFileTreeIndexAsync(string key, CancellationToken cancellationToken)
        {
            var tree = Manifest.FileTrees.FirstOrDefault(t => t.Key == key)
                ?? throw new KeyNotFoundException($"Restore point {Manifest.RestorePointId} has no file tree '{key}'.");
            var raw = ChunkIndexFormat.Decompress(await File.ReadAllBytesAsync(Path.Combine(directory, tree.Index.File), cancellationToken).ConfigureAwait(false));
            if (ChunkIndexFormat.Sha256Hex(raw) != tree.Index.Sha256)
            {
                throw new InvalidDataException($"The root index of file tree '{key}' in restore point {Manifest.RestorePointId} is corrupt (hash mismatch).");
            }

            var index = ChunkIndexFormat.Deserialize(raw);
            if (index.DiskSize != tree.RootLength || index.ChunkSize != Manifest.Chunking.ChunkSize)
            {
                throw new InvalidDataException($"The root index of file tree '{key}' does not match the manifest.");
            }

            return index;
        }

        public async Task<Stream> OpenBlobAsync(ManifestBlob blob, CancellationToken cancellationToken)
        {
            var raw = ChunkIndexFormat.Decompress(await File.ReadAllBytesAsync(Path.Combine(directory, blob.File), cancellationToken).ConfigureAwait(false));
            if (ChunkIndexFormat.Sha256Hex(raw) != blob.Sha256 || raw.LongLength != blob.Length)
            {
                throw new InvalidDataException($"Blob '{blob.File}' in restore point {Manifest.RestorePointId} is corrupt (hash mismatch).");
            }

            return new MemoryStream(raw, writable: false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stores chunks for one object (restore point or SQL backup) and protects them from GC until the object is committed
    /// or discarded.
    /// </summary>
    private abstract class ChunkWriterBase(LocalDirectoryRepository repository, Guid id)
    {
        private readonly Lock _protectedLock = new();
        private readonly HashSet<ChunkId> _protected = [];
        private readonly HashSet<PackFile> _dependencies = [];
        private PackAppender? _appender;
        private int _committed;
        private int _closed;

        public Guid Id { get; } = id;

        public bool IsCommitted => Volatile.Read(ref _committed) != 0;

        protected LocalDirectoryRepository Repository { get; } = repository;

        protected bool IsClosed => Volatile.Read(ref _closed) != 0;

        public bool Protects(ChunkId id)
        {
            lock (_protectedLock)
            {
                return _protected.Contains(id);
            }
        }

        public void CopyProtectedTo(HashSet<ChunkId>? target)
        {
            if (target is null)
            {
                return;
            }

            lock (_protectedLock)
            {
                target.UnionWith(_protected);
            }
        }

        public abstract ValueTask DisposeAsync();

        protected void MarkCommitted() => Volatile.Write(ref _committed, 1);

        /// <summary>Marks the writer closed; false when it already was.</summary>
        protected bool Close() => Interlocked.Exchange(ref _closed, 1) == 0;

        protected void ThrowIfClosed()
        {
            if (IsClosed || IsCommitted)
            {
                throw new InvalidOperationException("The writer is already committed or closed.");
            }
        }

        protected async ValueTask<ChunkPutResult> StoreChunkAsync(ChunkId id, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (id.IsZero)
            {
                throw new ArgumentException("Zero chunks are not stored.", nameof(id));
            }

            var level = Repository._descriptor.Compression.Level;
            EncodedChunk Encode()
            {
                var (buffer, length) = ChunkCodec.Encode(plaintext.Span, level);
                return new EncodedChunk(buffer, length, Pooled: true);
            }

            return Repository._packs is { } packs
                ? await StorePackedAsync(packs, id, plaintext.Length, Encode).ConfigureAwait(false)
                : await StoreLooseAsync(id, Encode, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Stores a chunk as it was stored elsewhere (its <c>BDCK</c> record), without compressing it again. The record is
        /// decoded and its hash checked first; a damaged one throws <see cref="ChunkCorruptException"/>.
        /// </summary>
        protected async ValueTask<ChunkPutResult> StoreEncodedChunkAsync(ChunkId id, ReadOnlyMemory<byte> stored, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (id.IsZero)
            {
                throw new ArgumentException("Zero chunks are not stored.", nameof(id));
            }

            var scratch = ArrayPool<byte>.Shared.Rent(Repository._descriptor.ChunkSize);
            int rawLength;
            try
            {
                rawLength = ChunkCodec.Decode(id, stored.Span, scratch);
            }
            catch (ArgumentException)
            {
                throw new ChunkCorruptException(id, $"larger than the repository's chunk size ({Repository._descriptor.ChunkSize} bytes)");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }

            EncodedChunk Encode() => new(stored.ToArray(), stored.Length, Pooled: false);
            return Repository._packs is { } packs
                ? await StorePackedAsync(packs, id, rawLength, Encode).ConfigureAwait(false)
                : await StoreLooseAsync(id, Encode, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether the repository holds the chunk already; it is then protected for this writer's commit like a
        /// deduplicated put (an import need not copy it).
        /// </summary>
        public bool TryReuseChunk(ChunkId id)
        {
            ThrowIfClosed();
            if (id.IsZero)
            {
                return true;
            }

            var repository = Repository;
            lock (repository._stripes[id.Stripe(StripeCount)])
            {
                lock (_protectedLock)
                {
                    _protected.Add(id);
                }

                return repository._packs is { } packs ? TryDeduplicate(packs, id) : File.Exists(repository.ChunkPath(id.ToHex()));
            }
        }

        /// <summary>A chunk's stored record; <paramref name="Pooled"/> buffers go back to the shared pool.</summary>
        protected readonly record struct EncodedChunk(byte[] Buffer, int Length, bool Pooled);

        /// <summary>
        /// Seals this writer's packs and every open pack it deduplicated against, then indexes them: everything its
        /// manifest refers to is durable before the manifest is written.
        /// </summary>
        protected async Task SealForCommitAsync(CancellationToken cancellationToken)
        {
            if (Repository._packs is not { } packs)
            {
                return;
            }

            if (_appender is { } appender)
            {
                await appender.FlushAsync().ConfigureAwait(false);
            }

            PackFile[] dependencies;
            lock (_protectedLock)
            {
                dependencies = [.. _dependencies];
            }

            foreach (var pack in dependencies)
            {
                try
                {
                    await (pack.Owner is { } owner ? owner.SealIfCurrentAsync(pack) : pack.Sealed.Task).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Pack '{pack.Path}', which this backup shares chunks with, could not be sealed: {ex.Message}", ex);
                }
            }

            await packs.PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Seals what this writer appended (writers that deduplicated against it may commit later).</summary>
        protected async Task FlushPacksAsync()
        {
            if (_appender is { } appender)
            {
                try
                {
                    await appender.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The pack is marked dead; writers depending on it fail their commit with the reason.
                }
            }
        }

        private async ValueTask<ChunkPutResult> StorePackedAsync(PackStore packs, ChunkId id, int rawLength, Func<EncodedChunk> encode)
        {
            var repository = Repository;
            var stripe = repository._stripes[id.Stripe(StripeCount)];
            lock (stripe)
            {
                lock (_protectedLock)
                {
                    _protected.Add(id);
                }

                if (TryDeduplicate(packs, id))
                {
                    return new ChunkPutResult(ChunkPutOutcome.Deduplicated, 0);
                }
            }

            var compressStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var (buffer, length, pooled) = encode();
            var compressTicks = System.Diagnostics.Stopwatch.GetTimestamp() - compressStart;
            OpenPack? full;
            try
            {
                lock (stripe)
                {
                    if (TryDeduplicate(packs, id))
                    {
                        return new ChunkPutResult(ChunkPutOutcome.Deduplicated, 0);
                    }

                    // Puts of different stripes run at the same time: they must all share the one appender the commit
                    // flushes. An appender that loses this race would keep its open pack (and the chunk in it) in
                    // incoming/, where the next open deletes it although the committed restore point refers to it.
                    var appender = _appender ?? LazyInitializer.EnsureInitialized(ref _appender, packs.CreateAppender);
                    var location = appender.Append(id, buffer.AsMemory(0, length), (uint)rawLength, out full);
                    packs.AddLive(id, location);
                }
            }
            finally
            {
                if (pooled)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            if (full is not null)
            {
                await _appender!.SealAsync(full).ConfigureAwait(false);
            }

            return new ChunkPutResult(ChunkPutOutcome.Stored, length, compressTicks);
        }

        /// <summary>Under the chunk's stripe lock: is there a copy to reuse? Records open packs as dependencies.</summary>
        private bool TryDeduplicate(PackStore packs, ChunkId id)
        {
            if (packs.TryLocateForDedup(id, out var location))
            {
                if (location.Pack.State == PackState.Open && location.Pack.Owner != _appender)
                {
                    lock (_protectedLock)
                    {
                        _dependencies.Add(location.Pack);
                    }
                }

                return true;
            }

            return Repository._looseMayExist && File.Exists(Repository.ChunkPath(id.ToHex()));
        }

        private async ValueTask<ChunkPutResult> StoreLooseAsync(ChunkId id, Func<EncodedChunk> encode, CancellationToken cancellationToken)
        {
            var repository = Repository;
            var hex = id.ToHex();
            var path = repository.ChunkPath(hex);
            var stripe = repository._stripes[id.Stripe(StripeCount)];
            lock (stripe)
            {
                lock (_protectedLock)
                {
                    _protected.Add(id);
                }

                if (File.Exists(path))
                {
                    return new ChunkPutResult(ChunkPutOutcome.Deduplicated, 0);
                }
            }

            var prefix = hex[..4];
            if (!repository._knownPrefixDirectories.ContainsKey(prefix))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                repository._knownPrefixDirectories.TryAdd(prefix, 0);
            }

            var compressStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var (buffer, length, pooled) = encode();
            var compressTicks = System.Diagnostics.Stopwatch.GetTimestamp() - compressStart;
            var temp = Path.Combine(repository._tempDirectory, $"{hex}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);

                    // Linux: one syncfs() at commit makes all chunks durable; Windows needs a flush per file.
                    if (OperatingSystem.IsWindows())
                    {
                        stream.Flush(flushToDisk: true);
                    }
                }

                lock (stripe)
                {
                    if (File.Exists(path))
                    {
                        return new ChunkPutResult(ChunkPutOutcome.Deduplicated, 0);
                    }

                    File.Move(temp, path);
                    return new ChunkPutResult(ChunkPutOutcome.Stored, length, compressTicks);
                }
            }
            finally
            {
                if (pooled)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }

    private sealed class Writer(LocalDirectoryRepository repository, Guid restorePointId, string vmDirectory, string directory)
        : ChunkWriterBase(repository, restorePointId), IRepositoryWriter
    {
        public Guid RestorePointId => Id;

        public ValueTask<ChunkPutResult> PutChunkAsync(ChunkId id, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken) =>
            StoreChunkAsync(id, plaintext, cancellationToken);

        /// <summary>For imports: a chunk's stored record, checked and kept as it is.</summary>
        public ValueTask<ChunkPutResult> PutEncodedChunkAsync(ChunkId id, ReadOnlyMemory<byte> stored, CancellationToken cancellationToken) =>
            StoreEncodedChunkAsync(id, stored, cancellationToken);

        /// <summary>For imports: one of the restore point's files (an index or a blob) as it was written elsewhere.</summary>
        public async Task WriteRawFileAsync(string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (name.Length is 0 or > 200 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0
                || name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase) || name.Equals(DeletedManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{name}' is not a file of a restore point.", nameof(name));
            }

            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, name), content, overwrite: true, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>For imports: whether a file was written.</summary>
        public bool HasFile(string name) => File.Exists(Path.Combine(directory, name));

        public async Task<ManifestIndexRef> WriteDiskIndexAsync(string diskKey, ChunkIndex index, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (index.ChunkSize != Repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Index chunk size does not match the Repository.", nameof(index));
            }

            var raw = ChunkIndexFormat.Serialize(index);
            var file = $"disk-{SafeName(diskKey)}.idx";
            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, file), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
            return new ManifestIndexRef(file, ChunkIndexFormat.Sha256Hex(raw), index.Entries.LongLength);
        }

        public async Task<ManifestIndexRef> WriteFileTreeIndexAsync(string key, ChunkIndex root, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (root.ChunkSize != Repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Index chunk size does not match the Repository.", nameof(root));
            }

            var raw = ChunkIndexFormat.Serialize(root);
            var file = $"tree-{SafeName(key)}.idx";
            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, file), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
            return new ManifestIndexRef(file, ChunkIndexFormat.Sha256Hex(raw), root.Entries.LongLength);
        }

        public async Task<ManifestBlob> WriteBlobAsync(string name, string format, Stream content, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var raw = memory.ToArray();
            var file = $"{SafeName(name)}.zst";
            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, file), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
            return new ManifestBlob(file, ChunkIndexFormat.Sha256Hex(raw), raw.LongLength, format);
        }

        public async Task<ManifestInfo> CommitAsync(RestorePointManifest manifest, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(manifest);
            if (manifest.RestorePointId != RestorePointId || manifest.RepositoryId != Repository.Info.RepositoryId)
            {
                throw new ArgumentException("Manifest does not belong to this writer/Repository.", nameof(manifest));
            }

            if (manifest.Chunking.ChunkSize != Repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Manifest chunk size does not match the Repository.", nameof(manifest));
            }

            // File trees need a version that walks them in GC and verify; restore points without stay readable by every version.
            manifest = manifest with
            {
                FormatVersion = manifest.FileTrees.Count > 0 ? RestorePointManifest.FileTreeFormatVersion : RestorePointManifest.BaseFormatVersion,
            };

            // Make all chunk/index/blob data durable before the manifest (the commit point) appears.
            await SealForCommitAsync(cancellationToken).ConfigureAwait(false);
            FileSystemSync.SyncFileSystem(Repository.Root);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, CoreJsonContext.Default.RestorePointManifest);
            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, ManifestFileName), json, overwrite: false, cancellationToken).ConfigureAwait(false);
            AtomicFile.SyncDirectory(directory);
            AtomicFile.SyncDirectory(vmDirectory);
            Repository._restorePoints[RestorePointId] = directory;
            MarkCommitted();
            return new ManifestInfo(RestorePointId, directory, manifest);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!Close())
            {
                return;
            }

            await FlushPacksAsync().ConfigureAwait(false);
            if (!IsCommitted)
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }

                    TryDeleteEmptyDirectory(vmDirectory);
                }
                catch (IOException)
                {
                    // Removed on the next repository open (no manifest = not committed).
                }
            }

            Repository.OnWriterClosed(this);
        }
    }
}
