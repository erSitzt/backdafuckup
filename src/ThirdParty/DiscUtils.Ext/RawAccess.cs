// Added by the backdafuckup project (MIT, like the rest of this assembly; see NOTICE.md).
// Path lookup by raw name components that never follows symbolic links, for file-level restore.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DiscUtils.Streams;

namespace DiscUtils.Ext;

public sealed partial class ExtFileSystem
{
    /// <summary>The file system UUID as blkid and /etc/fstab show it (UUID=...).</summary>
    public string VolumeUuid => GetRealFileSystem<VfsExtFileSystem>().RawVolumeUuid;

    /// <summary>The entry at <paramref name="path"/> (raw name components) without following links; empty = root.</summary>
    public RawUnixEntry GetRawEntry(IReadOnlyList<string> path) => GetRealFileSystem<VfsExtFileSystem>().RawStat(path);

    /// <summary>The entries of the directory at <paramref name="path"/>; links inside the path are not followed.</summary>
    public IReadOnlyList<RawUnixEntry> GetRawEntries(IReadOnlyList<string> path) => GetRealFileSystem<VfsExtFileSystem>().RawList(path);

    /// <summary>Opens the regular file at <paramref name="path"/>.</summary>
    public SparseStream OpenRawFile(IReadOnlyList<string> path) => GetRealFileSystem<VfsExtFileSystem>().RawOpen(path);

    /// <summary>The target of the symbolic link at <paramref name="path"/>, as stored.</summary>
    public string ReadRawLink(IReadOnlyList<string> path) => GetRealFileSystem<VfsExtFileSystem>().RawReadLink(path);
}

internal sealed partial class VfsExtFileSystem
{
    private const uint RootInodeNumber = 2;
    private const int MaxLinkLength = 4096;
    private readonly ConditionalWeakTable<Directory, Dictionary<string, DirEntry>> _rawIndex = new();
    private readonly Dictionary<uint, Directory> _rawDirectories = new();

    internal string RawVolumeUuid => FormatUuid(Context.SuperBlock.UniqueId.ToByteArray());

    internal RawUnixEntry RawStat(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return ToRaw(string.Empty, true, RootInodeNumber, GetInode(RootInodeNumber));
        }

        var entry = Lookup(path);
        return ToRaw(entry.Record.RawName, entry.Record.RawNameExact, entry.Record.Inode, GetInode(entry.Record.Inode));
    }

    internal IReadOnlyList<RawUnixEntry> RawList(IReadOnlyList<string> path)
    {
        var directory = path.Count == 0 ? RootDirectory : OpenDirectory(Lookup(path), path);
        return directory.RawEntries
            .Select(e => ToRaw(e.Record.RawName, e.Record.RawNameExact, e.Record.Inode, GetInode(e.Record.Inode)))
            .ToList();
    }

    internal SparseStream RawOpen(IReadOnlyList<string> path)
    {
        var entry = Lookup(path);
        var inode = GetInode(entry.Record.Inode);
        if (inode.FileType != UnixFileType.Regular)
        {
            throw new IOException($"{Join(path)} is not a regular file ({inode.FileType}).");
        }

        return new BufferStream(new File(Context, entry.Record.Inode, inode).FileContent, FileAccess.Read);
    }

    internal string RawReadLink(IReadOnlyList<string> path)
    {
        var entry = Lookup(path);
        var inode = GetInode(entry.Record.Inode);
        if (inode.FileType != UnixFileType.Link)
        {
            throw new IOException($"{Join(path)} is not a symbolic link.");
        }

        if (inode.FileSize is <= 0 or > MaxLinkLength)
        {
            throw new IOException($"The symbolic link {Join(path)} has an invalid length ({inode.FileSize}).");
        }

        var data = new Symlink(Context, entry.Record.Inode, inode).FileContent.ReadExactly(0, (int)inode.FileSize);
        var end = Array.IndexOf(data, (byte)0);
        return Context.Options.FileNameEncoding.GetString(data, 0, end < 0 ? data.Length : end);
    }

    private DirEntry Lookup(IReadOnlyList<string> path)
    {
        var directory = RootDirectory;
        DirEntry entry = null;
        for (var i = 0; i < path.Count; i++)
        {
            entry = Find(directory, path[i])
                ?? throw new FileNotFoundException($"No such file or directory: {Join(path, i + 1)}");
            if (i < path.Count - 1)
            {
                directory = OpenDirectory(entry, path.Take(i + 1).ToList());
            }
        }

        return entry;
    }

    private Directory OpenDirectory(DirEntry entry, IReadOnlyList<string> path)
    {
        var inode = GetInode(entry.Record.Inode);
        if (inode.FileType != UnixFileType.Directory)
        {
            throw new IOException($"{Join(path)} is not a directory ({inode.FileType}; links are not followed).");
        }

        if (!_rawDirectories.TryGetValue(entry.Record.Inode, out var directory))
        {
            if (_rawDirectories.Count >= 4096)
            {
                _rawDirectories.Clear();
            }

            directory = new Directory(Context, entry.Record.Inode, inode);
            _rawDirectories[entry.Record.Inode] = directory;
        }

        return directory;
    }

    private DirEntry Find(Directory directory, string name)
    {
        var index = _rawIndex.GetValue(directory, static d =>
        {
            var map = new Dictionary<string, DirEntry>(StringComparer.Ordinal);
            foreach (var e in d.RawEntries)
            {
                map.TryAdd(e.Record.RawName, e);
            }

            return map;
        });
        return index.TryGetValue(name, out var entry) ? entry : null;
    }

    private static RawUnixEntry ToRaw(string name, bool exact, uint number, Inode inode) => new()
    {
        Name = name,
        NameIsExact = exact,
        InodeNumber = number,
        FileType = inode.FileType,
        Mode = inode.Mode & 0xFFF,
        UserId = ((long)inode.UserIdHigh << 16) | inode.UserIdLow,
        GroupId = ((long)inode.GroupIdHigh << 16) | inode.GroupIdLow,
        Length = inode.FileSize,
        LinkCount = inode.LinksCount,
        AccessTimeUtc = inode.AccessTimeUtc,
        ModificationTimeUtc = inode.ModificationTimeUtc,
        ChangeTimeUtc = inode.ChangeTimeUtc,
    };

    private static string Join(IReadOnlyList<string> path, int count = int.MaxValue) => "/" + string.Join("/", path.Take(count));

    internal static string FormatUuid(byte[] b) =>
        $"{Convert.ToHexString(b, 0, 4)}-{Convert.ToHexString(b, 4, 2)}-{Convert.ToHexString(b, 6, 2)}-{Convert.ToHexString(b, 8, 2)}-{Convert.ToHexString(b, 10, 6)}".ToLowerInvariant();
}
