using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Testing;
using Backdafuckup.Testing.Xapi;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen.Tests;

[Trait("Category", "Unit")]
public sealed class XenProviderTests : IAsyncDisposable
{
    private const long DiskSize = 8L * 1024 * 1024;
    private readonly FakeXenPool _pool = new();
    private readonly InMemoryArtifactLedger _ledger = new();
    private readonly List<string> _log = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _pool.DisposeAsync();

    private XenProvider Provider(
        CbtBitOrder? bitOrder = CbtBitOrder.MostSignificantBitFirst,
        XenHttpExportFormat exportFormat = XenHttpExportFormat.Vhd,
        XenHttpImportFormat importFormat = XenHttpImportFormat.Vhd) => new(
        new XenOptions
        {
            CbtBitOrder = bitOrder,
            XapiRetryDelay = TimeSpan.FromMilliseconds(10),
            XapiRetryAttempts = 1,
            HttpExportFormat = exportFormat,
            HttpImportFormat = importFormat,
            SnapshotReleaseTimeout = TimeSpan.FromMilliseconds(300),
        },
        (_, host) => new XapiTransport(new Uri($"https://{host}/"), _pool.CreateHandler()));

    private static ConnectionSettings Settings(string password = "secret") => new(
        Guid.NewGuid(), HypervisorKind.XenServer, "xcp1", 443, new NetworkCredential("root", password),
        TlsTrustMode.Pinned, [], new Dictionary<string, string>());

    private IOperationLog Log => new ListLog(_log);

    [Fact]
    public async Task Test_connection_reports_platform_or_authentication_errors()
    {
        var ok = await Provider().TestConnectionAsync(Settings(), Ct);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal("lab-pool", ok.Platform!.PlatformName);
        Assert.Equal("XCP-ng", ok.Platform.ProductName);

        var bad = await Provider().TestConnectionAsync(Settings("wrong"), Ct);
        Assert.False(bad.Success);
        Assert.Contains("Authentication failed", bad.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_lists_vms_storage_and_nbd_networks()
    {
        var (vmUuid, _) = _pool.AddVm("web01", DiskSize, DiskSize / 2);
        await using var session = await Provider().OpenAsync(Settings(), Ct);

        var inventory = await session.GetInventoryAsync(Ct);

        var vm = Assert.Single(inventory.Items, i => i.Kind == InventoryKind.Vm);
        Assert.Equal(vmUuid, vm.NativeId);
        Assert.Equal("2", vm.Properties["diskCount"]);
        Assert.Contains(inventory.Items, i => i.Kind == InventoryKind.Network && i.Properties["nbd"] == "tls");
        Assert.Equal(2, inventory.Items.Count(i => i.Kind == InventoryKind.Storage));
    }

    [Fact]
    public async Task Full_backup_reads_snapshot_over_nbd_and_commit_keeps_only_cbt_metadata()
    {
        var (vmUuid, disks) = _pool.AddVm("db01", DiskSize, DiskSize);
        var expected = disks.Select(_pool.DiskContent).ToList();
        await using var session = await Provider().OpenAsync(Settings(), Ct);

        IReadOnlyList<ChangeToken?> tokens;
        await using (var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct))
        {
            Assert.Equal(2, backup.Disks.Count);
            Assert.NotEmpty(backup.Metadata.Content);
            Assert.Equal("db01", backup.Vm.Name);
            Assert.Single(backup.Vm.Nics);

            // Writes after the snapshot must not show up in the backup.
            _pool.WriteDisk(disks[0], 0, RandomNumberGenerator.GetBytes(4096));

            foreach (var (disk, i) in backup.Disks.Select((d, i) => (d, i)))
            {
                var changes = await backup.GetChangedExtentsAsync(disk, null, Log, Ct);
                Assert.True(changes.IsFull);
                Assert.Equal(expected[i], await ReadAllAsync(backup, disk));
            }

            tokens = backup.Disks.Select(backup.GetNewChangeToken).ToList();
            await backup.CommitAsync(Log, Ct);
        }

        Assert.All(tokens, t => Assert.NotNull(t));
        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        Assert.All(tokens, t => Assert.Equal("cbt_metadata", _pool.Records("VDI").Single(v => v.Str("uuid") == t!.Value).Str("type")));
        Assert.All(_pool.Records("VDI").Where(v => v.Str("snapshot_of") == "OpaqueRef:NULL"), v => Assert.True(v.Bool("cbt_enabled")));
        Assert.All(_ledger.Items, a => Assert.Equal(ArtifactState.Done, a.State));
    }

    [Fact]
    public async Task Incremental_backup_reports_exactly_the_written_blocks()
    {
        var (vmUuid, disks) = _pool.AddVm("app01", DiskSize);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        var token = await FullBackupAsync(session, vmUuid);

        _pool.WriteDisk(disks[0], 3 * CbtBitmap.BlockSize, RandomNumberGenerator.GetBytes(CbtBitmap.BlockSize * 2));
        _pool.WriteDisk(disks[0], DiskSize - 10, RandomNumberGenerator.GetBytes(10));

        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var changes = await backup.GetChangedExtentsAsync(backup.Disks[0], token, Log, Ct);

        Assert.False(changes.IsFull, changes.FullReason);
        Assert.Equal(
            [new DiskExtent(3 * CbtBitmap.BlockSize, 2 * CbtBitmap.BlockSize), new DiskExtent(DiskSize - CbtBitmap.BlockSize, CbtBitmap.BlockSize)],
            changes.Extents);
    }

    [Fact]
    public async Task Incrementals_stay_disabled_until_the_bit_order_is_configured()
    {
        var (vmUuid, _) = _pool.AddVm("app02", DiskSize);
        await using var session = await Provider(bitOrder: null).OpenAsync(Settings(), Ct);
        var token = await FullBackupAsync(session, vmUuid);

        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var changes = await backup.GetChangedExtentsAsync(backup.Disks[0], token, Log, Ct);

        Assert.True(changes.IsFull);
        Assert.Contains("Xen:CbtBitOrder", changes.FullReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_full_when_cbt_was_reset_or_metadata_is_gone()
    {
        var (vmUuid, disks) = _pool.AddVm("app03", DiskSize, DiskSize);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        var token = await FullBackupAsync(session, vmUuid);
        _pool.DisableCbt(disks[0]);

        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var reset = await backup.GetChangedExtentsAsync(backup.Disks[0], token, Log, Ct);
        var missing = await backup.GetChangedExtentsAsync(backup.Disks[1], new ChangeToken(XenTags.ChangeTokenKind, Guid.NewGuid().ToString()), Log, Ct);

        Assert.True(reset.IsFull);
        Assert.Contains("re-)enabled", reset.FullReason, StringComparison.Ordinal);
        Assert.True(missing.IsFull);
        Assert.Contains("no longer exists", missing.FullReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_waits_while_xapi_holds_the_snapshot_disk_and_logs_who_uses_it()
    {
        var (vmUuid, _) = _pool.AddVm("busy01", DiskSize);
        _pool.DataDestroyInUse = 2;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        await backup.CommitAsync(Log, Ct);

        Assert.Contains(_log, l => l.Contains("still in use (VDI_IN_USE)", StringComparison.Ordinal) && l.Contains("no running operations; no VBDs", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("released by XAPI after", StringComparison.Ordinal));
        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        Assert.Contains(_pool.Records("VDI"), v => v.Str("type") == "cbt_metadata");
    }

    [Fact]
    public async Task A_snapshot_disk_held_too_long_is_kept_for_the_background_retry()
    {
        var (vmUuid, _) = _pool.AddVm("busy02", DiskSize);
        _pool.DataDestroyInUse = int.MaxValue;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using (var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct))
        {
            var ex = await Assert.ThrowsAsync<SnapshotCleanupDeferredException>(() => backup.CommitAsync(Log, Ct));
            Assert.Contains("still in use after", ex.Message, StringComparison.Ordinal);
            Assert.Contains("kept for now", ex.Message, StringComparison.Ordinal);
        }

        // The snapshot VM is gone (removed first, so that it does not block data_destroy). Disposing must not delete
        // the snapshot disk: it becomes the change tracking base once XAPI releases it.
        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        Assert.Contains(_pool.Records("VDI"), v => v.Bool("is_a_snapshot") && v.Str("type") == "user");
        var pending = Assert.Single(_ledger.Items, a => a.State == ArtifactState.CommitPending);
        Assert.Equal(XenArtifactKinds.SnapshotVdi, pending.Kind);

        // Later, the background retry finishes the commit.
        _pool.DataDestroyInUse = 0;
        await session.CommitArtifactAsync(pending, Log, Ct);

        Assert.Contains(_pool.Records("VDI"), v => v.Str("type") == "cbt_metadata");
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Bool("is_a_snapshot") && v.Str("type") == "user");
    }

    [Fact]
    public async Task A_snapshot_disk_still_referenced_by_its_snapshot_vm_is_detached_before_data_destroy()
    {
        // Ledger state left by older versions: the snapshot VM still exists when the snapshot disk is committed.
        // XAPI answers VDI_IN_USE for as long as the snapshot VM's (unattached) VBD references the disk.
        var (vmUuid, _) = _pool.AddVm("old01", DiskSize);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var snapshotVdi = Assert.Single(_ledger.Items, a => a.Kind == XenArtifactKinds.SnapshotVdi);
        var snapshotVm = Assert.Single(_ledger.Items, a => a.Kind == XenArtifactKinds.VmSnapshot);

        await session.CommitArtifactAsync(snapshotVdi, Log, Ct);
        Assert.Contains(_log, l => l.Contains("detached from snapshot 'backdafuckup test'", StringComparison.Ordinal));
        Assert.Contains(_pool.Records("VDI"), v => v.Str("uuid") == snapshotVdi.NativeId && v.Str("type") == "cbt_metadata");
        Assert.DoesNotContain(_log, l => l.Contains("VDI_IN_USE", StringComparison.Ordinal));

        await session.CommitArtifactAsync(snapshotVm, Log, Ct);
        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
    }

    [Fact]
    public async Task The_guest_is_frozen_exactly_around_the_vm_snapshot()
    {
        var (vmUuid, _) = _pool.AddVm("sql01", DiskSize);
        var quiescer = new RecordingQuiescer();
        _pool.OnSnapshot = _ => quiescer.Events.Add(quiescer.Frozen ? "snapshot while frozen" : "snapshot while NOT frozen");
        await using var session = await Provider().OpenAsync(Settings(), Ct);

        await using var backup = await session.BeginBackupAsync(Request(vmUuid) with { Quiescer = quiescer }, Log, _ledger, Ct);

        Assert.Equal(["freeze", "snapshot while frozen", "thaw", "dispose"], quiescer.Events);
        var result = Assert.IsType<GuestFreezeResult>(backup.GuestFreeze);
        Assert.True(result.ApplicationConsistent);
        Assert.Contains(_log, l => l.Contains("Guest processing: application-consistent snapshot", StringComparison.Ordinal) && l.Contains("SqlServerWriter (Stable)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_guest_that_cannot_be_frozen_still_gets_a_crash_consistent_snapshot_with_the_reason()
    {
        var (vmUuid, _) = _pool.AddVm("sql02", DiskSize);
        var quiescer = new RecordingQuiescer { FailFreeze = "freezing the guest failed in the agent on sql02 (10.0.0.5): VSS_E_WRITERERROR_TIMEOUT." };
        await using var session = await Provider().OpenAsync(Settings(), Ct);

        await using var backup = await session.BeginBackupAsync(Request(vmUuid) with { Quiescer = quiescer }, Log, _ledger, Ct);

        Assert.Contains(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        var result = Assert.IsType<GuestFreezeResult>(backup.GuestFreeze);
        Assert.False(result.ApplicationConsistent);
        Assert.Contains("10.0.0.5", result.Reason, StringComparison.Ordinal);
        Assert.Contains(_log, l => l.Contains("only crash-consistent", StringComparison.Ordinal));
    }

    private sealed class RecordingQuiescer : IGuestQuiescer
    {
        public List<string> Events { get; } = [];

        public bool Frozen { get; private set; }

        public string? FailFreeze { get; init; }

        public Task<IGuestFreeze> FreezeAsync(IOperationLog log, CancellationToken cancellationToken)
        {
            if (FailFreeze is not null)
            {
                throw new GuestQuiesceException(FailFreeze);
            }

            Events.Add("freeze");
            Frozen = true;
            return Task.FromResult<IGuestFreeze>(new FreezeHandle(this));
        }

        private sealed class FreezeHandle(RecordingQuiescer owner) : IGuestFreeze
        {
            public Task<GuestFreezeResult> ThawAsync(CancellationToken cancellationToken)
            {
                owner.Events.Add("thaw");
                owner.Frozen = false;
                return Task.FromResult(new GuestFreezeResult(true, null, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2), [new GuestWriterResult("SqlServerWriter", "Stable", null)]));
            }

            public ValueTask DisposeAsync()
            {
                owner.Events.Add("dispose");
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task Disposing_without_commit_removes_the_snapshot()
    {
        var (vmUuid, _) = _pool.AddVm("tmp01", DiskSize);
        var vdisBefore = _pool.Records("VDI").Count;
        await using var session = await Provider().OpenAsync(Settings(), Ct);

        await using (var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct))
        {
            Assert.Contains(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        }

        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        Assert.Equal(vdisBefore, _pool.Records("VDI").Count);
        Assert.All(_ledger.Items, a => Assert.Equal(ArtifactState.Done, a.State));
    }

    [Fact]
    public async Task Reports_a_clear_error_when_no_nbd_network_is_available()
    {
        var (vmUuid, _) = _pool.AddVm("nonbd", DiskSize);
        _pool.NbdEnabled = false;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        var ex = await Assert.ThrowsAsync<XenNbdUnavailableException>(() => backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Transport: DiskTransportMode.NbdOnly), Ct));
        Assert.Contains("NBD", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Pool-wide network", ex.Message, StringComparison.Ordinal);
        Assert.Contains("xcp-host-1: 127.0.0.1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_next_nbd_endpoint_and_names_the_failed_address()
    {
        var closedPort = ClosedPort();
        var (vmUuid, _) = _pool.AddVm("failover", DiskSize);
        _pool.UnreachableNbdEndpoints.Add(("127.0.0.1", closedPort));
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        // The first endpoint is chosen at random; open until the unreachable one was tried first (every open must still succeed).
        bool FailedOver() => _log.Any(l => l.StartsWith("Warning:", StringComparison.Ordinal) && l.Contains($"127.0.0.1:{closedPort} (host xcp-host-1, network Pool-wide network, TLS)", StringComparison.Ordinal));
        for (var i = 0; i < 64 && !FailedOver(); i++)
        {
            await using var reader = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Connections: 1, Transport: DiskTransportMode.NbdOnly), Ct);
        }

        Assert.True(FailedOver());
        Assert.Contains(_log, l => l.Contains("NBD: connected to 127.0.0.1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lists_every_tried_nbd_endpoint_when_none_is_reachable()
    {
        var closedPort = ClosedPort();
        var (vmUuid, _) = _pool.AddVm("unreachable", DiskSize);
        _pool.UnreachableNbdEndpoints.Add(("127.0.0.1", closedPort));
        _pool.NbdReachable = false;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        var ex = await Assert.ThrowsAsync<XenNbdUnavailableException>(() => backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Connections: 1, Transport: DiskTransportMode.NbdOnly), Ct));
        Assert.Contains($"127.0.0.1:{closedPort} (host xcp-host-1, network Pool-wide network, TLS)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ConnectionRefused", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(XenHttpExportFormat.Vhd, false)]
    [InlineData(XenHttpExportFormat.Vhd, true)]
    [InlineData(XenHttpExportFormat.Raw, false)]
    public async Task Http_export_streams_the_snapshot_content(XenHttpExportFormat format, bool sparseBitmaps)
    {
        // 9 MiB + 1 sector: the last VHD block is partial; zero regions stay unallocated in the VHD stream.
        var (vmUuid, disks) = _pool.AddVm("export", (9L * 1024 * 1024) + 512);
        _pool.WriteDisk(disks[0], 4L * 1024 * 1024, new byte[2 * 1024 * 1024]);
        _pool.VhdExport = new VhdTestOptions { SparseBitmaps = sparseBitmaps };
        var expected = _pool.DiskContent(disks[0]);
        await using var session = await Provider(exportFormat: format).OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        await using var source = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Transport: DiskTransportMode.HttpExportOnly), Ct);
        Assert.Equal(format == XenHttpExportFormat.Vhd ? "http-export-vhd" : "http-export-raw", source.TransportName);
        Assert.Equal(expected, await StreamAllAsync(source));
        Assert.Equal([format == XenHttpExportFormat.Vhd ? "vhd" : "raw"], _pool.ExportRequests);
        Assert.Contains(_log, l => l.Contains($"HTTP export: GET https://xcp1/export_raw_vdi?vdi={backup.Disks[0].SnapshotDiskId}&format=", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("HTTP export from xcp1 finished", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains("session_id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automatic_mode_falls_back_to_the_http_export_and_logs_the_transport_check()
    {
        var (vmUuid, disks) = _pool.AddVm("fallback", DiskSize);
        _pool.NbdEnabled = false;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        await using var source = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(), Ct);

        Assert.Equal(_pool.DiskContent(disks[0]), await StreamAllAsync(source));
        Assert.Contains(_log, l => l.Contains("selecting the transport (mode automatic", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("Networks with NBD purpose: Pool-wide network [nbd] (xcp-host-1: 127.0.0.1)", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("VDI.get_nbd_info", StringComparison.Ordinal) && l.Contains("returned 0 endpoint(s)", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.StartsWith("Warning:", StringComparison.Ordinal) && l.Contains("falling back to the HTTP export", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("storage 'Local storage' is local to host xcp-host-1 (xcp1)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nbd_is_preferred_in_automatic_mode_and_the_connection_is_logged()
    {
        var (vmUuid, _) = _pool.AddVm("prefer-nbd", DiskSize);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);

        await using var source = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Connections: 2), Ct);

        Assert.Equal("nbd", source.TransportName);
        Assert.Empty(_pool.ExportRequests);
        Assert.Contains(_log, l => l.Contains("NBD: connected to 127.0.0.1:", StringComparison.Ordinal) && l.Contains("(host xcp-host-1, network Pool-wide network, TLS) in", StringComparison.Ordinal) && l.Contains("TLS 1.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_http_export_fails_with_the_endpoint_and_task_state()
    {
        var (vmUuid, _) = _pool.AddVm("truncated", DiskSize);
        _pool.TruncateExportAfterBytes = 3 * 1024 * 1024;
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        await using var source = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Transport: DiskTransportMode.HttpExportOnly), Ct);

        var ex = await Assert.ThrowsAsync<XenHttpExportException>(() => StreamAllAsync(source));
        Assert.Contains("HTTP export from xcp1 failed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", ex.Message, StringComparison.Ordinal);
        Assert.Contains("XAPI task status: pending", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vhd_stream_with_data_in_unused_sectors_is_rejected()
    {
        var (vmUuid, _) = _pool.AddVm("badbitmap", DiskSize);
        _pool.VhdExport = new VhdTestOptions { CorruptBitmap = true };
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        await using var source = await backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(Transport: DiskTransportMode.HttpExportOnly), Ct);

        var ex = await Assert.ThrowsAsync<XenHttpExportException>(() => StreamAllAsync(source));
        Assert.Contains("marked unused in the sector bitmap", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Xen:HttpExportFormat", ex.Message, StringComparison.Ordinal);
    }

    private static int ClosedPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Restores_to_an_alternate_location_with_new_name_network_and_macs()
    {
        var (vmUuid, disks) = _pool.AddVm("web02", DiskSize);
        var original = _pool.DiskContent(disks[0]);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        var (manifest, metadata) = await CaptureAsync(session, vmUuid);

        var targetSr = _pool.Uuid("SR", _pool.SecondSrRef);
        var targetNet = _pool.Uuid("network", _pool.SecondNetworkRef);
        var plan = new VmRestorePlan
        {
            Mode = RestoreMode.AlternateLocation,
            Manifest = manifest,
            Metadata = metadata,
            VmName = "web02-restored",
            DiskStorage = new Dictionary<string, string> { ["0"] = targetSr },
            NetworkMap = new Dictionary<string, string> { ["0"] = targetNet },
            RegenerateMacs = true,
            PowerOn = true,
            SessionId = Guid.NewGuid(),
        };

        await using (var restore = await session.BeginRestoreAsync(plan, Log, _ledger, Ct))
        {
            Assert.True((await restore.ValidateAsync(Ct)).Ok);
            await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget("0", disks[0], "disk", DiskSize, targetSr), Ct);
            await writer.WriteAsync(new ByteImage(original), null, Ct);
            await restore.CreateVmAsync(new Dictionary<string, string> { [disks[0]] = writer.DiskId }, Ct);
            var restored = await restore.CompleteAsync(Ct);
            Assert.Equal("web02-restored", restored.Name);
        }

        var vm = _pool.Records("VM").Single(v => v.Str("name_label") == "web02-restored");
        Assert.NotEqual(vmUuid, vm.Str("uuid"));
        Assert.Equal("Running", vm.Str("power_state"));
        var vif = _pool.Records("VIF").Single(v => v.Str("network") == _pool.SecondNetworkRef);
        Assert.NotEqual("aa:bb:cc:00:00:01", vif.Str("MAC"));
        var restoredVdi = _pool.Records("VDI").Single(v => v.Str("SR") == _pool.SecondSrRef);
        Assert.Equal(original, _pool.DiskContent(restoredVdi.Str("uuid")));
        Assert.All(_ledger.Items, a => Assert.Equal(ArtifactState.Done, a.State));
    }

    [Theory]
    [InlineData(XenHttpImportFormat.Vhd, "vhd")]
    [InlineData(XenHttpImportFormat.Raw, "raw")]
    public async Task Restore_uploads_only_blocks_with_data_as_vhd_or_everything_as_raw(XenHttpImportFormat format, string expectedFormat)
    {
        var (vmUuid, disks) = _pool.AddVm("sparse01", DiskSize);
        await using var session = await Provider(importFormat: format).OpenAsync(Settings(), Ct);
        var (manifest, metadata) = await CaptureAsync(session, vmUuid);

        // 8 MiB disk with data only in the second 2 MiB block.
        var content = new byte[DiskSize];
        new Random(7).NextBytes(content.AsSpan(3 * 1024 * 1024, 512 * 1024));
        var plan = new VmRestorePlan
        {
            Mode = RestoreMode.AlternateLocation, Manifest = manifest, Metadata = metadata, VmName = "sparse01-restored",
            DiskStorage = new Dictionary<string, string> { ["0"] = _pool.Uuid("SR", _pool.SecondSrRef) }, RegenerateMacs = true, SessionId = Guid.NewGuid(),
        };

        await using (var restore = await session.BeginRestoreAsync(plan, Log, _ledger, Ct))
        {
            await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget("0", disks[0], "disk", DiskSize, plan.DiskStorage["0"]), Ct);
            await writer.WriteAsync(new ByteImage(content), null, Ct);
            var upload = Assert.Single(_pool.ImportRequests);
            Assert.Equal(expectedFormat, upload.Format);
            Assert.Equal(upload.Bytes, writer.TransferredBytes);
            if (format == XenHttpImportFormat.Vhd)
            {
                Assert.True(upload.Bytes < 3L * 1024 * 1024, $"VHD upload of one data block was {upload.Bytes} bytes.");
                Assert.Contains(_log, l => l.Contains("as VHD, 1 of 4 blocks (2 MiB) contain data", StringComparison.Ordinal) && l.Contains("import_raw_vdi to xcp1", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(DiskSize, upload.Bytes);
            }

            Assert.Equal(content, _pool.DiskContent(writer.DiskId));
        }
    }

    [Fact]
    public async Task Replacing_the_original_keeps_it_renamed_and_halted_and_failed_restores_roll_back()
    {
        var (vmUuid, disks) = _pool.AddVm("erp01", DiskSize);
        await using var session = await Provider().OpenAsync(Settings(), Ct);
        var (manifest, metadata) = await CaptureAsync(session, vmUuid);
        var plan = new VmRestorePlan
        {
            Mode = RestoreMode.OriginalLocation,
            Manifest = manifest,
            Metadata = metadata,
            VmName = "erp01",
            DiskStorage = new Dictionary<string, string> { ["0"] = _pool.Uuid("SR", _pool.SrRef) },
            ReplaceExisting = true,
            SessionId = Guid.NewGuid(),
        };

        // A restore that fails after creating the disk leaves nothing behind and the original keeps its name.
        var vdiCount = _pool.Records("VDI").Count;
        await using (var failing = await session.BeginRestoreAsync(plan, Log, _ledger, Ct))
        {
            await failing.CreateDiskAsync(new RestoreDiskTarget("0", disks[0], "disk", DiskSize, plan.DiskStorage["0"]), Ct);
        }

        Assert.Equal(vdiCount, _pool.Records("VDI").Count);

        await using (var restore = await session.BeginRestoreAsync(plan, Log, _ledger, Ct))
        {
            Assert.True((await restore.ValidateAsync(Ct)).Ok);
            await using var writer = await restore.CreateDiskAsync(new RestoreDiskTarget("0", disks[0], "disk", DiskSize, plan.DiskStorage["0"]), Ct);
            await writer.WriteAsync(new ByteImage(_pool.DiskContent(disks[0])), null, Ct);
            await restore.CreateVmAsync(new Dictionary<string, string> { [disks[0]] = writer.DiskId }, Ct);
            await restore.CompleteAsync(Ct);
        }

        var replaced = _pool.Records("VM").Single(v => v.Str("uuid") == vmUuid);
        Assert.StartsWith("erp01 (replaced", replaced.Str("name_label"), StringComparison.Ordinal);
        Assert.Equal("Halted", replaced.Str("power_state"));
        Assert.Single(_pool.Records("VM"), v => v.Str("name_label") == "erp01");
    }

    private static VmBackupRequest Request(string vmUuid) => new(vmUuid, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "backdafuckup test");

    private async Task<ChangeToken> FullBackupAsync(IHypervisorSession session, string vmUuid)
    {
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var token = backup.GetNewChangeToken(backup.Disks[0])!;
        await backup.CommitAsync(Log, Ct);
        return token;
    }

    private async Task<(RestorePointManifest Manifest, VmMetadataBlob Metadata)> CaptureAsync(IHypervisorSession session, string vmUuid)
    {
        await using var backup = await session.BeginBackupAsync(Request(vmUuid), Log, _ledger, Ct);
        var manifest = new RestorePointManifest
        {
            RestorePointId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            CreatedUtc = DateTimeOffset.UtcNow,
            Kind = RestorePointKind.Full,
            Source = new ManifestSource(HypervisorKind.XenServer, "pool", vmUuid, backup.Vm.Name, backup.SnapshotId),
            Vm = backup.Vm,
            Chunking = new ManifestChunking(1024 * 1024, "sha256", "zstd"),
            Disks = backup.Disks.Select(d => new ManifestDisk
            {
                Key = d.Key,
                SourceDiskId = d.SourceDiskId,
                Name = d.Name,
                CapacityBytes = d.CapacityBytes,
                Index = new ManifestIndexRef("x", "x", 1),
                Stats = new DiskBackupStats(),
            }).ToList(),
        };
        var metadata = backup.Metadata;
        await backup.CommitAsync(Log, Ct);
        return (manifest, metadata);
    }

    private static async Task<byte[]> ReadAllAsync(IVmBackupSession backup, SnapshotDisk disk)
    {
        await using var source = await backup.OpenReaderAsync(disk, new DiskReaderOptions(Connections: 2, Transport: DiskTransportMode.NbdOnly), Ct);
        var reader = Assert.IsAssignableFrom<IDiskReader>(source);
        var data = new byte[reader.Capacity];
        const int block = 1024 * 1024;
        await Parallel.ForAsync(0L, (data.LongLength + block - 1) / block, Ct, async (i, ct) =>
        {
            var length = (int)Math.Min(block, data.LongLength - (i * block));
            await reader.ReadAsync(i * block, data.AsMemory((int)(i * block), length), ct);
        });
        return data;
    }

    private static async Task<byte[]> StreamAllAsync(IDiskSource source)
    {
        var reader = Assert.IsAssignableFrom<ISequentialDiskReader>(source);
        var data = new byte[reader.Capacity];
        long last = -1;
        await reader.StreamAsync(
            (offset, chunk, _) =>
            {
                Assert.True(offset > last, "the stream must deliver increasing offsets");
                last = offset;
                chunk.Span.CopyTo(data.AsSpan((int)offset));
                return ValueTask.CompletedTask;
            },
            Ct);
        return data;
    }

    private sealed class ListLog(List<string> lines) : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null)
        {
            lock (lines)
            {
                lines.Add($"{severity}: {message}");
            }
        }
    }

    private sealed class ByteImage(byte[] content) : IDiskImageSource
    {
        public long Capacity => content.LongLength;

        public int ChunkSize => 1024 * 1024;

        /// <summary>Like a restore point: the chunks that are not all-zero.</summary>
        public IReadOnlyList<DiskExtent> DataExtents { get; } = Enumerable.Range(0, (int)((content.LongLength + (1024 * 1024) - 1) / (1024 * 1024)))
            .Select(i => new DiskExtent(i * 1024L * 1024, Math.Min(1024L * 1024, content.LongLength - (i * 1024L * 1024))))
            .Where(e => content.AsSpan((int)e.Offset, (int)e.Length).ContainsAnyExcept((byte)0))
            .ToList();

        public Stream OpenSequential(int prefetchChunks = 16, int workers = 8) => new MemoryStream(content, writable: false);

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            content.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
            return ValueTask.CompletedTask;
        }
    }
}
