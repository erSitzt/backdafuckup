namespace Backdafuckup.Server.Data;

/// <summary>Where an instant recovery is in its life.</summary>
public enum InstantRecoveryState
{
    /// <summary>Being set up: pins, overlay, export, datastore, VMs (each step recorded in the ledger before it is made).</summary>
    Starting,

    /// <summary>The VMs run from the datastore the backup server serves.</summary>
    Running,

    /// <summary>The VMs are moved to production storage (Storage vMotion).</summary>
    Migrating,

    /// <summary>The VMs are powered off and removed, then the datastore.</summary>
    Stopping,

    /// <summary>Ended by Stop: nothing of it is left.</summary>
    Stopped,

    /// <summary>Ended by a migration: the VMs live on production storage, the datastore is gone.</summary>
    Migrated,

    /// <summary>Ended by an error; what it made is removed by the cleanup from the ledger.</summary>
    Failed,
}

/// <summary>
/// An instant recovery: one or more VMs of restore points running from one NFS datastore that this server serves, until
/// they are migrated to production storage or stopped. One per session; it survives a restart of the server.
/// </summary>
public sealed class InstantRecoveryEntity
{
    /// <summary>The session (kind InstantRecovery) the instant recovery belongs to; also its key.</summary>
    public Guid SessionId { get; set; }

    /// <summary>The vCenter the VMs run in.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>The ESXi host that mounts the datastore and runs the VMs (managed object id), and its name for messages.</summary>
    public required string HostId { get; set; }

    public string? HostName { get; set; }

    /// <summary>The datastore's name in vCenter, <c>bdfu-ir-&lt;sid8&gt;</c>, and its managed object id once mounted.</summary>
    public required string DatastoreName { get; set; }

    public string? DatastoreId { get; set; }

    /// <summary>The NFS export, <c>/bdfu/&lt;sid8&gt;</c>, and its id in the file handles (unique per server).</summary>
    public required string ExportPath { get; set; }

    public long ExportId { get; set; }

    /// <summary>The addresses that may mount the export: the host's VMkernel addresses.</summary>
    public List<string>? AllowedClients { get; set; }

    /// <summary>This server's address that the host mounts the export from (configured, or the one that routes to the host).</summary>
    public string? RemoteHost { get; set; }

    /// <summary>The VM folder and resource pool of the VMs (null: the datacenter's VM folder, the host's cluster's pool).</summary>
    public string? FolderId { get; set; }

    public string? ResourcePoolId { get; set; }

    /// <summary>Whether the VMs' NICs start connected (off by default: the originals may still run with the same addresses).</summary>
    public bool ConnectNics { get; set; }

    /// <summary>Keep the disks' identity (ddb.uuid) instead of new ones.</summary>
    public bool KeepDiskIdentity { get; set; }

    /// <summary>Where a migration asked for moves the VMs; kept so that a migration carries on after a restart.</summary>
    public Core.Hypervisor.InstantRecoveryMigration? MigrationTarget { get; set; }

    /// <summary>Where the overlay and the namespace journal live (below the configured overlay directory).</summary>
    public required string OverlayDirectory { get; set; }

    /// <summary>The most bytes the VMs may write into the overlay; null for no limit but the volume's free space.</summary>
    public long? OverlayLimitBytes { get; set; }

    public InstantRecoveryState State { get; set; }

    /// <summary>The Storage vMotion task of a migration, to wait for it again after a restart.</summary>
    public string? MigrateTaskId { get; set; }

    /// <summary>When ESXi last read or wrote (a warning after long silence).</summary>
    public DateTimeOffset? LastIoUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Why it failed, or what it is waiting for, for the console.</summary>
    public string? Message { get; set; }

    public List<InstantRecoveryVmEntity> Vms { get; set; } = [];
}

/// <summary>A VM of an instant recovery: which restore point it runs from, where its files are, what vCenter calls it.</summary>
public sealed class InstantRecoveryVmEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    /// <summary>The restore point it runs from (no foreign key: ended instant recoveries keep their history after the point is gone).</summary>
    public Guid RestorePointId { get; set; }

    /// <summary>The VM's name in vCenter.</summary>
    public required string Name { get; set; }

    /// <summary>Its directory in the datastore.</summary>
    public required string Directory { get; set; }

    /// <summary>The VM's managed object id and instance UUID once created.</summary>
    public string? VmId { get; set; }

    public string? InstanceUuid { get; set; }

    /// <summary>The restore point's disk key → the disk's descriptor in the datastore ("app01/disk0.vmdk").</summary>
    public Dictionary<string, string>? DiskFiles { get; set; }

    /// <summary>Network per NIC device key; NICs not listed keep their network.</summary>
    public Dictionary<string, string>? NetworkMap { get; set; }
}
