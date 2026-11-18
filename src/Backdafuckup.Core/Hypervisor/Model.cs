using System.Net;

namespace Backdafuckup.Core.Hypervisor;

public enum HypervisorKind
{
    XenServer,

    /// <summary>A Kubernetes cluster: its namespaces are backed up (objects and the data of their volumes).</summary>
    Kubernetes,

    /// <summary>A VMware vCenter: its VMs are backed up through the public vim25 API and a HotAdd proxy.</summary>
    VSphere,
}

public enum TlsTrustMode
{
    /// <summary>Trust exactly the pinned certificates (trust on first use).</summary>
    Pinned,

    /// <summary>Validate against the system CA store and the host name.</summary>
    SystemCa,
}

/// <summary>A trusted TLS certificate of one host, pinned by the SHA-256 of its public key (SPKI).</summary>
public sealed record PinnedCertificate(string HostAddress, string SpkiSha256, string Sha256Thumbprint, string Pem);

/// <summary>Everything a provider needs to talk to a hypervisor platform (e.g. one XCP-ng pool).</summary>
public sealed record ConnectionSettings(
    Guid ConnectionId,
    HypervisorKind Kind,
    string Address,
    int Port,
    NetworkCredential Credential,
    TlsTrustMode TrustMode,
    IReadOnlyList<PinnedCertificate> Pins,
    IReadOnlyDictionary<string, string> Options);

/// <summary>Certificate presented by a host, shown to the user for trust-on-first-use.</summary>
public sealed record CertificateProbe(
    string HostAddress,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string SpkiSha256,
    string Sha256Thumbprint,
    string Pem,
    bool ValidAgainstSystemCa);

public sealed record ConnectResult(
    bool Success,
    string? Error,
    PlatformInfo? Platform,
    IReadOnlyList<CertificateProbe> UntrustedHosts,
    IReadOnlyList<PinnedCertificate> HostPins);

public sealed record PlatformInfo(
    string PlatformId,
    string PlatformName,
    string MasterAddress,
    string ProductName,
    string ProductVersion);

public enum InventoryKind
{
    Host,
    Vm,
    Storage,
    Network,

    /// <summary>A Kubernetes namespace.</summary>
    Namespace,

    /// <summary>A Kubernetes PersistentVolumeClaim; its parent is the namespace.</summary>
    VolumeClaim,

    /// <summary>
    /// A volume of a workload's pods that is not a PVC (emptyDir, hostPath, …), or a container's own file system (kind
    /// <c>container</c>); its parent is the namespace.
    /// </summary>
    PodVolume,
}

/// <summary>Hypervisor-neutral inventory entry. Provider-specific details go into <see cref="Properties"/>.</summary>
public sealed record InventoryItem(
    InventoryKind Kind,
    string NativeId,
    string Name,
    string? ParentNativeId,
    IReadOnlyDictionary<string, string> Properties);

public sealed record Inventory(PlatformInfo Platform, IReadOnlyList<InventoryItem> Items)
{
    /// <summary>The platforms recognised in the cluster (Kubernetes: ids of the platform catalog), with what gave each away.</summary>
    public IReadOnlyList<DetectedPlatform>? Platforms { get; init; }
}

/// <summary>A platform recognised in a cluster and what gave it away, e.g. <c>("rke2", "version v1.32.13+rke2r1")</c>.</summary>
public sealed record DetectedPlatform(string Id, string Evidence);

/// <summary>Opaque token that allows asking "what changed since" for a disk (Xen: uuid of the cbt_metadata VDI).</summary>
public sealed record ChangeToken(string Kind, string Value);

/// <summary>Hypervisor-neutral description of a VM's configuration at backup time (for UI and restore).</summary>
public sealed record VmDescriptor
{
    public required string VmId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public int VCpus { get; init; }

    public long MemoryBytes { get; init; }

    public string? Firmware { get; init; }

    public bool SecureBoot { get; init; }

    public bool HasVtpm { get; init; }

    public string? OsName { get; init; }

    public IReadOnlyList<VmNic> Nics { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The VM generation id at backup time (Xen: <c>generation_id</c>). Windows compares it at start: a changed id tells a
    /// domain controller that it was restored (non-authoritative restore instead of a USN rollback).
    /// </summary>
    public string? GenerationId { get; init; }
}

public sealed record VmNic(string Device, string Mac, string NetworkId, string NetworkName, int Mtu);

/// <summary>A disk of a VM snapshot that is about to be backed up.</summary>
public sealed record SnapshotDisk(
    string Key,
    string SourceDiskId,
    string SnapshotDiskId,
    string Name,
    long CapacityBytes,
    bool Bootable,
    string StorageId,
    string StorageName,
    IReadOnlyDictionary<string, string> Locator)
{
    /// <summary>The key of a RAM image (snapshot with memory) in the reader path and the repository.</summary>
    public const string MemoryKey = "memory";
}

/// <summary>What changed on a disk since a change token, or "read everything" with a reason.</summary>
public sealed record ChangedExtents(bool IsFull, string? FullReason, IReadOnlyList<Extents.DiskExtent> Extents)
{
    /// <summary>Why everything is read, as one of the fixed <see cref="FullReadReasons"/> (for metrics).</summary>
    public string? FullReasonCode { get; init; }

    /// <summary>
    /// For a full read: the areas of the disk that hold data (vSphere: <c>QueryChangedDiskAreas</c> with "*"). Only chunks
    /// that touch them are read; the rest of the disk is recorded as zeros. Null: the whole disk is read.
    /// </summary>
    public IReadOnlyList<Extents.DiskExtent>? Allocated { get; init; }

    public static ChangedExtents Full(string reason, string code = FullReadReasons.Other) => new(true, reason, []) { FullReasonCode = code };

    /// <summary>A full read of only the <paramref name="allocated"/> areas; the rest of the disk holds no data.</summary>
    public static ChangedExtents FullAllocated(string reason, IReadOnlyList<Extents.DiskExtent> allocated, string code = FullReadReasons.Other) =>
        new(true, reason, []) { FullReasonCode = code, Allocated = allocated };
}

/// <summary>Raw provider-specific VM metadata needed to re-create the VM (Xen: export_metadata output).</summary>
public sealed record VmMetadataBlob(string Format, byte[] Content);

/// <param name="Quiescer">Freezes the guest around the snapshot (application-consistent backup); null: crash-consistent.</param>
/// <param name="Telemetry">Receives transport events of the backup (NBD connects, fallbacks) for metrics; null: none.</param>
/// <param name="WithMemory">
/// Snapshot with memory (Xen: <c>VM.checkpoint</c>) when the VM runs: the VM is paused while its RAM is written and all disks
/// are captured, and the RAM image is backed up (<see cref="IVmBackupSession.Memory"/>). A VM that does not run gets a normal snapshot.
/// </param>
/// <param name="PodVolumes">
/// Kubernetes: back up the data of every pod volume that is not a PVC (emptyDir and the like; Velero's opt-out mode), not only
/// those named in a pod's <c>backup.velero.io/backup-volumes</c> annotation.
/// </param>
/// <param name="PlatformQuiesce">
/// No agent of ours froze the guest: let the platform quiesce the snapshot through its guest tools (vSphere: VMware
/// Tools, VSS on Windows, fsfreeze on Linux).
/// </param>
public sealed record VmBackupRequest(
    string VmId, Guid JobId, Guid SessionId, Guid RestorePointId, string SnapshotName, IGuestQuiescer? Quiescer = null, IBackupTelemetry? Telemetry = null, bool WithMemory = false,
    bool PodVolumes = false, bool PlatformQuiesce = false);

public enum RestoreMode
{
    OriginalLocation,
    AlternateLocation,
}

/// <summary>How a Kubernetes namespace (a restore point of a namespace) is restored.</summary>
public sealed record KubernetesRestoreOptions
{
    /// <summary>The namespace the objects go to: the original one, or another (created when missing).</summary>
    public required string TargetNamespace { get; init; }

    /// <summary>Objects that exist already are replaced (updated) instead of left as they are; never PVCs with data.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>Only these PVCs (claim names), with their data, and no other object: the restore of single volumes. Null: the whole namespace.</summary>
    public IReadOnlyList<string>? OnlyClaims { get; init; }

    /// <summary>
    /// Only these objects (<c>Kind/name</c>, e.g. a workload or every object of an app) and what they need: their ConfigMaps,
    /// Secrets, ServiceAccount with its roles, PVCs with their data, Services and Ingresses. Null: the whole namespace.
    /// </summary>
    public IReadOnlyList<string>? Objects { get; init; }

    /// <summary>Restore <see cref="Objects"/> as they are, without what their workloads need (a selection in the object explorer).</summary>
    public bool ExactObjects { get; init; }

    /// <summary>
    /// With <see cref="OnlyClaims"/>: each PVC replaces the one of its name in the target namespace. The workloads using it are
    /// scaled to 0 for the swap and back afterwards; the PVC's current data is deleted.
    /// </summary>
    public bool ReplaceClaims { get; init; }

    /// <summary>New names for restored PVCs (claim → name), e.g. to restore one next to the original.</summary>
    public IReadOnlyDictionary<string, string> ClaimNames { get; init; } = new Dictionary<string, string>();

    /// <summary>StorageClasses of the restore point mapped to classes of the target cluster (source → target); unmapped ones keep their name.</summary>
    public IReadOnlyDictionary<string, string> StorageClassMap { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The target is another cluster than the restore point's: what only the old cluster means is left out (finalizers,
    /// Rancher's fields, static PVs), and StorageClasses of the backup are never created there.
    /// </summary>
    public bool OtherCluster { get; init; }

    public string ClaimName(string claim) => ClaimNames.TryGetValue(claim, out var name) && !string.IsNullOrWhiteSpace(name) ? name : claim;
}

public sealed record VmRestorePlan
{
    public required RestoreMode Mode { get; init; }

    public required Repository.RestorePointManifest Manifest { get; init; }

    public required VmMetadataBlob Metadata { get; init; }

    /// <summary>The restore of a Kubernetes namespace; null for a VM.</summary>
    public KubernetesRestoreOptions? Kubernetes { get; init; }

    public required string VmName { get; init; }

    public string? TargetHostId { get; init; }

    /// <summary>
    /// Original location only: write back into the existing VM only the blocks changed since the restore point (vSphere:
    /// changed block tracking), under a safety snapshot that a failure reverts to.
    /// </summary>
    public bool QuickRollback { get; init; }

    /// <summary>
    /// Whether the restored VM's NICs start connected; null: automatic (vSphere: a copy whose original still exists starts
    /// disconnected).
    /// </summary>
    public bool? ConnectNics { get; init; }

    /// <summary>Quick rollback: keep the safety snapshot afterwards (the VM's state before the rollback), instead of removing it.</summary>
    public bool KeepSafetySnapshot { get; init; }

    /// <summary>vSphere: the VM folder the restored VM goes into (null: where the backed-up VM was).</summary>
    public string? TargetFolderId { get; init; }

    /// <summary>vSphere: the resource pool of the restored VM (null: the backed-up VM's, else the target host's cluster).</summary>
    public string? TargetResourcePoolId { get; init; }

    /// <summary>Target storage per disk key. Required for every disk.</summary>
    public required IReadOnlyDictionary<string, string> DiskStorage { get; init; }

    /// <summary>Target network per NIC device. NICs not listed keep their original network.</summary>
    public IReadOnlyDictionary<string, string> NetworkMap { get; init; } = new Dictionary<string, string>();

    public bool RegenerateMacs { get; init; }

    public bool PowerOn { get; init; }

    /// <summary>Original location only: replace an existing VM with the same id.</summary>
    public bool ReplaceExisting { get; init; }

    /// <summary>
    /// Original location: the ids the VM had, oldest first: the backed-up VM's, then each VM that replaced it at an earlier
    /// restore to the original location (it got a new id because the original still held the old one). The last of them
    /// that still exists is the original. Empty: the backed-up VM's id only.
    /// </summary>
    public IReadOnlyList<string> OriginalVmIds { get; init; } = [];

    /// <summary>Original location only: delete the replaced VM after a successful restore (otherwise renamed and halted).</summary>
    public bool DeleteReplaced { get; init; }

    /// <summary>
    /// Resume the VM with its memory: <see cref="Metadata"/> is the snapshot with memory's own metadata, the RAM image is
    /// restored as the VM's suspend image (<see cref="RestoreDiskTarget.MemoryImage"/>), and the VM is created suspended.
    /// <see cref="PowerOn"/> resumes it. A copy (another location) resumes without network connections.
    /// </summary>
    public bool Resume { get; init; }

    public Guid SessionId { get; init; }
}

public sealed record RestoreValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

/// <param name="MemoryImage">The RAM image of a snapshot with memory, restored as the suspend image of a VM that resumes.</param>
public sealed record RestoreDiskTarget(string DiskKey, string SourceDiskId, string Name, long CapacityBytes, string StorageId, bool MemoryImage = false);

/// <param name="GenerationId">The VM generation id the hypervisor gave the VM (Xen: <c>generation_id</c>), if known.</param>
/// <param name="ReplacedVmId">The id of the VM this one replaced at the original location (renamed or deleted), if any.</param>
/// <param name="Warnings">What did not go as planned but did not stop the restore (an object the cluster refused).</param>
public sealed record RestoredVm(string VmId, string Name, string? GenerationId = null, string? ReplacedVmId = null, IReadOnlyList<string>? Warnings = null);

/// <summary>
/// VM generation ids (Xen: <c>generation_id</c>). An empty one means the VM has none: it was created from a template
/// without one, and the hypervisor never gives it one (not at a restore either); "0:0" means none generated yet.
/// </summary>
public static class VmGenerationIds
{
    public static bool IsSet(string? id) => !string.IsNullOrEmpty(id) && id != "0:0";
}
