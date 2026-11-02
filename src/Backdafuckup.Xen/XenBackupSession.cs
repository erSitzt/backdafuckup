using System.Globalization;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Nbd;
using Backdafuckup.Xen.Tls;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

/// <summary>
/// Backup of one VM: crash-consistent snapshot, CBT queries, NBD readers, and on commit conversion of the
/// snapshot disks to change-tracking metadata (VDI.data_destroy) for the next incremental backup.
/// </summary>
internal sealed class XenBackupSession : IVmBackupSession
{
    private readonly XenHypervisorSession _owner;
    private readonly XapiSession _xapi;
    private readonly IOperationLog _log;
    private readonly IArtifactLedger _ledger;
    private readonly Dictionary<string, DiskState> _disks;
    private readonly string _snapshotRef;
    private readonly Guid _snapshotArtifact;
    private bool _committed;
    private bool _disposed;

    private XenBackupSession(
        XenHypervisorSession owner,
        XapiSession xapi,
        IOperationLog log,
        IArtifactLedger ledger,
        VmDescriptor vm,
        string snapshotRef,
        string snapshotUuid,
        Guid snapshotArtifact,
        Dictionary<string, DiskState> disks,
        VmMetadataBlob metadata)
    {
        _owner = owner;
        _xapi = xapi;
        _log = log;
        _ledger = ledger;
        Vm = vm;
        _snapshotRef = snapshotRef;
        SnapshotId = snapshotUuid;
        _snapshotArtifact = snapshotArtifact;
        _disks = disks;
        Metadata = metadata;
        Disks = disks.Values.Select(d => d.Disk).OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
    }

    public VmDescriptor Vm { get; }

    public IReadOnlyList<SnapshotDisk> Disks { get; }

    public VmMetadataBlob Metadata { get; }

    public string SnapshotId { get; }

    public static async Task<XenBackupSession> CreateAsync(
        XenHypervisorSession owner,
        XapiSession xapi,
        VmBackupRequest request,
        IOperationLog log,
        IArtifactLedger ledger,
        CancellationToken cancellationToken)
    {
        var vmRef = await xapi.TryGetByUuidAsync("VM", request.VmId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"VM {request.VmId} does not exist (anymore).");
        var vm = await xapi.GetRecordAsync("VM", vmRef, cancellationToken).ConfigureAwait(false);
        if (vm.Bool("is_a_template") || vm.Bool("is_a_snapshot") || vm.Bool("is_control_domain"))
        {
            throw new InvalidOperationException($"{vm.Str("name_label")} is not a regular VM.");
        }

        // Make sure every disk tracks changes; disks where CBT was just enabled need a full read this time.
        var cbtJustEnabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vbdRef in vm.Set("VBDs"))
        {
            var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
            if (vbd.Str("type") != "Disk" || XapiRecord.IsNullRef(vbd.Str("VDI")))
            {
                continue;
            }

            var vdi = await xapi.GetRecordAsync("VDI", vbd.Str("VDI"), cancellationToken).ConfigureAwait(false);
            if (!vdi.Bool("cbt_enabled"))
            {
                try
                {
                    await xapi.CallAsync("VDI.enable_cbt", cancellationToken, vbd.Str("VDI")).ConfigureAwait(false);
                    cbtJustEnabled.Add(vdi.Str("uuid"));
                    log.Info($"Enabled changed block tracking on disk {vdi.Str("name_label")}.");
                }
                catch (XapiException ex)
                {
                    log.Warning($"Changed block tracking cannot be enabled on disk {vdi.Str("name_label")} ({ex.Code}); it will always be backed up in full.");
                }
            }
        }

        // Snapshot. The task is recorded before waiting so a crash never leaks the snapshot.
        log.Info($"Creating snapshot of {vm.Str("name_label")}.", "snapshot");
        Guid? taskArtifact = null;
        var snapshotRef = await xapi.RunTaskAsync(
            "VM.snapshot",
            async taskRef => taskArtifact = await ledger.RecordAsync(XenArtifactKinds.XapiTask, string.Empty, taskRef, null, cancellationToken).ConfigureAwait(false),
            owner.Provider.Options.TaskTimeout,
            cancellationToken,
            vmRef,
            request.SnapshotName).ConfigureAwait(false);
        var snapshot = await xapi.GetRecordAsync("VM", snapshotRef, cancellationToken).ConfigureAwait(false);
        var snapshotUuid = snapshot.Str("uuid");
        var snapshotArtifact = await ledger.RecordAsync(XenArtifactKinds.VmSnapshot, snapshotUuid, snapshotRef, null, cancellationToken).ConfigureAwait(false);
        if (taskArtifact is { } t)
        {
            await ledger.UpdateAsync(t, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        log.Info($"Snapshot {request.SnapshotName} created.", "snapshot");

        var disks = new Dictionary<string, DiskState>(StringComparer.Ordinal);
        XenBackupSession? session = null;
        try
        {
            await xapi.CallAsync("VM.add_to_other_config", cancellationToken, snapshotRef, XenTags.Session, request.SessionId.ToString()).ConfigureAwait(false);
            await xapi.CallAsync("VM.add_to_other_config", cancellationToken, snapshotRef, XenTags.RestorePoint, request.RestorePointId.ToString()).ConfigureAwait(false);

            foreach (var vbdRef in snapshot.Set("VBDs"))
            {
                var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
                if (vbd.Str("type") != "Disk" || XapiRecord.IsNullRef(vbd.Str("VDI")))
                {
                    continue;
                }

                var snapVdiRef = vbd.Str("VDI");
                var snapVdi = await xapi.GetRecordAsync("VDI", snapVdiRef, cancellationToken).ConfigureAwait(false);
                var artifact = await ledger.RecordAsync(XenArtifactKinds.SnapshotVdi, snapVdi.Str("uuid"), snapVdiRef, null, cancellationToken).ConfigureAwait(false);
                await xapi.CallAsync("VDI.add_to_other_config", cancellationToken, snapVdiRef, XenTags.Session, request.SessionId.ToString()).ConfigureAwait(false);
                await xapi.CallAsync("VDI.add_to_other_config", cancellationToken, snapVdiRef, XenTags.RestorePoint, request.RestorePointId.ToString()).ConfigureAwait(false);

                var sourceRef = snapVdi.Str("snapshot_of");
                var source = XapiRecord.IsNullRef(sourceRef) ? snapVdi : await xapi.GetRecordAsync("VDI", sourceRef, cancellationToken).ConfigureAwait(false);
                var sr = await xapi.GetRecordAsync("SR", snapVdi.Str("SR"), cancellationToken).ConfigureAwait(false);
                var key = vbd.Str("userdevice");
                var disk = new SnapshotDisk(
                    key,
                    source.Str("uuid"),
                    snapVdi.Str("uuid"),
                    source.Str("name_label"),
                    snapVdi.Long("virtual_size"),
                    vbd.Bool("bootable"),
                    sr.Str("uuid"),
                    sr.Str("name_label"),
                    new Dictionary<string, string> { ["srType"] = sr.Str("type"), ["userdevice"] = key });
                disks[key] = new DiskState(disk, snapVdiRef, sourceRef, artifact, cbtJustEnabled.Contains(source.Str("uuid")))
                {
                    CbtUsable = source.Bool("cbt_enabled"),
                };
            }

            var metadata = await ExportMetadataAsync(xapi, request.VmId, cancellationToken).ConfigureAwait(false);
            var descriptor = await XenInventory.DescribeVmAsync(xapi, vm, cancellationToken).ConfigureAwait(false);
            session = new XenBackupSession(owner, xapi, log, ledger, descriptor, snapshotRef, snapshotUuid, snapshotArtifact, disks, metadata);
            return session;
        }
        finally
        {
            if (session is null)
            {
                await RollbackAsync(xapi, log, ledger, snapshotUuid, snapshotArtifact, disks.Values).ConfigureAwait(false);
            }
        }
    }

    public async Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken)
    {
        var state = _disks[disk.Key];
        if (since is null)
        {
            return ChangedExtents.Full("no previous restore point");
        }

        if (since.Kind != XenTags.ChangeTokenKind)
        {
            return ChangedExtents.Full("previous restore point has no Xen change tracking data");
        }

        if (state.CbtJustEnabled)
        {
            return ChangedExtents.Full("changed block tracking was (re-)enabled on this disk, e.g. for the first backup or after it was reset by a host or storage crash");
        }

        if (_owner.Provider.Options.CbtBitOrder is not { } bitOrder)
        {
            return ChangedExtents.Full("incremental backups are disabled until changed block tracking is verified on this platform (Xen:CbtBitOrder)");
        }

        if (!XapiRecord.IsNullRef(state.SourceVdiRef))
        {
            var source = await _xapi.GetRecordAsync("VDI", state.SourceVdiRef, cancellationToken).ConfigureAwait(false);
            if (!source.Bool("cbt_enabled"))
            {
                return ChangedExtents.Full("changed block tracking was disabled on the disk (e.g. after a host or storage crash)");
            }
        }

        var baseRef = await _xapi.TryGetByUuidAsync("VDI", since.Value, cancellationToken).ConfigureAwait(false);
        if (baseRef is null)
        {
            return ChangedExtents.Full("the change tracking data of the previous backup no longer exists");
        }

        try
        {
            var bitmap = await _xapi.CallStringAsync("VDI.list_changed_blocks", cancellationToken, baseRef, state.SnapshotVdiRef).ConfigureAwait(false);
            var extents = CbtBitmap.DecodeBase64(bitmap, disk.CapacityBytes, bitOrder);
            var changed = extents.Sum(e => e.Length);
            log.Info($"Disk {disk.Name}: {FormatBytes(changed)} changed since the previous backup.");
            return new ChangedExtents(false, null, extents);
        }
        catch (XapiException ex)
        {
            return ChangedExtents.Full($"changed blocks could not be determined ({ex.Code})");
        }
        catch (FormatException ex)
        {
            return ChangedExtents.Full($"invalid changed block bitmap ({ex.Message})");
        }
    }

    public async Task<IDiskReader> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken)
    {
        var state = _disks[disk.Key];
        var infos = await _xapi.CallAsync("VDI.get_nbd_info", cancellationToken, state.SnapshotVdiRef).ConfigureAwait(false) as JsonArray ?? [];
        var candidates = infos.OfType<JsonObject>().ToList();
        if (candidates.Count == 0)
        {
            throw new XenNbdUnavailableException(
                $"No NBD-enabled network reaches storage '{disk.StorageName}'. Enable NBD on a network of the pool (Infrastructure → Pool → Networks).");
        }

        var secure = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Str("cert"))).ToList();
        if (secure.Count == 0 && !_owner.AllowInsecureNbd)
        {
            throw new XenNbdUnavailableException("Only unencrypted NBD (insecure_nbd) is available; allow it for this pool or enable TLS NBD (purpose nbd).");
        }

        var info = (secure.Count > 0 ? secure : candidates)[Random.Shared.Next(secure.Count > 0 ? secure.Count : candidates.Count)];
        var pem = info.Str("cert");
        var connect = new NbdConnectOptions
        {
            Host = info.Str("address"),
            Port = info.Long("port") is > 0 and var port ? (int)port : NbdProtocol.DefaultPort,
            ExportName = info.Str("exportname"),
            TlsMode = string.IsNullOrWhiteSpace(pem) ? NbdTlsMode.Disabled : NbdTlsMode.Required,
            TlsTargetHost = string.IsNullOrWhiteSpace(info.Str("subject")) ? info.Str("address") : info.Str("subject"),
            ServerCertificateValidation = string.IsNullOrWhiteSpace(pem)
                ? null
                : CertificatePinning.CreateValidator(TlsTrustMode.Pinned, [CertificatePinning.SpkiSha256FromPem(pem)]),
            MaxInFlight = options.MaxInFlightPerConnection,
        };

        var connections = Math.Clamp(options.Connections, 1, 16);
        var pool = await NbdConnectionPool.CreateAsync(ct => NbdClient.ConnectAsync(connect, ct), connections, options.MaxInFlightPerConnection, cancellationToken).ConfigureAwait(false);
        if (pool.Size != disk.CapacityBytes)
        {
            await pool.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"NBD export size {pool.Size} does not match the disk size {disk.CapacityBytes}.");
        }

        _log.Info($"Disk {disk.Name}: reading via NBD from {connect.Host} ({(connect.TlsMode == NbdTlsMode.Required ? "TLS" : "unencrypted")}, {connections} connections).");
        return new NbdDiskReader(pool, connections * options.MaxInFlightPerConnection);
    }

    public ChangeToken? GetNewChangeToken(SnapshotDisk disk) =>
        _disks.TryGetValue(disk.Key, out var state) && state.CbtUsable ? new ChangeToken(XenTags.ChangeTokenKind, state.Disk.SnapshotDiskId) : null;

    public async Task CommitAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        foreach (var state in _disks.Values)
        {
            await _ledger.UpdateAsync(state.Artifact, ArtifactState.CommitPending, null, cancellationToken).ConfigureAwait(false);
        }

        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.CommitPending, null, cancellationToken).ConfigureAwait(false);

        foreach (var state in _disks.Values)
        {
            if (state.CbtUsable)
            {
                await XenHypervisorSession.DataDestroyAsync(_xapi, state.SnapshotVdiRef, log, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _xapi.CallAsync("VDI.destroy", cancellationToken, state.SnapshotVdiRef).ConfigureAwait(false);
            }

            await _ledger.UpdateAsync(state.Artifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        await _xapi.CallAsync("VM.destroy", cancellationToken, _snapshotRef).ConfigureAwait(false);
        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        _committed = true;
        log.Info("Snapshot removed; change tracking data kept for the next incremental backup.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_committed)
        {
            await RollbackAsync(_xapi, _log, _ledger, SnapshotId, _snapshotArtifact, _disks.Values).ConfigureAwait(false);
        }

        await _xapi.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task RollbackAsync(XapiSession xapi, IOperationLog log, IArtifactLedger ledger, string snapshotUuid, Guid snapshotArtifact, IEnumerable<DiskState> disks)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await XenHypervisorSession.DestroySnapshotVmAsync(xapi, snapshotUuid, destroyDisks: true, log, timeout.Token).ConfigureAwait(false);
            foreach (var disk in disks)
            {
                await XenHypervisorSession.DestroyVdiByUuidAsync(xapi, disk.Disk.SnapshotDiskId, log, timeout.Token).ConfigureAwait(false);
                await ledger.UpdateAsync(disk.Artifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
            }

            await ledger.UpdateAsync(snapshotArtifact, ArtifactState.Done, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException or OperationCanceledException)
        {
            log.Warning($"The snapshot could not be removed ({ex.Message}); it will be cleaned up automatically later.");
            await ledger.UpdateAsync(snapshotArtifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
            foreach (var disk in disks)
            {
                await ledger.UpdateAsync(disk.Artifact, ArtifactState.CleanupPending, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<VmMetadataBlob> ExportMetadataAsync(XapiSession xapi, string vmUuid, CancellationToken cancellationToken)
    {
        var query = $"export_metadata?session_id={Uri.EscapeDataString(xapi.SessionRef)}&uuid={Uri.EscapeDataString(vmUuid)}&export_snapshots=false";
        using var response = await xapi.Transport.SendHandlerAsync(HttpMethod.Get, query, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"export_metadata failed: HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new VmMetadataBlob(XenTags.MetadataFormat, content);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0 GiB", CultureInfo.InvariantCulture)
        : (bytes / (double)(1L << 20)).ToString("0.0 MiB", CultureInfo.InvariantCulture);

    private sealed record DiskState(SnapshotDisk Disk, string SnapshotVdiRef, string SourceVdiRef, Guid Artifact, bool CbtJustEnabled)
    {
        /// <summary>CBT metadata can be kept for the next incremental only if the source disk tracks changes.</summary>
        public bool CbtUsable { get; init; } = true;
    }
}

/// <summary>Exposes an NBD connection pool as a data-mover disk reader.</summary>
internal sealed class NbdDiskReader(NbdConnectionPool pool, int maxConcurrentRequests) : IDiskReader
{
    public long Capacity => pool.Size;

    public int MaxRequestBytes => pool.MaxPayload;

    public int MaxConcurrentRequests => Math.Max(1, maxConcurrentRequests);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        pool.ReadAsync(offset, destination, cancellationToken);

    public ValueTask DisposeAsync() => pool.DisposeAsync();
}
