using System.Buffers.Binary;
using System.Security.Cryptography;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>
/// Pack file format v1 (repository format 2): many chunks in one immutable file.
/// <code>
/// header   32 bytes  "BDPK", version 1, flags 0, 2 reserved, repository id (16), 8 reserved
/// records  per chunk: chunk id (32), then its unchanged BDCK record (see <see cref="ChunkCodec"/>)
/// trailer  per record 44 bytes: chunk id (32), offset of the BDCK record (4), its length (4), raw length (4)
/// footer   64 bytes  "BDPF", version 1, 3 reserved, entry count (4), trailer offset (8), trailer length (8),
///                    SHA-256 of the trailer (32), 4 reserved
/// </code>
/// All integers are big-endian. The trailer describes every record, so the repository index can always be rebuilt
/// from the packs; the chunk id before each record lets a salvage scan read a pack whose trailer is damaged.
/// </summary>
public static class PackFormat
{
    public const int HeaderSize = 32;
    public const int RecordIdSize = ChunkId.Size;
    public const int TrailerEntrySize = 44;
    public const int FooterSize = 64;
    public const byte Version = 1;

    /// <summary>Largest pack the format allows (offsets are 32-bit).</summary>
    public const long MaxPackSize = 1L << 30;

    public const string Extension = ".pack";

    private static readonly uint HeaderMagic = BinaryPrimitives.ReadUInt32BigEndian("BDPK"u8);
    private static readonly uint FooterMagic = BinaryPrimitives.ReadUInt32BigEndian("BDPF"u8);

    public static void WriteHeader(Span<byte> destination, Guid repositoryId)
    {
        destination[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination, HeaderMagic);
        destination[4] = Version;
        repositoryId.TryWriteBytes(destination.Slice(8, 16), bigEndian: true, out _);
    }

    /// <summary>Checks the header and returns the repository id it names.</summary>
    public static Guid ReadHeader(ReadOnlySpan<byte> header, string path)
    {
        if (header.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(header) != HeaderMagic)
        {
            throw new InvalidDataException($"'{path}' is not a pack file (bad header).");
        }

        if (header[4] != Version)
        {
            throw new InvalidDataException($"Pack '{path}' has version {header[4]}, which this version does not know.");
        }

        return new Guid(header.Slice(8, 16), bigEndian: true);
    }

    public static void WriteTrailerEntry(Span<byte> destination, in PackTrailerEntry entry)
    {
        entry.Id.WriteTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[32..], entry.Offset);
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..], entry.StoredLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[40..], entry.RawLength);
    }

    public static PackTrailerEntry ReadTrailerEntry(ReadOnlySpan<byte> source) => new(
        ChunkId.FromBytes(source[..32]),
        BinaryPrimitives.ReadUInt32BigEndian(source[32..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[36..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[40..]));

    /// <summary>Builds the trailer and footer for <paramref name="entries"/>; the trailer starts at <paramref name="trailerOffset"/>.</summary>
    public static byte[] BuildTail(IReadOnlyList<PackTrailerEntry> entries, long trailerOffset)
    {
        var trailerLength = entries.Count * TrailerEntrySize;
        var tail = new byte[trailerLength + FooterSize];
        for (var i = 0; i < entries.Count; i++)
        {
            WriteTrailerEntry(tail.AsSpan(i * TrailerEntrySize, TrailerEntrySize), entries[i]);
        }

        var footer = tail.AsSpan(trailerLength, FooterSize);
        BinaryPrimitives.WriteUInt32BigEndian(footer, FooterMagic);
        footer[4] = Version;
        BinaryPrimitives.WriteUInt32BigEndian(footer[8..], (uint)entries.Count);
        BinaryPrimitives.WriteInt64BigEndian(footer[12..], trailerOffset);
        BinaryPrimitives.WriteInt64BigEndian(footer[20..], trailerLength);
        SHA256.HashData(tail.AsSpan(0, trailerLength), footer.Slice(28, 32));
        return tail;
    }

    /// <summary>Reads and checks the footer and trailer of a sealed pack.</summary>
    public static PackTrailerEntry[] ReadTrailer(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string path)
    {
        var length = RandomAccess.GetLength(handle);
        if (length < HeaderSize + FooterSize)
        {
            throw new InvalidDataException($"Pack '{path}' is too short ({length} bytes) to have a footer.");
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExactly(handle, footer, length - FooterSize, path);
        if (BinaryPrimitives.ReadUInt32BigEndian(footer) != FooterMagic || footer[4] != Version)
        {
            throw new InvalidDataException($"Pack '{path}' has no valid footer (not sealed or damaged).");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(footer[8..]);
        var trailerOffset = BinaryPrimitives.ReadInt64BigEndian(footer[12..]);
        var trailerLength = BinaryPrimitives.ReadInt64BigEndian(footer[20..]);
        if (trailerLength != (long)count * TrailerEntrySize || trailerOffset < HeaderSize || trailerOffset + trailerLength + FooterSize != length)
        {
            throw new InvalidDataException($"Pack '{path}' has an inconsistent footer (trailer at {trailerOffset}, {trailerLength} bytes, file {length} bytes).");
        }

        var trailer = new byte[trailerLength];
        ReadExactly(handle, trailer, trailerOffset, path);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(trailer, hash);
        if (!hash.SequenceEqual(footer.Slice(28, 32)))
        {
            throw new InvalidDataException($"Pack '{path}' has a damaged trailer (SHA-256 mismatch).");
        }

        var entries = new PackTrailerEntry[count];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = ReadTrailerEntry(trailer.AsSpan(i * TrailerEntrySize, TrailerEntrySize));
            if (entry.Offset < HeaderSize + RecordIdSize || (long)entry.Offset + entry.StoredLength > trailerOffset || entry.StoredLength < ChunkCodec.HeaderSize)
            {
                throw new InvalidDataException($"Pack '{path}' has a trailer entry outside its records (offset {entry.Offset}, {entry.StoredLength} bytes).");
            }

            entries[i] = entry;
        }

        return entries;
    }

    /// <summary>
    /// Reads every record from the start of a pack without its trailer (for packs whose trailer is damaged). Stops at the
    /// first record that does not decode; each returned record has been verified against its id.
    /// </summary>
    public static List<PackTrailerEntry> Salvage(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int maxRawLength)
    {
        var entries = new List<PackTrailerEntry>();
        var length = RandomAccess.GetLength(handle);
        long position = HeaderSize;
        var idBytes = new byte[RecordIdSize + ChunkCodec.HeaderSize];
        var plain = new byte[maxRawLength];
        while (position + idBytes.Length <= length)
        {
            if (RandomAccess.Read(handle, idBytes, position) != idBytes.Length)
            {
                break;
            }

            var id = ChunkId.FromBytes(idBytes.AsSpan(0, RecordIdSize));
            int payload, raw;
            try
            {
                (_, raw, payload) = ChunkCodec.ReadHeader(id, idBytes.AsSpan(RecordIdSize));
            }
            catch (ChunkCorruptException)
            {
                break;
            }

            var stored = ChunkCodec.HeaderSize + payload;
            if (raw > maxRawLength || position + RecordIdSize + stored > length)
            {
                break;
            }

            var record = new byte[stored];
            if (RandomAccess.Read(handle, record, position + RecordIdSize) != stored)
            {
                break;
            }

            try
            {
                ChunkCodec.Decode(id, record, plain);
            }
            catch (ChunkCorruptException)
            {
                break;
            }

            entries.Add(new PackTrailerEntry(id, (uint)(position + RecordIdSize), (uint)stored, (uint)raw));
            position += RecordIdSize + stored;
        }

        return entries;
    }

    internal static void ReadExactly(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> destination, long offset, string path)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = RandomAccess.Read(handle, destination[read..], offset + read);
            if (n == 0)
            {
                throw new InvalidDataException($"'{path}' ends at {offset + read}, expected {destination.Length - read} more bytes.");
            }

            read += n;
        }
    }
}

/// <summary>One record of a pack: where its BDCK record starts, how long it is and how long the chunk's content is.</summary>
public readonly record struct PackTrailerEntry(ChunkId Id, uint Offset, uint StoredLength, uint RawLength);
