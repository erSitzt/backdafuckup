namespace Backdafuckup.Core.Hypervisor;

/// <summary>
/// Where a VM's disks lie, read without a snapshot: a job with backup from storage snapshots groups its VMs by the array
/// volumes they share before it starts, so each volume is snapshotted once for all of them.
/// </summary>
public interface IVmStorageLocator
{
    /// <summary>
    /// The storage ids (XCP-ng: SR uuids; vSphere: datastore ids) of the disks a backup of the VM reads, sorted; disks the
    /// backup leaves out (marked [NOBAK], independent) are not counted. Null when the VM does not exist.
    /// </summary>
    Task<IReadOnlyList<string>?> GetVmStorageIdsAsync(string vmNativeId, CancellationToken cancellationToken);
}
