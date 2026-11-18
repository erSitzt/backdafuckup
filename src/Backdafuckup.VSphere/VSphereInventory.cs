using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>A disk of a VM as its configuration names it.</summary>
internal sealed record VSphereDisk(int Key, string Label, long CapacityBytes, string FileName, string? DatastoreId, string? Uuid, string? DiskMode, string? ChangeId);

/// <summary>A VM with what backup, restore and inventory need from it.</summary>
internal sealed record VSphereVm(
    ManagedObjectReference Ref, string Name, string InstanceUuid, string? BiosUuid, string PowerState, string? HostId, int VCpus, long MemoryBytes,
    bool ChangeTracking, string? Annotation, string? GuestName, IReadOnlyList<VSphereDisk> Disks);

/// <summary>Reads the vCenter's inventory (hosts, datastores, networks, VMs) and the state of its datastores.</summary>
internal static class VSphereInventory
{
    /// <summary>Description prefix of the snapshots this product takes (also how leftovers are recognised).</summary>
    public const string SnapshotDescriptionPrefix = "backdafuckup ";

    public const string SnapshotName = "backdafuckup temporary snapshot";

    private static readonly string[] VmPaths =
    [
        "name", "config.instanceUuid", "config.uuid", "config.template", "runtime.powerState", "runtime.host", "config.hardware.numCPU",
        "config.hardware.memoryMB", "config.hardware.device", "config.changeTrackingEnabled", "config.annotation", "config.guestFullName",
    ];

    public static async Task<Inventory> GetAsync(VimSession session, PlatformInfo platform, CancellationToken cancellationToken)
    {
        var items = new List<InventoryItem>();
        var clusters = (await session.RetrieveAllAsync("ComputeResource", ["name"], cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Obj.Value, c => c.Str("name") ?? c.Obj.Value, StringComparer.Ordinal);
        foreach (var cluster in (await session.RetrieveAllAsync("ClusterComputeResource", ["name"], cancellationToken).ConfigureAwait(false)))
        {
            clusters[cluster.Obj.Value] = cluster.Str("name") ?? cluster.Obj.Value;
        }

        var hosts = await session.RetrieveAllAsync("HostSystem", ["name", "parent", "runtime.connectionState", "runtime.inMaintenanceMode", "config.product.fullName"], cancellationToken)
            .ConfigureAwait(false);
        var hostNames = hosts.ToDictionary(h => h.Obj.Value, h => h.Str("name") ?? h.Obj.Value, StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            items.Add(new InventoryItem(InventoryKind.Host, host.Obj.Value, hostNames[host.Obj.Value], platform.PlatformId, Properties(
                ("address", host.Str("name")),
                ("enabled", (host.Str("runtime.connectionState") == "connected" && host.Str("runtime.inMaintenanceMode") != "true").ToString()),
                ("connectionState", host.Str("runtime.connectionState")),
                ("maintenance", host.Str("runtime.inMaintenanceMode")),
                ("version", host.Str("config.product.fullName")),
                ("cluster", host["parent"]?.AsRef() is { } parent ? clusters.GetValueOrDefault(parent.Value) : null))));
        }

        foreach (var datastore in await DatastoresAsync(session, cancellationToken).ConfigureAwait(false))
        {
            items.Add(new InventoryItem(InventoryKind.Storage, datastore.Id, datastore.Name, platform.PlatformId, Properties(
                ("type", datastore.Type),
                ("shared", datastore.Shared.ToString()),
                ("physicalSize", datastore.SizeBytes.ToString(CultureInfo.InvariantCulture)),
                ("physicalUtilisation", datastore.UsedBytes.ToString(CultureInfo.InvariantCulture)),
                ("accessible", datastore.Accessible.ToString()))));
        }

        foreach (var type in new[] { "Network", "DistributedVirtualPortgroup", "OpaqueNetwork" })
        {
            foreach (var network in await session.RetrieveAllAsync(type, ["name"], cancellationToken).ConfigureAwait(false))
            {
                if (items.Any(i => i.Kind == InventoryKind.Network && i.NativeId == network.Obj.Value))
                {
                    continue;
                }

                items.Add(new InventoryItem(InventoryKind.Network, network.Obj.Value, network.Str("name") ?? network.Obj.Value, platform.PlatformId, Properties(
                    ("type", network.Obj.Type))));
            }
        }

        foreach (var vm in await VmsAsync(session, cancellationToken).ConfigureAwait(false))
        {
            items.Add(new InventoryItem(InventoryKind.Vm, vm.InstanceUuid, vm.Name, vm.HostId, Properties(
                ("powerState", vm.PowerState),
                ("vcpus", vm.VCpus.ToString(CultureInfo.InvariantCulture)),
                ("memoryBytes", vm.MemoryBytes.ToString(CultureInfo.InvariantCulture)),
                ("diskCount", vm.Disks.Count.ToString(CultureInfo.InvariantCulture)),
                ("diskBytes", vm.Disks.Sum(d => d.CapacityBytes).ToString(CultureInfo.InvariantCulture)),
                ("cbtEnabled", vm.ChangeTracking.ToString()),
                ("description", vm.Annotation),
                ("guestOs", vm.GuestName),
                ("biosUuid", vm.BiosUuid),
                ("moref", vm.Ref.Value),
                ("host", vm.HostId is { } host ? hostNames.GetValueOrDefault(host) : null))));
        }

        return new Inventory(platform, items);
    }

    public static async Task<StorageState> GetStorageStateAsync(VimSession session, CancellationToken cancellationToken)
    {
        var datastores = await DatastoresAsync(session, cancellationToken).ConfigureAwait(false);
        var rows = await session.RetrieveAllAsync("VirtualMachine", ["name", "config.template", "config.hardware.device", "config.changeTrackingEnabled", "snapshot"], cancellationToken)
            .ConfigureAwait(false);
        var disksPerDatastore = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        var snapshots = new List<VmSnapshotInfo>();
        int tracked = 0, untracked = 0;
        foreach (var row in rows)
        {
            var disks = Disks(row["config.hardware.device"]);
            foreach (var disk in disks.Where(d => d.DatastoreId is not null))
            {
                var (count, bytes) = disksPerDatastore.GetValueOrDefault(disk.DatastoreId!);
                disksPerDatastore[disk.DatastoreId!] = (count + 1, bytes + disk.CapacityBytes);
            }

            if (row.Str("config.template") != "true")
            {
                if (row.Str("config.changeTrackingEnabled") == "true")
                {
                    tracked += disks.Count;
                }
                else
                {
                    untracked += disks.Count;
                }
            }

            snapshots.AddRange(Snapshots(row["snapshot"]).Select(s => new VmSnapshotInfo($"{row.Str("name")}: {s.Name}", s.Ours, s.Created)));
        }

        var usage = datastores.Select(d =>
        {
            var (count, bytes) = disksPerDatastore.GetValueOrDefault(d.Id);
            return new StorageUsage(d.Id, d.Name, d.Type, d.Shared, d.SizeBytes, d.UsedBytes, bytes, count, 0, 0, 0);
        }).ToList();
        return new StorageState(usage, snapshots, tracked, untracked);
    }

    public static async Task<IReadOnlyList<VSphereVm>> VmsAsync(VimSession session, CancellationToken cancellationToken, ManagedObjectReference? container = null)
    {
        var rows = await session.RetrieveAllAsync("VirtualMachine", VmPaths, cancellationToken, container).ConfigureAwait(false);
        return [.. rows.Select(ParseVm).OfType<VSphereVm>()];
    }

    public static async Task<VSphereVm?> FindVmAsync(VimSession session, string instanceUuid, CancellationToken cancellationToken)
    {
        if (session.Content.SearchIndex is not { } searchIndex)
        {
            return null;
        }

        var found = await session.InvokeAsync("FindByUuid", searchIndex, cancellationToken,
            VimXml.E("uuid", instanceUuid), VimXml.E("vmSearch", "true"), VimXml.E("instanceUuid", "true")).ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }

        var row = (await session.RetrievePropertiesAsync([found.AsRef()], VmPaths, cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        return row is null ? null : ParseVm(row);
    }

    /// <summary>The pins of all ESXi hosts, read over the authenticated vCenter session (<c>HostSystem.config.certificate</c>).</summary>
    public static async Task<List<PinnedCertificate>> GetHostPinsAsync(VimSession session, CancellationToken cancellationToken)
    {
        var pins = new List<PinnedCertificate>();
        foreach (var host in await session.RetrieveAllAsync("HostSystem", ["name", "config.certificate"], cancellationToken).ConfigureAwait(false))
        {
            if (host.Str("name") is not { } name || host["config.certificate"] is not { } certificate)
            {
                continue;
            }

            try
            {
                using var parsed = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certificate.Bytes()));
                pins.Add(CertificatePinning.ToPin(name, parsed));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A certificate that cannot be parsed: the host is not pinned (only file transfers to guests need it).
            }
        }

        return pins;
    }

    internal static IReadOnlyList<VSphereDisk> Disks(VimObject? devices)
    {
        if (devices is null)
        {
            return [];
        }

        var disks = new List<VSphereDisk>();
        foreach (var device in devices.Items.Where(d => d.Type == "VirtualDisk"))
        {
            var backing = device.Child("backing");
            disks.Add(new VSphereDisk(
                device.Int("key") ?? 0,
                device.Child("deviceInfo")?.Str("label") ?? $"disk {device.Int("key")}",
                device.Long("capacityInBytes") ?? (device.Long("capacityInKB") ?? 0) * 1024,
                backing?.Str("fileName") ?? string.Empty,
                backing?.Ref("datastore")?.Value,
                backing?.Str("uuid"),
                backing?.Str("diskMode"),
                backing?.Str("changeId")));
        }

        return disks;
    }

    private static VSphereVm? ParseVm(VimObjectContent row)
    {
        // Templates are not backed up as VMs; a VM without a configuration (orphaned, inaccessible) cannot be.
        if (row.Str("config.template") == "true" || row.Str("config.instanceUuid") is not { Length: > 0 } instanceUuid)
        {
            return null;
        }

        return new VSphereVm(
            row.Obj,
            row.Str("name") ?? row.Obj.Value,
            instanceUuid,
            row.Str("config.uuid"),
            row.Str("runtime.powerState") ?? "unknown",
            row["runtime.host"]?.AsRef().Value,
            int.TryParse(row.Str("config.hardware.numCPU"), out var cpus) ? cpus : 0,
            (long.TryParse(row.Str("config.hardware.memoryMB"), out var mb) ? mb : 0) * 1024 * 1024,
            row.Str("config.changeTrackingEnabled") == "true",
            row.Str("config.annotation"),
            row.Str("config.guestFullName"),
            Disks(row["config.hardware.device"]));
    }

    private static async Task<List<(string Id, string Name, string Type, bool Shared, long SizeBytes, long UsedBytes, bool Accessible)>> DatastoresAsync(
        VimSession session, CancellationToken cancellationToken)
    {
        var rows = await session.RetrieveAllAsync("Datastore", ["summary.name", "summary.type", "summary.multipleHostAccess", "summary.capacity", "summary.freeSpace", "summary.accessible"], cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(d =>
        {
            var capacity = long.TryParse(d.Str("summary.capacity"), out var c) ? c : 0;
            var free = long.TryParse(d.Str("summary.freeSpace"), out var f) ? f : 0;
            return (d.Obj.Value, d.Str("summary.name") ?? d.Obj.Value, d.Str("summary.type") ?? "unknown", d.Str("summary.multipleHostAccess") == "true", capacity, capacity - free,
                d.Str("summary.accessible") != "false");
        })];
    }

    private static IEnumerable<(string Name, bool Ours, DateTimeOffset? Created)> Snapshots(VimObject? info)
    {
        if (info is null)
        {
            yield break;
        }

        var pending = new Stack<VimObject>(info.Children("rootSnapshotList"));
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            var name = node.Str("name") ?? string.Empty;
            var description = node.Str("description") ?? string.Empty;
            yield return (name, name == SnapshotName || description.StartsWith(SnapshotDescriptionPrefix, StringComparison.Ordinal), node.Time("createTime"));
            foreach (var child in node.Children("childSnapshotList"))
            {
                pending.Push(child);
            }
        }
    }

    private static Dictionary<string, string> Properties(params (string Key, string? Value)[] entries) =>
        entries.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
}
