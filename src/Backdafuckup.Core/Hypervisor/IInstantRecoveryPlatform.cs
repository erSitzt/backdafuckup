using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Core.Hypervisor;

/// <summary>
/// Instant VM recovery on a platform (vSphere): VMs of restore points run from an NFS datastore that the backup server
/// serves, until they are migrated to production storage or stopped. Optional on an <see cref="IHypervisorSession"/>.
/// Nothing here deletes a file on the platform: a VM is only unregistered, and only a datastore that is this server's
/// export is unmounted.
/// </summary>
public interface IInstantRecoveryPlatform
{
    /// <summary>The host that would mount the datastore: its NFS addresses, mount limit, and why it cannot (if so).</summary>
    Task<InstantRecoveryHost> GetHostAsync(string hostId, CancellationToken cancellationToken);

    /// <summary>
    /// The files of the VM in its directory of the datastore: per disk a descriptor the host reads and the flat file that
    /// is the restore point's disk. Problems refuse the VM; warnings are logged.
    /// </summary>
    InstantRecoveryVmLayout PlanVm(RestorePointManifest manifest, VmMetadataBlob metadata, bool keepDiskIdentity);

    /// <summary>Mounts the export as an NFS datastore on the host (a ledger row first) and returns the datastore's id.</summary>
    Task<string> MountDatastoreAsync(string hostId, InstantRecoveryDatastore datastore, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>Creates (registers) the VM around the disk files already on the datastore (a ledger row first). It stays powered off.</summary>
    Task<InstantRecoveryVm> CreateVmAsync(InstantRecoveryVmSpec spec, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken);

    Task PowerOnAsync(string vmId, CancellationToken cancellationToken);

    /// <summary>Where the VM is now: whether it exists, its power state, and which of its files are on the datastore and which elsewhere.</summary>
    Task<InstantRecoveryVmState> GetVmStateAsync(string vmId, string datastoreName, CancellationToken cancellationToken);

    /// <summary>Starts moving the VM to production storage (Storage vMotion) and returns the platform's task id.</summary>
    Task<string> StartMigrationAsync(string vmId, InstantRecoveryMigration target, IOperationLog log, CancellationToken cancellationToken);

    Task<InstantRecoveryTask> GetTaskAsync(string taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Powers the VM off and unregisters it (its files stay; they are in the overlay that the server deletes). Refused with
    /// <see cref="InstantRecoveryRefusedException"/> while any of its files is on another datastore. A VM that is gone is fine.
    /// </summary>
    Task RemoveVmAsync(string vmId, string datastoreName, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>
    /// Unmounts the datastore from every host that mounts it, only if it is the given export (name, server, path) and no VM
    /// is registered on it (else <see cref="InstantRecoveryRefusedException"/>). A datastore that is gone is fine.
    /// </summary>
    Task UnmountDatastoreAsync(string datastoreId, InstantRecoveryDatastore datastore, IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>The NFS datastore of an instant recovery: its name on the platform and the export it mounts.</summary>
public sealed record InstantRecoveryDatastore(string Name, string RemoteHost, string RemotePath);

/// <summary>A host for an instant recovery.</summary>
/// <param name="Addresses">Its IPv4 addresses that mount NFS (vSphere: the VMkernel NICs): the export's allowlist.</param>
/// <param name="NfsMounts">NFS datastores it mounts now.</param>
/// <param name="NfsMountLimit">How many it may mount (vSphere: NFS.MaxVolumes); null if unknown.</param>
/// <param name="Problems">Why it cannot take an instant recovery now (disconnected, maintenance mode, mount limit reached).</param>
public sealed record InstantRecoveryHost(
    string Id, string Name, string? ClusterId, string? ResourcePoolId, IReadOnlyList<string> Addresses, int NfsMounts, int? NfsMountLimit, IReadOnlyList<string> Problems);

/// <summary>A disk of an instant recovery VM in its directory of the datastore.</summary>
/// <param name="DiskKey">The restore point's disk key.</param>
/// <param name="SourceDiskId">The disk's id at backup time (the VM configuration names its disks by it).</param>
/// <param name="Descriptor">The descriptor's file name, e.g. <c>disk0.vmdk</c>.</param>
/// <param name="FlatFile">The data file's name, e.g. <c>disk0-flat.vmdk</c>: the restore point's disk plus what the VM writes.</param>
/// <param name="Empty">The disk was left out of the backup: its data file starts empty (zeros).</param>
public sealed record InstantRecoveryDiskLayout(
    string DiskKey, string SourceDiskId, string Name, long CapacityBytes, string Descriptor, string FlatFile, byte[] DescriptorContent, bool Empty = false);

public sealed record InstantRecoveryVmLayout(IReadOnlyList<InstantRecoveryDiskLayout> Disks, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

/// <summary>The VM to register around the files of an instant recovery.</summary>
public sealed record InstantRecoveryVmSpec
{
    public required string Name { get; init; }

    public required string HostId { get; init; }

    public required string DatastoreId { get; init; }

    public required string DatastoreName { get; init; }

    /// <summary>The VM's directory in the datastore.</summary>
    public required string Directory { get; init; }

    public required RestorePointManifest Manifest { get; init; }

    public required VmMetadataBlob Metadata { get; init; }

    /// <summary>The disks: the layout's entries, their descriptors in <see cref="Directory"/>.</summary>
    public required IReadOnlyList<InstantRecoveryDiskLayout> Disks { get; init; }

    /// <summary>The VM folder (null: the datacenter's VM folder).</summary>
    public string? FolderId { get; init; }

    /// <summary>The resource pool (null: the host's cluster's).</summary>
    public string? ResourcePoolId { get; init; }

    /// <summary>Network per NIC device key; NICs not listed keep their network.</summary>
    public IReadOnlyDictionary<string, string> NetworkMap { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether the NICs start connected (off by default: the original may still run with the same addresses).</summary>
    public bool ConnectNics { get; init; }
}

public sealed record InstantRecoveryVm(string Id, string InstanceUuid, string Name);

/// <summary>Where an instant recovery VM is now.</summary>
/// <param name="FilesOnDatastore">Its files on the instant recovery datastore (swap files not counted).</param>
/// <param name="FilesElsewhere">Its files on other datastores (swap files not counted): after a migration, or a disk added by hand.</param>
public sealed record InstantRecoveryVmState(
    bool Exists, string? Name, string? PowerState, string? HostId, IReadOnlyList<string> FilesOnDatastore, IReadOnlyList<string> FilesElsewhere)
{
    public static InstantRecoveryVmState Gone { get; } = new(false, null, null, null, [], []);
}

/// <summary>Where a migration moves the VM: the datastore, optionally another host and resource pool, thin or thick disks.</summary>
public sealed record InstantRecoveryMigration(string DatastoreId, string? HostId = null, string? ResourcePoolId = null, bool Thin = true);

/// <summary>A platform task: "queued", "running", "success" or "error", with its progress and error.</summary>
public sealed record InstantRecoveryTask(string State, int? Progress, string? Error)
{
    public bool Done => State is "success" or "error";
}

/// <summary>An instant recovery operation refused to protect what is not ours (files on other datastores, another datastore).</summary>
public sealed class InstantRecoveryRefusedException(string message) : InvalidOperationException(message);
