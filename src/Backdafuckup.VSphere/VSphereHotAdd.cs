using System.Globalization;
using System.Xml.Linq;
using Backdafuckup.Core.Proxies;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>A free place for a hot-added disk on a proxy VM's SCSI controller.</summary>
internal sealed record ScsiSlot(int ControllerKey, int Bus, int Unit);

/// <summary>A connected proxy as a VM of this vCenter: where it runs, what it sees, where disks can go.</summary>
internal sealed record ProxyVm(
    ProxyInfo Proxy, ManagedObjectReference Vm, string VmName, string? HostId, string? HostName, string? ClusterId, IReadOnlySet<string> Datastores, IReadOnlyList<ScsiSlot> FreeSlots);

/// <summary>
/// HotAdd: attaching a snapshot's disk to a proxy VM (<c>ReconfigVM_Task</c>, mode independent_nonpersistent) and
/// detaching it again. Neither carries a <c>fileOperation</c>: an attach never creates and a remove never destroys
/// the disk file, which belongs to the backed-up VM. Only a restore creates a new disk file on the proxy
/// (<see cref="CreateSpec"/>, fileOperation create), which then stays when it is detached.
/// </summary>
internal static class VSphereHotAdd
{
    private static readonly string[] ScsiControllers = ["ParaVirtualSCSIController", "VirtualLsiLogicSASController", "VirtualLsiLogicController", "VirtualBusLogicController"];

    /// <summary>The device change that attaches <paramref name="fileName"/> to <paramref name="slot"/> (no fileOperation).</summary>
    public static XElement AddSpec(ScsiSlot slot, string fileName, long capacityBytes, string diskMode = "independent_nonpersistent") =>
        VimXml.E(
            "spec",
            VimXml.E(
                "deviceChange",
                VimXml.E("operation", "add"),
                VimXml.Typed(
                    "device", "VirtualDisk",
                    VimXml.E("key", -100),
                    VimXml.Typed("backing", "VirtualDiskFlatVer2BackingInfo", VimXml.E("fileName", fileName), VimXml.E("diskMode", diskMode)),
                    VimXml.E("controllerKey", slot.ControllerKey),
                    VimXml.E("unitNumber", slot.Unit),
                    VimXml.E("capacityInKB", capacityBytes / 1024))));

    /// <summary>
    /// A restore's new disk, created on the proxy VM as <paramref name="fileName"/> (its folder must exist) and attached
    /// independent_persistent, so that writes go straight into the file even if the proxy VM has a snapshot.
    /// </summary>
    public static XElement CreateSpec(ScsiSlot slot, string fileName, long capacityBytes, bool thin) =>
        VimXml.E(
            "spec",
            VimXml.E(
                "deviceChange",
                VimXml.E("operation", "add"),
                VimXml.E("fileOperation", "create"),
                VimXml.Typed(
                    "device", "VirtualDisk",
                    VimXml.E("key", -100),
                    VimXml.Typed("backing", "VirtualDiskFlatVer2BackingInfo", VimXml.E("fileName", fileName), VimXml.E("diskMode", "independent_persistent"),
                        VimXml.E("thinProvisioned", thin ? "true" : "false")),
                    VimXml.E("controllerKey", slot.ControllerKey),
                    VimXml.E("unitNumber", slot.Unit),
                    VimXml.E("capacityInKB", (capacityBytes + 1023) / 1024))));

    /// <summary>Creates a restore's disk on the proxy VM; returns its device key there and the disk's UUID (its SCSI serial).</summary>
    public static async Task<(int DeviceKey, string Uuid, string FileName)> CreateAsync(
        VimSession vim, ProxyVm proxy, ScsiSlot slot, string fileName, long capacityBytes, bool thin, CancellationToken cancellationToken)
    {
        var task = await vim.StartTaskAsync("ReconfigVM_Task", proxy.Vm, cancellationToken, CreateSpec(slot, fileName, capacityBytes, thin)).ConfigureAwait(false);
        await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
        var devices = (await vim.RetrievePropertiesAsync([proxy.Vm], ["config.hardware.device"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["config.hardware.device"];
        var disk = devices?.Items.FirstOrDefault(d => d.Type == "VirtualDisk" && d.Int("controllerKey") == slot.ControllerKey && d.Int("unitNumber") == slot.Unit)
            ?? throw new InvalidDataException($"vCenter {vim.Endpoint}: {fileName} was created on proxy VM {proxy.VmName}, but the VM shows no disk on SCSI {slot.Bus}:{slot.Unit}.");
        return (disk.Int("key") ?? 0, disk.Child("backing")?.Str("uuid") ?? throw new InvalidDataException($"vCenter {vim.Endpoint}: the new disk {fileName} has no UUID."),
            disk.Child("backing")?.Str("fileName") ?? fileName);
    }

    /// <summary>The device change that detaches a hot-added disk (no fileOperation: the file stays).</summary>
    public static XElement RemoveSpec(int deviceKey, ScsiSlot slot, long capacityBytes) =>
        VimXml.E(
            "spec",
            VimXml.E(
                "deviceChange",
                VimXml.E("operation", "remove"),
                VimXml.Typed("device", "VirtualDisk", VimXml.E("key", deviceKey), VimXml.E("controllerKey", slot.ControllerKey), VimXml.E("unitNumber", slot.Unit),
                    VimXml.E("capacityInKB", capacityBytes / 1024))));

    /// <summary>Attaches the disk and returns its device key on the proxy VM.</summary>
    public static async Task<int> AttachAsync(VimSession vim, ProxyVm proxy, ScsiSlot slot, string fileName, long capacityBytes, CancellationToken cancellationToken) =>
        (await AttachDiskAsync(vim, proxy, slot, fileName, capacityBytes, "independent_nonpersistent", cancellationToken).ConfigureAwait(false)).DeviceKey;

    /// <summary>
    /// Attaches a disk file in the given mode; returns its device key on the proxy VM and the UUID vCenter reports for it
    /// (the SCSI serial the proxy finds it by). A quick rollback attaches the VM's current disk persistent, to write into it.
    /// </summary>
    public static async Task<(int DeviceKey, string? Uuid)> AttachDiskAsync(
        VimSession vim, ProxyVm proxy, ScsiSlot slot, string fileName, long capacityBytes, string diskMode, CancellationToken cancellationToken)
    {
        var task = await vim.StartTaskAsync("ReconfigVM_Task", proxy.Vm, cancellationToken, AddSpec(slot, fileName, capacityBytes, diskMode)).ConfigureAwait(false);
        await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
        var devices = (await vim.RetrievePropertiesAsync([proxy.Vm], ["config.hardware.device"], cancellationToken).ConfigureAwait(false)).FirstOrDefault()?["config.hardware.device"];
        var disk = devices?.Items.FirstOrDefault(d => d.Type == "VirtualDisk" && d.Int("controllerKey") == slot.ControllerKey && d.Int("unitNumber") == slot.Unit)
            ?? throw new InvalidDataException($"vCenter {vim.Endpoint}: {fileName} was attached to proxy VM {proxy.VmName}, but the VM shows no disk on SCSI {slot.Bus}:{slot.Unit}.");
        return (disk.Int("key") ?? 0, disk.Child("backing")?.Str("uuid"));
    }

    public static async Task DetachAsync(VimSession vim, ManagedObjectReference proxyVm, int deviceKey, ScsiSlot slot, long capacityBytes, CancellationToken cancellationToken)
    {
        var task = await vim.StartTaskAsync("ReconfigVM_Task", proxyVm, cancellationToken, RemoveSpec(deviceKey, slot, capacityBytes)).ConfigureAwait(false);
        await vim.WaitForTaskAsync(task, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The connected proxies with HotAdd that run as VMs of this vCenter, found by their SMBIOS UUID (as shown, and with
    /// the first three groups byte-swapped); the others with the reason they cannot serve.
    /// </summary>
    public static async Task<(IReadOnlyList<ProxyVm> Usable, IReadOnlyList<string> Refused)> ResolveAsync(VimSession vim, IProxyPool pool, CancellationToken cancellationToken)
    {
        var usable = new List<ProxyVm>();
        var refused = new List<string>();
        foreach (var proxy in pool.Connected())
        {
            if (!proxy.Capabilities.Contains("hotadd-v1"))
            {
                refused.Add($"proxy {proxy.Name} (host {proxy.HostName}) cannot hot-add disks");
                continue;
            }

            var vm = await FindProxyVmAsync(vim, proxy, cancellationToken).ConfigureAwait(false);
            if (vm is null)
            {
                refused.Add($"proxy {proxy.Name} (host {proxy.HostName}, SMBIOS UUID {proxy.SmbiosUuid ?? "unknown"}) is no VM of vCenter {vim.Endpoint}");
                continue;
            }

            var row = (await vim.RetrievePropertiesAsync([vm], ["name", "runtime.powerState", "runtime.host", "config.hardware.device", "config.extraConfig"], cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();
            var name = row?.Str("name") ?? vm.Value;
            if (row?.Str("runtime.powerState") != "poweredOn")
            {
                refused.Add($"proxy {proxy.Name} (VM {name}) is not powered on");
                continue;
            }

            var enableUuid = row["config.extraConfig"]?.Items.FirstOrDefault(o => string.Equals(o.Str("key"), "disk.EnableUUID", StringComparison.OrdinalIgnoreCase))?.Str("value");
            if (!string.Equals(enableUuid, "TRUE", StringComparison.OrdinalIgnoreCase))
            {
                refused.Add($"proxy {proxy.Name} (VM {name}) has no disk.EnableUUID=TRUE in its configuration, so it cannot tell hot-added disks apart (set it and power-cycle the VM)");
                continue;
            }

            var host = row["runtime.host"]?.AsRef();
            var hostRow = host is null ? null : (await vim.RetrievePropertiesAsync([host], ["name", "parent", "datastore"], cancellationToken).ConfigureAwait(false)).FirstOrDefault();
            var datastores = hostRow?["datastore"]?.Items.Select(d => d.Value).ToHashSet(StringComparer.Ordinal) ?? [];
            var slots = FreeSlots(row["config.hardware.device"]);
            if (slots.Count == 0)
            {
                refused.Add($"proxy {proxy.Name} (VM {name}) has no free place on a SCSI controller for another disk");
                continue;
            }

            usable.Add(new ProxyVm(proxy, vm, name, host?.Value, hostRow?.Str("name"), hostRow?["parent"]?.AsRef().Value, datastores, slots));
        }

        return (usable, refused);
    }

    /// <summary>Free SCSI units, controllers other than bus 0 first (bus 0 holds the proxy's own disk); unit 7 is the controller's.</summary>
    internal static IReadOnlyList<ScsiSlot> FreeSlots(VimObject? devices)
    {
        if (devices is null)
        {
            return [];
        }

        var items = devices.Items.ToList();
        var slots = new List<ScsiSlot>();
        foreach (var controller in items.Where(d => ScsiControllers.Contains(d.Type)).OrderBy(c => c.Int("busNumber") == 0 ? 1 : 0).ThenBy(c => c.Int("busNumber")))
        {
            var key = controller.Int("key") ?? 0;
            var used = items.Where(d => d.Int("controllerKey") == key).Select(d => d.Int("unitNumber") ?? -1).ToHashSet();
            var reserved = controller.Int("scsiCtlrUnitNumber") ?? 7;
            slots.AddRange(Enumerable.Range(0, 16).Where(u => u != reserved && !used.Contains(u)).Select(u => new ScsiSlot(key, controller.Int("busNumber") ?? 0, u)));
        }

        return slots;
    }

    private static async Task<ManagedObjectReference?> FindProxyVmAsync(VimSession vim, ProxyInfo proxy, CancellationToken cancellationToken)
    {
        if (vim.Content.SearchIndex is not { } searchIndex || !Guid.TryParse(proxy.SmbiosUuid, out var smbios))
        {
            return null;
        }

        foreach (var uuid in new[] { smbios, new Guid(smbios.ToByteArray(), bigEndian: true) }.Distinct())
        {
            var found = await vim.InvokeAsync("FindAllByUuid", searchIndex, cancellationToken,
                VimXml.E("uuid", uuid.ToString("D", CultureInfo.InvariantCulture)), VimXml.E("vmSearch", "true"), VimXml.E("instanceUuid", "false")).ConfigureAwait(false);
            if (found?.Element.Parent?.Elements(VimXml.Ns + "returnval").Select(e => new VimObject(e).AsRef()).ToList() is { Count: 1 } single)
            {
                return single[0];
            }
        }

        return null;
    }
}

/// <summary>
/// The SCSI places this server has handed out on proxy VMs: two backups at the same time never pick the same unit of a
/// proxy, and the proxy with the fewest leases wins among equals.
/// </summary>
internal sealed class HotAddSlots
{
    private readonly Lock _lock = new();
    private readonly HashSet<(string Vm, int Controller, int Unit)> _taken = [];

    public int Leases(ManagedObjectReference proxyVm)
    {
        lock (_lock)
        {
            return _taken.Count(t => t.Vm == proxyVm.Value);
        }
    }

    public ScsiSlot? Reserve(ProxyVm proxy)
    {
        lock (_lock)
        {
            var slot = proxy.FreeSlots.FirstOrDefault(s => !_taken.Contains((proxy.Vm.Value, s.ControllerKey, s.Unit)));
            if (slot is not null)
            {
                _taken.Add((proxy.Vm.Value, slot.ControllerKey, slot.Unit));
            }

            return slot;
        }
    }

    public void Release(ManagedObjectReference proxyVm, ScsiSlot slot)
    {
        lock (_lock)
        {
            _taken.Remove((proxyVm.Value, slot.ControllerKey, slot.Unit));
        }
    }

    /// <summary>
    /// The proxy for a VM: its host must mount every datastore of the VM's disks; same host, then same cluster, then the
    /// fewest leases. Null with the reasons when none fits.
    /// </summary>
    public (ProxyVm? Proxy, IReadOnlyList<string> Refused) Choose(IReadOnlyList<ProxyVm> proxies, string? vmHost, string? vmCluster, IReadOnlyCollection<(string Id, string Name)> datastores)
    {
        var refused = new List<string>();
        var candidates = new List<ProxyVm>();
        foreach (var proxy in proxies)
        {
            var missing = datastores.Where(d => !proxy.Datastores.Contains(d.Id)).Select(d => d.Name).ToList();
            if (missing.Count > 0)
            {
                refused.Add($"proxy {proxy.Proxy.Name} (VM {proxy.VmName} on {proxy.HostName}) does not see datastore {string.Join(", ", missing)}");
                continue;
            }

            candidates.Add(proxy);
        }

        var best = candidates
            .OrderBy(p => p.HostId == vmHost ? 0 : p.ClusterId is not null && p.ClusterId == vmCluster ? 1 : 2)
            .ThenBy(p => Leases(p.Vm))
            .FirstOrDefault();
        return (best, refused);
    }
}
