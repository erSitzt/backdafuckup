using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;

namespace Backdafuckup.Core.Hypervisor;

/// <summary>Entry point of a hypervisor integration (XCP-ng now; vSphere and Hyper-V later).</summary>
public interface IHypervisorProvider
{
    HypervisorKind Kind { get; }

    /// <summary>Connects without trusting anything and returns the presented certificate (trust on first use).</summary>
    Task<CertificateProbe> ProbeCertificateAsync(string address, int port, CancellationToken cancellationToken);

    /// <summary>Logs in, resolves the pool master and returns pins for all hosts (or the hosts still to be trusted).</summary>
    Task<ConnectResult> TestConnectionAsync(ConnectionSettings settings, CancellationToken cancellationToken);

    Task<IHypervisorSession> OpenAsync(ConnectionSettings settings, CancellationToken cancellationToken);
}

public interface IHypervisorSession : IAsyncDisposable
{
    PlatformInfo Platform { get; }

    Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Creates the VM snapshot and returns a session to read its disks. Disposing without commit rolls back.</summary>
    Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken);

    Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken);

    /// <summary>Removes a leftover object recorded in the artifact ledger (crash recovery).</summary>
    Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>Commits a pending artifact (e.g. converts a snapshot to change-tracking metadata after a crash).</summary>
    Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>Releases change tokens that are no longer referenced by any restore point.</summary>
    Task ReleaseChangeTokensAsync(IEnumerable<ChangeToken> tokens, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>Lists objects tagged by us (snapshots, change-tracking metadata) for the orphan scan.</summary>
    IAsyncEnumerable<ProviderArtifact> FindTaggedArtifactsAsync(CancellationToken cancellationToken);
}

/// <summary>A VM snapshot being backed up.</summary>
public interface IVmBackupSession : IAsyncDisposable
{
    VmDescriptor Vm { get; }

    IReadOnlyList<SnapshotDisk> Disks { get; }

    VmMetadataBlob Metadata { get; }

    /// <summary>Platform object id of the snapshot (informational).</summary>
    string SnapshotId { get; }

    /// <summary>
    /// Returns the changed extents of <paramref name="disk"/> since <paramref name="since"/>, or a full read with a reason.
    /// Must be called before <see cref="OpenReaderAsync"/> for the same disk.
    /// </summary>
    Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken);

    Task<IDiskReader> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken);

    /// <summary>Token for the next incremental backup of this disk (null if the platform cannot track changes for it).</summary>
    ChangeToken? GetNewChangeToken(SnapshotDisk disk);

    /// <summary>The restore point is committed: keep only change-tracking state and remove the snapshot data.</summary>
    Task CommitAsync(IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>A running VM restore. Disposing without <see cref="CompleteAsync"/> removes everything created so far.</summary>
public interface IVmRestoreSession : IAsyncDisposable
{
    Task<RestoreValidation> ValidateAsync(CancellationToken cancellationToken);

    Task<IDiskWriter> CreateDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken);

    /// <summary>Creates the VM from the stored metadata, attached to the restored disks (source disk id → new disk id).</summary>
    Task<RestoredVm> CreateVmAsync(IReadOnlyDictionary<string, string> sourceToNewDiskId, CancellationToken cancellationToken);

    /// <summary>Final touches (name, NICs, affinity, power state). After this the restore is not rolled back anymore.</summary>
    Task<RestoredVm> CompleteAsync(CancellationToken cancellationToken);
}
