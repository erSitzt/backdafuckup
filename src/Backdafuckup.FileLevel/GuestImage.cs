using System.Text;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using DiscUtils.Xfs;

namespace Backdafuckup.FileLevel;

/// <summary>
/// The disks of one restore point opened for reading: partitions (MBR/GPT), LVM logical volumes (also across disks)
/// and their file systems. Not thread-safe: every thread (browser, download, restore) opens its own instance on its
/// own streams. Discovery is deterministic, so volume ids match between instances.
/// </summary>
public sealed class GuestImage : IDisposable
{
    private const long MinimumInterestingSize = 1024 * 1024;

    private static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    private static readonly Guid BiosBoot = new("21686148-6449-6E6F-744E-656564454649");
    private static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    private static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    private static readonly Guid StorageSpaces = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");

    private readonly List<VirtualDisk> _disks = [];
    private readonly Dictionary<string, (LogicalVolumeInfo Info, GuestVolume Volume)> _volumes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGuestFileSystem> _open = new(StringComparer.Ordinal);
    private bool _describe = true;

    static GuestImage()
    {
        DiscUtils.Core.Formats.Register();
        DiscUtils.Ntfs.Formats.Register();
        DiscUtils.Fat.Formats.Register();
        DiscUtils.Ext.Formats.Register();
        DiscUtils.Xfs.Formats.Register();
        DiscUtils.Lvm.Formats.Register();
    }

    private GuestImage()
    {
    }

    public IReadOnlyList<GuestVolume> Volumes { get; private set; } = [];

    /// <summary>Problems that concern the whole image (e.g. LVM metadata that could not be read).</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>
    /// Opens the disks and finds all volumes. With <paramref name="describe"/> false, file systems are not probed and
    /// mount points are not read: faster for a download or restore that only opens one known volume id.
    /// </summary>
    public static GuestImage Open(IReadOnlyList<GuestDiskSource> disks, bool describe = true)
    {
        ArgumentNullException.ThrowIfNull(disks);
        var image = new GuestImage { _describe = describe };
        try
        {
            image.Discover(disks);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>The file system of a readable volume (one instance per volume and image).</summary>
    public IGuestFileSystem OpenFileSystem(string volumeId)
    {
        if (_open.TryGetValue(volumeId, out var open))
        {
            return open;
        }

        if (!_volumes.TryGetValue(volumeId, out var volume))
        {
            throw new KeyNotFoundException($"The restore point has no volume '{volumeId}'.");
        }

        if (!volume.Volume.Readable)
        {
            throw new IOException($"Volume {Describe(volume.Volume)} cannot be read: {volume.Volume.Reason}");
        }

        var fs = OpenFileSystem(volume.Info) ?? throw new IOException($"Volume {Describe(volume.Volume)} has no readable file system.");
        if (fs is ISizeBoundedFileSystem bounded)
        {
            bounded.MaxFileSize = FileSizeBound.For(volume.Volume.Length);
        }

        _open[volumeId] = fs;
        return fs;
    }

    public void Dispose()
    {
        foreach (var fs in _open.Values)
        {
            fs.Dispose();
        }

        _open.Clear();
        foreach (var disk in _disks)
        {
            disk.Dispose();
        }

        _disks.Clear();
    }

    internal static string Describe(GuestVolume volume) =>
        volume.MountPoint is { } mount ? $"{mount} ({volume.Id})" : volume.Label is { Length: > 0 } label ? $"'{label}' ({volume.Id})" : volume.Id;

    private void Discover(IReadOnlyList<GuestDiskSource> sources)
    {
        var manager = new VolumeManager();
        var physical = new Dictionary<string, (GuestDiskSource Source, VirtualDisk Disk, PhysicalVolumeInfo Volume)>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var disk = new DiscUtils.Raw.Disk(source.Open(), Ownership.Dispose);
            _disks.Add(disk);
            manager.AddDisk(disk);
            foreach (var pv in VolumeManager.GetPhysicalVolumes(disk))
            {
                physical[pv.Identity] = (source, disk, pv);
            }
        }

        var warnings = new List<string>();
        LogicalVolumeInfo[] logical;
        try
        {
            logical = manager.GetLogicalVolumes();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or ArgumentException or NotImplementedException)
        {
            // Typically LVM layouts the reader does not support (striped, thin): fall back to the plain partitions.
            warnings.Add($"Logical volumes could not be read ({ex.Message}); only partitions are shown.");
            logical = [.. physical.Values.Select(p => new LogicalVolumeInfo(Guid.Empty, p.Volume, p.Volume.Open, p.Volume.Length, p.Volume.BiosType, LogicalVolumeStatus.Healthy, p.Volume.Partition?.TypeAsString ?? "partition"))];
        }

        var volumes = new List<GuestVolume>();
        foreach (var info in logical)
        {
            var volume = Classify(info, physical);
            if (volume is null)
            {
                continue;
            }

            _volumes[volume.Id] = (info, volume);
            volumes.Add(volume);
        }

        var mapped = _describe ? MountPointMapper.Map(this, volumes, warnings) : volumes;
        foreach (var volume in mapped)
        {
            _volumes[volume.Id] = (_volumes[volume.Id].Info, volume);
        }

        Volumes = mapped;
        Warnings = warnings;
    }

    private GuestVolume? Classify(LogicalVolumeInfo info, Dictionary<string, (GuestDiskSource Source, VirtualDisk Disk, PhysicalVolumeInfo Volume)> physical)
    {
        string id;
        GuestVolume volume;
        var pv = info.PhysicalVolume;
        if (pv is not null && physical.TryGetValue(pv.Identity, out var owner))
        {
            var partitionType = pv.Partition?.GuidType ?? Guid.Empty;
            if (partitionType == MicrosoftReserved || partitionType == BiosBoot || partitionType == LdmMetadata)
            {
                return null;
            }

            id = $"{owner.Source.Key}:{pv.PhysicalStartSector}";
            volume = new GuestVolume
            {
                Id = id,
                DiskKey = owner.Source.Key,
                DiskName = owner.Source.Name,
                StartOffset = pv.PhysicalStartSector * owner.Disk.SectorSize,
                Length = info.Length,
                PartitionType = info.TypeAsString,
                PartitionGuid = pv.VolumeType == PhysicalVolumeType.GptPartition ? pv.PartitionIdentity : null,
                MbrDiskSignature = pv.VolumeType == PhysicalVolumeType.BiosPartition ? unchecked((uint)pv.DiskSignature) : null,
            };
            if (partitionType == StorageSpaces)
            {
                return volume with { Reason = "Storage Spaces (not supported)" };
            }

            if (partitionType == LdmData || pv.BiosType == 0x42)
            {
                return volume with { Reason = "Windows dynamic disk (not supported)" };
            }
        }
        else
        {
            var type = info.TypeAsString ?? string.Empty;
            id = "lvm:" + info.Identity;
            volume = new GuestVolume
            {
                Id = id,
                Length = info.Length,
                PartitionType = type,
                LvmName = type.StartsWith("Linux LVM ", StringComparison.Ordinal) ? type["Linux LVM ".Length..] : null,
            };
        }

        if (!_describe)
        {
            return volume.Reason is null ? volume with { Readable = true } : volume;
        }

        try
        {
            using var fs = OpenFileSystem(info);
            if (fs is null)
            {
                var reason = ProbeUnreadable(info);
                if (reason is null || (reason == Unknown && info.Length < MinimumInterestingSize))
                {
                    return null; // LVM physical volume (shown as its logical volumes) or a tiny helper partition
                }

                return volume with { Reason = reason };
            }

            return Describe(volume, fs);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or ArgumentException or InvalidOperationException or NotImplementedException or IndexOutOfRangeException or OverflowException)
        {
            return volume with { Reason = $"the file system cannot be read: {ex.Message}" };
        }
    }

    private const string Unknown = "no known file system (empty, unformatted or unknown)";

    private static GuestVolume Describe(GuestVolume volume, IGuestFileSystem fs) => fs switch
    {
        NtfsGuestFileSystem ntfs => volume with { FileSystem = "NTFS", Readable = true, Label = ntfs.Label, VolumeSerial = ntfs.Serial },
        UnixGuestFileSystem unix => volume with { FileSystem = unix.Type, Readable = true, Label = unix.Label, FileSystemUuid = unix.VolumeUuid },
        FatGuestFileSystem fat => volume with { FileSystem = "FAT", Readable = true, Label = fat.Label },
        _ => volume with { FileSystem = fs.Type, Readable = true },
    };

    private static IGuestFileSystem? OpenFileSystem(LogicalVolumeInfo info)
    {
        var detected = FileSystemManager.DetectFileSystems(info);
        if (detected.Count == 0)
        {
            return null;
        }

        var fs = detected[0].Open(info);
        return fs switch
        {
            NtfsFileSystem ntfs => new NtfsGuestFileSystem(ntfs),
            ExtFileSystem ext => new UnixGuestFileSystem(ext),
            XfsFileSystem xfs => new UnixGuestFileSystem(xfs),
            DiscUtils.Fat.FatFileSystem fat => new FatGuestFileSystem(fat),
            _ => Unsupported(fs),
        };

        static IGuestFileSystem Unsupported(DiscFileSystem fs)
        {
            var name = fs.FriendlyName;
            fs.Dispose();
            throw new NotSupportedException($"{name} is not supported for file-level restore");
        }
    }

    /// <summary>Known signatures of volumes we cannot read; null for an LVM physical volume (not shown).</summary>
    private static string? ProbeUnreadable(LogicalVolumeInfo info)
    {
        var head = new byte[0x10100];
        int read;
        using (var stream = info.Open())
        {
            read = stream.ReadMaximum(head, 0, head.Length);
        }

        bool At(int offset, string text) => read >= offset + text.Length && head.AsSpan(offset, text.Length).SequenceEqual(Encoding.ASCII.GetBytes(text));

        if (At(512, "LABELONE") || At(0, "LABELONE"))
        {
            return null;
        }

        if (At(3, "-FVE-FS-"))
        {
            return "BitLocker-encrypted (not supported)";
        }

        if (read >= 6 && head.AsSpan(0, 4).SequenceEqual("LUKS"u8) && head[4] == 0xBA && head[5] == 0xBE)
        {
            return "LUKS-encrypted (not supported)";
        }

        if (At(3, "ReFS"))
        {
            return "ReFS (not supported)";
        }

        if (At(0x10040, "_BHRfS_M"))
        {
            return "Btrfs (not supported)";
        }

        if (At(4086, "SWAPSPACE2") || At(4086, "SWAP-SPACE"))
        {
            return "swap space";
        }

        return Unknown;
    }
}
