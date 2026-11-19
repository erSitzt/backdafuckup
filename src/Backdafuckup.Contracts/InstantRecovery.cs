namespace Backdafuckup.Contracts;

/// <summary>
/// Starts an instant recovery: the VMs of the restore points run on the ESXi host from an NFS datastore that the backup
/// server serves, until they are migrated to production storage or stopped.
/// </summary>
public sealed record StartInstantRecoveryDto
{
    /// <summary>The vCenter the VMs run in.</summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>The ESXi host that mounts the datastore and runs the VMs (its managed object id).</summary>
    public required string HostId { get; init; }

    public required IReadOnlyList<InstantRecoveryVmRequestDto> Vms { get; init; }

    /// <summary>The VM folder (null: the datacenter's VM folder).</summary>
    public string? FolderId { get; init; }

    /// <summary>The resource pool (null: the host's cluster's).</summary>
    public string? ResourcePoolId { get; init; }

    /// <summary>Whether the NICs start connected (off by default: the originals may still run with the same addresses).</summary>
    public bool ConnectNics { get; init; }

    /// <summary>Keep the disks' identity (ddb.uuid) instead of new ones.</summary>
    public bool KeepDiskIdentity { get; init; }

    /// <summary>The most bytes the VMs may write before their writes fail; null for the overlay volume's free space.</summary>
    public long? OverlayLimitBytes { get; init; }
}

/// <summary>A VM of an instant recovery: its restore point, its name in vCenter (default "&lt;name&gt; (instant)"), its NICs' networks (NIC device key → network).</summary>
public sealed record InstantRecoveryVmRequestDto(Guid RestorePointId, string? Name = null, IReadOnlyDictionary<string, string>? NetworkMap = null);

/// <summary>Moves the VMs of an instant recovery to production storage (Storage vMotion), optionally to another host and resource pool.</summary>
public sealed record MigrateInstantRecoveryDto(string DatastoreId, string? HostId = null, string? ResourcePoolId = null, bool Thin = true);

/// <summary>An instant recovery and where it stands.</summary>
/// <param name="State">Starting, Running, Migrating, Stopping, Stopped, Migrated or Failed.</param>
/// <param name="OverlayBytes">What the VMs wrote so far (while it is served).</param>
/// <param name="Message">Why it failed, or why a stop or migration did not happen.</param>
public sealed record InstantRecoveryDto(
    Guid SessionId,
    string State,
    Guid ConnectionId,
    string? ConnectionName,
    string HostId,
    string? HostName,
    string DatastoreName,
    string? RemoteHost,
    string ExportPath,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? Message,
    long? OverlayBytes,
    long? OverlayLimitBytes,
    IReadOnlyList<InstantRecoveryVmDto> Vms,
    MigrateInstantRecoveryDto? Migration);

/// <param name="SourceVmName">The backed-up VM's name.</param>
/// <param name="RestorePointCreatedUtc">When its restore point was taken; null if the point is gone.</param>
public sealed record InstantRecoveryVmDto(Guid RestorePointId, string Name, string? VmId, string? SourceVmName, DateTimeOffset? RestorePointCreatedUtc);

/// <summary>The NFS server for instant recovery on this backup server.</summary>
/// <param name="Running">Instant recoveries that are not over.</param>
/// <param name="MaxConcurrent">How many may run at once.</param>
public sealed record InstantRecoveryServerDto(
    bool Enabled, bool Listening, string ListenAddress, string? AdvertiseAddress, int PortmapperPort, int NfsPort, int MountPort, string Portmapper, string? Error,
    int Running, int MaxConcurrent, int MaxVmsPerSession);
