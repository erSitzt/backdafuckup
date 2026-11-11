using System.Diagnostics.Metrics;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// Metrics of backups and restores of namespaces, on the server's meter (<c>Backdafuckup</c>, exported at /metrics).
/// The label <c>cluster</c> is the cluster's name in messages (its address, or its connection's name behind an agent).
/// </summary>
internal static class KubernetesMetrics
{
    private static readonly Meter Meter = new("Backdafuckup");

    /// <summary>Snapshots are taken in seconds; copies of whole volumes take up to hours.</summary>
    private static readonly double[] Buckets = [1, 2, 5, 10, 20, 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200, 14400];

    private static readonly Histogram<double> SnapshotReady = Seconds(
        "bdfu_k8s_snapshot_ready_seconds", "From creating a VolumeSnapshot of a PVC until it is ready to use, by cluster and CSI driver.");

    private static readonly Histogram<double> SnapshotLifetime = Seconds(
        "bdfu_k8s_snapshot_lifetime_seconds", "How long a VolumeSnapshot of a PVC existed, from its creation until its content was gone, by cluster and CSI driver. vSphere allows 3 per volume; keep it short.");

    private static readonly Histogram<double> MoverStart = Seconds(
        "bdfu_k8s_mover_start_seconds", "From creating a mover pod until it runs (scheduling, image pull, and the volume: a copy of a snapshot on vSphere), by cluster and mode (backup, restore, files).");

    private static readonly Counter<long> Hooks = Meter.CreateCounter<long>(
        "bdfu_k8s_hooks_total", description: "Backup hooks run in pods, by cluster, phase (pre, post) and result (ok, failed, timeout).");

    public static void RecordSnapshotReady(string cluster, string driver, TimeSpan duration) =>
        SnapshotReady.Record(duration.TotalSeconds, Tag("cluster", cluster), Tag("driver", driver));

    public static void RecordSnapshotLifetime(string cluster, string driver, TimeSpan lifetime) =>
        SnapshotLifetime.Record(lifetime.TotalSeconds, Tag("cluster", cluster), Tag("driver", driver));

    public static void RecordMoverStart(string cluster, string mode, TimeSpan duration) =>
        MoverStart.Record(duration.TotalSeconds, Tag("cluster", cluster), Tag("mode", mode));

    public static void RecordHook(string cluster, string phase, string result) =>
        Hooks.Add(1, Tag("cluster", cluster), Tag("phase", phase), Tag("result", result));

    private static Histogram<double> Seconds(string name, string description) =>
        Meter.CreateHistogram(name, "s", description, tags: null, advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = Buckets });

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
