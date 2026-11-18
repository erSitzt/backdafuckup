using System.Net;
using System.Runtime.CompilerServices;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>An open connection to one vCenter.</summary>
internal sealed class VSphereHypervisorSession(VSphereProvider provider, ConnectionSettings settings, VimSession vim, PlatformInfo platform) : IHypervisorSession, IGuestOperations
{
    private VSphereGuestOperations? _guest;

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
            case VSphereArtifactKinds.RestoredVm when artifact.NativeId is { Length: > 0 } vm:
                await VSphereRestoreSession.DestroyAsync(Vim, new ManagedObjectReference("VirtualMachine", vm), cancellationToken).ConfigureAwait(false);
                log.Info($"Removed VM {artifact.NativeRef} ({vm}) that an unfinished restore created.");
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

    public ValueTask DisposeAsync() => Vim.DisposeAsync();
}
