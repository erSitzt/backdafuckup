using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>
/// Backup repository in a directory on a local (or locally mounted) volume.
/// <code>
/// &lt;root&gt;/repo.json                         self-description (format, chunk size, compression)
/// &lt;root&gt;/.owner.lock                       held exclusively by the owning server process
/// &lt;root&gt;/chunks/&lt;hex[0..4]&gt;/&lt;sha256hex&gt;    write-once chunk files (see ChunkCodec)
/// &lt;root&gt;/restore-points/&lt;vm&gt;/&lt;ts&gt;_&lt;id&gt;/   manifest.json (commit point), disk-*.idx, blobs
/// &lt;root&gt;/sql-backups/&lt;vm&gt;/&lt;ts&gt;_&lt;id&gt;/      manifest.json (commit point), file-*.idx (SQL Server backup files)
/// &lt;root&gt;/tmp/                              staging for atomic writes
/// </code>
/// Garbage collection is mark-and-sweep: chunks referenced by committed manifests, by active writers or by
/// writers that committed while GC was running are kept; deletions of restore points wait for GC.
/// </summary>
public sealed partial class LocalDirectoryRepository : IBackupRepository
{
    public const int DefaultChunkSize = 1024 * 1024;
    public const int DefaultCompressionLevel = 3;
    public const string DescriptorFileName = "repo.json";
    public const string ManifestFileName = "manifest.json";
    private const string DeletedManifestFileName = "manifest.deleted";
    private const int StripeCount = 4096;
    private static readonly TimeSpan GcSafetyWindow = TimeSpan.FromMinutes(10);

    private readonly string _chunksDirectory;
    private readonly string _restorePointsDirectory;
    private readonly string _sqlBackupsDirectory;
    private readonly string _tempDirectory;
    private readonly FileStream _ownerLock;
    private readonly RepositoryDescriptor _descriptor;
    private readonly TimeProvider _time;
    private readonly Lock[] _stripes = Enumerable.Range(0, StripeCount).Select(_ => new Lock()).ToArray();
    private readonly ConcurrentDictionary<Guid, string> _restorePoints = new();
    private readonly ConcurrentDictionary<Guid, string> _sqlBackups = new();
    private readonly ConcurrentDictionary<Guid, int> _pins = new();
    private readonly ConcurrentDictionary<Guid, ChunkWriterBase> _activeWriters = new();
    private readonly ConcurrentDictionary<string, byte> _knownPrefixDirectories = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gcMutex = new(1, 1);
    private readonly Lock _lateMarksLock = new();
    private HashSet<ChunkId>? _lateMarks;
    private int _disposed;

    private LocalDirectoryRepository(string root, RepositoryDescriptor descriptor, FileStream ownerLock, TimeProvider time)
    {
        Root = root;
        _descriptor = descriptor;
        _ownerLock = ownerLock;
        _time = time;
        _chunksDirectory = Path.Combine(root, "chunks");
        _restorePointsDirectory = Path.Combine(root, "restore-points");
        _sqlBackupsDirectory = Path.Combine(root, "sql-backups");
        _tempDirectory = Path.Combine(root, "tmp");
        Info = new RepositoryInfo(descriptor.RepositoryId, root, descriptor.ChunkSize, descriptor.Hash, descriptor.Compression.Codec, descriptor.Compression.Level);
        Chunks = new ChunkReader(this);
    }

    public string Root { get; }

    public RepositoryInfo Info { get; }

    public IChunkReader Chunks { get; }

    /// <summary>Creates a new repository in <paramref name="path"/> (which must not already contain one).</summary>
    public static async Task<LocalDirectoryRepository> CreateAsync(
        string path,
        CancellationToken cancellationToken,
        int chunkSize = DefaultChunkSize,
        int compressionLevel = DefaultCompressionLevel,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (chunkSize < 64 * 1024 || chunkSize > 16 * 1024 * 1024 || (chunkSize % (64 * 1024)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be a multiple of 64 KiB between 64 KiB and 16 MiB.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(compressionLevel, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compressionLevel, 19);

        var root = Path.GetFullPath(path);
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(root, DescriptorFileName)))
        {
            throw new InvalidOperationException($"'{root}' already contains a backup repository. Import it instead of creating a new one.");
        }

        foreach (var sub in new[] { "chunks", "restore-points", "sql-backups", "tmp" })
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }

        var descriptor = new RepositoryDescriptor
        {
            RepositoryId = Guid.CreateVersion7(),
            CreatedUtc = (time ?? TimeProvider.System).GetUtcNow(),
            ChunkSize = chunkSize,
            Compression = new RepositoryCompression(compressionLevel > 0 ? "zstd" : "none", compressionLevel),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(descriptor, CoreJsonContext.Default.RepositoryDescriptor);
        await AtomicFile.WriteAsync(Path.Combine(root, "tmp"), Path.Combine(root, DescriptorFileName), json, overwrite: false, cancellationToken).ConfigureAwait(false);
        AtomicFile.SyncDirectory(root);
        return await OpenAsync(root, cancellationToken, time).ConfigureAwait(false);
    }

    /// <summary>Opens an existing repository, takes ownership and recovers from interrupted operations.</summary>
    public static async Task<LocalDirectoryRepository> OpenAsync(string path, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        var root = Path.GetFullPath(path);
        var descriptorPath = Path.Combine(root, DescriptorFileName);
        if (!File.Exists(descriptorPath))
        {
            throw new InvalidOperationException($"'{root}' is not a backup repository ({DescriptorFileName} is missing).");
        }

        var descriptor = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(descriptorPath, cancellationToken).ConfigureAwait(false), CoreJsonContext.Default.RepositoryDescriptor)
            ?? throw new InvalidDataException($"Invalid {DescriptorFileName}.");
        if (descriptor.FormatVersion != RepositoryDescriptor.CurrentFormatVersion)
        {
            throw new NotSupportedException($"Repository format version {descriptor.FormatVersion} is not supported.");
        }

        FileStream ownerLock;
        try
        {
            ownerLock = new FileStream(Path.Combine(root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new RepositoryLockedException(root, ex);
        }

        var repository = new LocalDirectoryRepository(root, descriptor, ownerLock, time ?? TimeProvider.System);
        try
        {
            repository.Recover();
        }
        catch
        {
            await repository.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return repository;
    }

    public IRepositoryWriter BeginWrite(Guid restorePointId, string vmId, IRestorePointPin? basePin)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(vmId);
        if (_restorePoints.ContainsKey(restorePointId))
        {
            throw new InvalidOperationException($"Restore point {restorePointId} already exists.");
        }

        var vmDirectory = Path.Combine(_restorePointsDirectory, SafeName(vmId));
        var directory = Path.Combine(vmDirectory, $"{_time.GetUtcNow():yyyyMMdd'T'HHmmss'Z'}_{restorePointId:N}");
        Directory.CreateDirectory(directory);
        var writer = new Writer(this, restorePointId, vmDirectory, directory);
        if (!_activeWriters.TryAdd(restorePointId, writer))
        {
            throw new InvalidOperationException($"A writer for restore point {restorePointId} is already active.");
        }

        return writer;
    }

    public async IAsyncEnumerable<ManifestInfo> ListRestorePointsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (id, directory) in _restorePoints.ToArray())
        {
            var manifest = await TryReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                yield return new ManifestInfo(id, directory, manifest);
            }
        }
    }

    public async Task<IRestorePointReader> OpenRestorePointAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var directory = GetRestorePointDirectory(restorePointId);
        var manifest = await TryReadManifestAsync(directory, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Restore point {restorePointId} has no manifest.");
        return new RestorePointReader(this, directory, manifest);
    }

    public IRestorePointPin Pin(Guid restorePointId)
    {
        ThrowIfDisposed();
        _ = GetRestorePointDirectory(restorePointId);
        _pins.AddOrUpdate(restorePointId, 1, (_, count) => count + 1);
        return new RestorePointPin(this, restorePointId);
    }

    public async Task DeleteRestorePointAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gcMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pins.TryGetValue(restorePointId, out var count) && count > 0)
            {
                throw new RestorePointPinnedException(restorePointId);
            }

            if (!_restorePoints.TryGetValue(restorePointId, out var directory))
            {
                return;
            }

            var manifest = Path.Combine(directory, ManifestFileName);
            if (File.Exists(manifest))
            {
                File.Move(manifest, Path.Combine(directory, DeletedManifestFileName), overwrite: true);
                AtomicFile.SyncDirectory(directory);
            }

            _restorePoints.TryRemove(restorePointId, out _);
            Directory.Delete(directory, recursive: true);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(directory)!);
        }
        finally
        {
            _gcMutex.Release();
        }
    }

    public async Task<GcResult> CollectGarbageAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gcMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedUtc = _time.GetUtcNow().UtcDateTime;
            lock (_lateMarksLock)
            {
                _lateMarks = [];
            }

            // Mark: every chunk referenced by a committed manifest. Deletions cannot run concurrently (GC mutex),
            // and pinned base restore points of running backups are therefore always included.
            var marked = new HashSet<ChunkId>();

            // File trees: listings already walked (identical subtrees are one listing). Kept apart from the marked chunks on
            // purpose: a file whose bytes equal a listing marks that chunk, which must not stop its subtree from being walked.
            var walkedListings = new HashSet<ChunkId>();
            var restorePoints = 0;
            foreach (var (id, directory) in _restorePoints.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = await TryReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                if (manifest is null)
                {
                    continue;
                }

                await using var reader = new RestorePointReader(this, directory, manifest);
                foreach (var disk in manifest.AllImages)
                {
                    var index = await reader.ReadDiskIndexAsync(disk.Key, cancellationToken).ConfigureAwait(false);
                    foreach (var entry in index.Entries)
                    {
                        if (!entry.IsZero)
                        {
                            marked.Add(entry);
                        }
                    }
                }

                foreach (var tree in manifest.FileTrees)
                {
                    var root = await reader.ReadFileTreeIndexAsync(tree.Key, cancellationToken).ConfigureAwait(false);
                    FileTreeWalker.Visit(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), Chunks, _descriptor.ChunkSize, id => marked.Add(id), walkedListings);
                }

                restorePoints++;
                progress?.Report($"Marked restore point {id} ({restorePoints} done, {marked.Count} chunks referenced)");
            }

            var sqlBackups = 0;
            foreach (var (id, directory) in _sqlBackups.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = await TryReadSqlBackupManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                if (manifest is null)
                {
                    continue;
                }

                foreach (var file in manifest.Files)
                {
                    var index = await ReadSqlBackupIndexAsync(directory, manifest, file, cancellationToken).ConfigureAwait(false);
                    foreach (var entry in index.Entries)
                    {
                        if (!entry.IsZero)
                        {
                            marked.Add(entry);
                        }
                    }
                }

                if (++sqlBackups % 100 == 0)
                {
                    progress?.Report($"Marked {sqlBackups} SQL backups ({marked.Count} chunks referenced)");
                }
            }

            // Sweep.
            long scanned = 0, deleted = 0, freed = 0;
            var cutoff = startedUtc - GcSafetyWindow;
            foreach (var file in Directory.EnumerateFiles(_chunksDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;
                var name = Path.GetFileName(file);
                if (name.Length != ChunkId.Size * 2 || !IsHex(name))
                {
                    continue;
                }

                var id = ChunkId.Parse(name);
                if (marked.Contains(id))
                {
                    continue;
                }

                lock (_stripes[id.Stripe(StripeCount)])
                {
                    if (IsProtected(id))
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    if (!info.Exists || info.LastWriteTimeUtc > cutoff)
                    {
                        continue;
                    }

                    freed += info.Length;
                    info.Delete();
                    deleted++;
                }

                if (deleted % 10_000 == 1)
                {
                    progress?.Report($"Deleted {deleted} unreferenced chunks ({scanned} scanned)");
                }
            }

            PurgeTemp(olderThan: TimeSpan.FromHours(1));
            progress?.Report($"Garbage collection finished: {deleted} of {scanned} chunks deleted, {freed} bytes freed.");
            return new GcResult(scanned, deleted, freed, marked.Count);
        }
        finally
        {
            lock (_lateMarksLock)
            {
                _lateMarks = null;
            }

            _gcMutex.Release();
        }
    }

    public async Task<VerifyResult> VerifyAsync(Guid restorePointId, VerifyDepth depth, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        long checkedChunks = 0;
        await using var reader = await OpenRestorePointAsync(restorePointId, cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(_descriptor.ChunkSize);
        try
        {
            var seen = new HashSet<ChunkId>();

            // What to check: per disk its index, per file tree every chunk its listings reference (reading the listings
            // checks their chunks in full on the way).
            var sources = new List<(string What, IReadOnlyList<ChunkId> Ids)>();
            foreach (var disk in reader.Manifest.AllImages)
            {
                try
                {
                    sources.Add(($"Disk {disk.Key}", (await reader.ReadDiskIndexAsync(disk.Key, cancellationToken).ConfigureAwait(false)).Entries));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    problems.Add($"Disk {disk.Key}: index unreadable: {ex.Message}");
                }
            }

            foreach (var tree in reader.Manifest.FileTrees)
            {
                try
                {
                    var root = await reader.ReadFileTreeIndexAsync(tree.Key, cancellationToken).ConfigureAwait(false);
                    var ids = new List<ChunkId>();
                    FileTreeWalker.Visit(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), Chunks, _descriptor.ChunkSize, ids.Add, new HashSet<ChunkId>());
                    sources.Add(($"File tree {tree.Key}", ids));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    problems.Add($"File tree {tree.Key}: listings unreadable: {ex.Message}");
                }
            }

            foreach (var (what, ids) in sources)
            {
                foreach (var id in ids)
                {
                    if (id.IsZero || !seen.Add(id))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (depth == VerifyDepth.Full)
                        {
                            await Chunks.ReadAsync(id, buffer, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await CheckChunkHeaderAsync(id, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (IOException ex)
                    {
                        if (problems.Count < 100)
                        {
                            problems.Add($"{what}: {ex.Message}");
                        }
                    }

                    if (++checkedChunks % 1000 == 0)
                    {
                        progress?.Report($"Verified {checkedChunks} chunks");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new VerifyResult(problems.Count == 0, checkedChunks, problems);
    }

    public StorageStats GetStorageStats()
    {
        var drive = new DriveInfo(Root);
        return new StorageStats(drive.TotalSize, drive.AvailableFreeSpace);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var writer in _activeWriters.Values.ToArray())
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }

        await _ownerLock.DisposeAsync().ConfigureAwait(false);
        _gcMutex.Dispose();
    }

    internal string ChunkPath(string hex) => Path.Combine(_chunksDirectory, hex[..4], hex);

    private void Recover()
    {
        PurgeTemp(olderThan: TimeSpan.Zero);
        Directory.CreateDirectory(_sqlBackupsDirectory);
        RecoverObjects(_restorePointsDirectory, _restorePoints);
        RecoverObjects(_sqlBackupsDirectory, _sqlBackups);
    }

    private static void RecoverObjects(string root, ConcurrentDictionary<Guid, string> objects)
    {
        foreach (var vmDirectory in Directory.EnumerateDirectories(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(vmDirectory))
            {
                var name = Path.GetFileName(directory);
                var separator = name.LastIndexOf('_');
                if (separator < 0 || !Guid.TryParseExact(name[(separator + 1)..], "N", out var id))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(directory, ManifestFileName)))
                {
                    objects[id] = directory;
                }
                else
                {
                    // Interrupted delete (manifest.deleted) or a backup that never committed.
                    Directory.Delete(directory, recursive: true);
                }
            }

            TryDeleteEmptyDirectory(vmDirectory);
        }
    }

    private void PurgeTemp(TimeSpan olderThan)
    {
        Directory.CreateDirectory(_tempDirectory);
        var cutoff = _time.GetUtcNow().UtcDateTime - olderThan;
        foreach (var file in Directory.EnumerateFiles(_tempDirectory))
        {
            if (olderThan == TimeSpan.Zero || File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // In use by a concurrent write; it will be cleaned up later.
                }
            }
        }
    }

    private bool IsProtected(ChunkId id)
    {
        foreach (var writer in _activeWriters.Values)
        {
            if (writer.Protects(id))
            {
                return true;
            }
        }

        lock (_lateMarksLock)
        {
            return _lateMarks?.Contains(id) == true;
        }
    }

    private void OnWriterClosed(ChunkWriterBase writer)
    {
        if (writer.IsCommitted)
        {
            lock (_lateMarksLock)
            {
                writer.CopyProtectedTo(_lateMarks);
            }
        }

        _activeWriters.TryRemove(writer.Id, out _);
    }

    private string GetRestorePointDirectory(Guid restorePointId) =>
        _restorePoints.TryGetValue(restorePointId, out var directory)
            ? directory
            : throw new KeyNotFoundException($"Restore point {restorePointId} does not exist in repository {Info.RepositoryId}.");

    private async Task CheckChunkHeaderAsync(ChunkId id, CancellationToken cancellationToken)
    {
        var path = ChunkPath(id.ToHex());
        if (!File.Exists(path))
        {
            throw new ChunkMissingException(id);
        }

        var header = new byte[ChunkCodec.HeaderSize];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var (_, _, payload) = ChunkCodec.ReadHeader(id, header);
        if (stream.Length != ChunkCodec.HeaderSize + payload)
        {
            throw new ChunkCorruptException(id, "truncated");
        }
    }

    private static async Task<RestorePointManifest?> TryReadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), CoreJsonContext.Default.RestorePointManifest);
        if (manifest is { FormatVersion: > RestorePointManifest.CurrentFormatVersion })
        {
            throw new NotSupportedException($"Manifest format version {manifest.FormatVersion} is not supported.");
        }

        return manifest;
    }

    private static void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
            // A concurrent backup just created something inside.
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigitLower(c))
            {
                return false;
            }
        }

        return true;
    }

    internal static string SafeName(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_';
        }

        return new string(buffer);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class RestorePointPin(LocalDirectoryRepository repository, Guid restorePointId) : IRestorePointPin
    {
        private int _released;

        public Guid RestorePointId { get; } = restorePointId;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                repository._pins.AddOrUpdate(RestorePointId, 0, (_, count) => Math.Max(0, count - 1));
            }
        }
    }
}

public sealed class RepositoryLockedException(string root, Exception inner)
    : IOException($"The repository '{root}' is in use by another process.", inner);
