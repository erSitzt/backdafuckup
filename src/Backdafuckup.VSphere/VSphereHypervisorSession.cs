using System.Net;
using System.Runtime.CompilerServices;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>An open connection to one vCenter.</summary>
internal sealed partial class VSphereHypervisorSession(VSphereProvider provider, ConnectionSettings settings, VimSession vim, PlatformInfo platform) : IHypervisorSession, IGuestOperations, IInstantRecoveryPlatform
{
    private VSphereGuestOperations? _guest;
    private VSphereInstantRecovery? _instant;

    public PlatformInfo Platform { get; } = platform;

    internal VSphereProvider Provider { get; } = provider;

    internal ConnectionSettings Settings { get; } = settings;

    internal VimSession Vim { get; } = vim;

    public Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken) => VSphereInventory.GetAsync(Vim, Platform, cancellationToken);

    public Task<StorageState> GetStorageStateAsync(CancellationToken cancellationToken) => VSphereInventory.GetStorageStateAsync(Vim, cancellationToken);

    public async Task<IReadOnlyList<string>> GetGuestAddressesAsync(string vmId, CancellationToken cancellationToken)
    {
        var vm = await VSphereInventory.FindVmAsync(Vim, vmId, cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            return [];
        }

        var row = (await Vim.RetrievePropertiesAsync([vm.Ref], ["guest.net", "guest.ipAddress"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var addresses = new List<string>();
        foreach (var nic in row?["guest.net"]?.Items ?? [])
        {
            addresses.AddRange(nic.Children("ipAddress").Select(a => a.Value));
        }

        if (row?.Str("guest.ipAddress") is { Length: > 0 } primary)
        {
            addresses.Insert(0, primary);
        }

        return [.. addresses
            .Where(a => IPAddress.TryParse(a, out var ip) && !ip.IsIPv6LinkLocal && !IPAddress.IsLoopback(ip))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a.Contains(':', StringComparison.Ordinal))];
    }

    public async Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.WithMemory)
        {
            throw new NotSupportedException("A snapshot with memory is not offered for vSphere VMs: back the VM up without memory.");
        }

        return await VSphereBackupSession.CreateAsync(this, request, log, ledger, cancellationToken).ConfigureAwait(false);
    }

    public Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Task.FromResult<IVmRestoreSession>(new VSphereRestoreSession(this, plan, log, ledger));
    }

    /// <summary>
    /// After a crash: removes a backup snapshot that is still there, detaches a disk still hot-added to a proxy VM, and
    /// removes what an unfinished restore created (its VM, or else its disk files).
    /// </summary>
    public async Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(log);
        switch (artifact.Kind)
        {
            case VSphereArtifactKinds.Snapshot when artifact.NativeId is { Length: > 0 } snapshot:
                await VSphereBackupSession.RemoveSnapshotAsync(Vim, new ManagedObjectReference("VirtualMachineSnapshot", snapshot), artifact.NativeRef ?? snapshot, log, cancellationToken)
                    .ConfigureAwait(false);
                log.Info($"Removed the backup snapshot {snapshot} of VM {artifact.NativeRef} that a stopped session left.");
                break;
            case VSphereArtifactKinds.HotAddDisk when artifact.NativeRef is { } proxyVm && artifact.Data is { } data:
                var record = System.Text.Json.JsonSerializer.Deserialize<HotAddRecord>(data);
                var devices = (await Vim.RetrievePropertiesAsync([new ManagedObjectReference("VirtualMachine", proxyVm)], ["config.hardware.device"], cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault()?["config.hardware.device"];
                var attached = record is null ? null : devices?.Items.FirstOrDefault(d => d.Type == "VirtualDisk" && d.Int("controllerKey") == record.ControllerKey && d.Int("unitNumber") == record.Unit
                    && d.Child("backing")?.Str("fileName") == record.FileName);
                if (record is not null && attached?.Int("key") is { } key)
                {
                    await VSphereHotAdd.DetachAsync(Vim, new ManagedObjectReference("VirtualMachine", proxyVm), key, new ScsiSlot(record.ControllerKey, record.Bus, record.Unit), record.CapacityBytes, cancellationToken)
                        .ConfigureAwait(false);
                    log.Info($"Detached {record.FileName} from proxy VM {proxyVm} (SCSI {record.Bus}:{record.Unit}) that a stopped session left.");
                }

                break;
            case VSphereArtifactKinds.RollbackSnapshot when artifact.NativeId is { Length: > 0 } safety:
                // Reverting or removing it could undo a finished rollback or keep a half one: the administrator decides.
                log.Warning($"A quick rollback of VM {artifact.NativeRef} was interrupted; its safety snapshot {safety} (\"backdafuckup rollback safety snapshot\") is still there. " +
                            "Revert the VM to it if the rollback did not finish, else delete it.");
                break;
            case VSphereArtifactKinds.RestoredVm when artifact.NativeId is { Length: > 0 } vm:
                await VSphereRestoreSession.DestroyAsync(Vim, new ManagedObjectReference("VirtualMachine", vm), cancellationToken).ConfigureAwait(false);
                log.Info($"Removed VM {artifact.NativeRef} ({vm}) that an unfinished restore created.");
                break;
            case VSphereArtifactKinds.InstantRecoveryVm or VSphereArtifactKinds.NasDatastore:
                await Instant.CleanupAsync(artifact, log, cancellationToken).ConfigureAwait(false);
                break;
            case VSphereArtifactKinds.RestoredDisk when artifact.NativeId is { Length: > 0 } file && artifact.NativeRef is { Length: > 0 } datacenter:
                await VSphereRestoreSession.DeleteDiskFileAsync(Vim, file, new ManagedObjectReference("Datacenter", datacenter), cancellationToken).ConfigureAwait(false);
                log.Info($"Removed the disk file {file} that an unfinished restore created.");
                break;
        }
    }

    public Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>vSphere keeps no per-backup change-tracking objects: a changeId is just a position in the disk's log.</summary>
    public Task ReleaseChangeTokensAsync(IEnumerable<ChangeToken> tokens, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<ProviderArtifact> FindTaggedArtifactsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private VSphereGuestOperations Guest => _guest ??= new VSphereGuestOperations(this);

    public Task<string?> CheckGuestOperationsAsync(string vmId, CancellationToken cancellationToken) => Guest.CheckGuestOperationsAsync(vmId, cancellationToken);

    public Task CreateDirectoryAsync(string vmId, GuestLogin login, string path, CancellationToken cancellationToken) => Guest.CreateDirectoryAsync(vmId, login, path, cancellationToken);

    public Task UploadFileAsync(string vmId, GuestLogin login, string path, Stream content, long length, CancellationToken cancellationToken) =>
        Guest.UploadFileAsync(vmId, login, path, content, length, cancellationToken);

    public Task<long> StartProgramAsync(string vmId, GuestLogin login, string programPath, string arguments, CancellationToken cancellationToken) =>
        Guest.StartProgramAsync(vmId, login, programPath, arguments, cancellationToken);

    public Task<int?> GetExitCodeAsync(string vmId, GuestLogin login, long processId, CancellationToken cancellationToken) =>
        Guest.GetExitCodeAsync(vmId, login, processId, cancellationToken);

    private VSphereInstantRecovery Instant => _instant ??= new VSphereInstantRecovery(this);

    public Task<InstantRecoveryHost> GetHostAsync(string hostId, CancellationToken cancellationToken) => Instant.GetHostAsync(hostId, cancellationToken);

    public InstantRecoveryVmLayout PlanVm(Core.Repository.RestorePointManifest manifest, VmMetadataBlob metadata, bool keepDiskIdentity) =>
        Instant.PlanVm(manifest, metadata, keepDiskIdentity);

    public Task<string> MountDatastoreAsync(string hostId, InstantRecoveryDatastore datastore, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken) =>
        Instant.MountDatastoreAsync(hostId, datastore, ledger, log, cancellationToken);

    public Task<InstantRecoveryVm> CreateVmAsync(InstantRecoveryVmSpec spec, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken) =>
        Instant.CreateVmAsync(spec, ledger, log, cancellationToken);

    public Task PowerOnAsync(string vmId, CancellationToken cancellationToken) => Instant.PowerOnAsync(vmId, cancellationToken);

    public Task<InstantRecoveryVmState> GetVmStateAsync(string vmId, string datastoreName, CancellationToken cancellationToken) =>
        Instant.GetVmStateAsync(vmId, datastoreName, cancellationToken);

    public Task<string> StartMigrationAsync(string vmId, InstantRecoveryMigration target, IOperationLog log, CancellationToken cancellationToken) =>
        Instant.StartMigrationAsync(vmId, target, log, cancellationToken);

    public Task<InstantRecoveryTask> GetTaskAsync(string taskId, CancellationToken cancellationToken) => Instant.GetTaskAsync(taskId, cancellationToken);

    public Task RemoveVmAsync(string vmId, string datastoreName, IOperationLog log, CancellationToken cancellationToken) =>
        Instant.RemoveVmAsync(vmId, datastoreName, log, cancellationToken);

    public Task UnmountDatastoreAsync(string datastoreId, InstantRecoveryDatastore datastore, IOperationLog log, CancellationToken cancellationToken) =>
        Instant.UnmountDatastoreAsync(datastoreId, datastore, log, cancellationToken);

    public ValueTask DisposeAsync() => Vim.DisposeAsync();
}
