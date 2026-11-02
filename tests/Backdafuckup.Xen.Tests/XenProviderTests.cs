using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
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

    private XenProvider Provider(CbtBitOrder? bitOrder = CbtBitOrder.MostSignificantBitFirst) => new(
        new XenOptions { CbtBitOrder = bitOrder, XapiRetryDelay = TimeSpan.FromMilliseconds(10), XapiRetryAttempts = 1 },
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

        var ex = await Assert.ThrowsAsync<XenNbdUnavailableException>(() => backup.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(), Ct));
        Assert.Contains("NBD", ex.Message, StringComparison.Ordinal);
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
        await using var reader = await backup.OpenReaderAsync(disk, new DiskReaderOptions(Connections: 2), Ct);
        var data = new byte[reader.Capacity];
        const int block = 1024 * 1024;
        await Parallel.ForAsync(0L, (data.LongLength + block - 1) / block, Ct, async (i, ct) =>
        {
            var length = (int)Math.Min(block, data.LongLength - (i * block));
            await reader.ReadAsync(i * block, data.AsMemory((int)(i * block), length), ct);
        });
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

        public IReadOnlyList<DiskExtent> DataExtents => [new DiskExtent(0, content.LongLength)];

        public Stream OpenSequential(int prefetchChunks = 16, int workers = 8) => new MemoryStream(content, writable: false);

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            content.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
            return ValueTask.CompletedTask;
        }
    }
}
