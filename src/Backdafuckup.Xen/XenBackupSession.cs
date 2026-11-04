using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
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
    private NbdOverview? _nbdOverview;
    private bool _committed;
    private bool _commitDeferred;
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

        // Make sure every disk tracks changes; disks where CBT was just enabled need a full read this time. Disks marked
        // [NOBAK] are left out of the snapshot: fewer disks also make the snapshot shorter.
        var cbtJustEnabled = new HashSet<string>(StringComparer.Ordinal);
        var excluded = new List<(string VdiRef, SnapshotDisk Disk)>();
        var storages = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var snapshotStorages = new HashSet<string>(StringComparer.Ordinal);
        var diskCount = 0;
        foreach (var vbdRef in vm.Set("VBDs"))
        {
            var vbd = await xapi.GetRecordAsync("VBD", vbdRef, cancellationToken).ConfigureAwait(false);
            if (vbd.Str("type") != "Disk" || XapiRecord.IsNullRef(vbd.Str("VDI")))
            {
                continue;
            }

            var vdi = await xapi.GetRecordAsync("VDI", vbd.Str("VDI"), cancellationToken).ConfigureAwait(false);
            if (!storages.TryGetValue(vdi.Str("SR"), out var storage))
            {
                storages[vdi.Str("SR")] = storage = await xapi.GetRecordAsync("SR", vdi.Str("SR"), cancellationToken).ConfigureAwait(false);
            }

            if (vdi.Str("name_label").Contains(XenTags.ExcludeDiskMarker, StringComparison.Ordinal))
            {
                var key = vbd.Str("userdevice");
                excluded.Add((vbd.Str("VDI"), new SnapshotDisk(
                    key, vdi.Str("uuid"), string.Empty, vdi.Str("name_label"), vdi.Long("virtual_size"), vbd.Bool("bootable"),
                    storage.Str("uuid"), storage.Str("name_label"), new Dictionary<string, string> { ["srType"] = storage.Str("type"), ["userdevice"] = key })));
                continue;
            }

            snapshotStorages.Add(vdi.Str("SR"));
            diskCount++;
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

        var storageText = snapshotStorages.Count == 0 ? "no storage"
            : string.Join(", ", snapshotStorages.Select(s => $"'{storages[s].Str("name_label")}'"));
        var options = owner.Provider.Options;
        XenSnapshotGate.Lease? gate = null;
        if (options.OneSnapshotPerStorage)
        {
            gate = await owner.Provider.SnapshotGate.EnterAsync(snapshotStorages.Select(s => storages[s].Str("uuid")), cancellationToken).ConfigureAwait(false);
            if (gate.Waited is { } waited)
            {
                log.Info($"Waited {Seconds(waited)} for the snapshot of another VM on {storageText} (one snapshot at a time per storage).");
            }
        }

        string snapshotRef;
        Guid? taskArtifact = null;
        TimeSpan snapshotTime;
        GuestFreezeResult? freezeResult;
        string free;
        try
        {
            free = await CheckStorageSpaceAsync(xapi, snapshotStorages, options.MinStorageFreePercent, vm.Str("name_label"), cancellationToken).ConfigureAwait(false);

            // Guest processing: freeze the guest as late as possible. Everything slow (CBT, record reads, waiting for
            // other snapshots) is done by now, because VSS holds writes for at most 10 s and only the snapshot itself
            // may run in that window.
            (var freeze, freezeResult) = await FreezeGuestAsync(request.Quiescer, log, cancellationToken).ConfigureAwait(false);

            // Snapshot. The task is recorded before waiting so a crash never leaks the snapshot.
            log.Info($"Creating snapshot of {vm.Str("name_label")}: {diskCount} disk(s) on {free}.", "snapshot");
            var snapshotWatch = Stopwatch.StartNew();
            try
            {
                try
                {
                    snapshotRef = await SnapshotAsync(excluded.Count == 0 ? null : new JsonArray([.. excluded.Select(e => (JsonNode?)e.VdiRef)])).ConfigureAwait(false);
                }
                catch (XapiException ex) when (ex.Code == "MESSAGE_PARAMETER_COUNT_MISMATCH" && excluded.Count > 0)
                {
                    // XAPI before XCP-ng 8.3 has no list of disks to leave out (ignore_vdis).
                    log.Warning($"XAPI on this pool cannot leave disks out of a snapshot ({ex.Code}); {string.Join(", ", excluded.Select(e => e.Disk.Name))} are backed up as well.");
                    excluded.Clear();
                    snapshotRef = await SnapshotAsync(null).ConfigureAwait(false);
                }
            }
            finally
            {
                // Thaw right away, also when the snapshot failed.
                snapshotTime = snapshotWatch.Elapsed;
                freezeResult = await ThawGuestAsync(freeze, freezeResult).ConfigureAwait(false);
            }
        }
        finally
        {
            gate?.Dispose();
        }

        // Polled every 250 ms: with a frozen guest, every second after the snapshot completes counts against the 10 s.
        Task<string> SnapshotAsync(JsonArray? ignoredDisks)
        {
            JsonNode?[] args = ignoredDisks is null ? [vmRef, request.SnapshotName] : [vmRef, request.SnapshotName, ignoredDisks];
            return xapi.RunTaskAsync(
                "VM.snapshot",
                async taskRef => taskArtifact = await ledger.RecordAsync(XenArtifactKinds.XapiTask, string.Empty, taskRef, null, cancellationToken).ConfigureAwait(false),
                options.TaskTimeout,
                TimeSpan.FromMilliseconds(250),
                cancellationToken,
                args);
        }

        if (freezeResult is not null)
        {
            ReportGuestFreeze(log, freezeResult, snapshotTime);
        }

        var snapshot = await xapi.GetRecordAsync("VM", snapshotRef, cancellationToken).ConfigureAwait(false);
        var snapshotUuid = snapshot.Str("uuid");
        var snapshotArtifact = await ledger.RecordAsync(XenArtifactKinds.VmSnapshot, snapshotUuid, snapshotRef, null, cancellationToken).ConfigureAwait(false);
        if (taskArtifact is { } t)
        {
            await ledger.UpdateAsync(t, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

        // Same log key as "Creating snapshot", which this line replaces: it repeats the storages with their free space.
        log.Info($"Snapshot {request.SnapshotName} created in {Seconds(snapshotTime)}: {diskCount} disk(s) on {free}.", "snapshot");

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
                if (excluded.FindIndex(e => e.VdiRef == snapVdi.Str("snapshot_of")) is var index and >= 0)
                {
                    // XAPI snapshotted the disk after all: back it up rather than leave its snapshot behind.
                    log.Warning($"XAPI included the disk {excluded[index].Disk.Name} in the snapshot although it was left out; it is backed up.");
                    excluded.RemoveAt(index);
                }

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

            foreach (var (_, disk) in excluded)
            {
                log.Info($"Disk {disk.Name} ({XenFormat.Bytes(disk.CapacityBytes)}) is not backed up: its name contains {XenTags.ExcludeDiskMarker}. A restore of the VM creates it as an empty disk.");
            }

            var metadata = await ExportMetadataAsync(xapi, request.VmId, cancellationToken).ConfigureAwait(false);
            var descriptor = await XenInventory.DescribeVmAsync(xapi, vm, cancellationToken).ConfigureAwait(false);
            session = new XenBackupSession(owner, xapi, log, ledger, descriptor, snapshotRef, snapshotUuid, snapshotArtifact, disks, metadata)
            {
                GuestFreeze = freezeResult,
                ExcludedDisks = [.. excluded.Select(e => e.Disk).OrderBy(d => d.Key, StringComparer.Ordinal)],
            };
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

    public GuestFreezeResult? GuestFreeze { get; private init; }

    public IReadOnlyList<SnapshotDisk> ExcludedDisks { get; private init; } = [];

    /// <summary>
    /// Refuses the snapshot when a storage of the VM has less than <paramref name="minPercent"/> free. Returns the
    /// storages with their free space for the log ("'Alletra' (12.3 % free)").
    /// </summary>
    private static async Task<string> CheckStorageSpaceAsync(XapiSession xapi, IEnumerable<string> storageRefs, double minPercent, string vmName, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        foreach (var storageRef in storageRefs)
        {
            // Read now, after waiting for other snapshots: they took space meanwhile.
            var storage = await xapi.GetRecordAsync("SR", storageRef, cancellationToken).ConfigureAwait(false);
            var size = storage.Long("physical_size");
            if (size <= 0)
            {
                parts.Add($"'{storage.Str("name_label")}'");
                continue;
            }

            var free = Math.Max(0, size - storage.Long("physical_utilisation"));
            var percent = free * 100.0 / size;
            if (minPercent > 0 && percent < minPercent)
            {
                throw new XenStorageSpaceException(
                    $"Storage '{storage.Str("name_label")}' ({storage.Str("uuid")}) has only {percent.ToString("0.0", CultureInfo.InvariantCulture)} % free " +
                    $"({XenFormat.Bytes(free)} of {XenFormat.Bytes(size)}); a snapshot needs at least {minPercent.ToString("0.#", CultureInfo.InvariantCulture)} % free there. " +
                    $"{vmName} was not snapshotted, so the running VMs keep that space. Free up space on the storage, or change Xen:MinStorageFreePercent.");
            }

            parts.Add($"'{storage.Str("name_label")}' ({percent.ToString("0.0", CultureInfo.InvariantCulture)} % free)");
        }

        return parts.Count == 0 ? "no storage" : string.Join(", ", parts);
    }

    private static async Task<(IGuestFreeze? Freeze, GuestFreezeResult? Result)> FreezeGuestAsync(IGuestQuiescer? quiescer, IOperationLog log, CancellationToken cancellationToken)
    {
        if (quiescer is null)
        {
            return (null, null);
        }

        try
        {
            return (await quiescer.FreezeAsync(log, cancellationToken).ConfigureAwait(false), null);
        }
        catch (GuestQuiesceException ex)
        {
            return (null, GuestFreezeResult.NotConsistent(ex.Message));
        }
    }

    private static async Task<GuestFreezeResult?> ThawGuestAsync(IGuestFreeze? freeze, GuestFreezeResult? result)
    {
        if (freeze is null)
        {
            return result;
        }

        await using (freeze.ConfigureAwait(false))
        {
            try
            {
                return await freeze.ThawAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (GuestQuiesceException ex)
            {
                return GuestFreezeResult.NotConsistent(ex.Message);
            }
        }
    }

    private static void ReportGuestFreeze(IOperationLog log, GuestFreezeResult result, TimeSpan snapshotTime)
    {
        var writers = result.Writers.Count == 0 ? string.Empty
            : " Writers: " + string.Join(", ", result.Writers.Select(w => w.Failure is null ? $"{w.Name} ({w.State})" : $"{w.Name} ({w.State}: {w.Failure})")) + ".";
        if (result.ApplicationConsistent)
        {
            log.Info(
                $"Guest processing: application-consistent snapshot. Writes were held for {Seconds(result.HeldTime)} (VM snapshot {Seconds(snapshotTime)}, " +
                $"freeze {Seconds(result.FreezeTime)}).{writers}");
        }
        else
        {
            log.Warning($"Guest processing: the snapshot is only crash-consistent: {result.Reason} VM snapshot took {Seconds(snapshotTime)}.{writers}");
        }
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture);

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
            var bitmap = Convert.FromBase64String(await _xapi.CallStringAsync("VDI.list_changed_blocks", cancellationToken, baseRef, state.SnapshotVdiRef).ConfigureAwait(false));
            var extents = _owner.Provider.Options.CbtBitOrder is { } bitOrder
                ? CbtBitmap.Decode(bitmap, disk.CapacityBytes, bitOrder)
                : CbtBitmap.DecodeByteRanges(bitmap, disk.CapacityBytes);
            log.Info($"Disk {disk.Name}: {FormatBytes(CbtBitmap.ChangedBytes(bitmap, disk.CapacityBytes))} changed since the previous backup.");
            return new ChangedExtents(false, null, extents);
        }
        catch (XapiException ex)
        {
            // The storage manager's own words (e.g. "[opterr=Source and target VDI are unrelated]") and the state of the
            // CBT chain: what support needs to tell a broken chain from CBT that is off or a disk format without CBT.
            var detail = Detail(ex);
            log.Warning($"Disk {disk.Name}: XAPI VDI.list_changed_blocks failed: {ex.Code}{(detail.Length > 0 ? ": " + detail : string.Empty)}. " +
                await DescribeCbtAsync(baseRef, state, cancellationToken).ConfigureAwait(false));
            return ChangedExtents.Full($"changed blocks could not be determined ({ex.Code}{(detail.Length > 0 ? ": " + detail : string.Empty)})");
        }
        catch (FormatException ex)
        {
            return ChangedExtents.Full($"invalid changed block bitmap ({ex.Message})");
        }
    }

    public async Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken)
    {
        var state = _disks[disk.Key];
        _log.Info($"Disk {disk.Name}: selecting the transport ({ModeText(options.Transport)}).");
        if (options.Transport == DiskTransportMode.HttpExportOnly)
        {
            return await XenHttpExport.OpenAsync(_owner, _xapi, _log, disk, state.SnapshotVdiRef, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await OpenNbdAsync(disk, state, options, cancellationToken).ConfigureAwait(false);
        }
        catch (XenNbdUnavailableException ex) when (options.Transport == DiskTransportMode.Automatic)
        {
            _log.Warning(ex.Message);
            _log.Warning(
                $"Disk {disk.Name}: falling back to the HTTP export (export_raw_vdi over HTTPS). It uses one stream per disk and transfers the whole " +
                "allocated disk on every backup, also for incrementals. Set up NBD for faster backups.");
            return await XenHttpExport.OpenAsync(_owner, _xapi, _log, disk, state.SnapshotVdiRef, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ModeText(DiskTransportMode mode) => mode switch
    {
        DiskTransportMode.NbdOnly => "mode NBD only",
        DiskTransportMode.HttpExportOnly => "mode HTTP export only",
        _ => "mode automatic: NBD, otherwise HTTP export",
    };

    private async Task<IDiskSource> OpenNbdAsync(SnapshotDisk disk, DiskState state, DiskReaderOptions options, CancellationToken cancellationToken)
    {
        var overview = await GetNbdOverviewAsync(cancellationToken).ConfigureAwait(false);
        var infos = await _xapi.CallAsync("VDI.get_nbd_info", cancellationToken, state.SnapshotVdiRef).ConfigureAwait(false) as JsonArray ?? [];
        var candidates = infos.OfType<JsonObject>().ToList();
        _log.Info(
            $"Disk {disk.Name}: XAPI VDI.get_nbd_info (snapshot disk {disk.SnapshotDiskId}) returned {candidates.Count} endpoint(s)" +
            (candidates.Count == 0 ? "." : $": {string.Join(", ", candidates.Select(c => DescribeEndpoint(c, overview.InterfaceNames)))}."));
        if (candidates.Count == 0)
        {
            throw new XenNbdUnavailableException(DescribeMissingNbd(disk, overview));
        }

        var secure = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Str("cert"))).ToList();
        if (secure.Count == 0 && !_owner.AllowInsecureNbd)
        {
            throw new XenNbdUnavailableException(
                $"Disk {disk.Name}: the pool offers only unencrypted NBD (purpose insecure_nbd): {string.Join(", ", candidates.Select(c => DescribeEndpoint(c, overview.InterfaceNames)))}. " +
                "Allow unencrypted NBD for this pool or set the purpose nbd (TLS) on the network.");
        }

        // Start at a random endpoint to spread disks across hosts, but try every endpoint before giving up.
        var usable = secure.Count > 0 ? secure : candidates;
        var start = Random.Shared.Next(usable.Count);
        var ordered = usable.Skip(start).Concat(usable.Take(start)).ToList();

        var connections = Math.Clamp(options.Connections, 1, 16);
        var failures = new List<string>();
        foreach (var info in ordered)
        {
            var endpoint = DescribeEndpoint(info, overview.InterfaceNames);
            var connect = CreateConnectOptions(info, options);
            _log.Info($"Disk {disk.Name}: NBD: connecting to {endpoint} …");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            NbdConnectionPool pool;
            try
            {
                pool = await NbdConnectionPool.CreateAsync(ct => NbdClient.ConnectAsync(connect, ct), connections, options.MaxInFlightPerConnection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or SocketException or AuthenticationException && !cancellationToken.IsCancellationRequested)
            {
                var reason = ConnectFailureReason(ex);
                failures.Add($"{endpoint}: {reason}");
                _log.Warning($"Disk {disk.Name}: NBD: connection to {endpoint} failed after {watch.ElapsedMilliseconds} ms: {reason}");
                continue;
            }

            if (pool.Size != disk.CapacityBytes)
            {
                await pool.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"Disk {disk.Name}: NBD export size {pool.Size} at {endpoint} does not match the disk size {disk.CapacityBytes}.");
            }

            _log.Info(
                $"Disk {disk.Name}: NBD: connected to {endpoint} in {watch.ElapsedMilliseconds} ms " +
                $"({(pool.UsesTls ? $"TLS {TlsVersion(pool.TlsProtocol)}" : "unencrypted")}, {(pool.NegotiatedWithGo ? "NBD_OPT_GO" : "NBD_OPT_EXPORT_NAME")}, " +
                $"export size {XenFormat.Bytes(pool.Size)}, max request {XenFormat.Bytes(pool.MaxPayload)}, {pool.OpenConnections} of {connections} connection(s) open). Reading via NBD.");
            return new NbdDiskReader(pool, connections * options.MaxInFlightPerConnection);
        }

        throw new XenNbdUnavailableException(
            $"Disk {disk.Name}: no NBD endpoint offered by the pool is usable from this backup server ({Environment.MachineName}). Tried: {string.Join("; ", failures)}. " +
            "Check that the backup server reaches these addresses on the listed TCP ports, that the service xapi-nbd runs on the hosts (systemctl status xapi-nbd) " +
            "and that no firewall between them blocks the port.");
    }

    private static string TlsVersion(SslProtocols protocol) => protocol switch
    {
        SslProtocols.Tls13 => "1.3",
        SslProtocols.Tls12 => "1.2",
        _ => protocol.ToString(),
    };

    private static NbdConnectOptions CreateConnectOptions(JsonObject info, DiskReaderOptions options)
    {
        var pem = info.Str("cert");
        return new NbdConnectOptions
        {
            Host = info.Str("address"),
            Port = NbdPort(info),
            ExportName = info.Str("exportname"),
            TlsMode = string.IsNullOrWhiteSpace(pem) ? NbdTlsMode.Disabled : NbdTlsMode.Required,
            TlsTargetHost = string.IsNullOrWhiteSpace(info.Str("subject")) ? info.Str("address") : info.Str("subject"),
            ServerCertificateValidation = string.IsNullOrWhiteSpace(pem)
                ? null
                : CertificatePinning.CreateValidator(TlsTrustMode.Pinned, [CertificatePinning.SpkiSha256FromPem(pem)]),
            MaxInFlight = options.MaxInFlightPerConnection,
        };
    }

    private static int NbdPort(JsonObject info) => info.Long("port") is > 0 and var port ? (int)port : NbdProtocol.DefaultPort;

    /// <summary>"10.0.0.5:10809 (host xcp-ng1, network Storage101, TLS)".</summary>
    private static string DescribeEndpoint(JsonObject info, IReadOnlyDictionary<string, string> interfaceNames)
    {
        var address = info.Str("address");
        var endpoint = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]:{NbdPort(info)}" : $"{address}:{NbdPort(info)}";
        var tls = string.IsNullOrWhiteSpace(info.Str("cert")) ? "unencrypted" : "TLS";
        return interfaceNames.TryGetValue(address, out var where) ? $"{endpoint} ({where}, {tls})" : $"{endpoint} ({tls})";
    }

    private static string ConnectFailureReason(Exception ex) => ex switch
    {
        NbdException { InnerException: SocketException socket } => $"{socket.Message} ({socket.SocketErrorCode})",
        SocketException socket => $"{socket.Message} ({socket.SocketErrorCode})",
        AuthenticationException => $"TLS handshake failed, the server certificate does not match the one reported by XAPI ({ex.Message})",
        _ => ex.Message,
    };

    /// <summary>
    /// Reads networks, host interfaces and hosts once per session and logs which networks carry NBD and which
    /// addresses the hosts have on them. Also maps interface addresses to "host X, network Y" for messages.
    /// </summary>
    private async Task<NbdOverview> GetNbdOverviewAsync(CancellationToken cancellationToken)
    {
        if (_nbdOverview is not null)
        {
            return _nbdOverview;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var networks = await _xapi.GetAllRecordsAsync("network", cancellationToken).ConfigureAwait(false);
            var pifs = await _xapi.GetAllRecordsAsync("PIF", cancellationToken).ConfigureAwait(false);
            var hosts = await _xapi.GetAllRecordsAsync("host", cancellationToken).ConfigureAwait(false);
            foreach (var pif in pifs.Values)
            {
                var where = $"host {NameOf(hosts, pif.Str("host"))}, network {NameOf(networks, pif.Str("network"))}";
                if (pif.Str("IP") is { Length: > 0 } ip)
                {
                    names.TryAdd(ip, where);
                }

                foreach (var ipv6 in pif.Set("IPv6"))
                {
                    names.TryAdd(ipv6.Split('/')[0], where);
                }
            }

            var nbdNetworks = networks
                .Where(n => n.Value.Set("purpose").Any(p => p is "nbd" or "insecure_nbd"))
                .Select(n =>
                {
                    var interfaces = pifs.Values
                        .Where(p => p.Str("network") == n.Key)
                        .Select(p => $"{NameOf(hosts, p.Str("host"))}: {(p.Str("IP") is { Length: > 0 } ip ? ip : "no IP address")}")
                        .ToList();
                    return $"{n.Value.Str("name_label")} [{string.Join(", ", n.Value.Set("purpose"))}] ({(interfaces.Count == 0 ? "no host interface" : string.Join(", ", interfaces))})";
                })
                .ToList();
            _log.Info(
                $"NBD check: XAPI network/PIF/host.get_all_records returned {networks.Count} network(s), {pifs.Count} host interface(s), {hosts.Count} host(s). " +
                $"Networks with NBD purpose: {(nbdNetworks.Count == 0 ? "none" : string.Join("; ", nbdNetworks))}.");
            return _nbdOverview = new NbdOverview(names, nbdNetworks, true);
        }
        catch (Exception ex) when (ex is XapiException or HttpRequestException or IOException && !cancellationToken.IsCancellationRequested)
        {
            _log.Warning($"NBD check: the networks of the pool could not be read ({ex.Message}); endpoints are listed by address only.");
            return _nbdOverview = new NbdOverview(names, [], false);
        }
    }

    private static string DescribeMissingNbd(SnapshotDisk disk, NbdOverview overview)
    {
        const string Command = "xe network-param-add param-name=purpose param-key=nbd uuid=<network-uuid>";
        if (!overview.NetworksKnown)
        {
            return $"Disk {disk.Name}: no NBD-enabled network reaches storage '{disk.StorageName}'. Set the NBD purpose on a network: {Command}";
        }

        if (overview.NbdNetworks.Count == 0)
        {
            return $"Disk {disk.Name}: no network of the pool has the NBD purpose. Set it on a network that the backup server can reach " +
                   $"and on which the hosts have an IP address: {Command}";
        }

        return $"Disk {disk.Name}: the pool offers no NBD endpoint for storage '{disk.StorageName}'. Networks with NBD purpose: {string.Join("; ", overview.NbdNetworks)}. " +
               "XAPI only offers hosts that have an IP address on such a network and can access the storage.";
    }

    private static string NameOf(Dictionary<string, JsonObject> records, string reference) =>
        records.TryGetValue(reference, out var record) && record.Str("name_label") is { Length: > 0 } name ? name : "?";

    private sealed record NbdOverview(IReadOnlyDictionary<string, string> InterfaceNames, IReadOnlyList<string> NbdNetworks, bool NetworksKnown);

    public ChangeToken? GetNewChangeToken(SnapshotDisk disk) =>
        _disks.TryGetValue(disk.Key, out var state) && state.CbtUsable ? new ChangeToken(XenTags.ChangeTokenKind, state.Disk.SnapshotDiskId) : null;

    public async Task CommitAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        foreach (var state in _disks.Values)
        {
            await _ledger.UpdateAsync(state.Artifact, ArtifactState.CommitPending, null, cancellationToken).ConfigureAwait(false);
        }

        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.CommitPending, null, cancellationToken).ConfigureAwait(false);

        // The snapshot VM goes first: VDI.data_destroy refuses a disk that still has a VBD, also the unattached VBD of
        // the snapshot VM (VDI_IN_USE). VM.destroy removes the VM and its VBDs but keeps the disks.
        await _xapi.CallAsync("VM.destroy", cancellationToken, _snapshotRef).ConfigureAwait(false);
        await _ledger.UpdateAsync(_snapshotArtifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);

        foreach (var state in _disks.Values)
        {
            try
            {
                if (state.CbtUsable)
                {
                    await XenHypervisorSession.DataDestroyAsync(_xapi, state.SnapshotVdiRef, log, cancellationToken, _owner.Provider.Options.SnapshotReleaseTimeout).ConfigureAwait(false);
                }
                else
                {
                    await _xapi.CallAsync("VDI.destroy", cancellationToken, state.SnapshotVdiRef).ConfigureAwait(false);
                }
            }
            catch (XenSnapshotInUseException ex)
            {
                // Keep the snapshot: the ledger rows stay CommitPending and the background retry finishes the job.
                _commitDeferred = true;
                throw new SnapshotCleanupDeferredException(
                    $"Disk {state.Disk.Name}: {ex.Message}. The restore point is complete. The snapshot disk of '{Vm.Name}' is kept for now and reduced to " +
                    "change tracking data in the background (retried every 10 minutes and at service start).",
                    ex);
            }

            await _ledger.UpdateAsync(state.Artifact, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        }

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
        if (!_committed && !_commitDeferred)
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

    /// <summary>The non-empty parameters of an XAPI error (for SR_BACKEND_FAILURE_*: the storage manager's message).</summary>
    private static string Detail(XapiException ex) =>
        string.Join("; ", ex.Parameters.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    /// <summary>Type, CBT flag and time of the previous backup's tracking data, the new snapshot and the disk (best effort).</summary>
    private async Task<string> DescribeCbtAsync(string baseRef, DiskState state, CancellationToken cancellationToken)
    {
        async Task<string> Describe(string label, string reference)
        {
            try
            {
                var vdi = await _xapi.GetRecordAsync("VDI", reference, cancellationToken).ConfigureAwait(false);
                var smConfig = vdi.Map("sm_config");
                var format = smConfig.TryGetValue("vdi_type", out var type) ? type : smConfig.TryGetValue("image-format", out var image) ? image : "unknown";
                var time = vdi.Bool("is_a_snapshot") ? $", taken {vdi.Str("snapshot_time")}" : string.Empty;
                return $"{label} {vdi.Str("uuid")} (type {vdi.Str("type")}, format {format}, CBT {(vdi.Bool("cbt_enabled") ? "on" : "off")}{time})";
            }
            catch (XapiException ex)
            {
                return $"{label}: {ex.Code}";
            }
        }

        var parts = new List<string>
        {
            await Describe("previous backup's tracking data", baseRef).ConfigureAwait(false),
            await Describe("this backup's snapshot", state.SnapshotVdiRef).ConfigureAwait(false),
        };
        if (!XapiRecord.IsNullRef(state.SourceVdiRef))
        {
            parts.Add(await Describe("the disk", state.SourceVdiRef).ConfigureAwait(false));
        }

        return "CBT state: " + string.Join("; ", parts) + ".";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0 GiB", CultureInfo.InvariantCulture)
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("0.0 MiB", CultureInfo.InvariantCulture)
        : (bytes / 1024).ToString("0 KiB", CultureInfo.InvariantCulture);

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

    public string TransportName => "nbd";

    public int MaxRequestBytes => pool.MaxPayload;

    public int MaxConcurrentRequests => Math.Max(1, maxConcurrentRequests);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
        pool.ReadAsync(offset, destination, cancellationToken);

    public ValueTask DisposeAsync() => pool.DisposeAsync();
}
