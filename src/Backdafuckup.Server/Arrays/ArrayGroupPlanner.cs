using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Arrays;

/// <summary>
/// VMs of a job run whose disks lie on the same volumes of one storage system: their VM snapshots are taken one group at a
/// time, then one array snapshot per volume serves all of them. <see cref="Part"/> of <see cref="Parts"/> when the job
/// limits the VMs per array snapshot.
/// </summary>
public sealed record ArrayGroup(Guid Id, Guid StorageSystemId, string StorageSystemName, IReadOnlyList<string> VolumeNames, IReadOnlyList<BackupJobObject> Vms, int Part, int Parts)
{
    public string Describe() =>
        $"{StorageSystemName} {string.Join(" + ", VolumeNames)}{(Parts > 1 ? $" (part {Part} of {Parts})" : string.Empty)}: {Vms.Count} VM(s) ({string.Join(", ", Vms.Select(v => v.CachedName))})";
}

/// <summary>The groups of a job run, in the order of their first VM, and the VMs no group takes (with why).</summary>
public sealed record ArrayGroupPlan(IReadOnlyList<ArrayGroup> Groups, IReadOnlyList<(BackupJobObject Vm, string Reason)> Ungrouped)
{
    public ArrayGroup? GroupOf(BackupJobObject vm) => Groups.FirstOrDefault(g => g.Vms.Contains(vm));
}

/// <summary>
/// Plans a job run's groups before any VM snapshot: where each VM's disks lie (<see cref="IVmStorageLocator"/>), which
/// turned-on volumes hold them (the mapping of <see cref="StorageSystems"/>, by the rules the snapshot itself checks:
/// turned on, not refused, the primary side of a replicated volume, one storage system, experimental readers on). A VM
/// it cannot place runs as before; the snapshot step still checks every VM.
/// </summary>
public sealed class ArrayGroupPlanner(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<ArrayGroupPlan> PlanAsync(
        IReadOnlyList<BackupJobObject> vms, IHypervisorSession hypervisor, Guid connectionId, int? maxVmsPerSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vms);
        if (hypervisor is not IVmStorageLocator locator)
        {
            return new ArrayGroupPlan([], [.. vms.Select(v => (v, "this platform cannot tell where a VM's disks lie before its snapshot"))]);
        }

        var located = new List<(BackupJobObject Vm, IReadOnlyList<string>? Storages, string? Error)>();
        foreach (var vm in vms)
        {
            try
            {
                located.Add((vm, await locator.GetVmStorageIdsAsync(vm.NativeId, cancellationToken).ConfigureAwait(false), null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                located.Add((vm, null, $"where its disks lie could not be read ({ex.Message})"));
            }
        }

        var storageIds = located.SelectMany(l => l.Storages ?? []).Distinct(StringComparer.Ordinal).ToList();
        List<StorageVolumeMapping> mappings;
        Dictionary<Guid, StorageSystemEntity> systems;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            mappings = await db.StorageVolumes.AsNoTracking()
                .Where(v => v.ConnectionId == connectionId && v.StorageNativeId != null && storageIds.Contains(v.StorageNativeId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var systemIds = mappings.Select(m => m.StorageSystemId).Distinct().ToList();
            systems = await db.StorageSystems.AsNoTracking().Where(s => systemIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
        }

        var ungrouped = new List<(BackupJobObject Vm, string Reason)>();
        var keyed = new List<(BackupJobObject Vm, string Key, Guid System, List<StorageVolumeMapping> Volumes)>();
        foreach (var (vm, storages, error) in located)
        {
            if (Place(storages, error, mappings, systems) is { } reason)
            {
                ungrouped.Add((vm, reason));
                continue;
            }

            var volumes = storages!.Select(id => Usable(mappings, id)!).DistinctBy(v => v.VolumeUid).OrderBy(v => v.VolumeUid, StringComparer.Ordinal).ToList();
            keyed.Add((vm, $"{volumes[0].StorageSystemId:N}|{string.Join(",", volumes.Select(v => v.VolumeUid))}", volumes[0].StorageSystemId, volumes));
        }

        // In the order of each group's first VM; a limit splits a group into parts of that size.
        var groups = new List<ArrayGroup>();
        foreach (var same in keyed.GroupBy(k => k.Key, StringComparer.Ordinal))
        {
            var members = same.Select(k => k.Vm).ToList();
            var size = maxVmsPerSnapshot is > 0 and var limit ? limit : members.Count;
            var parts = members.Chunk(size).ToList();
            var first = same.First();
            for (var i = 0; i < parts.Count; i++)
            {
                groups.Add(new ArrayGroup(Guid.CreateVersion7(), first.System, systems[first.System].Name, [.. first.Volumes.Select(v => v.VolumeName)], parts[i], i + 1, parts.Count));
            }
        }

        var order = vms.Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
        return new ArrayGroupPlan([.. groups.OrderBy(g => order[g.Vms[0]])], ungrouped);
    }

    /// <summary>Why a VM gets no group, or null when every disk lies on a usable volume of one storage system.</summary>
    private static string? Place(
        IReadOnlyList<string>? storages, string? error, List<StorageVolumeMapping> mappings, Dictionary<Guid, StorageSystemEntity> systems)
    {
        if (error is not null)
        {
            return error;
        }

        if (storages is null)
        {
            return "the VM was not found";
        }

        if (storages.Count == 0)
        {
            return "it has no disk to back up";
        }

        foreach (var id in storages)
        {
            if (Usable(mappings, id) is null)
            {
                var rows = mappings.Where(m => m.StorageNativeId == id).ToList();
                var name = rows.FirstOrDefault()?.StorageName ?? id;
                return rows.Count == 0 ? $"storage {name} lies on no volume of a storage system"
                    : rows.All(r => !r.Enabled) ? $"the volume of storage {name} ({string.Join(", ", rows.Select(r => r.VolumeName))}) is not turned on"
                    : rows.Any(r => r.Refusal is not null) ? $"storage {name}: {rows.First(r => r.Refusal is not null).Refusal}"
                    : $"storage {name} is turned on only on the secondary side of its replicated volume";
            }
        }

        var used = storages.Select(id => Usable(mappings, id)!.StorageSystemId).Distinct().ToList();
        if (used.Count > 1)
        {
            return "its disks lie on volumes of more than one storage system";
        }

        var system = systems[used[0]];
        return system.Options.ExperimentalReaders ? null : $"the experimental readers of storage system {system.Name} are off";
    }

    /// <summary>The volume a storage can be read from: turned on, not refused, preferably the primary side; null if none.</summary>
    private static StorageVolumeMapping? Usable(List<StorageVolumeMapping> mappings, string storageId) =>
        mappings.Where(m => m.StorageNativeId == storageId && m.Enabled && m.Refusal is null && m.ReplicationRole != ArrayReplicationRoles.Secondary)
            .OrderBy(m => m.ReplicationRole == ArrayReplicationRoles.Primary ? 0 : 1).FirstOrDefault();
}
