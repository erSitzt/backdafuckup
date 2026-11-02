using DiscUtils;

namespace Backdafuckup.FileLevel;

/// <summary>FAT (EFI system partitions, /boot/efi): no links, no security descriptors.</summary>
internal sealed class FatGuestFileSystem(DiscFileSystem fs) : IGuestFileSystem, ISizeBoundedFileSystem
{
    public long MaxFileSize { get; set; } = long.MaxValue;

    public GuestFileSystemFamily Family => GuestFileSystemFamily.Windows;

    public string Type => "FAT";

    /// <summary>The volume label; "NO NAME" is what FAT stores for "no label" (e.g. on EFI system partitions).</summary>
    public string Label => fs.VolumeLabel?.Trim() is { Length: > 0 } label && label != "NO NAME" ? label : string.Empty;

    public GuestEntry Stat(GuestPath path)
    {
        if (path.IsRoot)
        {
            return new GuestEntry { Name = string.Empty, Kind = GuestEntryKind.Directory, Attributes = FileAttributes.Directory };
        }

        var p = string.Join('\\', path.Components);
        if (!fs.Exists(p))
        {
            throw new FileNotFoundException($"No such file or folder: {path}");
        }

        var attributes = fs.GetAttributes(p);
        var directory = (attributes & FileAttributes.Directory) != 0;
        var size = directory ? 0 : fs.GetFileLength(p);
        return new GuestEntry
        {
            Name = path.Name,
            Kind = directory ? GuestEntryKind.Directory : GuestEntryKind.File,
            Size = size,
            Unsupported = FileSizeBound.Check(size, MaxFileSize),
            ModifiedUtc = fs.GetLastWriteTimeUtc(p),
            CreatedUtc = fs.GetCreationTimeUtc(p),
            AccessedUtc = fs.GetLastAccessTimeUtc(p),
            Attributes = attributes,
        };
    }

    public IReadOnlyList<GuestEntry> List(GuestPath directory)
    {
        var p = string.Join('\\', directory.Components);
        return [.. fs.GetFileSystemEntries(p).Select(full => Stat(directory.Append(full.TrimEnd('\\', '/').Split('\\', '/')[^1])))];
    }

    public Stream OpenRead(GuestPath path)
    {
        var entry = Stat(path);
        if (entry.Kind != GuestEntryKind.File)
        {
            throw new IOException($"{path} is not a file.");
        }

        if (entry.Unsupported is not null)
        {
            throw new GuestFileUnsupportedException(path.ToString(), entry.Unsupported);
        }

        return fs.OpenFile(string.Join('\\', path.Components), FileMode.Open, FileAccess.Read);
    }

    public byte[]? GetSecurityDescriptor(GuestPath path) => null;

    public void Dispose() => fs.Dispose();
}
