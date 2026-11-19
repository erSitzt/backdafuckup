using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>
/// A restore to vSphere as a new VM or to the original location. Each disk is created as a new (thin) disk file on a
/// proxy VM (<c>ReconfigVM_Task</c> with fileOperation create, in a folder named after the VM), written there over NBD
/// with only the restore point's data regions, and detached again without removing the file. Then the VM is created
/// from the backed-up configuration (<see cref="VSphereConfig"/>) around these files. Disposing without
/// <see cref="CompleteAsync"/> removes only what this restore created: its VM, or else its disk files.
/// </summary>
internal sealed partial class VSphereRestoreSession : IVmRestoreSession
{
    private readonly VSphereHypervisorSession _owner;
    private readonly VmRestorePlan _plan;
    private readonly IOperationLog _log;
    private readonly IArtifactLedger _ledger;
    private readonly VSphereConfig? _config;
    private readonly Dictionary<string, (string Name, ManagedObjectReference Datacenter, ManagedObjectReference VmFolder)> _datastores = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _directories = new(StringComparer.Ordinal);
    private readonly List<CreatedDisk> _created = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ProxyVm? _proxy;
    private ManagedObjectReference? _vm;
    private string? _vmInstanceUuid;
    private Guid? _vmArtifact;
    private (ManagedObjectReference Ref, string InstanceUuid, string Name)? _replaced;
    private string? _replacedUuid;
    private bool _completed;

    public VSphereRestoreSession(VSphereHypervisorSession owner, VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger)
    {
        _owner = owner;
        _plan = plan;
        _log = log;
        _ledger = ledger;
        _config = plan.Metadata.Format == VSphereArtifactKinds.MetadataFormat ? JsonSerializer.Deserialize<VSphereConfig>(plan.Metadata.Content) : null;
    }

    private VimSession Vim => _owner.Vim;

    private VSphereConfig Config => _config ?? throw new InvalidOperationException($"The restore point's VM configuration is in format {_plan.Metadata.Format}, not a vSphere one.");

    private IReadOnlyList<string> OriginalIds => _plan.OriginalVmIds.Count > 0 ? _plan.OriginalVmIds : [_plan.Manifest.Source.VmId];

    public async Task<RestoreValidation> ValidateAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (_config is null)
        {
            errors.Add($"The restore point holds a VM configuration in format {_plan.Metadata.Format}; a vSphere restore needs {VSphereArtifactKinds.MetadataFormat}.");
            return new RestoreValidation(errors, warnings);
        }

        if (_plan.QuickRollback)
        {
            return await ValidateRollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_plan.Resume)
        {
            errors.Add("vSphere VMs are backed up without memory: the VM can only be booted from its disks.");
        }

        var datastores = (await Vim.RetrieveAllAsync("Datastore", ["summary.name", "summary.freeSpace", "summary.accessible"], cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => d.Obj.Value, StringComparer.Ordinal);
        var needed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, name, capacity) in _plan.Manifest.Disks.Select(d => (d.Key, d.Name, d.CapacityBytes)).Concat(_plan.Manifest.ExcludedDisks.Select(d => (d.Key, d.Name, d.CapacityBytes))))
        {
            if (!_plan.DiskStorage.TryGetValue(key, out var id) || string.IsNullOrEmpty(id))
            {
                errors.Add($"No target datastore selected for disk {name}.");
                continue;
            }

            needed[id] = needed.GetValueOrDefault(id) + capacity;
        }

        foreach (var (id, bytes) in needed)
        {
            if (!datastores.TryGetValue(id, out var datastore))
            {
                errors.Add($"Datastore {id} does not exist in vCenter {Vim.Endpoint}.");
            }
            else if (datastore.Str("summary.accessible") == "false")
            {
                errors.Add($"Datastore {datastore.Str("summary.name")} ({id}) is not accessible.");
            }
            else if (long.TryParse(datastore.Str("summary.freeSpace"), out var free) && free < bytes)
            {
                warnings.Add($"Datastore {datastore.Str("summary.name")} has {free / (1L << 30)} GiB free, the restored disks are up to {bytes / (1L << 30)} GiB; " +
                             "they are created thin and take only the space of their data.");
            }
        }

        var networks = (await Vim.RetrieveAllAsync("Network", ["name"], cancellationToken).ConfigureAwait(false)).Select(n => n.Obj.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var network in _plan.NetworkMap.Values.Distinct(StringComparer.Ordinal))
        {
            if (!networks.Contains(network))
            {
                errors.Add($"Target network {network} does not exist in vCenter {Vim.Endpoint}.");
            }
        }

        if (await PlacementAsync(cancellationToken).ConfigureAwait(false) is { Problem: { } problem })
        {
            errors.Add(problem);
        }

        if (!string.IsNullOrEmpty(_plan.TargetHostId) && !await ExistsAsync(new ManagedObjectReference("HostSystem", _plan.TargetHostId), cancellationToken).ConfigureAwait(false))
        {
            errors.Add($"Target ESXi host {_plan.TargetHostId} does not exist in vCenter {Vim.Endpoint}.");
        }

        if (errors.Count == 0)
        {
            try
            {
                await ProxyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        var original = _plan.Mode == RestoreMode.OriginalLocation ? await FindOriginalAsync(cancellationToken).ConfigureAwait(false) : null;
        if (original is { } existing && !_plan.ReplaceExisting)
        {
            errors.Add($"The original VM {existing.Name} ({existing.InstanceUuid}) still exists. Choose 'Replace existing VM' or restore to another location.");
        }

        var vms = await Vim.RetrieveAllAsync("VirtualMachine", ["name", "runtime.powerState", "config.instanceUuid"], cancellationToken).ConfigureAwait(false);
        var sameName = vms.Where(v => v.Str("name") == _plan.VmName && v.Str("config.instanceUuid") != original?.InstanceUuid).ToList();
        var running = sameName.FirstOrDefault(v => v.Str("runtime.powerState") != "poweredOff");
        if (_plan.Mode == RestoreMode.OriginalLocation && running is not null)
        {
            errors.Add($"A VM named '{_plan.VmName}' ({running.Str("config.instanceUuid")}, {running.Str("runtime.powerState")}) is not the backed-up VM ({_plan.Manifest.Source.VmId}) " +
                       "and is not powered off. Restore to another location, or shut it down or rename it first.");
        }
        else if (sameName.Count > 0)
        {
            warnings.Add($"A VM named '{_plan.VmName}' already exists.");
        }

        if (_plan.Mode == RestoreMode.AlternateLocation && !_plan.RegenerateMacs && vms.Any(v => OriginalIds.Contains(v.Str("config.instanceUuid") ?? string.Empty, StringComparer.Ordinal)))
        {
            warnings.Add("The original VM exists in this vCenter; keeping its MAC addresses creates duplicate MACs on the network.");
        }

        return new RestoreValidation(errors, warnings);
    }

    public async Task<IDiskWriter> CreateDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_plan.QuickRollback)
        {
            return await RollbackDiskAsync(target, cancellationToken).ConfigureAwait(false);
        }
        var created = await CreateOnProxyAsync(target, cancellationToken).ConfigureAwait(false);
        var pool = _owner.Provider.Proxies!;
        IProxyDisk? proxyDisk = null;
        try
        {
            proxyDisk = await pool.AttachAsync(created.Proxy.Proxy.Id, created.Disk.Uuid, target.CapacityBytes, writable: true, cancellationToken).ConfigureAwait(false);
            var writer = await proxyDisk.OpenWriterAsync(4, 8, cancellationToken).ConfigureAwait(false);
            _log.Info($"Created disk {target.Name} as {created.Disk.FileName} ({Bytes(target.CapacityBytes)}, thin) on proxy {created.Proxy.Proxy.Name} " +
                      $"(VM {created.Proxy.VmName}, SCSI {created.Slot.Bus}:{created.Slot.Unit}); writing {proxyDisk.Description}.");
            return new RestoreDiskWriter(this, writer, proxyDisk, created.Proxy, created.Slot, created.DeviceKey, created.Disk.FileName, target.CapacityBytes, created.HotAddArtifact);
        }
        catch
        {
            if (proxyDisk is not null)
            {
                await proxyDisk.DisposeAsync().ConfigureAwait(false);
            }

            await DetachAsync(created.Proxy, created.Slot, created.DeviceKey, created.Disk.FileName, target.CapacityBytes, created.HotAddArtifact).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A disk left out of the backup: created empty on the proxy and detached at once.</summary>
    public async Task<string> CreateEmptyDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_plan.QuickRollback)
        {
            return await LeaveDiskAsync(target, cancellationToken).ConfigureAwait(false);
        }
        var created = await CreateOnProxyAsync(target, cancellationToken).ConfigureAwait(false);
        await DetachAsync(created.Proxy, created.Slot, created.DeviceKey, created.Disk.FileName, target.CapacityBytes, created.HotAddArtifact).ConfigureAwait(false);
        _log.Info($"Created the empty disk {target.Name} as {created.Disk.FileName} ({Bytes(target.CapacityBytes)}): it was not in the backup.");
        return created.Disk.FileName;
    }

    public async Task<RestoredVm> CreateVmAsync(IReadOnlyDictionary<string, string> sourceToNewDiskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceToNewDiskId);
        if (_plan.QuickRollback)
        {
            return await FinishRollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        var config = Config;
        var (folder, pool, problem) = await PlacementAsync(cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }

        if (_plan.Mode == RestoreMode.OriginalLocation && _plan.ReplaceExisting && await FindOriginalAsync(cancellationToken).ConfigureAwait(false) is { } original)
        {
            if (original.InstanceUuid != _plan.Manifest.Source.VmId)
            {
                _log.Info($"The original VM is {original.Name} ({original.InstanceUuid}): it replaced the backed-up VM ({_plan.Manifest.Source.VmId}) at an earlier restore to the original location.");
            }

            await ReplaceOriginalAsync(original.Ref, original.InstanceUuid, original.Name, cancellationToken).ConfigureAwait(false);
        }

        var first = _created.FirstOrDefault() ?? throw new InvalidOperationException($"No disk of {config.Name} was restored, so there is no folder for the VM.");
        var vmx = first.FileName[..(first.FileName.LastIndexOf('/') + 1)] + SafeName(_plan.VmName) + ".vmx";
        var builder = new VSphereVmSpecBuilder(Vim, config, _log);
        var connect = await ConnectNicsAsync(cancellationToken).ConfigureAwait(false);
        var changes = await builder.DeviceChangesAsync(sourceToNewDiskId, _plan.NetworkMap, connect, keepMacs: !_plan.RegenerateMacs, cancellationToken).ConfigureAwait(false);
        var spec = builder.ConfigSpec(_plan.VmName, vmx, changes, _plan.Mode == RestoreMode.OriginalLocation ? config.BiosUuid : null);

        _vmArtifact = await _ledger.RecordAsync(VSphereArtifactKinds.RestoredVm, string.Empty, _plan.VmName, null, cancellationToken).ConfigureAwait(false);
        var task = await Vim.StartTaskAsync("CreateVM_Task", folder!, cancellationToken,
            spec, VimXml.Ref("pool", pool!), string.IsNullOrEmpty(_plan.TargetHostId) ? null : VimXml.Ref("host", new ManagedObjectReference("HostSystem", _plan.TargetHostId))).ConfigureAwait(false);
        _vm = (await Vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false))?.AsRef()
            ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: CreateVM_Task for {_plan.VmName} returned no VM.");
        await _ledger.UpdateAsync(_vmArtifact.Value, ArtifactState.Active, _vm.Value, cancellationToken).ConfigureAwait(false);
        var row = (await Vim.RetrievePropertiesAsync([_vm], ["config.instanceUuid", "config.uuid", "config.extraConfig"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        _vmInstanceUuid = row?.Str("config.instanceUuid") ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: the new VM {_vm.Value} has no instance UUID.");
        var names = await NamesAsync([folder!, pool!], cancellationToken).ConfigureAwait(false);
        _log.Info($"Created VM '{_plan.VmName}' ({_vmInstanceUuid}, BIOS UUID {row.Str("config.uuid")}) in folder '{names[0]}' and resource pool '{names[1]}' " +
                  $"from the backed-up configuration of {config.Name}; its files are in {vmx[..(vmx.LastIndexOf('/') + 1)]}.");
        return new RestoredVm(_vmInstanceUuid, _plan.VmName, VSphereDevices.GenerationId(row["config.extraConfig"]));
    }

    public async Task<RestoredVm> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_plan.QuickRollback)
        {
            return await CompleteRollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        var vm = _vm ?? throw new InvalidOperationException("CreateVmAsync must be called first.");
        if (_replaced is { } replaced && _plan.DeleteReplaced)
        {
            await DestroyAsync(replaced.Ref, cancellationToken).ConfigureAwait(false);
            _log.Info($"Deleted the replaced VM {replaced.Name} ({replaced.InstanceUuid}).");
            _replaced = null;
        }

        // From here on the restore is final.
        _completed = true;
        foreach (var disk in _created)
        {
            await _ledger.UpdateAsync(disk.Artifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        await _ledger.UpdateAsync(_vmArtifact!.Value, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        if (_plan.PowerOn)
        {
            _log.Info($"Starting the restored VM {_plan.VmName}.");
            await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOnVM_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return new RestoredVm(_vmInstanceUuid!, _plan.VmName, ReplacedVmId: _replacedUuid);
    }

    public async ValueTask DisposeAsync()
    {
        if (_plan.QuickRollback)
        {
            if (!_completed)
            {
                await UndoRollbackAsync().ConfigureAwait(false);
            }

            _lock.Dispose();
            return;
        }

        if (!_completed)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                if (_vm is not null)
                {
                    // Our own VM, created by this restore: with it go the disk files it holds.
                    await DestroyAsync(_vm, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    foreach (var disk in _created)
                    {
                        await DeleteDiskFileAsync(Vim, disk.FileName, disk.Datacenter, timeout.Token).ConfigureAwait(false);
                    }
                }

                foreach (var disk in _created)
                {
                    await _ledger.UpdateAsync(disk.Artifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
                }

                if (_vmArtifact is { } vmArtifact)
                {
                    await _ledger.UpdateAsync(vmArtifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
                }

                if (_replaced is { } replaced)
                {
                    await RenameAsync(replaced.Ref, replaced.Name, timeout.Token).ConfigureAwait(false);
                    _log.Info($"Restore failed; the original VM {replaced.Name} keeps its name again (it was shut down).");
                }

                if (_created.Count > 0 || _vm is not null)
                {
                    _log.Info($"Removed the partially restored VM and disks{(_directories.Count > 0 ? $" (the folder {string.Join(", ", _directories.Values)} stays, empty)" : string.Empty)}.");
                }
            }
            catch (Exception ex) when (ex is VimFaultException or VimTaskException or HttpRequestException or IOException or OperationCanceledException)
            {
                _log.Warning($"Cleanup after the failed restore was incomplete ({ex.Message}); leftovers are removed automatically later.");
                foreach (var disk in _created)
                {
                    await _ledger.UpdateAsync(disk.Artifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
                }

                if (_vmArtifact is { } vmArtifact)
                {
                    await _ledger.UpdateAsync(vmArtifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        _lock.Dispose();
    }

    /// <summary>Deletes a disk file a restore created (a file that is gone already is fine).</summary>
    internal static async Task DeleteDiskFileAsync(VimSession vim, string fileName, ManagedObjectReference datacenter, CancellationToken cancellationToken)
    {
        var manager = vim.Content.VirtualDiskManager ?? throw new InvalidOperationException($"vCenter {vim.Endpoint} has no VirtualDiskManager.");
        try
        {
            var task = await vim.StartTaskAsync("DeleteVirtualDisk_Task", manager, cancellationToken, VimXml.E("name", fileName), VimXml.Ref("datacenter", datacenter)).ConfigureAwait(false);
            await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (VimTaskException ex) when (ex.FaultType == "FileNotFound")
        {
        }
    }

    /// <summary>Powers off and destroys a VM (a restore's own VM, or the replaced one when asked to delete it).</summary>
    internal static async Task DestroyAsync(VimSession vim, ManagedObjectReference vm, CancellationToken cancellationToken)
    {
        var state = (await vim.RetrievePropertiesAsync([vm], ["runtime.powerState"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("runtime.powerState");
        if (state is null)
        {
            return;
        }

        if (state != "poweredOff")
        {
            await vim.WaitForTaskAsync(await vim.StartTaskAsync("PowerOffVM_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        await vim.WaitForTaskAsync(await vim.StartTaskAsync("Destroy_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private Task DestroyAsync(ManagedObjectReference vm, CancellationToken cancellationToken) => DestroyAsync(Vim, vm, cancellationToken);

    private sealed record CreatedDisk(string FileName, ManagedObjectReference Datacenter, Guid Artifact, string Uuid);

    private sealed record ProxyCreated(ProxyVm Proxy, ScsiSlot Slot, int DeviceKey, CreatedDisk Disk, Guid HotAddArtifact);

    /// <summary>Creates the disk file on a proxy VM (a ledger row first for the file and for the attachment).</summary>
    private async Task<ProxyCreated> CreateOnProxyAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var (datastoreName, datacenter, _) = await DatastoreAsync(target.StorageId, cancellationToken).ConfigureAwait(false);
        var directory = await DirectoryAsync(target.StorageId, cancellationToken).ConfigureAwait(false);
        string fileName;
        lock (_created)
        {
            var index = _created.Count;
            fileName = $"[{datastoreName}] {directory}/{SafeName(_plan.VmName)}{(index == 0 ? string.Empty : "_" + index.ToString(CultureInfo.InvariantCulture))}.vmdk";
        }

        var proxy = await ProxyAsync(cancellationToken).ConfigureAwait(false);
        var slot = _owner.Provider.Slots.Reserve(proxy)
            ?? throw new InvalidOperationException($"Proxy {proxy.Proxy.Name} (VM {proxy.VmName}) has no free SCSI place left for disk {target.Name}.");
        Guid? hotAdd = null;
        try
        {
            var fileArtifact = await _ledger.RecordAsync(VSphereArtifactKinds.RestoredDisk, fileName, datacenter.Value, null, cancellationToken).ConfigureAwait(false);
            hotAdd = await _ledger.RecordAsync(VSphereArtifactKinds.HotAddDisk, $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}", proxy.Vm.Value,
                JsonSerializer.Serialize(new HotAddRecord(-1, slot.ControllerKey, slot.Bus, slot.Unit, fileName, target.CapacityBytes)), cancellationToken).ConfigureAwait(false);
            var (key, uuid, actual) = await VSphereHotAdd.CreateAsync(Vim, proxy, slot, fileName, target.CapacityBytes, thin: true, cancellationToken).ConfigureAwait(false);
            var disk = new CreatedDisk(actual, datacenter, fileArtifact, uuid);
            lock (_created)
            {
                _created.Add(disk);
            }

            await _ledger.UpdateAsync(hotAdd.Value, ArtifactState.Active, $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}:{key.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
                .ConfigureAwait(false);
            return new ProxyCreated(proxy, slot, key, disk, hotAdd.Value);
        }
        catch
        {
            _owner.Provider.Slots.Release(proxy.Vm, slot);
            if (hotAdd is { } id)
            {
                await _ledger.UpdateAsync(id, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Detaches a restore's disk from the proxy VM (the file stays), frees its SCSI place and closes its ledger row.</summary>
    private async Task DetachAsync(ProxyVm proxy, ScsiSlot slot, int deviceKey, string fileName, long capacityBytes, Guid artifact)
    {
        try
        {
            await VSphereHotAdd.DetachAsync(Vim, proxy.Vm, deviceKey, slot, capacityBytes, CancellationToken.None).ConfigureAwait(false);
            await _ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VimFaultException or VimTaskException or HttpRequestException or TimeoutException)
        {
            _log.Warning($"{fileName} could not be detached from proxy VM {proxy.VmName} ({ex.Message}); it is detached later.");
            await _ledger.UpdateAsync(artifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _owner.Provider.Slots.Release(proxy.Vm, slot);
        }
    }

    /// <summary>
    /// Whether the NICs start connected: as asked; else a copy whose original still exists starts with its NICs
    /// disconnected (the guest would come up with the original's addresses), everything else as it was.
    /// </summary>
    private async Task<bool> ConnectNicsAsync(CancellationToken cancellationToken)
    {
        if (_plan.ConnectNics is { } asked)
        {
            if (!asked)
            {
                _log.Info($"The NICs of {_plan.VmName} start disconnected, as asked.");
            }

            return asked;
        }

        if (_plan.Mode != RestoreMode.AlternateLocation || await FindOriginalAsync(cancellationToken).ConfigureAwait(false) is not { } original)
        {
            return true;
        }

        _log.Info($"The NICs of {_plan.VmName} start disconnected: the original VM {original.Name} still exists, and the copy would come up with its network settings " +
                     "(IP addresses, host name). Connect them in vCenter once that is sorted out.");
        return false;
    }

    /// <summary>Folder and resource pool: as asked, else where the backed-up VM was, else the target host's cluster and its datacenter's VM folder.</summary>
    private async Task<(ManagedObjectReference? Folder, ManagedObjectReference? Pool, string? Problem)> PlacementAsync(CancellationToken cancellationToken)
    {
        var config = Config;
        var folderId = _plan.TargetFolderId ?? config.FolderId;
        var poolId = _plan.TargetResourcePoolId ?? config.ResourcePoolId;
        if (poolId is null && !string.IsNullOrEmpty(_plan.TargetHostId))
        {
            var parent = (await Vim.RetrievePropertiesAsync([new ManagedObjectReference("HostSystem", _plan.TargetHostId)], ["parent"], cancellationToken).ConfigureAwait(false))
                .FirstOrDefault()?["parent"]?.AsRef();
            poolId = parent is null ? null
                : (await Vim.RetrievePropertiesAsync([parent], ["resourcePool"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["resourcePool"]?.AsRef().Value;
        }

        if (folderId is null && _plan.DiskStorage.Values.FirstOrDefault(s => !string.IsNullOrEmpty(s)) is { } datastore)
        {
            folderId = (await DatastoreAsync(datastore, cancellationToken).ConfigureAwait(false)).VmFolder.Value;
        }

        if (folderId is null || poolId is null)
        {
            return (null, null, $"Choose the VM folder and resource pool for {_plan.VmName}: the restore point does not record where {config.Name} was.");
        }

        var folder = new ManagedObjectReference("Folder", folderId);
        var pool = new ManagedObjectReference(poolId.StartsWith("resgroup-v", StringComparison.Ordinal) ? "VirtualApp" : "ResourcePool", poolId);
        if (!await ExistsAsync(folder, cancellationToken).ConfigureAwait(false))
        {
            return (null, null, $"VM folder {folderId} does not exist in vCenter {Vim.Endpoint}; choose another.");
        }

        return await ExistsAsync(pool, cancellationToken).ConfigureAwait(false)
            ? (folder, pool, null)
            : (null, null, $"Resource pool {poolId} does not exist in vCenter {Vim.Endpoint}; choose another.");
    }

    private async Task<bool> ExistsAsync(ManagedObjectReference obj, CancellationToken cancellationToken)
    {
        try
        {
            return (await Vim.RetrievePropertiesAsync([obj], ["name"], cancellationToken).ConfigureAwait(false)).Count > 0;
        }
        catch (VimFaultException ex) when (ex.FaultType == "ManagedObjectNotFound")
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> NamesAsync(IReadOnlyList<ManagedObjectReference> objects, CancellationToken cancellationToken)
    {
        var rows = await Vim.RetrievePropertiesAsync(objects, ["name"], cancellationToken).ConfigureAwait(false);
        return [.. objects.Select(o => rows.FirstOrDefault(r => r.Obj.Value == o.Value)?.Str("name") ?? o.Value)];
    }

    /// <summary>A datastore's name, its datacenter (file operations need it) and that datacenter's VM folder.</summary>
    private async Task<(string Name, ManagedObjectReference Datacenter, ManagedObjectReference VmFolder)> DatastoreAsync(string datastoreId, CancellationToken cancellationToken)
    {
        if (_datastores.TryGetValue(datastoreId, out var known))
        {
            return known;
        }

        var name = (await Vim.RetrievePropertiesAsync([new ManagedObjectReference("Datastore", datastoreId)], ["summary.name"], cancellationToken).ConfigureAwait(false))
            .FirstOrDefault()?.Str("summary.name") ?? throw new InvalidOperationException($"Datastore {datastoreId} does not exist in vCenter {Vim.Endpoint}.");
        foreach (var dc in await Vim.RetrieveAllAsync("Datacenter", ["datastore", "vmFolder"], cancellationToken).ConfigureAwait(false))
        {
            if (dc["datastore"]?.Items.Any(i => i.AsRef().Value == datastoreId) == true && dc["vmFolder"]?.AsRef() is { } vmFolder)
            {
                return _datastores[datastoreId] = (name, dc.Obj, vmFolder);
            }
        }

        throw new InvalidOperationException($"Datastore {name} ({datastoreId}) belongs to no datacenter of vCenter {Vim.Endpoint}.");
    }

    /// <summary>The restored VM's folder on the datastore: its name, or with _1, _2, … when that exists already.</summary>
    private async Task<string> DirectoryAsync(string datastoreId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_directories.TryGetValue(datastoreId, out var known))
            {
                return known;
            }

            var (datastoreName, datacenter, _) = await DatastoreAsync(datastoreId, cancellationToken).ConfigureAwait(false);
            var manager = Vim.Content.FileManager ?? throw new InvalidOperationException($"vCenter {Vim.Endpoint} has no FileManager.");
            var baseName = SafeName(_plan.VmName);
            for (var i = 0; i < 20; i++)
            {
                var name = i == 0 ? baseName : $"{baseName}_{i.ToString(CultureInfo.InvariantCulture)}";
                try
                {
                    await Vim.InvokeAsync("MakeDirectory", manager, cancellationToken,
                        VimXml.E("name", $"[{datastoreName}] {name}"), VimXml.Ref("datacenter", datacenter), VimXml.E("createParentDirectories", "false")).ConfigureAwait(false);
                    return _directories[datastoreId] = name;
                }
                catch (VimFaultException ex) when (ex.FaultType == "FileAlreadyExists")
                {
                }
            }

            throw new InvalidOperationException($"Datastore {datastoreName} has folders {baseName} to {baseName}_19 already; rename the restored VM.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The proxy that writes the disks: one that sees every target datastore (near the target host if one is set).</summary>
    private async Task<ProxyVm> ProxyAsync(CancellationToken cancellationToken)
    {
        if (_proxy is not null)
        {
            return _proxy;
        }

        var pool = _owner.Provider.Proxies ?? throw new InvalidOperationException("This server has no proxies to write vSphere disks.");
        var targets = new List<(string Id, string Name)>();
        foreach (var id in (_rollback?.Datastores ?? _plan.DiskStorage.Values).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal))
        {
            targets.Add((id, (await DatastoreAsync(id, cancellationToken).ConfigureAwait(false)).Name));
        }

        var (usable, refused) = await VSphereHotAdd.ResolveAsync(Vim, pool, cancellationToken).ConfigureAwait(false);
        var (chosen, notSeeing) = _owner.Provider.Slots.Choose(usable, _plan.TargetHostId, null, targets);
        if (chosen is null)
        {
            var reasons = refused.Concat(notSeeing).ToList();
            throw new InvalidOperationException(reasons.Count == 0
                ? $"No proxy is connected to write the disks of {_plan.VmName} (Infrastructure → Proxies)."
                : $"No proxy can write the disks of {_plan.VmName}: {string.Join("; ", reasons)}.");
        }

        return _proxy = chosen;
    }

    private async Task<(ManagedObjectReference Ref, string InstanceUuid, string Name)?> FindOriginalAsync(CancellationToken cancellationToken)
    {
        foreach (var id in OriginalIds.Reverse())
        {
            if (await VSphereInventory.FindVmAsync(Vim, id, cancellationToken).ConfigureAwait(false) is { } vm)
            {
                return (vm.Ref, id, vm.Name);
            }
        }

        return null;
    }

    private async Task ReplaceOriginalAsync(ManagedObjectReference original, string instanceUuid, string name, CancellationToken cancellationToken)
    {
        if ((await Vim.RetrievePropertiesAsync([original], ["runtime.powerState"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("runtime.powerState") != "poweredOff")
        {
            _log.Info($"Shutting down the original VM {name}.");
            try
            {
                await Vim.InvokeAsync("ShutdownGuest", original, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < 150 && (await Vim.RetrievePropertiesAsync([original], ["runtime.powerState"], cancellationToken).ConfigureAwait(false))
                         .FirstOrDefault()?.Str("runtime.powerState") != "poweredOff"; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (VimFaultException ex)
            {
                _log.Warning($"A clean shutdown of {name} is not possible ({ex.FaultMessage}); powering it off.");
            }

            if ((await Vim.RetrievePropertiesAsync([original], ["runtime.powerState"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("runtime.powerState") != "poweredOff")
            {
                await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOffVM_Task", original, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
        }

        var replacedName = $"{name} (replaced {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})";
        await RenameAsync(original, replacedName, cancellationToken).ConfigureAwait(false);
        _replaced = (original, instanceUuid, name);
        _replacedUuid = instanceUuid;
        _log.Info($"The original VM is renamed to '{replacedName}' and stays powered off.");
    }

    private async Task RenameAsync(ManagedObjectReference vm, string name, CancellationToken cancellationToken) =>
        await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("Rename_Task", vm, cancellationToken, VimXml.E("newName", name)).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>A VM name as a datastore folder and file name.</summary>
    internal static string SafeName(string name)
    {
        var chars = name.Trim().Select(c => c is '/' or '\\' or '[' or ']' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "restored-vm" : new string(chars);
    }

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GiB"
        : (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB";

    /// <summary>
    /// Writes a restored disk through the proxy: only the image's data regions, in pieces of up to 1 MiB, several at once;
    /// pieces of zeros are left out (the new disk is thin and reads zeros). Closing it releases the disk on the proxy and
    /// detaches it from the proxy VM, keeping the file.
    /// </summary>
    private sealed class RestoreDiskWriter(
        VSphereRestoreSession session, IDiskBlockWriter writer, IProxyDisk proxyDisk, ProxyVm proxy, ScsiSlot slot, int deviceKey, string fileName, long capacity, Guid artifact) : IDiskWriter
    {
        private const int Piece = 1 << 20;
        private long _transferred;
        private int _closed;

        public string DiskId => fileName;

        public long Capacity => capacity;

        public DiskWriterCapabilities Capabilities => DiskWriterCapabilities.RandomAccess | DiskWriterCapabilities.SkipsZeroRegions;

        public long TransferredBytes => Interlocked.Read(ref _transferred);

        public async Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(image);
            var pieces = new List<(long Offset, int Length)>();
            foreach (var extent in image.DataExtents)
            {
                for (var offset = extent.Offset; offset < extent.Offset + extent.Length; offset += Piece)
                {
                    pieces.Add((offset, (int)Math.Min(Piece, extent.Offset + extent.Length - offset)));
                }
            }

            var data = pieces.Sum(p => (long)p.Length);
            var done = image.Capacity - data;
            processed?.Report(done);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(writer.MaxConcurrentRequests, 1, 16), CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(pieces, options, async (piece, ct) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(piece.Length);
                try
                {
                    var memory = buffer.AsMemory(0, piece.Length);
                    await image.ReadAsync(piece.Offset, memory, ct).ConfigureAwait(false);
                    if (memory.Span.IndexOfAnyExcept((byte)0) >= 0)
                    {
                        await writer.WriteAsync(piece.Offset, memory, ct).ConfigureAwait(false);
                        Interlocked.Add(ref _transferred, piece.Length);
                    }

                    processed?.Report(Interlocked.Add(ref done, piece.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // The proxy lets go of the device first; only then is it detached from the proxy VM.
                try
                {
                    await proxyDisk.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                {
                    session._log.Warning($"{proxyDisk.Description}: releasing it on the proxy failed ({ex.Message}); detaching it anyway.");
                }

                await session.DetachAsync(proxy, slot, deviceKey, fileName, capacity, artifact).ConfigureAwait(false);
            }
        }
    }
}
