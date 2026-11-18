using System.Xml.Linq;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Testing.Vim;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere.Tests;

/// <summary>HotAdd's building blocks: the reconfigurations, free SCSI places and the choice of a proxy.</summary>
public sealed class VSphereHotAddTests
{
    [Fact]
    public void Attaching_and_detaching_never_carry_a_file_operation_and_follow_the_xsd_order()
    {
        var slot = new ScsiSlot(1001, 1, 0);
        foreach (var spec in new[] { VSphereHotAdd.AddSpec(slot, "[ds] app01/app01.vmdk", 40L << 30), VSphereHotAdd.RemoveSpec(2016, slot, 40L << 30) })
        {
            Assert.Empty(spec.Descendants(VimXml.Ns + "fileOperation"));
            VimSchemaCheck.Check(VimXml.E("ReconfigVM_Task", VimXml.This(new ManagedObjectReference("VirtualMachine", "vm-1")), spec));
        }

        var add = VSphereHotAdd.AddSpec(slot, "[ds] app01/app01.vmdk", 40L << 30);
        Assert.Equal("independent_nonpersistent", add.Descendants(VimXml.Ns + "diskMode").Single().Value);
        Assert.Equal(((40L << 30) / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), add.Descendants(VimXml.Ns + "capacityInKB").Single().Value);
    }

    [Fact]
    public void Free_places_skip_unit_7_and_used_units_and_prefer_controllers_other_than_bus_0()
    {
        var devices = new VimObject(XElement.Parse(
            """
            <val xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <VirtualDevice xsi:type="ParaVirtualSCSIController"><key>1000</key><controllerKey>100</controllerKey><busNumber>0</busNumber><scsiCtlrUnitNumber>7</scsiCtlrUnitNumber></VirtualDevice>
              <VirtualDevice xsi:type="ParaVirtualSCSIController"><key>1001</key><controllerKey>100</controllerKey><busNumber>1</busNumber><scsiCtlrUnitNumber>7</scsiCtlrUnitNumber></VirtualDevice>
              <VirtualDevice xsi:type="VirtualDisk"><key>2000</key><controllerKey>1000</controllerKey><unitNumber>0</unitNumber></VirtualDevice>
              <VirtualDevice xsi:type="VirtualDisk"><key>2016</key><controllerKey>1001</controllerKey><unitNumber>0</unitNumber></VirtualDevice>
              <VirtualDevice xsi:type="VirtualAHCIController"><key>15000</key><busNumber>0</busNumber></VirtualDevice>
            </val>
            """));

        var slots = VSphereHotAdd.FreeSlots(devices);

        Assert.Equal(new ScsiSlot(1001, 1, 1), slots[0]);
        Assert.DoesNotContain(slots, s => s.Unit == 7);
        Assert.DoesNotContain(slots, s => s is { ControllerKey: 1001, Unit: 0 } or { ControllerKey: 1000, Unit: 0 });
        Assert.Equal(14 + 14, slots.Count);
    }

    [Fact]
    public void The_proxy_on_the_vms_host_wins_and_one_without_the_datastore_is_refused_with_the_reason()
    {
        static ProxyVm Proxy(string name, string host, string cluster, params string[] datastores) =>
            new(new ProxyInfo(Guid.NewGuid(), name, name + ".corp.example", null, null, ["hotadd-v1"]), new ManagedObjectReference("VirtualMachine", "vm-" + name), name, host, host + ".corp.example", cluster,
                datastores.ToHashSet(StringComparer.Ordinal), [new ScsiSlot(1001, 1, 0)]);

        var slots = new HotAddSlots();
        var sameCluster = Proxy("p1", "host-1", "domain-c1", "datastore-1", "datastore-2");
        var sameHost = Proxy("p2", "host-2", "domain-c1", "datastore-1", "datastore-2");
        var blind = Proxy("p3", "host-2", "domain-c1", "datastore-1");

        var (chosen, refused) = slots.Choose([sameCluster, sameHost, blind], "host-2", "domain-c1", [("datastore-1", "ds-01"), ("datastore-2", "ds-02")]);

        Assert.Same(sameHost, chosen);
        Assert.Equal(["proxy p3 (VM p3 on host-2.corp.example) does not see datastore ds-02"], refused);

        // A busy proxy loses to an idle one of the same rank.
        Assert.NotNull(slots.Reserve(sameHost));
        var otherOnHost = Proxy("p4", "host-2", "domain-c1", "datastore-1", "datastore-2");
        Assert.Same(otherOnHost, slots.Choose([sameHost, otherOnHost], "host-2", "domain-c1", [("datastore-1", "ds-01")]).Proxy);
    }
}
