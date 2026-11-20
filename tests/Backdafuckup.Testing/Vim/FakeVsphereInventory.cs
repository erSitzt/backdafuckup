using System.Xml.Linq;

namespace Backdafuckup.Testing.Vim;

/// <summary>A disk of a fake VM: its file on a datastore, size and the backing's UUID.</summary>
public sealed record FakeVmDisk(string FileName, long CapacityBytes, (string Type, string Value) Datastore, string Uuid, int Key = 2000, int ControllerKey = 1000, int Unit = 0);

/// <summary>Builds a vCenter inventory in a <see cref="FakeVcenter"/> with the properties the vSphere provider reads.</summary>
public static class FakeVsphereInventory
{
    public static (string Type, string Value) AddDatacenter(this FakeVcenter vcenter, string name)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        var id = vcenter.NewId("datacenter");
        var folder = vcenter.Add("Folder", vcenter.NewId("group-v"), ("name", Val.Str("vm")));
        var dc = vcenter.Add("Datacenter", id, ("name", Val.Str(name)), ("parent", Val.Mor("Folder", vcenter.RootFolder.Value)), ("vmFolder", Val.Mor("Folder", folder.Value)));
        vcenter.Set(folder, "parent", Val.Mor("Datacenter", dc.Value));
        return dc;
    }

    public static (string Type, string Value) AddCluster(this FakeVcenter vcenter, (string Type, string Value) datacenter, string name)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        return vcenter.Add("ClusterComputeResource", vcenter.NewId("domain-c"), ("name", Val.Str(name)), ("parent", Val.Mor(datacenter.Type, datacenter.Value)));
    }

    public static (string Type, string Value) AddHost(this FakeVcenter vcenter, (string Type, string Value) cluster, string name, string? certificatePem = null, string version = "7.0.3")
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        return vcenter.Add(
            "HostSystem", vcenter.NewId("host"),
            [("name", Val.Str(name)), ("parent", Val.Mor(cluster.Type, cluster.Value)), ("runtime.connectionState", Val.Str("connected")),
                ("runtime.inMaintenanceMode", Val.Bool(false)), ("config.product.fullName", Val.Str($"VMware ESXi {version} build-24784741")),
                ("config.product.version", Val.Str(version)),
                .. certificatePem is null ? Array.Empty<(string, XElement)>() : [("config.certificate", Bytes(System.Text.Encoding.ASCII.GetBytes(certificatePem)))]]);
    }

    public static (string Type, string Value) AddDatastore(this FakeVcenter vcenter, string name, long capacity, long free, bool shared = true, string type = "VMFS")
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        return vcenter.Add(
            "Datastore", vcenter.NewId("datastore"),
            ("summary.name", Val.Str(name)), ("summary.type", Val.Str(type)), ("summary.multipleHostAccess", Val.Bool(shared)),
            ("summary.capacity", Val.Long(capacity)), ("summary.freeSpace", Val.Long(free)), ("summary.accessible", Val.Bool(true)));
    }

    /// <summary>The datastore is VMFS of this major version on these LUNs (extent disk names "naa.…").</summary>
    public static void SetVmfs(this FakeVcenter vcenter, (string Type, string Value) datastore, int majorVersion, params string[] diskNames)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        ArgumentNullException.ThrowIfNull(diskNames);
        vcenter.Set(datastore, "info", Val.Obj("val", "VmfsDatastoreInfo",
            Val.F("name", vcenter.Get(datastore, "summary.name")?.Value),
            new XElement(FakeVcenter.Ns + "vmfs", Val.F("majorVersion", majorVersion), diskNames.Select(d => new XElement(FakeVcenter.Ns + "extent", Val.F("diskName", d), Val.F("partition", 1))))));
    }

    public static (string Type, string Value) AddNetwork(this FakeVcenter vcenter, string name, string type = "Network")
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        return vcenter.Add(type, vcenter.NewId(type == "Network" ? "network" : "dvportgroup"), ("name", Val.Str(name)));
    }

    /// <summary>A VM with disks; returns its reference and its instance UUID (the VM's id in the product).</summary>
    public static ((string Type, string Value) Ref, string InstanceUuid) AddVm(
        this FakeVcenter vcenter, string name, (string Type, string Value) host, IReadOnlyList<FakeVmDisk> disks, string powerState = "poweredOn", bool changeTracking = false,
        bool template = false, string? biosUuid = null)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        ArgumentNullException.ThrowIfNull(disks);
        var instanceUuid = Guid.NewGuid().ToString();
        var vm = vcenter.Add(
            "VirtualMachine", vcenter.NewId("vm"),
            ("name", Val.Str(name)), ("config.instanceUuid", Val.Str(instanceUuid)), ("config.uuid", Val.Str(biosUuid ?? Guid.NewGuid().ToString())),
            ("config.template", Val.Bool(template)), ("runtime.powerState", Val.Str(powerState)), ("runtime.host", Val.Mor(host.Type, host.Value)),
            ("config.hardware.numCPU", Val.Int(2)), ("config.hardware.memoryMB", Val.Int(4096)), ("config.changeTrackingEnabled", Val.Bool(changeTracking)),
            ("config.guestFullName", Val.Str("Ubuntu Linux (64-bit)")),
            ("config.hardware.device", Devices(disks)));
        return (vm, instanceUuid);
    }

    /// <summary>Sets the VM's snapshot tree (names and descriptions, nested in order).</summary>
    public static void SetSnapshots(this FakeVcenter vcenter, (string Type, string Value) vm, params (string Name, string Description, DateTimeOffset Created)[] chain)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        ArgumentNullException.ThrowIfNull(chain);
        XElement? inner = null;
        foreach (var (snapshotName, description, created) in chain.Reverse())
        {
            // A VirtualMachineSnapshotTree; its child (the next snapshot) goes into childSnapshotList.
            inner = new XElement(
                FakeVcenter.Ns + "node",
                Val.F("vm", vm.Value), Val.F("name", snapshotName), Val.F("description", description),
                Val.F("createTime", created.ToString("o", System.Globalization.CultureInfo.InvariantCulture)), Val.F("state", "poweredOn"), Val.F("quiesced", "false"),
                inner is null ? null : new XElement(FakeVcenter.Ns + "childSnapshotList", inner.Nodes()));
        }

        vcenter.Set(vm, "snapshot", inner is null ? null : Val.Obj("val", "VirtualMachineSnapshotInfo", new XElement(FakeVcenter.Ns + "rootSnapshotList", inner.Nodes())));
    }

    /// <summary>The VM's guest NICs and their addresses as VMware Tools reports them.</summary>
    public static void SetGuestAddresses(this FakeVcenter vcenter, (string Type, string Value) vm, string? primary, params string[] addresses)
    {
        ArgumentNullException.ThrowIfNull(vcenter);
        vcenter.Set(vm, "guest.ipAddress", primary is null ? null : Val.Str(primary));
        vcenter.Set(vm, "guest.net", Val.Array("ArrayOfGuestNicInfo", [Val.Obj("GuestNicInfo", "GuestNicInfo",
            Val.F("network", "VM Network"), addresses.Select(a => Val.F("ipAddress", a)), Val.F("connected", "true"))]));
    }

    public static XElement Devices(IReadOnlyList<FakeVmDisk> disks)
    {
        ArgumentNullException.ThrowIfNull(disks);
        return Val.Array("ArrayOfVirtualDevice", [
            Val.Obj("VirtualDevice", "ParaVirtualSCSIController", Val.F("key", 1000), Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", "SCSI controller 0")),
                Val.F("controllerKey", 100), Val.F("unitNumber", 3), Val.F("busNumber", 0)),
            .. disks.Select(d => Val.Obj("VirtualDevice", "VirtualDisk",
                Val.F("key", d.Key),
                Val.F("deviceInfo", new XElement(FakeVcenter.Ns + "label", $"Hard disk {d.Key - 1999}")),
                Val.Obj("backing", "VirtualDiskFlatVer2BackingInfo",
                    Val.F("fileName", d.FileName), Val.Mor("datastore", d.Datastore), Val.F("diskMode", "persistent"), Val.F("thinProvisioned", "true"), Val.F("uuid", d.Uuid)),
                Val.F("controllerKey", d.ControllerKey), Val.F("unitNumber", d.Unit), Val.F("capacityInKB", d.CapacityBytes / 1024), Val.F("capacityInBytes", d.CapacityBytes)))]);
    }

    private static XElement Bytes(byte[] bytes) =>
        Val.Array("ArrayOfByte", bytes.Select(b => Val.F("byte", unchecked((sbyte)b).ToString(System.Globalization.CultureInfo.InvariantCulture))));
}
