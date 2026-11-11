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
}

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

    /// <summary>Only these objects (<c>Kind/name</c>) and what they need (ConfigMaps, Secrets, PVCs, Services …). Null: the whole namespace.</summary>
    public List<string>? Objects { get; init; }

    public Dictionary<string, string> ClaimNames { get; init; } = [];
}

/// <summary>What a restore point of a namespace holds: the cluster, its volumes and its objects.</summary>
public sealed record KubernetesRestorePointDto(
    string Cluster,
    string Namespace,
    string? KubernetesVersion,
    IReadOnlyList<KubernetesVolumeDto> Volumes,
    IReadOnlyList<KubernetesObjectDto> Objects,
    IReadOnlyList<KubernetesHookDto> Hooks);

/// <param name="DataPath"><c>snapshot</c>: its data is in the restore point; <c>objects-only</c>: see <paramref name="Reason"/>.</param>
public sealed record KubernetesVolumeDto(string Claim, string DataPath, string? Reason, string? StorageClass, long SizeBytes, string VolumeMode);

/// <param name="Controlled">Made by a controller (a Deployment's ReplicaSet, a StatefulSet's pod): its owner recreates it.</param>
/// <param name="App">The app it belongs to (label app.kubernetes.io/instance, else the Helm release), if any.</param>
/// <param name="Needs">For a workload: the objects (<c>Kind/name</c>) a restore of it brings along.</param>
public sealed record KubernetesObjectDto(string ApiVersion, string Kind, string Name, string? Namespace, bool Controlled, string? App = null, IReadOnlyList<string>? Needs = null);

public sealed record KubernetesHookDto(string Phase, string Pod, string Container, string Command, int? ExitCode, string? Error, double Seconds);
