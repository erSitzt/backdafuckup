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
/// <list type="bullet">
/// <item>Incremental: with the previous run's tree (<see cref="MoverPreviousCursor"/>), a file whose size, mtime, ctime and
/// inode are all the same is sent as unchanged and not opened.</item>
/// <item>Sparse files: holes (<c>SEEK_DATA</c>/<c>SEEK_HOLE</c>) are sent as such, not read.</item>
/// <item>NFS with root_squash: root is nobody on the server, so what only its owner may read is refused. Such an operation is
/// tried again as the owner (<c>setfsuid</c>/<c>setfsgid</c>, which apply to this thread only; the walk never leaves it),
/// and a folder opened that way is walked as its owner.</item>
/// </list>
/// </summary>
internal sealed class VolumeTree
{
    /// <summary>Changed within this time before it was read: the next run reads it again, whatever its stat says.</summary>
    public static readonly TimeSpan DefaultRacy = TimeSpan.FromSeconds(2);

    private const int ReadSize = 1024 * 1024;

    private readonly MoverTreeWriter _writer;
    private readonly MoverPreviousCursor? _previous;
    private readonly long _racyNs;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<ulong> _linked = [];
    private readonly byte[] _read = new byte[ReadSize];
    private readonly bool _canSwitch;
    private (uint Major, uint Minor) _device;
    private Owner _as = Owner.Self;
    private long _entries;
    private long _bytes;
    private long _issues;
    private long _asOwner;
    private string _alsoTried = string.Empty;
    private volatile string _current = string.Empty;

    private VolumeTree(Stream output, Stream? previous, TimeSpan racy, CancellationToken cancellationToken)
    {
        _writer = new MoverTreeWriter(output);
        _previous = previous is null ? null : new MoverPreviousCursor(previous);
        _racyNs = racy.Ticks * 100;
        _cancellationToken = cancellationToken;

        // Only root (with CAP_SETUID and CAP_SETGID, as the mover pod runs) can act as another owner.
        _canSwitch = Environment.IsPrivilegedProcess;
    }

    /// <param name="Unchanged">Files sent as unchanged since the previous run (not read).</param>
    /// <param name="AsOwner">Entries read as their owner after root was refused (NFS root_squash).</param>
    public sealed record Summary(long Entries, long Bytes, long Issues, long Unchanged = 0, long AsOwner = 0);

    /// <summary>
    /// Reads the tree under <paramref name="root"/> into <paramref name="output"/>, with a heartbeat every
    /// <paramref name="heartbeat"/>. With <paramref name="previous"/> (the server's <c>BDFUPRV1</c> stream), files unchanged
    /// since then are not read; <paramref name="racy"/> is how recently a file may have changed before it is read again next time.
    /// </summary>
    public static Summary Read(string root, Stream output, TimeSpan heartbeat, CancellationToken cancellationToken, Stream? previous = null, TimeSpan? racy = null)
    {
        var tree = new VolumeTree(output, previous, racy ?? DefaultRacy, cancellationToken);
        var rootFd = tree.OpenRoot(root, out var stat);
        try
        {
            tree._device = stat.Device;
            tree._writer.Header(stat.ToTreeStat());
            using var timer = new Timer(_ => tree._writer.Heartbeat(Interlocked.Read(ref tree._entries), Interlocked.Read(ref tree._bytes), tree._current), null, heartbeat, heartbeat);
            tree.Walk(rootFd, string.Empty, stat);
            tree._previous?.Finish();
            tree._writer.End(tree._entries, tree._bytes);
            return new Summary(tree._entries, tree._bytes, tree._issues, tree._previous?.Unchanged ?? 0, tree._asOwner);
        }
        finally
        {
            tree.Become(Owner.Self);
            TreeNative.Close(rootFd);
        }
    }

    /// <summary>The volume's root folder: as root, or as its owner when root is refused (root_squash).</summary>
    private int OpenRoot(string root, out TreeNative.StatResult stat)
    {
        var fd = TreeNative.Open(root);
        if (fd < 0 && TreeNative.Errno() == TreeNative.EACCES && _canSwitch && TreeNative.StatPath(root) is { } owner)
        {
            Become(Owner.Of(owner));
            fd = TreeNative.Open(root);
            if (fd < 0)
            {
                Become(Owner.Self);
            }
            else
            {
                _asOwner++;
            }
        }

        if (fd < 0)
        {
            throw new IOException($"The volume at {root} cannot be opened: {TreeNative.Error()}.");
        }

        stat = TreeNative.Stat(fd, null) ?? throw new IOException($"The volume at {root} cannot be read: {TreeNative.Error()}.");
        return fd;
    }

    private void Walk(int dirFd, string path, TreeNative.StatResult folder)
    {
        var names = TreeNative.List(dirFd);
        if (names is null && Refused() && SwitchTo(Owner.Of(folder)))
        {
            names = TreeNative.List(dirFd);
        }

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

            // A folder that root may list but not search: its entries are looked up as its owner from here on.
            if (stat is null && Refused() && SwitchTo(Owner.Of(folder)))
            {
                stat = TreeNative.Stat(dirFd, name);
            }

            if (stat is null)
            {
                IssueEntry(name, TreeNative.Errno() == TreeNative.ENOENT ? "vanished while the volume was read" : TreeNative.Error());
                continue;
            }

            Interlocked.Increment(ref _entries);
            switch (stat.Type)
            {
                case TreeNative.SIfDir:
                    Folder(dirFd, name, stat, child, folder);
                    break;
                case TreeNative.SIfReg:
                    File(dirFd, name, stat, folder);
                    break;
                case TreeNative.SIfLnk:
                    var target = TreeNative.ReadLink(dirFd, name);
                    if (target is null && Refused() && SwitchTo(Owner.Of(folder)))
                    {
                        target = TreeNative.ReadLink(dirFd, name);
                    }

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

    private void Folder(int dirFd, byte[] name, TreeNative.StatResult stat, string path, TreeNative.StatResult parent)
    {
        if (stat.Device != _device)
        {
            IssueEntry(name, "another file system is mounted here; it is not part of the volume");
            return;
        }

        // Walked as whoever could open it: lookups inside need the same right.
        var walkedAs = _as;
        var fd = OpenAsOwner(dirFd, name, TreeNative.ODirectory, stat, parent, keep: true);
        if (fd < 0)
        {
            IssueEntry(name, $"cannot open the folder{_alsoTried}: {TreeNative.Error()}");
            return;
        }

        try
        {
            _writer.Enter(name, stat.ToTreeStat());
            _previous?.Enter(name);
            Walk(fd, path, stat);
            _previous?.Leave();
            _writer.Leave();
        }
        finally
        {
            TreeNative.Close(fd);
            Become(walkedAs);
        }
    }

    private void File(int dirFd, byte[] name, TreeNative.StatResult stat, TreeNative.StatResult folder)
    {
        // Another name of a file sent before: the same content, not read again.
        if (stat.Links > 1 && _linked.Contains(stat.Inode))
        {
            _writer.HardLink(name, stat.ToTreeStat());
            return;
        }

        // The same as in the previous run: the server takes its content from there.
        if (_previous is not null && _previous.IsUnchanged(name, stat.ToTreeStat()))
        {
            _writer.Unchanged(name, stat.ToTreeStat());
            if (stat.Links > 1)
            {
                _linked.Add(stat.Inode);
            }

            return;
        }

        // O_NONBLOCK: a FIFO swapped in for the file must not block the walk. Opened as its owner, it is read as the walk
        // goes on (reads use the rights it was opened with).
        var fd = OpenAsOwner(dirFd, name, TreeNative.ONonBlock | TreeNative.ONoCtty, stat, folder, keep: false);
        if (fd < 0)
        {
            IssueEntry(name, TreeNative.Errno() == TreeNative.ENOENT ? "vanished while the volume was read" : $"cannot open the file{_alsoTried}: {TreeNative.Error()}");
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
            if (!Content(fd, out var length, out var holes))
            {
                return;
            }

            var after = TreeNative.Stat(fd, null);
            var end = after is null || after.Size != before.Size || after.MtimeNs != before.MtimeNs || after.CtimeNs != before.CtimeNs
                ? TreeFileEnd.Changed
                : length < before.Size ? TreeFileEnd.Short : TreeFileEnd.Complete;
            var now = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
            var racy = now - Math.Max(before.MtimeNs, before.CtimeNs) < _racyNs;
            _writer.FileEnd(end, length, holes, racy);
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

    /// <summary>
    /// Sends the content of the open file: its data regions as pieces, the holes between them as such (<c>SEEK_DATA</c>,
    /// <c>SEEK_HOLE</c>; where the file system cannot tell, everything is read). False when reading failed (sent as aborted).
    /// </summary>
    private bool Content(int fd, out long length, out bool holes)
    {
        length = 0;
        holes = false;
        var seek = true;
        while (true)
        {
            var dataEnd = long.MaxValue;
            if (seek)
            {
                var data = TreeNative.Seek(fd, length, TreeNative.SeekData);
                if (data < 0 && TreeNative.Errno() == TreeNative.ENXIO)
                {
                    // No data from here on: up to the end it is a hole.
                    var end = TreeNative.Seek(fd, 0, TreeNative.SeekEnd);
                    if (end > length)
                    {
                        _writer.Hole(end - length);
                        holes = true;
                        length = end;
                    }

                    return true;
                }

                if (data < 0)
                {
                    seek = false;
                }
                else
                {
                    if (data > length)
                    {
                        _writer.Hole(data - length);
                        holes = true;
                        length = data;
                    }

                    var hole = TreeNative.Seek(fd, length, TreeNative.SeekHole);
                    dataEnd = hole > length ? hole : long.MaxValue;
                }
            }

            while (length < dataEnd)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var n = TreeNative.ReadAt(fd, _read, (int)Math.Min(_read.Length, dataEnd - length), length);
                if (n < 0)
                {
                    _writer.FileAbort("reading failed after " + length + " bytes: " + TreeNative.Error());
                    Interlocked.Increment(ref _issues);
                    return false;
                }

                if (n == 0)
                {
                    // The end (the file may have got shorter than its stat said).
                    return true;
                }

                _writer.Piece(_read.AsSpan(0, n));
                length += n;
                Interlocked.Add(ref _bytes, n);
            }

            if (!seek)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="name"/>; when root is refused, as the entry's owner, then as its folder's. With
    /// <paramref name="keep"/> the walk goes on as whoever opened it (a folder), else the caller switches back. When
    /// that fails too, <see cref="_alsoTried"/> names whom it was tried as, for the issue (a file even its owner may not
    /// read, not a squashed root).
    /// </summary>
    private int OpenAsOwner(int dirFd, byte[] name, int flags, TreeNative.StatResult stat, TreeNative.StatResult folder, bool keep)
    {
        _alsoTried = string.Empty;
        var fd = TreeNative.OpenAt(dirFd, name, flags);
        if (fd >= 0 || !Refused())
        {
            return fd;
        }

        var before = _as;
        var tried = new List<string>(2);
        foreach (var (owner, whose) in new[] { (Owner.Of(stat), "its owner"), (Owner.Of(folder), "its folder's owner") }.DistinctBy(o => o.Item1))
        {
            if (owner == before || !Become(owner))
            {
                continue;
            }

            fd = TreeNative.OpenAt(dirFd, name, flags);
            if (fd >= 0)
            {
                _asOwner++;
                if (!keep)
                {
                    Become(before);
                }

                return fd;
            }

            tried.Add($"{whose} {owner.Uid}:{owner.Gid}");
        }

        // The error of the last try (building the text and setfsuid leave errno alone); the identity back to what it was.
        _alsoTried = tried.Count > 0 ? $" (also tried as {string.Join(" and as ", tried)})" : string.Empty;
        Become(before);
        return -1;
    }

    /// <summary>Whether the last call was refused for lack of permission, and acting as an owner could help.</summary>
    private bool Refused() => _canSwitch && TreeNative.Errno() == TreeNative.EACCES;

    /// <summary>Goes on as <paramref name="owner"/> for the rest of the current folder; false if that is no change or not possible.</summary>
    private bool SwitchTo(Owner owner)
    {
        if (owner == _as || !Become(owner))
        {
            return false;
        }

        _asOwner++;
        return true;
    }

    /// <summary>Acts as <paramref name="owner"/> on this thread (file system ids only).</summary>
    private bool Become(Owner owner)
    {
        if (owner == _as)
        {
            return true;
        }

        if (!_canSwitch)
        {
            return false;
        }

        var ok = TreeNative.SetFsIds(owner.IsSelf ? 0 : (uint)owner.Uid, owner.IsSelf ? 0 : (uint)owner.Gid);
        _as = ok ? owner : Owner.Self;
        if (!ok)
        {
            TreeNative.SetFsIds(0, 0);
        }

        return ok;
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

    /// <summary>The file system identity the walk acts as: itself (root), or an entry's owner.</summary>
    private readonly record struct Owner(long Uid, long Gid, bool IsSelf)
    {
        public static Owner Self => new(0, 0, true);

        public static Owner Of(TreeNative.StatResult stat) => stat.Uid == 0 && stat.Gid == 0 ? Self : new(stat.Uid, stat.Gid, false);
    }
}

/// <summary>The few system calls the walk needs, with names as bytes (linux-x64).</summary>
internal static class TreeNative
{
    public const int ENOENT = 2;
    public const int ENXIO = 6;
    public const int EACCES = 13;
    public const int SeekEnd = 2;
    public const int SeekData = 3;
    public const int SeekHole = 4;
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

    /// <summary>The stat of a path (the volume's mount point), following it.</summary>
    public static StatResult? StatPath(string path)
    {
        const int AtFdCwd = -100;
        var buffer = new byte[StatxBytes];
        return Statx(AtFdCwd, Z(Encoding.UTF8.GetBytes(path)), 0, StatxBasic, buffer) == 0 ? Parse(buffer) : null;
    }

    /// <summary>Opens <paramref name="name"/> in <paramref name="dirFd"/> read-only, never following a link.</summary>
    public static int OpenAt(int dirFd, byte[] name, int flags) => OpenAtNative(dirFd, Z(name), flags | ONoFollow | OCloExec, 0);

    public static void Close(int fd) => _ = CloseNative(fd);

    /// <summary>The stat of <paramref name="name"/> in <paramref name="fd"/> (not following a link), or of <paramref name="fd"/> itself.</summary>
    public static StatResult? Stat(int fd, byte[]? name)
    {
        var buffer = new byte[StatxBytes];
        var rc = name is null ? Statx(fd, [0], AtEmptyPath | AtSymlinkNoFollow, StatxBasic, buffer) : Statx(fd, Z(name), AtSymlinkNoFollow, StatxBasic, buffer);
        return rc == 0 ? Parse(buffer) : null;
    }

    private static StatResult Parse(byte[] buffer)
    {
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

    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/> into the start of <paramref name="buffer"/>.</summary>
    public static int ReadAt(int fd, byte[] buffer, int count, long offset) => (int)PRead(fd, buffer, (nuint)Math.Min(count, buffer.Length), offset);

    /// <summary><c>lseek</c>: with <see cref="SeekData"/> or <see cref="SeekHole"/> where the next data or hole starts; -1 with errno.</summary>
    public static long Seek(int fd, long offset, int whence) => LSeek(fd, offset, whence);

    /// <summary>
    /// Sets this thread's file system user and group ids (not the process's: <c>setfsuid</c> is per thread, and glibc does not
    /// spread it to the other threads as it does <c>setuid</c>); true if both are now in effect.
    /// </summary>
    public static bool SetFsIds(uint uid, uint gid)
    {
        _ = SetFsGid(gid);
        _ = SetFsUid(uid);

        // An invalid id changes nothing and returns the current one.
        return SetFsUid(uint.MaxValue) == uid && SetFsGid(uint.MaxValue) == gid;
    }

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

    [DllImport("libc", EntryPoint = "pread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint PRead(int fd, [Out] byte[] buffer, nuint count, long offset);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long LSeek(int fd, long offset, int whence);

    // No SetLastError: these never fail with errno, and the error of the call they retry must stay readable.
    [DllImport("libc", EntryPoint = "setfsuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint SetFsUid(uint uid);

    [DllImport("libc", EntryPoint = "setfsgid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint SetFsGid(uint gid);

    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint ReadLinkAt(int dirFd, byte[] path, [Out] byte[] buffer, nuint size);
}
