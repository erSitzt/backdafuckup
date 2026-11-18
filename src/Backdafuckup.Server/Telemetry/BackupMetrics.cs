using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.DataMover;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.Telemetry;

/// <summary>
/// The server's metrics for what happens: snapshots, disks, transports, sessions. Recorded when it happens; the state of
/// catalog and pools is in <see cref="StateMetrics"/>. Exported at <c>/metrics</c> (Prometheus) under the meter
/// <see cref="MeterName"/>.
/// </summary>
public sealed class BackupMetrics
{
    public const string MeterName = "Backdafuckup";

    /// <summary>Snapshot and wait times: 10 s is the limit of a VSS freeze.</summary>
    internal static readonly double[] SnapshotBuckets = [1, 2, 5, 8, 10, 15, 20, 30, 45, 60, 90, 120, 300];

    internal static readonly double[] SessionBuckets = [10, 30, 60, 120, 300, 600, 1800, 3600, 7200, 14400, 28800, 86400];

    internal static readonly double[] ConnectBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 20];

    private readonly Histogram<double> _snapshotDuration;
    private readonly Histogram<double> _gateWait;
    private readonly Histogram<double> _diskSkew;
    private readonly Histogram<double> _release;
    private readonly Histogram<double> _writesHeld;
    private readonly Histogram<double> _freeze;
    private readonly Counter<long> _snapshots;
    private readonly Counter<long> _tapeWritten;
    private readonly Counter<long> _moveBytes;
    private readonly Counter<long> _tapeRead;
    private readonly Counter<long> _diskBytes;
    private readonly Counter<double> _diskReadSeconds;
    private readonly Counter<long> _fullReads;
    private readonly Counter<long> _fallbacks;
    private readonly Histogram<double> _nbdConnect;
    private readonly Histogram<double> _hotAdd;
    private readonly Counter<long> _nbdReconnects;
    private readonly Counter<long> _sessions;
    private readonly Histogram<double> _sessionDuration;
    private readonly Counter<long> _vmBackups;
    private readonly Counter<long> _restoredBytes;
    private readonly Counter<long> _repackedBytes;
    private readonly Counter<long> _packedLoose;
    private readonly ConcurrentDictionary<(string Pool, string Vm), VmLast> _last = new();

    public BackupMetrics(IMeterFactory meterFactory, RunningSessions running)
    {
        var meter = meterFactory.Create(MeterName);
        _snapshotDuration = Seconds(meter, "bdfu_snapshot_duration_seconds", "VM snapshot call by pool, storage, number of disks and kind (snapshot; checkpoint = snapshot with memory, the VM's pause).", SnapshotBuckets);
        _gateWait = Seconds(meter, "bdfu_snapshot_gate_wait_seconds", "Wait for snapshots of other VMs on the same storage before the snapshot (one at a time per storage).", SnapshotBuckets);
        _diskSkew = Seconds(meter, "bdfu_snapshot_disk_skew_seconds", "Time between the first and the last disk capture of a VM snapshot (VMs with two or more disks).", SnapshotBuckets);
        _release = Seconds(meter, "bdfu_snapshot_release_seconds", "Removing the snapshot after the backup (VM.destroy and VDI.data_destroy).", SnapshotBuckets);
        _writesHeld = Seconds(meter, "bdfu_guest_writes_held_seconds", "How long the guest held its writes for the snapshot (VSS freeze or SQL Server suspend).", SnapshotBuckets);
        _freeze = Seconds(meter, "bdfu_guest_freeze_seconds", "From the freeze request until the guest held its writes.", SnapshotBuckets);
        _snapshots = meter.CreateCounter<long>("bdfu_snapshots_total", description: "VM snapshots for backups, by consistency and guest processing method.");
        _diskBytes = meter.CreateCounter<long>("bdfu_backup_disk_bytes_total", "By", "Disk bytes of backups by transport and category (read, unchanged, zero, deduped, new, stored).");
        _diskReadSeconds = meter.CreateCounter<double>("bdfu_backup_disk_read_seconds_total", "s", "Time spent reading disks, by transport; divide the read bytes by it for the throughput.");
        _fullReads = meter.CreateCounter<long>("bdfu_backup_full_reads_total", description: "Disks read in full, by reason.");
        _fallbacks = meter.CreateCounter<long>("bdfu_transport_fallbacks_total", description: "Disks read with another transport than the preferred one (e.g. HTTP export instead of NBD), by reason.");
        _nbdConnect = Seconds(meter, "bdfu_nbd_connect_seconds", "NBD connection attempts by host and result (ok, timeout, refused, tls, error).", ConnectBuckets);
        _nbdReconnects = meter.CreateCounter<long>("bdfu_nbd_reconnects_total", description: "NBD connections that broke and were opened again while a disk was read.");
        _hotAdd = Seconds(meter, "bdfu_hotadd_attach_seconds", "vSphere HotAdd: attaching a snapshot's disk to a backup proxy VM, by datastore and proxy.", ConnectBuckets);
        _sessions = meter.CreateCounter<long>("bdfu_sessions_total", description: "Finished sessions by kind and result.");
        _sessionDuration = Seconds(meter, "bdfu_session_duration_seconds", "Duration of finished sessions by kind and result.", SessionBuckets);
        _vmBackups = meter.CreateCounter<long>("bdfu_vm_backups_total", description: "Backups of single VMs (each attempt) by job and result.");
        _restoredBytes = meter.CreateCounter<long>("bdfu_restore_bytes_total", "By", "Bytes written by VM restores.");
        _repackedBytes = meter.CreateCounter<long>("bdfu_repository_repacked_bytes_total", "By", "Bytes of chunks still in use that garbage collection copied out of sparse packs.");
        _packedLoose = meter.CreateCounter<long>("bdfu_repository_packed_loose_chunks_total", description: "Loose chunks of format 1 moved into packs after a conversion.");
        _tapeWritten = meter.CreateCounter<long>("bdfu_tape_written_bytes_total", "By", "Bytes written to tape (blocks with their headers), by media pool.");
        _moveBytes = meter.CreateCounter<long>("bdfu_repository_move_bytes_total", "By", "Bytes copied between the extents of a scale-out repository by moves, by scale-out repository.");
        _tapeRead = meter.CreateCounter<long>("bdfu_tape_read_bytes_total", "By", "Bytes of chunks brought back from tape, by media pool.");

        meter.CreateObservableGauge("bdfu_sessions_running", () =>
            Enum.GetValues<SessionKind>().Select(kind => new Measurement<int>(running.Count(kind), new KeyValuePair<string, object?>("kind", Kind(kind)))),
            description: "Sessions running now, by kind.");
        meter.CreateObservableGauge("bdfu_vm_last_snapshot_seconds", () => LastValues(l => l.SnapshotSeconds),
            description: "Duration of the VM's last snapshot (since the server started).");
        meter.CreateObservableGauge("bdfu_vm_last_disk_skew_seconds", () => LastValues(l => l.SkewSeconds),
            description: "Gap between the first and the last disk capture in the VM's last snapshot (since the server started).");
        meter.CreateObservableGauge("bdfu_vm_last_writes_held_seconds", () => LastValues(l => l.HeldSeconds),
            description: "How long the guest held its writes for the VM's last snapshot (since the server started).");
    }

    private static Histogram<double> Seconds(Meter meter, string name, string description, double[] buckets) =>
        meter.CreateHistogram(name, "s", description, tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = buckets });

    /// <summary>Transport events of one VM backup on <paramref name="pool"/>.</summary>
    public IBackupTelemetry ForPool(string pool) => new PoolTelemetry(this, pool);

    /// <summary>The snapshot of a VM backup: duration, wait, skew, guest freeze, consistency.</summary>
    public void RecordSnapshot(string pool, string vm, SnapshotFacts facts, GuestFreezeResult? freeze, TimeSpan? sqlSuspended, string consistency)
    {
        var storage = facts.StorageLabel;
        _snapshotDuration.Record(facts.Duration.TotalSeconds, Tag("pool", pool), Tag("storage", storage), Tag("disks", facts.DiskCount.ToString(System.Globalization.CultureInfo.InvariantCulture)), Tag("kind", facts.Method));
        _gateWait.Record(facts.GateWait.TotalSeconds, Tag("pool", pool), Tag("storage", storage));
        if (facts.DiskSkew is { } skew)
        {
            _diskSkew.Record(skew.TotalSeconds, Tag("pool", pool), Tag("storage", storage));
        }

        var method = freeze is null ? "none" : freeze.Sql is not null ? "sql" : "vss";
        double? held = null;
        if (method == "sql" && sqlSuspended is { } suspended)
        {
            held = suspended.TotalSeconds;
        }
        else if (method == "vss" && freeze!.HeldTime > TimeSpan.Zero)
        {
            held = freeze.HeldTime.TotalSeconds;
        }

        if (held is { } h)
        {
            _writesHeld.Record(h, Tag("method", method));
        }

        if (freeze is not null && freeze.FreezeTime > TimeSpan.Zero)
        {
            _freeze.Record(freeze.FreezeTime.TotalSeconds, Tag("method", method));
        }

        _snapshots.Add(1, Tag("pool", pool), Tag("storage", storage), Tag("consistency", consistency), Tag("method", method));
        _last[(pool, vm)] = new VmLast(facts.Duration.TotalSeconds, facts.DiskSkew?.TotalSeconds, held);
    }

    public void RecordRelease(string pool, SnapshotFacts facts, TimeSpan release) =>
        _release.Record(release.TotalSeconds, Tag("pool", pool), Tag("storage", facts.StorageLabel));

    /// <summary>One disk backed up.</summary>
    /// <param name="image"><c>disk</c>, or <c>memory</c> for the RAM image of a snapshot with memory.</param>
    public void RecordDisk(string pool, string storage, string transport, DiskBackupCounters counters, string image, string? fullReason)
    {
        var tags = new[] { Tag("pool", pool), Tag("storage", storage), Tag("transport", transport), Tag("image", image) };
        void Add(string category, long bytes)
        {
            if (bytes > 0)
            {
                _diskBytes.Add(bytes, [.. tags, Tag("category", category)]);
            }
        }

        Add("read", counters.ReadBytes);
        Add("unchanged", counters.UnchangedBytes);
        Add("zero", counters.ZeroBytes);
        Add("deduped", counters.DedupedBytes);
        Add("new", counters.NewDataBytes);
        Add("stored", counters.StoredBytes);
        _diskReadSeconds.Add(counters.Elapsed.TotalSeconds, tags);
        if (fullReason is not null)
        {
            _fullReads.Add(1, Tag("pool", pool), Tag("reason", fullReason));
        }
    }

    public void RecordVmBackup(string job, SessionState result) => _vmBackups.Add(1, Tag("backup_job", job), Tag("result", Result(result)));

    public void RecordSession(SessionKind kind, SessionState result, TimeSpan duration)
    {
        _sessions.Add(1, Tag("kind", Kind(kind)), Tag("result", Result(result)));
        _sessionDuration.Record(duration.TotalSeconds, Tag("kind", Kind(kind)), Tag("result", Result(result)));
    }

    /// <summary>Bytes a move between the extents of a scale-out repository copied.</summary>
    public void RecordMove(string scaleOut, long bytes)
    {
        if (bytes > 0)
        {
            _moveBytes.Add(bytes, new KeyValuePair<string, object?>("scale_out", scaleOut));
        }
    }

    public void RecordTape(string pool, long writtenBytes, long readBytes)
    {
        if (writtenBytes > 0)
        {
            _tapeWritten.Add(writtenBytes, Tag("pool", pool));
        }

        if (readBytes > 0)
        {
            _tapeRead.Add(readBytes, Tag("pool", pool));
        }
    }

    public void RecordPacks(string repository, long repackedBytes, long packedLoose)
    {
        if (repackedBytes > 0)
        {
            _repackedBytes.Add(repackedBytes, Tag("repository", repository));
        }

        if (packedLoose > 0)
        {
            _packedLoose.Add(packedLoose, Tag("repository", repository));
        }
    }

    public void RecordRestoredBytes(string pool, long bytes)
    {
        if (bytes > 0)
        {
            _restoredBytes.Add(bytes, Tag("pool", pool));
        }
    }

    internal static string Kind(SessionKind kind) => kind switch
    {
        SessionKind.Backup => "backup",
        SessionKind.Restore => "restore",
        SessionKind.RepositoryMaintenance => "maintenance",
        SessionKind.AgentDeployment => "agent_deployment",
        SessionKind.FileBrowse => "file_browse",
        SessionKind.FileRestore => "file_restore",
        SessionKind.SqlPublish => "sql_publish",
        SessionKind.SqlRestore => "sql_restore",
        SessionKind.SqlExport => "sql_export",
        SessionKind.TapeMaintenance => "tape_maintenance",
        SessionKind.TapeBackup => "tape_backup",
        SessionKind.TapeRestore => "tape_restore",
        SessionKind.RepositoryMove => "repository_move",
        _ => kind.ToString().ToLowerInvariant(),
    };

    internal static string Result(SessionState state) => state.ToString().ToLowerInvariant();

    private IEnumerable<Measurement<double>> LastValues(Func<VmLast, double?> value)
    {
        foreach (var ((pool, vm), last) in _last)
        {
            if (value(last) is { } v)
            {
                yield return new Measurement<double>(v, Tag("pool", pool), Tag("vm", vm));
            }
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);

    private sealed record VmLast(double SnapshotSeconds, double? SkewSeconds, double? HeldSeconds);

    private sealed class PoolTelemetry(BackupMetrics metrics, string pool) : IBackupTelemetry
    {
        public void NbdConnect(string storage, string host, TimeSpan elapsed, string result) =>
            metrics._nbdConnect.Record(elapsed.TotalSeconds, Tag("pool", pool), Tag("host", host), Tag("result", result));

        public void HotAdd(string storage, string proxy, TimeSpan elapsed) =>
            metrics._hotAdd.Record(elapsed.TotalSeconds, Tag("pool", pool), Tag("storage", storage), Tag("proxy", proxy));

        public void TransportFallback(string storage, string from, string to, string reason) =>
            metrics._fallbacks.Add(1, Tag("pool", pool), Tag("storage", storage), Tag("from", from), Tag("to", to), Tag("reason", reason));

        public void NbdReconnects(string storage, string host, int count) =>
            metrics._nbdReconnects.Add(count, Tag("pool", pool), Tag("host", host));
    }
}
