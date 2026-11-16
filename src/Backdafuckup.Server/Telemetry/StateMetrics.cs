using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Telemetry;

/// <summary>Configuration section <c>Metrics</c>.</summary>
public sealed class MetricsOptions
{
    /// <summary>Serve <c>/metrics</c> and refresh the state gauges.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the catalog state (VMs, repositories, agents, SQL log backups) is read.</summary>
    public TimeSpan CatalogInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often each pool's storages and snapshots are read (read only, three XAPI calls per pool).</summary>
    public TimeSpan PoolInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>An agent without contact for longer counts as offline.</summary>
    public TimeSpan AgentOfflineAfter { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Gauges for the state of catalog and pools. A scrape only reads the last snapshot; <see cref="MetricsStateService"/>
/// replaces the snapshots as a whole, so one scrape never mixes two refreshes and never waits for the database or a pool.
/// </summary>
public sealed class StateMetrics
{
    private readonly ConcurrentDictionary<Guid, PoolSnapshot> _pools = new();
    private CatalogSnapshot _catalog = CatalogSnapshot.Empty;

    public StateMetrics(IMeterFactory meterFactory, IConfiguration configuration)
    {
        var meter = meterFactory.Create(BackupMetrics.MeterName);
        var minFreeRatio = (configuration.GetValue<double?>("Xen:MinStorageFreePercent") ?? 5) / 100.0;

        // Per VM and job (the console's rule for stale and "no restore point").
        meter.CreateObservableGauge("bdfu_vm_last_backup_timestamp_seconds", () => Vms(v => v.LatestUnix),
            "s", "Time of the VM's newest restore point in the job (Unix seconds).");
        meter.CreateObservableGauge("bdfu_vm_stale", () => Vms(v => v.Stale ? 1 : 0),
            description: "1 when the VM missed the job's most recent scheduled run that started at least 12 hours ago.");
        meter.CreateObservableGauge("bdfu_vm_no_restore_point", () => Vms(v => v.NoPoint ? 1 : 0),
            description: "1 when the VM is in the job but has no restore point of it.");
        meter.CreateObservableGauge("bdfu_vm_restore_points", () => Vms(v => v.Points),
            description: "Restore points of the VM in the job.");
        meter.CreateObservableGauge("bdfu_vm_last_backup_result", () =>
            _catalog.LastResults.Select(r => new Measurement<int>(1, Tag("backup_job", r.Job), Tag("vm", r.Vm), Tag("result", r.Result))),
            description: "1 for the result of the VM's last backup attempt in the job (last 14 days).");

        meter.CreateObservableGauge("bdfu_sessions_queued", () =>
            _catalog.Queued.Select(q => new Measurement<int>(q.Value, Tag("kind", q.Key))),
            description: "Sessions waiting to run, by kind.");
        meter.CreateObservableGauge("bdfu_artifacts_pending", () => _catalog.PendingArtifacts,
            description: "Snapshots and disks of backups still to be removed or reduced (retried every 10 minutes).");

        meter.CreateObservableGauge("bdfu_repository_size_bytes", () => Repositories(r => r.SizeBytes), "By", "Size of the repository's volume.");
        meter.CreateObservableGauge("bdfu_repository_free_bytes", () => Repositories(r => r.FreeBytes), "By", "Free space on the repository's volume.");
        meter.CreateObservableGauge("bdfu_repository_stored_bytes", () => Repositories(r => r.StoredBytes), "By",
            "New data stored by the repository's VM restore points (sum at creation; deduplicated data counts once).");
        meter.CreateObservableGauge("bdfu_repository_restore_points", () => Repositories(r => r.Points), description: "VM restore points in the repository.");
        meter.CreateObservableGauge("bdfu_repository_format", () => Repositories(r => r.Format), description: "On-disk format of the repository: 1 = one file per chunk, 2 = pack files.");
        meter.CreateObservableGauge("bdfu_repository_packs", () => Repositories(r => r.Packs?.Packs), description: "Pack files of the repository (format 2).");
        meter.CreateObservableGauge("bdfu_repository_pack_bytes", () => Repositories(r => r.Packs?.PackBytes), "By", "Size of the repository's pack files (format 2).");
        meter.CreateObservableGauge("bdfu_repository_unused_bytes", () => Repositories(r => r.Packs?.UnusedBytes), "By",
            "Unreferenced bytes left in packs after the last garbage collection (not worth repacking yet).");
        meter.CreateObservableGauge("bdfu_repository_index_entries", () => Repositories(r => r.Packs?.IndexEntries), description: "Chunks in the repository's pack index.");
        meter.CreateObservableGauge("bdfu_repository_index_memory_bytes", () => Repositories(r => r.Packs?.IndexMemoryBytes), "By", "Memory the repository's pack index uses (approximate).");
        meter.CreateObservableGauge("bdfu_repository_packing_unfinished", () => Repositories(r => r.Packs is null ? null : r.Packs.LooseChunksMayExist ? 1 : 0),
            description: "1 while a converted repository still has loose chunks of format 1 to pack.");

        meter.CreateObservableGauge("bdfu_agents", () =>
            new[] { new Measurement<int>(_catalog.AgentsOnline, Tag("state", "online")), new Measurement<int>(_catalog.AgentsOffline, Tag("state", "offline")) },
            description: "Guest agents by state (offline: no contact for 10 minutes).");
        meter.CreateObservableGauge("bdfu_agent_last_contact_timestamp_seconds", () =>
            _catalog.Agents.Select(a => new Measurement<long>(a.LastContactUnix, Tag("agent", a.Name))),
            "s", "Last contact of each guest agent (Unix seconds).");

        meter.CreateObservableGauge("bdfu_sql_last_log_backup_timestamp_seconds", () =>
            _catalog.SqlLogs.Select(l => new Measurement<long>(l.LastLogUnix, Tag("vm", l.Vm), Tag("sql_instance", l.Instance), Tag("database", l.Database))),
            "s", "Newest log backup of each SQL Server database with log backups (Unix seconds).");
        meter.CreateObservableGauge("bdfu_sql_log_backup_interval_seconds", () =>
            _catalog.SqlLogs.Select(l => new Measurement<long>(l.IntervalSeconds, Tag("vm", l.Vm), Tag("sql_instance", l.Instance), Tag("database", l.Database))),
            "s", "Configured log backup interval of each SQL Server database's job.");

        // Pools.
        meter.CreateObservableGauge("bdfu_storage_size_bytes", () => Storages(s => s.SizeBytes), "By", "Size of the storage (Xen: SR physical_size).");
        meter.CreateObservableGauge("bdfu_storage_used_bytes", () => Storages(s => s.UsedBytes), "By", "Used space of the storage (Xen: SR physical_utilisation).");
        meter.CreateObservableGauge("bdfu_storage_virtual_allocation_bytes", () => Storages(s => s.VirtualAllocationBytes), "By",
            "Sum of the virtual sizes of the disks on the storage.");
        meter.CreateObservableGauge("bdfu_storage_min_free_ratio", () => Storages(_ => minFreeRatio),
            description: "Free share below which a backup refuses to snapshot a VM on the storage (Xen:MinStorageFreePercent).");
        meter.CreateObservableGauge("bdfu_storage_vdis", () => StorageVdis(),
            description: "Disks on the storage by kind (disk, snapshot, cbt_metadata, unmanaged = base copies); stands in for the LV count of LVM storages.");
        meter.CreateObservableGauge("bdfu_pool_snapshots", () => PoolSnapshots(s => s.Count()),
            description: "VM snapshots in the pool by owner (backdafuckup: tagged by a backup of this server; other).");
        meter.CreateObservableGauge("bdfu_pool_snapshot_oldest_seconds", () => PoolSnapshots(s => s.Max(x => x.AgeSeconds)),
            "s", "Age of the oldest VM snapshot in the pool by owner.");
        meter.CreateObservableGauge("bdfu_pool_vm_disks", () =>
            _pools.Values.Where(p => p.State is not null).SelectMany(p => new[]
            {
                new Measurement<int>(p.State!.DisksWithChangeTracking, Tag("pool", p.Pool), Tag("cbt", "true")),
                new Measurement<int>(p.State!.DisksWithoutChangeTracking, Tag("pool", p.Pool), Tag("cbt", "false")),
            }),
            description: "Regular VM disks with and without changed block tracking.");
        meter.CreateObservableGauge("bdfu_pool_poll_success", () => _pools.Values.Select(p => new Measurement<int>(p.Success ? 1 : 0, Tag("pool", p.Pool))),
            description: "1 when the last read of the pool's storages succeeded.");
        meter.CreateObservableGauge("bdfu_pool_poll_seconds", () => _pools.Values.Select(p => new Measurement<double>(p.Seconds, Tag("pool", p.Pool))),
            "s", "Duration of the last read of the pool's storages.");
    }

    internal void SetCatalog(CatalogSnapshot catalog) => Volatile.Write(ref _catalog, catalog);

    internal void SetPool(Guid connectionId, PoolSnapshot snapshot) => _pools[connectionId] = snapshot;

    internal PoolSnapshot? GetPool(Guid connectionId) => _pools.TryGetValue(connectionId, out var pool) ? pool : null;

    internal void RemovePoolsExcept(IReadOnlySet<Guid> connectionIds)
    {
        foreach (var id in _pools.Keys.Where(id => !connectionIds.Contains(id)))
        {
            _pools.TryRemove(id, out _);
        }
    }

    private IEnumerable<Measurement<long>> Vms(Func<VmRow, long?> value)
    {
        foreach (var row in _catalog.Vms)
        {
            if (value(row) is { } v)
            {
                yield return new Measurement<long>(v, Tag("backup_job", row.Job), Tag("vm", row.Vm));
            }
        }
    }

    private IEnumerable<Measurement<long>> Repositories(Func<RepositoryRow, long?> value)
    {
        foreach (var row in _catalog.Repositories)
        {
            if (value(row) is { } v)
            {
                yield return new Measurement<long>(v, Tag("repository", row.Name));
            }
        }
    }

    private IEnumerable<Measurement<double>> Storages(Func<StorageUsage, double> value) =>
        _pools.Values.Where(p => p.State is not null)
            .SelectMany(p => p.State!.Storages.Select(s => new Measurement<double>(value(s), Tag("pool", p.Pool), Tag("storage", s.Name), Tag("type", s.Type))));

    private IEnumerable<Measurement<int>> StorageVdis() =>
        _pools.Values.Where(p => p.State is not null).SelectMany(p => p.State!.Storages.SelectMany(s => new[]
        {
            new Measurement<int>(s.Disks, Tag("pool", p.Pool), Tag("storage", s.Name), Tag("kind", "disk")),
            new Measurement<int>(s.SnapshotDisks, Tag("pool", p.Pool), Tag("storage", s.Name), Tag("kind", "snapshot")),
            new Measurement<int>(s.ChangeTrackingMetadata, Tag("pool", p.Pool), Tag("storage", s.Name), Tag("kind", "cbt_metadata")),
            new Measurement<int>(s.Unmanaged, Tag("pool", p.Pool), Tag("storage", s.Name), Tag("kind", "unmanaged")),
        }));

    private IEnumerable<Measurement<double>> PoolSnapshots(Func<IEnumerable<(double AgeSeconds, bool Ours)>, double> aggregate)
    {
        foreach (var pool in _pools.Values.Where(p => p.State is not null))
        {
            var snapshots = pool.State!.Snapshots
                .Select(s => (AgeSeconds: s.TakenUtc is { } t ? Math.Max(0, (pool.ReadUtc - t).TotalSeconds) : 0, s.Ours))
                .ToList();
            foreach (var owner in new[] { true, false })
            {
                var mine = snapshots.Where(s => s.Ours == owner).ToList();
                yield return new Measurement<double>(mine.Count == 0 ? 0 : aggregate(mine), Tag("pool", pool.Pool), Tag("owner", owner ? "backdafuckup" : "other"));
            }
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    internal sealed record VmRow(string Job, string Vm, long? LatestUnix, bool Stale, bool NoPoint, long Points);

    internal sealed record ResultRow(string Job, string Vm, string Result);

    internal sealed record RepositoryRow(string Name, long? SizeBytes, long? FreeBytes, long StoredBytes, long Points, long? Format = null, Backdafuckup.Storage.RepositoryPackStats? Packs = null);

    internal sealed record AgentRow(string Name, long LastContactUnix);

    internal sealed record SqlLogRow(string Vm, string Instance, string Database, long LastLogUnix, long IntervalSeconds);

    internal sealed record CatalogSnapshot(
        IReadOnlyList<VmRow> Vms,
        IReadOnlyList<ResultRow> LastResults,
        IReadOnlyDictionary<string, int> Queued,
        int PendingArtifacts,
        IReadOnlyList<RepositoryRow> Repositories,
        IReadOnlyList<AgentRow> Agents,
        int AgentsOnline,
        int AgentsOffline,
        IReadOnlyList<SqlLogRow> SqlLogs)
    {
        public static CatalogSnapshot Empty { get; } = new([], [], new Dictionary<string, int>(), 0, [], [], 0, 0, []);
    }

    internal sealed record PoolSnapshot(string Pool, bool Success, double Seconds, StorageState? State, DateTimeOffset ReadUtc);
}

/// <summary>Refreshes the snapshots of <see cref="StateMetrics"/>: the catalog every minute, every pool every five minutes.</summary>
public sealed class MetricsStateService(
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectionManager connections,
    RepositoryManager repositories,
    StateMetrics state,
    IOptions<MetricsOptions> options,
    TimeProvider time,
    ILogger<MetricsStateService> logger) : BackgroundService
{
    private static readonly TimeSpan ResultWindow = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        var nextPools = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                state.SetCatalog(await ReadCatalogAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Metrics: reading the catalog state failed; the gauges keep the previous values.");
            }

            if (time.GetUtcNow() >= nextPools)
            {
                nextPools = time.GetUtcNow() + o.PoolInterval;
                await ReadPoolsAsync(stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(o.CatalogInterval, time, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<StateMetrics.CatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var vms = new List<StateMetrics.VmRow>();
        foreach (var vm in await BackupVms.ListAsync(db, new BackupVms.Filter(), now, cancellationToken).ConfigureAwait(false))
        {
            foreach (var job in vm.Jobs)
            {
                vms.Add(new StateMetrics.VmRow(job.JobName, vm.VmName, job.Latest?.CreatedUtc.ToUnixTimeSeconds(),
                    job.State == BackupVmStates.Stale, job.State == BackupVmStates.NoPoint, job.PointCount));
            }
        }

        var since = now - ResultWindow;
        var attempts = await db.SessionTasks.AsNoTracking()
            .Join(db.Sessions.Where(s => s.Kind == SessionKind.Backup && s.JobId != null), t => t.SessionId, s => s.Id, (t, s) => new { s.JobId, t.ObjectNativeId, t.ObjectName, t.State, t.EndedUtc })
            .Where(x => x.EndedUtc != null && x.EndedUtc >= since)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var jobNames = await db.Jobs.AsNoTracking().Select(j => new { j.Id, j.Name }).ToDictionaryAsync(j => j.Id, j => j.Name, cancellationToken).ConfigureAwait(false);
        var results = attempts
            .Where(a => a.JobId is { } id && jobNames.ContainsKey(id))
            .GroupBy(a => (a.JobId, a.ObjectNativeId))
            .Select(g => g.MaxBy(a => a.EndedUtc)!)
            .Select(a => new StateMetrics.ResultRow(jobNames[a.JobId!.Value], a.ObjectName, BackupMetrics.Result(a.State)))
            .ToList();

        var queued = (await db.Sessions.AsNoTracking().Where(s => s.State == SessionState.Queued).GroupBy(s => s.Kind)
                .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(g => BackupMetrics.Kind(g.Key), g => g.Count);
        foreach (var kind in Enum.GetValues<SessionKind>())
        {
            queued.TryAdd(BackupMetrics.Kind(kind), 0);
        }

        var pending = await db.Artifacts.CountAsync(a => a.State == ArtifactState.CommitPending || a.State == ArtifactState.CleanupPending, cancellationToken).ConfigureAwait(false);

        var pointsByRepository = await db.RestorePoints.AsNoTracking().Where(r => r.State == RestorePointState.Available)
            .GroupBy(r => r.RepositoryId).Select(g => new { g.Key, Count = g.LongCount(), Stored = g.Sum(r => r.NewStoredBytes) })
            .ToDictionaryAsync(g => g.Key, cancellationToken).ConfigureAwait(false);
        var repositoryRows = new List<StateMetrics.RepositoryRow>();
        foreach (var repository in await db.Repositories.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            long? size = null, free = null, format = null;
            Backdafuckup.Storage.RepositoryPackStats? packs = null;
            try
            {
                var open = await repositories.GetAsync(repository.Id, cancellationToken).ConfigureAwait(false);
                var stats = open.GetStorageStats();
                (size, free) = (stats.TotalBytes, stats.FreeBytes);
                if (open is Backdafuckup.Storage.LocalDirectoryRepository local)
                {
                    format = local.FormatVersion;
                    packs = local.GetPackStats();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
            {
                logger.LogDebug(ex, "Metrics: the volume of repository {Repository} ({Path}) cannot be read.", repository.Name, repository.Path);
            }

            pointsByRepository.TryGetValue(repository.Id, out var points);
            repositoryRows.Add(new StateMetrics.RepositoryRow(repository.Name, size, free, points?.Stored ?? 0, points?.Count ?? 0, format, packs));
        }

        var agents = await db.Agents.AsNoTracking().Select(a => new { a.HostName, a.LastContactUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var offlineBefore = now - options.Value.AgentOfflineAfter;

        var sqlJobs = await db.Jobs.AsNoTracking().Where(j => j.SqlBackup.Enabled).Select(j => new { j.Id, j.SqlBackup.LogIntervalMinutes })
            .ToDictionaryAsync(j => j.Id, j => j.LogIntervalMinutes, cancellationToken).ConfigureAwait(false);
        // A database dropped in the guest stays in the catalog with its last log backup: it is left out when the newest run of
        // its VM and instance saw other databases but not it (more than one interval later), so it raises no alert. When
        // the whole VM stops being backed up, its databases stay in and their age grows.
        var sqlRows = (await db.SqlDatabases.AsNoTracking().Where(d => d.JobId != null && d.LastLogUtc != null)
                .Select(d => new { d.JobId, d.VmNativeId, d.VmName, d.Instance, d.Name, d.LastLogUtc, d.LastSeenUtc }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(d => sqlJobs.ContainsKey(d.JobId!.Value))
            .ToList();
        var newestSeen = sqlRows.GroupBy(d => (d.VmNativeId, d.Instance)).ToDictionary(g => g.Key, g => g.Max(d => d.LastSeenUtc));
        var sqlLogs = sqlRows
            .Where(d => d.LastSeenUtc >= newestSeen[(d.VmNativeId, d.Instance)] - TimeSpan.FromMinutes(sqlJobs[d.JobId!.Value]))
            .Select(d => new StateMetrics.SqlLogRow(d.VmName, d.Instance, d.Name, d.LastLogUtc!.Value.ToUnixTimeSeconds(), sqlJobs[d.JobId!.Value] * 60L))
            .ToList();

        return new StateMetrics.CatalogSnapshot(
            vms,
            results,
            queued,
            pending,
            repositoryRows,
            [.. agents.Select(a => new StateMetrics.AgentRow(a.HostName, a.LastContactUtc.ToUnixTimeSeconds()))],
            agents.Count(a => a.LastContactUtc >= offlineBefore),
            agents.Count(a => a.LastContactUtc < offlineBefore),
            sqlLogs);
    }

    internal async Task ReadPoolsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, string Name)> pools;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Pools only: a Kubernetes cluster has no storages of its own to report.
            pools = (await db.Connections.AsNoTracking().Where(c => c.Kind == HypervisorKind.XenServer).Select(c => new { c.Id, c.DisplayName }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(c => (c.Id, c.DisplayName)).ToList();
        }

        state.RemovePoolsExcept(pools.Select(p => p.Id).ToHashSet());
        foreach (var (id, name) in pools)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await using var session = await connections.OpenAsync(id, cancellationToken).ConfigureAwait(false);
                var storage = await session.GetStorageStateAsync(cancellationToken).ConfigureAwait(false);
                state.SetPool(id, new StateMetrics.PoolSnapshot(name, true, watch.Elapsed.TotalSeconds, storage, time.GetUtcNow()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The storage gauges keep the last values read; bdfu_pool_poll_success shows the failure. Logged as a
                // warning when the pool stops answering, not every few minutes while it stays down.
                var previous = state.GetPool(id);
                logger.Log(previous is null or { Success: true } ? LogLevel.Warning : LogLevel.Debug,
                    "Metrics: reading the storages of pool {Pool} failed after {Seconds:0.0} s: {Error}", name, watch.Elapsed.TotalSeconds, ex.Message);
                state.SetPool(id, new StateMetrics.PoolSnapshot(name, false, watch.Elapsed.TotalSeconds, previous?.State, previous?.ReadUtc ?? time.GetUtcNow()));
            }
        }
    }
}
