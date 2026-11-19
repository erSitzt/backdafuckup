using System.Diagnostics;
using System.Globalization;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Core.Repository;
using Backdafuckup.Lvhd;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Arrays;

/// <summary>A step of the array path failed; the VM's disks are read from its VM snapshot instead.</summary>
public sealed class ArrayPathException(string message, string reason, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The reason as a metric label: array_error, no_proxy, not_found, lun_error.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Backup from storage snapshots. After a VM's snapshot is taken (the guest is thawed again), its disks are read from an
/// array snapshot of the volumes they lie on, when every disk can be: one array snapshot per volume serves up to
/// <see cref="StorageSystemOptions.MaxVmsPerSnapshot"/> VMs of a job run that arrive within
/// <see cref="StorageSystemOptions.BatchWindowSeconds"/>. The snapshot is presented to a proxy with SAN access; once each
/// VM's chains are found and checked on the LUN, its VM snapshot is released, so the VM snapshot lives only seconds. The
/// last VM of a batch gives the LUN back (device removed on the proxy, presentation and snapshot removed on the array).
/// Whatever does not fit, or fails before the VM snapshot is released, falls back to the normal path with a log line.
/// The disks are found on the LUN by an experimental reader per layout: LVHD (XCP-ng LVM SRs) or VMFS6 (vSphere datastores,
/// through <see cref="IVmfsReader"/> when the build has one).
/// </summary>
public sealed class ArraySnapshotService(
    IDbContextFactory<AppDbContext> dbFactory, StorageSystems systems, ArrayReaderStates readerStates, IProxyPool proxies, Telemetry.BackupMetrics metrics, TimeProvider time,
    ILogger<ArraySnapshotService> logger, IVmfsReader? vmfs = null) : ISnapshotLifecycleHook
{
    /// <summary>How often the expiry of a snapshot still in use is moved forward.</summary>
    internal static readonly TimeSpan ExtendEvery = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private readonly List<Batch> _open = [];

    public async Task<ArrayReadLease?> OnVmSnapshotCreatedAsync(VmSnapshotContext context, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        var started = Stopwatch.GetTimestamp();
        var snapshotAt = time.GetUtcNow();
        var backup = context.Session;
        var plan = await PlanAsync(context, cancellationToken).ConfigureAwait(false);
        if (plan.Refusal is { } refusal)
        {
            log.Info($"Backup from storage snapshots: not for {backup.Vm.Name}, {refusal}. Its disks are read from the VM snapshot.");
            return null;
        }

        // What changed is asked while the VM snapshot still exists.
        var changes = new Dictionary<string, ChangedExtents>(StringComparer.Ordinal);
        foreach (var disk in backup.Disks)
        {
            changes[disk.Key] = await backup.GetChangedExtentsAsync(disk, context.Previous.GetValueOrDefault(disk.Key), log, cancellationToken).ConfigureAwait(false);
        }

        var batch = Join(context, plan, log, snapshotAt);
        BatchState state;
        try
        {
            state = await batch.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArrayPathException ex)
        {
            await LeaveAsync(batch).ConfigureAwait(false);
            metrics.RecordArrayFallback(plan.System.Name, ex.Reason);
            log.Warning($"Backup from storage snapshots failed for {backup.Vm.Name}: {ex.Message} Its disks are read from the VM snapshot.");
            return null;
        }
        catch
        {
            await LeaveAsync(batch).ConfigureAwait(false);
            throw;
        }

        // Each disk found on the LUN and checked against what the platform reported, before the VM snapshot is given up.
        var readers = new Dictionary<string, OpenedDisk>(StringComparer.Ordinal);
        var verifiedRuns = plan.ReaderState.VerifiedRuns;
        try
        {
            foreach (var disk in backup.Disks)
            {
                var facts = plan.Disks[disk.Key];
                var volume = plan.Volumes[facts.StorageId];
                var lun = state.Luns[volume.VolumeUid];
                readers[disk.Key] = plan.Reader == ArrayReaderStates.Vmfs6
                    ? await OpenVmfsDiskAsync(lun, disk, facts, cancellationToken).ConfigureAwait(false)
                    : await OpenLvhdDiskAsync(lun, disk, facts, cancellationToken).ConfigureAwait(false);
            }

            // A new on-disk format version (an ESXi update, say) starts verify mode again.
            foreach (var version in state.Luns.Values.Select(l => l.VmfsVolume).OfType<Task<IVmfsVolume>>().Where(t => t.IsCompletedSuccessfully).Select(t => t.Result.FormatVersion).Distinct())
            {
                if (await readerStates.ObserveFormatAsync(plan.System.Id, plan.Reader, version, cancellationToken).ConfigureAwait(false) is { } previous)
                {
                    verifiedRuns = 0;
                    log.Info($"Storage system {plan.System.Name}: the datastore format is {version} now (was {previous}); verify mode starts again.");
                }
            }
        }
        catch (Exception ex) when (ex is LvhdRefusedException or VmfsRefusedException or IOException or InvalidDataException or HttpRequestException)
        {
            await CloseAsync(readers.Values).ConfigureAwait(false);
            await LeaveAsync(batch).ConfigureAwait(false);
            var reason = ex switch
            {
                LvhdRefusedException => "lvhd_refused",
                VmfsRefusedException { Kind: VmfsRefusalKinds.Io } => "lun_error",
                VmfsRefusedException vmfsRefused => $"vmfs_{vmfsRefused.Kind}",
                _ => "lun_error",
            };
            metrics.RecordArrayFallback(plan.System.Name, reason);
            var message = $"Backup from storage snapshots: the disks of {backup.Vm.Name} are not read from array snapshot {state.Names} ({ex.Message}). They are read from the VM snapshot.";
            if (ex is VmfsRefusedException { Kind: VmfsRefusalKinds.InUse })
            {
                message += " A host had the disk open for writing when the array snapshot was taken, so it holds no consistent state of it: is the disk independent, or was the VM snapshot not over it?";
            }

            if (ex is VmfsRefusedException { Kind: VmfsRefusalKinds.Corrupt })
            {
                log.Error(message);
            }
            else
            {
                log.Warning(message);
            }

            return null;
        }

        // Verify mode: the first backups through this reader compare a sample with the VM snapshot, which still exists.
        var verifications = new Dictionary<string, DiskVerification>(StringComparer.Ordinal);
        var readerName = ArrayReaderStates.DisplayName(plan.Reader);
        if (verifiedRuns < plan.System.Options.ProbationRuns)
        {
            try
            {
                foreach (var disk in backup.Disks)
                {
                    var arrayReader = readers[disk.Key].Reader;
                    var c = changes[disk.Key];
                    var regions = VerifyAgainstVmSnapshot.PickRegions(
                        c.IsFull ? c.Allocated ?? readers[disk.Key].Allocated : c.Extents, disk.CapacityBytes, VerifyAgainstVmSnapshot.RegionsPerDisk);
                    if (regions.Count == 0)
                    {
                        continue;
                    }

                    await using var normal = await backup.OpenReaderAsync(disk, new DiskReaderOptions(1, 4), cancellationToken).ConfigureAwait(false);
                    if (normal is not IDiskReader random)
                    {
                        throw new ArrayPathException($"disk {disk.Name} cannot be read at given offsets through {normal.TransportName}, which verify mode needs", "verify_unavailable");
                    }

                    var result = await VerifyAgainstVmSnapshot.RunAsync(arrayReader, random, regions, cancellationToken).ConfigureAwait(false);
                    metrics.RecordArrayVerify(plan.System.Name, VerifyAgainstVmSnapshot.Method, result.Verification.Passed);
                    if (!result.Verification.Passed)
                    {
                        var reason = $"verify mode: disk {disk.Name} of {backup.Vm.Name} read differently from array snapshot {state.Names} than through {random.TransportName} " +
                                     $"in {result.Verification.Mismatches} of {result.Verification.Regions} regions, first {result.FirstMismatch}";
                        await readerStates.TripAsync(plan.System.Id, plan.Reader, reason, CancellationToken.None).ConfigureAwait(false);
                        throw new ArrayPathException(
                            $"{reason}. The {readerName} reader is switched off for {plan.System.Name} until it is turned back on under Storage systems.", "verify_mismatch");
                    }

                    verifications[disk.Key] = result.Verification;
                }
            }
            catch (Exception ex) when (ex is ArrayPathException or IOException or InvalidDataException or HttpRequestException or NotSupportedException or InvalidOperationException)
            {
                await CloseAsync(readers.Values).ConfigureAwait(false);
                await LeaveAsync(batch).ConfigureAwait(false);
                var reason = ex is ArrayPathException path ? path.Reason : "verify_unavailable";
                metrics.RecordArrayFallback(plan.System.Name, reason);
                var message = $"Backup from storage snapshots: {ex.Message}{(ex is ArrayPathException ? string.Empty : " (verify mode could not compare)")}. The disks of {backup.Vm.Name} are read from the VM snapshot.";
                if (reason == "verify_mismatch")
                {
                    log.Error(message);
                }
                else
                {
                    log.Warning(message);
                }

                return null;
            }

            var verified = await readerStates.RecordVerifiedAsync(plan.System.Id, plan.Reader, cancellationToken).ConfigureAwait(false);
            log.Info($"Verify mode ({readerName} reader on {plan.System.Name}, backup {verified.VerifiedRuns} of {plan.System.Options.ProbationRuns}): " +
                     $"{verifications.Values.Sum(v => v.Regions)} region(s) of {backup.Vm.Name} read the same from the array snapshot and from the VM snapshot.");
        }

        // The disks are found on the LUN: the VM snapshot goes now.
        try
        {
            await backup.ReleaseSnapshotAsync(log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CloseAsync(readers.Values).ConfigureAwait(false);
            await LeaveAsync(batch).ConfigureAwait(false);
            metrics.RecordArrayFallback(plan.System.Name, "release_failed");
            log.Warning($"Backup from storage snapshots: the VM snapshot of {backup.Vm.Name} could not be released early ({ex.Message}); its disks are read from it.");
            return null;
        }

        var lifetime = (backup.Snapshot?.Duration ?? TimeSpan.Zero) + Stopwatch.GetElapsedTime(started);
        metrics.RecordVmSnapshotLifetime(plan.System.Name, "array", lifetime);
        log.Info($"Disks of {backup.Vm.Name} are read from array snapshot {state.Names} on {plan.System.Name} through proxy {state.Proxy.Name}; " +
                 $"the VM snapshot existed {lifetime.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
        return new Lease(this, batch, state, plan, changes, readers, verifications, backup, lifetime);
    }

    private Task TripAsync(Plan plan, string reason, CancellationToken cancellationToken) => readerStates.TripAsync(plan.System.Id, plan.Reader, reason, cancellationToken);

    /// <summary>The VHD chain of an XCP-ng disk in the SR's volume group on the LUN, checked against what XAPI reported.</summary>
    private static async Task<OpenedDisk> OpenLvhdDiskAsync(Lun lun, SnapshotDisk disk, ArrayDiskFacts facts, CancellationToken cancellationToken)
    {
        var chain = await LvhdSnapshot.OpenAsync(
            new DiskReaderBlockSource(lun.Reader), facts.StorageId, [.. facts.Chain.Select(m => new ExpectedVdi(m.Id, m.VirtualSize, m.PhysicalUtilisation))], cancellationToken)
            .ConfigureAwait(false);
        if (chain.Capacity != disk.CapacityBytes)
        {
            throw new LvhdRefusedException($"disk {disk.Name} is {disk.CapacityBytes} bytes, its chain on the LUN {chain.Capacity}");
        }

        return new OpenedDisk(new LvhdDiskReader(chain, lun.Reader), chain.AllocatedExtents);
    }

    /// <summary>
    /// The VMDK under the VM snapshot (the descriptor vim25 named in the snapshot's configuration) on the datastore on the
    /// LUN, its size checked against vim25. The volume is opened once per LUN and shared by the batch's VMs.
    /// </summary>
    private async Task<OpenedDisk> OpenVmfsDiskAsync(Lun lun, SnapshotDisk disk, ArrayDiskFacts facts, CancellationToken cancellationToken)
    {
        var reader = vmfs ?? throw new VmfsRefusedException("this build has no VMFS reader", VmfsRefusalKinds.Unsupported);
        Task<IVmfsVolume> opening;
        lock (lun)
        {
            opening = lun.VmfsVolume ??= reader.OpenVolumeAsync(lun.Reader, $"array snapshot {lun.Snapshot.Name} on {lun.Disk.Description}", CancellationToken.None);
        }

        var volume = await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = facts.Chain[0].Id;
        var opened = await volume.OpenDiskAsync(path, cancellationToken).ConfigureAwait(false);
        if (opened.Capacity != disk.CapacityBytes)
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw new VmfsRefusedException($"disk {disk.Name} ({path}) is {disk.CapacityBytes} bytes in vCenter, {opened.Capacity} on the LUN", VmfsRefusalKinds.Unsupported);
        }

        return new OpenedDisk(opened, opened.AllocatedExtents);
    }

    private static async Task CloseAsync(IEnumerable<OpenedDisk> disks)
    {
        foreach (var disk in disks)
        {
            await disk.Reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Whether every disk of the VM can be read from an array snapshot, and from which volumes; or why not.</summary>
    private async Task<Plan> PlanAsync(VmSnapshotContext context, CancellationToken cancellationToken)
    {
        var backup = context.Session;
        if (backup is not IArraySnapshotFacts source)
        {
            return Plan.Refused("its platform cannot read disks from array snapshots");
        }

        if (!backup.CanReleaseEarly)
        {
            return Plan.Refused("its VM snapshot cannot be released before the disks are read (a memory image, or a disk without change tracking)");
        }

        if (backup.Disks.Count == 0)
        {
            return Plan.Refused("it has no disks to read");
        }

        IReadOnlyDictionary<string, ArrayDiskFacts> disks;
        try
        {
            disks = await source.GetArrayDisksAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Plan.Refused($"the chains of its disks could not be read from the platform ({ex.Message})");
        }

        var outside = backup.Disks.Where(d => !disks.ContainsKey(d.Key)).ToList();
        if (outside.Count > 0)
        {
            return Plan.Refused($"disk(s) {string.Join(", ", outside.Select(d => $"{d.Name} on {d.StorageName}"))} lie on no storage that can be read from array snapshots " +
                                "(XCP-ng LVM over iSCSI or FC, a VMFS datastore on one LUN)");
        }

        var layouts = disks.Values.Select(d => d.Layout).Distinct().ToList();
        var reader = layouts is [StorageLayout.Lvhd] ? ArrayReaderStates.Lvhd : layouts is [StorageLayout.Vmfs6] ? ArrayReaderStates.Vmfs6 : null;
        if (reader is null)
        {
            return Plan.Refused($"its disks lie on {string.Join(" and ", layouts)} storage; XCP-ng LVM storage (LVHD) and VMFS6 datastores are read from array snapshots");
        }

        if (reader == ArrayReaderStates.Vmfs6 && vmfs is null)
        {
            return Plan.Refused("reading VMFS6 datastores from array snapshots needs the OpenVmfs reader, which this build of the server does not include");
        }

        // Trust anchor: the VMFS reader is used only for disks that have a restore point, so every run can be checked against it.
        if (reader == ArrayReaderStates.Vmfs6 && backup.Disks.FirstOrDefault(d => context.Previous.GetValueOrDefault(d.Key) is null) is { } first)
        {
            return Plan.Refused($"disk {first.Name} has no restore point yet; the VMFS reader is used from a disk's second backup on, so that every run is checked against the previous one");
        }

        var storageIds = disks.Values.Select(d => d.StorageId).Distinct(StringComparer.Ordinal).ToList();
        List<StorageVolumeMapping> mappings;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            mappings = await db.StorageVolumes.AsNoTracking()
                .Where(m => m.ConnectionId == context.ConnectionId && m.StorageNativeId != null && storageIds.Contains(m.StorageNativeId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var volumes = new Dictionary<string, StorageVolumeMapping>(StringComparer.Ordinal);
        foreach (var storageId in storageIds)
        {
            var name = backup.Disks.First(d => disks[d.Key].StorageId == storageId).StorageName;
            var mapping = mappings.FirstOrDefault(m => m.StorageNativeId == storageId);
            if (mapping is null)
            {
                return Plan.Refused($"storage {name} lies on no volume of a storage system (Storage systems → refresh the mapping)");
            }

            if (mapping.Refusal is { } refusal)
            {
                return Plan.Refused($"storage {name} on volume {mapping.VolumeName}: {refusal}");
            }

            if (!mapping.Enabled)
            {
                return Plan.Refused($"volume {mapping.VolumeName} (storage {name}) is not turned on for storage snapshots");
            }

            if (disks.Values.Any(d => d.StorageId == storageId && d.Layout != mapping.Layout))
            {
                return Plan.Refused($"storage {name} is {mapping.Layout} by the volume mapping, but {disks.Values.First(d => d.StorageId == storageId).Layout} by the platform now (Storage systems → refresh the mapping)");
            }

            volumes[storageId] = mapping;
        }

        var systemIds = volumes.Values.Select(v => v.StorageSystemId).Distinct().ToList();
        if (systemIds.Count > 1)
        {
            return Plan.Refused($"its disks lie on volumes of {systemIds.Count} storage systems");
        }

        StorageSystemEntity? system;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            system = await db.StorageSystems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == systemIds[0], cancellationToken).ConfigureAwait(false);
        }

        if (system is null)
        {
            return Plan.Refused("the storage system of its volumes was removed");
        }

        if (!system.Options.ExperimentalReaders)
        {
            return Plan.Refused($"reading {ArrayReaderStates.DisplayName(reader)} from array snapshots is experimental; turn on 'Experimental readers' for storage system {system.Name}");
        }

        var readerState = ArrayReaderStates.Find(system, reader);
        if (readerState.Tripped)
        {
            return Plan.Refused(
                $"the {ArrayReaderStates.DisplayName(reader)} reader is switched off for storage system {system.Name} since " +
                $"{readerState.TrippedUtc:yyyy-MM-dd HH:mm} UTC ({readerState.TripReason}); turn it back on under Storage systems once the cause is known");
        }

        return new Plan(null, system, volumes, disks, reader, readerState);
    }

    /// <summary>Joins the open batch of this job run on this storage system, or opens one (which then waits for others).</summary>
    private Batch Join(VmSnapshotContext context, Plan plan, IOperationLog log, DateTimeOffset snapshotAt)
    {
        Batch? batch;
        var start = false;
        lock (_lock)
        {
            batch = _open.FirstOrDefault(b => !b.Closed && b.SessionId == context.SessionId && b.System.Id == plan.System.Id && b.Members.Count < b.Capacity);
            if (batch is null)
            {
                var capacity = Math.Clamp(Math.Min(plan.System.Options.MaxVmsPerSnapshot, context.MaxPeers), 1, 64);
                batch = new Batch(Guid.CreateVersion7(), context.SessionId, plan.System, capacity, context.Hypervisor);
                _open.Add(batch);
                start = true;
            }

            batch.Members.Add(new Member(context, plan, log, snapshotAt));
            batch.Users++;
            if (batch.Members.Count >= batch.Capacity)
            {
                batch.Full.TrySetResult();
            }
        }

        if (start)
        {
            _ = Task.Run(() => RunBatchAsync(batch));
        }

        return batch;
    }

    /// <summary>Waits for the batch to fill (or its window to pass), then takes the snapshot and presents it to a proxy.</summary>
    private async Task RunBatchAsync(Batch batch)
    {
        var window = TimeSpan.FromSeconds(Math.Max(0, batch.System.Options.BatchWindowSeconds));
        if (batch.Capacity > 1 && window > TimeSpan.Zero)
        {
            await Task.WhenAny(batch.Full.Task, Task.Delay(window, time)).ConfigureAwait(false);
        }

        List<Member> members;
        lock (_lock)
        {
            batch.Closed = true;
            _open.Remove(batch);
            members = [.. batch.Members];
        }

        var log = new FanOutLog([.. members.Select(m => m.Log)], logger);
        var state = new BatchState(batch.System);
        try
        {
            await TakeAsync(batch, members, state, log).ConfigureAwait(false);
            batch.Ready.TrySetResult(state);
        }
        catch (Exception ex)
        {
            var failure = ex as ArrayPathException ?? new ArrayPathException($"{ex.Message} (storage system {batch.System.Name}, {batch.System.Address}).", "array_error", ex);
            batch.Ready.TrySetException(failure);
        }

        bool cleanup;
        lock (_lock)
        {
            cleanup = batch.Users == 0 && !batch.CleanedUp;
            batch.CleanedUp |= cleanup;
        }

        if (!cleanup && batch.Ready.Task.IsFaulted)
        {
            // Nobody reads a failed batch: what it made goes now (members only leave it).
            lock (_lock)
            {
                cleanup = !batch.CleanedUp;
                batch.CleanedUp = true;
            }
        }

        if (cleanup)
        {
            await CleanupAsync(batch, state, log).ConfigureAwait(false);
        }
    }

    private async Task TakeAsync(Batch batch, List<Member> members, BatchState state, IOperationLog log)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var first = members[0].Context;
        var system = batch.System;
        state.Array = await systems.OpenAsync(system.Id, ct).ConfigureAwait(false);
        state.Ledger = new TrackingLedger(new DbArtifactLedger(dbFactory, first.ConnectionId, first.SessionId, null, time, system.Id));

        var wanted = members.SelectMany(m => m.Plan.Volumes.Values).GroupBy(v => v.VolumeUid, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var volumes = await state.Array.FindVolumesByWwnAsync([.. wanted.Select(v => v.Wwn)], ct).ConfigureAwait(false);
        var missing = wanted.Where(w => !volumes.Any(v => Wwns.Normalize(v.Wwn) == w.Wwn)).ToList();
        if (missing.Count > 0)
        {
            throw new ArrayPathException(
                $"Volume(s) {string.Join(", ", missing.Select(m => $"{m.VolumeName} (WWN {m.Wwn})"))} not found on storage system {system.Name} ({system.Address}).", "not_found");
        }

        (state.Proxy, state.HostName) = await ChooseProxyAsync(state.Array, system, ct).ConfigureAwait(false);
        MarkBusy(state.Proxy.Id);
        var portals = (await state.Array.GetIscsiPortalsAsync(ct).ConfigureAwait(false)).Where(p => p.TargetIqn is { Length: > 0 }).ToList();
        if (portals.Count == 0)
        {
            throw new ArrayPathException($"Storage system {system.Name} ({system.Address}) reports no iSCSI port with a target name.", "array_error");
        }

        // Not sooner than the gap after the last VM snapshot: measured on VMFS, a disk's block map is in place on the LUN within
        // 0.6-2 s after the host closed it for writing (the VM snapshot), not necessarily at once.
        var ready = members.Max(m => m.SnapshotAt) + TimeSpan.FromSeconds(Math.Max(0, system.Options.MinSecondsAfterVmSnapshot));
        if (ready - time.GetUtcNow() is { Ticks: > 0 } wait)
        {
            await Task.Delay(wait, time, ct).ConfigureAwait(false);
        }

        // The snapshot, while no snapshot of ours is half done on these storages.
        var storages = members.SelectMany(m => m.Plan.Volumes.Keys).Distinct(StringComparer.Ordinal).ToList();
        var hold = batch.Hypervisor is IStorageSnapshotGate gate ? await gate.HoldAsync(storages, ct).ConfigureAwait(false) : null;
        var watch = Stopwatch.StartNew();
        try
        {
            state.Snapshots = await state.Array.CreateSnapshotsAsync(
                new ArraySnapshotRequest(volumes, $"bdfu-{batch.Id.ToString("N", CultureInfo.InvariantCulture)[^8..]}", $"session={first.SessionId}", ReadOnly: true,
                    TimeSpan.FromHours(Math.Max(1, system.Options.ExpiryHours))),
                state.Ledger, log, ct).ConfigureAwait(false);
        }
        finally
        {
            if (hold is not null)
            {
                await hold.DisposeAsync().ConfigureAwait(false);
            }
        }

        state.TakenUtc = time.GetUtcNow();
        metrics.RecordArraySnapshot(system.Name, watch.Elapsed, members.Count);
        log.Info($"Array snapshot {state.Names} of {string.Join(", ", volumes.Select(v => v.Name))} on {system.Name} taken in " +
                 $"{watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s for {members.Count} VM(s).");
        state.StartExtending(time, TimeSpan.FromHours(Math.Max(1, system.Options.ExpiryHours)), logger);

        // Presented to the proxy's host object, logged in, opened read-only on the proxy.
        foreach (var snapshot in state.Snapshots)
        {
            state.Presentations.Add(await state.Array.PresentAsync(snapshot, state.HostName, state.Ledger, ct).ConfigureAwait(false));
        }

        foreach (var target in portals.GroupBy(p => p.TargetIqn!, StringComparer.Ordinal))
        {
            try
            {
                await proxies.LoginIscsiAsync(state.Proxy.Id, [.. target.Select(p => p.Address)], target.Key, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new ArrayPathException($"Proxy {state.Proxy.Name}: {ex.Message}", "no_proxy", ex);
            }
        }

        foreach (var snapshot in state.Snapshots)
        {
            var size = volumes.First(v => v.Uid == snapshot.SourceUid).SizeBytes;
            IProxyDisk disk;
            try
            {
                disk = await proxies.AttachAsync(state.Proxy.Id, Wwns.Normalize(snapshot.Wwn), size, writable: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
            {
                throw new ArrayPathException($"Proxy {state.Proxy.Name} does not see snapshot {snapshot.Name} (WWN {snapshot.Wwn}, LUN of host {state.HostName}): {ex.Message}", "lun_error", ex);
            }

            var lun = new Lun(snapshot, disk);
            state.Luns[snapshot.SourceUid] = lun;
            lun.Reader = await disk.OpenReaderAsync(4, 8, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A connected proxy with SAN access whose iSCSI initiator a host object of the array lists.</summary>
    private async Task<(ProxyInfo Proxy, string HostName)> ChooseProxyAsync(IStorageArraySession array, StorageSystemEntity system, CancellationToken ct)
    {
        var connected = proxies.Connected();
        var san = connected.Where(p => p.Capabilities.Contains(ProxyAgentProtocol.CapabilitySan, StringComparer.Ordinal) && p.InitiatorName is { Length: > 0 }).ToList();
        if (san.Count == 0)
        {
            var why = connected.Count == 0 ? "no proxy is connected" : string.Join("; ", connected.Select(p => $"{p.Name}: {p.SanRefusal ?? "no SAN access"}"));
            throw new ArrayPathException($"No proxy with SAN access ({why}).", "no_proxy");
        }

        var hosts = await array.ListHostsAsync(ct).ConfigureAwait(false);
        foreach (var proxy in san.OrderBy(p => Busy(p.Id)))
        {
            if (hosts.FirstOrDefault(h => h.IscsiNames.Contains(proxy.InitiatorName!, StringComparer.OrdinalIgnoreCase)) is { } host)
            {
                return (proxy, host.Name);
            }
        }

        throw new ArrayPathException(
            $"No host object on storage system {system.Name} ({system.Address}) lists the iSCSI initiator of a proxy with SAN access: " +
            $"{string.Join(", ", san.Select(p => $"{p.Name} {p.InitiatorName}"))}.", "no_proxy");
    }

    /// <summary>Batches currently reading through a proxy (the least busy proxy is chosen).</summary>
    private int Busy(Guid proxyId)
    {
        lock (_lock)
        {
            return _busy.GetValueOrDefault(proxyId);
        }
    }

    private readonly Dictionary<Guid, int> _busy = [];

    private async Task LeaveAsync(Batch batch)
    {
        bool cleanup;
        lock (_lock)
        {
            batch.Users--;
            cleanup = batch.Users == 0 && batch.Ready.Task.IsCompletedSuccessfully && !batch.CleanedUp;
            batch.CleanedUp |= cleanup;
        }

        if (cleanup)
        {
            var state = batch.Ready.Task.Result;
            await CleanupAsync(batch, state, new FanOutLog([], logger)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives the LUNs back in reverse order of creation: readers and devices on the proxy, presentations, snapshots. A step
    /// that fails leaves its ledger row for the background cleanup (and the array's expiry removes the snapshot anyway).
    /// </summary>
    private async Task CleanupAsync(Batch batch, BatchState state, IOperationLog log)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        await state.StopExtendingAsync().ConfigureAwait(false);
        foreach (var lun in state.Luns.Values)
        {
            try
            {
                if (lun.VmfsVolume is { IsCompletedSuccessfully: true } volume)
                {
                    await volume.Result.DisposeAsync().ConfigureAwait(false);
                }

                if (lun.Reader is not null)
                {
                    await lun.Reader.DisposeAsync().ConfigureAwait(false);
                }

                await lun.Disk.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Closing snapshot LUN {Snapshot} on proxy {Proxy} failed", lun.Snapshot.Name, state.Proxy?.Name);
            }
        }

        if (state.Array is not { } array || state.Ledger is not { } ledger)
        {
            return;
        }

        foreach (var presentation in Enumerable.Reverse(state.Presentations))
        {
            await RemoveAsync(ledger, presentation.Uid, () => array.UnpresentAsync(presentation, ct), $"presentation of {presentation.VolumeName} to {presentation.HostName}", batch, log)
                .ConfigureAwait(false);
        }

        foreach (var snapshot in Enumerable.Reverse(state.Snapshots))
        {
            await RemoveAsync(ledger, snapshot.Uid, () => array.DeleteSnapshotAsync(snapshot, ct), $"snapshot {snapshot.Name}", batch, log).ConfigureAwait(false);
        }

        await array.DisposeAsync().ConfigureAwait(false);
        if (state.Proxy is { } proxy)
        {
            lock (_lock)
            {
                _busy[proxy.Id] = Math.Max(0, _busy.GetValueOrDefault(proxy.Id) - 1);
            }
        }
    }

    private async Task RemoveAsync(TrackingLedger ledger, string uid, Func<Task> remove, string what, Batch batch, IOperationLog log)
    {
        try
        {
            await remove().ConfigureAwait(false);
            await ledger.DoneAsync(uid).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Warning($"Storage system {batch.System.Name} ({batch.System.Address}): {what} could not be removed ({ex.Message}); it is removed in the background.");
            await ledger.PendingAsync(uid).ConfigureAwait(false);
        }
    }

    /// <summary>Counts a proxy as busy from the moment a batch chose it.</summary>
    private void MarkBusy(Guid proxyId)
    {
        lock (_lock)
        {
            _busy[proxyId] = _busy.GetValueOrDefault(proxyId) + 1;
        }
    }

    /// <param name="Reader">The experimental reader the disks need (<see cref="ArrayReaderStates.Lvhd"/>), and its state on the system.</param>
    private sealed record Plan(
        string? Refusal, StorageSystemEntity System, IReadOnlyDictionary<string, StorageVolumeMapping> Volumes, IReadOnlyDictionary<string, ArrayDiskFacts> Disks,
        string Reader = ArrayReaderStates.Lvhd, StorageReaderState ReaderState = null!)
    {
        public static Plan Refused(string reason) => new(reason, null!, new Dictionary<string, StorageVolumeMapping>(), new Dictionary<string, ArrayDiskFacts>());
    }

    /// <param name="SnapshotAt">When the VM snapshot was there (the hook was called right after it).</param>
    private sealed record Member(VmSnapshotContext Context, Plan Plan, IOperationLog Log, DateTimeOffset SnapshotAt);

    private sealed class Batch(Guid id, Guid sessionId, StorageSystemEntity system, int capacity, IHypervisorSession hypervisor)
    {
        public Guid Id { get; } = id;

        public Guid SessionId { get; } = sessionId;

        public StorageSystemEntity System { get; } = system;

        public int Capacity { get; } = capacity;

        public IHypervisorSession Hypervisor { get; } = hypervisor;

        public List<Member> Members { get; } = [];

        public TaskCompletionSource Full { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<BatchState> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>No more VMs join.</summary>
        public bool Closed { get; set; }

        /// <summary>Members that have not given their share back.</summary>
        public int Users { get; set; }

        public bool CleanedUp { get; set; }
    }

    private sealed class Lun(ArraySnapshot snapshot, IProxyDisk disk)
    {
        public ArraySnapshot Snapshot { get; } = snapshot;

        public IProxyDisk Disk { get; } = disk;

        /// <summary>The VMFS datastore on the LUN, opened by the first VM that needs it (under a lock on the Lun).</summary>
        public Task<IVmfsVolume>? VmfsVolume { get; set; }

        public IDiskReader Reader { get; set; } = null!;
    }

    /// <summary>What a batch made: the array session, snapshots, presentations, the proxy and the opened LUNs by source volume.</summary>
    private sealed class BatchState(StorageSystemEntity system)
    {
        private CancellationTokenSource? _extend;
        private Task? _extending;

        public StorageSystemEntity System { get; } = system;

        public IStorageArraySession? Array { get; set; }

        public TrackingLedger? Ledger { get; set; }

        public ProxyInfo Proxy { get; set; } = null!;

        public string HostName { get; set; } = string.Empty;

        public IReadOnlyList<ArraySnapshot> Snapshots { get; set; } = [];

        public List<ArrayPresentation> Presentations { get; } = [];

        public Dictionary<string, Lun> Luns { get; } = new(StringComparer.Ordinal);

        public string Names => string.Join(", ", Snapshots.Select(s => s.Name));

        public DateTimeOffset TakenUtc { get; set; }

        /// <summary>Moves the expiry forward every half hour while the snapshots are read.</summary>
        public void StartExtending(TimeProvider time, TimeSpan expiry, ILogger logger)
        {
            _extend = new CancellationTokenSource();
            var stop = _extend.Token;
            _extending = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(ExtendEvery, time, stop).ConfigureAwait(false);
                        foreach (var snapshot in Snapshots)
                        {
                            try
                            {
                                await Array!.ExtendExpiryAsync(snapshot, expiry, stop).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogWarning(ex, "Extending the expiry of array snapshot {Snapshot} on {System} failed", snapshot.Name, System.Name);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Read; the snapshots are removed now.
                }
            }, CancellationToken.None);
        }

        public async Task StopExtendingAsync()
        {
            if (_extend is null)
            {
                return;
            }

            await _extend.CancelAsync().ConfigureAwait(false);
            await (_extending ?? Task.CompletedTask).ConfigureAwait(false);
            _extend.Dispose();
            _extend = null;
        }
    }

    /// <summary>A disk found on the LUN: its reader (owned by the lease) and the areas that hold data.</summary>
    private sealed record OpenedDisk(IDiskReader Reader, IReadOnlyList<Core.Extents.DiskExtent> Allocated);

    /// <summary>The lease's reader handed to the backup, which disposes what it is given; the lease closes the real one.</summary>
    private sealed class BorrowedReader(IDiskReader inner) : IDiskReader
    {
        public long Capacity => inner.Capacity;

        public string TransportName => inner.TransportName;

        public int MaxRequestBytes => inner.MaxRequestBytes;

        public int MaxConcurrentRequests => inner.MaxConcurrentRequests;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => inner.ReadAsync(offset, destination, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>One VM's disks read from the batch's LUNs; disposing gives its share of the batch back.</summary>
    private sealed class Lease(
        ArraySnapshotService owner, Batch batch, BatchState state, Plan plan, IReadOnlyDictionary<string, ChangedExtents> changes, IReadOnlyDictionary<string, OpenedDisk> disks,
        IReadOnlyDictionary<string, DiskVerification> verifications, IVmBackupSession backup, TimeSpan lifetime) : ArrayReadLease
    {
        public override DiskVerification? GetVerification(SnapshotDisk disk) => verifications.GetValueOrDefault(disk.Key);

        public override Task ReportMismatchAsync(SnapshotDisk disk, string details, CancellationToken cancellationToken) =>
            owner.TripAsync(plan, $"disk {disk.Name} of {backup.Vm.Name}: {details}", cancellationToken);

        public override string Reader => ArrayReaderStates.DisplayName(plan.Reader);

        private int _disposed;

        public override string Description => $"array snapshot {state.Names} on {plan.System.Name}";

        public override string Transport => plan.Reader == ArrayReaderStates.Vmfs6 ? "array-vmfs" : "array-lvhd";

        public override ArraySnapshotInfo Info => new()
        {
            System = plan.System.Name,
            Kind = plan.System.Kind,
            Snapshots = [.. state.Snapshots.Select(s => s.Name)],
            TakenUtc = state.TakenUtc,
            Method = plan.Reader,
            VmSnapshotLifetime = lifetime,
        };

        /// <summary>The changes asked before the VM snapshot went; a full read covers only the blocks the chain holds.</summary>
        public override ChangedExtents GetChangedExtents(SnapshotDisk disk) =>
            changes[disk.Key] is { IsFull: true, Allocated: null } full ? full with { Allocated = disks[disk.Key].Allocated } : changes[disk.Key];

        public override ChangeToken? GetNewChangeToken(SnapshotDisk disk) => backup.GetNewChangeToken(disk);

        public override Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken) =>
            Task.FromResult<IDiskSource>(new BorrowedReader(disks[disk.Key].Reader));

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await CloseAsync(disks.Values).ConfigureAwait(false);
                await owner.LeaveAsync(batch).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Keeps the ledger row of each array object by its uid, to close it after the object is removed.</summary>
    private sealed class TrackingLedger(IArtifactLedger inner) : IArtifactLedger
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Guid> _rows = new(StringComparer.Ordinal);

        public Task<Guid> RecordAsync(string kind, string nativeId, string? nativeRef, string? data, CancellationToken cancellationToken) =>
            inner.RecordAsync(kind, nativeId, nativeRef, data, cancellationToken);

        public async Task UpdateAsync(Guid artifactId, ArtifactState state, string? nativeId, CancellationToken cancellationToken)
        {
            await inner.UpdateAsync(artifactId, state, nativeId, cancellationToken).ConfigureAwait(false);
            if (nativeId is { Length: > 0 })
            {
                lock (_lock)
                {
                    _rows[nativeId] = artifactId;
                }
            }
        }

        public Task DoneAsync(string uid) => SetAsync(uid, ArtifactState.Done);

        public Task PendingAsync(string uid) => SetAsync(uid, ArtifactState.CleanupPending);

        private Task SetAsync(string uid, ArtifactState state)
        {
            Guid row;
            lock (_lock)
            {
                if (!_rows.TryGetValue(uid, out row))
                {
                    return Task.CompletedTask;
                }
            }

            return inner.UpdateAsync(row, state, null, CancellationToken.None);
        }
    }

    /// <summary>Writes batch messages into the log of every VM of the batch (and the server log when none is left).</summary>
    private sealed class FanOutLog(IReadOnlyList<IOperationLog> logs, ILogger logger) : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null)
        {
            if (logs.Count == 0)
            {
                logger.Log(severity switch { LogSeverity.Error => LogLevel.Error, LogSeverity.Warning => LogLevel.Warning, _ => LogLevel.Information }, "{Message}", message);
            }

            foreach (var log in logs)
            {
                log.Write(severity, message, key);
            }
        }
    }
}
