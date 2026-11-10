using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

internal sealed partial class XenHypervisorSession
{
    public async Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var xapi = await OpenDedicatedSessionAsync(cancellationToken).ConfigureAwait(false);
        return new XenRestoreSession(this, xapi, plan, log, ledger);
    }
}

/// <summary>
/// Restore of a whole VM: creates disks (VDI.create), streams them with import_raw_vdi, re-creates the VM with
/// import_metadata (mapping old to new disk UUIDs) and applies name, networks, MACs, affinity and power state.
/// Everything created is removed again if the restore does not complete.
/// </summary>
internal sealed class XenRestoreSession(XenHypervisorSession owner, XapiSession xapi, VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger) : IVmRestoreSession
{
    private readonly List<(string VdiRef, string Uuid, Guid Artifact)> _createdVdis = [];
    private string? _vmRef;
    private Guid? _vmArtifact;
    private (string Ref, string Uuid, string OriginalName)? _replaced;

    // Survives the delete of the replaced VM: the result names it.
    private string? _replacedUuid;
    private bool _completed;

    public async Task<RestoreValidation> ValidateAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var srs = await xapi.GetAllRecordsAsync("SR", cancellationToken).ConfigureAwait(false);
        var networks = await xapi.GetAllRecordsAsync("network", cancellationToken).ConfigureAwait(false);

        // Disks left out of the backup are created empty, but on LVM storage they still take their full size.
        var needed = new Dictionary<string, long>(StringComparer.Ordinal);
        var images = plan.Manifest.Disks.Select(d => (d.Key, d.Name, d.CapacityBytes))
            .Concat(plan.Manifest.ExcludedDisks.Select(d => (d.Key, d.Name, d.CapacityBytes)));
        if (plan.Resume && plan.Manifest.Memory is { } memory)
        {
            images = images.Append((memory.Key, "the memory image", memory.CapacityBytes));
        }

        foreach (var (key, name, capacity) in images)
        {
            if (!plan.DiskStorage.TryGetValue(key, out var srUuid))
            {
                errors.Add($"No target storage selected for disk {name}.");
                continue;
            }

            needed[srUuid] = needed.GetValueOrDefault(srUuid) + capacity;
        }

        foreach (var (srUuid, bytes) in needed)
        {
            var sr = srs.Values.FirstOrDefault(s => s.Str("uuid") == srUuid);
            if (sr is null)
            {
                errors.Add($"Target storage {srUuid} does not exist.");
                continue;
            }

            var free = sr.Long("physical_size") - sr.Long("physical_utilisation");
            if (free < bytes)
            {
                var message = $"Storage {sr.Str("name_label")} has {free / (1L << 30)} GiB free, the restored disks need up to {bytes / (1L << 30)} GiB.";
                if (sr.Str("type") is "lvm" or "lvmoiscsi" or "lvmohba")
                {
                    errors.Add(message);
                }
                else
                {
                    warnings.Add(message + " Thin provisioned storage may still fit.");
                }
            }
        }

        foreach (var network in plan.NetworkMap.Values.Distinct())
        {
            if (!networks.Values.Any(n => n.Str("uuid") == network))
            {
                errors.Add($"Target network {network} does not exist.");
            }
        }

        var original = plan.Mode == RestoreMode.OriginalLocation ? await FindOriginalAsync(cancellationToken).ConfigureAwait(false) : null;
        if (original is { } existing && !plan.ReplaceExisting)
        {
            errors.Add($"The original VM {existing.Name} ({existing.Uuid}) still exists. Choose 'Replace existing VM' or restore to another location.");
        }

        var vms = await xapi.GetAllRecordsAsync("VM", cancellationToken).ConfigureAwait(false);
        var sameName = vms.Values.Where(v => !v.Bool("is_a_snapshot") && !v.Bool("is_a_template") && v.Str("name_label") == plan.VmName
                                             && v.Str("uuid") != original?.Uuid).ToList();
        var running = sameName.FirstOrDefault(v => v.Str("power_state") != "Halted");
        if (plan.Mode == RestoreMode.OriginalLocation && running is not null)
        {
            // Most likely the VM in use now, which replaced the backed-up one outside of this restore's knowledge (by hand, or
            // at a restore before replacements were recorded). Putting the old VM back beside it would run both, e.g. two
            // instances of a domain controller.
            errors.Add($"A VM named '{plan.VmName}' ({running.Str("uuid")}, {running.Str("power_state")}) is not the backed-up VM ({plan.Manifest.Source.VmId}) " +
                       "and is not halted. Restore to another location, or shut it down or rename it first.");
        }
        else if (sameName.Count > 0)
        {
            warnings.Add($"A VM named '{plan.VmName}' already exists.");
        }

        if (plan.Manifest.Vm.HasVtpm)
        {
            warnings.Add("The VM had a virtual TPM. TPM contents are not part of the backup; BitLocker or measured boot may need recovery keys.");
        }

        if (plan.Mode == RestoreMode.AlternateLocation && !plan.RegenerateMacs
            && vms.Values.Any(v => OriginalIds.Contains(v.Str("uuid"))))
        {
            warnings.Add("The original VM exists in the target pool; keeping its MAC addresses creates duplicate MACs on the network.");
        }

        if (!string.IsNullOrEmpty(plan.TargetHostId)
            && await xapi.TryGetByUuidAsync("host", plan.TargetHostId, cancellationToken).ConfigureAwait(false) is null)
        {
            errors.Add($"Target host {plan.TargetHostId} does not exist.");
        }

        if (plan.Resume && plan.Manifest.Memory is null)
        {
            errors.Add($"The restore point of {plan.Manifest.Vm.Name} from {plan.Manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC has no memory image: it can only be booted from its disks.");
        }

        return new RestoreValidation(errors, warnings);
    }

    public async Task<IDiskWriter> CreateDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var (srRef, vdiRef, vdiUuid) = await CreateVdiAsync(target, cancellationToken).ConfigureAwait(false);
        var importHost = await ResolveImportHostAsync(srRef, cancellationToken).ConfigureAwait(false);
        return new XenImportWriter(
            owner, xapi, log, target.Name, importHost, vdiRef, vdiUuid, target.CapacityBytes, owner.Provider.Options.HttpImportFormat, owner.Provider.Options.ImportTimeout);
    }

    public async Task<string> CreateEmptyDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken) =>
        (await CreateVdiAsync(target, cancellationToken).ConfigureAwait(false)).VdiUuid;

    private async Task<(string SrRef, string VdiRef, string VdiUuid)> CreateVdiAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var srRef = await xapi.TryGetByUuidAsync("SR", target.StorageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Storage {target.StorageId} does not exist.");
        var record = new JsonObject
        {
            ["name_label"] = target.Name,
            ["name_description"] = $"Restored by backdafuckup from {plan.Manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC",
            ["SR"] = srRef,
            ["virtual_size"] = target.CapacityBytes.ToString(CultureInfo.InvariantCulture),
            ["type"] = target.MemoryImage ? "suspend" : "user",
            ["sharable"] = false,
            ["read_only"] = false,
            ["other_config"] = XenHypervisorSession.Map((XenTags.Session, plan.SessionId.ToString())),
            ["xenstore_data"] = new JsonObject(),
            ["sm_config"] = new JsonObject(),
            ["tags"] = new JsonArray(),
        };
        var vdiRef = await xapi.CallStringAsync("VDI.create", cancellationToken, record).ConfigureAwait(false);
        var vdi = await xapi.GetRecordAsync("VDI", vdiRef, cancellationToken).ConfigureAwait(false);
        var artifact = await ledger.RecordAsync(XenArtifactKinds.RestoredVdi, vdi.Str("uuid"), vdiRef, null, cancellationToken).ConfigureAwait(false);
        _createdVdis.Add((vdiRef, vdi.Str("uuid"), artifact));

        var srName = (await xapi.GetRecordAsync("SR", srRef, cancellationToken).ConfigureAwait(false)).Str("name_label");
        log.Info($"Created disk {target.Name} ({XenFormat.Bytes(target.CapacityBytes)}) on storage '{srName}' ({target.StorageId}).");
        return (srRef, vdiRef, vdi.Str("uuid"));
    }

    public async Task<RestoredVm> CreateVmAsync(IReadOnlyDictionary<string, string> sourceToNewDiskId, CancellationToken cancellationToken)
    {
        var keepIdentity = plan.Mode == RestoreMode.OriginalLocation;
        if (plan.Mode == RestoreMode.OriginalLocation && plan.ReplaceExisting && await FindOriginalAsync(cancellationToken).ConfigureAwait(false) is { } original)
        {
            if (original.Uuid != plan.Manifest.Source.VmId)
            {
                log.Info($"The original VM is {original.Name} ({original.Uuid}): it replaced the backed-up VM ({plan.Manifest.Source.VmId}) at an earlier restore to the original location.");
            }

            await ReplaceOriginalAsync(original.Ref, original.Uuid, cancellationToken).ConfigureAwait(false);
            keepIdentity = false; // the old VM still owns the UUID
        }

        var taskRef = await xapi.CreateTaskAsync("backdafuckup restore: import VM metadata", cancellationToken).ConfigureAwait(false);
        try
        {
            var query = new List<string>
            {
                $"session_id={Uri.EscapeDataString(xapi.SessionRef)}",
                $"task_id={Uri.EscapeDataString(taskRef)}",
                $"restore={(keepIdentity ? "true" : "false")}",
                $"force={(owner.Provider.Options.ForceMetadataImport ? "true" : "false")}",
            };
            if (plan.Resume)
            {
                // XAPI checks that the pool's CPUs can run the suspended VM (its last boot CPU flags).
                query.Add("check_cpu=true");
            }

            query.AddRange(sourceToNewDiskId.Select(kv => $"vdi:{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var content = new ByteArrayContent(plan.Metadata.Content);
            using var response = await xapi.Transport.SendHandlerAsync(HttpMethod.Put, "import_metadata?" + string.Join('&', query), content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"import_metadata failed: HTTP {(int)response.StatusCode}", null, response.StatusCode);
            }

            var result = await xapi.WaitForTaskAsync(taskRef, owner.Provider.Options.TaskTimeout, null, cancellationToken).ConfigureAwait(false);
            var task = await xapi.GetRecordAsync("task", taskRef, cancellationToken).ConfigureAwait(false);
            var vmRefs = XapiSession.DecodeTaskResultRefs(task.Str("result"));
            _vmRef = vmRefs.Count > 0 ? vmRefs[0] : result;
            if (!_vmRef.StartsWith("OpaqueRef:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("import_metadata did not return the new VM.");
            }
        }
        finally
        {
            await xapi.TryDestroyTaskAsync(taskRef).ConfigureAwait(false);
        }

        var vm = await xapi.GetRecordAsync("VM", _vmRef, cancellationToken).ConfigureAwait(false);
        _vmArtifact = await ledger.RecordAsync(XenArtifactKinds.RestoredVm, vm.Str("uuid"), _vmRef, null, cancellationToken).ConfigureAwait(false);
        if (plan.Resume)
        {
            await CheckSuspendedAsync(vm, sourceToNewDiskId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log.Info($"Created VM {vm.Str("name_label")} from the backed-up configuration.");
        }

        return new RestoredVm(vm.Str("uuid"), vm.Str("name_label"), vm.Str("generation_id"));
    }

    /// <summary>
    /// The snapshot with memory's metadata gives a suspended VM whose suspend image is the restored RAM image. It is marked
    /// as a template like the snapshot it came from; a VM that is to resume must not be one.
    /// </summary>
    private async Task CheckSuspendedAsync(JsonObject vm, IReadOnlyDictionary<string, string> sourceToNewDiskId, CancellationToken cancellationToken)
    {
        var expected = plan.Manifest.Memory is { } memory && sourceToNewDiskId.TryGetValue(memory.SourceDiskId, out var id) ? id : null;
        var suspendRef = vm.Str("suspend_VDI");
        var suspendUuid = XapiRecord.IsNullRef(suspendRef) ? null
            : (await xapi.GetRecordAsync("VDI", suspendRef, cancellationToken).ConfigureAwait(false)).Str("uuid");
        if (vm.Str("power_state") != "Suspended" || suspendUuid is null || suspendUuid != expected)
        {
            throw new InvalidOperationException(
                $"XAPI created the VM {vm.Str("name_label")} ({vm.Str("uuid")}) {vm.Str("power_state")} with suspend image {suspendUuid ?? "none"} " +
                $"(expected Suspended with the restored memory image {expected ?? "?"}), so it cannot resume. Restore it without memory: it then boots from its disks.");
        }

        if (vm.Bool("is_a_template"))
        {
            await xapi.CallAsync("VM.set_is_a_template", cancellationToken, _vmRef!, false).ConfigureAwait(false);
        }

        var generation = vm.Str("generation_id");
        var generationText = string.IsNullOrEmpty(generation) || generation == "0:0" ? "no VM generation id"
            : generation == plan.Manifest.Vm.GenerationId ? $"the VM generation id of the backup ({generation})" : $"a new VM generation id ({generation})";
        log.Info($"Created VM {vm.Str("name_label")} suspended, with its memory as at the backup (memory image {suspendUuid}) and {generationText}.");
    }

    public async Task<RestoredVm> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_vmRef is null)
        {
            throw new InvalidOperationException("CreateVmAsync must be called first.");
        }

        await xapi.CallAsync("VM.set_name_label", cancellationToken, _vmRef, plan.VmName).ConfigureAwait(false);
        if (plan.Resume && plan.Mode == RestoreMode.AlternateLocation)
        {
            await RemoveNicsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReconfigureNicsAsync(cancellationToken).ConfigureAwait(false);
        }

        string? hostRef = null;
        if (!string.IsNullOrEmpty(plan.TargetHostId))
        {
            hostRef = await xapi.TryGetByUuidAsync("host", plan.TargetHostId, cancellationToken).ConfigureAwait(false);
            if (hostRef is not null)
            {
                await xapi.CallAsync("VM.set_affinity", cancellationToken, _vmRef, hostRef).ConfigureAwait(false);
            }
        }

        if (_replaced is { } replaced && plan.DeleteReplaced)
        {
            await DeleteVmWithDisksAsync(replaced.Ref, cancellationToken).ConfigureAwait(false);
            log.Info($"Deleted the replaced VM {replaced.OriginalName}.");
            _replaced = null;
        }

        // From here on the restore is final.
        _completed = true;
        foreach (var vdi in _createdVdis)
        {
            await ledger.UpdateAsync(vdi.Artifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        if (_vmArtifact is { } vmArtifact)
        {
            await ledger.UpdateAsync(vmArtifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        foreach (var vdi in _createdVdis)
        {
            try
            {
                await xapi.CallAsync("VDI.remove_from_other_config", cancellationToken, vdi.VdiRef, XenTags.Session).ConfigureAwait(false);
            }
            catch (XapiException)
            {
            }
        }

        if (plan.Resume)
        {
            var at = plan.Manifest.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            if (plan.PowerOn)
            {
                log.Info($"Resuming the restored VM: it continues where it was at the snapshot with memory of the restore point from {at}.");
                if (hostRef is not null)
                {
                    await xapi.RunTaskAsync("VM.resume_on", null, owner.Provider.Options.TaskTimeout, cancellationToken, _vmRef, hostRef, false, false).ConfigureAwait(false);
                }
                else
                {
                    await xapi.RunTaskAsync("VM.resume", null, owner.Provider.Options.TaskTimeout, cancellationToken, _vmRef, false, false).ConfigureAwait(false);
                }
            }
            else
            {
                log.Info($"The restored VM stays suspended. Resumed (e.g. in Xen Orchestra), it continues where it was at the snapshot with memory of the restore point from {at}.");
            }
        }
        else if (plan.PowerOn)
        {
            log.Info("Starting the restored VM.");
            if (hostRef is not null)
            {
                await xapi.RunTaskAsync("VM.start_on", null, owner.Provider.Options.TaskTimeout, cancellationToken, _vmRef, hostRef, false, false).ConfigureAwait(false);
            }
            else
            {
                await xapi.RunTaskAsync("VM.start", null, owner.Provider.Options.TaskTimeout, cancellationToken, _vmRef, false, false).ConfigureAwait(false);
            }
        }

        var vm = await xapi.GetRecordAsync("VM", _vmRef, cancellationToken).ConfigureAwait(false);
        return new RestoredVm(vm.Str("uuid"), vm.Str("name_label"), ReplacedVmId: _replacedUuid);
    }

    private IReadOnlyList<string> OriginalIds => plan.OriginalVmIds.Count > 0 ? plan.OriginalVmIds : [plan.Manifest.Source.VmId];

    /// <summary>The original VM: the last of <see cref="VmRestorePlan.OriginalVmIds"/> that still exists.</summary>
    private async Task<(string Ref, string Uuid, string Name)?> FindOriginalAsync(CancellationToken cancellationToken)
    {
        foreach (var uuid in OriginalIds.Reverse())
        {
            if (await xapi.TryGetByUuidAsync("VM", uuid, cancellationToken).ConfigureAwait(false) is { } vmRef)
            {
                var vm = await xapi.GetRecordAsync("VM", vmRef, cancellationToken).ConfigureAwait(false);
                return (vmRef, uuid, vm.Str("name_label"));
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                if (_vmRef is not null)
                {
                    if ((await xapi.GetRecordAsync("VM", _vmRef, timeout.Token).ConfigureAwait(false)).Str("power_state") != "Halted")
                    {
                        await xapi.CallAsync("VM.hard_shutdown", timeout.Token, _vmRef).ConfigureAwait(false);
                    }

                    await xapi.CallAsync("VM.destroy", timeout.Token, _vmRef).ConfigureAwait(false);
                }

                if (_vmArtifact is { } vmArtifact)
                {
                    await ledger.UpdateAsync(vmArtifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
                }

                foreach (var vdi in _createdVdis)
                {
                    await XenHypervisorSession.DestroyVdiByUuidAsync(xapi, vdi.Uuid, log, timeout.Token).ConfigureAwait(false);
                    await ledger.UpdateAsync(vdi.Artifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
                }

                if (_replaced is { } replaced)
                {
                    await xapi.CallAsync("VM.set_name_label", timeout.Token, replaced.Ref, replaced.OriginalName).ConfigureAwait(false);
                    log.Info($"Restore failed; the original VM {replaced.OriginalName} keeps its name again (it was shut down).");
                }

                if (_createdVdis.Count > 0 || _vmRef is not null)
                {
                    log.Info("Removed the partially restored VM and disks.");
                }
            }
            catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException or OperationCanceledException)
            {
                log.Warning($"Cleanup after the failed restore was incomplete ({ex.Message}); leftovers are removed automatically later.");
                foreach (var vdi in _createdVdis)
                {
                    await ledger.UpdateAsync(vdi.Artifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
                }

                if (_vmArtifact is { } vmArtifact)
                {
                    await ledger.UpdateAsync(vmArtifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        await xapi.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReplaceOriginalAsync(string originalRef, string originalUuid, CancellationToken cancellationToken)
    {
        var original = await xapi.GetRecordAsync("VM", originalRef, cancellationToken).ConfigureAwait(false);
        var name = original.Str("name_label");
        if (original.Str("power_state") != "Halted")
        {
            log.Info($"Shutting down the original VM {name}.");
            try
            {
                await xapi.RunTaskAsync("VM.clean_shutdown", null, TimeSpan.FromMinutes(5), cancellationToken, originalRef).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is XapiException or TimeoutException)
            {
                log.Warning($"Clean shutdown failed ({ex.Message}); forcing power off.");
                await xapi.RunTaskAsync("VM.hard_shutdown", null, TimeSpan.FromMinutes(5), cancellationToken, originalRef).ConfigureAwait(false);
            }
        }

        var replacedName = $"{name} (replaced {DateTime.Now:yyyy-MM-dd HH:mm})";
        await xapi.CallAsync("VM.set_name_label", cancellationToken, originalRef, replacedName).ConfigureAwait(false);
        _replaced = (originalRef, originalUuid, name);
        _replacedUuid = originalUuid;
        log.Info(plan.RegenerateMacs
            ? $"The original VM was renamed to '{replacedName}'."
            : $"The original VM was renamed to '{replacedName}'. The restored VM gets its MAC addresses: do not start both at the same time.");
    }

    private async Task ReconfigureNicsAsync(CancellationToken cancellationToken)
    {
        var vm = await xapi.GetRecordAsync("VM", _vmRef!, cancellationToken).ConfigureAwait(false);
        var regenerate = plan.RegenerateMacs;

        // Without regenerating, every NIC keeps the MAC of the backup. XAPI gives an imported NIC a new MAC when its MAC was
        // generated and the import is not a restore (beside the replaced original, which still owns the VM's UUID); Windows
        // then sees a new network adapter without the old one's settings (static address, DNS servers) and DHCP gives another address.
        var backedUp = plan.Manifest.Vm.Nics.Where(n => !string.IsNullOrEmpty(n.Mac)).GroupBy(n => n.Device, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.Ordinal);
        foreach (var vifRef in vm.Set("VIFs"))
        {
            var vif = await xapi.GetRecordAsync("VIF", vifRef, cancellationToken).ConfigureAwait(false);
            var device = vif.Str("device");
            var networkRef = vif.Str("network");
            if (plan.NetworkMap.TryGetValue(device, out var networkUuid))
            {
                networkRef = await xapi.TryGetByUuidAsync("network", networkUuid, cancellationToken).ConfigureAwait(false) ?? networkRef;
            }

            var mac = vif.Str("MAC");
            var wanted = regenerate ? string.Empty : backedUp.TryGetValue(device, out var original) ? original : mac;
            if (networkRef == vif.Str("network") && !regenerate && string.Equals(mac, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!regenerate && !string.Equals(mac, wanted, StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"NIC {device}: MAC {wanted} as in the backup (the import had given it {mac}).");
            }

            var record = new JsonObject
            {
                ["device"] = device,
                ["network"] = networkRef,
                ["VM"] = _vmRef,
                ["MAC"] = wanted,
                ["MTU"] = vif.Long("MTU").ToString(CultureInfo.InvariantCulture),
                ["other_config"] = new JsonObject(),
                ["qos_algorithm_type"] = string.Empty,
                ["qos_algorithm_params"] = new JsonObject(),
                ["locking_mode"] = "network_default",
                ["ipv4_allowed"] = new JsonArray(),
                ["ipv6_allowed"] = new JsonArray(),
            };
            try
            {
                await xapi.CallAsync("VIF.destroy", cancellationToken, vifRef).ConfigureAwait(false);
                await xapi.CallAsync("VIF.create", cancellationToken, record).ConfigureAwait(false);
            }
            catch (XapiException ex) when (plan.Resume)
            {
                throw new InvalidOperationException(
                    $"NIC {device} of the suspended VM {plan.VmName} could not be set to network {networkRef} and MAC {(wanted.Length == 0 ? "(new)" : wanted)} ({ex.Code}). " +
                    "Restore it without memory (cold start), or keep its networks and MAC addresses.", ex);
            }
        }
    }

    /// <summary>
    /// A copy that resumes has the original's addresses and connections in its memory: it gets no network connections, so it
    /// never appears beside the original on the network.
    /// </summary>
    private async Task RemoveNicsAsync(CancellationToken cancellationToken)
    {
        var vm = await xapi.GetRecordAsync("VM", _vmRef!, cancellationToken).ConfigureAwait(false);
        foreach (var vifRef in vm.Set("VIFs"))
        {
            var vif = await xapi.GetRecordAsync("VIF", vifRef, cancellationToken).ConfigureAwait(false);
            try
            {
                await xapi.CallAsync("VIF.destroy", cancellationToken, vifRef).ConfigureAwait(false);
            }
            catch (XapiException ex)
            {
                throw new InvalidOperationException(
                    $"NIC {vif.Str("device")} (MAC {vif.Str("MAC")}) of the suspended copy {plan.VmName} could not be removed ({ex.Code}); a copy resumes only " +
                    "without network connections. Restore it to the original location, or without memory (cold start).", ex);
            }

            log.Info($"NIC {vif.Str("device")} (MAC {vif.Str("MAC")}) removed: the copy resumes without network connections, since its memory holds the original's addresses. " +
                     "Add a NIC (e.g. on an isolated network) once it may be connected.");
        }
    }

    private async Task DeleteVmWithDisksAsync(string vmRef, CancellationToken cancellationToken)
    {
        var vm = await xapi.GetRecordAsync("VM", vmRef, cancellationToken).ConfigureAwait(false);
        var vdis = new List<string>();
        foreach (var vbdRef in vm.Set("VBDs"))
        {
            var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
            if (vbd.Str("type") == "Disk" && !XapiRecord.IsNullRef(vbd.Str("VDI")))
            {
                vdis.Add(vbd.Str("VDI"));
            }
        }

        await xapi.CallAsync("VM.destroy", cancellationToken, vmRef).ConfigureAwait(false);
        foreach (var vdi in vdis)
        {
            await xapi.CallAsync("VDI.destroy", cancellationToken, vdi).ConfigureAwait(false);
        }
    }

    /// <summary>Shared SRs are imported through the master; local SRs through the host that has them attached.</summary>
    private async Task<string> ResolveImportHostAsync(string srRef, CancellationToken cancellationToken) =>
        (await XenSrHosts.ResolveAsync(xapi, srRef, cancellationToken).ConfigureAwait(false)).Address;
}

/// <summary>
/// Streams a restore-point disk into a new VDI with PUT /import_raw_vdi: as a dynamic VHD with only the blocks that
/// contain data (default), or raw with every byte of the disk (<see cref="XenOptions.HttpImportFormat"/>).
/// </summary>
internal sealed class XenImportWriter(
    XenHypervisorSession owner,
    XapiSession xapi,
    IOperationLog log,
    string diskName,
    string importHost,
    string vdiRef,
    string vdiUuid,
    long capacity,
    XenHttpImportFormat format,
    TimeSpan timeout) : IDiskWriter
{
    private Func<long>? _sent;

    public string DiskId => vdiUuid;

    public long Capacity => capacity;

    public DiskWriterCapabilities Capabilities =>
        format == XenHttpImportFormat.Vhd ? DiskWriterCapabilities.Sequential | DiskWriterCapabilities.SkipsZeroRegions : DiskWriterCapabilities.Sequential;

    public long TransferredBytes => _sent?.Invoke() ?? 0;

    public async Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken)
    {
        if (image.Capacity != capacity)
        {
            throw new ArgumentException($"Image size {image.Capacity} does not match the disk size {capacity}.", nameof(image));
        }

        var vhd = format == XenHttpImportFormat.Vhd ? VhdImageWriter.Create(capacity, image.DataExtents) : null;
        log.Info(vhd is null
            ? $"Disk {diskName}: import_raw_vdi to {importHost} as raw, {XenFormat.Bytes(capacity)} to transfer."
            : $"Disk {diskName}: import_raw_vdi to {importHost} as VHD, {vhd.AllocatedBlocks} of {vhd.BlockCount} blocks (2 MiB) contain data, " +
              $"{XenFormat.Bytes(vhd.Length)} to transfer instead of {XenFormat.Bytes(capacity)}.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var taskRef = await xapi.CreateTaskAsync("backdafuckup restore: import disk", timeoutCts.Token).ConfigureAwait(false);
        try
        {
            var host = importHost;
            for (var redirects = 0; ; redirects++)
            {
                var transport = host == xapi.MasterAddress ? xapi.Transport : owner.CreateTransport(host);
                try
                {
                    var query = $"import_raw_vdi?session_id={Uri.EscapeDataString(xapi.SessionRef)}&task_id={Uri.EscapeDataString(taskRef)}" +
                        $"&vdi={Uri.EscapeDataString(vdiRef)}&format={(vhd is null ? "raw" : "vhd")}";
                    using HttpContent content = vhd is null ? new DiskImageContent(image, processed) : new VhdImageContent(vhd, image, processed);
                    _sent = content switch
                    {
                        VhdImageContent v => () => v.Writer.BytesWritten,
                        DiskImageContent d => () => d.BytesWritten,
                        _ => null,
                    };
                    using var response = await transport.SendHandlerAsync(HttpMethod.Put, query, content, timeoutCts.Token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect && response.Headers.Location is { } location && redirects < 3)
                    {
                        log.Info($"Disk {diskName}: import_raw_vdi redirected from {host} to {location.Host}.");
                        host = location.Host;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"import_raw_vdi of disk {diskName} to {host} ({(vhd is null ? "raw" : "vhd")}) failed: HTTP {(int)response.StatusCode}", null, response.StatusCode);
                    }

                    var sent = TransferredBytes;
                    _sent = () => sent;
                    break;
                }
                finally
                {
                    if (!ReferenceEquals(transport, xapi.Transport))
                    {
                        transport.Dispose();
                    }
                }
            }

            await xapi.WaitForTaskAsync(taskRef, owner.Provider.Options.TaskTimeout, null, timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await xapi.TryDestroyTaskAsync(taskRef).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>HTTP body with a disk image as dynamic VHD (<see cref="VhdImageWriter"/>).</summary>
internal sealed class VhdImageContent(VhdImageWriter writer, IDiskImageSource image, IProgress<long>? progress) : HttpContent
{
    public VhdImageWriter Writer => writer;

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = image.OpenSequential(prefetchChunks: 32, workers: 8);
        await writer.WriteAsync(stream, source, progress, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = writer.Length;
        return true;
    }
}

/// <summary>HTTP body that streams a disk image in 4 MiB writes.</summary>
internal sealed class DiskImageContent(IDiskImageSource image, IProgress<long>? progress) : HttpContent
{
    private const int WriteSize = 4 * 1024 * 1024;
    private long _written;

    public long BytesWritten => Interlocked.Read(ref _written);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await using var source = image.OpenSequential(prefetchChunks: 32, workers: 8);
        var buffer = ArrayPool<byte>.Shared.Rent(WriteSize);
        try
        {
            long total = 0;
            while (true)
            {
                var filled = 0;
                while (filled < WriteSize)
                {
                    var n = await source.ReadAsync(buffer.AsMemory(filled, WriteSize - filled), cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    filled += n;
                }

                if (filled == 0)
                {
                    break;
                }

                await stream.WriteAsync(buffer.AsMemory(0, filled), cancellationToken).ConfigureAwait(false);
                total += filled;
                Interlocked.Exchange(ref _written, total);
                progress?.Report(total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = image.Capacity;
        return true;
    }
}
