namespace Backdafuckup.Lvhd;

/// <summary>A VDI of the chain as XAPI reported it before the array snapshot (leaf first): its uuid and sizes.</summary>
/// <param name="PhysicalUtilisation">XAPI's physical utilisation: on LVHD the size of the VDI's logical volume.</param>
public sealed record ExpectedVdi(string Uuid, long VirtualSize, long PhysicalUtilisation);

/// <summary>The snapshot cannot be read safely from the LUN; the reason names the logical volume or VHD.</summary>
public sealed class LvhdRefusedException(string message) : Exception(message);

/// <summary>
/// An LVHD SR on a snapshot LUN, read in user space: the volume group's newest metadata, the chain of the VDI's VHDs in
/// their logical volumes checked against what XAPI reported, and a reader through that chain. Refuses whenever the LUN
/// does not look exactly as expected (another SR, journal volumes of an operation on the chain, other formats, sizes or
/// links that differ), so that the backup falls back to reading through the hypervisor.
/// </summary>
public static class LvhdSnapshot
{
    /// <summary>
    /// Logical volumes XCP-ng's storage manager uses while it changes a chain (clone, leaf coalesce, relink, coalesce,
    /// inflate, zero) or keeps for an undo: if one names a member, the chain on the LUN may be half changed.
    /// </summary>
    public static readonly string[] JournalPrefixes = ["clone_", "leaf_", "relink_", "coalesce_", "inflate_", "zero_", "OLD_"];

    public static async Task<VhdChainReader> OpenAsync(IBlockSource lun, string srUuid, IReadOnlyList<ExpectedVdi> chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lun);
        ArgumentNullException.ThrowIfNull(chain);
        LvmVolumeGroup group;
        try
        {
            group = await Lvm2Reader.ReadAsync(lun, cancellationToken).ConfigureAwait(false);
        }
        catch (LvmFormatException ex)
        {
            throw new LvhdRefusedException($"The LUN's LVM metadata cannot be read: {ex.Message}");
        }

        var volumes = Validate(group, srUuid, chain);
        var sources = new List<IBlockSource>();
        foreach (var volume in volumes)
        {
            try
            {
                sources.Add(Lvm2Reader.Open(lun, group, volume));
            }
            catch (LvmFormatException ex)
            {
                throw new LvhdRefusedException(ex.Message);
            }
        }

        VhdChainReader reader;
        try
        {
            reader = await VhdChainReader.OpenAsync(sources, cancellationToken).ConfigureAwait(false);
        }
        catch (VhdFormatException ex)
        {
            throw new LvhdRefusedException($"The VHD chain of VDI {chain[0].Uuid} on the LUN: {ex.Message}");
        }

        if (reader.Capacity != chain[0].VirtualSize)
        {
            throw new LvhdRefusedException($"VDI {chain[0].Uuid} is {reader.Capacity} bytes on the LUN, but XAPI reported {chain[0].VirtualSize}.");
        }

        return reader;
    }

    /// <summary>The checks on the metadata alone; returns the logical volumes of the chain, leaf first.</summary>
    public static IReadOnlyList<LvmLogicalVolume> Validate(LvmVolumeGroup group, string srUuid, IReadOnlyList<ExpectedVdi> chain)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("The chain needs at least the leaf.", nameof(chain));
        }

        if (group.Name != $"VG_XenStorage-{srUuid}")
        {
            throw new LvhdRefusedException($"The LUN holds volume group {group.Name}, not the one of SR {srUuid} (VG_XenStorage-{srUuid}).");
        }

        if (group.PhysicalVolumes.Count != 1)
        {
            throw new LvhdRefusedException($"Volume group {group.Name} spans {group.PhysicalVolumes.Count} physical volumes; only an SR on one LUN is read.");
        }

        foreach (var journal in group.LogicalVolumes.Where(l => JournalPrefixes.Any(p => l.Name.StartsWith(p, StringComparison.Ordinal))))
        {
            if (chain.FirstOrDefault(v => journal.Name.Contains(v.Uuid, StringComparison.OrdinalIgnoreCase)) is { } member)
            {
                throw new LvhdRefusedException($"Logical volume {journal.Name} shows an operation of the storage manager on VDI {member.Uuid} of the chain; it is read through XAPI instead.");
            }
        }

        var volumes = new List<LvmLogicalVolume>();
        foreach (var vdi in chain)
        {
            var volume = group.Find($"VHD-{vdi.Uuid}");
            if (volume is null)
            {
                var other = group.LogicalVolumes.FirstOrDefault(l => l.Name.EndsWith(vdi.Uuid, StringComparison.OrdinalIgnoreCase));
                throw new LvhdRefusedException(other is null
                    ? $"VDI {vdi.Uuid} has no logical volume VHD-{vdi.Uuid} on the LUN."
                    : $"VDI {vdi.Uuid} is stored as {other.Name}; only VHD (VHD-…) volumes are read.");
            }

            var size = volume.ExtentCount * group.ExtentSizeBytes;
            if (size != vdi.PhysicalUtilisation)
            {
                throw new LvhdRefusedException($"Logical volume {volume.Name} is {size} bytes, but XAPI reported {vdi.PhysicalUtilisation} (inflated or deflated since?).");
            }

            volumes.Add(volume);
        }

        return volumes;
    }
}
