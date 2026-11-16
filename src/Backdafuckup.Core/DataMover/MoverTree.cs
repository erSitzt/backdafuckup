using System.Text;

namespace Backdafuckup.Core.DataMover;

/// <summary>
/// The stream a mover sends when it reads a volume file by file (spec 2026-11-12-1952-kubernetes-file-level-volumes):
/// <c>BDFUTRE1</c>, the root folder's stat, then records in canonical order: depth first, each folder's entries sorted by
/// their name bytes, a folder's <c>D</c> before its entries and its <c>U</c> after them. File content comes as pieces of
/// any length up to <see cref="MaxPiece"/> and as holes (runs of zeros not sent); the server cuts them into chunks.
/// <list type="bullet">
/// <item><c>D</c> enter a folder (name, stat); <c>U</c> leave it.</item>
/// <item><c>F</c> a file (name, stat), then <c>C</c> pieces and <c>0</c> holes, then <c>Z</c> (how it ended) or <c>X</c> (why not).</item>
/// <item><c>S</c> a file unchanged since the previous run (name, stat): its content is taken from there.</item>
/// <item><c>H</c> another name of a file sent before (name, stat; the same inode).</item>
/// <item><c>L</c> a symbolic link (name, stat, target); <c>P</c> a FIFO, socket or device (name, stat, type, device number).</item>
/// <item><c>I</c> an entry not read (name, reason); <c>B</c> a heartbeat (counts, the path being read); <c>E</c> the end (counts).</item>
/// </list>
/// Names are the file system's bytes. Integers are varints (signed ones zig-zag).
/// </summary>
public static class MoverTree
{
    public const int MaxPiece = 4 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "BDFUTRE1"u8;
}

/// <summary>What the stat of an entry says (times in nanoseconds since 1970).</summary>
public readonly record struct TreeStat(int Mode, long Uid, long Gid, long MtimeNs, long CtimeNs, ulong Inode, long Links, long Size);

public enum TreeSpecialType : byte
{
    Fifo = 4,
    Socket = 5,
    CharDevice = 6,
    BlockDevice = 7,
}

public enum TreeFileEnd : byte
{
    /// <summary>Read in full, its stat the same after as before.</summary>
    Complete = 0,

    /// <summary>Its stat changed while it was read: the content may be torn.</summary>
    Changed = 1,

    /// <summary>It got shorter while it was read; the content stops early.</summary>
    Short = 2,
}

public abstract record TreeRecord;

public sealed record TreeEnter(byte[] Name, TreeStat Stat) : TreeRecord;

public sealed record TreeLeave : TreeRecord
{
    public static TreeLeave Instance { get; } = new();
}

public sealed record TreeFileStart(byte[] Name, TreeStat Stat) : TreeRecord;

/// <summary>A piece of the current file's content; <see cref="Data"/> is only valid until the next read.</summary>
public sealed record TreePiece(ReadOnlyMemory<byte> Data) : TreeRecord;

public sealed record TreeHole(long Length) : TreeRecord;

/// <summary>The end of the current file: how it ended, its length as sent, and whether it had holes or was read just after a change.</summary>
public sealed record TreeFileEndRecord(TreeFileEnd End, long Length, bool Holes, bool Racy) : TreeRecord;

public sealed record TreeFileAbort(string Reason) : TreeRecord;

public sealed record TreeUnchanged(byte[] Name, TreeStat Stat) : TreeRecord;

public sealed record TreeHardLink(byte[] Name, TreeStat Stat) : TreeRecord;

public sealed record TreeSymlink(byte[] Name, TreeStat Stat, byte[] Target) : TreeRecord;

public sealed record TreeSpecial(byte[] Name, TreeStat Stat, TreeSpecialType Type, long Rdev) : TreeRecord;

public sealed record TreeIssue(byte[] Name, string Reason) : TreeRecord;

public sealed record TreeHeartbeat(long Entries, long Bytes, string Path) : TreeRecord;

public sealed record TreeEnd(long Entries, long Bytes) : TreeRecord;

/// <summary>Writes the stream (the mover's side). Thread-safe per record: a heartbeat may come from a timer.</summary>
public sealed class MoverTreeWriter(Stream output)
{
    private readonly Lock _lock = new();
    private readonly byte[] _scratch = new byte[64];

    public void Header(TreeStat root)
    {
        lock (_lock)
        {
            output.Write(MoverTree.Magic);
            WriteStat(root);
        }
    }

    public void Enter(ReadOnlySpan<byte> name, TreeStat stat) => Named((byte)'D', name, stat);

    public void Leave() => Record((byte)'U');

    public void FileStart(ReadOnlySpan<byte> name, TreeStat stat) => Named((byte)'F', name, stat);

    public void Piece(ReadOnlySpan<byte> data)
    {
        if (data.Length > MoverTree.MaxPiece)
        {
            throw new ArgumentException($"A piece has at most {MoverTree.MaxPiece} bytes.", nameof(data));
        }

        lock (_lock)
        {
            output.WriteByte((byte)'C');
            UInt((ulong)data.Length);
            output.Write(data);
        }
    }

    public void Hole(long length)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'0');
            UInt((ulong)length);
        }
    }

    public void FileEnd(TreeFileEnd end, long length, bool holes, bool racy)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'Z');
            output.WriteByte((byte)end);
            UInt((ulong)length);
            output.WriteByte((byte)((holes ? 1 : 0) | (racy ? 2 : 0)));
        }
    }

    public void FileAbort(string reason)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'X');
            Bytes(Encoding.UTF8.GetBytes(reason));
        }
    }

    public void Unchanged(ReadOnlySpan<byte> name, TreeStat stat) => Named((byte)'S', name, stat);

    public void HardLink(ReadOnlySpan<byte> name, TreeStat stat) => Named((byte)'H', name, stat);

    public void Symlink(ReadOnlySpan<byte> name, TreeStat stat, ReadOnlySpan<byte> target)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'L');
            Bytes(name);
            WriteStat(stat);
            Bytes(target);
        }
    }

    public void Special(ReadOnlySpan<byte> name, TreeStat stat, TreeSpecialType type, long rdev)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'P');
            Bytes(name);
            WriteStat(stat);
            output.WriteByte((byte)type);
            Int(rdev);
        }
    }

    public void Issue(ReadOnlySpan<byte> name, string reason)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'I');
            Bytes(name);
            Bytes(Encoding.UTF8.GetBytes(reason));
        }
    }

    public void Heartbeat(long entries, long bytes, string path)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'B');
            UInt((ulong)entries);
            UInt((ulong)bytes);
            Bytes(Encoding.UTF8.GetBytes(path));
            output.Flush();
        }
    }

    public void End(long entries, long bytes)
    {
        lock (_lock)
        {
            output.WriteByte((byte)'E');
            UInt((ulong)entries);
            UInt((ulong)bytes);
            output.Flush();
        }
    }

    private void Record(byte type)
    {
        lock (_lock)
        {
            output.WriteByte(type);
        }
    }

    private void Named(byte type, ReadOnlySpan<byte> name, TreeStat stat)
    {
        lock (_lock)
        {
            output.WriteByte(type);
            Bytes(name);
            WriteStat(stat);
        }
    }

    private void WriteStat(TreeStat s)
    {
        UInt((ulong)s.Mode);
        Int(s.Uid);
        Int(s.Gid);
        Int(s.MtimeNs);
        Int(s.CtimeNs);
        UInt(s.Inode);
        Int(s.Links);
        Int(s.Size);
    }

    private void Bytes(ReadOnlySpan<byte> value)
    {
        UInt((ulong)value.Length);
        output.Write(value);
    }

    private void Int(long value) => UInt((ulong)((value << 1) ^ (value >> 63)));

    private void UInt(ulong value)
    {
        var n = 0;
        while (value >= 0x80)
        {
            _scratch[n++] = (byte)(value | 0x80);
            value >>= 7;
        }

        _scratch[n++] = (byte)value;
        output.Write(_scratch, 0, n);
    }
}

/// <summary>Reads the stream (the server's side), asynchronously from a request body.</summary>
public sealed class MoverTreeReader(Stream input)
{
    private readonly byte[] _buffer = new byte[256 * 1024];
    private byte[] _piece = new byte[64 * 1024];
    private int _start;
    private int _end;

    public async ValueTask<TreeStat> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var magic = new byte[MoverTree.Magic.Length];
        await ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(MoverTree.Magic))
        {
            throw new InvalidDataException("The mover's stream is no file tree (no BDFUTRE1 header).");
        }

        return await ReadStatAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next record; after <see cref="TreeEnd"/> there is none.</summary>
    public async ValueTask<TreeRecord> ReadAsync(CancellationToken cancellationToken)
    {
        var type = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        switch ((char)type)
        {
            case 'D':
                return new TreeEnter(await BytesAsync(cancellationToken).ConfigureAwait(false), await ReadStatAsync(cancellationToken).ConfigureAwait(false));
            case 'U':
                return TreeLeave.Instance;
            case 'F':
                return new TreeFileStart(await BytesAsync(cancellationToken).ConfigureAwait(false), await ReadStatAsync(cancellationToken).ConfigureAwait(false));
            case 'C':
            {
                var length = checked((int)await UIntAsync(cancellationToken).ConfigureAwait(false));
                if (length > MoverTree.MaxPiece)
                {
                    throw new InvalidDataException($"The mover sent a piece of {length} bytes (at most {MoverTree.MaxPiece}).");
                }

                if (_piece.Length < length)
                {
                    _piece = new byte[Math.Max(length, _piece.Length * 2)];
                }

                await ReadExactlyAsync(_piece.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                return new TreePiece(_piece.AsMemory(0, length));
            }

            case '0':
                return new TreeHole((long)await UIntAsync(cancellationToken).ConfigureAwait(false));
            case 'Z':
            {
                var end = (TreeFileEnd)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                var length = (long)await UIntAsync(cancellationToken).ConfigureAwait(false);
                var flags = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                return new TreeFileEndRecord(end, length, (flags & 1) != 0, (flags & 2) != 0);
            }

            case 'X':
                return new TreeFileAbort(Encoding.UTF8.GetString(await BytesAsync(cancellationToken).ConfigureAwait(false)));
            case 'S':
                return new TreeUnchanged(await BytesAsync(cancellationToken).ConfigureAwait(false), await ReadStatAsync(cancellationToken).ConfigureAwait(false));
            case 'H':
                return new TreeHardLink(await BytesAsync(cancellationToken).ConfigureAwait(false), await ReadStatAsync(cancellationToken).ConfigureAwait(false));
            case 'L':
                return new TreeSymlink(await BytesAsync(cancellationToken).ConfigureAwait(false), await ReadStatAsync(cancellationToken).ConfigureAwait(false),
                    await BytesAsync(cancellationToken).ConfigureAwait(false));
            case 'P':
            {
                var name = await BytesAsync(cancellationToken).ConfigureAwait(false);
                var stat = await ReadStatAsync(cancellationToken).ConfigureAwait(false);
                var special = (TreeSpecialType)await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                return new TreeSpecial(name, stat, special, await IntAsync(cancellationToken).ConfigureAwait(false));
            }

            case 'I':
                return new TreeIssue(await BytesAsync(cancellationToken).ConfigureAwait(false), Encoding.UTF8.GetString(await BytesAsync(cancellationToken).ConfigureAwait(false)));
            case 'B':
                return new TreeHeartbeat((long)await UIntAsync(cancellationToken).ConfigureAwait(false), (long)await UIntAsync(cancellationToken).ConfigureAwait(false),
                    Encoding.UTF8.GetString(await BytesAsync(cancellationToken).ConfigureAwait(false)));
            case 'E':
                return new TreeEnd((long)await UIntAsync(cancellationToken).ConfigureAwait(false), (long)await UIntAsync(cancellationToken).ConfigureAwait(false));
            default:
                throw new InvalidDataException($"The mover sent a file tree record of unknown type {type}.");
        }
    }

    private async ValueTask<TreeStat> ReadStatAsync(CancellationToken cancellationToken) => new(
        (int)await UIntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false),
        await UIntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false),
        await IntAsync(cancellationToken).ConfigureAwait(false));

    private async ValueTask<byte[]> BytesAsync(CancellationToken cancellationToken)
    {
        var length = checked((int)await UIntAsync(cancellationToken).ConfigureAwait(false));
        if (length > 64 * 1024)
        {
            throw new InvalidDataException($"The mover sent a name or text of {length} bytes.");
        }

        var bytes = new byte[length];
        await ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private async ValueTask<long> IntAsync(CancellationToken cancellationToken)
    {
        var raw = await UIntAsync(cancellationToken).ConfigureAwait(false);
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    private async ValueTask<ulong> UIntAsync(CancellationToken cancellationToken)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new InvalidDataException("The mover sent a number that is too long.");
    }

    private async ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The mover's stream ended before its end record.");
        }

        return _buffer[_start++];
    }

    private async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (destination.Length > 0)
        {
            if (_start == _end)
            {
                // Large reads go straight into the destination.
                if (destination.Length >= _buffer.Length)
                {
                    await input.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new EndOfStreamException("The mover's stream ended before its end record.");
                }
            }

            var n = Math.Min(destination.Length, _end - _start);
            _buffer.AsMemory(_start, n).CopyTo(destination);
            _start += n;
            destination = destination[n..];
        }
    }

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await input.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
        return _end > 0;
    }
}
