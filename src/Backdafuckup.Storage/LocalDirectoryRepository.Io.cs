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
            if (id.IsZero)
            {
                throw new ArgumentException("The zero chunk is never stored; fill zeros instead.", nameof(id));
            }

            SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(repository.ChunkPath(id.ToHex()), FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new ChunkMissingException(id);
            }

            using (handle)
            {
                var length = RandomAccess.GetLength(handle);
                if (length > ChunkCodec.HeaderSize + (long)repository._descriptor.ChunkSize + 1024)
                {
                    throw new ChunkCorruptException(id, "file too large");
                }

                var buffer = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    var read = 0;
                    while (read < length)
                    {
                        var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read, (int)length - read), read, cancellationToken).ConfigureAwait(false);
                        if (n == 0)
                        {
                            throw new ChunkCorruptException(id, "truncated");
                        }

                        read += n;
                    }

                    return ChunkCodec.Decode(id, buffer.AsSpan(0, (int)length), destination.Span);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }

    private sealed class RestorePointReader(LocalDirectoryRepository repository, string directory, RestorePointManifest manifest) : IRestorePointReader
    {
        public RestorePointManifest Manifest { get; } = manifest;

        public IChunkReader Chunks => repository.Chunks;

        public async Task<ChunkIndex> ReadDiskIndexAsync(string diskKey, CancellationToken cancellationToken)
        {
            var disk = Manifest.Disks.FirstOrDefault(d => d.Key == diskKey)
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

    private sealed class Writer(LocalDirectoryRepository repository, Guid restorePointId, string vmDirectory, string directory) : IRepositoryWriter
    {
        private readonly Lock _protectedLock = new();
        private readonly HashSet<ChunkId> _protected = [];
        private int _committed;
        private int _closed;

        public Guid RestorePointId { get; } = restorePointId;

        public bool IsCommitted => Volatile.Read(ref _committed) != 0;

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

        public async ValueTask<ChunkPutResult> PutChunkAsync(ChunkId id, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (id.IsZero)
            {
                throw new ArgumentException("Zero chunks are not stored.", nameof(id));
            }

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

            var (buffer, length) = ChunkCodec.Encode(plaintext.Span, repository._descriptor.Compression.Level);
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
                    return new ChunkPutResult(ChunkPutOutcome.Stored, length);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        public async Task<ManifestIndexRef> WriteDiskIndexAsync(string diskKey, ChunkIndex index, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            if (index.ChunkSize != repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Index chunk size does not match the repository.", nameof(index));
            }

            var raw = ChunkIndexFormat.Serialize(index);
            var file = $"disk-{SafeName(diskKey)}.idx";
            await AtomicFile.WriteAsync(repository._tempDirectory, Path.Combine(directory, file), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
            return new ManifestIndexRef(file, ChunkIndexFormat.Sha256Hex(raw), index.Entries.LongLength);
        }

        public async Task<ManifestBlob> WriteBlobAsync(string name, string format, Stream content, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            var raw = memory.ToArray();
            var file = $"{SafeName(name)}.zst";
            await AtomicFile.WriteAsync(repository._tempDirectory, Path.Combine(directory, file), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
            return new ManifestBlob(file, ChunkIndexFormat.Sha256Hex(raw), raw.LongLength, format);
        }

        public async Task<ManifestInfo> CommitAsync(RestorePointManifest manifest, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(manifest);
            if (manifest.RestorePointId != RestorePointId || manifest.RepositoryId != repository.Info.RepositoryId)
            {
                throw new ArgumentException("Manifest does not belong to this writer/repository.", nameof(manifest));
            }

            if (manifest.Chunking.ChunkSize != repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Manifest chunk size does not match the repository.", nameof(manifest));
            }

            // Make all chunk/index/blob data durable before the manifest (the commit point) appears.
            FileSystemSync.SyncFileSystem(repository.Root);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, CoreJsonContext.Default.RestorePointManifest);
            await AtomicFile.WriteAsync(repository._tempDirectory, Path.Combine(directory, ManifestFileName), json, overwrite: false, cancellationToken).ConfigureAwait(false);
            AtomicFile.SyncDirectory(directory);
            AtomicFile.SyncDirectory(vmDirectory);
            repository._restorePoints[RestorePointId] = directory;
            Volatile.Write(ref _committed, 1);
            return new ManifestInfo(RestorePointId, directory, manifest);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

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

            repository.OnWriterClosed(this);
            return ValueTask.CompletedTask;
        }

        private void ThrowIfClosed()
        {
            if (Volatile.Read(ref _closed) != 0 || IsCommitted)
            {
                throw new InvalidOperationException("The restore point writer is already committed or closed.");
            }
        }
    }
}
