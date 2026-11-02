using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

internal sealed partial class XenHypervisorSession
{
    public async Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        var xapi = await OpenDedicatedSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await XenBackupSession.CreateAsync(this, xapi, request, log, ledger, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await xapi.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        switch (artifact.Kind)
        {
            case XenArtifactKinds.XapiTask:
                await CleanupTaskAsync(artifact, log, cancellationToken).ConfigureAwait(false);
                break;
            case XenArtifactKinds.VmSnapshot:
                await DestroySnapshotVmAsync(Xapi, artifact.NativeId, destroyDisks: true, log, cancellationToken).ConfigureAwait(false);
                break;
            case XenArtifactKinds.SnapshotVdi:
            case XenArtifactKinds.CbtMetadataVdi:
            case XenArtifactKinds.RestoredVdi:
                await DestroyVdiByUuidAsync(Xapi, artifact.NativeId, log, cancellationToken).ConfigureAwait(false);
                break;
            case XenArtifactKinds.RestoredVm:
                await DestroyVmByUuidAsync(Xapi, artifact.NativeId, log, cancellationToken).ConfigureAwait(false);
                break;
            default:
                log.Warning($"Unknown artifact kind '{artifact.Kind}' ({artifact.NativeId}) was not cleaned up.");
                break;
        }
    }

    public async Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        switch (artifact.Kind)
        {
            case XenArtifactKinds.SnapshotVdi:
                var vdiRef = await Xapi.TryGetByUuidAsync("VDI", artifact.NativeId, cancellationToken).ConfigureAwait(false);
                if (vdiRef is not null)
                {
                    await DataDestroyAsync(Xapi, vdiRef, log, cancellationToken, Provider.Options.SnapshotReleaseTimeout).ConfigureAwait(false);
                }

                break;
            case XenArtifactKinds.VmSnapshot:
                await DestroySnapshotVmAsync(Xapi, artifact.NativeId, destroyDisks: false, log, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await CleanupArtifactAsync(artifact, log, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    public async Task ReleaseChangeTokensAsync(IEnumerable<ChangeToken> tokens, IOperationLog log, CancellationToken cancellationToken)
    {
        foreach (var token in tokens.Where(t => t.Kind == XenTags.ChangeTokenKind))
        {
            await DestroyVdiByUuidAsync(Xapi, token.Value, log, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ProviderArtifact> FindTaggedArtifactsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var vm in (await Xapi.GetAllRecordsAsync("VM", cancellationToken).ConfigureAwait(false)).Values)
        {
            var tags = vm.Map("other_config");
            if (vm.Bool("is_a_snapshot") && tags.TryGetValue(XenTags.Session, out var session))
            {
                yield return Artifact(XenArtifactKinds.VmSnapshot, vm.Str("uuid"), session, tags);
            }
        }

        foreach (var vdi in (await Xapi.GetAllRecordsAsync("VDI", cancellationToken).ConfigureAwait(false)).Values)
        {
            var tags = vdi.Map("other_config");
            if (!tags.TryGetValue(XenTags.Session, out var session))
            {
                continue;
            }

            var kind = vdi.Str("type") == "cbt_metadata" ? XenArtifactKinds.CbtMetadataVdi
                : vdi.Bool("is_a_snapshot") ? XenArtifactKinds.SnapshotVdi
                : XenArtifactKinds.RestoredVdi;
            yield return Artifact(kind, vdi.Str("uuid"), session, tags);
        }

        static ProviderArtifact Artifact(string kind, string uuid, string session, IReadOnlyDictionary<string, string> tags) => new(
            Guid.Empty,
            kind,
            uuid,
            null,
            Guid.TryParse(session, out var s) ? s : null,
            tags.TryGetValue(XenTags.RestorePoint, out var rp) && Guid.TryParse(rp, out var r) ? r : null,
            ArtifactState.Active,
            null);
    }

    /// <summary>
    /// Reduces a snapshot disk to its change-tracking metadata. XAPI refuses this (VDI_IN_USE) while the disk has any VBD,
    /// attached or not: the VBD of its snapshot VM, or one XAPI or xapi-nbd still holds shortly after an export. VBDs of
    /// snapshot VMs are removed first; other users are waited for, and the wait is logged with the current users of the disk.
    /// </summary>
    internal static async Task DataDestroyAsync(XapiSession xapi, string vdiRef, IOperationLog log, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await RemoveSnapshotVbdsAsync(xapi, vdiRef, log, cancellationToken).ConfigureAwait(false);
        var limit = timeout ?? TimeSpan.FromMinutes(5);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var delay = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(3).Ticks, Math.Max(TimeSpan.FromMilliseconds(10).Ticks, limit.Ticks / 10)));
        var nextReport = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await xapi.CallAsync("VDI.data_destroy", cancellationToken, vdiRef).ConfigureAwait(false);
                if (attempt > 1)
                {
                    log.Info($"Snapshot disk released by XAPI after {watch.Elapsed:mm\\:ss} ({attempt} attempts); reduced to change tracking data.");
                }

                return;
            }
            catch (XapiException ex) when (ex.Code == "VDI_IN_USE")
            {
                var usage = await DescribeVdiUsageAsync(xapi, vdiRef, cancellationToken).ConfigureAwait(false);
                if (watch.Elapsed >= limit)
                {
                    throw new XenSnapshotInUseException($"the snapshot disk is still in use after {watch.Elapsed:mm\\:ss} (VDI_IN_USE): {usage}", ex);
                }

                if (watch.Elapsed >= nextReport)
                {
                    log.Info($"Snapshot disk is still in use (VDI_IN_USE), waiting up to {limit:mm\\:ss}: {usage}");
                    nextReport = watch.Elapsed + TimeSpan.FromMinutes(1);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
            catch (XapiException ex) when (ex.Code is "VDI_NO_CBT_METADATA" or "VDI_INCOMPATIBLE_TYPE")
            {
                log.Warning($"Change tracking metadata could not be kept ({ex.Code}); the snapshot disk is deleted instead.");
                await xapi.CallAsync("VDI.destroy", cancellationToken, vdiRef).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// Removes the unattached VBDs of backdafuckup's own snapshot VMs (tagged with <see cref="XenTags.Session"/>) from a
    /// snapshot disk. On XCP-ng 8.3, VDI.data_destroy answered VDI_IN_USE for as long as the snapshot VM still referenced
    /// the disk. VBDs of other VMs or snapshots and attached VBDs stay untouched.
    /// </summary>
    private static async Task RemoveSnapshotVbdsAsync(XapiSession xapi, string vdiRef, IOperationLog log, CancellationToken cancellationToken)
    {
        var vdi = await xapi.GetRecordAsync("VDI", vdiRef, cancellationToken).ConfigureAwait(false);
        foreach (var vbdRef in vdi.Set("VBDs"))
        {
            try
            {
                var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
                var vm = await xapi.GetRecordAsync("VM", vbd.Str("VM"), cancellationToken).ConfigureAwait(false);
                if (vbd.Bool("currently_attached") || !vm.Bool("is_a_snapshot") || !vm.Map("other_config").ContainsKey(XenTags.Session))
                {
                    continue;
                }

                await xapi.CallAsync("VBD.destroy", cancellationToken, vbdRef).ConfigureAwait(false);
                log.Info($"Snapshot disk {vdi.Str("uuid")} '{vdi.Str("name_label")}' detached from snapshot '{vm.Str("name_label")}' ({vm.Str("uuid")}) so that it can be reduced to change tracking data.");
            }
            catch (XapiException ex) when (ex.Code == "HANDLE_INVALID")
            {
                // The VBD or its VM is already gone.
            }
        }
    }

    /// <summary>Who uses a VDI: its running operations and the VBDs with their VMs (diagnostics for VDI_IN_USE).</summary>
    internal static async Task<string> DescribeVdiUsageAsync(XapiSession xapi, string vdiRef, CancellationToken cancellationToken)
    {
        try
        {
            var vdi = await xapi.GetRecordAsync("VDI", vdiRef, cancellationToken).ConfigureAwait(false);
            var parts = new List<string> { $"VDI {vdi.Str("uuid")} '{vdi.Str("name_label")}'" };
            var operations = vdi.Map("current_operations").Values.ToList();
            parts.Add(operations.Count == 0 ? "no running operations" : $"running operations: {string.Join(", ", operations)}");
            foreach (var vbdRef in vdi.Set("VBDs"))
            {
                var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
                var vm = await xapi.GetRecordAsync("VM", vbd.Str("VM"), cancellationToken).ConfigureAwait(false);
                var owner = vm.Bool("is_control_domain") ? $"control domain '{vm.Str("name_label")}'"
                    : vm.Bool("is_a_snapshot") ? $"snapshot '{vm.Str("name_label")}'"
                    : $"VM '{vm.Str("name_label")}'";
                var vbdOperations = vbd.Map("current_operations").Values.ToList();
                parts.Add($"VBD of {owner} ({(vbd.Bool("currently_attached") ? "attached" : "not attached")}{(vbdOperations.Count > 0 ? $", operations: {string.Join(", ", vbdOperations)}" : string.Empty)})");
            }

            if (vdi.Set("VBDs").Count == 0)
            {
                parts.Add("no VBDs");
            }

            return string.Join("; ", parts);
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            return $"details unavailable ({ex.Message})";
        }
    }

    internal static async Task DestroySnapshotVmAsync(XapiSession xapi, string snapshotUuid, bool destroyDisks, IOperationLog log, CancellationToken cancellationToken)
    {
        var vmRef = await xapi.TryGetByUuidAsync("VM", snapshotUuid, cancellationToken).ConfigureAwait(false);
        if (vmRef is null)
        {
            return;
        }

        var vm = await xapi.GetRecordAsync("VM", vmRef, cancellationToken).ConfigureAwait(false);
        if (!vm.Bool("is_a_snapshot"))
        {
            log.Warning($"Refusing to delete VM {snapshotUuid}: it is not a snapshot.");
            return;
        }

        var vdis = new List<string>();
        if (destroyDisks)
        {
            foreach (var vbdRef in vm.Set("VBDs"))
            {
                var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
                if (vbd.Str("type") == "Disk" && !XapiRecord.IsNullRef(vbd.Str("VDI")))
                {
                    vdis.Add(vbd.Str("VDI"));
                }
            }
        }

        await xapi.CallAsync("VM.destroy", cancellationToken, vmRef).ConfigureAwait(false);
        foreach (var vdiRef in vdis)
        {
            try
            {
                await xapi.CallAsync("VDI.destroy", cancellationToken, vdiRef).ConfigureAwait(false);
            }
            catch (XapiException ex) when (ex.Code == "HANDLE_INVALID")
            {
            }
        }

        log.Info($"Removed snapshot {vm.Str("name_label")}.");
    }

    internal static async Task DestroyVdiByUuidAsync(XapiSession xapi, string uuid, IOperationLog log, CancellationToken cancellationToken)
    {
        var vdiRef = await xapi.TryGetByUuidAsync("VDI", uuid, cancellationToken).ConfigureAwait(false);
        if (vdiRef is null)
        {
            return;
        }

        try
        {
            await xapi.CallAsync("VDI.destroy", cancellationToken, vdiRef).ConfigureAwait(false);
        }
        catch (XapiException ex) when (ex.Code is "HANDLE_INVALID" or "UUID_INVALID")
        {
        }
        catch (XapiException ex)
        {
            log.Warning($"Could not delete disk {uuid}: {ex.Code}");
            throw;
        }
    }

    internal static async Task DestroyVmByUuidAsync(XapiSession xapi, string uuid, IOperationLog log, CancellationToken cancellationToken)
    {
        var vmRef = await xapi.TryGetByUuidAsync("VM", uuid, cancellationToken).ConfigureAwait(false);
        if (vmRef is null)
        {
            return;
        }

        var vm = await xapi.GetRecordAsync("VM", vmRef, cancellationToken).ConfigureAwait(false);
        if (vm.Str("power_state") != "Halted")
        {
            await xapi.CallAsync("VM.hard_shutdown", cancellationToken, vmRef).ConfigureAwait(false);
        }

        await xapi.CallAsync("VM.destroy", cancellationToken, vmRef).ConfigureAwait(false);
        log.Info($"Removed VM {vm.Str("name_label")}.");
    }

    private async Task CleanupTaskAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(artifact.NativeRef))
        {
            return;
        }

        try
        {
            var task = await Xapi.GetRecordAsync("task", artifact.NativeRef, cancellationToken).ConfigureAwait(false);
            if (task.Str("status") == "pending")
            {
                await Xapi.CallAsync("task.cancel", cancellationToken, artifact.NativeRef).ConfigureAwait(false);
            }
            else if (task.Str("status") == "success")
            {
                // A snapshot that finished after we lost track of it.
                var resultRef = XapiSession.DecodeTaskResult(task.Str("result"));
                if (resultRef.StartsWith("OpaqueRef:", StringComparison.Ordinal))
                {
                    var vm = await Xapi.GetRecordAsync("VM", resultRef, cancellationToken).ConfigureAwait(false);
                    await DestroySnapshotVmAsync(Xapi, vm.Str("uuid"), destroyDisks: true, log, cancellationToken).ConfigureAwait(false);
                }
            }

            await Xapi.TryDestroyTaskAsync(artifact.NativeRef).ConfigureAwait(false);
        }
        catch (XapiException ex) when (ex.Code == "HANDLE_INVALID")
        {
            // Task already gone.
        }
    }
}
