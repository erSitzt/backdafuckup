using System.Buffers.Binary;
using System.Security.Cryptography;
using Backdafuckup.Core.Repository;
using ZstdSharp;

namespace Backdafuckup.Storage;

/// <summary>
/// Disk index file format v1 (the whole file is zstd-compressed):
/// <code>
/// 0  4  magic "BDIX"
/// 4  1  version (1)
/// 5  3  reserved (0)
/// 8  4  chunk size
/// 12 8  disk size
/// 20 8  entry count
/// 28 .. entries, 32 bytes each (SHA-256 chunk id, all zero = zero chunk)
/// </code>
/// The manifest stores the SHA-256 of the uncompressed content.
/// </summary>
public static class ChunkIndexFormat
{
    public const int HeaderSize = 28;
    public const byte Version = 1;
    private static readonly uint Magic = BinaryPrimitives.ReadUInt32BigEndian("BDIX"u8);

    public static byte[] Serialize(ChunkIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var raw = new byte[HeaderSize + (index.Entries.LongLength * ChunkId.Size)];
        var span = raw.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, Magic);
        span[4] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)index.ChunkSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[12..], (ulong)index.DiskSize);
        BinaryPrimitives.WriteUInt64BigEndian(span[20..], (ulong)index.Entries.LongLength);
        for (var i = 0; i < index.Entries.Length; i++)
        {
            index.Entries[i].WriteTo(span.Slice(HeaderSize + (i * ChunkId.Size), ChunkId.Size));
        }

        return raw;
    }

    public static ChunkIndex Deserialize(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(raw) != Magic)
        {
            throw new InvalidDataException("Not a chunk index file.");
        }

        if (raw[4] != Version)
        {
            throw new InvalidDataException($"Unsupported chunk index version {raw[4]}.");
        }

        var chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(raw[8..]);
        var diskSize = (long)BinaryPrimitives.ReadUInt64BigEndian(raw[12..]);
        var count = (long)BinaryPrimitives.ReadUInt64BigEndian(raw[20..]);
        if (raw.Length != HeaderSize + (count * ChunkId.Size))
        {
            throw new InvalidDataException("Chunk index length does not match its entry count.");
        }

        var entries = new ChunkId[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = ChunkId.FromBytes(raw.Slice(HeaderSize + (i * ChunkId.Size), ChunkId.Size));
        }

        return new ChunkIndex(chunkSize, diskSize, entries);
    }

    /// <summary>Compresses serialized index (or blob) content.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> raw, int level = 3)
    {
        using var compressor = new Compressor(level);
        return compressor.Wrap(raw).ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
