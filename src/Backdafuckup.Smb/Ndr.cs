using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Smb;

/// <summary>
/// The part of NDR 2.0 (DCE/RPC transfer syntax, little-endian) that the MS-SCMR calls need: 32-bit integers, context
/// handles, conformant varying strings (<c>[string] wchar_t*</c>) and top-level unique pointers.
/// </summary>
internal sealed class NdrWriter
{
    private readonly MemoryStream _stream = new();
    private uint _referent = 0x00020000;

    public NdrWriter UInt32(uint value)
    {
        Align(4);
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
        return this;
    }

    /// <summary>An RPC context handle (20 opaque bytes).</summary>
    public NdrWriter Handle(ReadOnlySpan<byte> handle)
    {
        if (handle.Length != ScmHandle.Size)
        {
            throw new ArgumentException("A context handle has 20 bytes.", nameof(handle));
        }

        Align(4);
        _stream.Write(handle);
        return this;
    }

    /// <summary><c>[string] wchar_t*</c> (reference pointer): max count, offset 0, actual count, UTF-16 with terminator.</summary>
    public NdrWriter String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var count = (uint)value.Length + 1;
        UInt32(count).UInt32(0).UInt32(count);
        _stream.Write(Encoding.Unicode.GetBytes(value + '\0'));
        return this;
    }

    /// <summary><c>[string, unique] wchar_t*</c>: a referent ID followed by the string, or 0 for null.</summary>
    public NdrWriter UniqueString(string? value)
    {
        if (value is null)
        {
            return UInt32(0);
        }

        UInt32(NextReferent());
        return String(value);
    }

    /// <summary>A null unique pointer (e.g. no tag ID, no dependencies, no password).</summary>
    public NdrWriter Null() => UInt32(0);

    public byte[] ToArray() => _stream.ToArray();

    private uint NextReferent()
    {
        var referent = _referent;
        _referent += 4;
        return referent;
    }

    private void Align(int alignment)
    {
        while (_stream.Length % alignment != 0)
        {
            _stream.WriteByte(0);
        }
    }
}

internal sealed class NdrReader(byte[] data)
{
    private int _position;

    public uint UInt32()
    {
        Align(4);
        if (_position + 4 > data.Length)
        {
            throw new InvalidDataException("The RPC response is too short.");
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position));
        _position += 4;
        return value;
    }

    public ScmHandle Handle()
    {
        Align(4);
        if (_position + ScmHandle.Size > data.Length)
        {
            throw new InvalidDataException("The RPC response is too short for a context handle.");
        }

        var handle = new ScmHandle(data.AsSpan(_position, ScmHandle.Size).ToArray());
        _position += ScmHandle.Size;
        return handle;
    }

    /// <summary>A unique pointer to a DWORD: the value, or null for a null pointer.</summary>
    public uint? UniqueUInt32() => UInt32() == 0 ? null : UInt32();

    private void Align(int alignment) => _position = (_position + alignment - 1) / alignment * alignment;
}

/// <summary>An SCM context handle (opaque, 20 bytes).</summary>
public sealed class ScmHandle(byte[] value)
{
    public const int Size = 20;

    public byte[] Value { get; } = value;

    public bool IsNull => Value.All(b => b == 0);
}
