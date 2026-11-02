using System.Buffers.Binary;
using System.Text;
using DiscUtils.Ntfs;

namespace Backdafuckup.FileLevel;

/// <summary>NTFS through DiscUtils, with hidden and system files shown and links reported instead of followed.</summary>
internal sealed class NtfsGuestFileSystem : IGuestFileSystem, ISizeBoundedFileSystem
{
    private const uint TagMountPoint = 0xA0000003;
    private const uint TagSymlink = 0xA000000C;
    private const uint TagDedup = 0x80000013;
    private const uint TagWof = 0x80000017;
    private const uint TagAppExecLink = 0x8000001B;

    private static readonly HashSet<string> RootMetafiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend", "$Quota", "$ObjId", "$Reparse",
    };

    private readonly NtfsFileSystem _fs;

    public NtfsGuestFileSystem(NtfsFileSystem fs)
    {
        _fs = fs;
        _fs.NtfsOptions.HideHiddenFiles = false;
        _fs.NtfsOptions.HideSystemFiles = false;
        _fs.NtfsOptions.HideMetafiles = true;
        _fs.NtfsOptions.HideDosFileNames = true;
    }

    public long MaxFileSize { get; set; } = long.MaxValue;

    public GuestFileSystemFamily Family => GuestFileSystemFamily.Windows;

    public string Type => "NTFS";

    public string Label => _fs.VolumeLabel;

    public uint Serial => _fs.VolumeId;

    public GuestEntry Stat(GuestPath path)
    {
        if (path.IsRoot)
        {
            return new GuestEntry { Name = string.Empty, Kind = GuestEntryKind.Directory, Attributes = FileAttributes.Directory };
        }

        var p = ToNtfs(path);
        if (!_fs.Exists(p))
        {
            throw new FileNotFoundException($"No such file or folder: {path}");
        }

        var attributes = _fs.GetAttributes(p);
        var kind = (attributes & FileAttributes.Directory) != 0 ? GuestEntryKind.Directory : GuestEntryKind.File;
        string? target = null;
        string? unsupported = null;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            (kind, target, unsupported) = ClassifyReparsePoint(p, kind);
        }

        if ((attributes & FileAttributes.Encrypted) != 0 && kind == GuestEntryKind.File)
        {
            unsupported = "EFS-encrypted (only the encrypted data is in the backup)";
        }

        var size = kind == GuestEntryKind.File ? _fs.GetFileLength(p) : 0;
        unsupported ??= FileSizeBound.Check(size, MaxFileSize);
        return new GuestEntry
        {
            Name = path.Name,
            Kind = kind,
            Size = size,
            ModifiedUtc = _fs.GetLastWriteTimeUtc(p),
            CreatedUtc = _fs.GetCreationTimeUtc(p),
            AccessedUtc = _fs.GetLastAccessTimeUtc(p),
            Attributes = attributes,
            LinkTarget = target,
            FileId = _fs.GetFileId(p),
            LinkCount = kind == GuestEntryKind.File ? Math.Max(1, _fs.GetHardLinkCount(p)) : 1,
            Unsupported = unsupported,
        };
    }

    public IReadOnlyList<GuestEntry> List(GuestPath directory)
    {
        var entry = Stat(directory);
        if (entry.Kind != GuestEntryKind.Directory)
        {
            throw new IOException($"{directory} is not a folder ({entry.Kind}).");
        }

        var result = new List<GuestEntry>();
        foreach (var full in _fs.GetFileSystemEntries(ToNtfs(directory)))
        {
            var name = LastComponent(full);
            if (directory.IsRoot && RootMetafiles.Contains(name))
            {
                continue;
            }

            result.Add(Stat(directory.Append(name)));
        }

        return result;
    }

    public Stream OpenRead(GuestPath path)
    {
        var entry = Stat(path);
        if (entry.Kind != GuestEntryKind.File)
        {
            throw new IOException($"{path} is not a file ({entry.Kind}).");
        }

        if (entry.Unsupported is not null)
        {
            throw new GuestFileUnsupportedException(path.ToString(), entry.Unsupported);
        }

        return _fs.OpenFile(ToNtfs(path), FileMode.Open, FileAccess.Read);
    }

    public byte[]? GetSecurityDescriptor(GuestPath path)
    {
        var descriptor = _fs.GetSecurity(path.IsRoot ? string.Empty : ToNtfs(path));
        if (descriptor is null)
        {
            return null;
        }

        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        return binary;
    }

    public void Dispose() => _fs.Dispose();

    private (GuestEntryKind Kind, string? Target, string? Unsupported) ClassifyReparsePoint(string p, GuestEntryKind kind)
    {
        var reparse = _fs.GetReparsePoint(p);
        var tag = unchecked((uint)reparse.Tag);
        return tag switch
        {
            TagMountPoint => (GuestEntryKind.Junction, ReparseTarget(reparse.Content, 8), null),
            TagSymlink => (GuestEntryKind.Symlink, ReparseTarget(reparse.Content, 12), null),
            TagWof => (kind, null, null),
            TagDedup => (kind, null, "deduplicated by Windows Data Deduplication (not supported)"),
            TagAppExecLink => (GuestEntryKind.ReparsePoint, null, "app execution alias"),
            _ when (tag & 0xFFFF0FFF) == 0x9000001A => (kind, null, "cloud file placeholder (e.g. OneDrive); the content is not on the disk"),
            _ => (GuestEntryKind.ReparsePoint, null, $"reparse point 0x{tag:X8} (not supported)"),
        };
    }

    /// <summary>Print name (or substitute name) of a symlink or mount point reparse buffer.</summary>
    private static string? ReparseTarget(byte[] content, int pathBufferOffset)
    {
        try
        {
            var substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(content);
            var substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(2));
            var printOffset = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(4));
            var printLength = BinaryPrimitives.ReadUInt16LittleEndian(content.AsSpan(6));
            var (offset, length) = printLength > 0 ? (printOffset, printLength) : (substituteOffset, substituteLength);
            return Encoding.Unicode.GetString(content, pathBufferOffset + offset, length);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string ToNtfs(GuestPath path)
    {
        foreach (var component in path.Components)
        {
            if (component.Contains('\\'))
            {
                throw new FileNotFoundException($"No such file or folder: {path}");
            }
        }

        return string.Join('\\', path.Components);
    }

    private static string LastComponent(string full)
    {
        var trimmed = full.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
