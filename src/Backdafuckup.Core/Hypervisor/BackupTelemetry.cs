namespace Backdafuckup.Core.Hypervisor;

/// <summary>How the VM snapshot of a backup went, for metrics and the session log.</summary>
/// <param name="Duration">The snapshot call itself (Xen: <c>VM.snapshot</c>).</param>
/// <param name="GateWait">How long the snapshot waited for snapshots of other VMs on the same storages (zero: none).</param>
/// <param name="DiskCount">Disks in the snapshot (disks left out are not counted).</param>
/// <param name="Storages">Names of the storages of those disks, sorted.</param>
/// <param name="DiskSkew">
/// Time between the first and the last disk capture, when the platform snapshots disks one after another (Xen does);
/// null with fewer than two disks or when the platform does not report capture times.
/// </param>
public sealed record SnapshotFacts(TimeSpan Duration, TimeSpan GateWait, int DiskCount, IReadOnlyList<string> Storages, TimeSpan? DiskSkew)
{
    /// <summary>The storages as one metric label: "alletra-mp01" or "alletra-mp01+alletra-mp02".</summary>
    public string StorageLabel => Storages.Count == 0 ? "none" : string.Join('+', Storages);

    /// <summary>
    /// <c>snapshot</c>, or <c>checkpoint</c> for a snapshot with memory: then <see cref="Duration"/> is the VM's pause and the
    /// disks match each other (<see cref="DiskSkew"/> does not matter).
    /// </summary>
    public string Method { get; init; } = SnapshotMethods.Snapshot;

    /// <summary>
    /// The platform captured all disks at one moment (vSphere stuns the VM once for all its disks), so a crash-consistent
    /// snapshot is also consistent across the disks. Xen captures them one after another.
    /// </summary>
    public bool DisksAtOnce { get; init; }
}

/// <summary>How a VM snapshot was taken (metric label values).</summary>
public static class SnapshotMethods
{
    public const string Snapshot = "snapshot";

    /// <summary>Snapshot with memory (Xen: <c>VM.checkpoint</c>).</summary>
    public const string Checkpoint = "checkpoint";
}

/// <summary>
/// Transport events of one VM backup for metrics. The server binds the pool and the VM; the provider reports what only it
/// sees. Implementations must not throw and must not block.
/// </summary>
public interface IBackupTelemetry
{
    /// <summary>One NBD connection attempt to <paramref name="host"/> for a disk on <paramref name="storage"/>.</summary>
    /// <param name="result">One of <see cref="NbdConnectResults"/>.</param>
    void NbdConnect(string storage, string host, TimeSpan elapsed, string result);

    /// <summary>A disk was read with another transport than the preferred one.</summary>
    /// <param name="reason">A fixed code, e.g. <c>no_endpoint</c>, <c>unreachable</c>, <c>insecure_only</c>.</param>
    void TransportFallback(string storage, string from, string to, string reason);

    /// <summary>Connections to <paramref name="host"/> that broke and were opened again while a disk was read.</summary>
    void NbdReconnects(string storage, string host, int count);
}

/// <summary>Results of an NBD connection attempt (metric label values).</summary>
public static class NbdConnectResults
{
    public const string Ok = "ok";
    public const string Timeout = "timeout";
    public const string Refused = "refused";
    public const string Tls = "tls";
    public const string Error = "error";
}

/// <summary>Why a disk is read in full (metric label values; the text reason goes to the log).</summary>
public static class FullReadReasons
{
    public const string FirstBackup = "first_backup";
    public const string NoChangeData = "no_change_data";
    public const string CbtReenabled = "cbt_reenabled";
    public const string CbtDisabled = "cbt_disabled";
    public const string ChangeDataGone = "change_data_gone";
    public const string CbtError = "cbt_error";
    public const string SizeChanged = "size_changed";
    public const string IndexUnreadable = "index_unreadable";
    public const string ActiveFull = "active_full";
    public const string MemoryImage = "memory_image";

    /// <summary>The platform tracks no changes for this kind of disk at all (Kubernetes volumes): every backup reads it, no warning.</summary>
    public const string NotTracked = "not_tracked";
    public const string Other = "other";
}

/// <summary>The storages of a pool for monitoring: space, what lies on them, and snapshots left in the pool.</summary>
/// <param name="DisksWithChangeTracking">Regular VM disks with change tracking on.</param>
/// <param name="DisksWithoutChangeTracking">Regular VM disks without change tracking.</param>
public sealed record StorageState(
    IReadOnlyList<StorageUsage> Storages,
    IReadOnlyList<VmSnapshotInfo> Snapshots,
    int DisksWithChangeTracking,
    int DisksWithoutChangeTracking);

/// <summary>One storage (Xen: SR) and the disks on it by kind.</summary>
/// <param name="VirtualAllocationBytes">Sum of the virtual sizes of the disks on it (0 if the platform does not say).</param>
/// <param name="Disks">Regular disks of VMs and templates.</param>
/// <param name="SnapshotDisks">Disks of snapshots.</param>
/// <param name="ChangeTrackingMetadata">Change-tracking metadata left by backups (Xen: VDIs of type cbt_metadata).</param>
/// <param name="Unmanaged">Disks the platform manages itself (Xen: base copies of snapshot chains, managed = false).</param>
public sealed record StorageUsage(
    string Id,
    string Name,
    string Type,
    bool Shared,
    long SizeBytes,
    long UsedBytes,
    long VirtualAllocationBytes,
    int Disks,
    int SnapshotDisks,
    int ChangeTrackingMetadata,
    int Unmanaged);

/// <summary>A VM snapshot in the pool; <paramref name="Ours"/> when a backup of this product made it (tagged).</summary>
public sealed record VmSnapshotInfo(string Name, bool Ours, DateTimeOffset? TakenUtc);
