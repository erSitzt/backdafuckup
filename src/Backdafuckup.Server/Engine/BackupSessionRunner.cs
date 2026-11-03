using System.Globalization;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Sql;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>Runs a backup job session: every VM of the job (with retries), then retention.</summary>
public sealed class BackupSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectionManager connections,
    RepositoryManager repositories,
    SessionEventWriter events,
    SessionTracker tracker,
    SessionQueue queue,
    GuestProcessingService guestProcessing,
    TimeProvider time,
    ILogger<BackupSessionRunner> logger)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = events.CreateLog(session.Id, null);
        BackupJob job;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            job = await db.Jobs.Include(j => j.Objects).Include(j => j.Repository).Include(j => j.Connection).AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == session.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The job of this session no longer exists.");
        }

        if (job.Objects.Count == 0)
        {
            log.Warning("The job contains no VMs.");
            return (SessionState.Warning, "No VMs in job.");
        }

        log.Info($"Job started ({(session.RunMode == SessionRunMode.ActiveFull ? "active full" : "incremental")}), {job.Objects.Count} VM(s), repository {job.Repository.Name}.");
        var repository = await repositories.GetAsync(job.RepositoryId, cancellationToken).ConfigureAwait(false);
        await using var hypervisor = await connections.OpenAsync(job.ConnectionId, cancellationToken).ConfigureAwait(false);

        var tasks = new List<(BackupJobObject Vm, SessionTaskEntity Task)>();
        foreach (var vm in job.Objects.OrderBy(o => o.Order))
        {
            tasks.Add((vm, await tracker.AddTaskAsync(session.Id, vm.NativeId, vm.CachedName, cancellationToken).ConfigureAwait(false)));
        }

        var outcomes = new Dictionary<Guid, SessionState>();
        var pending = tasks;
        for (var attempt = 0; attempt <= job.RetryCount && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
            {
                log.Info($"Retrying {pending.Count} failed VM(s) in {job.RetryDelayMinutes} minute(s) (attempt {attempt + 1}).");
                await Task.Delay(TimeSpan.FromMinutes(job.RetryDelayMinutes), time, cancellationToken).ConfigureAwait(false);
            }

            using var gate = new SemaphoreSlim(Math.Max(1, job.MaxParallelVms));
            var results = await Task.WhenAll(pending.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var task = new VmBackupTask(dbFactory, events, tracker, guestProcessing, time, logger, job, session, item.Vm, item.Task.Id, attempt + 1, repository, hypervisor);
                    return (item, State: await task.RunAsync(cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            foreach (var (item, outcome) in results)
            {
                outcomes[item.Task.Id] = outcome;
            }

            pending = results.Where(r => r.State == SessionState.Failed).Select(r => r.item).ToList();
            await tracker.RollUpAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await ApplyRetentionAsync(job, repository, hypervisor, log, cancellationToken).ConfigureAwait(false);
        }

        var failed = outcomes.Values.Count(s => s == SessionState.Failed);
        var warnings = outcomes.Values.Count(s => s == SessionState.Warning);
        var succeeded = outcomes.Count - failed - warnings;
        var state = failed > 0 ? SessionState.Failed : warnings > 0 ? SessionState.Warning : SessionState.Succeeded;
        return (state, $"{succeeded} succeeded, {warnings} warning(s), {failed} failed.");
    }

    /// <summary>Keeps the newest N restore points per VM of the job; deletes older ones and queues garbage collection.</summary>
    private async Task ApplyRetentionAsync(BackupJob job, IBackupRepository repository, IHypervisorSession hypervisor, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var points = await db.RestorePoints.Include(r => r.Disks)
            .Where(r => r.JobId == job.Id && r.RepositoryId == job.RepositoryId && r.State == RestorePointState.Available)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var deleted = 0;
        foreach (var group in points.GroupBy(p => p.VmNativeId))
        {
            foreach (var old in group.OrderByDescending(p => p.CreatedUtc).Skip(Math.Max(1, job.RetentionPoints)))
            {
                try
                {
                    old.State = RestorePointState.Deleting;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await repository.DeleteRestorePointAsync(old.Id, cancellationToken).ConfigureAwait(false);
                    var tokens = old.Disks.Where(d => d.ChangeTokenValue is not null).Select(d => new ChangeToken(d.ChangeTokenKind!, d.ChangeTokenValue!)).ToList();
                    if (tokens.Count > 0)
                    {
                        await hypervisor.ReleaseChangeTokensAsync(tokens, log, cancellationToken).ConfigureAwait(false);
                    }

                    db.RestorePoints.Remove(old);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    deleted++;
                    log.Info($"Retention: removed restore point of {old.VmName} from {old.CreatedUtc.ToLocalTime():g}.");
                }
                catch (RestorePointPinnedException)
                {
                    old.State = RestorePointState.Available;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    log.Info($"Retention: restore point of {old.VmName} from {old.CreatedUtc.ToLocalTime():g} is in use and will be removed later.");
                }
            }
        }

        if (deleted > 0)
        {
            await queue.EnqueueMaintenanceAsync(job.RepositoryId, job.Repository.Name, "Retention", cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Backs up one VM: snapshot, per-disk full or CBT-incremental read into the repository, commit.</summary>
internal sealed class VmBackupTask(
    IDbContextFactory<AppDbContext> dbFactory,
    SessionEventWriter events,
    SessionTracker tracker,
    GuestProcessingService guestProcessing,
    TimeProvider time,
    ILogger logger,
    BackupJob job,
    SessionEntity session,
    BackupJobObject vm,
    Guid taskId,
    int attempt,
    IBackupRepository repository,
    IHypervisorSession hypervisor)
{
    private readonly IOperationLog _log = events.CreateLog(session.Id, taskId, vm.CachedName);
    private long _readBytes;
    private long _storedBytes;
    private long _totalBytes = 1;
    private DateTimeOffset? _transferStarted;
    private DateTimeOffset? _transferEnded;
    private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;
    private readonly Lock _metricsLock = new();
    private readonly RateMeter _rate = new();
    private List<DiskMetricsDto> _diskMetrics = [];

    public async Task<SessionState> RunAsync(CancellationToken cancellationToken)
    {
        var restorePointId = Guid.CreateVersion7();
        await tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = SessionState.Running;
            t.Attempt = attempt;
            t.StartedUtc = time.GetUtcNow();
            t.Error = null;
            t.Progress = 0;
        }, CancellationToken.None).ConfigureAwait(false);

        IRestorePointPin? basePin = null;
        IRestorePointReader? baseReader = null;
        var restorePointCreated = false;
        try
        {
            var baseline = session.RunMode == SessionRunMode.ActiveFull ? null : await FindBaseAsync(cancellationToken).ConfigureAwait(false);
            if (baseline is not null)
            {
                try
                {
                    basePin = repository.Pin(baseline.Id);
                    baseReader = await repository.OpenRestorePointAsync(baseline.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    _log.Warning("The previous restore point is missing in the repository; a full backup is made.");
                    baseline = null;
                }
            }

            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                db.RestorePoints.Add(new RestorePointEntity
                {
                    Id = restorePointId,
                    RepositoryId = job.RepositoryId,
                    JobId = job.Id,
                    JobName = job.Name,
                    SessionId = session.Id,
                    ConnectionId = job.ConnectionId,
                    PlatformId = hypervisor.Platform.PlatformId,
                    VmNativeId = vm.NativeId,
                    VmName = vm.CachedName,
                    CreatedUtc = time.GetUtcNow(),
                    State = RestorePointState.Creating,
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                restorePointCreated = true;
            }

            var guest = await PrepareGuestAsync(cancellationToken).ConfigureAwait(false);
            var ledger = new DbArtifactLedger(dbFactory, job.ConnectionId, session.Id, restorePointId, time);
            await using var writer = repository.BeginWrite(restorePointId, vm.NativeId, basePin);
            var snapshotName = $"{vm.CachedName} backdafuckup {time.GetUtcNow().ToLocalTime():yyyy-MM-dd HH:mm}";
            await using var backup = await hypervisor.BeginBackupAsync(
                new VmBackupRequest(vm.NativeId, job.Id, session.Id, restorePointId, snapshotName, guest?.Quiescer), _log, ledger, cancellationToken).ConfigureAwait(false);
            var (consistency, consistencyReason) = CheckConsistency(guest, backup.GuestFreeze);

            _totalBytes = Math.Max(1, backup.Disks.Sum(d => d.CapacityBytes));
            lock (_metricsLock)
            {
                _diskMetrics = backup.Disks.Select(d => new DiskMetricsDto
                {
                    Key = d.Key,
                    Name = d.Name,
                    CapacityBytes = d.CapacityBytes,
                    Metrics = new TransferMetricsDto { TotalBytes = d.CapacityBytes },
                }).ToList();
            }

            await tracker.UpdateTaskAsync(taskId, t => t.Metrics = CurrentMetrics(), CancellationToken.None).ConfigureAwait(false);
            var (manifestDisks, warnings) = await BackupDisksAsync(backup, writer, baseline, baseReader, cancellationToken).ConfigureAwait(false);
            warnings |= consistencyReason is not null;
            ManifestBlob? guestBlob = null;
            ManifestBlob? vssBlob = null;
            int? sqlDatabases = null;
            if (guest?.Quiescer is { Prepared: { } prepared } quiescer)
            {
                var stored = StoredGuestMetadata.Build(prepared, quiescer.Thawed, consistency, consistencyReason);
                sqlDatabases = stored.SqlDatabases.Count;
                guestBlob = await writer.WriteBlobAsync("guest-metadata", StoredGuestMetadata.FormatV2, new MemoryStream(stored.Serialize()), cancellationToken).ConfigureAwait(false);
                if (StoredVssDocuments.From(prepared, quiescer.Thawed) is { } documents)
                {
                    vssBlob = await writer.WriteBlobAsync("vss-documents", StoredVssDocuments.Format, new MemoryStream(documents.Serialize()), cancellationToken).ConfigureAwait(false);
                }

                foreach (var database in stored.SqlDatabases.Where(d => d.Consistency != "application" && consistency == "application"))
                {
                    _log.Warning($"SQL database {database.Instance}\\{database.Database} is only crash-consistent: {database.ConsistencyReason}.");
                    warnings = true;
                }
            }

            var metadata = await writer.WriteBlobAsync("vm-metadata", backup.Metadata.Format, new MemoryStream(backup.Metadata.Content), cancellationToken).ConfigureAwait(false);
            var kind = manifestDisks.All(d => d.Stats.Mode == DiskBackupMode.Cbt) && manifestDisks.Count > 0 ? RestorePointKind.Incremental : RestorePointKind.Full;
            var manifest = new RestorePointManifest
            {
                RestorePointId = restorePointId,
                RepositoryId = repository.Info.RepositoryId,
                CreatedUtc = time.GetUtcNow(),
                Kind = kind,
                BaseRestorePointId = baseline?.Id,
                Job = new ManifestJob(job.Id, job.Name),
                Source = new ManifestSource(job.Connection.Kind, hypervisor.Platform.PlatformId, vm.NativeId, backup.Vm.Name, backup.SnapshotId),
                Vm = backup.Vm,
                Metadata = metadata,
                Consistency = consistency,
                ConsistencyReason = consistencyReason,
                Guest = guestBlob,
                SqlDatabaseCount = guest?.Quiescer is { Prepared: not null } ? sqlDatabases : 0,
                VssDocuments = vssBlob,
                Chunking = new ManifestChunking(repository.Info.ChunkSize, repository.Info.Hash, repository.Info.Compression),
                Disks = manifestDisks,
            };
            var committed = await writer.CommitAsync(manifest, cancellationToken).ConfigureAwait(false);

            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var entity = await db.RestorePoints.FirstAsync(r => r.Id == restorePointId, CancellationToken.None).ConfigureAwait(false);
                ApplyManifest(entity, manifest, committed.Path);
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await backup.CommitAsync(_log, CancellationToken.None).ConfigureAwait(false);
                await ReleaseBaseTokensAsync(baseline, manifestDisks).ConfigureAwait(false);
            }
            catch (SnapshotCleanupDeferredException ex)
            {
                warnings = true;
                _log.Warning(ex.Message);
                await ReleaseBaseTokensAsync(baseline, manifestDisks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings = true;
                _log.Warning(
                    $"Reducing the snapshot to change tracking data failed ({ex.Message}). The restore point is complete; the snapshot is removed, " +
                    "so the next backup of this VM reads its disks in full.");
            }

            _log.Info($"Backup finished ({kind}): {MetricsText.Describe(CurrentMetrics())}.", "summary");
            await FinishTaskAsync(warnings ? SessionState.Warning : SessionState.Succeeded, null, restorePointId).ConfigureAwait(false);
            return warnings ? SessionState.Warning : SessionState.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkRunningDisks("Failed");
            _log.Warning("Stopped by user.");
            await RemoveRestorePointAsync(restorePointId, restorePointCreated).ConfigureAwait(false);
            await FinishTaskAsync(SessionState.Stopped, "Stopped", null).ConfigureAwait(false);
            return SessionState.Stopped;
        }
        catch (Exception ex)
        {
            MarkRunningDisks("Failed");
            logger.LogWarning(ex, "Backup of VM {Vm} failed", vm.CachedName);
            _log.Error($"Backup failed: {ex.Message}");
            await RemoveRestorePointAsync(restorePointId, restorePointCreated).ConfigureAwait(false);
            await FinishTaskAsync(SessionState.Failed, ex.Message, null).ConfigureAwait(false);
            return SessionState.Failed;
        }
        finally
        {
            if (baseReader is not null)
            {
                await baseReader.DisposeAsync().ConfigureAwait(false);
            }

            basePin?.Dispose();
        }
    }


    /// <summary>
    /// Guest processing before the snapshot: the agent of the VM (installed first if needed), or the reason why the backup
    /// will be crash-consistent. With the policy "fail the VM" a missing agent fails the VM before any snapshot.
    /// </summary>
    private async Task<GuestPreparation?> PrepareGuestAsync(CancellationToken cancellationToken)
    {
        if (!job.GuestProcessing)
        {
            return null;
        }

        var guest = await guestProcessing.PrepareAsync(job, vm, _log, cancellationToken).ConfigureAwait(false);
        if (guest.Quiescer is null)
        {
            if (job.GuestFailurePolicy == GuestFailurePolicy.FailVm)
            {
                throw new InvalidOperationException($"Guest processing: {guest.Reason}");
            }

            // Reported before the snapshot, so the log reads in the order things happen.
            _log.Warning($"Guest processing: {guest.Reason} The VM is backed up crash-consistently.");
        }

        return guest;
    }

    /// <summary>"application" or "crash" plus the reason; with the policy "fail the VM" a failed freeze fails the VM.</summary>
    private (string Consistency, string? Reason) CheckConsistency(GuestPreparation? guest, GuestFreezeResult? freeze)
    {
        if (guest is null)
        {
            return ("crash", null);
        }

        if (freeze is { ApplicationConsistent: true })
        {
            return ("application", null);
        }

        var reason = freeze?.Reason ?? guest.Reason ?? "the guest could not be frozen";
        if (job.GuestFailurePolicy == GuestFailurePolicy.FailVm)
        {
            throw new InvalidOperationException($"Guest processing: the snapshot is not application-consistent: {reason}");
        }

        // A missing agent was reported before the snapshot, a failed freeze by the hypervisor provider.
        return ("crash", reason);
    }

    /// <summary>
    /// Backs up the disks of the VM, up to <see cref="BackupJob.MaxParallelDisks"/> at a time. If one disk fails, the
    /// others are stopped and that disk's error is reported (not the resulting cancellation of the others).
    /// </summary>
    private async Task<(List<ManifestDisk> Disks, bool Warnings)> BackupDisksAsync(
        IVmBackupSession backup,
        IRepositoryWriter writer,
        RestorePointEntity? baseline,
        IRestorePointReader? baseReader,
        CancellationToken cancellationToken)
    {
        var parallel = Math.Clamp(job.MaxParallelDisks, 1, 8);
        if (parallel > 1 && backup.Disks.Count > 1)
        {
            _log.Info($"Processing up to {Math.Min(parallel, backup.Disks.Count)} of {backup.Disks.Count} disks in parallel.");
        }

        var results = new (ManifestDisk Disk, bool Warned)[backup.Disks.Count];
        using var gate = new SemaphoreSlim(parallel);
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = backup.Disks.Select(async (disk, index) =>
        {
            await gate.WaitAsync(failed.Token).ConfigureAwait(false);
            try
            {
                results[index] = await BackupDiskAsync(backup, writer, disk, baseline, baseReader, failed.Token).ConfigureAwait(false);
            }
            catch
            {
                await failed.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            var cause = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).FirstOrDefault(e => e is not OperationCanceledException);
            if (cause is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
            }

            throw;
        }
        finally
        {
            lock (_metricsLock)
            {
                _transferEnded = time.GetUtcNow();
            }
        }

        return (results.Select(r => r.Disk).ToList(), results.Any(r => r.Warned));
    }

    private async Task<(ManifestDisk Disk, bool Warned)> BackupDiskAsync(
        IVmBackupSession backup,
        IRepositoryWriter writer,
        SnapshotDisk disk,
        RestorePointEntity? baseline,
        IRestorePointReader? baseReader,
        CancellationToken cancellationToken)
    {
        var warned = false;
        var baseDisk = baseline?.Disks.FirstOrDefault(d => d.SourceDiskId == disk.SourceDiskId);
        ChangeToken? since = baseDisk is { ChangeTokenValue: not null } ? new ChangeToken(baseDisk.ChangeTokenKind!, baseDisk.ChangeTokenValue) : null;
        var changes = await backup.GetChangedExtentsAsync(disk, since, _log, cancellationToken).ConfigureAwait(false);

        ChunkIndex? baseIndex = null;
        if (!changes.IsFull && baseDisk is not null && baseReader is not null)
        {
            if (baseDisk.CapacityBytes != disk.CapacityBytes)
            {
                changes = ChangedExtents.Full("the disk size changed");
            }
            else
            {
                try
                {
                    baseIndex = await baseReader.ReadDiskIndexAsync(baseDisk.DiskKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException)
                {
                    changes = ChangedExtents.Full($"the index of the previous restore point is unreadable ({ex.Message})");
                }
            }
        }

        if (changes.IsFull && baseline is not null && session.RunMode != SessionRunMode.ActiveFull)
        {
            _log.Warning($"Disk {disk.Name}: full backup because {changes.FullReason}.");
            warned = !string.IsNullOrEmpty(changes.FullReason) && !changes.FullReason.Contains("disabled until", StringComparison.Ordinal);
        }
        else if (changes.IsFull)
        {
            _log.Info($"Disk {disk.Name}: full backup ({changes.FullReason}).");
        }

        var key = $"disk-{disk.Key}";
        lock (_metricsLock)
        {
            _transferStarted ??= time.GetUtcNow();
        }

        UpdateDisk(disk.Key, d => d with { State = "Running", Mode = changes.IsFull ? nameof(DiskBackupMode.Full) : nameof(DiskBackupMode.Cbt), FullReason = changes.FullReason });
        await using var reader = await backup.OpenReaderAsync(disk, new DiskReaderOptions(Math.Clamp(job.NbdConnectionsPerDisk, 1, 16), 4, job.TransportMode), cancellationToken).ConfigureAwait(false);
        UpdateDisk(disk.Key, d => d with { Transport = reader.TransportName });
        var result = await DiskBackupPipeline.RunAsync(
            new DiskBackupJob
            {
                Reader = reader,
                Writer = writer,
                ChunkSize = repository.Info.ChunkSize,
                Changes = changes,
                BaseIndex = baseIndex,
                Parallelism = 8,
                Progress = new Progress(this, disk, key),
            },
            cancellationToken).ConfigureAwait(false);

        Interlocked.Add(ref _readBytes, result.Stats.ReadBytes);
        Interlocked.Add(ref _storedBytes, result.Stats.NewStoredBytes);
        var diskMetrics = ToMetrics(result.Counters);
        UpdateDisk(disk.Key, d => d with { State = "Done", Mode = result.Stats.Mode.ToString(), FullReason = result.Stats.FullReason, Transport = result.Stats.Transport, Metrics = diskMetrics });
        _log.Info($"Disk {disk.Name}: {result.Stats.Mode} via {result.Stats.Transport}: {MetricsText.Describe(diskMetrics)}.", key);

        var indexRef = await writer.WriteDiskIndexAsync(disk.Key, result.Index, cancellationToken).ConfigureAwait(false);
        return (new ManifestDisk
        {
            Key = disk.Key,
            SourceDiskId = disk.SourceDiskId,
            Name = disk.Name,
            CapacityBytes = disk.CapacityBytes,
            Bootable = disk.Bootable,
            StorageId = disk.StorageId,
            StorageName = disk.StorageName,
            Index = indexRef,
            ChangeToken = backup.GetNewChangeToken(disk),
            Stats = result.Stats,
        }, warned);
    }

    private async Task<RestorePointEntity?> FindBaseAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.RestorePoints.Include(r => r.Disks).AsNoTracking()
            .Where(r => r.JobId == job.Id && r.RepositoryId == job.RepositoryId && r.VmNativeId == vm.NativeId && r.State == RestorePointState.Available)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The previous restore point's change tokens are superseded by the new ones and can be released.</summary>
    private async Task ReleaseBaseTokensAsync(RestorePointEntity? baseline, List<ManifestDisk> disks)
    {
        if (baseline is null)
        {
            return;
        }

        var superseded = baseline.Disks.Where(d => d.ChangeTokenValue is not null && disks.All(n => n.ChangeToken?.Value != d.ChangeTokenValue)).ToList();
        if (superseded.Count == 0)
        {
            return;
        }

        await hypervisor.ReleaseChangeTokensAsync(superseded.Select(d => new ChangeToken(d.ChangeTokenKind!, d.ChangeTokenValue!)), _log, CancellationToken.None).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        var ids = superseded.Select(d => d.Id).ToList();
        await db.RestorePointDisks.Where(d => ids.Contains(d.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChangeTokenValue, (string?)null).SetProperty(d => d.ChangeTokenKind, (string?)null), CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static void ApplyManifest(RestorePointEntity entity, RestorePointManifest manifest, string? manifestPath)
    {
        entity.State = RestorePointState.Available;
        entity.Kind = manifest.Kind;
        entity.CreatedUtc = manifest.CreatedUtc;
        entity.VmName = manifest.Vm.Name;
        entity.Vm = manifest.Vm;
        entity.ManifestPath = manifestPath;
        entity.Consistency = manifest.Consistency;
        entity.ConsistencyReason = manifest.ConsistencyReason;
        entity.SqlDatabaseCount = manifest.SqlDatabaseCount ?? (manifest.Guest is null ? 0 : null);
        entity.LogicalBytes = manifest.Disks.Sum(d => d.CapacityBytes);
        entity.NewStoredBytes = manifest.Disks.Sum(d => d.Stats.NewStoredBytes);
        entity.Disks = manifest.Disks.Select(d => new RestorePointDisk
        {
            RestorePointId = entity.Id,
            DiskKey = d.Key,
            SourceDiskId = d.SourceDiskId,
            Name = d.Name,
            CapacityBytes = d.CapacityBytes,
            StorageId = d.StorageId,
            StorageName = d.StorageName,
            Mode = d.Stats.Mode,
            FullReason = d.Stats.FullReason,
            ChangeTokenKind = d.ChangeToken?.Kind,
            ChangeTokenValue = d.ChangeToken?.Value,
            ReadBytes = d.Stats.ReadBytes,
            NewStoredBytes = d.Stats.NewStoredBytes,
        }).ToList();
    }

    private async Task RemoveRestorePointAsync(Guid restorePointId, bool created)
    {
        if (!created)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
        await db.RestorePoints.Where(r => r.Id == restorePointId && r.State == RestorePointState.Creating).ExecuteDeleteAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Task FinishTaskAsync(SessionState state, string? error, Guid? restorePointId) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.EndedUtc = time.GetUtcNow();
            t.Error = error;
            t.RestorePointId = restorePointId;
            t.ReadBytes = Interlocked.Read(ref _readBytes);
            t.TransferredBytes = Interlocked.Read(ref _storedBytes);
            t.Metrics = CurrentMetrics() with { CurrentBytesPerSecond = 0 };
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                t.Progress = 100;
            }
        }, CancellationToken.None);

    private void ReportProgress(SnapshotDisk disk, string key, DiskBackupProgress progress)
    {
        var now = time.GetUtcNow();
        var fraction = progress.TotalChunks == 0 ? 1 : progress.ProcessedChunks / (double)progress.TotalChunks;
        var counters = progress.Counters;
        UpdateDisk(disk.Key, d => d with { State = "Running", Metrics = ToMetrics(counters) });
        _log.Info(
            $"Disk {disk.Name}: {fraction:P0} · read {Format(counters.ReadBytes)} at {MetricsText.Rate(counters.Elapsed.TotalSeconds > 0 ? counters.ReadBytes / counters.Elapsed.TotalSeconds : 0)} · stored {Format(counters.StoredBytes)}",
            key);
        lock (_metricsLock)
        {
            if (now - _lastProgress < TimeSpan.FromSeconds(2) && progress.ProcessedChunks != progress.TotalChunks)
            {
                return;
            }

            _lastProgress = now;
        }

        _ = UpdateProgressAsync();
    }

    /// <summary>
    /// Statistics of the whole VM: the disks combined, rates over the wall time of the disk transfers (disks may run in
    /// parallel), plus the current read rate.
    /// </summary>
    private TransferMetricsDto CurrentMetrics()
    {
        lock (_metricsLock)
        {
            var disks = _diskMetrics.ToList();
            var seconds = _transferStarted is { } started ? ((_transferEnded ?? time.GetUtcNow()) - started).TotalSeconds : 0;
            var combined = TransferMetricsDto.Combine(disks.Select(d => d.Metrics), seconds);
            return combined with { Disks = disks, CurrentBytesPerSecond = _rate.Current };
        }
    }

    private void UpdateDisk(string key, Func<DiskMetricsDto, DiskMetricsDto> update)
    {
        lock (_metricsLock)
        {
            var index = _diskMetrics.FindIndex(d => d.Key == key);
            if (index >= 0)
            {
                _diskMetrics[index] = update(_diskMetrics[index]);
            }
        }
    }

    private void MarkRunningDisks(string state)
    {
        lock (_metricsLock)
        {
            _diskMetrics = _diskMetrics.Select(d => d.State == "Running" ? d with { State = state } : d).ToList();
        }
    }

    private static TransferMetricsDto ToMetrics(DiskBackupCounters c) => new()
    {
        TotalBytes = c.CapacityBytes,
        ProcessedBytes = c.ProcessedBytes,
        ReadBytes = c.ReadBytes,
        UnchangedBytes = c.UnchangedBytes,
        ZeroBytes = c.ZeroBytes,
        DedupedBytes = c.DedupedBytes,
        NewDataBytes = c.NewDataBytes,
        StoredBytes = c.StoredBytes,
        Seconds = Math.Round(c.Elapsed.TotalSeconds, 3),
        SourceLoad = c.SourceLoad,
        ProcessingLoad = c.ProcessingLoad,
        TargetLoad = c.TargetLoad,
    };

    private async Task UpdateProgressAsync()
    {
        try
        {
            var metrics = CurrentMetrics();
            _rate.Sample(time.GetUtcNow(), metrics.ReadBytes);
            metrics = metrics with { CurrentBytesPerSecond = _rate.Current };
            var overall = Math.Min(99.0, metrics.ProcessedBytes * 100.0 / _totalBytes);
            await tracker.UpdateTaskAsync(taskId, t =>
            {
                t.Progress = Math.Round(overall, 1);
                t.ReadBytes = metrics.ReadBytes;
                t.TransferredBytes = metrics.StoredBytes;
                t.Metrics = metrics;
            }).ConfigureAwait(false);
            await tracker.RollUpAsync(session.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            logger.LogDebug(ex, "Progress update failed");
        }
    }

    internal static string Format(long bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("0.0 TB", CultureInfo.InvariantCulture),
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0 GB", CultureInfo.InvariantCulture),
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0 MB", CultureInfo.InvariantCulture),
        _ => (bytes / 1024.0).ToString("0.0 KB", CultureInfo.InvariantCulture),
    };

    private sealed class Progress(VmBackupTask owner, SnapshotDisk disk, string key) : IProgress<DiskBackupProgress>
    {
        public void Report(DiskBackupProgress value) => owner.ReportProgress(disk, key, value);
    }
}
