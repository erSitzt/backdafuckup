using System.Globalization;
using System.Xml.Linq;
using Backdafuckup.Core.Operations;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>
/// A new VM's configuration from the backed-up one (<see cref="VSphereConfig"/>): the disk controllers again, each disk on
/// its controller and unit with the file it now has, the NICs (mapped network, original or new MAC, connected or not).
/// Other devices (CD-ROM, USB, serial, vTPM) are left out. Restores and instant recoveries build their VMs with it.
/// </summary>
internal sealed class VSphereVmSpecBuilder(VimSession vim, VSphereConfig config, IOperationLog log)
{
    public IReadOnlyList<VimObject> Devices { get; } =
        string.IsNullOrEmpty(config.DevicesXml) ? [] : [.. XElement.Parse(config.DevicesXml).Elements().Select(e => new VimObject(e))];

    /// <summary>The outermost backing of a disk: the base disk below its snapshot deltas, whose UUID is the disk's id in a restore point.</summary>
    public static VimObject? RootBacking(VimObject disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var root = disk.Child("backing");
        while (root?.Child("parent") is { } parent)
        {
            root = parent;
        }

        return root;
    }

    /// <summary>The backed-up disk whose id in the restore point is <paramref name="sourceDiskId"/>, and its controller.</summary>
    public (VimObject Disk, VimObject? Controller)? FindDisk(string sourceDiskId)
    {
        foreach (var disk in Devices.Where(d => d.Type == "VirtualDisk"))
        {
            if (RootBacking(disk)?.Str("uuid") == sourceDiskId || disk.Child("backing")?.Str("uuid") == sourceDiskId
                || disk.Int("key")?.ToString(CultureInfo.InvariantCulture) == sourceDiskId)
            {
                return (disk, Devices.FirstOrDefault(d => d.Int("key") == disk.Int("controllerKey")));
            }
        }

        return null;
    }

    /// <summary>
    /// The devices to add: the controllers, every disk that has a file in <paramref name="sourceToFile"/> (by the id the
    /// restore point gives it), and the NICs.
    /// </summary>
    /// <param name="networkMap">Network per NIC device key; NICs not listed keep their network (a distributed port group by its key, vCenter picks a free port).</param>
    /// <param name="connectNics">Whether the NICs start connected (as they were) or disconnected.</param>
    /// <param name="keepMacs">Keep the original MAC addresses, else let vCenter generate new ones.</param>
    public async Task<List<XElement>> DeviceChangesAsync(
        IReadOnlyDictionary<string, string> sourceToFile, IReadOnlyDictionary<string, string> networkMap, bool connectNics, bool keepMacs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceToFile);
        ArgumentNullException.ThrowIfNull(networkMap);
        var changes = new List<XElement>();
        var keys = new Dictionary<int, int>();
        var next = -200;
        foreach (var controller in Devices.Where(d => VSphereDevices.ScsiControllerTypes.Contains(d.Type) || VSphereDevices.OtherDiskControllerTypes.Contains(d.Type)))
        {
            var key = next--;
            keys[controller.Int("key") ?? 0] = key;
            var scsi = VSphereDevices.ScsiControllerTypes.Contains(controller.Type);
            changes.Add(Add(VimXml.Typed("device", controller.Type!, VimXml.E("key", key), PciSlot(controller), VimXml.E("busNumber", controller.Int("busNumber") ?? 0),
                scsi ? VimXml.E("sharedBus", controller.Str("sharedBus") ?? "noSharing") : null)));
        }

        foreach (var disk in Devices.Where(d => d.Type == "VirtualDisk"))
        {
            var backing = disk.Child("backing");
            var root = RootBacking(disk);
            var label = disk.Child("deviceInfo")?.Str("label") ?? $"disk {disk.Int("key")}";
            var file = new[] { root?.Str("uuid"), backing?.Str("uuid"), disk.Int("key")?.ToString(CultureInfo.InvariantCulture) }
                .Where(id => id is not null).Select(id => sourceToFile.GetValueOrDefault(id!)).FirstOrDefault(f => f is not null);
            if (file is null)
            {
                log.Warning($"{label} of {config.Name} is not in the restore point; the new VM has no such disk.");
                continue;
            }

            var mode = backing?.Str("diskMode") is { } original && original.StartsWith("independent", StringComparison.Ordinal) ? original : "persistent";
            var controllerKey = disk.Int("controllerKey") ?? 0;
            changes.Add(Add(VimXml.Typed(
                "device", "VirtualDisk",
                VimXml.E("key", next--),
                VimXml.Typed("backing", "VirtualDiskFlatVer2BackingInfo", VimXml.E("fileName", file), VimXml.E("diskMode", mode)),
                VimXml.E("controllerKey", keys.GetValueOrDefault(controllerKey, controllerKey)),
                VimXml.E("unitNumber", disk.Int("unitNumber") ?? 0),
                VimXml.E("capacityInKB", disk.Long("capacityInKB") ?? (disk.Long("capacityInBytes") ?? 0) / 1024))));
        }

        foreach (var nic in Devices.Where(d => VSphereDevices.NicTypes.Contains(d.Type)))
        {
            var deviceKey = (nic.Int("key") ?? 0).ToString(CultureInfo.InvariantCulture);
            var original = nic.Child("backing");
            var backing = networkMap.TryGetValue(deviceKey, out var network)
                ? await NetworkBackingAsync(vim, network, cancellationToken).ConfigureAwait(false)
                : original?.Type == "VirtualEthernetCardDistributedVirtualPortBackingInfo"
                    // The same port group, but not the original's port: vCenter picks a free one.
                    ? VimXml.Typed("backing", original.Type, VimXml.E("port", VimXml.E("switchUuid", original.Child("port")?.Str("switchUuid")), VimXml.E("portgroupKey", original.Child("port")?.Str("portgroupKey"))))
                    : original is null ? null : new XElement(original.Element);
            var mac = nic.Str("macAddress");
            var keepMac = keepMacs && !string.IsNullOrEmpty(mac);
            changes.Add(Add(VimXml.Typed(
                "device", nic.Type!,
                VimXml.E("key", next--),
                backing,
                VimXml.E("connectable", VimXml.E("startConnected", connectNics ? nic.Child("connectable")?.Str("startConnected") ?? "true" : "false"), VimXml.E("allowGuestControl", "true"),
                    VimXml.E("connected", "false")),
                // The guest knows its NICs by PCI slot (Windows keeps a static address with it): the same slot as before.
                PciSlot(nic),
                VimXml.E("addressType", keepMac ? "manual" : "generated"),
                keepMac ? VimXml.E("macAddress", mac) : null)));
        }

        return changes;

        static XElement Add(XElement device) => VimXml.E("deviceChange", VimXml.E("operation", "add"), device);

        static XElement? PciSlot(VimObject device) =>
            device.Child("slotInfo") is { Type: "VirtualDevicePciBusSlotInfo" } slot && slot.Int("pciSlotNumber") is { } number
                ? VimXml.Typed("slotInfo", "VirtualDevicePciBusSlotInfo", VimXml.E("pciSlotNumber", number))
                : null;
    }

    /// <summary>
    /// The VirtualMachineConfigSpec for CreateVM_Task, in the XSD's order.
    /// </summary>
    /// <param name="vmxPath">Where the .vmx goes ("[ds] folder/name.vmx"); the VM's other files go next to it.</param>
    /// <param name="biosUuid">The BIOS UUID to keep (original location), or null for a new one.</param>
    /// <param name="hostLocalSwap">Put the swap file on the host's swap datastore, where the host has one (not next to the .vmx).</param>
    /// <param name="annotation">The VM's notes; null keeps the backed-up VM's.</param>
    public XElement ConfigSpec(string name, string vmxPath, IReadOnlyList<XElement> deviceChanges, string? biosUuid, bool hostLocalSwap = false, string? annotation = null) =>
        VimXml.E(
            "config",
            VimXml.E("name", name),
            config.HardwareVersion is { Length: > 0 } version ? VimXml.E("version", version) : null,
            biosUuid is { Length: > 0 } ? VimXml.E("uuid", biosUuid) : null,
            VimXml.E("guestId", config.GuestId ?? "otherGuest64"),
            (annotation ?? config.Annotation) is { Length: > 0 } notes ? VimXml.E("annotation", notes) : null,
            VimXml.E("files", VimXml.E("vmPathName", vmxPath)),
            VimXml.E("numCPUs", config.NumCpu),
            config.CoresPerSocket is { } cores ? VimXml.E("numCoresPerSocket", cores) : null,
            VimXml.E("memoryMB", config.MemoryMb),
            deviceChanges,
            hostLocalSwap ? VimXml.E("swapPlacement", "hostLocal") : null,
            config.EfiSecureBoot == true ? VimXml.E("bootOptions", VimXml.E("efiSecureBootEnabled", "true")) : null,
            VimXml.E("firmware", config.Firmware ?? "bios"));

    /// <summary>The NIC backing for a network: a standard network by name and reference, a distributed port group by switch and key.</summary>
    public static async Task<XElement> NetworkBackingAsync(VimSession vim, string networkId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vim);
        if (networkId.StartsWith("dvportgroup-", StringComparison.Ordinal))
        {
            var group = new ManagedObjectReference("DistributedVirtualPortgroup", networkId);
            var row = (await vim.RetrievePropertiesAsync([group], ["key", "config.distributedVirtualSwitch"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            var dvs = row?["config.distributedVirtualSwitch"]?.AsRef() ?? throw new InvalidOperationException($"Port group {networkId} belongs to no distributed switch.");
            var uuid = (await vim.RetrievePropertiesAsync([dvs], ["uuid"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("uuid")
                ?? throw new InvalidOperationException($"Distributed switch {dvs.Value} has no UUID.");
            return VimXml.Typed("backing", "VirtualEthernetCardDistributedVirtualPortBackingInfo",
                VimXml.E("port", VimXml.E("switchUuid", uuid), VimXml.E("portgroupKey", row.Str("key") ?? networkId)));
        }

        var network = new ManagedObjectReference("Network", networkId);
        var name = (await vim.RetrievePropertiesAsync([network], ["name"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Str("name") ?? networkId;
        return VimXml.Typed("backing", "VirtualEthernetCardNetworkBackingInfo", VimXml.E("deviceName", name), VimXml.Ref("network", network));
    }
}
