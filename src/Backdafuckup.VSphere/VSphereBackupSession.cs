using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>What the artifact ledger records for vSphere (crash cleanup).</summary>
public static class VSphereArtifactKinds
{
    /// <summary>A backup snapshot: native id = snapshot moref (once known), native ref = VM moref.</summary>
    public const string Snapshot = "vsphere.snapshot";

    /// <summary>A disk hot-added to a proxy VM: native ref = proxy VM moref, data = slot, device key and file.</summary>
    public const string HotAddDisk = "vsphere.hotadd";

    public const string ChangeTokenKind = "vsphere-cbt";

    public const string MetadataFormat = "vsphere-config-v1";

    /// <summary>A disk file a restore created (NativeId: its datastore path; NativeRef: the datacenter).</summary>
    public const string RestoredDisk = "vsphere.restored-disk";

    /// <summary>The safety snapshot of a quick rollback (NativeId: the snapshot; NativeRef: the VM).</summary>
    public const string RollbackSnapshot = "vsphere.rollback-snapshot";

    /// <summary>A VM a restore created (NativeId: its managed object id).</summary>
    public const string RestoredVm = "vsphere.restored-vm";

    /// <summary>
    /// An instant recovery's NFS datastore (NativeId: the datastore's managed object id once mounted; NativeRef: the host
    /// that mounts it; Data: name, server and export path).
    /// </summary>
    public const string NasDatastore = "vsphere.nas-datastore";

    /// <summary>An instant recovery's VM (NativeId: its managed object id once created; NativeRef: the datastore; Data: its name and the datastore's).</summary>
    public const string InstantRecoveryVm = "vsphere.ir-vm";
}

/// <summary>A disk of the snapshot as vCenter describes it.</summary>
internal sealed record VSphereSnapshotDisk(int DeviceKey, string Key, string Label, string FileName, string Uuid, string RootUuid, string? ChangeId, long CapacityBytes, string? DatastoreId);

/// <summary>Paths of files on a datastore.</summary>
public static class VSphereDatastorePaths
{
    /// <summary>"[ds1] vm1/vm1.vmdk" → "vm1/vm1.vmdk"; null for a name without the datastore in brackets.</summary>
    public static string? DescriptorPath(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var close = fileName.IndexOf("] ", StringComparison.Ordinal);
        return fileName.StartsWith('[') && close > 0 && close + 2 < fileName.Length ? fileName[(close + 2)..] : null;
    }
}

/// <summary>Where a hot-added disk sits (kept in the ledger for crash cleanup).</summary>
internal sealed record HotAddRecord(int DeviceKey, int ControllerKey, int Bus, int Unit, string FileName, long CapacityBytes);

/// <summary>
/// The backup of one vSphere VM: a snapshot (between guest freeze and thaw), CBT through QueryChangedDiskAreas, and its
/// disks read through a proxy VM by HotAdd. The snapshot is removed at commit, or when the session ends without one.
/// </summary>
internal sealed class VSphereBackupSession : IVmBackupSession, IArraySnapshotFacts
{
    private readonly VSphereHypervisorSession _owner;
    private readonly IArtifactLedger _ledger;
    private readonly IBackupTelemetry? _telemetry;
    private readonly IOperationLog _log;
    private readonly ManagedObjectReference _snapshot;
    private readonly Guid _snapshotArtifact;
    private readonly Dictionary<string, VSphereSnapshotDisk> _disks;
    private readonly IReadOnlyCollection<(string Id, string Name)> _datastores;
    private readonly string? _vmCluster;
    private readonly SemaphoreSlim _proxyLock = new(1, 1);
    private readonly List<HotAddReader> _readers = [];
    private ProxyVm? _proxy;
    private bool _finished;
    private bool _released;

    private VSphereBackupSession(
        VSphereHypervisorSession owner, VSphereVm vm, IArtifactLedger ledger, IOperationLog log, ManagedObjectReference snapshot, Guid snapshotArtifact,
        IReadOnlyList<VSphereSnapshotDisk> disks, IReadOnlyList<SnapshotDisk> excluded, IReadOnlyCollection<(string Id, string Name)> datastores, string? vmCluster,
        VmMetadataBlob metadata, SnapshotFacts facts, GuestFreezeResult? freeze, IReadOnlyList<VmNic> nics, string? firmware, bool secureBoot, string? generationId,
        IBackupTelemetry? telemetry)
    {
        _owner = owner;
        VmInfo = vm;
        _ledger = ledger;
        _log = log;
        _snapshot = snapshot;
        _snapshotArtifact = snapshotArtifact;
        _telemetry = telemetry;
        _disks = disks.ToDictionary(d => d.Key, StringComparer.Ordinal);
        _datastores = datastores;
        _vmCluster = vmCluster;
        Metadata = metadata;
        Snapshot = facts;
        GuestFreeze = freeze;
        ExcludedDisks = excluded;
        Vm = new VmDescriptor
        {
            VmId = vm.InstanceUuid,
            Name = vm.Name,
            Description = vm.Annotation,
            VCpus = vm.VCpus,
            MemoryBytes = vm.MemoryBytes,
            OsName = vm.GuestName,
            Nics = nics,
            Firmware = firmware,
            SecureBoot = secureBoot,
            GenerationId = generationId,
        };
        Disks = [.. disks.Select(d => new SnapshotDisk(
            d.Key, d.RootUuid, d.Uuid, d.Label, d.CapacityBytes, d.Key.EndsWith(":0", StringComparison.Ordinal) && d.Key.StartsWith("scsi0", StringComparison.Ordinal),
            d.DatastoreId ?? string.Empty, datastores.FirstOrDefault(s => s.Id == d.DatastoreId).Name ?? string.Empty,
            new Dictionary<string, string> { ["fileName"] = d.FileName, ["deviceKey"] = d.DeviceKey.ToString(CultureInfo.InvariantCulture) }))];
    }

    public VSphereVm VmInfo { get; }

    public VmDescriptor Vm { get; }

    public IReadOnlyList<SnapshotDisk> Disks { get; }

    public IReadOnlyList<SnapshotDisk> ExcludedDisks { get; }

    public VmMetadataBlob Metadata { get; }

    public string SnapshotId => _snapshot.Value;

    public GuestFreezeResult? GuestFreeze { get; }

    public SnapshotFacts? Snapshot { get; }

    public TimeSpan? ReleaseDuration { get; private set; }

    /// <summary>
    /// The snapshot may go before the disks are read (backup from an array snapshot) when every disk has a change id: the
    /// changed areas are asked while it exists, and the change ids stay valid after its removal.
    /// </summary>
    public bool CanReleaseEarly => _disks.Count > 0 && _disks.Values.All(d => !string.IsNullOrEmpty(d.ChangeId));

    /// <summary>
    /// Each disk's datastore, its VMFS version and the descriptor of the disk under the snapshot ("vm1/vm1.vmdk", or
    /// "vm1/vm1-000002.vmdk" when the VM had snapshots already): what is read from the array snapshot of the datastore's LUN.
    /// Disks on other datastores (NFS, vSAN, several LUNs) are left out, and the VM takes the normal path.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ArrayDiskFacts>> GetArrayDisksAsync(CancellationToken cancellationToken)
    {
        var datastores = _disks.Values.Select(d => d.DatastoreId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var backings = await _owner.GetStorageBackingAsync(datastores, cancellationToken).ConfigureAwait(false);
        var writers = await _owner.GetDatastoreWritersAsync(datastores, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, ArrayDiskFacts>(StringComparer.Ordinal);
        foreach (var info in _disks.Values)
        {
            if (info.DatastoreId is { } datastore && backings.FirstOrDefault(b => b.StorageId == datastore) is { Refusal: null } backing
                && backing.Layout is StorageLayout.Vmfs6 or StorageLayout.Vmfs5 && VSphereDatastorePaths.DescriptorPath(info.FileName) is { } path)
            {
                result[info.Key] = new ArrayDiskFacts(datastore, backing.Layout, [new ArrayChainMember(path, info.CapacityBytes, 0)], writers.GetValueOrDefault(datastore));
            }
        }

        return result;
    }

    /// <summary>Removes the snapshot now; the disks are read from an array snapshot. Throws (the snapshot still whole) when vCenter refuses.</summary>
    public async Task ReleaseSnapshotAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!CanReleaseEarly)
        {
            throw new InvalidOperationException($"The snapshot of {VmInfo.Name} cannot be released before its disks are read (a disk without change tracking).");
        }

        await CloseReadersAsync().ConfigureAwait(false);
        var watch = Stopwatch.StartNew();
        await RemoveSnapshotAsync(Vim, _snapshot, VmInfo.Name, log, cancellationToken).ConfigureAwait(false);
        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        _released = true;
        _finished = true;
        ReleaseDuration = watch.Elapsed;
        log.Info($"Snapshot of {VmInfo.Name} removed before its disks are read ({watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s); " +
                 "changed block tracking keeps its change ids for the next backup.");
    }

    private VimSession Vim => _owner.Vim;

    public static async Task<VSphereBackupSession> CreateAsync(VSphereHypervisorSession owner, VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        var vim = owner.Vim;
        var vm = await VSphereInventory.FindVmAsync(vim, request.VmId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"VM {request.VmId} no longer exists in vCenter {vim.Endpoint}.");
        var row = (await vim.RetrievePropertiesAsync([vm.Ref], ["config.hardware.device", "config.guestId", "config.version", "config.firmware", "config.hardware.numCoresPerSocket", "config.extraConfig", "parent", "resourcePool", "config.bootOptions.efiSecureBootEnabled"], cancellationToken)
            .ConfigureAwait(false)).First();
        var devices = row["config.hardware.device"];
        var (usable, excluded) = Classify(devices);
        foreach (var disk in excluded)
        {
            log.Warning($"{vm.Name}: {disk.Name} is not backed up ({disk.Locator["reason"]}); a restore creates it empty.");
        }

        if (usable.Count == 0)
        {
            throw new InvalidOperationException($"VM {vm.Name} has no disk that can be backed up (independent disks and RDMs are left out).");
        }

        if (!vm.ChangeTracking)
        {
            var enable = await vim.StartTaskAsync("ReconfigVM_Task", vm.Ref, cancellationToken, VimXml.E("spec", VimXml.E("changeTrackingEnabled", "true"))).ConfigureAwait(false);
            await vim.WaitForTaskAsync(enable, cancellationToken).ConfigureAwait(false);
            log.Info($"{vm.Name}: changed block tracking switched on; this backup reads all allocated blocks, the next ones only the changes.");
        }

        var datastores = await DatastoreNamesAsync(vim, usable.Select(d => d.DatastoreId).OfType<string>(), cancellationToken).ConfigureAwait(false);
        var vmCluster = vm.HostId is null ? null
            : (await vim.RetrievePropertiesAsync([new ManagedObjectReference("HostSystem", vm.HostId)], ["parent"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["parent"]?.AsRef().Value;

        var description = $"{VSphereInventory.SnapshotDescriptionPrefix}session={request.SessionId:D} job={request.JobId:D} restorepoint={request.RestorePointId:D}";
        var artifact = await ledger.RecordAsync(VSphereArtifactKinds.Snapshot, string.Empty, vm.Ref.Value, description, cancellationToken).ConfigureAwait(false);
        var (freeze, freezeResult) = await GuestFreezeOrchestration.FreezeAsync(request.Quiescer, log, cancellationToken).ConfigureAwait(false);
        var toolsQuiesce = request.Quiescer is null && request.PlatformQuiesce;
        GuestFreezeResult? toolsResult = null;
        ManagedObjectReference? snapshot = null;
        var watch = Stopwatch.StartNew();
        try
        {
            try
            {
                snapshot = await CreateSnapshotAsync(vim, vm, description, toolsQuiesce, cancellationToken).ConfigureAwait(false);
            }
            catch (VimTaskException ex) when (toolsQuiesce)
            {
                log.Warning($"{vm.Name}: VMware Tools could not quiesce the snapshot ({ex.FaultMessage}); it is taken without quiescing (crash-consistent).");
                toolsResult = GuestFreezeResult.NotConsistent($"VMware Tools could not quiesce the snapshot: {ex.FaultMessage}.");
                snapshot = await CreateSnapshotAsync(vim, vm, description, quiesce: false, cancellationToken).ConfigureAwait(false);
            }

            if (toolsQuiesce && toolsResult is null)
            {
                var windows = row.Str("config.guestId")?.StartsWith("windows", StringComparison.OrdinalIgnoreCase) == true
                    || vm.GuestName?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;
                toolsResult = windows
                    ? new GuestFreezeResult(true, "quiesced by VMware Tools (VSS in the guest)", TimeSpan.Zero, watch.Elapsed, [])
                    : new GuestFreezeResult(false, "VMware Tools froze the file systems (fsfreeze): file-system-consistent, but applications were not asked to flush their data.", TimeSpan.Zero, watch.Elapsed, []);
                var seconds = watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
                if (windows)
                {
                    log.Info($"{vm.Name}: snapshot quiesced by VMware Tools (VSS in the guest) in {seconds} s.");
                }
                else
                {
                    // Says why the VM ends with a warning: guest processing promised more than file-system consistency.
                    log.Warning($"{vm.Name}: snapshot quiesced by VMware Tools: the file systems were frozen (fsfreeze) for {seconds} s. The backup is file-system-consistent; " +
                                "applications were not asked to flush their data (use their own pre-freeze scripts in the VMware Tools for that).");
                }
            }
        }
        finally
        {
            watch.Stop();
            freezeResult = await GuestFreezeOrchestration.ThawAsync(freeze, freezeResult, snapshotTaken: snapshot is not null).ConfigureAwait(false);
            if (snapshot is null)
            {
                await ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await ledger.UpdateAsync(artifact, ArtifactState.Active, snapshot.Value, cancellationToken).ConfigureAwait(false);
        if (freezeResult is not null)
        {
            GuestFreezeOrchestration.Report(log, freezeResult, watch.Elapsed);
        }

        freezeResult ??= toolsResult;

        try
        {
            var snapshotDevices = (await vim.RetrievePropertiesAsync([snapshot], ["config.hardware.device"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["config.hardware.device"];
            var disks = SnapshotDisks(snapshotDevices, devices).Where(d => usable.Any(u => u.DeviceKey == d.DeviceKey)).ToList();
            log.Info($"Snapshot {VSphereInventory.SnapshotName} of {vm.Name} created in {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s: " +
                $"{disks.Count} disk(s) on {string.Join(", ", datastores.Select(d => d.Name).Distinct(StringComparer.Ordinal))}.");
            var metadata = new VmMetadataBlob(VSphereArtifactKinds.MetadataFormat, JsonSerializer.SerializeToUtf8Bytes(new VSphereConfig(
                vm.Name, vm.InstanceUuid, vm.BiosUuid, row.Str("config.guestId"), vm.GuestName, row.Str("config.version"), row.Str("config.firmware"), vm.VCpus,
                int.TryParse(row.Str("config.hardware.numCoresPerSocket"), out var cores) ? cores : null, vm.MemoryBytes / (1024 * 1024), vm.Annotation,
                devices?.ToString() ?? string.Empty, row["parent"]?.AsRef().Value, row["resourcePool"]?.AsRef().Value,
                row.Str("config.bootOptions.efiSecureBootEnabled") is { } secureBoot ? secureBoot == "true" : null)));
            var facts = new SnapshotFacts(watch.Elapsed, TimeSpan.Zero, disks.Count, [.. datastores.Select(d => d.Name).Distinct(StringComparer.Ordinal)], null) { DisksAtOnce = true };
            return new VSphereBackupSession(owner, vm, ledger, log, snapshot, artifact, disks, excluded, datastores, vmCluster, metadata, facts, freezeResult,
                VSphereDevices.Nics(devices), row.Str("config.firmware"), row.Str("config.bootOptions.efiSecureBootEnabled") == "true", VSphereDevices.GenerationId(row["config.extraConfig"]), request.Telemetry);
        }
        catch
        {
            await RemoveSnapshotAsync(vim, snapshot, vm.Name, log, CancellationToken.None).ConfigureAwait(false);
            await ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<ManagedObjectReference> CreateSnapshotAsync(VimSession vim, VSphereVm vm, string description, bool quiesce, CancellationToken cancellationToken)
    {
        var task = await vim.StartTaskAsync("CreateSnapshot_Task", vm.Ref, cancellationToken,
            VimXml.E("name", VSphereInventory.SnapshotName), VimXml.E("description", description), VimXml.E("memory", "false"), VimXml.E("quiesce", quiesce ? "true" : "false"))
            .ConfigureAwait(false);
        return (await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false))?.AsRef()
            ?? throw new InvalidDataException($"vCenter {vim.Endpoint}: the snapshot of {vm.Name} was created without a reference.");
    }

    public async Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken)
    {
        var info = _disks[disk.Key];
        if (string.IsNullOrEmpty(info.ChangeId))
        {
            return ChangedExtents.Full($"changed block tracking of {disk.Name} was not active when the snapshot was taken", FullReadReasons.NoChangeData);
        }

        if (since is null || since.Kind != VSphereArtifactKinds.ChangeTokenKind)
        {
            return ChangedExtents.FullAllocated("first backup with changed block tracking", await QueryAsync(info, "*", cancellationToken).ConfigureAwait(false), FullReadReasons.FirstBackup);
        }

        if (Epoch(since.Value) != Epoch(info.ChangeId))
        {
            return ChangedExtents.FullAllocated(
                "changed block tracking of the disk was reset since the last backup (another change-tracking epoch)", await QueryAsync(info, "*", cancellationToken).ConfigureAwait(false),
                FullReadReasons.CbtReenabled);
        }

        try
        {
            return new ChangedExtents(false, null, await QueryAsync(info, since.Value, cancellationToken).ConfigureAwait(false));
        }
        catch (VimFaultException ex)
        {
            return ChangedExtents.FullAllocated(
                $"vCenter refused the change id of the last backup ({ex.FaultMessage})", await QueryAsync(info, "*", cancellationToken).ConfigureAwait(false), FullReadReasons.CbtError);
        }
    }

    public async Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_released)
        {
            throw new InvalidOperationException($"The snapshot of {VmInfo.Name} was removed; its disks are read from the array snapshot.");
        }

        var info = _disks[disk.Key];
        var pool = _owner.Provider.Proxies ?? throw new InvalidOperationException("This server has no proxies to read vSphere disks.");
        var proxy = await ProxyAsync(pool, cancellationToken).ConfigureAwait(false);
        var slot = _owner.Provider.Slots.Reserve(proxy)
            ?? throw new InvalidOperationException($"Proxy {proxy.Proxy.Name} (VM {proxy.VmName}) has no free SCSI place left for {disk.Name} of {VmInfo.Name}.");
        Guid? artifact = null;
        int? deviceKey = null;
        IProxyDisk? proxyDisk = null;
        try
        {
            artifact = await _ledger.RecordAsync(VSphereArtifactKinds.HotAddDisk, $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}", proxy.Vm.Value,
                JsonSerializer.Serialize(new HotAddRecord(-1, slot.ControllerKey, slot.Bus, slot.Unit, info.FileName, info.CapacityBytes)), cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            deviceKey = await VSphereHotAdd.AttachAsync(Vim, proxy, slot, info.FileName, info.CapacityBytes, cancellationToken).ConfigureAwait(false);
            await _ledger.UpdateAsync(artifact.Value, ArtifactState.Active,
                $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}:{deviceKey.Value.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            proxyDisk = await pool.AttachAsync(proxy.Proxy.Id, info.Uuid, info.CapacityBytes, writable: false, cancellationToken).ConfigureAwait(false);
            var reader = await proxyDisk.OpenReaderAsync(Math.Clamp(options.Connections, 1, 16), Math.Max(1, options.MaxInFlightPerConnection), cancellationToken).ConfigureAwait(false);
            _telemetry?.HotAdd(_datastores.FirstOrDefault(d => d.Id == info.DatastoreId).Name ?? info.DatastoreId ?? string.Empty, proxy.Proxy.Name, watch.Elapsed);
            _log.Info($"{disk.Name} of {VmInfo.Name}: hot-added to proxy {proxy.Proxy.Name} (VM {proxy.VmName} on {proxy.HostName}, SCSI {slot.Bus}:{slot.Unit}) in " +
                $"{watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s; reading {proxyDisk.Description}.");
            var opened = new HotAddReader(this, reader, proxyDisk, proxy, slot, deviceKey.Value, info, artifact.Value);
            lock (_readers)
            {
                _readers.Add(opened);
            }

            return opened;
        }
        catch
        {
            if (proxyDisk is not null)
            {
                await proxyDisk.DisposeAsync().ConfigureAwait(false);
            }

            if (deviceKey is { } key)
            {
                await DetachQuietlyAsync(proxy, slot, key, info).ConfigureAwait(false);
            }

            _owner.Provider.Slots.Release(proxy.Vm, slot);
            if (artifact is { } id)
            {
                await _ledger.UpdateAsync(id, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    public ChangeToken? GetNewChangeToken(SnapshotDisk disk) =>
        _disks[disk.Key].ChangeId is { Length: > 0 } id ? new ChangeToken(VSphereArtifactKinds.ChangeTokenKind, id) : null;

    public async Task CommitAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        await CloseReadersAsync().ConfigureAwait(false);
        if (_released)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        await RemoveSnapshotAsync(Vim, _snapshot, VmInfo.Name, log, cancellationToken).ConfigureAwait(false);
        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        _finished = true;
        log.Info($"Snapshot of {VmInfo.Name} removed in {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
    }

    public async ValueTask DisposeAsync()
    {
        await CloseReadersAsync().ConfigureAwait(false);
        if (!_finished)
        {
            _finished = true;
            await RemoveSnapshotAsync(Vim, _snapshot, VmInfo.Name, _log, CancellationToken.None).ConfigureAwait(false);
            await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        }

        _proxyLock.Dispose();
    }

    /// <summary>Removes a backup snapshot (consolidating); a failure is a warning, cleanup retries it from the ledger.</summary>
    internal static async Task RemoveSnapshotAsync(VimSession vim, ManagedObjectReference snapshot, string vmName, IOperationLog log, CancellationToken cancellationToken)
    {
        try
        {
            var task = await vim.StartTaskAsync("RemoveSnapshot_Task", snapshot, cancellationToken, VimXml.E("removeChildren", "false"), VimXml.E("consolidate", "true")).ConfigureAwait(false);
            await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (VimFaultException ex) when (ex.FaultType == "ManagedObjectNotFound")
        {
            // Removed already.
        }
        catch (Exception ex) when (ex is VimFaultException or VimTaskException or HttpRequestException or TimeoutException)
        {
            log.Warning($"The backup snapshot of {vmName} could not be removed now ({ex.Message}); it is removed later.");
            throw;
        }
    }

    private async Task<ProxyVm> ProxyAsync(IProxyPool pool, CancellationToken cancellationToken)
    {
        await _proxyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_proxy is not null)
            {
                return _proxy;
            }

            var (usable, refused) = await VSphereHotAdd.ResolveAsync(Vim, pool, cancellationToken).ConfigureAwait(false);
            var (chosen, notSeeing) = _owner.Provider.Slots.Choose(usable, VmInfo.HostId, _vmCluster, _datastores);
            if (chosen is null)
            {
                var reasons = refused.Concat(notSeeing).ToList();
                throw new InvalidOperationException(reasons.Count == 0
                    ? $"No proxy is connected to read the disks of {VmInfo.Name} (Infrastructure → Proxies)."
                    : $"No proxy can read the disks of {VmInfo.Name}: {string.Join("; ", reasons)}.");
            }

            _proxy = chosen;
            return chosen;
        }
        finally
        {
            _proxyLock.Release();
        }
    }

    private Task<IReadOnlyList<DiskExtent>> QueryAsync(VSphereSnapshotDisk disk, string changeId, CancellationToken cancellationToken) =>
        QueryChangedAsync(Vim, VmInfo.Ref, VmInfo.Name, _snapshot, disk.DeviceKey, disk.Label, disk.CapacityBytes, changeId, cancellationToken);

    /// <summary>The areas of a disk changed since <paramref name="changeId"/> up to the snapshot ("*": the allocated areas), all pages.</summary>
    internal static async Task<IReadOnlyList<DiskExtent>> QueryChangedAsync(
        VimSession vim, ManagedObjectReference vm, string vmName, ManagedObjectReference snapshot, int deviceKey, string label, long capacityBytes, string changeId,
        CancellationToken cancellationToken)
    {
        var extents = new List<DiskExtent>();
        var offset = 0L;
        while (offset < capacityBytes)
        {
            var info = await vim.InvokeAsync("QueryChangedDiskAreas", vm, cancellationToken,
                    VimXml.Ref("snapshot", snapshot), VimXml.E("deviceKey", deviceKey), VimXml.E("startOffset", offset), VimXml.E("changeId", changeId)).ConfigureAwait(false)
                ?? throw new InvalidDataException($"vCenter {vim.Endpoint}: QueryChangedDiskAreas of {label} of {vmName} returned nothing.");
            foreach (var area in info.Children("changedArea"))
            {
                extents.Add(new DiskExtent(area.Long("start") ?? 0, area.Long("length") ?? 0));
            }

            var length = info.Long("length") ?? 0;
            if (length <= 0)
            {
                break;
            }

            offset = (info.Long("startOffset") ?? offset) + length;
        }

        return ExtentMath.Normalize(extents);
    }

    private async Task CloseReadersAsync()
    {
        List<HotAddReader> readers;
        lock (_readers)
        {
            readers = [.. _readers];
        }

        foreach (var reader in readers)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DetachQuietlyAsync(ProxyVm proxy, ScsiSlot slot, int deviceKey, VSphereSnapshotDisk disk)
    {
        try
        {
            await VSphereHotAdd.DetachAsync(Vim, proxy.Vm, deviceKey, slot, disk.CapacityBytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VimFaultException or VimTaskException or HttpRequestException or TimeoutException)
        {
            _log.Warning($"{disk.Label} of {VmInfo.Name} could not be detached from proxy VM {proxy.VmName} ({ex.Message}); it is detached later.");
        }
    }

    /// <summary>The changeId's epoch: the part before "/" (the sequence after it grows with every snapshot).</summary>
    private static string Epoch(string changeId) => changeId.Split('/')[0].Trim();

    /// <summary>Disks that can be backed up (flat, sparse and SE sparse VMDKs in a persistent mode) and those left out.</summary>
    private static (List<(int DeviceKey, string? DatastoreId)> Usable, List<SnapshotDisk> Excluded) Classify(VimObject? devices)
    {
        var usable = new List<(int, string?)>();
        var excluded = new List<SnapshotDisk>();
        var controllers = Controllers(devices);
        foreach (var disk in devices?.Items.Where(d => d.Type == "VirtualDisk") ?? [])
        {
            var backing = disk.Child("backing");
            var mode = backing?.Str("diskMode") ?? "persistent";
            var key = disk.Int("key") ?? 0;
            var reason = backing?.Type is "VirtualDiskRawDiskMappingVer1BackingInfo" or "VirtualDiskRawDiskVer2BackingInfo" or "VirtualDiskPartitionedRawDiskVer2BackingInfo"
                ? "a raw device mapping"
                : mode.StartsWith("independent", StringComparison.Ordinal) ? $"an independent disk ({mode})"
                : backing?.Str("sharing") == "sharingMultiWriter" ? "a multi-writer disk"
                : null;
            if (reason is null)
            {
                usable.Add((key, backing?.Ref("datastore")?.Value));
                continue;
            }

            var capacity = disk.Long("capacityInBytes") ?? (disk.Long("capacityInKB") ?? 0) * 1024;
            excluded.Add(new SnapshotDisk(DeviceSlot(disk, controllers), backing?.Str("uuid") ?? key.ToString(CultureInfo.InvariantCulture), string.Empty,
                disk.Child("deviceInfo")?.Str("label") ?? $"disk {key}", capacity, false, backing?.Ref("datastore")?.Value ?? string.Empty, string.Empty,
                new Dictionary<string, string> { ["reason"] = reason, ["fileName"] = backing?.Str("fileName") ?? string.Empty }));
        }

        return (usable, excluded);
    }

    /// <summary>The disks of the snapshot (backing at snapshot time: file, UUID, changeId, the chain's root UUID).</summary>
    private static List<VSphereSnapshotDisk> SnapshotDisks(VimObject? snapshotDevices, VimObject? liveDevices)
    {
        var controllers = Controllers(snapshotDevices ?? liveDevices);
        var disks = new List<VSphereSnapshotDisk>();
        foreach (var disk in (snapshotDevices ?? liveDevices)?.Items.Where(d => d.Type == "VirtualDisk") ?? [])
        {
            var backing = disk.Child("backing");
            var root = backing;
            while (root?.Child("parent") is { } parent)
            {
                root = parent;
            }

            var key = disk.Int("key") ?? 0;
            disks.Add(new VSphereSnapshotDisk(
                key, DeviceSlot(disk, controllers), disk.Child("deviceInfo")?.Str("label") ?? $"disk {key}", backing?.Str("fileName") ?? string.Empty, backing?.Str("uuid") ?? string.Empty,
                root?.Str("uuid") ?? backing?.Str("uuid") ?? string.Empty, backing?.Str("changeId"), disk.Long("capacityInBytes") ?? (disk.Long("capacityInKB") ?? 0) * 1024,
                backing?.Ref("datastore")?.Value));
        }

        return disks;
    }

    private static Dictionary<int, string> Controllers(VimObject? devices)
    {
        var result = new Dictionary<int, string>();
        foreach (var device in devices?.Items ?? [])
        {
            var prefix = device.Type switch
            {
                "ParaVirtualSCSIController" or "VirtualLsiLogicSASController" or "VirtualLsiLogicController" or "VirtualBusLogicController" => "scsi",
                "VirtualAHCIController" => "sata",
                "VirtualIDEController" => "ide",
                "VirtualNVMEController" => "nvme",
                _ => null,
            };
            if (prefix is not null && device.Int("key") is { } key)
            {
                result[key] = $"{prefix}{device.Int("busNumber") ?? 0}";
            }
        }

        return result;
    }

    private static string DeviceSlot(VimObject disk, Dictionary<int, string> controllers) =>
        $"{controllers.GetValueOrDefault(disk.Int("controllerKey") ?? -1, "disk")}:{disk.Int("unitNumber") ?? 0}";

    private static async Task<IReadOnlyCollection<(string Id, string Name)>> DatastoreNamesAsync(VimSession vim, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var refs = ids.Distinct(StringComparer.Ordinal).Select(id => new ManagedObjectReference("Datastore", id)).ToList();
        if (refs.Count == 0)
        {
            return [];
        }

        return [.. (await vim.RetrievePropertiesAsync(refs, ["summary.name"], cancellationToken).ConfigureAwait(false)).Select(d => (d.Obj.Value, d.Str("summary.name") ?? d.Obj.Value))];
    }

    /// <summary>A hot-added disk's reader: closing it releases the disk on the proxy, detaches it and frees the slot.</summary>
    private sealed class HotAddReader(
        VSphereBackupSession session, IDiskSource inner, IProxyDisk proxyDisk, ProxyVm proxy, ScsiSlot slot, int deviceKey, VSphereSnapshotDisk disk, Guid artifact) : IDiskReader
    {
        private int _closed;

        public long Capacity => inner.Capacity;

        public string TransportName => inner.TransportName;

        public int MaxRequestBytes => ((IDiskReader)inner).MaxRequestBytes;

        public int MaxConcurrentRequests => ((IDiskReader)inner).MaxConcurrentRequests;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => ((IDiskReader)inner).ReadAsync(offset, destination, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
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

                await session.DetachQuietlyAsync(proxy, slot, deviceKey, disk).ConfigureAwait(false);
                session._owner.Provider.Slots.Release(proxy.Vm, slot);
                await session._ledger.UpdateAsync(artifact, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
                lock (session._readers)
                {
                    session._readers.Remove(this);
                }
            }
        }
    }
}

/// <summary>The VM configuration stored with a vSphere restore point (<see cref="VSphereArtifactKinds.MetadataFormat"/>).</summary>
/// <param name="DevicesXml">config.hardware.device as vCenter sent it, for the restore.</param>
/// <param name="FolderId">The VM folder the VM was in (restore puts it back there by default).</param>
/// <param name="ResourcePoolId">The VM's resource pool.</param>
internal sealed record VSphereConfig(
    string Name, string InstanceUuid, string? BiosUuid, string? GuestId, string? GuestFullName, string? HardwareVersion, string? Firmware, int NumCpu, int? CoresPerSocket,
    long MemoryMb, string? Annotation, string DevicesXml, string? FolderId = null, string? ResourcePoolId = null, bool? EfiSecureBoot = null);
