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
                    await DataDestroyAsync(Xapi, vdiRef, log, cancellationToken).ConfigureAwait(false);
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

    internal static async Task DataDestroyAsync(XapiSession xapi, string vdiRef, IOperationLog log, CancellationToken cancellationToken)
    {
        // The NBD server may still hold the VDI attached for a short moment after the client disconnects.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await xapi.CallAsync("VDI.data_destroy", cancellationToken, vdiRef).ConfigureAwait(false);
                return;
            }
            catch (XapiException ex) when (ex.Code == "VDI_IN_USE" && attempt < 20)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (XapiException ex) when (ex.Code is "VDI_NO_CBT_METADATA" or "VDI_INCOMPATIBLE_TYPE")
            {
                log.Warning($"Change tracking metadata could not be kept ({ex.Code}); the snapshot disk is deleted instead.");
                await xapi.CallAsync("VDI.destroy", cancellationToken, vdiRef).ConfigureAwait(false);
                return;
            }
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
