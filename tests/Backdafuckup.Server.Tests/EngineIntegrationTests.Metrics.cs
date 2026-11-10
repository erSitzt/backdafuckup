using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backdafuckup.Server.Tests;

public sealed partial class EngineIntegrationTests
{
    [Fact]
    public async Task A_backup_records_snapshot_disk_transport_session_and_pool_metrics()
    {
        using var metrics = new MetricsRecorder();
        var (vmUuid, _) = _pool.AddVm("metrics-vm", DiskSize, DiskSize / 2);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        string pool, job;
        await using (var db = await DbAsync())
        {
            var entity = await db.Jobs.Include(j => j.Connection).AsNoTracking().FirstAsync(j => j.Id == jobId, Ct);
            (pool, job) = (entity.Connection.DisplayName, entity.Name);
        }

        var queue = Services.GetRequiredService<SessionQueue>();
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);

        // The snapshot: two disks, captured 3 s apart (the fake pool's disk interval), and the release afterwards.
        var snapshot = Assert.Single(metrics.Of("bdfu_snapshot_duration_seconds", pool));
        Assert.Equal("2", snapshot.Tags["disks"]);
        Assert.Equal(3, Assert.Single(metrics.Of("bdfu_snapshot_disk_skew_seconds", pool)).Value);
        Assert.Single(metrics.Of("bdfu_snapshot_gate_wait_seconds", pool));
        Assert.Single(metrics.Of("bdfu_snapshot_release_seconds", pool));
        Assert.Equal("crash", Assert.Single(metrics.Of("bdfu_snapshots_total", pool)).Tags["consistency"]);

        // Not application-consistent with two disks: the restore point says the disks were captured one after another.
        await using (var db = await DbAsync())
        {
            var point = await db.RestorePoints.AsNoTracking().SingleAsync(r => r.JobId == jobId, Ct);
            Assert.Equal("crash", point.Consistency);
            Assert.Contains("The 2 disks were captured one after another over 3 s", point.ConsistencyReason, StringComparison.Ordinal);
        }

        // The disks: read over NBD in full, as the first backup.
        Assert.Equal(DiskSize + DiskSize / 2, metrics.Of("bdfu_backup_disk_bytes_total", pool).Where(m => (string?)m.Tags["category"] == "read" && (string?)m.Tags["transport"] == "nbd").Sum(m => m.Value));
        Assert.All(metrics.Of("bdfu_backup_full_reads_total", pool), m => Assert.Equal(FullReadReasons.FirstBackup, m.Tags["reason"]));
        Assert.Equal(2, metrics.Of("bdfu_backup_full_reads_total", pool).Count);
        Assert.All(metrics.Of("bdfu_nbd_connect_seconds", pool), m => Assert.Equal(NbdConnectResults.Ok, m.Tags["result"]));
        Assert.Contains(metrics.All("bdfu_vm_backups_total"), m => (string?)m.Tags["backup_job"] == job && (string?)m.Tags["result"] == "succeeded");
        Assert.Contains(metrics.All("bdfu_sessions_total"), m => (string?)m.Tags["kind"] == "backup" && (string?)m.Tags["result"] == "succeeded");

        // Without NBD the disks fall back to the HTTP export, and that is counted.
        _pool.NbdEnabled = false;
        Assert.Contains((await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State, new[] { SessionState.Succeeded, SessionState.Warning });
        var fallbacks = metrics.Of("bdfu_transport_fallbacks_total", pool);
        Assert.Equal(2, fallbacks.Count);
        Assert.All(fallbacks, m => Assert.Equal("no_endpoint", m.Tags["reason"]));
        Assert.Contains(metrics.Of("bdfu_backup_disk_bytes_total", pool), m => ((string?)m.Tags["transport"])!.StartsWith("http-export", StringComparison.Ordinal));

        // The state gauges: the catalog (VM rows of the job) and the pool (disks by kind on the storage).
        var service = Services.GetServices<IHostedService>().OfType<MetricsStateService>().Single();
        var state = Services.GetRequiredService<StateMetrics>();
        state.SetCatalog(await service.ReadCatalogAsync(Ct));
        await service.ReadPoolsAsync(Ct);
        metrics.Observe();
        Assert.Equal(2, Assert.Single(metrics.All("bdfu_vm_restore_points"), m => (string?)m.Tags["backup_job"] == job).Value);
        Assert.Equal(0, Assert.Single(metrics.All("bdfu_vm_no_restore_point"), m => (string?)m.Tags["backup_job"] == job).Value);
        Assert.Contains(metrics.All("bdfu_vm_last_backup_result"), m => (string?)m.Tags["backup_job"] == job);
        Assert.Equal(1, Assert.Single(metrics.Of("bdfu_pool_poll_success", pool)).Value);
        Assert.Equal(2, metrics.Of("bdfu_storage_vdis", pool).Where(m => (string?)m.Tags["kind"] == "cbt_metadata").Sum(m => m.Value));
        Assert.Contains(metrics.Of("bdfu_storage_size_bytes", pool), m => m.Value > 0);
        Assert.Equal(0, Assert.Single(metrics.Of("bdfu_pool_snapshots", pool), m => (string?)m.Tags["owner"] == "backdafuckup").Value);
        Assert.Contains(metrics.Of("bdfu_vm_last_disk_skew_seconds", pool), m => m.Value == 3);
    }

    [Fact]
    public async Task Sql_log_gauges_leave_out_databases_the_newest_run_no_longer_saw()
    {
        var (vmUuid, _) = _pool.AddVm("sql-vm", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        var now = DateTimeOffset.UtcNow;
        await using (var db = await DbAsync())
        {
            var job = await db.Jobs.FirstAsync(j => j.Id == jobId, Ct);
            job.SqlBackup.Enabled = true;
            job.SqlBackup.LogIntervalMinutes = 15;
            SqlDatabaseEntity Row(string name, DateTimeOffset lastLog, DateTimeOffset lastSeen) => new()
            {
                PlatformId = "pool", VmNativeId = vmUuid, VmName = "sql-vm", Instance = "SQLEXPRESS", Name = name, JobId = jobId,
                LastLogUtc = lastLog, LastSeenUtc = lastSeen, FirstSeenUtc = now.AddDays(-3),
            };
            db.SqlDatabases.AddRange(
                Row("current", now.AddMinutes(-5), now.AddMinutes(-5)),
                Row("late", now.AddHours(-3), now.AddMinutes(-5)),
                Row("dropped", now.AddDays(-1), now.AddDays(-1)));
            await db.SaveChangesAsync(Ct);
        }

        var service = Services.GetServices<IHostedService>().OfType<MetricsStateService>().Single();
        var logs = (await service.ReadCatalogAsync(Ct)).SqlLogs.Where(l => l.Vm == "sql-vm").ToList();
        Assert.Equal(["current", "late"], logs.Select(l => l.Database).Order(StringComparer.Ordinal));
        Assert.All(logs, l => Assert.Equal(900, l.IntervalSeconds));
    }

    /// <summary>Collects the measurements of the server's meter (all hosts in the process; filter by pool or job).</summary>
    private sealed class MetricsRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> _all = new();

        public MetricsRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BackupMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Add(i, v, t));
            _listener.SetMeasurementEventCallback<int>((i, v, t, _) => Add(i, v, t));
            _listener.Start();
        }

        /// <summary>Reads the observable gauges once.</summary>
        public void Observe() => _listener.RecordObservableInstruments();

        public List<(string Name, double Value, Dictionary<string, object?> Tags)> All(string name) => _all.Where(m => m.Name == name).ToList();

        public List<(string Name, double Value, Dictionary<string, object?> Tags)> Of(string name, string pool) =>
            All(name).Where(m => m.Tags.TryGetValue("pool", out var p) && (string?)p == pool).ToList();

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            _all.Enqueue((instrument.Name, value, map));
        }
    }
}
