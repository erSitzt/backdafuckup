using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Mover;

/// <summary>
/// Reads a mounted volume file by file into the <see cref="MoverTree"/> stream (spec
/// 2026-11-12-1952-kubernetes-file-level-volumes): depth first, each folder's entries sorted by their name bytes. Folders are
/// opened relative to their parent's descriptor and nothing is followed (<c>O_NOFOLLOW</c>), so a link swapped in while
/// the walk runs cannot lead it out of the volume; it stays on the volume's file system. Names are kept as bytes. A file
/// whose stat differs after reading is marked as changed while read.
/// </summary>
internal sealed class VolumeTree
{
    /// <summary>Changed within this time before it was read: the next run reads it again, whatever its stat says.</summary>
    private static readonly long RacyNs = TimeSpan.FromSeconds(2).Ticks * 100;

    private const int ReadSize = 1024 * 1024;

    private readonly MoverTreeWriter _writer;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<ulong> _linked = [];
    private readonly byte[] _read = new byte[ReadSize];
    private (uint Major, uint Minor) _device;
    private long _entries;
    private long _bytes;
    private long _issues;
    private volatile string _current = string.Empty;

    private VolumeTree(Stream output, CancellationToken cancellationToken)
    {
        _writer = new MoverTreeWriter(output);
        _cancellationToken = cancellationToken;
    }

    public sealed record Summary(long Entries, long Bytes, long Issues);

    /// <summary>Reads the tree under <paramref name="root"/> into <paramref name="output"/>, with a heartbeat every <paramref name="heartbeat"/>.</summary>
    public static Summary Read(string root, Stream output, TimeSpan heartbeat, CancellationToken cancellationToken)
    {
        var tree = new VolumeTree(output, cancellationToken);
        var rootFd = TreeNative.Open(root);
        if (rootFd < 0)
        {
            throw new IOException($"The volume at {root} cannot be opened: {TreeNative.Error()}.");
        }

        try
        {
            var stat = TreeNative.Stat(rootFd, null) ?? throw new IOException($"The volume at {root} cannot be read: {TreeNative.Error()}.");
            tree._device = stat.Device;
            tree._writer.Header(stat.ToTreeStat());
            using var timer = new Timer(_ => tree._writer.Heartbeat(Interlocked.Read(ref tree._entries), Interlocked.Read(ref tree._bytes), tree._current), null, heartbeat, heartbeat);
            tree.Walk(rootFd, string.Empty);
            tree._writer.End(tree._entries, tree._bytes);
            return new Summary(tree._entries, tree._bytes, tree._issues);
        }
        finally
        {
            TreeNative.Close(rootFd);
        }
    }

    private void Walk(int dirFd, string path)
    {
        var names = TreeNative.List(dirFd);
        if (names is null)
        {
            Issue(path, "cannot list the folder: " + TreeNative.Error());
            return;
        }

        if (path.Length == 0)
        {
            // The file system's own folder, not the volume's data.
            names.RemoveAll(n => n.AsSpan().SequenceEqual("lost+found"u8));
        }

        names.Sort((a, b) => a.AsSpan().SequenceCompareTo(b));
        foreach (var name in names)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var child = path.Length == 0 ? Encoding.UTF8.GetString(name) : $"{path}/{Encoding.UTF8.GetString(name)}";
            _current = child;
            var stat = TreeNative.Stat(dirFd, name);
            if (stat is null)
            {
                IssueEntry(name, TreeNative.Errno() == TreeNative.ENOENT ? "vanished while the volume was read" : TreeNative.Error());
                continue;
            }

            Interlocked.Increment(ref _entries);
            switch (stat.Type)
            {
                case TreeNative.SIfDir:
                    Folder(dirFd, name, stat, child);
                    break;
                case TreeNative.SIfReg:
                    File(dirFd, name, stat);
                    break;
                case TreeNative.SIfLnk:
                    var target = TreeNative.ReadLink(dirFd, name);
                    if (target is null)
                    {
                        IssueEntry(name, "cannot read the link: " + TreeNative.Error());
                    }
                    else
                    {
                        _writer.Symlink(name, stat.ToTreeStat(), target);
                    }

                    break;
                case TreeNative.SIfIfo:
                    _writer.Special(name, stat.ToTreeStat(), TreeSpecialType.Fifo, 0);
                    break;
                case TreeNative.SIfSock:
                    _writer.Special(name, stat.ToTreeStat(), TreeSpecialType.Socket, 0);
                    break;
                case TreeNative.SIfChr:
                    _writer.Special(name, stat.ToTreeStat(), TreeSpecialType.CharDevice, stat.Rdev);
                    break;
                case TreeNative.SIfBlk:
                    _writer.Special(name, stat.ToTreeStat(), TreeSpecialType.BlockDevice, stat.Rdev);
                    break;
                default:
                    IssueEntry(name, $"of an unknown type ({stat.Type:x})");
                    break;
            }
        }
    }

    private void Folder(int dirFd, byte[] name, TreeNative.StatResult stat, string path)
    {
        if (stat.Device != _device)
        {
            IssueEntry(name, "another file system is mounted here; it is not part of the volume");
            return;
        }

        var fd = TreeNative.OpenAt(dirFd, name, TreeNative.ODirectory);
        if (fd < 0)
        {
            IssueEntry(name, "cannot open the folder: " + TreeNative.Error());
            return;
        }

        try
        {
            _writer.Enter(name, stat.ToTreeStat());
            Walk(fd, path);
            _writer.Leave();
        }
        finally
        {
            TreeNative.Close(fd);
        }
    }

    private void File(int dirFd, byte[] name, TreeNative.StatResult stat)
    {
        // Another name of a file sent before: the same content, not read again.
        if (stat.Links > 1 && _linked.Contains(stat.Inode))
        {
            _writer.HardLink(name, stat.ToTreeStat());
            return;
        }

        // O_NONBLOCK: a FIFO swapped in for the file must not block the walk.
        var fd = TreeNative.OpenAt(dirFd, name, TreeNative.ONonBlock | TreeNative.ONoCtty);
        if (fd < 0)
        {
            IssueEntry(name, TreeNative.Errno() == TreeNative.ENOENT ? "vanished while the volume was read" : "cannot open the file: " + TreeNative.Error());
            return;
        }

        try
        {
            var before = TreeNative.Stat(fd, null);
            if (before is not { Type: TreeNative.SIfReg })
            {
                IssueEntry(name, "changed into something else while the volume was read");
                return;
            }

            _writer.FileStart(name, before.ToTreeStat());
            long length = 0;
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var n = TreeNative.Read(fd, _read);
                if (n < 0)
                {
                    _writer.FileAbort("reading failed after " + length + " bytes: " + TreeNative.Error());
                    Interlocked.Increment(ref _issues);
                    return;
                }

                if (n == 0)
                {
                    break;
                }

                _writer.Piece(_read.AsSpan(0, n));
                length += n;
                Interlocked.Add(ref _bytes, n);
            }

            var after = TreeNative.Stat(fd, null);
            var end = after is null || after.Size != before.Size || after.MtimeNs != before.MtimeNs || after.CtimeNs != before.CtimeNs
                ? TreeFileEnd.Changed
                : length < before.Size ? TreeFileEnd.Short : TreeFileEnd.Complete;
            var now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
            var racy = now - Math.Max(before.MtimeNs, before.CtimeNs) < RacyNs;
            _writer.FileEnd(end, length, holes: false, racy);
            if (before.Links > 1)
            {
                _linked.Add(before.Inode);
            }
        }
        finally
        {
            TreeNative.Close(fd);
        }
    }

    private void IssueEntry(byte[] name, string reason)
    {
        _writer.Issue(name, reason);
        Interlocked.Increment(ref _issues);
    }

    private void Issue(string path, string reason)
    {
        _writer.Issue(Encoding.UTF8.GetBytes(path.Length == 0 ? "." : path[(path.LastIndexOf('/') + 1)..]), reason);
        Interlocked.Increment(ref _issues);
    }
}

/// <summary>The few system calls the walk needs, with names as bytes (linux-x64).</summary>
internal static class TreeNative
{
    public const int ENOENT = 2;
    public const int SIfDir = 0x4000;
    public const int SIfReg = 0x8000;
    public const int SIfLnk = 0xA000;
    public const int SIfIfo = 0x1000;
    public const int SIfSock = 0xC000;
    public const int SIfChr = 0x2000;
    public const int SIfBlk = 0x6000;

    public const int ONonBlock = 0x800;
    public const int ONoCtty = 0x100;
    public const int ODirectory = 0x10000;
    private const int ONoFollow = 0x20000;
    private const int OCloExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasic = 0x7ff;
    private const int StatxBytes = 256;

    public sealed record StatResult(int Type, int Mode, long Uid, long Gid, long MtimeNs, long CtimeNs, ulong Inode, long Links, long Size, long Rdev, (uint Major, uint Minor) Device)
    {
        public TreeStat ToTreeStat() => new(Mode, Uid, Gid, MtimeNs, CtimeNs, Inode, Links, Size);
    }

    public static int Open(string path)
    {
        Debug.Assert(RuntimeInformation.ProcessArchitecture == Architecture.X64, "the open flags are those of x86_64");
        return OpenNative(Z(Encoding.UTF8.GetBytes(path)), ODirectory | OCloExec, 0);
    }

    /// <summary>Opens <paramref name="name"/> in <paramref name="dirFd"/> read-only, never following a link.</summary>
    public static int OpenAt(int dirFd, byte[] name, int flags) => OpenAtNative(dirFd, Z(name), flags | ONoFollow | OCloExec, 0);

    public static void Close(int fd) => _ = CloseNative(fd);

    /// <summary>The stat of <paramref name="name"/> in <paramref name="fd"/> (not following a link), or of <paramref name="fd"/> itself.</summary>
    public static StatResult? Stat(int fd, byte[]? name)
    {
        var buffer = new byte[StatxBytes];
        var rc = name is null ? Statx(fd, [0], AtEmptyPath | AtSymlinkNoFollow, StatxBasic, buffer) : Statx(fd, Z(name), AtSymlinkNoFollow, StatxBasic, buffer);
        if (rc != 0)
        {
            return null;
        }

        var span = buffer.AsSpan();
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
        static long Time(ReadOnlySpan<byte> t) => (BinaryPrimitives.ReadInt64LittleEndian(t) * 1_000_000_000L) + BinaryPrimitives.ReadUInt32LittleEndian(t[8..]);
        var rdevMajor = BinaryPrimitives.ReadUInt32LittleEndian(span[128..]);
        var rdevMinor = BinaryPrimitives.ReadUInt32LittleEndian(span[132..]);
        return new StatResult(
            mode & 0xF000,
            mode & 0xFFF,
            BinaryPrimitives.ReadUInt32LittleEndian(span[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[24..]),
            Time(span[112..]),
            Time(span[96..]),
            BinaryPrimitives.ReadUInt64LittleEndian(span[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(span[16..]),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(span[40..]),
            ((long)rdevMajor << 32) | rdevMinor,
            (BinaryPrimitives.ReadUInt32LittleEndian(span[136..]), BinaryPrimitives.ReadUInt32LittleEndian(span[140..])));
    }

    /// <summary>The names in a folder (without "." and ".."), or null if it cannot be listed.</summary>
    public static List<byte[]>? List(int dirFd)
    {
        var names = new List<byte[]>();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var n = (long)GetDents64(dirFd, buffer, (nuint)buffer.Length);
            if (n < 0)
            {
                return null;
            }

            if (n == 0)
            {
                return names;
            }

            for (var offset = 0; offset < n;)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 16));
                var name = buffer.AsSpan(offset + 19, length - 19);
                name = name[..name.IndexOf((byte)0)];
                if (!name.SequenceEqual("."u8) && !name.SequenceEqual(".."u8))
                {
                    names.Add(name.ToArray());
                }

                offset += length;
            }
        }
    }

    public static int Read(int fd, byte[] buffer) => (int)ReadNative(fd, buffer, (nuint)buffer.Length);

    public static byte[]? ReadLink(int dirFd, byte[] name)
    {
        var buffer = new byte[4096];
        var n = (long)ReadLinkAt(dirFd, Z(name), buffer, (nuint)buffer.Length);
        return n < 0 ? null : buffer.AsSpan(0, (int)n).ToArray();
    }

    public static int Errno() => Marshal.GetLastPInvokeError();

    public static string Error() => Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());

    /// <summary>A name as a C string.</summary>
    private static byte[] Z(byte[] name)
    {
        var z = new byte[name.Length + 1];
        name.CopyTo(z, 0);
        return z;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenNative(byte[] path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAtNative(int dirFd, byte[] path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CloseNative(int fd);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int dirFd, byte[] path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "getdents64", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint GetDents64(int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ReadNative(int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ReadLinkAt(int dirFd, byte[] path, [Out] byte[] buffer, nuint size);
}
