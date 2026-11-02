using System.Buffers;
using System.Buffers.Binary;
using Backdafuckup.Core.Repository;
using ZstdSharp;

namespace Backdafuckup.Storage;

/// <summary>
/// Chunk file format v1: 16-byte header followed by the payload.
/// <code>
/// 0  4  magic "BDCK"
/// 4  1  version (1)
/// 5  1  codec (0 = none, 1 = zstd)
/// 6  2  flags (bit 0 reserved: encrypted)
/// 8  4  raw (uncompressed) length
/// 12 4  payload length
/// </code>
/// The file name is the SHA-256 of the raw content; every decode verifies it.
/// </summary>
public static class ChunkCodec
{
    public const int HeaderSize = 16;
    public const byte Version = 1;
    public const byte CodecNone = 0;
    public const byte CodecZstd = 1;

    private static readonly uint Magic = BinaryPrimitives.ReadUInt32BigEndian("BDCK"u8);

    [ThreadStatic]
    private static Compressor? t_compressor;

    [ThreadStatic]
    private static int t_compressorLevel;

    [ThreadStatic]
    private static Decompressor? t_decompressor;

    /// <summary>Encodes a chunk. The caller returns the rented buffer to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public static (byte[] Buffer, int Length) Encode(ReadOnlySpan<byte> plaintext, int compressionLevel)
    {
        var bound = Compressor.GetCompressBound(plaintext.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + Math.Max(bound, plaintext.Length));
        var codec = CodecNone;
        var payloadLength = plaintext.Length;

        if (compressionLevel > 0)
        {
            var compressor = GetCompressor(compressionLevel);
            var compressed = compressor.Wrap(plaintext, buffer.AsSpan(HeaderSize));
            if (compressed < plaintext.Length)
            {
                codec = CodecZstd;
                payloadLength = compressed;
            }
        }

        if (codec == CodecNone)
        {
            plaintext.CopyTo(buffer.AsSpan(HeaderSize));
        }

        var header = buffer.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
        header[4] = Version;
        header[5] = codec;
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)plaintext.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)payloadLength);
        return (buffer, HeaderSize + payloadLength);
    }

    /// <summary>Validates a chunk header and returns (codec, raw length, payload length).</summary>
    public static (byte Codec, int RawLength, int PayloadLength) ReadHeader(ChunkId id, ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(header) != Magic)
        {
            throw new ChunkCorruptException(id, "bad header magic");
        }

        if (header[4] != Version)
        {
            throw new ChunkCorruptException(id, $"unsupported chunk version {header[4]}");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
        if (flags != 0)
        {
            throw new ChunkCorruptException(id, $"unsupported chunk flags 0x{flags:x4}");
        }

        var codec = header[5];
        if (codec is not (CodecNone or CodecZstd))
        {
            throw new ChunkCorruptException(id, $"unknown codec {codec}");
        }

        var raw = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var payload = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (raw > int.MaxValue || payload > int.MaxValue)
        {
            throw new ChunkCorruptException(id, "invalid lengths");
        }

        return (codec, (int)raw, (int)payload);
    }

    /// <summary>Decodes a complete chunk file into <paramref name="destination"/> and verifies its hash.</summary>
    public static int Decode(ChunkId id, ReadOnlySpan<byte> file, Span<byte> destination)
    {
        var (codec, raw, payload) = ReadHeader(id, file);
        if (file.Length != HeaderSize + payload)
        {
            throw new ChunkCorruptException(id, $"file length {file.Length} does not match header ({HeaderSize + payload})");
        }

        if (destination.Length < raw)
        {
            throw new ArgumentException($"Destination too small for chunk {id} ({raw} bytes).", nameof(destination));
        }

        var body = file.Slice(HeaderSize, payload);
        int written;
        if (codec == CodecNone)
        {
            body.CopyTo(destination);
            written = payload;
        }
        else
        {
            try
            {
                written = (t_decompressor ??= new Decompressor()).Unwrap(body, destination[..raw]);
            }
            catch (ZstdException ex)
            {
                throw new ChunkCorruptException(id, $"decompression failed: {ex.Message}");
            }
        }

        if (written != raw)
        {
            throw new ChunkCorruptException(id, $"decoded {written} bytes, expected {raw}");
        }

        if (ChunkId.Compute(destination[..raw]) != id)
        {
            throw new ChunkCorruptException(id, "content hash mismatch");
        }

        return raw;
    }

    private static Compressor GetCompressor(int level)
    {
        if (t_compressor is null || t_compressorLevel != level)
        {
            t_compressor?.Dispose();
            t_compressor = new Compressor(level);
            t_compressorLevel = level;
        }

        return t_compressor;
    }
}
