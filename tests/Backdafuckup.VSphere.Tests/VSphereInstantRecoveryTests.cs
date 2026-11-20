using System.Net;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Core.Tls;
using Backdafuckup.Testing.Vim;

namespace Backdafuckup.VSphere.Tests;

/// <summary>
/// Instant recovery on vSphere (spec 2, task 6) against the fake vCenter: the host's NFS addresses and mount limit, the
/// export mounted as a datastore, the VM registered around its files there, Storage vMotion, and the end that deletes no
/// file anywhere and touches only our own datastore.
/// </summary>
public sealed class VSphereInstantRecoveryTests : IDisposable
{
    private const long Gib = 1L << 30;
    private static readonly InstantRecoveryDatastore Export = new("bdfu-ir-1a2b3c4d", "192.0.2.10", "/bdfu/1a2b3c4d");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-ir-vsphere-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeVsphereLab _lab;
    private readonly InMemoryArtifactLedger _ledger = new();
    private readonly ListLog _log = new();

    public VSphereInstantRecoveryTests()
    {
        _lab = new FakeVsphereLab(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_host_offers_its_vmkernel_addresses_and_refuses_when_its_nfs_mounts_are_used_up()
    {
        await using var session = await OpenAsync();

        var host = await session.GetHostAsync(_lab.Host.Value, Ct);
        Assert.Equal("esx01.corp.example", host.Name);
        Assert.Equal(["192.0.2.21", "198.51.100.21"], host.Addresses);
        Assert.Equal((0, 8), (host.NfsMounts, host.NfsMountLimit));
        Assert.Equal(_lab.Cluster.Value, host.ClusterId);
        Assert.Equal(_lab.ResourcePool.Value, host.ResourcePoolId);
        Assert.Empty(host.Problems);

        await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        _lab.NfsMaxVolumes = 1;
        host = await session.GetHostAsync(_lab.Host.Value, Ct);
        Assert.Equal(1, host.NfsMounts);
        Assert.Equal("ESXi host esx01.corp.example mounts 1 NFS datastores, as many as NFS.MaxVolumes (1) allows; unmount one or raise the limit.", Assert.Single(host.Problems));
    }

    [Fact]
    public async Task The_layout_has_a_flat_vmfs_descriptor_per_disk_with_a_new_identity_unless_kept()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, uuids) = Backup("app01", 8 * Gib, 2 * Gib);

        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);

        Assert.Empty(layout.Problems);
        Assert.Equal(["disk0.vmdk", "disk1.vmdk"], layout.Disks.Select(d => d.Descriptor));
        Assert.Equal(["disk0-flat.vmdk", "disk1-flat.vmdk"], layout.Disks.Select(d => d.FlatFile));
        var text = Encoding.UTF8.GetString(layout.Disks[0].DescriptorContent);
        Assert.Contains("createType=\"vmfs\"\n", text, StringComparison.Ordinal);
        Assert.Contains("parentCID=ffffffff\n", text, StringComparison.Ordinal);
        Assert.Contains($"RW {8 * Gib / 512} VMFS \"disk0-flat.vmdk\"\n", text, StringComparison.Ordinal);
        Assert.Contains("ddb.adapterType = \"lsilogic\"\n", text, StringComparison.Ordinal);
        Assert.Contains($"ddb.geometry.cylinders = \"{8 * Gib / 512 / (255 * 63)}\"\n", text, StringComparison.Ordinal);
        Assert.Contains("ddb.virtualHWVersion = \"19\"\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Ddb(uuids[0]), text, StringComparison.Ordinal);
        var cid = text.Split('\n').Single(l => l.StartsWith("CID=", StringComparison.Ordinal))[4..];
        Assert.Contains($"{cid}\"\n", text.Split('\n').Single(l => l.StartsWith("ddb.longContentID", StringComparison.Ordinal)) + "\n", StringComparison.Ordinal);

        var kept = session.PlanVm(manifest, metadata, keepDiskIdentity: true);
        Assert.Contains($"ddb.uuid = \"{Ddb(uuids[1])}\"", Encoding.UTF8.GetString(kept.Disks[1].DescriptorContent), StringComparison.Ordinal);

        // A configuration that is not vSphere's is refused, naming the format.
        var xen = session.PlanVm(manifest, new VmMetadataBlob("xapi-v1", []), keepDiskIdentity: false);
        Assert.Contains("format xapi-v1", Assert.Single(xen.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vm_runs_from_the_export_and_stop_only_unregisters_it_and_unmounts_our_datastore()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib, 2 * Gib);
        var original = _lab.FindVm("app01")!.Value;
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);

        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);

        // Mounted read-write as NFS 3 from our server; the VM's disks are the descriptors there, its NICs disconnected
        // with new MACs, its swap file on the host's local datastore.
        Assert.Contains("mount 192.0.2.10:/bdfu/1a2b3c4d as bdfu-ir-1a2b3c4d", _lab.Events);
        var created = ("VirtualMachine", vm.Id);
        Assert.Equal(["[bdfu-ir-1a2b3c4d] app01/disk0.vmdk", "[bdfu-ir-1a2b3c4d] app01/disk1.vmdk"], _lab.VmDisks(created).Select(d => d.FileName));
        var nic = Assert.Single(_lab.VmNics(created));
        Assert.False(nic.StartConnected);
        Assert.NotEqual(Assert.Single(_lab.VmNics(original)).Mac, nic.Mac);
        Assert.Equal("hostLocal", _lab.Vcenter.Get(created, "config.swapPlacement")?.Value);
        Assert.StartsWith("Instant recovery of app01 from its restore point of", _lab.Vcenter.Get(created, "config.annotation")?.Value, StringComparison.Ordinal);
        Assert.Equal("poweredOn", _lab.Vcenter.Get(created, "runtime.powerState")?.Value);
        Assert.Equal(
            [(VSphereArtifactKinds.NasDatastore, datastoreId, ArtifactState.Active), (VSphereArtifactKinds.InstantRecoveryVm, vm.Id, ArtifactState.Active)],
            _ledger.Items.Select(a => (a.Kind, a.NativeId, a.State)));

        var state = await session.GetVmStateAsync(vm.Id, Export.Name, Ct);
        Assert.True(state.Exists);
        Assert.Contains("[bdfu-ir-1a2b3c4d] app01/app01 (instant).vmx", state.FilesOnDatastore);
        Assert.Contains("[bdfu-ir-1a2b3c4d] app01/disk1-flat.vmdk", state.FilesOnDatastore);
        Assert.Empty(state.FilesElsewhere);

        // While the VM is registered there, the datastore stays.
        var busy = await Assert.ThrowsAsync<InstantRecoveryRefusedException>(() => session.UnmountDatastoreAsync(datastoreId, Export, _log, Ct));
        Assert.Contains($"VMs are still registered on it: app01 (instant) ({vm.Id})", busy.Message, StringComparison.Ordinal);

        // Stop: powered off and unregistered, nothing destroyed or deleted; then the datastore is unmounted.
        await session.RemoveVmAsync(vm.Id, Export.Name, _log, Ct);
        await session.UnmountDatastoreAsync(datastoreId, Export, _log, Ct);
        Assert.Equal(["power off app01 (instant)", "unregister app01 (instant)", "unmount bdfu-ir-1a2b3c4d"], _lab.Events.SkipWhile(e => !e.StartsWith("power off", StringComparison.Ordinal)));
        Assert.DoesNotContain(_lab.Events, e => e.StartsWith("destroy", StringComparison.Ordinal) || e.StartsWith("delete", StringComparison.Ordinal));
        Assert.DoesNotContain(_lab.Vcenter.Calls, c => c is "Destroy_Task" or "DeleteVirtualDisk_Task" or "DeleteDatastoreFile_Task");
        Assert.False((await session.GetVmStateAsync(vm.Id, Export.Name, Ct)).Exists);
        Assert.True(_lab.Vcenter.Exists(original));

        // Both are gone already: doing it again is fine.
        await session.RemoveVmAsync(vm.Id, Export.Name, _log, Ct);
        await session.UnmountDatastoreAsync(datastoreId, Export, _log, Ct);
    }

    [Fact]
    public async Task A_migration_moves_every_file_thin_and_leaves_the_datastore_free_to_go()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib);
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);
        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);

        var task = await session.StartMigrationAsync(vm.Id, new InstantRecoveryMigration(_lab.Datastore.Value), _log, Ct);
        Assert.Equal(new InstantRecoveryTask("success", 100, null), await session.GetTaskAsync(task, Ct));
        var relocate = _lab.Vcenter.Requests.Last(r => r.Name.LocalName == "RelocateVM_Task");
        Assert.All(relocate.Descendants(FakeVcenter.Ns + "thinProvisioned"), t => Assert.Equal("true", t.Value));
        Assert.Contains(_log.Lines, l => l.Contains("Moving VM app01 (instant) to datastore ds-shared-01 (thin disks) by Storage vMotion", StringComparison.Ordinal));

        var state = await session.GetVmStateAsync(vm.Id, Export.Name, Ct);
        Assert.Empty(state.FilesOnDatastore);
        Assert.Contains("[ds-shared-01] app01 (instant)/app01 (instant).vmx", state.FilesElsewhere);
        Assert.Equal("poweredOn", state.PowerState);

        await session.UnmountDatastoreAsync(datastoreId, Export, _log, Ct);
        Assert.Contains("unmount bdfu-ir-1a2b3c4d", _lab.Events);
        Assert.True(_lab.Vcenter.Exists(("VirtualMachine", vm.Id)));

        // A datastore the host does not mount is refused before vCenter is asked.
        var other = _lab.Vcenter.AddDatastore("ds-elsewhere", Gib << 10, Gib << 9);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartMigrationAsync(vm.Id, new InstantRecoveryMigration(other.Value), _log, Ct));
        Assert.Equal("ESXi host esx01.corp.example does not mount datastore ds-elsewhere; choose a datastore it mounts, or another host as well.", refused.Message);
    }

    [Fact]
    public async Task A_vm_paused_on_the_out_of_space_question_shows_it_and_stop_answers_cancel_first()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib);
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);
        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);

        // Its writes passed the write limit: ESXi pauses it with a question; nothing powers it off while that is pending.
        _lab.AskOutOfSpace(vm.Id);
        var state = await session.GetVmStateAsync(vm.Id, Export.Name, Ct);
        Assert.StartsWith("msg.hbacommon.outofspace:There is no more space for virtual disk 'disk0.vmdk'.", state.Question, StringComparison.Ordinal);

        // Stop answers Cancel (which ends the session), then unregisters it.
        await session.RemoveVmAsync(vm.Id, Export.Name, _log, Ct);
        Assert.Equal(["answer app01 (instant) Cancel", "unregister app01 (instant)"], _lab.Events.SkipWhile(e => !e.StartsWith("answer", StringComparison.Ordinal)));
        Assert.Contains(_log.Lines, l => l.Contains("waited on a question in vCenter (\"msg.hbacommon.outofspace:There is no more space", StringComparison.Ordinal)
                                         && l.Contains("answered \"Cancel\", which ends its session", StringComparison.Ordinal));
        Assert.False(_lab.Vcenter.Exists(("VirtualMachine", vm.Id)));
    }

    [Fact]
    public async Task A_question_without_cancel_is_left_to_the_administrator()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib);
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);
        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);
        _lab.AskOutOfSpace(vm.Id, withCancel: false);

        var refused = await Assert.ThrowsAsync<InstantRecoveryRefusedException>(() => session.RemoveVmAsync(vm.Id, Export.Name, _log, Ct));
        Assert.Contains("waits on a question in vCenter that offers no Cancel", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_lab.Events, e => e.StartsWith("unregister", StringComparison.Ordinal) || e.StartsWith("answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_refuses_a_vm_with_files_on_production_storage_and_unmount_refuses_what_is_not_our_export()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib);
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);
        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);

        // An administrator added a disk on production storage: stopping would lose the VM's writes to it, so it is refused, the VM left running.
        _lab.AddForeignDisk(("VirtualMachine", vm.Id), "[ds-shared-01] data/data.vmdk", Gib);
        var refused = await Assert.ThrowsAsync<InstantRecoveryRefusedException>(() => session.RemoveVmAsync(vm.Id, Export.Name, _log, Ct));
        Assert.Contains("has files on other datastores than bdfu-ir-1a2b3c4d ([ds-shared-01] data/data.vmdk, [ds-shared-01] data/data-flat.vmdk)", refused.Message, StringComparison.Ordinal);
        Assert.Equal("poweredOn", _lab.Vcenter.Get(("VirtualMachine", vm.Id), "runtime.powerState")?.Value);

        // Not our export: a VMFS datastore, or an NFS datastore of another path.
        var vmfs = await Assert.ThrowsAsync<InstantRecoveryRefusedException>(() => session.UnmountDatastoreAsync(_lab.Datastore.Value, Export, _log, Ct));
        Assert.Contains($"Datastore ds-shared-01 ({_lab.Datastore.Value}) is not unmounted: it is not the export 192.0.2.10:/bdfu/1a2b3c4d", vmfs.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InstantRecoveryRefusedException>(() => session.UnmountDatastoreAsync(datastoreId, Export with { RemotePath = "/bdfu/ffffffff" }, _log, Ct));
        Assert.DoesNotContain(_lab.Events, e => e.StartsWith("unmount", StringComparison.Ordinal) || e.StartsWith("unregister", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mount_the_host_cannot_make_names_host_server_and_path_and_closes_its_ledger_row()
    {
        await using var session = await OpenAsync();
        _lab.NasMountFails = "NFS mount 192.0.2.10:/bdfu/1a2b3c4d failed: Unable to connect to NFS server.";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct));

        Assert.Equal(
            "ESXi host esx01.corp.example could not mount 192.0.2.10:/bdfu/1a2b3c4d as NFS 3 datastore bdfu-ir-1a2b3c4d: NFS mount 192.0.2.10:/bdfu/1a2b3c4d failed: " +
            "Unable to connect to NFS server. (HostConfigFault).", error.Message);
        Assert.Equal(ArtifactState.Done, Assert.Single(_ledger.Items).State);

        // A datastore of that name exists already.
        _lab.NasMountFails = null;
        await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct));
        Assert.Contains("a datastore named bdfu-ir-1a2b3c4d exists already in vCenter", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_after_a_crash_finds_the_vm_and_datastore_even_when_their_ids_were_never_recorded()
    {
        await using var session = await OpenAsync();
        var (manifest, metadata, _) = Backup("app01", 8 * Gib);
        var layout = session.PlanVm(manifest, metadata, keepDiskIdentity: false);
        var datastoreId = await session.MountDatastoreAsync(_lab.Host.Value, Export, _ledger, _log, Ct);
        var vm = await session.CreateVmAsync(Spec(manifest, metadata, layout, datastoreId), _ledger, _log, Ct);
        await session.PowerOnAsync(vm.Id, Ct);

        // The process ended after the calls were sent but before their answers were recorded: no native ids.
        var vmRow = _ledger.Items.Single(a => a.Kind == VSphereArtifactKinds.InstantRecoveryVm) with { NativeId = string.Empty };
        var datastoreRow = _ledger.Items.Single(a => a.Kind == VSphereArtifactKinds.NasDatastore) with { NativeId = string.Empty };
        await session.CleanupArtifactAsync(vmRow, _log, Ct);
        await session.CleanupArtifactAsync(datastoreRow, _log, Ct);

        Assert.Equal(["power off app01 (instant)", "unregister app01 (instant)", "unmount bdfu-ir-1a2b3c4d"], _lab.Events.SkipWhile(e => !e.StartsWith("power off", StringComparison.Ordinal)));
        Assert.True(_lab.Vcenter.Exists(_lab.FindVm("app01")!.Value));
    }

    private async Task<VSphereHypervisorSession> OpenAsync()
    {
        var provider = new VSphereProvider(new VSphereOptions(), _ => _lab.Vcenter.CreateHandler());
        var settings = new ConnectionSettings(Guid.NewGuid(), HypervisorKind.VSphere, "vcenter.corp.example", 443, new NetworkCredential(_lab.Vcenter.UserName, _lab.Vcenter.Password),
            TlsTrustMode.Pinned, [], new Dictionary<string, string>());
        return (VSphereHypervisorSession)await provider.OpenAsync(settings, Ct);
    }

    /// <summary>A VM in the lab and what its restore point would hold: the manifest and the vSphere configuration.</summary>
    private (RestorePointManifest Manifest, VmMetadataBlob Metadata, IReadOnlyList<string> DiskUuids) Backup(string name, params long[] sizes)
    {
        var (vm, instanceUuid, uuids) = _lab.AddVm(name, sizes);
        var config = new VSphereConfig(name, instanceUuid, _lab.Vcenter.Get(vm, "config.uuid")!.Value, "ubuntu64Guest", "Ubuntu Linux (64-bit)", "vmx-19", "efi", 2, 1, 4096, null,
            _lab.Vcenter.Get(vm, "config.hardware.device")!.ToString(), _lab.TestFolder.Value, _lab.ResourcePool.Value, false);
        var manifest = new RestorePointManifest
        {
            RestorePointId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            CreatedUtc = DateTimeOffset.Parse("2026-11-19T08:53:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Kind = RestorePointKind.Full,
            Source = new ManifestSource(HypervisorKind.VSphere, _lab.Vcenter.InstanceUuid, instanceUuid, name, null),
            Vm = new VmDescriptor { VmId = instanceUuid, Name = name },
            Chunking = new ManifestChunking(1024 * 1024, "sha256", "zstd"),
            Disks = [.. uuids.Select((uuid, i) => new ManifestDisk
            {
                Key = $"scsi0:{i}", SourceDiskId = uuid, Name = $"Hard disk {i + 1}", CapacityBytes = sizes[i], Index = new ManifestIndexRef("x", "x", 1), Stats = new DiskBackupStats(),
            })],
        };
        return (manifest, new VmMetadataBlob(VSphereArtifactKinds.MetadataFormat, JsonSerializer.SerializeToUtf8Bytes(config)), uuids);
    }

    private InstantRecoveryVmSpec Spec(RestorePointManifest manifest, VmMetadataBlob metadata, InstantRecoveryVmLayout layout, string datastoreId) => new()
    {
        Name = $"{manifest.Vm.Name} (instant)",
        HostId = _lab.Host.Value,
        DatastoreId = datastoreId,
        DatastoreName = Export.Name,
        Directory = manifest.Vm.Name,
        Manifest = manifest,
        Metadata = metadata,
        Disks = layout.Disks,
        FolderId = _lab.TestFolder.Value,
    };

    /// <summary>A backing UUID ("6000C29a-3b4c-…") as the descriptor writes it ("60 00 c2 9a 3b 4c 5d 6e-…").</summary>
    private static string Ddb(string uuid)
    {
        var hex = uuid.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        var bytes = Enumerable.Range(0, 16).Select(i => hex.Substring(i * 2, 2)).ToList();
        return $"{string.Join(' ', bytes[..8])}-{string.Join(' ', bytes[8..])}";
    }

    private sealed class ListLog : IOperationLog
    {
        public List<string> Lines { get; } = [];

        public void Write(LogSeverity severity, string message, string? key = null)
        {
            lock (Lines)
            {
                Lines.Add(message);
            }
        }
    }
}
