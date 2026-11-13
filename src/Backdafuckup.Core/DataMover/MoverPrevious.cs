namespace Backdafuckup.Core.DataMover;

/// <summary>
/// The tree of a volume's previous run, as the server sends it to the mover for an incremental read (<c>BDFUPRV1</c>, spec
/// 2026-11-12-1952-kubernetes-file-level-volumes): in the canonical order of <see cref="MoverTree"/> (depth first, each
/// folder's entries sorted by their name bytes), but only folders and the files whose content can be taken over (read in
/// full, not changed while read, not racy). <c>D</c> name enters a folder, <c>U</c> leaves it, <c>F</c> name, size, mtime,
/// ctime and inode is a file, <c>E</c> ends the root folder. Integers are varints (signed ones zig-zag).
/// </summary>
public static class MoverPrevious
{
    public static ReadOnlySpan<byte> Magic => "BDFUPRV1"u8;
}

/// <summary>What the previous run saw of a file: it is unchanged when all four are the same now.</summary>
public readonly record struct PreviousFile(long Size, long MtimeNs, long CtimeNs, ulong Inode)
{
    public bool Matches(TreeStat stat) => stat.Size == Size && stat.MtimeNs == MtimeNs && stat.CtimeNs == CtimeNs && stat.Inode == Inode;
}

public enum PreviousRecordKind : byte
{
    Enter = (byte)'D',
    Leave = (byte)'U',
    File = (byte)'F',
    End = (byte)'E',
}

public readonly record struct PreviousRecord(PreviousRecordKind Kind, byte[] Name, PreviousFile File);

/// <summary>Writes the stream (the server's side), synchronously into a buffer the caller sends on.</summary>
public sealed class MoverPreviousWriter(Stream output)
{
    private readonly byte[] _scratch = new byte[10];

    public void Header() => output.Write(MoverPrevious.Magic);

    public void Enter(ReadOnlySpan<byte> name)
    {
        output.WriteByte((byte)PreviousRecordKind.Enter);
        Bytes(name);
    }

    public void Leave() => output.WriteByte((byte)PreviousRecordKind.Leave);

    public void File(ReadOnlySpan<byte> name, PreviousFile file)
    {
        output.WriteByte((byte)PreviousRecordKind.File);
        Bytes(name);
        Int(file.Size);
        Int(file.MtimeNs);
        Int(file.CtimeNs);
        UInt(file.Inode);
    }

    public void End() => output.WriteByte((byte)PreviousRecordKind.End);

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

/// <summary>Reads the stream (the mover's side), synchronously: the walk that consumes it runs on a thread of its own.</summary>
public sealed class MoverPreviousReader(Stream input)
{
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _ended;

    public void ReadHeader()
    {
        Span<byte> magic = stackalloc byte[MoverPrevious.Magic.Length];
        for (var i = 0; i < magic.Length; i++)
        {
            magic[i] = Byte();
        }

        if (!magic.SequenceEqual(MoverPrevious.Magic))
        {
            throw new InvalidDataException("The server's previous tree has no BDFUPRV1 header.");
        }
    }

    /// <summary>The next record; after <see cref="PreviousRecordKind.End"/> it keeps returning the end.</summary>
    public PreviousRecord Read()
    {
        if (_ended)
        {
            return new PreviousRecord(PreviousRecordKind.End, [], default);
        }

        var kind = (PreviousRecordKind)Byte();
        switch (kind)
        {
            case PreviousRecordKind.Enter:
                return new PreviousRecord(kind, Bytes(), default);
            case PreviousRecordKind.Leave:
                return new PreviousRecord(kind, [], default);
            case PreviousRecordKind.File:
            {
                var name = Bytes();
                return new PreviousRecord(kind, name, new PreviousFile(Int(), Int(), Int(), UInt()));
            }

            case PreviousRecordKind.End:
                _ended = true;
                return new PreviousRecord(kind, [], default);
            default:
                throw new InvalidDataException($"The server's previous tree has a record of unknown type {(byte)kind}.");
        }
    }

    private byte[] Bytes()
    {
        var length = checked((int)UInt());
        if (length > 64 * 1024)
        {
            throw new InvalidDataException($"The server's previous tree has a name of {length} bytes.");
        }

        var bytes = new byte[length];
        for (var i = 0; i < length;)
        {
            if (_start == _end)
            {
                Fill();
            }

            var n = Math.Min(length - i, _end - _start);
            _buffer.AsSpan(_start, n).CopyTo(bytes.AsSpan(i));
            _start += n;
            i += n;
        }

        return bytes;
    }

    private long Int()
    {
        var raw = UInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    private ulong UInt()
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = Byte();
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }

        throw new InvalidDataException("The server's previous tree has a number that is too long.");
    }

    private byte Byte()
    {
        if (_start == _end)
        {
            Fill();
        }

        return _buffer[_start++];
    }

    private void Fill()
    {
        _start = 0;
        _end = input.Read(_buffer);
        if (_end <= 0)
        {
            _end = 0;
            throw new EndOfStreamException("The server's previous tree ended before its end record.");
        }
    }
}

/// <summary>
/// The previous run's tree, followed along with the walk of the volume (a streaming merge): the walk says which folder it
/// enters and leaves and which file it is at, in the same canonical order; the cursor skips what the walk no longer has and
/// tells whether a file is the same as before. Below a folder the previous run did not have, nothing is the same.
/// </summary>
public sealed class MoverPreviousCursor
{
    private readonly MoverPreviousReader _reader;
    private PreviousRecord? _peek;
    private int _detached;

    public MoverPreviousCursor(Stream input)
    {
        _reader = new MoverPreviousReader(input);
        _reader.ReadHeader();
    }

    /// <summary>Files the previous run had and the walk found unchanged.</summary>
    public long Unchanged { get; private set; }

    /// <summary>The walk enters the folder <paramref name="name"/> of the folder it is in.</summary>
    public void Enter(ReadOnlySpan<byte> name)
    {
        if (_detached > 0)
        {
            _detached++;
            return;
        }

        if (SkipTo(name) is { Kind: PreviousRecordKind.Enter })
        {
            _peek = null;
            return;
        }

        // A file of that name before (now a folder), or nothing: nothing below is the same.
        if (Peek() is { Kind: PreviousRecordKind.File } same && same.Name.AsSpan().SequenceEqual(name))
        {
            _peek = null;
        }

        _detached++;
    }

    /// <summary>The walk leaves the folder it is in (not the root).</summary>
    public void Leave()
    {
        if (_detached > 0)
        {
            _detached--;
            return;
        }

        SkipRest();
        if (Next().Kind != PreviousRecordKind.Leave)
        {
            throw new InvalidDataException("The server's previous tree ended inside a folder.");
        }
    }

    /// <summary>What the previous run saw of the file <paramref name="name"/> in the folder the walk is in, if it can be taken over.</summary>
    public PreviousFile? File(ReadOnlySpan<byte> name)
    {
        if (_detached > 0 || SkipTo(name) is not { Kind: PreviousRecordKind.File } file)
        {
            return null;
        }

        _peek = null;
        return file.File;
    }

    /// <summary>Whether the file <paramref name="name"/> with <paramref name="stat"/> is the same as in the previous run.</summary>
    public bool IsUnchanged(ReadOnlySpan<byte> name, TreeStat stat)
    {
        if (File(name) is { } previous && previous.Matches(stat))
        {
            Unchanged++;
            return true;
        }

        return false;
    }

    /// <summary>The walk is done: the rest of the root folder is skipped up to the end.</summary>
    public void Finish()
    {
        if (_detached > 0)
        {
            throw new InvalidOperationException("The walk finished inside a folder.");
        }

        SkipRest();
        if (Next().Kind != PreviousRecordKind.End)
        {
            throw new InvalidDataException("The server's previous tree left the root folder.");
        }
    }

    /// <summary>Skips the entries of the current folder (with their subtrees) whose names sort before <paramref name="name"/>; returns the entry of that name, not taken.</summary>
    private PreviousRecord? SkipTo(ReadOnlySpan<byte> name)
    {
        while (Peek() is { Kind: PreviousRecordKind.Enter or PreviousRecordKind.File } entry)
        {
            var order = entry.Name.AsSpan().SequenceCompareTo(name);
            if (order > 0)
            {
                return null;
            }

            if (order == 0)
            {
                return entry;
            }

            SkipEntry();
        }

        return null;
    }

    /// <summary>Skips everything left in the current folder, up to (not including) its <c>U</c> or the end.</summary>
    private void SkipRest()
    {
        while (Peek() is { Kind: PreviousRecordKind.Enter or PreviousRecordKind.File })
        {
            SkipEntry();
        }
    }

    /// <summary>Takes the next entry; a folder with everything in it.</summary>
    private void SkipEntry()
    {
        if (Next().Kind != PreviousRecordKind.Enter)
        {
            return;
        }

        for (var depth = 1; depth > 0;)
        {
            switch (Next().Kind)
            {
                case PreviousRecordKind.Enter:
                    depth++;
                    break;
                case PreviousRecordKind.Leave:
                    depth--;
                    break;
                case PreviousRecordKind.End:
                    throw new InvalidDataException("The server's previous tree ended inside a folder.");
            }
        }
    }

    private PreviousRecord Peek() => _peek ??= _reader.Read();

    private PreviousRecord Next()
    {
        var next = Peek();
        _peek = null;
        return next;
    }
}
