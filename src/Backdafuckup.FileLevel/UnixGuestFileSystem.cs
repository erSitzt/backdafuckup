using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Xfs;

namespace Backdafuckup.FileLevel;

/// <summary>ext2/3/4 and XFS through our patched providers: raw names, lstat semantics, links never followed.</summary>
internal sealed class UnixGuestFileSystem : IGuestFileSystem, ISizeBoundedFileSystem
{
    private readonly DiscFileSystem _fs;
    private readonly Func<IReadOnlyList<string>, Raw> _stat;
    private readonly Func<IReadOnlyList<string>, IEnumerable<Raw>> _list;
    private readonly Func<IReadOnlyList<string>, Stream> _open;
    private readonly Func<IReadOnlyList<string>, string> _readLink;

    public UnixGuestFileSystem(ExtFileSystem ext)
    {
        _fs = ext;
        Type = "ext";
        VolumeUuid = ext.VolumeUuid;
        _stat = p => Raw.From(ext.GetRawEntry(p));
        _list = p => ext.GetRawEntries(p).Select(Raw.From);
        _open = ext.OpenRawFile;
        _readLink = ext.ReadRawLink;
    }

    public UnixGuestFileSystem(XfsFileSystem xfs)
    {
        _fs = xfs;
        Type = "XFS";
        VolumeUuid = xfs.VolumeUuid;
        _stat = p => Raw.From(xfs.GetRawEntry(p));
        _list = p => xfs.GetRawEntries(p).Select(Raw.From);
        _open = xfs.OpenRawFile;
        _readLink = xfs.ReadRawLink;
    }

    public long MaxFileSize { get; set; } = long.MaxValue;

    public GuestFileSystemFamily Family => GuestFileSystemFamily.Unix;

    public string Type { get; }

    public string VolumeUuid { get; }

    public string Label => _fs.VolumeLabel ?? string.Empty;

    public GuestEntry Stat(GuestPath path) => ToEntry(path, _stat(path.Components));

    public IReadOnlyList<GuestEntry> List(GuestPath directory)
    {
        var self = _stat(directory.Components);
        if (self.Type != UnixFileType.Directory)
        {
            throw new IOException($"{directory} is not a folder ({self.Type}).");
        }

        var result = new List<GuestEntry>();
        foreach (var raw in _list(directory.Components))
        {
            if (raw.Name.Length == 0 || raw.Name is "." or ".." || raw.Name.Contains('/') || raw.Name.Contains('\0'))
            {
                continue;
            }

            result.Add(ToEntry(directory.Append(raw.Name), raw));
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

        return _open(path.Components);
    }

    public byte[]? GetSecurityDescriptor(GuestPath path) => null;

    public void Dispose() => _fs.Dispose();

    private GuestEntry ToEntry(GuestPath path, Raw raw)
    {
        var kind = raw.Type switch
        {
            UnixFileType.Regular => GuestEntryKind.File,
            UnixFileType.Directory => GuestEntryKind.Directory,
            UnixFileType.Link => GuestEntryKind.Symlink,
            _ => GuestEntryKind.Special,
        };
        string? unsupported = null;
        if (!raw.NameIsExact)
        {
            unsupported = "the file name is not valid UTF-8";
        }
        else if (kind == GuestEntryKind.Special)
        {
            unsupported = $"{raw.Type} (device, FIFO or socket)";
        }
        else if (kind == GuestEntryKind.File)
        {
            unsupported = FileSizeBound.Check(raw.Length, MaxFileSize);
        }

        return new GuestEntry
        {
            Name = path.Name,
            Kind = kind,
            Size = kind == GuestEntryKind.File ? raw.Length : 0,
            ModifiedUtc = raw.Modified,
            CreatedUtc = raw.Changed,
            AccessedUtc = raw.Accessed,
            Mode = raw.Mode,
            UserId = raw.UserId,
            GroupId = raw.GroupId,
            LinkTarget = kind == GuestEntryKind.Symlink ? TryReadLink(path) : null,
            FileId = (long)raw.Inode,
            LinkCount = (int)Math.Clamp(raw.LinkCount, 1, int.MaxValue),
            Unsupported = unsupported,
        };
    }

    private string? TryReadLink(GuestPath path)
    {
        try
        {
            return _readLink(path.Components);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record Raw(string Name, bool NameIsExact, ulong Inode, UnixFileType Type, int Mode, long UserId, long GroupId, long Length, long LinkCount, DateTime Modified, DateTime Changed, DateTime Accessed)
    {
        public static Raw From(DiscUtils.Ext.RawUnixEntry e) => new(e.Name, e.NameIsExact, e.InodeNumber, e.FileType, e.Mode, e.UserId, e.GroupId, e.Length, e.LinkCount, e.ModificationTimeUtc, e.ChangeTimeUtc, e.AccessTimeUtc);

        public static Raw From(DiscUtils.Xfs.RawUnixEntry e) => new(e.Name, e.NameIsExact, e.InodeNumber, e.FileType, e.Mode, e.UserId, e.GroupId, e.Length, e.LinkCount, e.ModificationTimeUtc, e.ChangeTimeUtc, e.AccessTimeUtc);
    }
}
