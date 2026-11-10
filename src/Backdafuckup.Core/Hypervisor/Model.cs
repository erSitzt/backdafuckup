using System.Net;

namespace Backdafuckup.Core.Hypervisor;

public enum HypervisorKind
{
    XenServer,
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
}

/// <summary>Hypervisor-neutral inventory entry. Provider-specific details go into <see cref="Properties"/>.</summary>
public sealed record InventoryItem(
    InventoryKind Kind,
    string NativeId,
    string Name,
    string? ParentNativeId,
    IReadOnlyDictionary<string, string> Properties);

public sealed record Inventory(PlatformInfo Platform, IReadOnlyList<InventoryItem> Items);

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

    public IReadOnlyList<VmNic> Nics { get; init; } = [];

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
    IReadOnlyDictionary<string, string> Locator);

/// <summary>What changed on a disk since a change token, or "read everything" with a reason.</summary>
public sealed record ChangedExtents(bool IsFull, string? FullReason, IReadOnlyList<Extents.DiskExtent> Extents)
{
    /// <summary>Why everything is read, as one of the fixed <see cref="FullReadReasons"/> (for metrics).</summary>
    public string? FullReasonCode { get; init; }

    public static ChangedExtents Full(string reason, string code = FullReadReasons.Other) => new(true, reason, []) { FullReasonCode = code };
}

/// <summary>Raw provider-specific VM metadata needed to re-create the VM (Xen: export_metadata output).</summary>
public sealed record VmMetadataBlob(string Format, byte[] Content);

/// <param name="Quiescer">Freezes the guest around the snapshot (application-consistent backup); null: crash-consistent.</param>
/// <param name="Telemetry">Receives transport events of the backup (NBD connects, fallbacks) for metrics; null: none.</param>
public sealed record VmBackupRequest(string VmId, Guid JobId, Guid SessionId, Guid RestorePointId, string SnapshotName, IGuestQuiescer? Quiescer = null, IBackupTelemetry? Telemetry = null);

public enum RestoreMode
{
    OriginalLocation,
    AlternateLocation,
}

public sealed record VmRestorePlan
{
    public required RestoreMode Mode { get; init; }

    public required Repository.RestorePointManifest Manifest { get; init; }

    public required VmMetadataBlob Metadata { get; init; }

    public required string VmName { get; init; }

    public string? TargetHostId { get; init; }

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

    public Guid SessionId { get; init; }
}

public sealed record RestoreValidation(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Ok => Errors.Count == 0;
}

public sealed record RestoreDiskTarget(string DiskKey, string SourceDiskId, string Name, long CapacityBytes, string StorageId);

/// <param name="GenerationId">The VM generation id the hypervisor gave the VM (Xen: <c>generation_id</c>), if known.</param>
/// <param name="ReplacedVmId">The id of the VM this one replaced at the original location (renamed or deleted), if any.</param>
public sealed record RestoredVm(string VmId, string Name, string? GenerationId = null, string? ReplacedVmId = null);

/// <summary>
/// VM generation ids (Xen: <c>generation_id</c>). An empty one means the VM has none: it was created from a template
/// without one, and the hypervisor never gives it one (not at a restore either); "0:0" means none generated yet.
/// </summary>
public static class VmGenerationIds
{
    public static bool IsSet(string? id) => !string.IsNullOrEmpty(id) && id != "0:0";
}
