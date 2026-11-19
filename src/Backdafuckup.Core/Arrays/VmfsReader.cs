using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Core.Arrays;

/// <summary>
/// Reads the virtual disks of a VMFS6 datastore straight from a snapshot LUN, without ESXi (an experimental reader, opt-in
/// per storage system and always verified). Implemented with OpenVmfs; a build without it has none, and VMs on VMFS take
/// the normal path.
/// </summary>
public interface IVmfsReader
{
    /// <summary>Opens the datastore on <paramref name="lun"/>; throws <see cref="VmfsRefusedException"/> when it cannot or will not.</summary>
    Task<IVmfsVolume> OpenVolumeAsync(IDiskReader lun, string where, CancellationToken cancellationToken);
}

/// <summary>A VMFS datastore on a snapshot LUN. Disks opened from it stay readable after it is disposed.</summary>
public interface IVmfsVolume : IAsyncDisposable
{
    /// <summary>The on-disk format, e.g. "VMFS 6.82": a new one starts verify mode again.</summary>
    string FormatVersion { get; }

    /// <summary>The datastore's label as written on the volume.</summary>
    string Label { get; }

    /// <summary>
    /// Opens a disk by its descriptor's path in the datastore ("vm1/vm1.vmdk", the disk under the VM snapshot). Refuses a disk
    /// a host had open for writing when the array snapshot was taken (<see cref="VmfsRefusalKinds.InUse"/>).
    /// </summary>
    Task<IVmfsDisk> OpenDiskAsync(string descriptorPath, CancellationToken cancellationToken);
}

/// <summary>A virtual disk read from the volume: zeros outside <see cref="AllocatedExtents"/>.</summary>
public interface IVmfsDisk : IDiskReader
{
    IReadOnlyList<DiskExtent> AllocatedExtents { get; }
}

/// <summary>Why the VMFS reader did not open a volume or disk (a fallback reason, never a failed backup).</summary>
public static class VmfsRefusalKinds
{
    /// <summary>Not verified, so not read: VMFS5, VMFS-L, RDM, encryption, an unknown layout.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>A host was writing to the disk when the array snapshot was taken (no VM snapshot over it).</summary>
    public const string InUse = "in_use";

    public const string NotFound = "not_found";

    /// <summary>The metadata contradicts itself.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>The LUN could not be read.</summary>
    public const string Io = "io";

    public const string Other = "other";
}

/// <summary>The VMFS reader will not read this volume or disk; <see cref="Kind"/> is one of <see cref="VmfsRefusalKinds"/>.</summary>
public sealed class VmfsRefusedException(string message, string kind, Exception? inner = null) : Exception(message, inner)
{
    public string Kind { get; } = kind;
}
