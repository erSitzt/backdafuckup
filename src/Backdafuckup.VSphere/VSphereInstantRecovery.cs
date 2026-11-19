using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>
/// Instant VM recovery on vSphere: the backup server's NFS export mounted as a datastore on one ESXi host
/// (<c>CreateNasDatastore</c>), the VM registered around the disk files there (<c>CreateVM_Task</c> without file
/// operations), moved to production storage by Storage vMotion (<c>RelocateVM_Task</c>), and at the end unregistered
/// (<c>UnregisterVM</c>: no file is deleted anywhere) and the datastore unmounted (<c>RemoveDatastore</c>), only when it is
/// our export and no VM is left on it.
/// </summary>
internal sealed class VSphereInstantRecovery(VSphereHypervisorSession owner)
{
    /// <summary>The layout's swap files are on the host's swap datastore, if it has one; they are not the VM's data.</summary>
    private static readonly string[] SwapFileTypes = ["swap", "uwswap"];

    private VimSession Vim => owner.Vim;

    public async Task<InstantRecoveryHost> GetHostAsync(string hostId, CancellationToken cancellationToken)
    {
        var host = new ManagedObjectReference("HostSystem", hostId);
        var row = await PropertiesAsync(host, ["name", "parent", "runtime.connectionState", "runtime.inMaintenanceMode", "config.network.vnic", "configManager.advancedOption", "datastore",
                "config.localSwapDatastore"], cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException($"ESXi host {hostId} does not exist in vCenter {Vim.Endpoint}.");
        var name = row.Str("name") ?? hostId;
        var addresses = (row["config.network.vnic"]?.Items ?? [])
            .Select(vnic => vnic.Child("spec")?.Child("ip")?.Str("ipAddress"))
            .Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any))
            .Select(a => a!).Distinct(StringComparer.Ordinal).ToList();

        var datastores = row["datastore"]?.Items.Select(d => d.AsRef()).ToList() ?? [];
        var mounts = datastores.Count == 0 ? 0
            : (await Vim.RetrievePropertiesAsync(datastores, ["summary.type"], cancellationToken).ConfigureAwait(false)).Count(d => d.Str("summary.type") is "NFS" or "NFS41");
        int? limit = null;
        if (row["configManager.advancedOption"]?.AsRef() is { } options)
        {
            try
            {
                var value = await Vim.InvokeAsync("QueryOptions", options, cancellationToken, VimXml.E("name", "NFS.MaxVolumes")).ConfigureAwait(false);
                limit = value?.Element.Parent?.Elements(VimXml.Ns + "returnval").Select(e => new VimObject(e)).FirstOrDefault(o => o.Str("key") == "NFS.MaxVolumes")?.Long("value") is { } max
                    ? (int)max : null;
            }
            catch (VimFaultException ex) when (ex.FaultType is "InvalidName" or "InvalidArgument")
            {
            }
        }

        var parent = row["parent"]?.AsRef();
        string? pool = null;
        if (parent is not null && await PropertiesAsync(parent, ["resourcePool"], cancellationToken).ConfigureAwait(false) is { } compute)
        {
            pool = compute["resourcePool"]?.AsRef().Value;
        }

        var problems = new List<string>();
        if (row.Str("runtime.connectionState") is { } state && state != "connected")
        {
            problems.Add($"ESXi host {name} is {state} in vCenter {Vim.Endpoint}.");
        }

        if (row.Str("runtime.inMaintenanceMode") == "true")
        {
            problems.Add($"ESXi host {name} is in maintenance mode.");
        }

        if (addresses.Count == 0)
        {
            problems.Add($"ESXi host {name} has no VMkernel NIC with an IPv4 address to mount NFS from.");
        }

        if (limit is { } max2 && mounts >= max2)
        {
            problems.Add($"ESXi host {name} mounts {mounts} NFS datastores, as many as NFS.MaxVolumes ({max2}) allows; unmount one or raise the limit.");
        }

        string? swap = null;
        if (row["config.localSwapDatastore"]?.AsRef() is { } swapDatastore)
        {
            swap = (await PropertiesAsync(swapDatastore, ["summary.name"], cancellationToken).ConfigureAwait(false))?.Str("summary.name") ?? swapDatastore.Value;
        }

        return new InstantRecoveryHost(hostId, name, parent?.Type == "ClusterComputeResource" ? parent.Value : null, pool, addresses, mounts, limit, problems, swap);
    }

    public InstantRecoveryVmLayout PlanVm(RestorePointManifest manifest, VmMetadataBlob metadata, bool keepDiskIdentity)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(metadata);
        var problems = new List<string>();
        var warnings = new List<string>();
        if (metadata.Format != VSphereArtifactKinds.MetadataFormat || JsonSerializer.Deserialize<VSphereConfig>(metadata.Content) is not { } config)
        {
            problems.Add($"The restore point of {manifest.Vm.Name} holds a VM configuration in format {metadata.Format}; an instant recovery on vSphere needs {VSphereArtifactKinds.MetadataFormat}.");
            return new InstantRecoveryVmLayout([], problems, warnings);
        }

        var builder = new VSphereVmSpecBuilder(Vim, config, NullOperationLog.Instance);
        if (builder.Devices.Any(d => d.Type == "VirtualTPM"))
        {
            warnings.Add($"{config.Name} has a virtual TPM; the instant recovery VM has none (a vTPM needs VM encryption). Windows boots, but BitLocker with a TPM protector asks for its recovery key.");
        }

        foreach (var disk in builder.Devices.Where(d => d.Type == "VirtualDisk"))
        {
            if (disk.Child("backing")?.Child("keyId") is not null)
            {
                problems.Add($"{disk.Child("deviceInfo")?.Str("label") ?? "A disk"} of {config.Name} was encrypted; an instant recovery cannot run encrypted disks.");
            }
        }

        var disks = new List<InstantRecoveryDiskLayout>();
        var all = manifest.Disks.Select(d => (d.Key, d.SourceDiskId, d.Name, d.CapacityBytes, Empty: false))
            .Concat(manifest.ExcludedDisks.Select(d => (d.Key, d.SourceDiskId, d.Name, d.CapacityBytes, Empty: true)));
        foreach (var (key, sourceDiskId, name, capacity, empty) in all)
        {
            if (capacity <= 0 || capacity % VSphereVmdkDescriptor.SectorSize != 0)
            {
                problems.Add($"Disk {name} of {config.Name} has {capacity} bytes, not a multiple of 512: it cannot be a flat disk.");
                continue;
            }

            var index = disks.Count.ToString(CultureInfo.InvariantCulture);
            var found = builder.FindDisk(sourceDiskId);
            if (found is null)
            {
                warnings.Add($"Disk {name} of {config.Name} is not in its backed-up configuration; the instant recovery VM has no such disk.");
                continue;
            }

            var uuid = VSphereVmdkDescriptor.Uuid(keepDiskIdentity ? VSphereVmSpecBuilder.RootBacking(found.Value.Disk)?.Str("uuid") : null);
            var flat = $"disk{index}-flat.vmdk";
            var descriptor = VSphereVmdkDescriptor.Create(flat, capacity, VSphereVmdkDescriptor.AdapterType(found.Value.Controller?.Type), config.HardwareVersion, uuid);
            disks.Add(new InstantRecoveryDiskLayout(key, sourceDiskId, name, capacity, $"disk{index}.vmdk", flat, Encoding.UTF8.GetBytes(descriptor), empty));
            if (empty)
            {
                warnings.Add($"Disk {name} was left out of the backup; in the instant recovery it starts empty.");
            }
        }

        if (disks.Count == 0 && problems.Count == 0)
        {
            problems.Add($"The restore point of {config.Name} has no disk to run the VM from.");
        }

        return new InstantRecoveryVmLayout(disks, problems, warnings, config.MemoryMb * 1024 * 1024);
    }

    public async Task<string> MountDatastoreAsync(string hostId, InstantRecoveryDatastore datastore, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken)
    {
        var host = new ManagedObjectReference("HostSystem", hostId);
        var row = await PropertiesAsync(host, ["name", "configManager.datastoreSystem"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ESXi host {hostId} does not exist in vCenter {Vim.Endpoint}.");
        var hostName = row.Str("name") ?? hostId;
        var system = row["configManager.datastoreSystem"]?.AsRef() ?? throw new InvalidOperationException($"ESXi host {hostName} has no datastore system in vCenter {Vim.Endpoint}.");
        var artifact = await ledger.RecordAsync(VSphereArtifactKinds.NasDatastore, string.Empty, hostId, JsonSerializer.Serialize(datastore), cancellationToken).ConfigureAwait(false);
        VimObject? created;
        try
        {
            created = await Vim.InvokeAsync("CreateNasDatastore", system, cancellationToken, VimXml.E(
                "spec",
                VimXml.E("remoteHost", datastore.RemoteHost),
                VimXml.E("remotePath", datastore.RemotePath),
                VimXml.E("localPath", datastore.Name),
                VimXml.E("accessMode", "readWrite"),
                VimXml.E("type", "NFS"))).ConfigureAwait(false);
        }
        catch (VimFaultException ex)
        {
            // vCenter answered: nothing was mounted.
            await ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(ex.FaultType is "DuplicateName" or "AlreadyExists"
                ? $"ESXi host {hostName} cannot mount {datastore.RemoteHost}:{datastore.RemotePath}: a datastore named {datastore.Name} exists already in vCenter {Vim.Endpoint}."
                : $"ESXi host {hostName} could not mount {datastore.RemoteHost}:{datastore.RemotePath} as NFS 3 datastore {datastore.Name}: {ex.FaultMessage} ({ex.FaultType}).", ex);
        }

        var id = created?.AsRef().Value ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: CreateNasDatastore on {hostName} returned no datastore.");
        await ledger.UpdateAsync(artifact, ArtifactState.Active, id, cancellationToken).ConfigureAwait(false);
        log.Info($"ESXi host {hostName} mounted {datastore.RemoteHost}:{datastore.RemotePath} as NFS 3 datastore {datastore.Name} ({id}).");
        return id;
    }

    public async Task<InstantRecoveryVm> CreateVmAsync(InstantRecoveryVmSpec spec, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Metadata.Format != VSphereArtifactKinds.MetadataFormat || JsonSerializer.Deserialize<VSphereConfig>(spec.Metadata.Content) is not { } config)
        {
            throw new InvalidOperationException($"The restore point of {spec.Manifest.Vm.Name} holds a VM configuration in format {spec.Metadata.Format}, not {VSphereArtifactKinds.MetadataFormat}.");
        }

        var host = new ManagedObjectReference("HostSystem", spec.HostId);
        var hostRow = await PropertiesAsync(host, ["name", "parent"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ESXi host {spec.HostId} does not exist in vCenter {Vim.Endpoint}.");
        var hostName = hostRow.Str("name") ?? spec.HostId;
        var (folder, pool) = await PlacementAsync(hostRow, spec, cancellationToken).ConfigureAwait(false);

        var builder = new VSphereVmSpecBuilder(Vim, config, log);
        var files = spec.Disks.ToDictionary(d => d.SourceDiskId, d => $"[{spec.DatastoreName}] {spec.Directory}/{d.Descriptor}", StringComparer.Ordinal);
        var changes = await builder.DeviceChangesAsync(files, spec.NetworkMap, spec.ConnectNics, keepMacs: false, cancellationToken).ConfigureAwait(false);
        var vmx = $"[{spec.DatastoreName}] {spec.Directory}/{VSphereRestoreSession.SafeName(spec.Name)}.vmx";
        var annotation = $"Instant recovery of {config.Name} from its restore point of {spec.Manifest.CreatedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, " +
                         $"running from datastore {spec.DatastoreName} that the backup server serves. Migrate it to production storage or stop it in the backup console.";
        var configSpec = builder.ConfigSpec(spec.Name, vmx, changes, biosUuid: null, hostLocalSwap: true, annotation);

        var artifact = await ledger.RecordAsync(VSphereArtifactKinds.InstantRecoveryVm, string.Empty, spec.DatastoreId,
            JsonSerializer.Serialize(new InstantRecoveryVmRecord(spec.Name, spec.DatastoreName)), cancellationToken).ConfigureAwait(false);
        ManagedObjectReference vm;
        try
        {
            var task = await Vim.StartTaskAsync("CreateVM_Task", folder, cancellationToken, configSpec, VimXml.Ref("pool", pool), VimXml.Ref("host", host)).ConfigureAwait(false);
            vm = (await Vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false))?.AsRef()
                ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: CreateVM_Task for {spec.Name} returned no VM.");
        }
        catch (Exception ex) when (ex is VimFaultException or VimTaskException)
        {
            await ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"vCenter {Vim.Endpoint} could not create VM {spec.Name} on ESXi host {hostName} from datastore {spec.DatastoreName}: {ex.Message}", ex);
        }

        await ledger.UpdateAsync(artifact, ArtifactState.Active, vm.Value, cancellationToken).ConfigureAwait(false);
        var row = await PropertiesAsync(vm, ["config.instanceUuid"], cancellationToken).ConfigureAwait(false);
        var instanceUuid = row?.Str("config.instanceUuid") ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: the new VM {vm.Value} has no instance UUID.");
        log.Info($"Created VM {spec.Name} ({vm.Value}) on ESXi host {hostName} from the backed-up configuration of {config.Name}: {spec.Disks.Count} disk(s) and its files in " +
                 $"[{spec.DatastoreName}] {spec.Directory}; NICs start {(spec.ConnectNics ? "connected" : "disconnected")}, with new MAC addresses.");
        return new InstantRecoveryVm(vm.Value, instanceUuid, spec.Name);
    }

    public async Task PowerOnAsync(string vmId, CancellationToken cancellationToken)
    {
        var vm = new ManagedObjectReference("VirtualMachine", vmId);
        await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOnVM_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstantRecoveryVmState> GetVmStateAsync(string vmId, string datastoreName, CancellationToken cancellationToken)
    {
        var vm = new ManagedObjectReference("VirtualMachine", vmId);
        var row = await PropertiesAsync(vm, ["name", "runtime.powerState", "runtime.host", "config.files", "config.hardware.device", "layoutEx.file"], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return InstantRecoveryVmState.Gone;
        }

        var files = new List<string>();
        if (row["config.files"] is { } directories)
        {
            files.AddRange(new[] { "vmPathName", "snapshotDirectory", "suspendDirectory", "logDirectory" }.Select(directories.Str).OfType<string>());
        }

        foreach (var disk in (row["config.hardware.device"]?.Items ?? []).Where(d => d.Type == "VirtualDisk"))
        {
            for (var backing = disk.Child("backing"); backing is not null; backing = backing.Child("parent"))
            {
                if (backing.Str("fileName") is { Length: > 0 } file)
                {
                    files.Add(file);
                }
            }
        }

        files.AddRange((row["layoutEx.file"]?.Items ?? []).Where(f => !SwapFileTypes.Contains(f.Str("type"), StringComparer.Ordinal)).Select(f => f.Str("name")).OfType<string>());
        var prefix = $"[{datastoreName}]";
        var distinct = files.Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return new InstantRecoveryVmState(
            true, row.Str("name"), row.Str("runtime.powerState"), row["runtime.host"]?.AsRef().Value,
            [.. distinct.Where(f => f.StartsWith(prefix, StringComparison.Ordinal))],
            [.. distinct.Where(f => !f.StartsWith(prefix, StringComparison.Ordinal))]);
    }

    public async Task<string> StartMigrationAsync(string vmId, InstantRecoveryMigration target, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var vm = new ManagedObjectReference("VirtualMachine", vmId);
        var row = await PropertiesAsync(vm, ["name", "runtime.host", "config.hardware.device"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"VM {vmId} does not exist in vCenter {Vim.Endpoint}.");
        var name = row.Str("name") ?? vmId;
        var datastore = new ManagedObjectReference("Datastore", target.DatastoreId);
        var ds = await PropertiesAsync(datastore, ["summary.name", "summary.accessible", "host"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Datastore {target.DatastoreId} does not exist in vCenter {Vim.Endpoint}.");
        var dsName = ds.Str("summary.name") ?? target.DatastoreId;
        if (ds.Str("summary.accessible") == "false")
        {
            throw new InvalidOperationException($"Datastore {dsName} is not accessible; {name} cannot move there.");
        }

        var hostId = target.HostId ?? row["runtime.host"]?.AsRef().Value;
        var mountedOn = (ds["host"]?.Items ?? []).Select(m => m.Ref("key")?.Value).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (hostId is not null && !mountedOn.Contains(hostId))
        {
            var hostName = (await PropertiesAsync(new ManagedObjectReference("HostSystem", hostId), ["name"], cancellationToken).ConfigureAwait(false))?.Str("name") ?? hostId;
            throw new InvalidOperationException($"ESXi host {hostName} does not mount datastore {dsName}; choose a datastore it mounts, or another host as well.");
        }

        var locators = (row["config.hardware.device"]?.Items ?? []).Where(d => d.Type == "VirtualDisk").Select(disk => VimXml.E(
            "disk",
            VimXml.E("diskId", disk.Int("key") ?? 0),
            VimXml.Ref("datastore", datastore),
            VimXml.Typed("diskBackingInfo", "VirtualDiskFlatVer2BackingInfo",
                VimXml.E("fileName", string.Empty),
                VimXml.E("diskMode", disk.Child("backing")?.Str("diskMode") ?? "persistent"),
                VimXml.E("thinProvisioned", target.Thin ? "true" : "false"),
                target.Thin ? null : VimXml.E("eagerlyScrub", "false")))).ToList();
        var spec = VimXml.E(
            "spec",
            VimXml.Ref("datastore", datastore),
            target.ResourcePoolId is { } pool ? VimXml.Ref("pool", PoolRef(pool)) : null,
            target.HostId is { } host ? VimXml.Ref("host", new ManagedObjectReference("HostSystem", host)) : null,
            locators);
        var task = await Vim.StartTaskAsync("RelocateVM_Task", vm, cancellationToken, spec, VimXml.E("priority", "defaultPriority")).ConfigureAwait(false);
        log.Info($"Moving VM {name} to datastore {dsName} ({(target.Thin ? "thin" : "thick")} disks) by Storage vMotion: vCenter task {task.Value}.");
        return task.Value;
    }

    public async Task<InstantRecoveryTask> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = new ManagedObjectReference("Task", taskId);
        var row = await PropertiesAsync(task, ["info.state", "info.progress", "info.error"], cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"vCenter {Vim.Endpoint} no longer knows task {taskId}.");
        var error = row["info.error"];
        return new InstantRecoveryTask(
            row.Str("info.state") ?? "running",
            row["info.progress"] is { } progress && int.TryParse(progress.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) ? percent : null,
            error is null ? null : $"{error.Str("localizedMessage")} ({error.Child("fault")?.Type})");
    }

    public async Task RemoveVmAsync(string vmId, string datastoreName, IOperationLog log, CancellationToken cancellationToken)
    {
        var vm = new ManagedObjectReference("VirtualMachine", vmId);
        var state = await GetVmStateAsync(vmId, datastoreName, cancellationToken).ConfigureAwait(false);
        if (!state.Exists)
        {
            log.Info($"VM {vmId} is gone from vCenter {Vim.Endpoint} already.");
            return;
        }

        Guard(state, vmId, datastoreName);
        if (state.PowerState != "poweredOff")
        {
            try
            {
                await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOffVM_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (VimTaskException ex) when (ex.FaultType == "InvalidPowerState")
            {
            }

            log.Info($"Powered off VM {state.Name}.");
            state = await GetVmStateAsync(vmId, datastoreName, cancellationToken).ConfigureAwait(false);
            if (!state.Exists)
            {
                return;
            }

            Guard(state, vmId, datastoreName);
        }

        await Vim.InvokeAsync("UnregisterVM", vm, cancellationToken).ConfigureAwait(false);
        log.Info($"Removed VM {state.Name} ({vmId}) from vCenter {Vim.Endpoint}; its files were all on datastore {datastoreName}, which goes with the instant recovery.");

        static void Guard(InstantRecoveryVmState state, string vmId, string datastoreName)
        {
            if (state.FilesElsewhere.Count > 0)
            {
                throw new InstantRecoveryRefusedException(
                    $"VM {state.Name} ({vmId}) is not removed: it has files on other datastores than {datastoreName} ({string.Join(", ", state.FilesElsewhere)}). " +
                    "Migrate it to production storage, or remove those disks from it in vCenter first.");
            }
        }
    }

    public async Task UnmountDatastoreAsync(string datastoreId, InstantRecoveryDatastore datastore, IOperationLog log, CancellationToken cancellationToken)
    {
        var reference = new ManagedObjectReference("Datastore", datastoreId);
        var row = await PropertiesAsync(reference, ["summary.name", "summary.type", "info", "host", "vm"], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            log.Info($"Datastore {datastore.Name} ({datastoreId}) is gone from vCenter {Vim.Endpoint} already.");
            return;
        }

        var name = row.Str("summary.name") ?? datastoreId;
        var nas = row["info"]?.Child("nas");
        if (row.Str("summary.type") is not ("NFS" or "NFS41") || nas?.Str("remotePath") != datastore.RemotePath
            || !string.Equals(nas.Str("remoteHost"), datastore.RemoteHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstantRecoveryRefusedException(
                $"Datastore {name} ({datastoreId}) is not unmounted: it is not the export {datastore.RemoteHost}:{datastore.RemotePath} of this backup server " +
                $"(it is {row.Str("summary.type")} {nas?.Str("remoteHost")}:{nas?.Str("remotePath")}).");
        }

        var vms = row["vm"]?.Items.Select(v => v.AsRef()).ToList() ?? [];
        if (vms.Count > 0)
        {
            var names = (await Vim.RetrievePropertiesAsync(vms, ["name"], cancellationToken).ConfigureAwait(false)).Select(v => $"{v.Str("name")} ({v.Obj.Value})");
            throw new InstantRecoveryRefusedException($"Datastore {name} is not unmounted: VMs are still registered on it: {string.Join(", ", names)}.");
        }

        foreach (var hostId in (row["host"]?.Items ?? []).Select(m => m.Ref("key")).OfType<ManagedObjectReference>())
        {
            var host = await PropertiesAsync(hostId, ["name", "configManager.datastoreSystem"], cancellationToken).ConfigureAwait(false);
            if (host?["configManager.datastoreSystem"]?.AsRef() is not { } system)
            {
                continue;
            }

            try
            {
                await Vim.InvokeAsync("RemoveDatastore", system, cancellationToken, VimXml.Ref("datastore", reference)).ConfigureAwait(false);
            }
            catch (VimFaultException ex) when (ex.FaultType is "NotFound" or "ManagedObjectNotFound")
            {
            }

            log.Info($"ESXi host {host.Str("name")} unmounted datastore {name} ({datastore.RemoteHost}:{datastore.RemotePath}).");
        }
    }

    /// <summary>
    /// After a crash: an instant recovery VM whose session failed is unregistered (it never ran for a user: its session
    /// failed before or while starting), the datastore unmounted once no VM is on it.
    /// </summary>
    public async Task CleanupAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        switch (artifact.Kind)
        {
            case VSphereArtifactKinds.InstantRecoveryVm when artifact.Data is { } data && JsonSerializer.Deserialize<InstantRecoveryVmRecord>(data) is { } record:
                if (artifact.NativeId is { Length: > 0 } vmId)
                {
                    await RemoveVmAsync(vmId, record.DatastoreName, log, cancellationToken).ConfigureAwait(false);
                }
                else if (artifact.NativeRef is { Length: > 0 } datastoreId)
                {
                    // CreateVM_Task was sent but its answer is unknown: a VM of that name on our datastore is ours.
                    var ds = await PropertiesAsync(new ManagedObjectReference("Datastore", datastoreId), ["vm"], cancellationToken).ConfigureAwait(false);
                    var vms = ds?["vm"]?.Items.Select(v => v.AsRef()).ToList() ?? [];
                    foreach (var vm in vms.Count == 0 ? [] : await Vim.RetrievePropertiesAsync(vms, ["name"], cancellationToken).ConfigureAwait(false))
                    {
                        if (vm.Str("name") == record.Name)
                        {
                            await RemoveVmAsync(vm.Obj.Value, record.DatastoreName, log, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                break;
            case VSphereArtifactKinds.NasDatastore when artifact.Data is { } data && JsonSerializer.Deserialize<InstantRecoveryDatastore>(data) is { } datastore:
                var id = artifact.NativeId is { Length: > 0 } known ? known : await FindDatastoreAsync(artifact.NativeRef, datastore.Name, cancellationToken).ConfigureAwait(false);
                if (id is not null)
                {
                    await UnmountDatastoreAsync(id, datastore, log, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    /// <summary>The datastore of that name the host mounts (CreateNasDatastore was sent, its answer is unknown).</summary>
    private async Task<string?> FindDatastoreAsync(string? hostId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hostId) || await PropertiesAsync(new ManagedObjectReference("HostSystem", hostId), ["datastore"], cancellationToken).ConfigureAwait(false) is not { } host)
        {
            return null;
        }

        var datastores = host["datastore"]?.Items.Select(d => d.AsRef()).ToList() ?? [];
        return datastores.Count == 0 ? null
            : (await Vim.RetrievePropertiesAsync(datastores, ["summary.name"], cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Str("summary.name") == name)?.Obj.Value;
    }

    /// <summary>The folder (as asked, else the datacenter's VM folder) and resource pool (as asked, else the host's cluster's).</summary>
    private async Task<(ManagedObjectReference Folder, ManagedObjectReference Pool)> PlacementAsync(VimObjectContent host, InstantRecoveryVmSpec spec, CancellationToken cancellationToken)
    {
        var hostName = host.Str("name") ?? host.Obj.Value;
        ManagedObjectReference? pool = spec.ResourcePoolId is { } asked ? PoolRef(asked) : null;
        var current = host["parent"]?.AsRef();
        if (pool is null && current is not null)
        {
            pool = (await PropertiesAsync(current, ["resourcePool"], cancellationToken).ConfigureAwait(false))?["resourcePool"]?.AsRef();
        }

        ManagedObjectReference? folder = spec.FolderId is { } folderId ? new ManagedObjectReference("Folder", folderId) : null;
        for (var depth = 0; folder is null && current is not null && depth < 16; depth++)
        {
            var row = await PropertiesAsync(current, current.Type == "Datacenter" ? ["vmFolder"] : ["parent"], cancellationToken).ConfigureAwait(false);
            if (current.Type == "Datacenter")
            {
                folder = row?["vmFolder"]?.AsRef();
                break;
            }

            current = row?["parent"]?.AsRef();
        }

        if (pool is null || await PropertiesAsync(pool, ["name"], cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"No resource pool for VM {spec.Name}: choose one, ESXi host {hostName}'s cluster has none in vCenter {Vim.Endpoint}.");
        }

        if (folder is null || await PropertiesAsync(folder, ["name"], cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"No VM folder for VM {spec.Name}: choose one, ESXi host {hostName}'s datacenter has none in vCenter {Vim.Endpoint}.");
        }

        return (folder, pool);
    }

    private static ManagedObjectReference PoolRef(string id) => new(id.StartsWith("resgroup-v", StringComparison.Ordinal) ? "VirtualApp" : "ResourcePool", id);

    /// <summary>The object's properties, or null when it does not exist (any more).</summary>
    private async Task<VimObjectContent?> PropertiesAsync(ManagedObjectReference obj, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        try
        {
            return (await Vim.RetrievePropertiesAsync([obj], paths, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        }
        catch (VimFaultException ex) when (ex.FaultType == "ManagedObjectNotFound")
        {
            return null;
        }
    }
}

/// <summary>What the ledger keeps of an instant recovery VM: its name and the datastore it runs from.</summary>
internal sealed record InstantRecoveryVmRecord(string Name, string DatastoreName);
