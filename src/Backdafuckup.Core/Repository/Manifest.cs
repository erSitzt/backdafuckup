using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Core.Repository;

public enum RestorePointKind
{
    Full,
    Incremental,
}

public enum DiskBackupMode
{
    Full,
    Cbt,
}

/// <summary>
/// The commit record of a restore point (<c>manifest.json</c>). A restore point exists exactly when its manifest
/// exists. Every manifest references complete chunk indexes, so each restore point is restorable on its own.
/// </summary>
public sealed record RestorePointManifest
{
    /// <summary>The newest format this version reads.</summary>
    public const int CurrentFormatVersion = FileTreeFormatVersion;

    /// <summary>Restore points without file trees: readable by every version.</summary>
    public const int BaseFormatVersion = 1;

    /// <summary>
    /// Restore points with file trees (<see cref="FileTrees"/>). Older versions refuse them, so their garbage collection
    /// and verification never miss the trees' chunks.
    /// </summary>
    public const int FileTreeFormatVersion = 2;

    /// <summary>Set at commit: <see cref="FileTreeFormatVersion"/> with file trees, else <see cref="BaseFormatVersion"/>.</summary>
    public int FormatVersion { get; init; } = BaseFormatVersion;

    public required Guid RestorePointId { get; init; }

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required RestorePointKind Kind { get; init; }

    /// <summary>"crash" or "application" (guest frozen through the agent while the snapshot was taken).</summary>
    public string Consistency { get; init; } = "crash";

    /// <summary>Why a backup with guest processing is only crash-consistent.</summary>
    public string? ConsistencyReason { get; init; }

    /// <summary>
    /// Whether the consistency is worse than the job's guest processing promises: the guest could not be frozen, a SQL Server
    /// snapshot left databases out, or the disks were captured one after another. The note of a SQL Server snapshot that
    /// recorded every database is no warning. Null in manifests from before it was recorded:
    /// <see cref="RestorePointConsistency.IsWarning"/> decides then.
    /// </summary>
    public bool? ConsistencyWarning { get; init; }

    /// <summary>What the guest contained (volumes, VSS writers, SQL databases), as reported by the agent.</summary>
    public ManifestBlob? Guest { get; init; }

    /// <summary>Number of SQL Server databases in <see cref="Guest"/>; null in manifests written before it was recorded.</summary>
    public int? SqlDatabaseCount { get; init; }

    /// <summary>
    /// The user databases of <see cref="SqlDatabaseCount"/> (not master, model, msdb, tempdb); null in manifests from before
    /// it was recorded.
    /// </summary>
    public int? SqlUserDatabaseCount { get; init; }

    /// <summary>
    /// Of <see cref="SqlUserDatabaseCount"/>, the databases that are application-consistent (VSS with SQL Server's writer, or
    /// a SQL Server snapshot that recorded them). Only valid together with <see cref="SqlUserDatabaseCount"/>.
    /// </summary>
    public int? SqlConsistentCount { get; init; }

    /// <summary>Whether the VM was a domain controller (its directory facts are in <see cref="Guest"/>); null in older manifests.</summary>
    public bool? DomainController { get; init; }

    /// <summary>The DNS name of the domain controller's domain ("" if the agent could not read it).</summary>
    public string? AdDomain { get; init; }

    /// <summary>The VSS documents of the guest (SQL Server writer metadata, Backup Components Document), if any.</summary>
    public ManifestBlob? VssDocuments { get; init; }

    /// <summary>The SQL Server snapshot (recorded databases and their <c>.bkm</c>), if the guest was frozen that way.</summary>
    public ManifestBlob? SqlSnapshot { get; init; }

    /// <summary>Informational only: the restore point whose indexes were reused for unchanged chunks.</summary>
    public Guid? BaseRestorePointId { get; init; }

    public ManifestJob? Job { get; init; }

    public required ManifestSource Source { get; init; }

    public required VmDescriptor Vm { get; init; }

    public ManifestBlob? Metadata { get; init; }

    public required ManifestChunking Chunking { get; init; }

    public required IReadOnlyList<ManifestDisk> Disks { get; init; }

    /// <summary>Disks of the VM that were deliberately not backed up; a restore of the VM creates them empty.</summary>
    public IReadOnlyList<ManifestExcludedDisk> ExcludedDisks { get; init; } = [];

    /// <summary>
    /// The RAM image of a snapshot with memory (key <c>memory</c>), stored like a disk but not a disk of the VM; null without
    /// memory. A restore that resumes the VM uploads it as the VM's suspend image.
    /// </summary>
    public ManifestDisk? Memory { get; init; }

    /// <summary>The metadata of the snapshot with memory itself (Xen: export_metadata of the checkpoint), for a resume.</summary>
    public ManifestBlob? MemoryMetadata { get; init; }

    /// <summary>
    /// The disks as <see cref="MemoryMetadata"/> names them: disk key (of <see cref="Disks"/> and <see cref="ExcludedDisks"/>)
    /// → the disk's id in the snapshot with memory. A resume maps these ids to the restored disks. Null without memory, and
    /// in restore points with memory from before resume existed (they cannot be resumed).
    /// </summary>
    public IReadOnlyDictionary<string, string>? MemoryDiskIds { get; init; }

    /// <summary>For a Kubernetes namespace (the "VM" of the restore point): the cluster, its volumes and the hooks; null for VMs.</summary>
    public KubernetesNamespaceDescriptor? Kubernetes { get; init; }

    /// <summary>
    /// Volumes backed up file by file (spec 2026-11-12-1952-kubernetes-file-level-volumes): Kubernetes volumes without CSI
    /// snapshots, and pod volumes without a PVC. Their chunks are referenced through their listings, not through
    /// <see cref="AllImages"/>.
    /// </summary>
    public IReadOnlyList<ManifestFileTree> FileTrees { get; init; } = [];

    /// <summary>The disks and, with memory, the RAM image: everything whose chunks the restore point references.</summary>
    [JsonIgnore]
    public IEnumerable<ManifestDisk> AllImages => Memory is null ? Disks : Disks.Append(Memory);
}

/// <summary>
/// A Kubernetes namespace as it was backed up. Its objects are the restore point's <see cref="RestorePointManifest.Metadata"/>
/// (one JSON object per line); its volumes with data are the disks (key = the PVC's name).
/// </summary>
public sealed record KubernetesNamespaceDescriptor
{
    /// <summary>The cluster's API address.</summary>
    public required string Cluster { get; init; }

    public required string Namespace { get; init; }

    /// <summary>
    /// The cluster-scoped objects of a whole-cluster job's run, not a namespace (<see cref="Namespace"/> is then
    /// <c>@cluster</c>): restored by a cluster restore before the run's namespaces.
    /// </summary>
    public bool ClusterScope { get; init; }

    public string? NamespaceUid { get; init; }

    public string? KubernetesVersion { get; init; }

    /// <summary>The objects in the metadata blob.</summary>
    public int ObjectCount { get; init; }

    /// <summary>Every PVC of the namespace: how its data was backed up, or why only its objects were.</summary>
    public IReadOnlyList<KubernetesVolumeRecord> Volumes { get; init; } = [];

    /// <summary>The hooks that ran in pods before and after the snapshots.</summary>
    public IReadOnlyList<KubernetesHookRecord> Hooks { get; init; } = [];

    /// <summary>
    /// The volumes of the namespace's running pods that are not PVCs (emptyDir, hostPath, NFS or CSI mounted directly): whether
    /// their data was backed up, or why not, and how much there was when usage numbers were available.
    /// </summary>
    public IReadOnlyList<KubernetesPodVolumeRecord> PodVolumes { get; init; } = [];

    /// <summary>What the running containers wrote into their own file systems (never backed up), from the usage numbers.</summary>
    public IReadOnlyList<KubernetesContainerWriteRecord> ContainerWrites { get; init; } = [];

    /// <summary>Where the usage numbers came from ("Prometheus https://…"); null: none were read.</summary>
    public string? UsageSource { get; init; }

    /// <summary>
    /// The platforms of the cluster at backup time (ids of the platform catalog): what a restore leaves out as made by them.
    /// Null in restore points from before platforms were detected.
    /// </summary>
    public IReadOnlyList<string>? Platforms { get; init; }
}

/// <param name="DataPath"><c>snapshot</c> (the data is the disk <paramref name="DiskKey"/>) or <c>objects-only</c> (see <paramref name="Reason"/>).</param>
/// <param name="DiskKey">The disk with the volume's data (read through a snapshot); null without.</param>
/// <param name="TreeKey">The file tree with the volume's files (read file by file); null without.</param>
public sealed record KubernetesVolumeRecord(
    string Claim, string DataPath, string? Reason, string? StorageClass, string? Driver, string? SnapshotClass, long SizeBytes, string VolumeMode, string? DiskKey,
    string? TreeKey = null)
{
    /// <summary>Whether the restore point holds the volume's data (as a disk or as files), not only its objects.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasData => DiskKey is not null || TreeKey is not null;
}

/// <summary>A volume of a running pod that is not a PVC.</summary>
/// <param name="Workload">The pod's controller ("Deployment web"), or "Pod name" without one.</param>
/// <param name="Kind"><c>emptyDir</c>, <c>emptyDir-memory</c> (tmpfs), <c>hostPath</c>, <c>nfs</c>, <c>csi</c> or another volume type mounted directly.</param>
/// <param name="Source">Where it lives: the hostPath's path, the NFS export, the CSI driver; null for an emptyDir.</param>
/// <param name="OptIn">Why its data was asked for: <c>job</c> (the job's switch), <c>annotation</c> (<c>backup.velero.io/backup-volumes</c>); null: not asked for.</param>
/// <param name="Platform">The platform that puts it into pods (a sidecar's volume, not the workload's data), or null.</param>
/// <param name="UsedBytes">Its data from the usage numbers; null: not known.</param>
/// <param name="SizeLimitBytes">An emptyDir's sizeLimit; null: none.</param>
/// <param name="TreeKey">The file tree with its files; null: not backed up (see <paramref name="Reason"/>).</param>
public sealed record KubernetesPodVolumeRecord(
    string Pod, string Workload, string Volume, string Kind, string? Source, string? OptIn, string? Platform, long? UsedBytes, long? SizeLimitBytes, string? TreeKey, string? Reason);

/// <summary>A running container and what it wrote into its own file system (its writable layer), from the usage numbers.</summary>
/// <param name="LayerBytes">The data in its writable layer; null: not known.</param>
/// <param name="WriteBytesPerSecond">How fast it writes to local disks over the last hour (its writable layer, emptyDirs, block PVCs); null: not known.</param>
public sealed record KubernetesContainerWriteRecord(string Pod, string Workload, string Container, long? LayerBytes, double? WriteBytesPerSecond);

/// <param name="Phase"><c>pre</c> (before the snapshots) or <c>post</c>.</param>
public sealed record KubernetesHookRecord(string Phase, string Pod, string Container, string Command, int? ExitCode, string? Error, double Seconds);

/// <summary>A disk that was left out of the backup (e.g. marked [NOBAK]): what is needed to create it empty again.</summary>
public sealed record ManifestExcludedDisk(
    string Key,
    string SourceDiskId,
    string Name,
    long CapacityBytes,
    bool Bootable,
    string? StorageId,
    string? StorageName);

public sealed record ManifestJob(Guid Id, string Name);

public sealed record ManifestSource(
    HypervisorKind Provider,
    string PlatformId,
    string VmId,
    string VmName,
    string? SnapshotId);

public sealed record ManifestBlob(string File, string Sha256, long Length, string Format);

public sealed record ManifestChunking(int ChunkSize, string Hash, string Compression);

public sealed record ManifestDisk
{
    public required string Key { get; init; }

    public required string SourceDiskId { get; init; }

    public required string Name { get; init; }

    public required long CapacityBytes { get; init; }

    public bool Bootable { get; init; }

    public string? StorageId { get; init; }

    public string? StorageName { get; init; }

    public required ManifestIndexRef Index { get; init; }

    public ChangeToken? ChangeToken { get; init; }

    public required DiskBackupStats Stats { get; init; }
}

public sealed record ManifestIndexRef(string File, string Sha256, long ChunkCount);

/// <summary>A volume backed up file by file: its root listing (<c>tree-&lt;key&gt;.idx</c>), the root folder's own meta, and counts.</summary>
public sealed record ManifestFileTree
{
    /// <summary>The volume: a PVC's name, or <c>pod/&lt;pod&gt;/&lt;volume&gt;</c> for a pod volume without a PVC.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>"pvc" or "pod-volume".</summary>
    public string Kind { get; init; } = "pvc";

    /// <summary>How it was read: "shadow-pv" (a read-only copy of its PV) or "node-agent".</summary>
    public string Reader { get; init; } = "shadow-pv";

    /// <summary>The root listing's chunks (its length is the index's size).</summary>
    public required ManifestIndexRef Index { get; init; }

    public required long RootLength { get; init; }

    public FileTreeRootMeta Root { get; init; } = new(0755, 0, 0, 0);

    public FileTreeStats Stats { get; init; } = new();

    /// <summary>The restore point whose tree unchanged files were taken from (their chunks, not the restore point itself).</summary>
    public Guid? BaseRestorePointId { get; init; }
}

/// <summary>The root folder of a file tree: mode, owner and modification time (nanoseconds since 1970).</summary>
public sealed record FileTreeRootMeta(int Mode, long Uid, long Gid, long MtimeNs);

public sealed record FileTreeStats
{
    public long Files { get; init; }

    public long Directories { get; init; }

    public long Bytes { get; init; }

    /// <summary>Entries not read (permission denied, vanished, …).</summary>
    public long Issues { get; init; }

    public long ChangedWhileRead { get; init; }

    /// <summary>Files taken from the base tree without reading them (unchanged).</summary>
    public long UnchangedFiles { get; init; }

    /// <summary>The content bytes of those files.</summary>
    public long UnchangedBytes { get; init; }

    /// <summary>Bytes of file content read in this run.</summary>
    public long ReadBytes { get; init; }

    /// <summary>Bytes the repository stored new for this tree (compressed; chunks it had already are not counted).</summary>
    public long NewStoredBytes { get; init; }
}

public sealed record DiskBackupStats
{
    public DiskBackupMode Mode { get; init; }

    public string? FullReason { get; init; }

    public long ReadBytes { get; init; }

    public long ChangedChunks { get; init; }

    public long NewChunks { get; init; }

    public long NewStoredBytes { get; init; }

    public long ZeroChunks { get; init; }

    public long UnchangedChunks { get; init; }

    /// <summary>Transport the disk was read with ("nbd", "http-export-vhd", ...); null in restore points of older versions.</summary>
    public string? Transport { get; init; }

    /// <summary>Non-zero chunks that were already in the repository (or identical to the previous restore point).</summary>
    public long DedupedChunks { get; init; }

    /// <summary>Uncompressed size of the chunks stored new (compression ratio = NewDataBytes / NewStoredBytes).</summary>
    public long NewDataBytes { get; init; }

    /// <summary>Time the disk transfer took.</summary>
    public double DurationSeconds { get; init; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RestorePointManifest))]
[JsonSerializable(typeof(RepositoryDescriptor))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>Contents of <c>repo.json</c>, which makes a repository self-describing.</summary>
public sealed record RepositoryDescriptor
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required int ChunkSize { get; init; }

    public string Hash { get; init; } = "sha256";

    public required RepositoryCompression Compression { get; init; }
}

public sealed record RepositoryCompression(string Codec, int Level);
