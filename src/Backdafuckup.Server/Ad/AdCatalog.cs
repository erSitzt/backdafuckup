using Backdafuckup.FileLevel;
using Backdafuckup.Server.Sql;
using DiscUtils.Registry;
using DiscUtils.Streams;

namespace Backdafuckup.Server.Ad;

/// <summary>A file of a domain controller's directory database in a restore point.</summary>
/// <param name="WindowsPath">Its path in the guest, e.g. <c>C:\Windows\NTDS\ntds.dit</c>.</param>
/// <param name="Volume">The restore point's volume id.</param>
public sealed record AdDatabaseFile(string WindowsPath, string Volume, GuestPath Path, long Size)
{
    public string Name => WindowsPath[(WindowsPath.LastIndexOf('\\') + 1)..];
}

/// <summary>
/// Finds the directory database of a domain controller in a restore point: ntds.dit, the checkpoint (edb.chk) and the
/// transaction logs (edb*.log). The paths come from the guest metadata (the NTDS writer), else from the backup's SYSTEM
/// hive (<c>Services\NTDS\Parameters</c>), else the default <c>C:\Windows\NTDS</c>.
/// </summary>
public static class AdCatalog
{
    private const string SystemHive = "Windows/System32/config/SYSTEM";
    private const string DefaultFolder = @"C:\Windows\NTDS";

    public static List<AdDatabaseFile> Locate(GuestImage image, StoredGuestMetadata? stored, List<string> notes)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(notes);
        var (dit, logs, source) = stored?.ActiveDirectory is { DitPath: { Length: > 0 } ditPath } ad
            ? (ditPath, ad.LogPath is { Length: > 0 } logPath ? logPath : Folder(ditPath), "the guest metadata")
            : ReadRegistry(image) is { } fromRegistry
                ? (fromRegistry.Dit, fromRegistry.Logs, "the backup's registry")
                : (DefaultFolder + @"\ntds.dit", DefaultFolder, "the default location");
        notes.Add($"Directory database {dit}, logs in {logs} (from {source}).");

        var files = new List<AdDatabaseFile> { File(image, dit) ?? throw new InvalidOperationException($"The restore point has no {dit}: it is not readable or not there.") };
        var (volume, folder) = Resolve(image, logs.TrimEnd('\\') + @"\x");
        if (volume is null)
        {
            throw new InvalidOperationException($"The restore point has no readable volume for the log folder {logs}.");
        }

        var fs = image.OpenFileSystem(volume.Id);
        var parent = folder!.Parent ?? throw new InvalidOperationException($"{logs} is not a folder.");
        IReadOnlyList<GuestEntry> entries;
        try
        {
            entries = fs.List(parent);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException($"The log folder {logs} cannot be read in the restore point: {ex.Message}", ex);
        }

        foreach (var entry in entries.Where(e => e.Kind == GuestEntryKind.File && IsLog(e.Name)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var windowsPath = logs.TrimEnd('\\') + "\\" + entry.Name;
            if (!files.Any(f => string.Equals(f.WindowsPath, windowsPath, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(new AdDatabaseFile(windowsPath, volume.Id, parent.Append(entry.Name), entry.Size));
            }
        }

        if (files.Count == 1)
        {
            notes.Add($"No transaction logs in {logs}: the database must be consistent on its own.");
        }

        return files;
    }

    /// <summary>edb.chk and the logs edb*.log (also edbtmp.log); not the reserve logs or temp.edb.</summary>
    internal static bool IsLog(string name) =>
        string.Equals(name, "edb.chk", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("edb", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));

    private static string Folder(string path) => path[..Math.Max(0, path.LastIndexOf('\\'))];

    private static AdDatabaseFile? File(GuestImage image, string windowsPath)
    {
        var (volume, path) = Resolve(image, windowsPath);
        if (volume is null || path is null)
        {
            return null;
        }

        try
        {
            var entry = image.OpenFileSystem(volume.Id).Stat(path);
            return entry.Kind == GuestEntryKind.File ? new AdDatabaseFile(windowsPath, volume.Id, path, entry.Size) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static (GuestVolume? Volume, GuestPath? Path) Resolve(GuestImage image, string windowsPath)
    {
        if (windowsPath.Length < 4 || windowsPath[1] != ':' || windowsPath[2] != '\\' || !char.IsAsciiLetter(windowsPath[0]))
        {
            return (null, null);
        }

        var letter = char.ToUpperInvariant(windowsPath[0]) + ":";
        var volume = image.Volumes.FirstOrDefault(v => v.Readable && string.Equals(v.MountPoint, letter, StringComparison.OrdinalIgnoreCase));
        try
        {
            return volume is null ? (null, null) : (volume, GuestPath.Parse(windowsPath[3..].Replace('\\', '/')));
        }
        catch (ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>The NTDS database paths from the SYSTEM hive of the backup (the current control set).</summary>
    private static (string Dit, string Logs)? ReadRegistry(GuestImage image)
    {
        foreach (var volume in image.Volumes.Where(v => v.Readable && v.FileSystem == "NTFS"))
        {
            try
            {
                var fs = image.OpenFileSystem(volume.Id);
                using var stream = fs.OpenRead(GuestPath.Parse(SystemHive));
                using var hive = new RegistryHive(stream, Ownership.None);
                var current = hive.Root.OpenSubKey("Select")?.GetValue("Current") is int set ? set : 1;
                var parameters = hive.Root.OpenSubKey($@"ControlSet{current:000}\Services\NTDS\Parameters");
                if (parameters?.GetValue("DSA Database file") is string dit && dit.Length > 0)
                {
                    return (dit, parameters.GetValue("Database log files path") as string is { Length: > 0 } logs ? logs : Folder(dit));
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
            }
        }

        return null;
    }
}
