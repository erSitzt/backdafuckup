using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.Testing.Vim;

namespace Backdafuckup.VSphere.Tests;

/// <summary>The vSphere provider against the fake vCenter: connection test, inventory, storage state, guest addresses.</summary>
public sealed class VSphereProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (VSphereProvider Provider, ConnectionSettings Settings) Provider(FakeVcenter vcenter, string? password = null) =>
        (new VSphereProvider(new VSphereOptions(), _ => vcenter.CreateHandler()),
         new ConnectionSettings(Guid.NewGuid(), HypervisorKind.VSphere, "vcenter.corp.example", 443, new NetworkCredential(vcenter.UserName, password ?? vcenter.Password),
             TlsTrustMode.Pinned, [], new Dictionary<string, string>()));

    private static (FakeVcenter Vcenter, (string Type, string Value) Host, (string Type, string Value) Datastore) Lab(string? hostCertificate = null)
    {
        var vcenter = new FakeVcenter();
        var dc = vcenter.AddDatacenter("DC-Lab");
        var cluster = vcenter.AddCluster(dc, "Cluster-A");
        var host = vcenter.AddHost(cluster, "esx01.corp.example", hostCertificate);
        var datastore = vcenter.AddDatastore("ds-shared-01", 1L << 40, 400L << 30);
        return (vcenter, host, datastore);
    }

    [Fact]
    public async Task The_connection_test_names_the_platform_and_pins_the_esxi_hosts()
    {
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=esx01.corp.example", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var (vcenter, _, _) = Lab(certificate.ExportCertificatePem());
        var (provider, settings) = Provider(vcenter);

        var result = await provider.TestConnectionAsync(settings, Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(("1c0306a4-870a-4572-9472-3fbdd6561a0a", "vcenter.corp.example", "VMware vCenter Server", "7.0.3 build 21477706"),
            (result.Platform!.PlatformId, result.Platform.PlatformName, result.Platform.ProductName, result.Platform.ProductVersion));
        var pin = Assert.Single(result.HostPins);
        Assert.Equal(("esx01.corp.example", CertificatePinning.SpkiSha256(certificate)), (pin.HostAddress, pin.SpkiSha256));
        Assert.Contains("Logout", vcenter.Calls);
    }

    [Fact]
    public async Task A_standalone_esxi_host_and_a_wrong_password_are_refused_with_the_reason()
    {
        var (vcenter, _, _) = Lab();
        vcenter.ApiType = "HostAgent";
        var (provider, settings) = Provider(vcenter);
        var esxi = await provider.TestConnectionAsync(settings, Ct);
        Assert.False(esxi.Success);
        Assert.StartsWith("vcenter.corp.example is a standalone ESXi host (VMware vCenter Server 7.0.3 build-21477706), not a vCenter. Add the vCenter that manages it", esxi.Error, StringComparison.Ordinal);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAsync(settings, Ct));

        vcenter.ApiType = "VirtualCenter";
        var (_, wrong) = Provider(vcenter, "nope");
        var login = await provider.TestConnectionAsync(wrong, Ct);
        Assert.False(login.Success);
        Assert.Contains("the user name or password of 'backup@vsphere.local' is wrong (a local SSO user needs its domain, e.g. user@vsphere.local)", login.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inventory_lists_hosts_datastores_networks_and_vms_but_not_templates()
    {
        var (vcenter, host, datastore) = Lab();
        vcenter.AddNetwork("VM Network");
        vcenter.AddNetwork("dvpg-servers", "DistributedVirtualPortgroup");
        var (app, appUuid) = vcenter.AddVm("app01", host, [
            new FakeVmDisk("[ds-shared-01] app01/app01.vmdk", 40L << 30, datastore, "6000C291-1111-2222-3333-444455556666"),
            new FakeVmDisk("[ds-shared-01] app01/app01_1.vmdk", 10L << 30, datastore, "6000C292-1111-2222-3333-444455556666", Key: 2001, Unit: 1)],
            changeTracking: true, biosUuid: "421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8");
        vcenter.AddVm("tpl-ubuntu", host, [new FakeVmDisk("[ds-shared-01] tpl/tpl.vmdk", 10L << 30, datastore, "6000C293-0000-0000-0000-000000000000")], template: true);
        var (provider, settings) = Provider(vcenter);
        await using var session = await provider.OpenAsync(settings, Ct);

        var inventory = await session.GetInventoryAsync(Ct);

        var esx = Assert.Single(inventory.Items, i => i.Kind == InventoryKind.Host);
        Assert.Equal(("esx01.corp.example", "Cluster-A", "True"), (esx.Name, esx.Properties["cluster"], esx.Properties["enabled"]));
        var ds = Assert.Single(inventory.Items, i => i.Kind == InventoryKind.Storage);
        Assert.Equal(("ds-shared-01", "VMFS", "True", (1L << 40).ToString(), ((1L << 40) - (400L << 30)).ToString()),
            (ds.Name, ds.Properties["type"], ds.Properties["shared"], ds.Properties["physicalSize"], ds.Properties["physicalUtilisation"]));
        Assert.Equal(["VM Network", "dvpg-servers"], inventory.Items.Where(i => i.Kind == InventoryKind.Network).Select(i => i.Name));
        var vm = Assert.Single(inventory.Items, i => i.Kind == InventoryKind.Vm);
        Assert.Equal((appUuid, "app01", host.Value), (vm.NativeId, vm.Name, vm.ParentNativeId));
        Assert.Equal(("poweredOn", "2", (4096L << 20).ToString(), "2", (50L << 30).ToString(), "True", "421a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8", app.Value, "esx01.corp.example"),
            (vm.Properties["powerState"], vm.Properties["vcpus"], vm.Properties["memoryBytes"], vm.Properties["diskCount"], vm.Properties["diskBytes"], vm.Properties["cbtEnabled"],
             vm.Properties["biosUuid"], vm.Properties["moref"], vm.Properties["host"]));
    }

    [Fact]
    public async Task The_storage_state_counts_disks_per_datastore_and_recognises_our_leftover_snapshots()
    {
        var (vcenter, host, datastore) = Lab();
        var (app, _) = vcenter.AddVm("app01", host, [new FakeVmDisk("[ds-shared-01] app01/app01.vmdk", 40L << 30, datastore, "6000C291-1111-2222-3333-444455556666")], changeTracking: true);
        vcenter.AddVm("db01", host, [new FakeVmDisk("[ds-shared-01] db01/db01.vmdk", 20L << 30, datastore, "6000C291-9999-2222-3333-444455556666")]);
        var taken = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);
        vcenter.SetSnapshots(app, ("before update", "by the admin", taken.AddDays(-3)), ("backdafuckup temporary snapshot", "backdafuckup session=1 job=2 restorepoint=3", taken));
        var (provider, settings) = Provider(vcenter);
        await using var session = await provider.OpenAsync(settings, Ct);

        var state = await session.GetStorageStateAsync(Ct);

        var usage = Assert.Single(state.Storages);
        Assert.Equal(("ds-shared-01", 2, 60L << 30), (usage.Name, usage.Disks, usage.VirtualAllocationBytes));
        Assert.Equal((1, 1), (state.DisksWithChangeTracking, state.DisksWithoutChangeTracking));
        Assert.Equal([("app01: before update", false), ("app01: backdafuckup temporary snapshot", true)], state.Snapshots.Select(s => (s.Name, s.Ours)));
        Assert.Equal(taken, state.Snapshots[1].TakenUtc);
    }

    [Fact]
    public async Task Guest_addresses_come_from_vmware_tools_ipv4_first_without_link_local()
    {
        var (vcenter, host, datastore) = Lab();
        var (app, appUuid) = vcenter.AddVm("app01", host, [new FakeVmDisk("[ds-shared-01] app01/app01.vmdk", 40L << 30, datastore, "6000C291-1111-2222-3333-444455556666")]);
        vcenter.SetGuestAddresses(app, "10.26.16.91", "fe80::250:56ff:fe8b:357e", "2001:db8::5", "10.26.16.91", "127.0.0.1");
        var (provider, settings) = Provider(vcenter);
        await using var session = await provider.OpenAsync(settings, Ct);

        Assert.Equal(["10.26.16.91", "2001:db8::5"], await session.GetGuestAddressesAsync(appUuid, Ct));
        Assert.Empty(await session.GetGuestAddressesAsync(Guid.NewGuid().ToString(), Ct));
    }
}
