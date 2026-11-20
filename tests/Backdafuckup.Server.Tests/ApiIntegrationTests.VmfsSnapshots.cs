using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Arrays;
using Backdafuckup.Server.Data;
using Backdafuckup.Testing.Arrays;
using Backdafuckup.Testing.Vim;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// Backup from storage snapshots of VMFS6 datastores (spec 3, method b): the disk under the VM snapshot read from the
/// snapshot LUN by the VMFS reader (a fake standing in for OpenVmfs), with its refusals, verify mode and format changes.
/// </summary>
public sealed partial class ApiIntegrationTests
{
    private const long VmfsDiskBytes = 8L * 1024 * 1024;
    private const long VmfsFileOffset = 4L * 1024 * 1024;
    private const string VmfsDescriptor = "app02/app02.vmdk";

    private FakeVmfsReader? _fakeVmfs;

    private FakeVmfsReader FakeVmfs => _fakeVmfs ??= new FakeVmfsReader();

    /// <summary>The SR volume of the array setup turned into a VMFS6 datastore volume: the LUN holds the fake datastore with one disk.</summary>
    private async Task<(ArraySnapshotService Service, Guid ConnectionId, Guid SystemId, string DatastoreId, byte[] Disk)> VmfsSetupAsync(int probation = 0)
    {
        var (service, _, pool, datastoreId, connectionId, systemId) = await ArraySetupAsync(probation: probation);
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            await db.StorageVolumes.Where(v => v.StorageSystemId == systemId).ExecuteUpdateAsync(u => u.SetProperty(v => v.Layout, StorageLayout.Vmfs6), Ct);
        }

        var disk = new byte[VmfsDiskBytes];
        new Random(11).NextBytes(disk.AsSpan(0, 3 * 1024 * 1024));
        var lun = FakeVmfsReader.NewLun(VmfsFileOffset + VmfsDiskBytes + (1024 * 1024));
        disk.CopyTo(lun, VmfsFileOffset);
        pool.Luns[SrWwn.ToLowerInvariant()] = lun;
        FakeVmfs.Files[VmfsDescriptor] = new FakeVmfsReader.FakeFile(VmfsFileOffset, VmfsDiskBytes, [new DiskExtent(0, 3 * 1024 * 1024)]);
        return (service, connectionId, systemId, datastoreId, disk);
    }

    [Fact]
    public async Task A_vm_on_a_vmfs6_datastore_is_read_from_the_disk_under_its_vm_snapshot_on_the_lun()
    {
        var (service, connectionId, systemId, datastoreId, disk) = await VmfsSetupAsync(probation: 1);
        var log = new RecordingLog();

        // A disk without a restore point is read through HotAdd: the reader needs a previous backup to check every run against.
        var first = new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk };
        Assert.Null(await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, first, full: true), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("disk Hard disk 1 has no restore point yet; the VMFS reader is used from a disk's second backup on", StringComparison.Ordinal));
        Assert.False(first.Released);

        // From the second backup on (here one where change tracking was reset, so the whole disk is read).
        var backup = new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk, ReadEverything = true };
        await using (var lease = await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, backup), log, Ct))
        {
            Assert.True(lease is not null, string.Join("\n", log.Entries.Select(e => e.Message)));
            Assert.True(backup.Released);
            Assert.Equal(("array-vmfs", "vmfs6"), (lease.Transport, lease.Info.Method));
            Assert.Equal("VMFS6 (vSphere datastores)", lease.Reader);
            Assert.True(lease.GetVerification(backup.Disks[0])!.Passed);

            // A full read covers only what the disk on the LUN holds.
            var changes = lease.GetChangedExtents(backup.Disks[0]);
            Assert.True(changes.IsFull);
            Assert.Equal([new DiskExtent(0, 3 * 1024 * 1024)], changes.Allocated);
            await using var reader = (IDiskReader)await lease.OpenReaderAsync(backup.Disks[0], new DiskReaderOptions(), Ct);
            var all = new byte[VmfsDiskBytes];
            await reader.ReadAsync(0, all, Ct);
            Assert.Equal(disk, all);
            Assert.Equal(1, FakeVmfs.DisksOpen);
        }

        // The lease closed the disk; the format version is recorded for the next format change.
        Assert.Equal(0, FakeVmfs.DisksOpen);
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
        var state = ArrayReaderStates.Find(await db.StorageSystems.AsNoTracking().SingleAsync(s => s.Id == systemId, Ct), ArrayReaderStates.Vmfs6);
        Assert.Equal((1, "VMFS 6.82"), (state.VerifiedRuns, state.FormatVersion));
    }

    [Fact]
    public async Task A_disk_a_host_had_open_for_writing_keeps_the_vm_on_its_vm_snapshot()
    {
        var (service, connectionId, _, datastoreId, disk) = await VmfsSetupAsync();
        FakeVmfs.Files[VmfsDescriptor] = FakeVmfs.Files[VmfsDescriptor] with { InUse = true };
        var backup = new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk };
        var log = new RecordingLog();

        Assert.Null(await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, backup), log, Ct));
        Assert.False(backup.Released);
        Assert.Contains(log.Entries, e => e.Severity == LogSeverity.Warning && e.Message.Contains("A host had the disk open for writing", StringComparison.Ordinal));

        // A descriptor that is not on the datastore, and a disk of another size, fall back the same way.
        FakeVmfs.Files[VmfsDescriptor] = FakeVmfs.Files[VmfsDescriptor] with { InUse = false, Capacity = VmfsDiskBytes / 2 };
        Assert.Null(await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, backup), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains($"is {VmfsDiskBytes} bytes in vCenter, {VmfsDiskBytes / 2} on the LUN", StringComparison.Ordinal));
        Assert.Null(await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, new VmfsFakeSession(datastoreId, "[ds-test] other/other.vmdk")), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("other/other.vmdk does not exist on the datastore", StringComparison.Ordinal));
        Assert.Equal(0, FakeVmfs.DisksOpen);
    }

    [Fact]
    public async Task A_new_datastore_format_starts_verify_mode_again()
    {
        var (service, connectionId, _, datastoreId, disk) = await VmfsSetupAsync(probation: 1);
        async Task<(bool Verified, RecordingLog Log)> RunAsync()
        {
            var backup = new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk };
            var log = new RecordingLog();
            await using var lease = await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, backup), log, Ct);
            Assert.NotNull(lease);
            return (lease.GetVerification(backup.Disks[0]) is not null, log);
        }

        Assert.True((await RunAsync()).Verified);
        Assert.False((await RunAsync()).Verified);
        FakeVmfs.FormatVersion = "VMFS 6.83";
        var (verified, log) = await RunAsync();
        Assert.True(verified);
        Assert.Contains(log.Entries, e => e.Message.Contains("the datastore format is VMFS 6.83 now (was VMFS 6.82); verify mode starts again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verify_mode_is_kept_per_esxi_version_so_mixed_clusters_do_not_start_it_over()
    {
        var (service, connectionId, systemId, datastoreId, disk) = await VmfsSetupAsync(probation: 1);
        async Task<(bool Verified, RecordingLog Log)> RunAsync(string? writer)
        {
            var backup = new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk, Writer = writer };
            var log = new RecordingLog();
            await using var lease = await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, backup), log, Ct);
            Assert.NotNull(lease);
            return (lease.GetVerification(backup.Disks[0]) is not null, log);
        }

        var seven = await RunAsync("ESXi 7.0.3");
        Assert.True(seven.Verified);
        Assert.Contains(seven.Log.Entries, e => e.Message.Contains(", ESXi 7.0.3, backup 1 of 1)", StringComparison.Ordinal));
        Assert.False((await RunAsync("ESXi 7.0.3")).Verified);

        // A datastore written by ESXi 8 on the same storage system is verified on its own, and the log says why.
        var eight = await RunAsync("ESXi 8.0.3");
        Assert.True(eight.Verified);
        Assert.Contains(eight.Log.Entries, e => e.Message.Contains("ESXi 8.0.3 is new to the VMFS6 (vSphere datastores) reader on ", StringComparison.Ordinal));

        // Jobs of both clusters taking turns do not start verify mode over.
        Assert.False((await RunAsync("ESXi 7.0.3")).Verified);
        Assert.False((await RunAsync("ESXi 8.0.3")).Verified);

        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
        var reader = ArrayReaderStates.ToDto(ArrayReaderStates.Find(await db.StorageSystems.SingleAsync(s => s.Id == systemId, Ct), ArrayReaderStates.Vmfs6), new StorageSystemOptions { ProbationRuns = 1 });
        Assert.Equal(2, reader.VerifiedRuns);
        Assert.Equal(1, reader.WriterRuns!["ESXi 7.0.3"]);
        Assert.Equal(1, reader.WriterRuns!["ESXi 8.0.3"]);
        Assert.Equal("ESXi 10.0.0", ArrayReaderStates.Highest(["ESXi 8.0.3", null, "ESXi 10.0.0", "ESXi 7.0.3"]));
    }

    [Fact]
    public async Task The_array_snapshot_waits_the_gap_after_the_last_vm_snapshot()
    {
        var (service, connectionId, systemId, datastoreId, disk) = await VmfsSetupAsync();
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var entity = await db.StorageSystems.SingleAsync(s => s.Id == systemId, Ct);
            entity.Options = new StorageSystemOptions { ExperimentalReaders = true, BatchWindowSeconds = 0, ProbationRuns = 0, MinSecondsAfterVmSnapshot = 1 };
            await db.SaveChangesAsync(Ct);
        }

        var watch = Stopwatch.StartNew();
        var log = new RecordingLog();
        await using var lease = await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk }), log, Ct);
        Assert.NotNull(lease);
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.95), $"{watch.Elapsed}");
    }

    [Fact]
    public async Task Without_a_vmfs_reader_vmfs_datastores_take_the_normal_path()
    {
        var (_, connectionId, _, datastoreId, disk) = await VmfsSetupAsync();
        var sp = _factory!.Services;
        var service = new ArraySnapshotService(
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>(), sp.GetRequiredService<StorageSystems>(), sp.GetRequiredService<ArrayReaderStates>(), SanProxies,
            sp.GetRequiredService<Telemetry.BackupMetrics>(), TimeProvider.System, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ArraySnapshotService>>());
        var log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(VmfsContext(connectionId, new VmfsFakeSession(datastoreId, $"[ds-test] {VmfsDescriptor}") { Normal = disk }), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("needs the OpenVmfs reader, which this build of the server does not include", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_vsphere_job_reads_from_array_snapshots_from_the_second_backup_on_with_its_vm_snapshot_removed_first()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var array = Alletra;
        array.AddVolume("vdi-ds1", DatastoreWwn, 64);
        array.AddHost("proxy01", ProxyIqn);
        array.AddIscsiPort("0:2:1", "10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001");
        lab.Vcenter.SetVmfs(lab.Datastore, 6, "naa." + DatastoreWwn.ToLowerInvariant());
        lab.Mount(lab.Datastore, lab.Host); // its writer: the host's ESXi 7.0.3
        var (vmRef, vmId, disks) = lab.AddVm("app03", 8L << 20);
        lab.Write(disks[0], 1L << 20, RandomNumberGenerator.GetBytes(2 << 20));
        var path = Backdafuckup.VSphere.VSphereDatastorePaths.DescriptorPath(lab.VmDisks(vmRef)[0].FileName)!;

        // The array snapshot's LUN: the datastore with the VM's disk as it is when the snapshot is taken.
        SanProxies.LunBuilder = _ =>
        {
            var content = lab.Content(disks[0]);
            var lun = FakeVmfsReader.NewLun(64L << 20);
            content.CopyTo(lun, VmfsFileOffset);
            FakeVmfs.Files[path] = new FakeVmfsReader.FakeFile(VmfsFileOffset, content.Length, [new DiskExtent(0, content.Length)]);
            return lun;
        };

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
            {
                Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
            }, Ct));
            await Read<StorageSystemDto>(client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/options",
                system.Options with { ExperimentalReaders = true, BatchWindowSeconds = 0, ProbationRuns = 1, MinSecondsAfterVmSnapshot = 0 }, Ct));
            var volume = (await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{system.Id}/volumes", Ct))!.Single(v => v.VolumeName == "vdi-ds1");
            Assert.Equal("Vmfs6", volume.Layout);
            await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{volume.Id}", new SetStorageVolumeDto(true), Ct);
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere from the array", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7,
                Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"), Objects = [new JobObjectDto(vmId, "app03")], RetryCount = 0, FromStorageSnapshots = true,
            }, Ct));

            // Full: no restore point to check against yet, so through HotAdd.
            var full = await RunVsphereJobAsync(client, job.Id);
            Assert.Contains(full.Log, l => l.Contains("has no restore point yet", StringComparison.Ordinal));
            Assert.Contains(full.Log, l => l.Contains("hot-added to proxy proxy01", StringComparison.Ordinal));

            // Incremental: verify mode compares with the VM snapshot (through HotAdd), which then goes before the disk is read from the LUN.
            lab.Write(disks[0], 6L << 20, RandomNumberGenerator.GetBytes(128 << 10));
            var incremental = await RunVsphereJobAsync(client, job.Id);
            Assert.Contains(incremental.Log, l => l.Contains("Verify mode (VMFS6 (vSphere datastores) reader on Alletra, ESXi 7.0.3, backup 1 of 1)", StringComparison.Ordinal));
            Assert.Contains(incremental.Log, l => l.Contains("removed before its disks are read", StringComparison.Ordinal));
            Assert.Contains(incremental.Log, l => l.Contains("are read from array snapshot bdfu-", StringComparison.Ordinal));
            Assert.Contains(incremental.Log, l => l.Contains("via array-vmfs", StringComparison.Ordinal));
            Assert.Contains(incremental.Log, l => l.Contains("unchanged region(s) read from the array snapshot match the previous restore point", StringComparison.Ordinal));
            var latest = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!.OrderByDescending(p => p.CreatedUtc).First();
            await AssertRestorePointMatchesAsync(latest, disks);
            Assert.Equal(1L << 20, (await ReadBytesAsync(client, latest.Id))["scsi0:0"]);

            // Nothing left: no VM snapshot, no array snapshot or presentation, no disk open on the LUN.
            Assert.Empty(lab.Snapshots());
            Assert.Empty(array.Vluns);
            Assert.DoesNotContain(array.Volumes.Values, v => v["copyOfID"] is not null);
            Assert.Equal(0, FakeVmfs.DisksOpen);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Theory]
    [InlineData("[ds1] vm1/vm1.vmdk", "vm1/vm1.vmdk")]
    [InlineData("[datastore with spaces] vm 1/vm 1-000002.vmdk", "vm 1/vm 1-000002.vmdk")]
    [InlineData("vm1/vm1.vmdk", null)]
    [InlineData("[ds1] ", null)]
    public void The_descriptor_path_drops_the_datastore_in_brackets(string fileName, string? expected) =>
        Assert.Equal(expected, Backdafuckup.VSphere.VSphereDatastorePaths.DescriptorPath(fileName));

    private static VmSnapshotContext VmfsContext(Guid connectionId, VmfsFakeSession backup, bool full = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), connectionId, null!, backup,
            new Dictionary<string, ChangeToken?> { ["scsi0:0"] = full ? null : new ChangeToken("vsphere-cbt", "52 1a/4") }, MaxPeers: 1);

    /// <summary>A vSphere VM snapshot of one disk on a VMFS6 datastore; records the early release.</summary>
    private sealed class VmfsFakeSession(string datastoreId, string fileName) : IVmBackupSession, IArraySnapshotFacts
    {
        public bool Released { get; private set; }

        public byte[]? Normal { get; init; }

        /// <summary>Change tracking answers "everything" (it was reset).</summary>
        public bool ReadEverything { get; init; }

        /// <summary>The ESXi version that writes the datastore (null: none named).</summary>
        public string? Writer { get; init; }

        public VmDescriptor Vm { get; } = new() { VmId = "vm-2", Name = "app02" };

        public IReadOnlyList<SnapshotDisk> Disks { get; } =
            [new SnapshotDisk("scsi0:0", "6000c29a-0001", "6000c29a-0001", "Hard disk 1", VmfsDiskBytes, true, datastoreId, "ds-test", new Dictionary<string, string>())];

        public VmMetadataBlob Metadata { get; } = new("none", []);

        public string SnapshotId => "snapshot-2";

        public GuestFreezeResult? GuestFreeze => null;

        public bool CanReleaseEarly => true;

        public Task<IReadOnlyDictionary<string, ArrayDiskFacts>> GetArrayDisksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, ArrayDiskFacts>>(new Dictionary<string, ArrayDiskFacts>
            {
                ["scsi0:0"] = new(datastoreId, StorageLayout.Vmfs6, [new ArrayChainMember(Backdafuckup.VSphere.VSphereDatastorePaths.DescriptorPath(fileName)!, VmfsDiskBytes, 0)], Writer),
            });

        public Task ReleaseSnapshotAsync(IOperationLog log, CancellationToken cancellationToken)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken) =>
            Task.FromResult(since is null || ReadEverything ? ChangedExtents.Full("first backup") : new ChangedExtents(false, null, [new DiskExtent(1024 * 1024, 1024 * 1024)]));

        public Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken) =>
            Released || Normal is null ? throw new NotSupportedException("No HotAdd in this test.") : Task.FromResult<IDiskSource>(new SnapshotReader(Normal));

        public ChangeToken? GetNewChangeToken(SnapshotDisk disk) => new("vsphere-cbt", "52 1a/5");

        public Task CommitAsync(IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
