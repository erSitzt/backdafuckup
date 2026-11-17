using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Storage;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Format;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Tape;

/// <summary>A queued import of a tape copy into a repository, or a direct VM restore from it (the session's parameters).</summary>
/// <param name="IncludeSql">Also the SQL Server backups of the VM in the set from this restore point on (point-in-time restores).</param>
/// <param name="Vm">A direct VM restore: where and how; the copy goes through a cache instead of a repository.</param>
public sealed record TapeRestoreRequest(Guid CopyId, Guid RepositoryId, bool IncludeSql = true, Contracts.RestoreRequestDto? Vm = null);

/// <summary>
/// Brings a tape copy back into a repository: reads its restore-point file (its files and where its chunks are), then
/// the chunks tape by tape and file by file, stores them as they were stored (checked, not compressed again) and commits
/// the restore point under a new id. It belongs to no job, so job retention never deletes it.
/// </summary>
public sealed class TapeRestoreRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeLocks locks,
    TapeSessionRunner robot,
    RepositoryManager repositories,
    CredentialProtector protector,
    SessionEventWriter events,
    Sql.SqlCatalogBackfill sqlBackfill,
    TimeProvider time,
    Telemetry.BackupMetrics metrics,
    TapeOperatorRequests requests,
    RestoreSessionRunner vmRestore,
    SessionTracker tracker,
    ScaleOut.RepositoryPlacement placement,
    Hosting.ServerPaths paths,
    Microsoft.Extensions.Options.IOptions<TapeOptions> options)
{
    /// <summary>Where direct VM restores keep the chunks they read (one directory per session, deleted at the end and at start).</summary>
    internal string CacheRoot => options.Value.CacheDirectory is { Length: > 0 } directory ? Path.GetFullPath(directory) : Path.Combine(paths.DataDirectory, "tape", "cache");

    /// <summary>At start: caches of direct restores a crash or restart left behind.</summary>
    public void DeleteLeftoverCaches(Action<string> warn)
    {
        if (!Directory.Exists(CacheRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(CacheRoot))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"The tape cache {directory} could not be deleted: {ex.Message}");
            }
        }
    }

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<TapeRestoreRequest>(session.Parameters ?? "null") ?? throw new InvalidOperationException("The tape session has no parameters.");
        TapeCopyEntity copy;
        MediaSetEntity set;
        MediaPoolEntity pool;
        List<TapeMediumEntity> media;
        List<TapeFileEntity> files;
        List<TapeCopyEntity> sqlCopies;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            copy = await db.TapeCopies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CopyId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tape copy {request.CopyId} does not exist.");
            set = await db.MediaSets.AsNoTracking().FirstAsync(s => s.Id == copy.SetId, cancellationToken).ConfigureAwait(false);
            pool = await db.MediaPools.AsNoTracking().FirstAsync(p => p.Id == set.PoolId, cancellationToken).ConfigureAwait(false);
            media = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == set.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            files = await db.TapeFiles.AsNoTracking().Where(f => f.SetId == set.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var sameRepository = await ScaleOut.RepositoryGroups.MembersAsync(db, copy.RepositoryId, cancellationToken).ConfigureAwait(false);
            sqlCopies = !request.IncludeSql || request.Vm is not null || copy.Kind != TapeFileTypes.RestorePoint ? [] : await db.TapeCopies.AsNoTracking()
                .Where(c => c.SetId == set.Id && c.Kind == TapeFileTypes.Sql && c.VmId == copy.VmId && sameRepository.Contains(c.RepositoryId) && c.CreatedUtc >= copy.CreatedUtc)
                .OrderBy(c => c.CreatedUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var library = await libraries.LoadAsync(pool.LibraryId, cancellationToken).ConfigureAwait(false);
        var missing = library.ChangerSerial is null ? [] : copy.NeededSequences.Concat(sqlCopies.SelectMany(c => c.NeededSequences)).Distinct().Where(s => media.FirstOrDefault(m => m.Sequence == s) is not { Location: not TapeLocation.Offline } m || m.LibraryId != library.Id).ToList();
        if (missing.Count > 0)
        {
            var names = missing.Select(s => media.FirstOrDefault(m => m.Sequence == s)?.Barcode ?? $"#{s}");
            throw new IOException($"{copy.VmName} needs tape(s) {string.Join(", ", names)}, which are not in {library.Name}: import them from the mail slots first.");
        }

        var original = JsonSerializer.Deserialize(copy.Manifest, CoreJsonContext.Default.RestorePointManifest)!;
        IRepositoryTransfer transfer;
        LocalDirectoryRepository? cache = null;
        string? cacheDirectory = null;
        // Into a scale-out repository: the extent with the most room.
        var targetId = request.Vm is null ? (await placement.ForImportAsync(request.RepositoryId, copy.StoredBytes, cancellationToken).ConfigureAwait(false)).RepositoryId : request.RepositoryId;
        if (request.Vm is null)
        {
            var repository = await repositories.GetAsync(targetId, cancellationToken).ConfigureAwait(false);
            transfer = repository as IRepositoryTransfer ?? throw new InvalidOperationException("This repository cannot take copies from tape.");
            var free = repository.GetStorageStats().FreeBytes;
            if (free < copy.StoredBytes + (copy.StoredBytes / 20))
            {
                throw new IOException($"The repository has {VmBackupTask.Format(free)} free; {copy.VmName} needs up to {VmBackupTask.Format(copy.StoredBytes)} (+5 %).");
            }
        }
        else
        {
            // Directly: only this restore point's chunks, into a cache that is deleted at the end.
            Directory.CreateDirectory(CacheRoot);
            var free = new DriveInfo(CacheRoot).AvailableFreeSpace;
            if (free < copy.StoredBytes + (copy.StoredBytes / 20))
            {
                throw new IOException($"The tape cache {CacheRoot} has {VmBackupTask.Format(free)} free; {copy.VmName} needs {VmBackupTask.Format(copy.StoredBytes)} (+5 %). " +
                    "Set Tape:CacheDirectory to a larger disk, or bring the copy back into a repository.");
            }

            cacheDirectory = Path.Combine(CacheRoot, session.Id.ToString("N"));
            cache = await LocalDirectoryRepository.CreateAsync(cacheDirectory, cancellationToken, chunkSize: original.Chunking.ChunkSize, time: time).ConfigureAwait(false);
            transfer = cache;
            log.Info($"{copy.VmName} is read from tape into the cache {cacheDirectory} (up to {VmBackupTask.Format(copy.StoredBytes)}), then restored from there.");
        }

        var kek = await KekAsync(set, cancellationToken).ConfigureAwait(false);
        var cipher = kek is null ? null : new AesGcmRecordCipher(TapeKeys.SetKey(kek, set.Id, TapeKeys.SoftwarePurpose), set.Id);
        // Encrypted by the drive: every tape needs the set's key in the drive after its load (the drive forgets it at unload).
        Func<TapeDrive, CancellationToken, Task>? afterLoad = kek is not null && pool.Encryption == TapeEncryption.Hardware
            ? async (tape, token) =>
            {
                var capabilities = await tape.ReadEncryptionCapabilitiesAsync(token).ConfigureAwait(false)
                    ?? throw new IOException($"{copy.VmName} was encrypted by the drive, but this drive cannot decrypt.");
                await tape.SetEncryptionAsync(capabilities, TapeKeys.SetKey(kek, set.Id, TapeKeys.HardwarePurpose), TapeKeys.KeyAssociatedData(set.KeyFingerprint!, set.Id), token).ConfigureAwait(false);
            }
            : null;
        var holder = $"Restore {copy.VmName} from tape";
        var drive = library.Drives.OrderBy(d => locks.Drive(d.Id).Snapshot().Holder is null ? 0 : 1).ThenBy(d => d.ElementAddress).First();
        var lease = await locks.Drive(drive.Id).AcquireAsync(holder, h => log.Info($"Waiting for {drive.Name}: '{h}' uses it."), cancellationToken).ConfigureAwait(false);
        // Blocks are never larger than the pool's block size; a larger buffer only costs transfer where no residual is reported.
        var blockSize = pool.BlockSize;
        var tapes = new TapeReaderSession(library, drive, media, libraries, robot, holder, log, requests, session.Id, set.Id, afterLoad);
        var newId = Guid.CreateVersion7();
        (long StartBlock, ulong FileId) FileOf(int sequence, int number) =>
            files.FirstOrDefault(f => f.Sequence == sequence && f.FileNumber == number) is { } f
                ? (f.StartBlock, f.FileId)
                : throw new InvalidDataException($"Tape #{sequence} file {number} is not in the set's catalog.");
        var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        // Reading is a task of its own: its progress is the session's until a VM restore adds the VM's task.
        var reading = await TaskProgress.BeginAsync(tracker, session.Id, copy.Id.ToString(), $"{copy.VmName} from tape", time, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await TapeSetReader.RestoreAsync(
                tapes,
                blockSize,
                cipher,
                copy.Sequence,
                copy.FileStartBlock,
                FileOf,
                transfer,
                newId,
                m => m with { Job = null, BaseRestorePointId = null },
                message => log.Info(message),
                reading.Report,
                cancellationToken).ConfigureAwait(false);
            await reading.FinishAsync(SessionState.Succeeded, null).ConfigureAwait(false);
            metrics.RecordTape(pool.Name, 0, copy.StoredBytes);
            var readSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(readStarted).TotalSeconds;
            log.Info($"{VmBackupTask.Format(copy.StoredBytes)} read in {MetricsText.Duration(readSeconds)} ({MetricsText.Rate(copy.StoredBytes / Math.Max(0.001, readSeconds))}); " +
                $"waiting for the drive took {MetricsText.Duration(tapes.Clock.Elapsed.TotalSeconds)} of it, the rest went to checking and storing the chunks.");
            if (request.Vm is { } vm)
            {
                // The drive is free again before the VM goes to the pool.
                var tapeCount = tapes.Loaded.Count;
                await tapes.DisposeAsync().ConfigureAwait(false);
                await lease.DisposeAsync().ConfigureAwait(false);
                log.Info($"{result.Chunks} chunk(s) read from {tapeCount} tape(s) into the cache; {drive.Name} is free again.");
                return await RestoreVmAsync(session, vm, copy, set, result.Committed, cache!, log, cancellationToken).ConfigureAwait(false);
            }

            await CatalogAsync(result.Committed, copy, set, targetId, session.Id, cancellationToken).ConfigureAwait(false);

            // The SQL Server backups of the VM from this restore point on: logs for a point-in-time restore.
            foreach (var sqlCopy in sqlCopies)
            {
                var (info, _) = await TapeSetReader.RestoreSqlAsync(
                    tapes, blockSize, cipher, sqlCopy.Sequence, sqlCopy.FileStartBlock, FileOf, transfer, Guid.CreateVersion7(), m => m with { Job = null }, cancellationToken).ConfigureAwait(false);
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await Sql.SqlBackupCatalog.RecordSqlBackupAsync(db, targetId, info, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (sqlCopies.Count > 0)
            {
                log.Info($"{sqlCopies.Count} SQL Server backup(s) of {copy.VmName} from {copy.CreatedUtc:yyyy-MM-dd HH:mm} to {sqlCopies[^1].CreatedUtc:yyyy-MM-dd HH:mm} UTC brought back with it.");
            }

            await sqlBackfill.RecordSnapshotFullsAsync(cancellationToken).ConfigureAwait(false);
            var summary = $"{copy.VmName} (restore point of {copy.CreatedUtc:yyyy-MM-dd HH:mm} UTC) is back in the repository as {newId}" +
                (sqlCopies.Count > 0 ? $", with {sqlCopies.Count} SQL Server backup(s)" : string.Empty) + $": {result.Chunks} chunk(s) of the restore point read from {tapes.Loaded.Count} tape(s).";
            log.Info(summary);
            return (log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
        }
        catch (Exception ex) when (!reading.Finished)
        {
            await reading.FinishAsync(cancellationToken.IsCancellationRequested ? SessionState.Stopped : SessionState.Failed, ex.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await tapes.DisposeAsync().ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
            if (cache is not null)
            {
                await cache.DisposeAsync().ConfigureAwait(false);
                try
                {
                    Directory.Delete(cacheDirectory!, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warning($"The tape cache {cacheDirectory} could not be deleted: {ex.Message}. It is deleted at the next start.");
                }
            }
        }
    }

    /// <summary>The VM restore of a copy read into <paramref name="cache"/>, as from a repository; its restore point exists only there.</summary>
    private async Task<(SessionState State, string Summary)> RestoreVmAsync(
        SessionEntity session, Contracts.RestoreRequestDto vm, TapeCopyEntity copy, MediaSetEntity set, ManifestInfo committed, LocalDirectoryRepository cache, WarningTrackingLog log, CancellationToken cancellationToken)
    {
        var manifest = committed.Manifest;
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = new RestorePointEntity
            {
                Id = committed.RestorePointId,
                RepositoryId = Guid.Empty,
                JobName = $"On tape (set of {set.CreatedUtc:yyyy-MM-dd})",
                SessionId = session.Id,
                ImportedFromTapeCopyId = copy.Id,
                ConnectionId = await db.Connections.Where(c => c.PlatformId == manifest.Source.PlatformId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
                PlatformId = manifest.Source.PlatformId,
                VmNativeId = manifest.Source.VmId,
                VmName = manifest.Vm.Name,
            };
        }

        VmBackupTask.ApplyManifest(point, manifest, null);
        var (state, summary) = await vmRestore.RestoreAsync(
            session,
            vm with { RestorePointId = point.Id },
            point,
            async token => new OpenedRestorePoint(await cache.OpenRestorePointAsync(point.Id, token).ConfigureAwait(false), null),
            cancellationToken).ConfigureAwait(false);
        return (state == SessionState.Succeeded && log.Warned ? SessionState.Warning : state, summary);
    }

    /// <summary>The set's key-encryption key, when it is encrypted (records by the server, or the tapes by the drive).</summary>
    private async Task<byte[]?> KekAsync(MediaSetEntity set, CancellationToken cancellationToken)
    {
        if (set.KeyFingerprint is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Fingerprint == set.KeyFingerprint, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The set is encrypted with key {set.KeyFingerprint}, which this server does not have: add it under Tape → Media pools → Keys (existing key, with its passphrase or key file).");
        return Convert.FromBase64String(protector.Unprotect(key.ProtectedKek));
    }

    private async Task CatalogAsync(ManifestInfo committed, TapeCopyEntity copy, MediaSetEntity set, Guid repositoryId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var manifest = committed.Manifest;
        var entity = new RestorePointEntity
        {
            Id = committed.RestorePointId,
            RepositoryId = repositoryId,
            JobId = null,
            JobName = $"Imported from tape (set of {set.CreatedUtc:yyyy-MM-dd})",
            SessionId = sessionId,
            ImportedFromTapeCopyId = copy.Id,
            ConnectionId = await db.Connections.Where(c => c.PlatformId == manifest.Source.PlatformId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            PlatformId = manifest.Source.PlatformId,
            VmNativeId = manifest.Source.VmId,
            VmName = manifest.Vm.Name,
        };
        VmBackupTask.ApplyManifest(entity, manifest, committed.Path);
        db.RestorePoints.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The tapes of a set for reading: loads the one asked for into the held drive and places it at a block.</summary>
internal sealed class TapeReaderSession(
    TapeLibraryEntity library,
    TapeDriveEntity drive,
    IReadOnlyList<TapeMediumEntity> media,
    TapeLibraries libraries,
    TapeSessionRunner robot,
    string holder,
    IOperationLog log,
    TapeOperatorRequests requests,
    Guid sessionId,
    Guid setId,
    Func<TapeDrive, CancellationToken, Task>? afterLoad = null) : ITapeVolumes, IAsyncDisposable
{
    private OpenedDrive? _drive;
    private int? _sequence;
    private bool _disposed;

    public HashSet<int> Loaded { get; } = [];

    /// <summary>Time spent waiting for the drive while reading.</summary>
    public TransferClock Clock { get; } = new();

    public async Task<DriveBlocks> AtAsync(int sequence, long block, CancellationToken cancellationToken)
    {
        if (_sequence != sequence)
        {
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
            var medium = media.First(m => m.Sequence == sequence);
            if (library.ChangerSerial is null)
            {
                // A standalone drive: the operator inserts the tape; a wrong one is ejected and named.
                _drive ??= await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
                var name = medium.LabelName ?? medium.Barcode;
                var message = $"Insert tape {name} (#{sequence} of the set) into {drive.Name} of {library.Name}.";
                log.Info($"Waiting for the operator: {message}");
                await requests.WaitAsync(sessionId, library.Id, drive.Id, message, async (say, token) =>
                {
                    if (await _drive.Drive.TestReadyAsync(token).ConfigureAwait(false) != DriveReadiness.Ready)
                    {
                        return false;
                    }

                    await _drive.Drive.PrepareAsync(token).ConfigureAwait(false);
                    var label = await TapeLabelReader.ReadAsync(_drive.Drive, token).ConfigureAwait(false);
                    if (label.Label?.Set is { } set && set.Id == setId && set.Sequence == sequence)
                    {
                        if (afterLoad is not null)
                        {
                            await afterLoad(_drive.Drive, token).ConfigureAwait(false);
                        }

                        return true;
                    }

                    say($"The cartridge inserted is {label.Label?.Name ?? (label.Blank ? "blank" : "not a backdafuckup tape")}, not tape {name}; it was ejected.");
                    await _drive.Drive.UnloadAsync(token).ConfigureAwait(false);
                    return false;
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await robot.LoadHeldAsync(library, drive, medium.Barcode, holder, log, cancellationToken).ConfigureAwait(false);
                _drive = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
                await _drive.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
                if (afterLoad is not null)
                {
                    await afterLoad(_drive.Drive, cancellationToken).ConfigureAwait(false);
                }
            }

            _sequence = sequence;
            Loaded.Add(sequence);
        }

        await _drive!.Drive.LocateBlockAsync(block, cancellationToken).ConfigureAwait(false);
        return new DriveBlocks(_drive.Drive, Clock);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await UnloadAsync(CancellationToken.None).ConfigureAwait(false);
        if (_drive is not null)
        {
            await _drive.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task UnloadAsync(CancellationToken cancellationToken)
    {
        if (_drive is null)
        {
            return;
        }

        if (library.ChangerSerial is null)
        {
            if (_sequence is not null)
            {
                await _drive.Drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
                log.Info($"Tape #{_sequence} ejected from {drive.Name}: take it out.");
            }

            _sequence = null;
            return;
        }

        await _drive.DisposeAsync().ConfigureAwait(false);
        _drive = null;
        _sequence = null;
        await robot.UnloadHeldAsync(library, drive, export: false, holder, log, cancellationToken).ConfigureAwait(false);
    }
}
