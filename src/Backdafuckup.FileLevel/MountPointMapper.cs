using System.Text;
using DiscUtils.Registry;
using DiscUtils.Streams;

namespace Backdafuckup.FileLevel;

/// <summary>
/// Finds drive letters and mount points from the guest's own configuration in the backup, so it also works for
/// restore points taken without the guest agent: SYSTEM\MountedDevices for Windows, /etc/fstab for Linux.
/// </summary>
internal static class MountPointMapper
{
    private const string WindowsSystemHive = "Windows/System32/config/SYSTEM";
    private const int MaxFstabBytes = 1024 * 1024;

    public static List<GuestVolume> Map(GuestImage image, List<GuestVolume> volumes, List<string> warnings)
    {
        var result = volumes.ToList();
        foreach (var volume in volumes.Where(v => v.Readable && v.FileSystem == "NTFS"))
        {
            if (TryMapWindows(image, volume, result, warnings))
            {
                break;
            }
        }

        foreach (var volume in volumes.Where(v => v.Readable && v.FileSystem is "ext" or "XFS"))
        {
            if (TryMapLinux(image, volume, result, warnings))
            {
                break;
            }
        }

        return result;
    }

    private static bool TryMapWindows(GuestImage image, GuestVolume system, List<GuestVolume> volumes, List<string> warnings)
    {
        var fs = image.OpenFileSystem(system.Id);
        var hivePath = GuestPath.Parse(WindowsSystemHive);
        try
        {
            if (fs.Stat(hivePath).Kind != GuestEntryKind.File)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            using var stream = fs.OpenRead(hivePath);
            using var hive = new RegistryHive(stream, Ownership.None);
            var devices = hive.Root.OpenSubKey("MountedDevices");
            if (devices is null)
            {
                return false;
            }

            foreach (var name in devices.GetValueNames())
            {
                if (!name.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase) || name.Length != 14 || name[13] != ':')
                {
                    continue;
                }

                if (devices.GetValue(name) is not byte[] value)
                {
                    continue;
                }

                var letter = char.ToUpperInvariant(name[12]) + ":";
                var index = -1;
                if (value.Length == 12)
                {
                    var signature = BitConverter.ToUInt32(value, 0);
                    var offset = BitConverter.ToInt64(value, 4);
                    index = volumes.FindIndex(v => v.MbrDiskSignature == signature && v.StartOffset == offset);
                }
                else if (value.Length >= 24 && Encoding.ASCII.GetString(value, 0, 8) == "DMIO:ID:")
                {
                    var guid = new Guid(value.AsSpan(8, 16));
                    index = volumes.FindIndex(v => v.PartitionGuid == guid);
                }

                if (index >= 0 && volumes[index].MountPoint is null)
                {
                    volumes[index] = volumes[index] with { MountPoint = letter };
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException)
        {
            warnings.Add($"The drive letters could not be read from {WindowsSystemHive} on volume {GuestImage.Describe(system)}: {ex.Message}");
            return false;
        }
    }

    private static bool TryMapLinux(GuestImage image, GuestVolume root, List<GuestVolume> volumes, List<string> warnings)
    {
        var fs = image.OpenFileSystem(root.Id);
        var fstabPath = GuestPath.Parse("etc/fstab");
        string text;
        try
        {
            var entry = fs.Stat(fstabPath);
            if (entry.Kind != GuestEntryKind.File || entry.Size > MaxFstabBytes)
            {
                return false;
            }

            using var stream = fs.OpenRead(fstabPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var rootIndex = volumes.FindIndex(v => v.Id == root.Id);
        var rootMapped = false;
        foreach (var (spec, mountPoint) in ParseFstab(text))
        {
            var index = Resolve(spec, volumes);
            if (index < 0)
            {
                continue;
            }

            rootMapped |= index == rootIndex && mountPoint == "/";
            if (volumes[index].MountPoint is null)
            {
                volumes[index] = volumes[index] with { MountPoint = mountPoint };
            }
        }

        if (!rootMapped && volumes[rootIndex].MountPoint is null)
        {
            volumes[rootIndex] = volumes[rootIndex] with { MountPoint = "/" };
        }

        if (volumes.Count(v => v.MountPoint == "/") > 1)
        {
            warnings.Add("More than one volume looks like a Linux root file system; mount points come from the first /etc/fstab found.");
        }

        return true;
    }

    internal static IEnumerable<(string Spec, string MountPoint)> ParseFstab(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields[1].StartsWith('/'))
            {
                continue;
            }

            yield return (fields[0], Unescape(fields[1]));
        }
    }

    private static int Resolve(string spec, List<GuestVolume> volumes)
    {
        static string? After(string value, params string[] prefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return value[prefix.Length..];
                }
            }

            return null;
        }

        if (After(spec, "UUID=", "/dev/disk/by-uuid/") is { } uuid)
        {
            return volumes.FindIndex(v => string.Equals(v.FileSystemUuid, uuid.Trim('"'), StringComparison.OrdinalIgnoreCase));
        }

        if (After(spec, "LABEL=", "/dev/disk/by-label/") is { } label)
        {
            return volumes.FindIndex(v => v.Label == Unescape(label.Trim('"')));
        }

        if (After(spec, "PARTUUID=", "/dev/disk/by-partuuid/") is { } partUuid && Guid.TryParse(partUuid.Trim('"'), out var partGuid))
        {
            return volumes.FindIndex(v => v.PartitionGuid == partGuid);
        }

        if (After(spec, "/dev/mapper/") is { } mapper)
        {
            // VG-LV with "-" inside names written as "--".
            var split = mapper.Replace("--", "\u0001").Split('-');
            if (split.Length == 2)
            {
                var lv = split[1].Replace('\u0001', '-');
                return UniqueLv(volumes, lv);
            }

            return -1;
        }

        if (spec.StartsWith("/dev/", StringComparison.Ordinal) && spec.Count(c => c == '/') == 3)
        {
            return UniqueLv(volumes, spec[(spec.LastIndexOf('/') + 1)..]);
        }

        return -1;
    }

    private static int UniqueLv(List<GuestVolume> volumes, string lv)
    {
        var matches = volumes.Select((v, i) => (v, i)).Where(x => x.v.LvmName == lv).ToList();
        return matches.Count == 1 ? matches[0].i : -1;
    }

    /// <summary>fstab writes spaces and tabs in paths as \040 and \011.</summary>
    private static string Unescape(string value) => value.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\");
}
