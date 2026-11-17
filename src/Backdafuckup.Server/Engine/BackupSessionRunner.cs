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
    Telemetry.BackupMetrics metrics,
    Microsoft.Extensions.Options.IOptions<EngineOptions> engineOptions,
    TimeProvider time,
    ILogger<BackupSessionRunner> logger,
    ScaleOut.RepositoryPlacement placement,
    ScaleOut.ChainLocks chainLocks)
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

        var objects = job.Connection.Kind == HypervisorKind.Kubernetes ? "namespace(s)" : "VM(s)";
        if (job.Objects.Count == 0)
        {
            log.Warning($"The job contains no {objects.Replace("(s)", "s", StringComparison.Ordinal)}.");
            return (SessionState.Warning, $"No {objects.Replace("(s)", "s", StringComparison.Ordinal)} in job.");
        }

        // A standalone repository, or a scale-out one whose extents each chain is placed on.
        var scope = await placement.ScopeAsync(job.RepositoryId, cancellationToken).ConfigureAwait(false);
        if (scope.IsScaleOut && scope.Extents.Count == 0)
        {
            throw new InvalidOperationException($"The scale-out repository {scope.Repository.Name} has no extents.");
        }

        if (!scope.IsScaleOut)
        {
            _ = await repositories.GetAsync(scope.Repository.Id, cancellationToken).ConfigureAwait(false); // fails early when it cannot be opened
        }

        await using var hypervisor = await connections.OpenAsync(job.ConnectionId, cancellationToken).ConfigureAwait(false);

        // A cluster's platforms, recognised at every run: they decide what is left out as made by them, and are recorded
        // with each restore point. The connection keeps them for the console.
        if (hypervisor is Backdafuckup.Kubernetes.IKubernetesPlatforms platforms)
        {
            var selection = await platforms.GetPlatformsAsync(cancellationToken).ConfigureAwait(false);
            log.Info(selection.Describe());
            var detected = selection.Detected.ToList();
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.Connections.Where(c => c.Id == job.ConnectionId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.DetectedPlatforms, detected)
                .SetProperty(c => c.ProductName, selection.Set.Display), cancellationToken).ConfigureAwait(false);
        }

        // Namespaces chosen by their labels: those that have them now, next to the ones chosen by name.
        var (members, selections) = await ExpandSelectorsAsync(job, hypervisor, cancellationToken).ConfigureAwait(false);
        log.Info($"Job started ({(session.RunMode == SessionRunMode.ActiveFull ? "active full" : "incremental")}), {members.Count} {objects}, repository {scope.Repository.Name}" +
            (scope.IsScaleOut ? $" (scale-out: {string.Join(", ", scope.Extents.Select(e => e.Name))})." : "."));
        foreach (var (selector, names, whole) in selections)
        {
            if (whole)
            {
                log.Info($"{selector}: {names.Count} namespace(s) ({string.Join(", ", names)}) and the cluster-scoped objects.");
            }
            else if (names.Count == 0)
            {
                log.Warning($"No namespace has the labels {selector} now.");
            }
            else
            {
                log.Info($"Namespaces with the labels {selector}: {string.Join(", ", names)}.");
            }
        }

        if (members.Count == 0)
        {
            return (SessionState.Warning, "No namespace has the labels of the job now.");
        }

        var tasks = new List<(BackupJobObject Vm, SessionTaskEntity Task)>();
        foreach (var vm in members)
        {
            tasks.Add((vm, await tracker.AddTaskAsync(session.Id, vm.NativeId, vm.CachedName, cancellationToken).ConfigureAwait(false)));
        }

        var outcomes = new Dictionary<Guid, SessionState>();
        var pending = tasks;
        for (var attempt = 0; attempt <= job.RetryCount && pending.Count > 0; attempt++)
        {
            if (attempt > 0)
            {
                log.Info($"Retrying {pending.Count} failed {objects} in {job.RetryDelayMinutes} minute(s) (attempt {attempt + 1}).");
                await Task.Delay(TimeSpan.FromMinutes(job.RetryDelayMinutes), time, cancellationToken).ConfigureAwait(false);
            }

            using var gate = new SemaphoreSlim(Math.Max(1, job.MaxParallelVms));
            var results = await Task.WhenAll(pending.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var task = new VmBackupTask(dbFactory, events, tracker, guestProcessing, metrics, engineOptions.Value.CheckpointWithVss, time, logger, job, session, item.Vm, item.Task.Id, attempt + 1,
                        new ChainTargets(scope, placement, chainLocks, repositories), hypervisor);
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
            await ApplyRetentionAsync(job, scope, hypervisor, log, cancellationToken).ConfigureAwait(false);
        }

        var failed = outcomes.Values.Count(s => s == SessionState.Failed);
        var warnings = outcomes.Values.Count(s => s == SessionState.Warning);
        var succeeded = outcomes.Count - failed - warnings;
        var state = failed > 0 ? SessionState.Failed : warnings > 0 ? SessionState.Warning : SessionState.Succeeded;
        return (state, $"{succeeded} succeeded, {warnings} warning(s), {failed} failed.");
    }

    /// <summary>
    /// The job's members for this run: its VMs or namespaces by name, plus the namespaces whose labels match its label
    /// selectors now (each once); and per selector the namespaces it matched. A whole-cluster object stands for every
    /// namespace but its excludes, and the cluster-scoped objects (<see cref="KubernetesClusterScope.Member"/>, first).
    /// </summary>
    private static async Task<(List<BackupJobObject> Members, List<(string Selector, IReadOnlyList<string> Names, bool Whole)> Selections)> ExpandSelectorsAsync(
        BackupJob job, IHypervisorSession hypervisor, CancellationToken cancellationToken)
    {
        static bool Expands(string id) => KubernetesLabelSelector.IsJobObject(id) || KubernetesClusterScope.IsJobObject(id);
        var members = job.Objects.Where(o => !Expands(o.NativeId)).OrderBy(o => o.Order).ToList();
        var selections = new List<(string Selector, IReadOnlyList<string> Names, bool Whole)>();
        var selectors = job.Objects.Where(o => Expands(o.NativeId)).OrderBy(o => o.Order).ToList();
        if (selectors.Count == 0)
        {
            return (members, selections);
        }

        if (hypervisor is not Kubernetes.IKubernetesNamespaceSelector cluster)
        {
            throw new InvalidOperationException("Only namespaces of Kubernetes clusters are chosen by labels or as a whole cluster.");
        }

        var order = members.Count == 0 ? 0 : members.Max(m => m.Order) + 1;
        foreach (var selector in selectors)
        {
            string text;
            IReadOnlyList<string> names;
            if (KubernetesClusterScope.IsJobObject(selector.NativeId))
            {
                // Every namespace but the excluded ones; @platform: those the cluster's platforms make, as detected now.
                var excludes = KubernetesClusterScope.Excludes(selector.NativeId);
                var all = await cluster.SelectNamespacesAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                text = KubernetesClusterScope.Describe(excludes);
                if (excludes.Contains(KubernetesClusterScope.PlatformToken))
                {
                    var platform = hypervisor is Backdafuckup.Kubernetes.IKubernetesPlatforms platforms
                        ? (await platforms.GetPlatformsAsync(cancellationToken).ConfigureAwait(false)).Set
                        : Backdafuckup.Kubernetes.KubernetesPlatformSet.Base;
                    var patterns = platform.NamespacePatterns;
                    var byPlatform = all
                        .Select(n => (Namespace: n, Profile: patterns.FirstOrDefault(p => KubernetesClusterScope.IsExcluded(n, [p.Pattern]) && n != "backdafuckup-system").Profile))
                        .Where(n => n.Profile is not null)
                        .GroupBy(n => n.Profile!)
                        .OrderBy(g => platform.Profiles.ToList().IndexOf(g.Key))
                        .Select(g => $"{g.Key.Name}: {string.Join(", ", g.Select(n => n.Namespace))}")
                        .ToList();
                    text += byPlatform.Count > 0 ? $" ({string.Join("; ", byPlatform)})" : " (none here)";
                    excludes = KubernetesClusterScope.Expand(excludes, patterns.Select(p => p.Pattern));
                }

                names = [.. all.Where(n => !KubernetesClusterScope.IsExcluded(n, excludes))];
                if (members.All(m => m.NativeId != KubernetesClusterScope.Member))
                {
                    members.Insert(0, new BackupJobObject
                    {
                        JobId = job.Id, NativeId = KubernetesClusterScope.Member, CachedName = "Cluster-scoped objects", Order = members.Count == 0 ? 0 : members.Min(m => m.Order) - 1,
                    });
                }
            }
            else
            {
                text = KubernetesLabelSelector.Of(selector.NativeId);
                names = await cluster.SelectNamespacesAsync(text, cancellationToken).ConfigureAwait(false);
            }

            selections.Add((text, names, KubernetesClusterScope.IsJobObject(selector.NativeId)));
            foreach (var name in names.Where(n => members.All(m => m.NativeId != n)))
            {
                members.Add(new BackupJobObject { JobId = job.Id, NativeId = name, CachedName = name, Order = order++ });
            }
        }

        return (members, selections);
    }

    /// <summary>Keeps the newest N restore points per VM of the job; deletes older ones and queues garbage collection.</summary>
    private async Task ApplyRetentionAsync(BackupJob job, ScaleOut.RepositoryScope scope, IHypervisorSession hypervisor, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var repositoryIds = scope.RepositoryIds.ToList();
        var points = await db.RestorePoints.Include(r => r.Disks)
            .Where(r => r.JobId == job.Id && repositoryIds.Contains(r.RepositoryId) && r.State == RestorePointState.Available)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var touched = new HashSet<Guid>();
        foreach (var group in points.GroupBy(p => p.VmNativeId))
        {
            foreach (var old in group.OrderByDescending(p => p.CreatedUtc).Skip(Math.Max(1, job.RetentionPoints)))
            {
                // Marked first (compare-and-set): a scale-out move cannot switch it meanwhile, and its repository is fixed.
                if (await CatalogDeletes.MarkRestorePointAsync(db, old.Id, cancellationToken).ConfigureAwait(false) is not { } repositoryId)
                {
                    continue; // moved or deleted meanwhile: the next run decides again
                }

                try
                {
                    var holder = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
                    await holder.DeleteRestorePointAsync(old.Id, cancellationToken).ConfigureAwait(false);
                    var tokens = old.Disks.Where(d => d.ChangeTokenValue is not null).Select(d => new ChangeToken(d.ChangeTokenKind!, d.ChangeTokenValue!)).ToList();
                    if (tokens.Count > 0)
                    {
                        await hypervisor.ReleaseChangeTokensAsync(tokens, log, cancellationToken).ConfigureAwait(false);
                    }

                    db.RestorePoints.Remove(old);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    touched.Add(repositoryId);
                    log.Info($"Retention: removed restore point of {old.VmName} from {old.CreatedUtc.ToLocalTime():g}.");
                }
                catch (RestorePointPinnedException)
                {
                    await CatalogDeletes.UnmarkRestorePointAsync(db, old.Id, cancellationToken).ConfigureAwait(false);
                    log.Info($"Retention: restore point of {old.VmName} from {old.CreatedUtc.ToLocalTime():g} is in use and will be removed later.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await CatalogDeletes.UnmarkRestorePointAsync(db, old.Id, cancellationToken).ConfigureAwait(false);
                    log.Info($"Retention: restore point of {old.VmName} from {old.CreatedUtc.ToLocalTime():g} cannot be removed now ({ex.Message}); it is removed later.");
                }
            }
        }

        foreach (var repositoryId in touched)
        {
            var name = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? job.Repository.Name;
            await queue.EnqueueMaintenanceAsync(repositoryId, name, "Retention", cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Backs up one VM: snapshot, per-disk full or CBT-incremental read into the repository, commit.</summary>
internal sealed class VmBackupTask(
    IDbContextFactory<AppDbContext> dbFactory,
    SessionEventWriter events,
    SessionTracker tracker,
    GuestProcessingService guestProcessing,
    Telemetry.BackupMetrics metrics,
    bool checkpointWithVss,
    TimeProvider time,
    ILogger logger,
    BackupJob job,
    SessionEntity session,
    BackupJobObject vm,
    Guid taskId,
    int attempt,
    ChainTargets targets,
    IHypervisorSession hypervisor)
{
    private readonly IOperationLog _log = events.CreateLog(session.Id, taskId, vm.CachedName);

    // Set once the chain's extent is chosen (under the chain's lease).
    private IBackupRepository repository = null!;
    private Guid _repositoryId;
    private long _readBytes;
    private long _storedBytes;
    private long _totalBytes = 1;
    private DateTimeOffset? _transferStarted;
    private DateTimeOffset? _transferEnded;
    private DateTimeOffset _lastProgress = DateTimeOffset.MinValue;
    private readonly Lock _metricsLock = new();
    private readonly RateMeter _rate = new();
    private List<DiskMetricsDto> _diskMetrics = [];

    /// <summary>The pool label of this task's metrics: the connection's name.</summary>
    private string Pool => job.Connection.DisplayName;

    /// <summary>A namespace's disks are its volumes (PVCs).</summary>
    private string DiskNoun => job.Connection.Kind == HypervisorKind.Kubernetes ? "Volume" : "Disk";

    public async Task<SessionState> RunAsync(CancellationToken cancellationToken)
    {
        var state = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        metrics.RecordVmBackup(job.Name, state);
        return state;
    }

    private async Task<SessionState> RunCoreAsync(CancellationToken cancellationToken)
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
        ChainTarget? chain = null;
        var restorePointCreated = false;
        try
        {
            // The chain's lease (a move switching this chain waits for this attempt, and the other way round), then its extent.
            chain = await targets.AcquireAsync(job, vm, _log, cancellationToken).ConfigureAwait(false);
            repository = chain.Repository;
            _repositoryId = chain.Target.RepositoryId;
            if (targets.Scope.IsScaleOut)
            {
                _log.Info($"Extent {chain.Target.Name} of {targets.Scope.Repository.Name}{(chain.Target.NewChain ? $" (new chain: {chain.Target.Reason})" : string.Empty)}.");
            }

            var previousHead = chain.Target.NewChain ? await FindHeadElsewhereAsync(cancellationToken).ConfigureAwait(false) : null;
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
                    RepositoryId = _repositoryId,
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

            var guest = await PrepareGuestAsync(restorePointId, cancellationToken).ConfigureAwait(false);
            var ledger = new DbArtifactLedger(dbFactory, job.ConnectionId, session.Id, restorePointId, time);
            await using var writer = repository.BeginWrite(restorePointId, vm.NativeId, basePin);
            var snapshotName = $"{vm.CachedName} backdafuckup {time.GetUtcNow().ToLocalTime():yyyy-MM-dd HH:mm}";
            IVmBackupSession started;
            try
            {
                started = await hypervisor.BeginBackupAsync(
                    new VmBackupRequest(vm.NativeId, job.Id, session.Id, restorePointId, snapshotName, guest?.Quiescer, metrics.ForPool(Pool), vm.WithMemory, job.KubernetesPodVolumes), _log, ledger, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The snapshot holds what the guest prepared for it (a domain controller's directory copy): it goes from the guest.
                guest?.Quiescer?.CleanupAfterSnapshot();
            }

            await using var backup = started;
            var (consistency, consistencyReason, consistencyWarning) = CheckConsistency(guest, backup.GuestFreeze);
            var guestReason = consistencyReason;
            consistencyReason = AcrossDisks(consistency, consistencyReason, backup, job.Connection.Kind == HypervisorKind.Kubernetes ? "volumes" : "disks");

            // Disks captured one after another are a warning too; a SQL Server snapshot's own note is not.
            var pointWarning = consistencyWarning || !string.Equals(consistencyReason, guestReason, StringComparison.Ordinal);
            if (backup.Snapshot is { } facts)
            {
                var sqlSuspended = guest?.Quiescer is { SqlSuspendedMs: > 0 } q ? TimeSpan.FromMilliseconds(q.SqlSuspendedMs) : (TimeSpan?)null;
                metrics.RecordSnapshot(Pool, vm.CachedName, facts, backup.GuestFreeze, sqlSuspended, consistency);
            }

            var images = backup.Memory is { } ram ? backup.Disks.Append(ram).ToList() : backup.Disks.ToList();

            // Volumes read file by file count with what the previous run found in them, else with their size.
            var treeBytes = backup.FileTrees.ToDictionary(
                t => t.Key, t => baseReader?.Manifest.FileTrees.FirstOrDefault(b => b.Key == t.Key)?.Stats.Bytes ?? t.EstimatedBytes ?? 0);
            _totalBytes = Math.Max(1, images.Sum(d => d.CapacityBytes) + treeBytes.Values.Sum());
            lock (_metricsLock)
            {
                _diskMetrics = images.Select(d => new DiskMetricsDto
                {
                    Key = d.Key,
                    Name = d.Name,
                    CapacityBytes = d.CapacityBytes,
                    Metrics = new TransferMetricsDto { TotalBytes = d.CapacityBytes },
                }).Concat(backup.FileTrees.Select(t => new DiskMetricsDto
                {
                    Key = t.Key,
                    Name = t.Name,
                    CapacityBytes = treeBytes[t.Key],
                    Mode = "Files",
                    Metrics = new TransferMetricsDto { TotalBytes = treeBytes[t.Key] },
                })).ToList();
            }

            await tracker.UpdateTaskAsync(taskId, t => t.Metrics = CurrentMetrics(), CancellationToken.None).ConfigureAwait(false);
            var (manifestDisks, warnings) = await BackupDisksAsync(backup, writer, baseline, baseReader, cancellationToken).ConfigureAwait(false);

            // Volumes without snapshots, read file by file; what waited for their files (post-hooks) runs when they are read.
            var fileTrees = new List<ManifestFileTree>();
            if (backup.FileTrees.Count > 0)
            {
                try
                {
                    var read = await BackupFileTreesAsync(backup, writer, baseline, baseReader, cancellationToken).ConfigureAwait(false);
                    fileTrees = read.Trees;
                    warnings |= read.Warnings;
                }
                finally
                {
                    await backup.FileTreesReadAsync(_log, CancellationToken.None).ConfigureAwait(false);
                }

                // Files read while in use are not one point in time; a file that changed while it was read may be torn.
                var changed = fileTrees.Sum(t => t.Stats.ChangedWhileRead);
                var note = $"{string.Join(", ", fileTrees.Select(t => t.Name))} read file by file while in use, not at one point in time" +
                           (changed > 0 ? $"; {changed} file(s) changed while they were read" : string.Empty);
                consistencyReason = consistencyReason is null ? note : $"{consistencyReason}; {note}";
                pointWarning |= changed > 0;
            }

            // Snapshot with memory: the RAM image (read in full) and the checkpoint's metadata, for a restore that resumes.
            ManifestDisk? memoryImage = null;
            ManifestBlob? memoryMetadata = null;
            if (backup.Memory is { } memory)
            {
                (memoryImage, _) = await BackupDiskAsync(backup, writer, memory, null, null, cancellationToken).ConfigureAwait(false);
                if (backup.MemoryMetadata is { } checkpointMetadata)
                {
                    memoryMetadata = await writer.WriteBlobAsync("vm-metadata-memory", checkpointMetadata.Format, new MemoryStream(checkpointMetadata.Content), cancellationToken).ConfigureAwait(false);
                }

                lock (_metricsLock)
                {
                    _transferEnded = time.GetUtcNow();
                }
            }

            warnings |= consistencyWarning || guest?.Warning == true || backup.Warned;
            ManifestBlob? guestBlob = null;
            ManifestBlob? vssBlob = null;
            ManifestBlob? sqlSnapshotBlob = null;
            int? sqlDatabases = null;
            var (sqlUser, sqlConsistent) = (0, 0);
            StoredAdDatabase? directory = null;
            if (guest?.Quiescer is { Prepared: { } prepared } quiescer)
            {
                var stored = StoredGuestMetadata.Build(prepared, quiescer.Thawed, consistency, consistencyReason, quiescer.SqlSnapshot);
                sqlDatabases = stored.SqlDatabases.Count;
                (sqlUser, sqlConsistent) = stored.UserDatabaseConsistency();
                directory = stored.ActiveDirectory;
                if (directory is { Consistency: not "application" } && consistency == "application")
                {
                    _log.Warning($"The Active Directory database of domain {directory.DomainDns ?? "(unknown)"} is only crash-consistent: {directory.ConsistencyReason}.");
                    warnings = true;
                }

                if (directory is { IfmPath: not null, IfmError: null })
                {
                    _log.Info($"Active Directory: a consistent copy of the database ({VmBackupTask.Format(directory.IfmBytes ?? 0)}) was made with ntdsutil IFM " +
                        $"in {(directory.IfmSeconds ?? 0).ToString("0.0 s", CultureInfo.InvariantCulture)} before the snapshot; AD records it as a backup.");
                }
                else if (directory?.IfmError is { } ifmError)
                {
                    _log.Warning($"Active Directory: no consistent copy with ntdsutil IFM ({ifmError}); the directory is only as consistent as the snapshot.");
                    warnings = true;
                }

                if (directory?.Error is { } adError)
                {
                    _log.Warning($"Domain controller: some Active Directory facts could not be read in the VM ({adError}); object restores do not need them.");
                }

                guestBlob = await writer.WriteBlobAsync("guest-metadata", StoredGuestMetadata.FormatV2, new MemoryStream(stored.Serialize()), cancellationToken).ConfigureAwait(false);
                if (quiescer.SqlSnapshot is { Count: > 0 } snapshot)
                {
                    var blob = new StoredSqlSnapshot { Instances = [.. snapshot], SuspendedMs = quiescer.SqlSuspendedMs };
                    sqlSnapshotBlob = await writer.WriteBlobAsync("sql-snapshot", StoredSqlSnapshot.Format, new MemoryStream(blob.Serialize()), cancellationToken).ConfigureAwait(false);
                }

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

            // Without a VM generation id Windows cannot tell a restored copy of this DC that it was restored. The hypervisor
            // gives a VM none after its creation (Xen: generation_id is read-only), so only a new VM from another template helps.
            if (directory is not null && !VmGenerationIds.IsSet(backup.Vm.GenerationId))
            {
                _log.Warning($"Domain controller {backup.Vm.Name}: its VM has no VM generation id (created from a template without one), so Windows cannot tell that a VM restore of it was restored: " +
                    $"restoring it risks a USN rollback. Create domain controllers from a template with a VM generation id (XCP-ng's Windows templates have one; on the pool: xe vm-param-get uuid={vm.NativeId} param-name=generation_id). Single objects come back safely under Restore → Active Directory objects.");
                warnings = true;
            }

            var metadata = await writer.WriteBlobAsync("vm-metadata", backup.Metadata.Format, new MemoryStream(backup.Metadata.Content), cancellationToken).ConfigureAwait(false);
            // Incremental when every volume was read as its changes: disks by change tracking, file trees against the base's tree.
            var kind = manifestDisks.Count + fileTrees.Count > 0
                && manifestDisks.All(d => d.Stats.Mode == DiskBackupMode.Cbt)
                && fileTrees.All(t => t.BaseRestorePointId is not null)
                ? RestorePointKind.Incremental : RestorePointKind.Full;
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
                ConsistencyWarning = pointWarning,
                Guest = guestBlob,
                SqlDatabaseCount = guest?.Quiescer is { Prepared: not null } ? sqlDatabases : 0,
                SqlUserDatabaseCount = sqlUser,
                SqlConsistentCount = sqlConsistent,
                DomainController = guest?.Quiescer is { Prepared: not null } ? directory is not null : null,
                AdDomain = directory is null ? null : directory.DomainDns ?? string.Empty,
                VssDocuments = vssBlob,
                SqlSnapshot = sqlSnapshotBlob,
                Chunking = new ManifestChunking(repository.Info.ChunkSize, repository.Info.Hash, repository.Info.Compression),
                Disks = manifestDisks,
                ExcludedDisks = [.. backup.ExcludedDisks.Select(d => new ManifestExcludedDisk(d.Key, d.SourceDiskId, d.Name, d.CapacityBytes, d.Bootable, d.StorageId, d.StorageName))],
                Memory = memoryImage,
                MemoryMetadata = memoryMetadata,
                MemoryDiskIds = memoryImage is null ? null : backup.MemoryDiskIds,
                Kubernetes = backup.Kubernetes,
                FileTrees = fileTrees,
            };
            var committed = await writer.CommitAsync(manifest, cancellationToken).ConfigureAwait(false);

            await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var entity = await db.RestorePoints.FirstAsync(r => r.Id == restorePointId, CancellationToken.None).ConfigureAwait(false);
                ApplyManifest(entity, manifest, committed.Path);
                if (guest?.Quiescer?.SqlSnapshot is { Count: > 0 } recorded)
                {
                    await SqlBackupCatalog.RecordSnapshotAsync(db, entity, recorded, time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                }

                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await backup.CommitAsync(_log, CancellationToken.None).ConfigureAwait(false);
                if (backup.Snapshot is { } snapshotFacts && backup.ReleaseDuration is { } release)
                {
                    metrics.RecordRelease(Pool, snapshotFacts, release);
                }

                await ReleaseBaseTokensAsync(baseline ?? previousHead, manifestDisks).ConfigureAwait(false);
            }
            catch (SnapshotCleanupDeferredException ex)
            {
                warnings = true;
                _log.Warning(ex.Message);
                await ReleaseBaseTokensAsync(baseline ?? previousHead, manifestDisks).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings = true;
                _log.Warning(
                    $"Reducing the snapshot to change tracking data failed ({ex.Message}). The restore point is complete; the snapshot is removed, " +
                    "so the next backup of this VM reads its disks in full.");
            }

            _log.Info($"Backup finished ({kind}): {MetricsText.Describe(CurrentMetrics())}.", "summary");

            // A chain that had to start anew on another extent (its own sealed, in maintenance or offline) is worth a look.
            warnings |= chain.Target.NewChain && previousHead is not null;
            await FinishTaskAsync(warnings ? SessionState.Warning : SessionState.Succeeded, null, restorePointId).ConfigureAwait(false);
            return warnings ? SessionState.Warning : SessionState.Succeeded;
        }
        catch (ScaleOut.ChainSkippedException ex)
        {
            _log.Warning(ex.Message);
            await FinishTaskAsync(SessionState.Warning, ex.Message, null).ConfigureAwait(false);
            return SessionState.Warning;
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
            if (chain is not null)
            {
                await chain.DisposeAsync().ConfigureAwait(false);
            }
        }
    }


    /// <summary>
    /// Guest processing before the snapshot: the agent of the VM (installed first if needed), or the reason why the backup
    /// will be crash-consistent. With the policy "fail the VM" a missing agent fails the VM before any snapshot.
    /// </summary>
    private async Task<GuestPreparation?> PrepareGuestAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        // A namespace has no guest: its consistency comes from its hooks.
        if (!job.GuestProcessing || job.Connection.Kind == HypervisorKind.Kubernetes)
        {
            return null;
        }

        if (vm.WithMemory && !checkpointWithVss)
        {
            _log.Info($"Guest processing is not used for {vm.CachedName}: it is backed up with memory, so the snapshot pauses the whole VM instead of freezing its writes with VSS. " +
                      "Its restore points have no SQL Server or Active Directory details.");
            return null;
        }

        var guest = await guestProcessing.PrepareAsync(job, vm, _log, cancellationToken, $"backdafuckup restore point {restorePointId}").ConfigureAwait(false);
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

    /// <summary>
    /// The platform snapshots the disks of a VM one after another (Xen: each disk is paused and captured on its own). Unless
    /// the guest held its writes for the whole snapshot (application-consistent), a VM with two or more disks is not
    /// consistent across its disks, not even like a power failure: the reason says so, with the measured gap, and the
    /// restore point shows the warning. The session's result does not change.
    /// </summary>
    private static string? AcrossDisks(string consistency, string? reason, IVmBackupSession backup, string disks)
    {
        if (consistency == "application" || backup.Snapshot is not { DiskCount: >= 2 } facts || facts.Method == SnapshotMethods.Checkpoint)
        {
            // A snapshot with memory pauses the whole VM: its disks match.
            return reason;
        }

        var gap = facts.DiskSkew is { } skew ? $" over {skew.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s" : string.Empty;
        var scope = backup.GuestFreeze?.Sql is not null ? "Outside SQL Server, the" : "The";
        var text = $"{scope} {facts.DiskCount} {disks} were captured one after another{gap}, not at one moment: data that spans {disks} " +
                   $"({(disks == "disks" ? "a database and its log, a volume over several disks" : "an application whose data lies on several volumes")}) may not match after a restore.";
        return reason is null ? text : $"{reason} {text}";
    }

    /// <summary>
    /// "application" or "crash" plus the reason and whether it is a warning; with the policy "fail the VM" a failed freeze
    /// fails the VM. A SQL Server snapshot is "crash" for the guest's files without a warning when every database was
    /// recorded: that is what it promises.
    /// </summary>
    private (string Consistency, string? Reason, bool Warning) CheckConsistency(GuestPreparation? guest, GuestFreezeResult? freeze)
    {
        if (guest is null)
        {
            // Without guest processing, a platform's own quiescing decides (a namespace's hooks): failed hooks are a warning.
            return freeze switch
            {
                null => ("crash", null, false),
                { ApplicationConsistent: true } => ("application", null, false),
                _ => ("crash", freeze.Reason, true),
            };
        }

        if (freeze is { ApplicationConsistent: true })
        {
            return ("application", null, false);
        }

        if (freeze?.Sql is { } sql)
        {
            if (!sql.AllDatabasesConsistent && job.GuestFailurePolicy == GuestFailurePolicy.FailVm)
            {
                throw new InvalidOperationException($"Guest processing: not every SQL Server database is consistent: {freeze.Reason ?? sql.Summary}");
            }

            return ("crash", sql.AllDatabasesConsistent
                ? RestorePointConsistency.SqlSnapshotNote
                : $"SQL Server snapshot: {freeze.Reason ?? sql.Summary} Files outside SQL Server are crash-consistent.",
                !sql.AllDatabasesConsistent || sql.Warnings is { Count: > 0 });
        }

        var reason = freeze?.Reason ?? guest.Reason ?? "the guest could not be frozen";
        if (job.GuestFailurePolicy == GuestFailurePolicy.FailVm)
        {
            throw new InvalidOperationException($"Guest processing: the snapshot is not application-consistent: {reason}");
        }

        // A missing agent was reported before the snapshot, a failed freeze by the hypervisor provider.
        return ("crash", reason, true);
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

    /// <summary>
    /// Reads each volume of <see cref="IVmBackupSession.FileTrees"/> file by file into a file tree, one after another; in an
    /// incremental run against its tree in the previous restore point, whose unchanged files are not read again.
    /// </summary>
    private async Task<(List<ManifestFileTree> Trees, bool Warnings)> BackupFileTreesAsync(
        IVmBackupSession backup, IRepositoryWriter writer, RestorePointEntity? baseline, IRestorePointReader? baseReader, CancellationToken cancellationToken)
    {
        var trees = new List<ManifestFileTree>();
        var warned = false;
        foreach (var tree in backup.FileTrees)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_metricsLock)
            {
                _transferStarted ??= time.GetUtcNow();
            }

            var previous = await PreviousTreeAsync(tree, baseline, baseReader, cancellationToken).ConfigureAwait(false);
            await using var reader = await backup.OpenFileTreeAsync(tree, previous, cancellationToken).ConfigureAwait(false);
            var builder = new FileTreeBuilder(writer, repository.Info.ChunkSize) { Previous = previous };
            FileTreeBuilder.Result? result = null;
            using (var reporting = new CancellationTokenSource())
            {
                var progress = ReportTreeProgressAsync(tree, builder, reader.TransportName, started, reporting.Token);
                try
                {
                    await reader.ReadAsync(async (stream, ct) => result = await builder.BuildAsync(stream, ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await reporting.CancelAsync().ConfigureAwait(false);
                    await progress.ConfigureAwait(false);
                }
            }

            var built = result ?? throw new InvalidOperationException($"The files of {tree.Description} were not read.");
            var index = await writer.WriteFileTreeIndexAsync(tree.Key, built.Root, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _storedBytes, built.Stats.NewStoredBytes);
            var stats = built.Stats;
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            UpdateDisk(tree.Key, d => d with
            {
                // The tree in the run's figures (the summary line, the task's metrics).
                CapacityBytes = stats.Bytes,
                State = "Done",
                Mode = "Files",
                Transport = reader.TransportName,
                Metrics = new TransferMetricsDto
                {
                    TotalBytes = stats.Bytes, ProcessedBytes = stats.Bytes, ReadBytes = stats.ReadBytes, UnchangedBytes = stats.UnchangedBytes, DedupedBytes = built.DedupedBytes,
                    NewDataBytes = built.NewBytes, StoredBytes = stats.NewStoredBytes, Seconds = Math.Round(elapsed.TotalSeconds, 3),
                },
            });
            lock (_metricsLock)
            {
                _transferEnded = time.GetUtcNow();
            }

            var read = previous is null
                ? $"{Format(stats.ReadBytes)} read"
                : stats.UnchangedFiles == 0
                    ? $"no file unchanged since the previous run, {Format(stats.ReadBytes)} read"
                    : $"{stats.UnchangedFiles:N0} file(s) unchanged since the previous run ({Format(stats.UnchangedBytes)} not read again), {Format(stats.ReadBytes)} read";
            _log.Info($"Volume {tree.Name}: {stats.Files:N0} file(s) in {stats.Directories:N0} folder(s), {Format(stats.Bytes)}, read file by file through {reader.TransportName} " +
                      $"in {elapsed:hh\\:mm\\:ss}; {read}; {Format(stats.NewStoredBytes)} new in the repository.", $"tree-{tree.Key}");
            if (stats.ChangedWhileRead > 0)
            {
                _log.Warning($"Volume {tree.Name}: {stats.ChangedWhileRead} file(s) changed while they were read; their copies may be torn (the next run reads them again).");
            }

            if (stats.Issues > 0)
            {
                warned = true;
                _log.Warning($"Volume {tree.Name}: {stats.Issues} entr{(stats.Issues == 1 ? "y" : "ies")} not read: {string.Join("; ", built.Issues.Take(5))}" +
                             (stats.Issues > 5 ? $"; and {stats.Issues - 5} more" : string.Empty) + ".");
            }

            trees.Add(new ManifestFileTree
            {
                Key = tree.Key, Name = tree.Name, Kind = tree.Kind, Reader = tree.Reader, Index = index, RootLength = built.Root.DiskSize, Root = built.RootMeta, Stats = stats,
                BaseRestorePointId = stats.UnchangedFiles > 0 ? previous?.RestorePointId : null,
            });
        }

        return (trees, warned);
    }

    /// <summary>The volume's tree in the previous restore point, if this run is incremental and that restore point has it.</summary>
    private async Task<FileTreeBase?> PreviousTreeAsync(FileTreeSource tree, RestorePointEntity? baseline, IRestorePointReader? baseReader, CancellationToken cancellationToken)
    {
        if (session.RunMode == SessionRunMode.ActiveFull)
        {
            _log.Info($"Volume {tree.Name}: active full, every file is read.");
            return null;
        }

        if (baseline is null || baseReader?.Manifest.FileTrees.FirstOrDefault(t => t.Key == tree.Key) is not { } before)
        {
            _log.Info($"Volume {tree.Name}: no previous restore point of this job has it; every file is read.");
            return null;
        }

        try
        {
            var root = await baseReader.ReadFileTreeIndexAsync(tree.Key, cancellationToken).ConfigureAwait(false);
            _log.Info($"Volume {tree.Name}: compared with the restore point of {baseline.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} " +
                      $"({before.Stats.Files:N0} file(s), {Format(before.Stats.Bytes)}); files unchanged since then are not read again.");
            return new FileTreeBase(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), baseReader.Chunks, repository.Info.ChunkSize, baseline.Id);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or KeyNotFoundException)
        {
            _log.Warning($"Volume {tree.Name}: the tree of the previous restore point {baseline.Id} cannot be read ({ex.Message}); every file is read.");
            return null;
        }
    }

    /// <summary>The volume's progress every few seconds while it is read: in the task's metrics, the overall progress and a log line.</summary>
    private async Task ReportTreeProgressAsync(FileTreeSource tree, FileTreeBuilder builder, string transport, long started, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var processed = builder.ProcessedBytes;
                var read = builder.Bytes;
                var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
                UpdateDisk(tree.Key, d => d with
                {
                    State = "Running",
                    Transport = transport,
                    CapacityBytes = Math.Max(d.CapacityBytes, processed),
                    Metrics = new TransferMetricsDto
                    {
                        TotalBytes = Math.Max(d.CapacityBytes, processed), ProcessedBytes = processed, ReadBytes = read, UnchangedBytes = builder.UnchangedBytes,
                        Seconds = Math.Round(seconds, 3),
                    },
                });
                var unchanged = builder.UnchangedFiles > 0 ? $" · {builder.UnchangedFiles:N0} unchanged" : string.Empty;
                var at = builder.LastHeartbeat is { Path.Length: > 0 } heartbeat ? $" · at {heartbeat.Path}" : string.Empty;
                _log.Info($"Volume {tree.Name}: {builder.Files:N0} file(s) · read {Format(read)} at {MetricsText.Rate(seconds > 0 ? read / seconds : 0)}{unchanged}{at}", $"tree-{tree.Key}");
                await UpdateProgressAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The volume is read (or failed).
        }
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
                changes = ChangedExtents.Full("the disk size changed", FullReadReasons.SizeChanged);
            }
            else
            {
                try
                {
                    baseIndex = await baseReader.ReadDiskIndexAsync(baseDisk.DiskKey, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException)
                {
                    changes = ChangedExtents.Full($"the index of the previous restore point is unreadable ({ex.Message})", FullReadReasons.IndexUnreadable);
                }
            }
        }

        if (changes.IsFull && baseline is not null && session.RunMode != SessionRunMode.ActiveFull && changes.FullReasonCode != FullReadReasons.NotTracked)
        {
            _log.Warning($"{DiskNoun} {disk.Name}: full backup because {changes.FullReason}.");
            warned = !string.IsNullOrEmpty(changes.FullReason);
        }
        else if (changes.IsFull)
        {
            _log.Info($"{DiskNoun} {disk.Name}: full backup ({changes.FullReason}).");
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
        metrics.RecordDisk(Pool, disk.StorageName, result.Stats.Transport ?? reader.TransportName, result.Counters, disk.Key == SnapshotDisk.MemoryKey ? "memory" : "disk",
            !changes.IsFull ? null : session.RunMode == SessionRunMode.ActiveFull ? FullReadReasons.ActiveFull : changes.FullReasonCode ?? FullReadReasons.Other);
        UpdateDisk(disk.Key, d => d with { State = "Done", Mode = result.Stats.Mode.ToString(), FullReason = result.Stats.FullReason, Transport = result.Stats.Transport, Metrics = diskMetrics });
        _log.Info($"{DiskNoun} {disk.Name}: {result.Stats.Mode} via {result.Stats.Transport}: {MetricsText.Describe(diskMetrics)}.", key);

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
            .Where(r => r.JobId == job.Id && r.RepositoryId == _repositoryId && r.VmNativeId == vm.NativeId && r.State == RestorePointState.Available)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>When a chain starts anew on another extent: its newest restore point elsewhere, whose change tokens the new full supersedes.</summary>
    private async Task<RestorePointEntity?> FindHeadElsewhereAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var others = targets.Scope.RepositoryIds.Where(id => id != _repositoryId).ToList();
        return await db.RestorePoints.Include(r => r.Disks).AsNoTracking()
            .Where(r => r.JobId == job.Id && others.Contains(r.RepositoryId) && r.VmNativeId == vm.NativeId && r.State == RestorePointState.Available)
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
        entity.ConsistencyWarning = manifest.ConsistencyWarning ?? RestorePointConsistency.IsWarning(manifest.ConsistencyReason);
        entity.ObjectKind = manifest.Kubernetes switch
        {
            null => RestorePointObjectKinds.Vm,
            { ClusterScope: true } => RestorePointObjectKinds.KubernetesCluster,
            _ => RestorePointObjectKinds.KubernetesNamespace,
        };
        if (string.IsNullOrEmpty(entity.PlatformId) && !string.IsNullOrEmpty(manifest.Source.PlatformId))
        {
            // A cluster's session learns its platform id (kube-system's uid) only when the backup begins.
            entity.PlatformId = manifest.Source.PlatformId;
        }
        entity.SqlDatabaseCount = manifest.SqlDatabaseCount ?? (manifest.Guest is null ? 0 : null);
        entity.SqlUserDatabaseCount = manifest.SqlUserDatabaseCount ?? (manifest.Guest is null ? 0 : null);
        entity.SqlConsistentCount = manifest.SqlUserDatabaseCount is null ? entity.SqlUserDatabaseCount : manifest.SqlConsistentCount;
        entity.DomainController = manifest.DomainController ?? (manifest.Guest is null ? false : null);
        entity.AdDomain = manifest.AdDomain;
        entity.SqlBackupSetsRecorded = manifest.SqlSnapshot is null ? true : null;
        entity.LogicalBytes = manifest.Disks.Sum(d => d.CapacityBytes) + manifest.FileTrees.Sum(t => t.Stats.Bytes);
        entity.NewStoredBytes = manifest.AllImages.Sum(d => d.Stats.NewStoredBytes) + manifest.FileTrees.Sum(t => t.Stats.NewStoredBytes);
        entity.MemoryImageBytes = manifest.Memory?.CapacityBytes;
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
        }).Concat(manifest.FileTrees.Select(t => new RestorePointDisk
        {
            RestorePointId = entity.Id,
            DiskKey = t.Key,
            SourceDiskId = t.Key,
            Name = t.Name,
            Kind = RestorePointDiskKinds.FileTree,
            CapacityBytes = t.Stats.Bytes,
            Mode = DiskBackupMode.Full,
            ReadBytes = t.Stats.ReadBytes,
            NewStoredBytes = t.Stats.NewStoredBytes,
            FileCount = t.Stats.Files,
            IssueCount = t.Stats.Issues,
        })).ToList();
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
            $"{DiskNoun} {disk.Name}: {fraction:P0} · read {Format(counters.ReadBytes)} at {MetricsText.Rate(counters.Elapsed.TotalSeconds > 0 ? counters.ReadBytes / counters.Elapsed.TotalSeconds : 0)} · stored {Format(counters.StoredBytes)}",
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

/// <summary>Picks the extent for each VM of a backup session (or the standalone repository), under the chain's lease.</summary>
internal sealed class ChainTargets(ScaleOut.RepositoryScope scope, ScaleOut.RepositoryPlacement placement, ScaleOut.ChainLocks locks, RepositoryManager repositories)
{
    public ScaleOut.RepositoryScope Scope => scope;

    public async Task<ChainTarget> AcquireAsync(BackupJob job, BackupJobObject vm, IOperationLog log, CancellationToken cancellationToken)
    {
        var lease = await locks.For(job.Id, vm.NativeId)
            .AcquireAsync($"Backup of {vm.CachedName}", holder => log.Info($"Waiting for '{holder}' of this chain to finish."), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var target = await placement.ForBackupAsync(scope, job, vm.NativeId, vm.CachedName, log, cancellationToken).ConfigureAwait(false);
            try
            {
                return new ChainTarget(target, await repositories.GetAsync(target.RepositoryId, cancellationToken).ConfigureAwait(false), lease);
            }
            catch
            {
                target.Reservation.Dispose();
                throw;
            }
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>The extent of a VM's backup attempt, its reservation and the chain's lease; disposing releases both.</summary>
internal sealed class ChainTarget(ScaleOut.PlacementTarget target, IBackupRepository repository, IAsyncDisposable lease) : IAsyncDisposable
{
    public ScaleOut.PlacementTarget Target => target;

    public IBackupRepository Repository => repository;

    public async ValueTask DisposeAsync()
    {
        target.Reservation.Dispose();
        await lease.DisposeAsync().ConfigureAwait(false);
    }
}
