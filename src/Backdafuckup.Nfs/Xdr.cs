using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Nfs;

/// <summary>The arguments of a call do not decode (too short, a length beyond its limit); the call is answered GARBAGE_ARGS.</summary>
public sealed class XdrException(string message) : Exception(message);

/// <summary>Reads XDR (RFC 4506): big-endian 32-bit units, variable-length data padded to 4 bytes.</summary>
public sealed class XdrReader(ReadOnlyMemory<byte> data)
{
    private int _position;

    public int Position => _position;

    public int Remaining => data.Length - _position;

    public uint ReadUInt32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.Span[_position..]);
        _position += 4;
        return value;
    }

    public int ReadInt32() => unchecked((int)ReadUInt32());

    public ulong ReadUInt64()
    {
        Need(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(data.Span[_position..]);
        _position += 8;
        return value;
    }

    public long ReadInt64() => unchecked((long)ReadUInt64());

    public bool ReadBool() => ReadUInt32() switch
    {
        0 => false,
        1 => true,
        var other => throw new XdrException($"A boolean is {other}, not 0 or 1."),
    };

    /// <summary>Variable-length opaque data of at most <paramref name="max"/> bytes (a slice of the message, not copied).</summary>
    public ReadOnlyMemory<byte> ReadOpaque(int max)
    {
        var length = ReadUInt32();
        if (length > (uint)max)
        {
            throw new XdrException($"Opaque data of {length} bytes exceeds its limit of {max}.");
        }

        return ReadFixedOpaque((int)length);
    }

    /// <summary>Fixed-length opaque data (padded to 4 bytes).</summary>
    public ReadOnlyMemory<byte> ReadFixedOpaque(int length)
    {
        var padded = (length + 3) & ~3;
        Need(padded);
        var value = data.Slice(_position, length);
        _position += padded;
        return value;
    }

    /// <summary>A string of at most <paramref name="max"/> bytes; NFS names are UTF-8 (invalid bytes are refused).</summary>
    public string ReadString(int max)
    {
        var bytes = ReadOpaque(max);
        try
        {
            return Strict.GetString(bytes.Span);
        }
        catch (DecoderFallbackException)
        {
            throw new XdrException("A string is not valid UTF-8.");
        }
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private void Need(int bytes)
    {
        if (bytes < 0 || Remaining < bytes)
        {
            throw new XdrException($"The message ends after {data.Length} bytes; {bytes} more were expected at offset {_position}.");
        }
    }
}

/// <summary>Writes XDR (RFC 4506) into a growing buffer.</summary>
public sealed class XdrWriter
{
    private readonly ArrayBufferWriter<byte> _buffer;

    public XdrWriter(int initialCapacity = 256) => _buffer = new ArrayBufferWriter<byte>(initialCapacity);

    public int Length => _buffer.WrittenCount;

    public ReadOnlyMemory<byte> Written => _buffer.WrittenMemory;

    public XdrWriter WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetSpan(4), value);
        _buffer.Advance(4);
        return this;
    }

    public XdrWriter WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public XdrWriter WriteUInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
        return this;
    }

    public XdrWriter WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public XdrWriter WriteBool(bool value) => WriteUInt32(value ? 1u : 0u);

    public XdrWriter WriteOpaque(ReadOnlySpan<byte> value)
    {
        WriteUInt32((uint)value.Length);
        return WriteFixedOpaque(value);
    }

    public XdrWriter WriteFixedOpaque(ReadOnlySpan<byte> value)
    {
        var padded = (value.Length + 3) & ~3;
        var span = _buffer.GetSpan(padded);
        value.CopyTo(span);
        span[value.Length..padded].Clear();
        _buffer.Advance(padded);
        return this;
    }

    public XdrWriter WriteString(string value) => WriteOpaque(Encoding.UTF8.GetBytes(value));

    /// <summary>Reserves a 32-bit unit to fill in later (e.g. a count known only after the entries); returns its offset.</summary>
    public int Reserve()
    {
        var at = _buffer.WrittenCount;
        WriteUInt32(0);
        return at;
    }

    public void Patch(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.MemoryMarshal.AsMemory(_buffer.WrittenMemory).Span[offset..], value);

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}
