using System.Globalization;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

internal static class XenInventory
{
    public static async Task<PlatformInfo> GetPlatformAsync(XapiSession session, CancellationToken cancellationToken)
    {
        var pools = await session.GetAllRecordsAsync("pool", cancellationToken).ConfigureAwait(false);
        var pool = pools.Values.FirstOrDefault() ?? throw new InvalidOperationException("XAPI returned no pool.");
        var master = await session.GetRecordAsync("host", pool.Str("master"), cancellationToken).ConfigureAwait(false);
        var software = master.Map("software_version");
        var name = pool.Str("name_label");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = master.Str("name_label");
        }

        return new PlatformInfo(
            pool.Str("uuid"),
            name,
            master.Str("address"),
            software.GetValueOrDefault("product_brand") ?? "XenServer",
            software.GetValueOrDefault("product_version") ?? software.GetValueOrDefault("platform_version") ?? string.Empty);
    }

    /// <summary>
    /// Storages with space and disks by kind, the VM snapshots in the pool, and how many disks track changes. Read only:
    /// three get_all_records calls (SR, VDI, VM).
    /// </summary>
    public static async Task<StorageState> GetStorageStateAsync(XapiSession session, CancellationToken cancellationToken)
    {
        var srs = await session.GetAllRecordsAsync("SR", cancellationToken).ConfigureAwait(false);
        var vdis = await session.GetAllRecordsAsync("VDI", cancellationToken).ConfigureAwait(false);
        var vms = await session.GetAllRecordsAsync("VM", cancellationToken).ConfigureAwait(false);

        var byStorage = vdis.Values.GroupBy(v => v.Str("SR")).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var storages = new List<StorageUsage>();
        foreach (var (srRef, sr) in srs)
        {
            if (sr.Str("content_type") == "iso" || sr.Str("type") is "udev" or "iso")
            {
                continue;
            }

            var onIt = byStorage.TryGetValue(srRef, out var list) ? list : [];
            storages.Add(new StorageUsage(
                sr.Str("uuid"),
                sr.Str("name_label"),
                sr.Str("type"),
                sr.Bool("shared"),
                sr.Long("physical_size"),
                sr.Long("physical_utilisation"),
                sr.Long("virtual_allocation"),
                Disks: onIt.Count(v => Kind(v) == "disk"),
                SnapshotDisks: onIt.Count(v => Kind(v) == "snapshot"),
                ChangeTrackingMetadata: onIt.Count(v => Kind(v) == "cbt_metadata"),
                Unmanaged: onIt.Count(v => Kind(v) == "unmanaged")));
        }

        var snapshots = vms.Values
            .Where(vm => vm.Bool("is_a_snapshot"))
            .Select(vm => new VmSnapshotInfo(vm.Str("name_label"), vm.Map("other_config").ContainsKey(XenTags.Session), vm.Time("snapshot_time")))
            .ToList();

        var isoStorages = srs.Where(s => s.Value.Str("content_type") == "iso" || s.Value.Str("type") is "udev" or "iso").Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var regular = vdis.Values.Where(v => Kind(v) == "disk" && !isoStorages.Contains(v.Str("SR"))).ToList();
        return new StorageState(storages, snapshots, regular.Count(v => v.Bool("cbt_enabled")), regular.Count(v => !v.Bool("cbt_enabled")));

        static string Kind(JsonObject vdi) =>
            vdi.Str("type") == "cbt_metadata" ? "cbt_metadata"
            : !vdi.Bool("managed") ? "unmanaged"
            : vdi.Bool("is_a_snapshot") ? "snapshot"
            : "disk";
    }

    public static async Task<Inventory> GetAsync(XapiSession session, PlatformInfo platform, CancellationToken cancellationToken)
    {
        var hosts = await session.GetAllRecordsAsync("host", cancellationToken).ConfigureAwait(false);
        var vms = await session.GetAllRecordsAsync("VM", cancellationToken).ConfigureAwait(false);
        var srs = await session.GetAllRecordsAsync("SR", cancellationToken).ConfigureAwait(false);
        var networks = await session.GetAllRecordsAsync("network", cancellationToken).ConfigureAwait(false);
        var vbds = await session.GetAllRecordsAsync("VBD", cancellationToken).ConfigureAwait(false);
        var vdis = await session.GetAllRecordsAsync("VDI", cancellationToken).ConfigureAwait(false);

        string Uuid(Dictionary<string, JsonObject> records, string reference) =>
            records.TryGetValue(reference, out var record) ? record.Str("uuid") : string.Empty;

        var items = new List<InventoryItem>();
        foreach (var host in hosts.Values)
        {
            items.Add(new InventoryItem(InventoryKind.Host, host.Str("uuid"), host.Str("name_label"), platform.PlatformId, new Dictionary<string, string>
            {
                ["address"] = host.Str("address"),
                ["enabled"] = host.Bool("enabled").ToString(),
            }));
        }

        foreach (var vm in vms.Values)
        {
            if (vm.Bool("is_a_template") || vm.Bool("is_control_domain") || vm.Bool("is_a_snapshot"))
            {
                continue;
            }

            var disks = vm.Set("VBDs")
                .Select(r => vbds.GetValueOrDefault(r))
                .Where(v => v is not null && v.Str("type") == "Disk" && !XapiRecord.IsNullRef(v.Str("VDI")))
                .Select(v => vdis.GetValueOrDefault(v!.Str("VDI")))
                .Where(v => v is not null)
                .ToList();
            var residentOn = vm.Str("resident_on");
            items.Add(new InventoryItem(InventoryKind.Vm, vm.Str("uuid"), vm.Str("name_label"), XapiRecord.IsNullRef(residentOn) ? null : Uuid(hosts, residentOn), new Dictionary<string, string>
            {
                ["powerState"] = vm.Str("power_state"),
                ["vcpus"] = vm.Long("VCPUs_max").ToString(CultureInfo.InvariantCulture),
                ["memoryBytes"] = vm.Long("memory_static_max").ToString(CultureInfo.InvariantCulture),
                ["diskCount"] = disks.Count.ToString(CultureInfo.InvariantCulture),
                ["diskBytes"] = disks.Sum(d => d!.Long("virtual_size")).ToString(CultureInfo.InvariantCulture),
                ["cbtEnabled"] = (disks.Count > 0 && disks.All(d => d!.Bool("cbt_enabled"))).ToString(),
                ["description"] = vm.Str("name_description"),
            }));
        }

        foreach (var (srRef, sr) in srs)
        {
            if (sr.Str("content_type") == "iso" || sr.Str("type") is "udev" or "iso")
            {
                continue;
            }

            items.Add(new InventoryItem(InventoryKind.Storage, sr.Str("uuid"), sr.Str("name_label"), platform.PlatformId, new Dictionary<string, string>
            {
                ["type"] = sr.Str("type"),
                ["shared"] = sr.Bool("shared").ToString(),
                ["physicalSize"] = sr.Long("physical_size").ToString(CultureInfo.InvariantCulture),
                ["physicalUtilisation"] = sr.Long("physical_utilisation").ToString(CultureInfo.InvariantCulture),
                ["ref"] = srRef,
            }));
        }

        foreach (var network in networks.Values)
        {
            var purposes = network.Set("purpose");
            items.Add(new InventoryItem(InventoryKind.Network, network.Str("uuid"), network.Str("name_label"), platform.PlatformId, new Dictionary<string, string>
            {
                ["bridge"] = network.Str("bridge"),
                ["mtu"] = network.Long("MTU").ToString(CultureInfo.InvariantCulture),
                ["nbd"] = purposes.Contains("nbd") ? "tls" : purposes.Contains("insecure_nbd") ? "insecure" : "off",
            }));
        }

        return new Inventory(platform, items);
    }

    public static async Task<VmDescriptor> DescribeVmAsync(XapiSession session, JsonObject vm, CancellationToken cancellationToken)
    {
        var nics = new List<VmNic>();
        foreach (var vifRef in vm.Set("VIFs"))
        {
            var vif = await session.GetRecordAsync("VIF", vifRef, cancellationToken).ConfigureAwait(false);
            var network = await session.GetRecordAsync("network", vif.Str("network"), cancellationToken).ConfigureAwait(false);
            nics.Add(new VmNic(vif.Str("device"), vif.Str("MAC"), network.Str("uuid"), network.Str("name_label"), (int)vif.Long("MTU")));
        }

        var boot = vm.Map("HVM_boot_params");
        var platformFlags = vm.Map("platform");
        return new VmDescriptor
        {
            VmId = vm.Str("uuid"),
            Name = vm.Str("name_label"),
            Description = vm.Str("name_description"),
            VCpus = (int)vm.Long("VCPUs_max"),
            MemoryBytes = vm.Long("memory_static_max"),
            Firmware = boot.GetValueOrDefault("firmware") ?? "bios",
            SecureBoot = string.Equals(platformFlags.GetValueOrDefault("secureboot"), "true", StringComparison.OrdinalIgnoreCase),
            HasVtpm = vm.Set("VTPMs").Count > 0,
            OsName = vm.Map("other_config").GetValueOrDefault("os_name"),
            Nics = nics.OrderBy(n => n.Device, StringComparer.Ordinal).ToList(),
            GenerationId = vm.Str("generation_id") is { Length: > 0 } generation ? generation : null,
        };
    }
}
