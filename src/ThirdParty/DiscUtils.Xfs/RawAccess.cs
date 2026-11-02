// Added by the backdafuckup project (MIT, like the rest of this assembly; see NOTICE.md).
// Path lookup by raw name components that never follows symbolic links, for file-level restore.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using DiscUtils.Streams;

namespace DiscUtils.Xfs;

public sealed partial class XfsFileSystem
{
    /// <summary>The file system UUID as blkid and /etc/fstab show it (UUID=...).</summary>
    public string VolumeUuid => GetRealFileSystem<VfsXfsFileSystem>().RawVolumeUuid;

    /// <summary>The entry at <paramref name="path"/> (raw name components) without following links; empty = root.</summary>
    public RawUnixEntry GetRawEntry(IReadOnlyList<string> path) => GetRealFileSystem<VfsXfsFileSystem>().RawStat(path);

    /// <summary>The entries of the directory at <paramref name="path"/>; links inside the path are not followed.</summary>
    public IReadOnlyList<RawUnixEntry> GetRawEntries(IReadOnlyList<string> path) => GetRealFileSystem<VfsXfsFileSystem>().RawList(path);

    /// <summary>Opens the regular file at <paramref name="path"/>.</summary>
    public SparseStream OpenRawFile(IReadOnlyList<string> path) => GetRealFileSystem<VfsXfsFileSystem>().RawOpen(path);

    /// <summary>The target of the symbolic link at <paramref name="path"/>, as stored.</summary>
    public string ReadRawLink(IReadOnlyList<string> path) => GetRealFileSystem<VfsXfsFileSystem>().RawReadLink(path);
}

internal sealed partial class VfsXfsFileSystem
{
    private const int MaxLinkLength = 4096;
    private readonly ConditionalWeakTable<Directory, Dictionary<string, DirEntry>> _rawIndex = new();

    internal string RawVolumeUuid
    {
        get
        {
            var b = Context.SuperBlock.UniqueId.ToByteArray(bigEndian: true);
            return $"{Convert.ToHexString(b, 0, 4)}-{Convert.ToHexString(b, 4, 2)}-{Convert.ToHexString(b, 6, 2)}-{Convert.ToHexString(b, 8, 2)}-{Convert.ToHexString(b, 10, 6)}".ToLowerInvariant();
        }
    }

    internal RawUnixEntry RawStat(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return ToRaw(string.Empty, true, Context.SuperBlock.RootInode, RootDirectory.Inode);
        }

        var entry = Lookup(path);
        return ToRaw(entry);
    }

    internal IReadOnlyList<RawUnixEntry> RawList(IReadOnlyList<string> path)
    {
        var directory = path.Count == 0 ? RootDirectory : OpenDirectory(Lookup(path), path);
        return directory.RawEntries.Select(ToRaw).ToList();
    }

    internal SparseStream RawOpen(IReadOnlyList<string> path)
    {
        var entry = Lookup(path);
        if (entry.Inode.FileType != UnixFileType.Regular)
        {
            throw new IOException($"{Join(path)} is not a regular file ({entry.Inode.FileType}).");
        }

        return new BufferStream(new File(Context, entry.Inode).FileContent, FileAccess.Read);
    }

    internal string RawReadLink(IReadOnlyList<string> path)
    {
        var entry = Lookup(path);
        if (entry.Inode.FileType != UnixFileType.Link)
        {
            throw new IOException($"{Join(path)} is not a symbolic link.");
        }

        if (entry.Inode.Length is 0 or > MaxLinkLength)
        {
            throw new IOException($"The symbolic link {Join(path)} has an invalid length ({entry.Inode.Length}).");
        }

        var data = new Symlink(Context, entry.Inode).FileContent.ReadExactly(0, (int)entry.Inode.Length);
        return Context.Options.FileNameEncoding.GetString(data);
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
        if (entry.Inode.FileType != UnixFileType.Directory)
        {
            throw new IOException($"{Join(path)} is not a directory ({entry.Inode.FileType}; links are not followed).");
        }

        return entry.CachedDirectory ??= new Directory(Context, entry.Inode);
    }

    private DirEntry Find(Directory directory, string name)
    {
        var encoding = Context.Options.FileNameEncoding;
        var index = _rawIndex.GetValue(directory, d =>
        {
            var map = new Dictionary<string, DirEntry>(StringComparer.Ordinal);
            foreach (var e in d.RawEntries)
            {
                map.TryAdd(encoding.GetString(e.RawNameBytes), e);
            }

            return map;
        });
        return index.TryGetValue(name, out var entry) ? entry : null;
    }

    private RawUnixEntry ToRaw(DirEntry entry)
    {
        var bytes = entry.RawNameBytes;
        var name = Context.Options.FileNameEncoding.GetString(bytes);
        var exact = bytes.AsSpan().SequenceEqual(Context.Options.FileNameEncoding.GetBytes(name));
        return ToRaw(name, exact, entry.InodeNumber, entry.Inode);
    }

    private static RawUnixEntry ToRaw(string name, bool exact, ulong number, Inode inode) => new()
    {
        Name = name,
        NameIsExact = exact,
        InodeNumber = number,
        FileType = inode.FileType,
        Mode = inode.Mode & 0xFFF,
        UserId = inode.UserId,
        GroupId = inode.GroupId,
        Length = (long)inode.Length,
        LinkCount = inode.Version == 1 ? inode.Onlink : inode.Nlink,
        AccessTimeUtc = inode.AccessTime,
        ModificationTimeUtc = inode.ModificationTime,
        ChangeTimeUtc = inode.CreationTime,
    };

    private static string Join(IReadOnlyList<string> path, int count = int.MaxValue) => "/" + string.Join("/", path.Take(count));
}
