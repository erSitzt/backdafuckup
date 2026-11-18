namespace Backdafuckup.Contracts;

public enum RestoreModeDto
{
    OriginalLocation,
    AlternateLocation,
}

/// <summary>Request to restore a whole VM from a restore point.</summary>
public sealed record RestoreRequestDto
{
    public required Guid RestorePointId { get; init; }

    public RestoreModeDto Mode { get; init; } = RestoreModeDto.AlternateLocation;

    /// <summary>Target pool (connection). Defaults to the connection the restore point came from.</summary>
    public Guid? TargetConnectionId { get; init; }

    public string? VmName { get; init; }

    public string? TargetHostId { get; init; }

    /// <summary>
    /// Original location: write back into the existing VM only what changed since the restore point (vSphere, needs
    /// changed block tracking); much faster than a full restore of large disks.
    /// </summary>
    public bool QuickRollback { get; init; }

    /// <summary>
    /// Whether the restored VM's NICs start connected. Null: automatic, i.e. a copy whose original still exists starts with
    /// its NICs disconnected, so that it does not come up with the original's addresses (vSphere).
    /// </summary>
    public bool? ConnectNics { get; init; }

    /// <summary>Quick rollback: keep the safety snapshot (the VM before the rollback) instead of removing it.</summary>
    public bool KeepSafetySnapshot { get; init; }

    /// <summary>vSphere: the VM folder (managed object id, e.g. group-v123). Null: where the backed-up VM was.</summary>
    public string? TargetFolderId { get; init; }

    /// <summary>vSphere: the resource pool (e.g. resgroup-12). Null: the backed-up VM's, else the target host's cluster.</summary>
    public string? TargetResourcePoolId { get; init; }

    /// <summary>Target storage per disk key. Missing disks go to their original storage.</summary>
    public Dictionary<string, string> DiskStorage { get; init; } = [];

    /// <summary>Target network per NIC device.</summary>
    public Dictionary<string, string> NetworkMap { get; init; } = [];

    public bool RegenerateMacs { get; init; } = true;

    public bool PowerOn { get; init; }

    public bool ReplaceExisting { get; init; }

    public bool DeleteReplaced { get; init; }

    /// <summary>
    /// Restore point with memory: resume the VM where it was at the backup instead of booting it from its disks. Only in the
    /// pool the restore point came from; a copy (another location) resumes without network connections.
    /// </summary>
    public bool ResumeWithMemory { get; init; }

    /// <summary>A restore point of a Kubernetes namespace: where and what to restore. <see cref="TargetConnectionId"/> is the cluster.</summary>
    public KubernetesRestoreDto? Kubernetes { get; init; }

    /// <summary>
    /// A whole-cluster restore: <see cref="RestorePointId"/> is the restore point of a run's cluster-scoped objects; they
    /// come back first, then the chosen namespaces of the same run, each as its own task.
    /// </summary>
    public KubernetesClusterRestoreDto? KubernetesCluster { get; init; }
}

/// <summary>What a whole-cluster restore brings back of a run (see <see cref="RestoreRequestDto.KubernetesCluster"/>).</summary>
public sealed record KubernetesClusterRestoreDto
{
    /// <summary>The namespaces of the run to restore; null: all of them.</summary>
    public List<string>? Namespaces { get; init; }

    public Dictionary<string, string> StorageClassMap { get; init; } = [];

    /// <summary>Objects that exist (namespaced and cluster-scoped) are replaced; otherwise left as they are.</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>
/// The restore of a whole-cluster job's run into a cluster (<c>POST /api/kubernetes/cluster-restores</c>): its cluster-scoped
/// objects, then its namespaces.
/// </summary>
public sealed record ClusterRestoreRequestDto
{
    /// <summary>The run: the backup session of the whole-cluster job.</summary>
    public required Guid BackupSessionId { get; init; }

    /// <summary>The target cluster; empty: the one backed up.</summary>
    public Guid? TargetConnectionId { get; init; }

    public List<string>? Namespaces { get; init; }

    public Dictionary<string, string> StorageClassMap { get; init; } = [];

    public bool ReplaceExisting { get; init; }
}

/// <summary>A run of a whole-cluster job, as the cluster restore offers it.</summary>
public sealed record ClusterRunDto(
    Guid BackupSessionId, string JobName, Guid? ConnectionId, string Cluster, DateTimeOffset CreatedUtc, Guid ClusterPointId, int ClusterObjects,
    IReadOnlyList<ClusterRunNamespaceDto> Namespaces);

/// <param name="PointId">The namespace's restore point of the run (or, when it failed in the run, its newest older one; see <paramref name="FromRun"/>).</param>
public sealed record ClusterRunNamespaceDto(string Namespace, Guid PointId, bool FromRun, DateTimeOffset CreatedUtc, int Volumes, int VolumesWithData, IReadOnlyList<string> StorageClasses);

/// <summary>
/// The restore of a namespace: the whole namespace (into the original or another namespace), or single PVCs with their data
/// (<see cref="OnlyClaims"/>), e.g. as new PVCs next to the originals (<see cref="ClaimNames"/>).
/// </summary>
public sealed record KubernetesRestoreDto
{
    /// <summary>Empty: the original namespace.</summary>
    public string? TargetNamespace { get; init; }

    /// <summary>Objects that exist are updated to their state in the restore point; otherwise they are left as they are.</summary>
    public bool ReplaceExisting { get; init; }

    public List<string>? OnlyClaims { get; init; }

    /// <summary>With <see cref="OnlyClaims"/>: replace the PVCs of their names (their workloads are stopped for the swap; their current data is deleted).</summary>
    public bool ReplaceClaims { get; init; }

    /// <summary>Only these objects (<c>Kind/name</c>) and what they need (ConfigMaps, Secrets, PVCs, Services …). Null: the whole namespace.</summary>
    public List<string>? Objects { get; init; }

    /// <summary>Restore <see cref="Objects"/> exactly, without what their workloads need.</summary>
    public bool ExactObjects { get; init; }

    public Dictionary<string, string> ClaimNames { get; init; } = [];

    /// <summary>
    /// StorageClasses of the restore point → classes of the target cluster (needed when the target lacks a class, e.g.
    /// another cluster). A volume whose target class has a VolumeSnapshotClass goes in as a block device, any other as files.
    /// </summary>
    public Dictionary<string, string> StorageClassMap { get; init; } = [];
}

/// <summary>The preview of a namespace restore into a cluster (<c>POST /api/restore-points/{id}/kubernetes/restore-plan</c>).</summary>
public sealed record KubernetesRestorePlanRequestDto
{
    /// <summary>The target cluster; empty: the restore point's.</summary>
    public Guid? TargetConnectionId { get; init; }

    /// <summary>Empty: the original namespace.</summary>
    public string? TargetNamespace { get; init; }

    public Dictionary<string, string> StorageClassMap { get; init; } = [];

    /// <summary>As the restore's: only these objects (<c>Kind/name</c>) and what they need. Null: the whole namespace.</summary>
    public List<string>? Objects { get; init; }

    /// <summary>As the restore's: only these PVCs.</summary>
    public List<string>? OnlyClaims { get; init; }
}

/// <summary>How a restore would go: each PVC's class and data path in the target, what to look at first, and what stops it.</summary>
public sealed record KubernetesRestorePlanDto(
    Guid TargetConnectionId,
    string Cluster,
    string TargetNamespace,
    bool OtherCluster,
    bool NamespaceExists,
    IReadOnlyList<string> StorageClasses,
    string? DefaultStorageClass,
    IReadOnlyList<KubernetesVolumePlanDto> Volumes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <param name="Path"><c>block</c> (copied as a device, snapshotted with the target's class), <c>files</c> (read from its file system),
/// <c>objects</c> (its data is not in the restore point) or <c>missing</c> (the target has no such class).</param>
public sealed record KubernetesVolumePlanDto(string Claim, string? SourceStorageClass, string? TargetStorageClass, string Path, string Reason, long SizeBytes);

/// <summary>What a restore point of a namespace holds: the cluster, its volumes and its objects.</summary>
public sealed record KubernetesRestorePointDto(
    string Cluster,
    string Namespace,
    string? KubernetesVersion,
    IReadOnlyList<KubernetesVolumeDto> Volumes,
    IReadOnlyList<KubernetesObjectDto> Objects,
    IReadOnlyList<KubernetesHookDto> Hooks,
    IReadOnlyList<KubernetesPodVolumeDto>? PodVolumes = null,
    IReadOnlyList<KubernetesContainerWriteDto>? ContainerWrites = null,
    string? UsageSource = null);

/// <summary>A volume of a pod that is not a PVC, as the backup found it.</summary>
/// <param name="OptIn"><c>job</c> or <c>annotation</c> when its data was asked for; null: not.</param>
/// <param name="Platform">The platform that put it into the pod, or whose namespace it is in.</param>
/// <param name="BackedUp">Its files are in the restore point.</param>
public sealed record KubernetesPodVolumeDto(
    string Pod, string Workload, string Volume, string Kind, string? Source, string? OptIn, string? Platform, long? UsedBytes, bool BackedUp, string? Reason);

/// <summary>What a container had written into its own file system (never backed up), from the usage numbers.</summary>
public sealed record KubernetesContainerWriteDto(string Pod, string Workload, string Container, long? LayerBytes, double? WriteBytesPerSecond);

/// <param name="DataPath"><c>snapshot</c>: its data is in the restore point; <c>objects-only</c>: see <paramref name="Reason"/>.</param>
public sealed record KubernetesVolumeDto(string Claim, string DataPath, string? Reason, string? StorageClass, long SizeBytes, string VolumeMode);

/// <param name="Controlled">Made by a controller (a Deployment's ReplicaSet, a StatefulSet's pod): its owner recreates it.</param>
/// <param name="App">The app it belongs to (label app.kubernetes.io/instance, else the Helm release), if any.</param>
/// <param name="Needs">For a workload: the objects (<c>Kind/name</c>) a restore of it brings along.</param>
public sealed record KubernetesObjectDto(string ApiVersion, string Kind, string Name, string? Namespace, bool Controlled, string? App = null, IReadOnlyList<string>? Needs = null);

public sealed record KubernetesHookDto(string Phase, string Pod, string Container, string Command, int? ExitCode, string? Error, double Seconds);

/// <summary>A restore point of a namespace compared with its live namespace.</summary>
public sealed record KubernetesComparisonDto(string Namespace, DateTimeOffset ComparedUtc, IReadOnlyList<KubernetesObjectStateDto> Objects);

/// <param name="State"><c>same</c>, <c>changed</c>, <c>missing</c> (only in the restore point) or <c>new</c> (only in the cluster).</param>
public sealed record KubernetesObjectStateDto(string ApiVersion, string Kind, string Name, string? App, bool Controlled, string State);

/// <summary>An object as the restore point has it and as the cluster has it now (Secrets without their values).</summary>
public sealed record KubernetesObjectDetailDto(string Kind, string Name, string? Backup, string? Live);
