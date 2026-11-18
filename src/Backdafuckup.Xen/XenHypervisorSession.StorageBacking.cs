using Backdafuckup.Core.Arrays;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

/// <summary>
/// Which LUN holds an SR: for LVM over iSCSI or FC (LVHD) the SCSI id of its PBDs, which every host must agree on. And the
/// gate of the SRs while an array snapshot of their LUNs is taken: the same per-SR gate our VM snapshots take, so no
/// VM snapshot of ours is half done in the volume group when the array copies it.
/// </summary>
internal sealed partial class XenHypervisorSession : IStorageBackingSource, IStorageSnapshotGate
{
    public async Task<IAsyncDisposable> HoldAsync(IReadOnlyCollection<string> storageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageIds);
        return new GateHold(await Provider.SnapshotGate.EnterAsync(storageIds, cancellationToken).ConfigureAwait(false));
    }

    private sealed class GateHold(XenSnapshotGate.Lease lease) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    public async Task<IReadOnlyList<StorageBacking>> GetStorageBackingAsync(IReadOnlyCollection<string> storageIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storageIds);
        var srs = await Xapi.GetAllRecordsAsync("SR", cancellationToken).ConfigureAwait(false);
        var pbds = await Xapi.GetAllRecordsAsync("PBD", cancellationToken).ConfigureAwait(false);
        var result = new List<StorageBacking>();
        foreach (var id in storageIds)
        {
            var (srRef, sr) = srs.FirstOrDefault(s => s.Value.Str("uuid") == id);
            if (sr is null)
            {
                result.Add(new StorageBacking(id, StorageLayout.Other, [], $"SR {id} does not exist in pool {Platform.PlatformName}."));
                continue;
            }

            var type = sr.Str("type");
            if (type is not ("lvmoiscsi" or "lvmohba"))
            {
                result.Add(new StorageBacking(id, StorageLayout.Other, [], $"SR {sr.Str("name_label")} is of type {type}; only LVM over iSCSI or FC (lvmoiscsi, lvmohba) lies on an array LUN."));
                continue;
            }

            var scsiIds = pbds.Values.Where(p => p.Str("SR") == srRef).Select(p => p.Map("device_config").GetValueOrDefault("SCSIid")).ToList();
            var distinct = scsiIds.OfType<string>().Where(s => s.Length > 0).Select(Wwns.Normalize).Distinct(StringComparer.Ordinal).ToList();
            result.Add(distinct.Count == 1 && scsiIds.All(s => !string.IsNullOrEmpty(s))
                ? new StorageBacking(id, StorageLayout.Lvhd, distinct, null)
                : new StorageBacking(id, StorageLayout.Lvhd, distinct,
                    distinct.Count == 0 ? $"SR {sr.Str("name_label")} names no SCSI id in its PBDs." : $"The hosts' PBDs of SR {sr.Str("name_label")} name different SCSI ids ({string.Join(", ", distinct)})."));
        }

        return result;
    }
}
