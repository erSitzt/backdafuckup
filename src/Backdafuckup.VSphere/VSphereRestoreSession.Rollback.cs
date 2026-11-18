using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>
/// Quick rollback: the original VM gets back the content of the restore point by writing only the blocks that changed
/// since it. The VM is shut down and a safety snapshot taken; changed block tracking lists, per disk, what changed
/// between the restore point's changeId and the safety snapshot; the VM's current disk (the snapshot's delta) is
/// attached writable to a proxy and exactly those blocks are written from the restore point. Then the safety snapshot is
/// removed (its delta, now holding the rollback, is consolidated) and change tracking is reset, so the next backup reads
/// the disks fully. If anything fails, the VM is reverted to the safety snapshot.
/// </summary>
internal sealed partial class VSphereRestoreSession
{
    private RollbackState? _rollback;

    private sealed class RollbackState(ManagedObjectReference vm, string name, string instanceUuid, IReadOnlyList<string> datastores)
    {
        public ManagedObjectReference Vm { get; } = vm;

        public string Name { get; } = name;

        public string InstanceUuid { get; } = instanceUuid;

        public IReadOnlyList<string> Datastores { get; } = datastores;

        public bool WasPoweredOn { get; set; }

        public ManagedObjectReference? Snapshot { get; set; }

        public Guid? SnapshotArtifact { get; set; }

        /// <summary>The VM's disks under the safety snapshot by the root UUID of their chain: device key, current file, UUID, capacity.</summary>
        public Dictionary<string, (int DeviceKey, string FileName, string? Uuid, long Capacity, string Label)> Disks { get; } = new(StringComparer.Ordinal);

        public long Written;

        public long Changed;
    }

    private async Task<RestoreValidation> ValidateRollbackAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (_plan.Mode != RestoreMode.OriginalLocation)
        {
            errors.Add("A quick rollback writes back into the original VM: choose the original location.");
            return new RestoreValidation(errors, warnings);
        }

        if (_plan.Manifest.DomainController == true)
        {
            // A new VM gets a new VM-Generation-ID, from which AD learns that it was restored (new invocation ID); writing
            // the old blocks into the same VM keeps the ID, and its partners would take its old state as current (USN rollback).
            errors.Add($"{_plan.Manifest.Vm.Name} was a domain controller at the restore point. A quick rollback keeps its VM-Generation-ID, so Active Directory " +
                       "would not notice that it went back in time (USN rollback). Restore it fully instead: the restored VM gets a new VM-Generation-ID.");
            return new RestoreValidation(errors, warnings);
        }

        if (await FindOriginalAsync(cancellationToken).ConfigureAwait(false) is not { } original)
        {
            errors.Add($"The original VM ({_plan.Manifest.Source.VmId}) no longer exists in vCenter {Vim.Endpoint}: restore it fully instead of a quick rollback.");
            return new RestoreValidation(errors, warnings);
        }

        var row = (await Vim.RetrievePropertiesAsync([original.Ref], ["config.hardware.device", "config.changeTrackingEnabled", "snapshot"], cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();
        if (row?.Str("config.changeTrackingEnabled") != "true")
        {
            errors.Add($"Changed block tracking is off for {original.Name}, so the changes since the restore point are unknown: restore it fully instead.");
        }

        var disks = CurrentDisks(row?["config.hardware.device"]);
        foreach (var disk in _plan.Manifest.Disks)
        {
            if (!disks.TryGetValue(disk.SourceDiskId, out var current))
            {
                errors.Add($"{disk.Name} of the restore point is no longer a disk of {original.Name}: a quick rollback is not possible, restore the VM fully.");
            }
            else if (current.Capacity != disk.CapacityBytes)
            {
                errors.Add($"{disk.Name} of {original.Name} is {Bytes(current.Capacity)} now and was {Bytes(disk.CapacityBytes)} at the restore point: a quick rollback is not possible, restore the VM fully.");
            }
            else if (disk.ChangeToken is not { Kind: VSphereArtifactKinds.ChangeTokenKind, Value.Length: > 0 })
            {
                errors.Add($"The restore point has no changeId for {disk.Name} (it was backed up without change tracking): restore the VM fully.");
            }
        }

        if (row?["snapshot"] is not null)
        {
            warnings.Add($"{original.Name} has snapshots of its own; the rollback writes into its current state, they stay as they are.");
        }

        _rollback = new RollbackState(original.Ref, original.Name, original.InstanceUuid,
            [.. disks.Values.Select(d => d.Datastore).OfType<string>().Distinct(StringComparer.Ordinal)]);
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

        if (errors.Count == 0)
        {
            _log.Info($"Quick rollback of {original.Name}: only the blocks changed since the restore point are written back, under a safety snapshot.");
        }

        return new RestoreValidation(errors, warnings);
    }

    /// <summary>The disks of the VM by the root UUID of their chain (as the restore point names them).</summary>
    private static Dictionary<string, (int DeviceKey, string FileName, string? Uuid, long Capacity, string Label, string? Datastore)> CurrentDisks(VimObject? devices)
    {
        var result = new Dictionary<string, (int, string, string?, long, string, string?)>(StringComparer.Ordinal);
        foreach (var disk in devices?.Items.Where(d => d.Type == "VirtualDisk") ?? [])
        {
            var backing = disk.Child("backing");
            var root = backing;
            while (root?.Child("parent") is { } parent)
            {
                root = parent;
            }

            if (root?.Str("uuid") is { } rootUuid)
            {
                result[rootUuid] = (disk.Int("key") ?? 0, backing?.Str("fileName") ?? string.Empty, backing?.Str("uuid"),
                    disk.Long("capacityInBytes") ?? (disk.Long("capacityInKB") ?? 0) * 1024, disk.Child("deviceInfo")?.Str("label") ?? $"disk {disk.Int("key")}",
                    backing?.Ref("datastore")?.Value);
            }
        }

        return result;
    }

    /// <summary>Once: shut the VM down and take the safety snapshot; then the disks' current files are the snapshot's deltas.</summary>
    private async Task<RollbackState> PrepareRollbackAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = _rollback ?? throw new InvalidOperationException("ValidateAsync must be called first.");
            if (state.Snapshot is not null)
            {
                return state;
            }

            var power = (await Vim.RetrievePropertiesAsync([state.Vm], ["runtime.powerState"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("runtime.powerState");
            state.WasPoweredOn = power != "poweredOff";
            if (state.WasPoweredOn)
            {
                await ShutDownAsync(state.Vm, state.Name, cancellationToken).ConfigureAwait(false);
            }

            state.SnapshotArtifact = await _ledger.RecordAsync(VSphereArtifactKinds.RollbackSnapshot, string.Empty, state.Vm.Value, null, cancellationToken).ConfigureAwait(false);
            var task = await Vim.StartTaskAsync("CreateSnapshot_Task", state.Vm, cancellationToken,
                VimXml.E("name", "backdafuckup rollback safety snapshot"),
                // Not our backup snapshots' description prefix: a safety snapshot that is kept is the user's from then on.
                VimXml.E("description", $"Safety snapshot of a quick rollback to the restore point of {_plan.Manifest.CreatedUtc:yyyy-MM-dd HH:mm} UTC (backdafuckup session {_plan.SessionId})"),
                VimXml.E("memory", "false"), VimXml.E("quiesce", "false")).ConfigureAwait(false);
            state.Snapshot = (await Vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false))?.AsRef()
                ?? throw new InvalidDataException($"vCenter {Vim.Endpoint}: the safety snapshot of {state.Name} was created without a reference.");
            await _ledger.UpdateAsync(state.SnapshotArtifact.Value, ArtifactState.Active, state.Snapshot.Value, cancellationToken).ConfigureAwait(false);
            var devices = (await Vim.RetrievePropertiesAsync([state.Vm], ["config.hardware.device"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["config.hardware.device"];
            foreach (var (root, disk) in CurrentDisks(devices))
            {
                state.Disks[root] = (disk.DeviceKey, disk.FileName, disk.Uuid, disk.Capacity, disk.Label);
            }

            _log.Info($"Safety snapshot of {state.Name} taken: if the rollback fails, the VM goes back to it.");
            return state;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ShutDownAsync(ManagedObjectReference vm, string name, CancellationToken cancellationToken)
    {
        _log.Info($"Shutting down {name} for the rollback.");
        try
        {
            await Vim.InvokeAsync("ShutdownGuest", vm, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < 150 && (await Vim.RetrievePropertiesAsync([vm], ["runtime.powerState"], cancellationToken).ConfigureAwait(false))
                     .FirstOrDefault()?.Str("runtime.powerState") != "poweredOff"; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (VimFaultException ex)
        {
            _log.Warning($"A clean shutdown of {name} is not possible ({ex.FaultMessage}); powering it off.");
        }

        if ((await Vim.RetrievePropertiesAsync([vm], ["runtime.powerState"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("runtime.powerState") != "poweredOff")
        {
            await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOffVM_Task", vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A disk of the rollback: what changed since the restore point, and a writer for exactly that onto the VM's current disk.</summary>
    private async Task<IDiskWriter> RollbackDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var state = await PrepareRollbackAsync(cancellationToken).ConfigureAwait(false);
        var disk = state.Disks.TryGetValue(target.SourceDiskId, out var found)
            ? found
            : throw new InvalidOperationException($"{target.Name} is no longer a disk of {state.Name}.");
        var changeId = _plan.Manifest.Disks.First(d => d.Key == target.DiskKey).ChangeToken!.Value;
        IReadOnlyList<DiskExtent> changed;
        try
        {
            changed = await VSphereBackupSession.QueryChangedAsync(Vim, state.Vm, state.Name, state.Snapshot!, disk.DeviceKey, disk.Label, disk.Capacity, changeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VimFaultException ex)
        {
            throw new InvalidOperationException(
                $"vCenter cannot list what changed on {disk.Label} of {state.Name} since the restore point (changeId {changeId}): {ex.FaultMessage}. " +
                "Change tracking was probably reset since; restore the VM fully instead.", ex);
        }

        var bytes = changed.Sum(e => e.Length);
        Interlocked.Add(ref state.Changed, bytes);
        _log.Info($"{disk.Label} of {state.Name}: {Bytes(bytes)} changed since the restore point ({changed.Count} area(s)); writing them back.");

        var proxy = await ProxyAsync(cancellationToken).ConfigureAwait(false);
        var pool = _owner.Provider.Proxies!;
        var slot = _owner.Provider.Slots.Reserve(proxy)
            ?? throw new InvalidOperationException($"Proxy {proxy.Proxy.Name} (VM {proxy.VmName}) has no free SCSI place left for {disk.Label} of {state.Name}.");
        Guid? artifact = null;
        int? deviceKey = null;
        IProxyDisk? proxyDisk = null;
        try
        {
            artifact = await _ledger.RecordAsync(VSphereArtifactKinds.HotAddDisk, $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}", proxy.Vm.Value,
                JsonSerializer.Serialize(new HotAddRecord(-1, slot.ControllerKey, slot.Bus, slot.Unit, disk.FileName, disk.Capacity)), cancellationToken).ConfigureAwait(false);
            var (key, uuid) = await VSphereHotAdd.AttachDiskAsync(Vim, proxy, slot, disk.FileName, disk.Capacity, "persistent", cancellationToken).ConfigureAwait(false);
            deviceKey = key;
            await _ledger.UpdateAsync(artifact.Value, ArtifactState.Active, $"{proxy.Vm.Value}:{slot.ControllerKey}:{slot.Unit}:{key.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
                .ConfigureAwait(false);
            proxyDisk = await pool.AttachAsync(proxy.Proxy.Id, uuid ?? disk.Uuid ?? target.SourceDiskId, disk.Capacity, writable: true, cancellationToken).ConfigureAwait(false);
            var writer = await proxyDisk.OpenWriterAsync(4, 8, cancellationToken).ConfigureAwait(false);
            return new RollbackDiskWriter(this, state, writer, proxyDisk, proxy, slot, key, disk.FileName, disk.Capacity, changed, artifact.Value);
        }
        catch
        {
            if (proxyDisk is not null)
            {
                await proxyDisk.DisposeAsync().ConfigureAwait(false);
            }

            if (deviceKey is { } attached)
            {
                await DetachAsync(proxy, slot, attached, disk.FileName, disk.Capacity, artifact!.Value).ConfigureAwait(false);
            }
            else
            {
                _owner.Provider.Slots.Release(proxy.Vm, slot);
                if (artifact is { } id)
                {
                    await _ledger.UpdateAsync(id, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
                }
            }

            throw;
        }
    }

    /// <summary>A disk left out of the backup stays as it is in the VM.</summary>
    private async Task<string> LeaveDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var state = await PrepareRollbackAsync(cancellationToken).ConfigureAwait(false);
        _log.Info($"{target.Name} of {state.Name} was not in the backup; the rollback leaves it as it is.");
        return state.Disks.TryGetValue(target.SourceDiskId, out var disk) ? disk.FileName : target.SourceDiskId;
    }

    /// <summary>All disks written: the safety snapshot goes (unless kept), change tracking is reset.</summary>
    private async Task<RestoredVm> FinishRollbackAsync(CancellationToken cancellationToken)
    {
        var state = _rollback ?? throw new InvalidOperationException("No disk was rolled back.");
        if (state.Snapshot is { } snapshot)
        {
            if (_plan.KeepSafetySnapshot)
            {
                _log.Info($"The safety snapshot of {state.Name} stays (\"backdafuckup rollback safety snapshot\"): it holds the VM as it was before the rollback. " +
                          "Delete it when it is no longer needed; while it exists the VM runs on a snapshot.");
            }
            else
            {
                var watch = Stopwatch.StartNew();
                await VSphereBackupSession.RemoveSnapshotAsync(Vim, snapshot, state.Name, _log, cancellationToken).ConfigureAwait(false);
                _log.Info($"Safety snapshot of {state.Name} removed (the rollback consolidated into the disks) in {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
            }

            await _ledger.UpdateAsync(state.SnapshotArtifact!.Value, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        // The proxy's writes are not in the VM's change tracking: a new epoch makes the next backup read the disks fully.
        foreach (var enabled in new[] { false, true })
        {
            var task = await Vim.StartTaskAsync("ReconfigVM_Task", state.Vm, cancellationToken, VimXml.E("spec", VimXml.E("changeTrackingEnabled", enabled ? "true" : "false")))
                .ConfigureAwait(false);
            await Vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }

        _completed = true;
        _log.Info($"Quick rollback of {state.Name}: {Bytes(Interlocked.Read(ref state.Changed))} changed since the restore point, {Bytes(Interlocked.Read(ref state.Written))} written back. " +
                  "Change tracking was reset: the next backup reads the disks fully.");
        return new RestoredVm(state.InstanceUuid, state.Name);
    }

    private async Task<RestoredVm> CompleteRollbackAsync(CancellationToken cancellationToken)
    {
        var state = _rollback ?? throw new InvalidOperationException("No disk was rolled back.");
        if (_plan.PowerOn || state.WasPoweredOn)
        {
            _log.Info($"Starting {state.Name} again.");
            await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("PowerOnVM_Task", state.Vm, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        return new RestoredVm(state.InstanceUuid, state.Name);
    }

    /// <summary>The rollback failed: the VM goes back to the safety snapshot, which is then removed.</summary>
    private async Task UndoRollbackAsync()
    {
        if (_rollback is not { Snapshot: { } snapshot } state)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await Vim.WaitForTaskAsync(await Vim.StartTaskAsync("RevertToSnapshot_Task", snapshot, timeout.Token, VimXml.E("suppressPowerOn", "true")).ConfigureAwait(false), timeout.Token)
                .ConfigureAwait(false);
            await VSphereBackupSession.RemoveSnapshotAsync(Vim, snapshot, state.Name, _log, timeout.Token).ConfigureAwait(false);
            await _ledger.UpdateAsync(state.SnapshotArtifact!.Value, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
            _log.Info($"The rollback failed; {state.Name} is back as it was before it (reverted to the safety snapshot, which is removed). " +
                      (state.WasPoweredOn ? "It was running before: start it again." : string.Empty));
        }
        catch (Exception ex) when (ex is VimFaultException or VimTaskException or HttpRequestException or IOException or OperationCanceledException)
        {
            _log.Warning($"The rollback failed and {state.Name} could not be reverted to its safety snapshot ({ex.Message}). " +
                         "Revert it to \"backdafuckup rollback safety snapshot\" in vCenter before starting it: its disks may be partly rolled back.");
        }
    }

    /// <summary>Writes exactly the changed areas from the restore point (zeros included: the VM's disk has other data there).</summary>
    private sealed class RollbackDiskWriter(
        VSphereRestoreSession session, RollbackState state, IDiskBlockWriter writer, IProxyDisk proxyDisk, ProxyVm proxy, ScsiSlot slot, int deviceKey, string fileName, long capacity,
        IReadOnlyList<DiskExtent> changed, Guid artifact) : IDiskWriter
    {
        private const int Piece = 1 << 20;
        private long _transferred;
        private int _closed;

        public string DiskId => fileName;

        public long Capacity => capacity;

        public DiskWriterCapabilities Capabilities => DiskWriterCapabilities.RandomAccess;

        public long TransferredBytes => Interlocked.Read(ref _transferred);

        public async Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(image);
            var pieces = new List<(long Offset, int Length)>();
            foreach (var extent in changed)
            {
                var end = Math.Min(extent.Offset + extent.Length, image.Capacity);
                for (var offset = extent.Offset; offset < end; offset += Piece)
                {
                    pieces.Add((offset, (int)Math.Min(Piece, end - offset)));
                }
            }

            var done = image.Capacity - pieces.Sum(p => (long)p.Length);
            processed?.Report(done);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(writer.MaxConcurrentRequests, 1, 16), CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(pieces, options, async (piece, ct) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(piece.Length);
                try
                {
                    var memory = buffer.AsMemory(0, piece.Length);
                    await image.ReadAsync(piece.Offset, memory, ct).ConfigureAwait(false);
                    await writer.WriteAsync(piece.Offset, memory, ct).ConfigureAwait(false);
                    Interlocked.Add(ref _transferred, piece.Length);
                    Interlocked.Add(ref state.Written, piece.Length);
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
