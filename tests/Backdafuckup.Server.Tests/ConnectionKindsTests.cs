using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.Tests;

/// <summary>The connection kinds (pool, cluster, vCenter) and what changes with a third, VM-hosting kind.</summary>
public sealed class ConnectionKindsTests
{
    [Theory]
    [InlineData("XenServer", HypervisorKind.XenServer)]
    [InlineData("kubernetes", HypervisorKind.Kubernetes)]
    [InlineData(" VSphere ", HypervisorKind.VSphere)]
    [InlineData("vsphere", HypervisorKind.VSphere)]
    public void A_kind_is_parsed_by_its_name(string text, HypervisorKind expected)
    {
        Assert.True(ConnectionKinds.TryParse(text, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("VMware")]
    public void Numbers_and_unknown_names_are_no_kind(string? text) => Assert.False(ConnectionKinds.TryParse(text, out _));

    [Fact]
    public void A_list_takes_several_kinds_and_names_the_one_it_does_not_know()
    {
        Assert.True(ConnectionKinds.TryParseList("XenServer, VSphere", out var kinds, out _));
        Assert.Equal([HypervisorKind.XenServer, HypervisorKind.VSphere], kinds!.Order());
        Assert.True(ConnectionKinds.TryParseList(null, out var all, out _));
        Assert.Null(all);

        Assert.False(ConnectionKinds.TryParseList("XenServer,Proxmox", out _, out var error));
        Assert.Equal("Unknown connection kind 'Proxmox' (XenServer, Kubernetes or VSphere).", error);
    }

    [Fact]
    public void Each_kind_has_a_noun_and_a_platform_name()
    {
        Assert.Equal(["pool", "cluster", "vCenter"], Enum.GetValues<HypervisorKind>().Select(ConnectionKinds.Noun));
        Assert.Equal(["XCP-ng", "Kubernetes", "vSphere"], Enum.GetValues<HypervisorKind>().Select(ConnectionKinds.Platform));
        Assert.Equal([HypervisorKind.XenServer, HypervisorKind.VSphere], ConnectionKinds.VmPlatforms.Order());
    }

    [Fact]
    public void Only_a_pool_gets_the_insecure_nbd_option()
    {
        Assert.Equal("true", ConnectionManager.KindOptions(HypervisorKind.XenServer, allowInsecureNbd: true)[Backdafuckup.Xen.XenConnectionOptions.AllowInsecureNbd]);
        Assert.Empty(ConnectionManager.KindOptions(HypervisorKind.VSphere, allowInsecureNbd: true));
        Assert.Empty(ConnectionManager.KindOptions(HypervisorKind.Kubernetes, allowInsecureNbd: false));
    }

    [Fact]
    public void A_vmware_bios_serial_number_is_the_vms_bios_uuid()
    {
        Assert.True(AgentService.TryParseVmwareSerial("VMware-42 1a 2b 3c 4d 5e 6f 70-81 92 a3 b4 c5 d6 e7 f8", out var uuid));
        Assert.Equal(Guid.Parse("421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8"), uuid);
        Assert.False(AgentService.TryParseVmwareSerial("VMware-42 1a 2b", out _));
        Assert.False(AgentService.TryParseVmwareSerial("Parallels-42 1a 2b 3c 4d 5e 6f 70-81 92 a3 b4 c5 d6 e7 f8", out _));

        // As shown and with the first three groups swapped (how Windows reads an SMBIOS UUID).
        var candidates = AgentService.VmIdCandidates(new AgentHostInfo { BiosSerial = "VMware-42 1a 2b 3c 4d 5e 6f 70-81 92 a3 b4 c5 d6 e7 f8" });
        Assert.Equal(["421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8", "3c2b1a42-5e4d-706f-8192-a3b4c5d6e7f8"], candidates);
    }

    [Fact]
    public void Disks_captured_at_one_moment_get_no_warning_about_their_order()
    {
        var apart = new FakeSession(new SnapshotFacts(TimeSpan.FromSeconds(4), TimeSpan.Zero, 2, ["ds1"], TimeSpan.FromSeconds(3)));
        var atOnce = new FakeSession(new SnapshotFacts(TimeSpan.FromSeconds(1), TimeSpan.Zero, 2, ["ds1"], null) { DisksAtOnce = true });

        Assert.Contains("were captured one after another over 3 s", VmBackupTask.AcrossDisks("crash", null, apart, "disks"), StringComparison.Ordinal);
        Assert.Equal("no VSS", VmBackupTask.AcrossDisks("crash", "no VSS", atOnce, "disks"));
    }

    /// <summary>Just enough of a backup session for <see cref="VmBackupTask.AcrossDisks"/>.</summary>
    private sealed class FakeSession(SnapshotFacts facts) : IVmBackupSession
    {
        public VmDescriptor Vm { get; } = new() { VmId = "vm", Name = "vm" };

        public IReadOnlyList<SnapshotDisk> Disks => [];

        public VmMetadataBlob Metadata { get; } = new("none", []);

        public string SnapshotId => "snapshot";

        public GuestFreezeResult? GuestFreeze => null;

        public SnapshotFacts? Snapshot => facts;

        public Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ChangeToken? GetNewChangeToken(SnapshotDisk disk) => null;

        public Task CommitAsync(IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
