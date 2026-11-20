using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.VSphere;

internal sealed partial class VSphereHypervisorSession : IVmStorageLocator
{
    /// <summary>The datastores of the VM's disks a backup reads (independent disks are left out, as the backup does).</summary>
    public async Task<IReadOnlyList<string>?> GetVmStorageIdsAsync(string vmNativeId, CancellationToken cancellationToken)
    {
        var vm = await VSphereInventory.FindVmAsync(Vim, vmNativeId, cancellationToken).ConfigureAwait(false);
        return vm is null
            ? null
            : [.. vm.Disks.Where(d => d.DatastoreId is not null && d.DiskMode?.StartsWith("independent", StringComparison.Ordinal) != true)
                .Select(d => d.DatastoreId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}
