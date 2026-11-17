namespace Backdafuckup.Server.ScaleOut;

/// <summary>The size and free space of the volume a directory is on, and which volume that is (tests replace it).</summary>
public interface IVolumeSpace
{
    /// <summary>Total and free bytes of the volume holding <paramref name="path"/>; null when it cannot be reached.</summary>
    (long Total, long Free)? Probe(string path);

    /// <summary>The volume holding <paramref name="path"/> (drive letter or mount point), to refuse two extents on one volume.</summary>
    string? VolumeOf(string path);
}

/// <summary>Through <see cref="DriveInfo"/> on Windows and Linux.</summary>
public sealed class DriveVolumeSpace : IVolumeSpace
{
    public (long Total, long Free)? Probe(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var drive = new DriveInfo(Path.GetFullPath(path));
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The longest mount point (Linux) or drive root (Windows) that contains the path.</summary>
    public string? VolumeOf(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return DriveInfo.GetDrives()
                .Select(d => Path.TrimEndingDirectorySeparator(d.Name) + Path.DirectorySeparatorChar)
                .Where(root => full.StartsWith(root, comparison))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
