using Backdafuckup.Core.Arrays;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>Which LUN holds a datastore: the NAA ids of its VMFS extents (exactly one for a backup from array snapshots).</summary>
internal sealed partial class VSphereHypervisorSession : IStorageBackingSource
{
    public async Task<IReadOnlyList<StorageBacking>> GetStorageBackingAsync(IReadOnlyCollection<string> storageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageIds);
        var result = new List<StorageBacking>();
        var refs = storageIds.Select(id => new ManagedObjectReference("Datastore", id)).ToList();
        var rows = refs.Count == 0 ? [] : await Vim.RetrievePropertiesAsync(refs, ["summary.name", "summary.type", "info"], cancellationToken).ConfigureAwait(false);
        foreach (var id in storageIds)
        {
            var row = rows.FirstOrDefault(r => r.Obj.Value == id);
            var name = row?.Str("summary.name") ?? id;
            if (row is null)
            {
                result.Add(new StorageBacking(id, StorageLayout.Other, [], $"Datastore {id} does not exist in vCenter {Vim.Endpoint}."));
                continue;
            }

            if (row.Str("summary.type") != "VMFS" || row["info"]?.Child("vmfs") is not { } vmfs)
            {
                result.Add(new StorageBacking(id, StorageLayout.Other, [], $"Datastore {name} is {row.Str("summary.type")}; only VMFS datastores lie on array LUNs."));
                continue;
            }

            var layout = vmfs.Int("majorVersion") switch
            {
                6 => StorageLayout.Vmfs6,
                5 => StorageLayout.Vmfs5,
                _ => StorageLayout.Other,
            };
            var luns = vmfs.Children("extent").Select(e => e.Str("diskName")).OfType<string>().Select(Wwns.Normalize).Distinct(StringComparer.Ordinal).ToList();
            result.Add(new StorageBacking(id, layout, luns, luns.Count switch
            {
                0 => $"Datastore {name} names no extent.",
                1 => null,
                _ => $"Datastore {name} spans {luns.Count} LUNs; a backup from array snapshots needs a datastore on one LUN.",
            }));
        }

        return result;
    }
}
