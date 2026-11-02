using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Backdafuckup.Core.Repository;

/// <summary>
/// Content address of a chunk: SHA-256 of the uncompressed chunk data.
/// <see cref="Zero"/> (all 32 bytes zero) is reserved as the sentinel for an all-zero chunk, which is never stored.
/// </summary>
public readonly struct ChunkId : IEquatable<ChunkId>
{
    public const int Size = 32;

    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;
    private readonly ulong _d;

    private ChunkId(ulong a, ulong b, ulong c, ulong d)
    {
        _a = a;
        _b = b;
        _c = c;
        _d = d;
    }

    public static ChunkId Zero => default;

    public bool IsZero => (_a | _b | _c | _d) == 0;

    public static ChunkId Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[Size];
        SHA256.HashData(data, hash);
        return FromBytes(hash);
    }

    public static ChunkId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A chunk id has exactly {Size} bytes.", nameof(bytes));
        }

        return new ChunkId(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
    }

    public static ChunkId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return FromBytes(Convert.FromHexString(hex));
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _a);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _b);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _c);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _d);
    }

    public string ToHex()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>First two bytes as 4 lowercase hex characters (used for directory fan-out).</summary>
    public string Prefix => ToHex()[..4];

    /// <summary>Cheap stripe selector for lock striping.</summary>
    public int Stripe(int stripes) => (int)(_a % (ulong)stripes);

    public bool Equals(ChunkId other) => _a == other._a && _b == other._b && _c == other._c && _d == other._d;

    public override bool Equals(object? obj) => obj is ChunkId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_a, _d);

    public static bool operator ==(ChunkId left, ChunkId right) => left.Equals(right);

    public static bool operator !=(ChunkId left, ChunkId right) => !left.Equals(right);

    public override string ToString() => IsZero ? "zero" : ToHex();
}
