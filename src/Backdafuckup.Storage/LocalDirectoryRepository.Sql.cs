using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>SQL backup objects: SQL Server backup files of one agent run, stored as chunks with one index per file.</summary>
public sealed partial class LocalDirectoryRepository
{
    public ISqlBackupWriter BeginSqlBackup(Guid sqlBackupId, string vmId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        if (_sqlBackups.ContainsKey(sqlBackupId))
        {
            throw new InvalidOperationException($"SQL backup {sqlBackupId} already exists.");
        }

        var vmDirectory = Path.Combine(_sqlBackupsDirectory, SafeName(vmId));
        var directory = Path.Combine(vmDirectory, $"{_time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}_{sqlBackupId:N}");
        Directory.CreateDirectory(directory);
        var writer = new SqlBackupWriter(this, sqlBackupId, vmDirectory, directory);
        if (!_activeWriters.TryAdd(sqlBackupId, writer))
        {
            throw new InvalidOperationException($"A writer for SQL backup {sqlBackupId} is already active.");
        }

        return writer;
    }

    public async IAsyncEnumerable<SqlBackupInfo> ListSqlBackupsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (id, directory) in _sqlBackups.ToArray())
        {
            var manifest = await TryReadSqlBackupManifestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                yield return new SqlBackupInfo(id, directory, manifest);
            }
        }
    }

    public async Task<ISqlBackupReader> OpenSqlBackupAsync(Guid sqlBackupId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var directory = GetSqlBackupDirectory(sqlBackupId);
        var manifest = await TryReadSqlBackupManifestAsync(directory, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"SQL backup {sqlBackupId} has no manifest.");
        return new SqlBackupReader(this, directory, manifest);
    }

    public IRestorePointPin PinSqlBackup(Guid sqlBackupId)
    {
        ThrowIfDisposed();
        _ = GetSqlBackupDirectory(sqlBackupId);
        _pins.AddOrUpdate(sqlBackupId, 1, (_, count) => count + 1);
        return new RestorePointPin(this, sqlBackupId);
    }

    public async Task DeleteSqlBackupAsync(Guid sqlBackupId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gcMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pins.TryGetValue(sqlBackupId, out var count) && count > 0)
            {
                throw new SqlBackupPinnedException(sqlBackupId);
            }

            if (!_sqlBackups.TryGetValue(sqlBackupId, out var directory))
            {
                return;
            }

            var manifest = Path.Combine(directory, ManifestFileName);
            if (File.Exists(manifest))
            {
                File.Move(manifest, Path.Combine(directory, DeletedManifestFileName), overwrite: true);
                AtomicFile.SyncDirectory(directory);
            }

            _sqlBackups.TryRemove(sqlBackupId, out _);
            Directory.Delete(directory, recursive: true);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(directory)!);
        }
        finally
        {
            _gcMutex.Release();
        }
    }

    public async Task<VerifyResult> VerifySqlBackupAsync(Guid sqlBackupId, VerifyDepth depth, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        long checkedChunks = 0;
        await using var reader = (SqlBackupReader)await OpenSqlBackupAsync(sqlBackupId, cancellationToken).ConfigureAwait(false);
        foreach (var file in reader.Manifest.Files)
        {
            ChunkIndex index;
            try
            {
                index = await ReadSqlBackupIndexAsync(reader.Directory, reader.Manifest, file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                problems.Add($"{file.FileName}: index unreadable: {ex.Message}");
                continue;
            }

            if (depth == VerifyDepth.Full)
            {
                // Reading the whole file checks every chunk and the file's SHA-256.
                try
                {
                    await using var stream = new SqlBackupFileStream(Chunks, index, file, sqlBackupId);
                    var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                    try
                    {
                        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                        {
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    checkedChunks += index.Entries.LongLength;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    problems.Add($"{file.FileName}: {ex.Message}");
                }

                continue;
            }

            foreach (var id in index.Entries)
            {
                if (id.IsZero)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await CheckChunkHeaderAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    if (problems.Count < 100)
                    {
                        problems.Add($"{file.FileName}: {ex.Message}");
                    }
                }

                checkedChunks++;
            }
        }

        progress?.Report($"Verified SQL backup {sqlBackupId}: {reader.Manifest.Files.Count} file(s), {checkedChunks} chunks");
        return new VerifyResult(problems.Count == 0, checkedChunks, problems);
    }

    private string GetSqlBackupDirectory(Guid sqlBackupId) =>
        _sqlBackups.TryGetValue(sqlBackupId, out var directory)
            ? directory
            : throw new KeyNotFoundException($"SQL backup {sqlBackupId} does not exist in repository {Info.RepositoryId}.");

    private static async Task<SqlBackupManifest?> TryReadSqlBackupManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), SqlBackupJsonContext.Default.SqlBackupManifest);
        if (manifest is { FormatVersion: > SqlBackupManifest.CurrentFormatVersion })
        {
            throw new NotSupportedException($"SQL backup manifest format version {manifest.FormatVersion} is not supported.");
        }

        return manifest;
    }

    private static async Task<ChunkIndex> ReadSqlBackupIndexAsync(string directory, SqlBackupManifest manifest, SqlBackupFile file, CancellationToken cancellationToken)
    {
        var raw = ChunkIndexFormat.Decompress(await File.ReadAllBytesAsync(Path.Combine(directory, file.Index.File), cancellationToken).ConfigureAwait(false));
        if (ChunkIndexFormat.Sha256Hex(raw) != file.Index.Sha256)
        {
            throw new InvalidDataException($"Index of '{file.FileName}' in SQL backup {manifest.SqlBackupId} is corrupt (hash mismatch).");
        }

        var index = ChunkIndexFormat.Deserialize(raw);
        if (index.DiskSize != file.Length || index.ChunkSize != manifest.Chunking.ChunkSize)
        {
            throw new InvalidDataException($"Index of '{file.FileName}' in SQL backup {manifest.SqlBackupId} does not match the manifest.");
        }

        return index;
    }

    private sealed class SqlBackupWriter(LocalDirectoryRepository repository, Guid sqlBackupId, string vmDirectory, string directory)
        : ChunkWriterBase(repository, sqlBackupId), ISqlBackupWriter
    {
        private readonly Dictionary<string, SqlBackupStoredFile> _files = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Guid SqlBackupId => Id;

        public async Task<SqlBackupStoredFile> WriteFileAsync(string key, Stream content, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(content);
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_files.ContainsKey(key))
                {
                    throw new InvalidOperationException($"SQL backup {SqlBackupId} already has a file '{key}'.");
                }

                var chunkSize = Repository._descriptor.ChunkSize;
                var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var entries = new List<ChunkId>();
                long length = 0, newChunks = 0, newBytes = 0;
                try
                {
                    while (true)
                    {
                        var filled = await FillAsync(content, buffer.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
                        if (filled == 0)
                        {
                            break;
                        }

                        var data = buffer.AsMemory(0, filled);
                        hash.AppendData(data.Span);
                        length += filled;
                        if (data.Span.IndexOfAnyExcept((byte)0) < 0)
                        {
                            entries.Add(ChunkId.Zero);
                        }
                        else
                        {
                            var id = ChunkId.Compute(data.Span);
                            var result = await StoreChunkAsync(id, data, cancellationToken).ConfigureAwait(false);
                            if (result.Outcome == ChunkPutOutcome.Stored)
                            {
                                newChunks++;
                                newBytes += result.StoredBytes;
                            }

                            entries.Add(id);
                        }

                        if (filled < chunkSize)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                var index = new ChunkIndex(chunkSize, length, [.. entries]);
                var raw = ChunkIndexFormat.Serialize(index);
                var name = $"file-{SafeName(key)}.idx";
                await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, name), ChunkIndexFormat.Compress(raw), overwrite: true, cancellationToken).ConfigureAwait(false);
                var stored = new SqlBackupStoredFile(
                    key, length, Convert.ToHexStringLower(hash.GetHashAndReset()), new ManifestIndexRef(name, ChunkIndexFormat.Sha256Hex(raw), index.Entries.LongLength), newChunks, newBytes);
                _files[key] = stored;
                return stored;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task<SqlBackupInfo> CommitAsync(SqlBackupManifest manifest, CancellationToken cancellationToken)
        {
            ThrowIfClosed();
            ArgumentNullException.ThrowIfNull(manifest);
            if (manifest.SqlBackupId != SqlBackupId || manifest.RepositoryId != Repository.Info.RepositoryId)
            {
                throw new ArgumentException("Manifest does not belong to this writer/Repository.", nameof(manifest));
            }

            if (manifest.Chunking.ChunkSize != Repository._descriptor.ChunkSize)
            {
                throw new ArgumentException("Manifest chunk size does not match the Repository.", nameof(manifest));
            }

            foreach (var file in manifest.Files)
            {
                if (!_files.TryGetValue(file.Key, out var stored) || stored.Index != file.Index || stored.Length != file.Length || stored.Sha256 != file.Sha256)
                {
                    throw new ArgumentException($"File '{file.Key}' of the manifest was not written by this writer as described.", nameof(manifest));
                }
            }

            FileSystemSync.SyncFileSystem(Repository.Root);
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, SqlBackupJsonContext.Default.SqlBackupManifest);
            await AtomicFile.WriteAsync(Repository._tempDirectory, Path.Combine(directory, ManifestFileName), json, overwrite: false, cancellationToken).ConfigureAwait(false);
            AtomicFile.SyncDirectory(directory);
            AtomicFile.SyncDirectory(vmDirectory);
            Repository._sqlBackups[SqlBackupId] = directory;
            MarkCommitted();
            return new SqlBackupInfo(SqlBackupId, directory, manifest);
        }

        public override ValueTask DisposeAsync()
        {
            if (!Close())
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

            _writeLock.Dispose();
            Repository.OnWriterClosed(this);
            return ValueTask.CompletedTask;
        }

        private static async Task<int> FillAsync(Stream content, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var filled = 0;
            while (filled < buffer.Length)
            {
                var n = await content.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                filled += n;
            }

            return filled;
        }
    }

    private sealed class SqlBackupReader(LocalDirectoryRepository repository, string directory, SqlBackupManifest manifest) : ISqlBackupReader
    {
        public SqlBackupManifest Manifest { get; } = manifest;

        public string Directory { get; } = directory;

        public async Task<Stream> OpenFileAsync(string key, CancellationToken cancellationToken)
        {
            var file = Manifest.Files.FirstOrDefault(f => f.Key == key)
                ?? throw new KeyNotFoundException($"SQL backup {Manifest.SqlBackupId} has no file '{key}'.");
            var index = await ReadSqlBackupIndexAsync(Directory, Manifest, file, cancellationToken).ConfigureAwait(false);
            return new SqlBackupFileStream(repository.Chunks, index, file, Manifest.SqlBackupId);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Forward-only stream over a backup file's chunks; checks the file's SHA-256 when it reaches the end.</summary>
    private sealed class SqlBackupFileStream(IChunkReader chunks, ChunkIndex index, SqlBackupFile file, Guid sqlBackupId) : Stream
    {
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(index.ChunkSize);
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _offset;
        private int _available;
        private long _next;
        private long _position;
        private bool _verified;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => file.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset == _available)
            {
                if (_next == index.Entries.LongLength)
                {
                    VerifyEnd();
                    return 0;
                }

                var expected = ChunkLength();
                var id = index.Entries[_next];
                var read = id.IsZero ? Zero(expected) : chunks.Read(id, _buffer);
                Loaded(read, expected);
            }

            return Copy(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == _available)
            {
                if (_next == index.Entries.LongLength)
                {
                    VerifyEnd();
                    return 0;
                }

                var expected = ChunkLength();
                var id = index.Entries[_next];
                var read = id.IsZero ? Zero(expected) : await chunks.ReadAsync(id, _buffer, cancellationToken).ConfigureAwait(false);
                Loaded(read, expected);
            }

            return Copy(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                ArrayPool<byte>.Shared.Return(_buffer);
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }

        private int ChunkLength() => (int)Math.Min(index.ChunkSize, file.Length - (_next * index.ChunkSize));

        private int Zero(int length)
        {
            _buffer.AsSpan(0, length).Clear();
            return length;
        }

        private void Loaded(int read, int expected)
        {
            if (read != expected)
            {
                throw new InvalidDataException(
                    $"Chunk {_next} of '{file.FileName}' in SQL backup {sqlBackupId} has {read} bytes, expected {expected}.");
            }

            _hash.AppendData(_buffer, 0, read);
            _offset = 0;
            _available = read;
            _next++;
        }

        private int Copy(Span<byte> destination)
        {
            var n = Math.Min(destination.Length, _available - _offset);
            _buffer.AsSpan(_offset, n).CopyTo(destination);
            _offset += n;
            _position += n;
            return n;
        }

        private void VerifyEnd()
        {
            if (_verified)
            {
                return;
            }

            _verified = true;
            var actual = Convert.ToHexStringLower(_hash.GetHashAndReset());
            if (!string.Equals(actual, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"'{file.FileName}' in SQL backup {sqlBackupId} is corrupt: SHA-256 {actual}, expected {file.Sha256}.");
            }
        }
    }
}
