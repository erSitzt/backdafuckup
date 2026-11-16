using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Tape.Format;

public static class TapeFileTypes
{
    /// <summary>File 0 of every tape: one block, never encrypted.</summary>
    public const string Label = "label";

    /// <summary>Chunks (<c>CHNK</c>) and at the end their index (<c>AIDX</c>).</summary>
    public const string Archive = "archive";

    /// <summary>A copy of a restore point: its files (<c>ENTR</c>) and where its chunks are (<c>LOCS</c>).</summary>
    public const string RestorePoint = "restore-point";

    /// <summary>A copy of a SQL Server backup object, like a restore point.</summary>
    public const string Sql = "sql";

    /// <summary>The set's catalog (<c>CATL</c>): its tapes, files and copies; the last one on the highest tape is complete.</summary>
    public const string Catalog = "catalog";
}

/// <summary>The first record of every file, in clear: enough for a scan to know what the file is.</summary>
public sealed record TapeFileHeader
{
    public required string Type { get; init; }

    public required ulong FileId { get; init; }

    public Guid? SetId { get; init; }

    /// <summary>The tape's number in its set, from 1.</summary>
    public int Sequence { get; init; }

    /// <summary>The file's number on its tape: the label is 0.</summary>
    public int FileNumber { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>For restore-point and SQL files: the copy.</summary>
    public Guid? CopyId { get; init; }
}

/// <summary>The tape's label (file 0, record <c>LABL</c>): what the tape is, which set it belongs to, how it is encrypted.</summary>
public sealed record TapeLabel
{
    public const string FormatName = "BDFU-TAPE";

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = TapeFormat.Version;

    public required Guid MediumId { get; init; }

    public string? Barcode { get; init; }

    /// <summary>What people call the tape: its barcode, or the name to write on a cartridge without one.</summary>
    public required string Name { get; init; }

    public required int BlockSize { get; init; }

    public bool Worm { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>The server that labelled it (name, not address).</summary>
    public string? Server { get; init; }

    /// <summary>Null on a free tape.</summary>
    public TapeLabelSet? Set { get; init; }

    public TapeLabelEncryption? Encryption { get; init; }
}

public sealed record TapeLabelSet(Guid Id, int Sequence, Guid PoolId, string PoolName, DateTimeOffset CreatedUtc);

/// <param name="Mode"><c>software</c> or <c>hardware</c>.</param>
/// <param name="Fingerprint">Of the key-encryption key, e.g. <c>3F2A-91C0-7B11-02DE</c>.</param>
/// <param name="Salt">Base64.</param>
public sealed record TapeLabelEncryption(string Mode, string Fingerprint, string Kdf, int MemoryKiB, int Iterations, int Parallelism, string Salt);

/// <summary>
/// Where a chunk's record is: the tape (sequence), its file, the block of that file and the offset in the block's
/// payload, and the record's length (header and body).
/// </summary>
public readonly record struct ChunkLocation(ChunkId Id, int Sequence, int FileNumber, uint Block, int Offset, int Length);

/// <summary><c>LOCS</c> and <c>AIDX</c>: chunk locations sorted by id, 50 bytes each, zstd-compressed.</summary>
public static class ChunkLocations
{
    private const int EntrySize = 32 + 2 + 4 + 4 + 4 + 4;

    public static byte[] Encode(IEnumerable<ChunkLocation> locations)
    {
        var sorted = locations.OrderBy(l => l.Id).ToList();
        var raw = new byte[sorted.Count * EntrySize];
        for (var i = 0; i < sorted.Count; i++)
        {
            var span = raw.AsSpan(i * EntrySize, EntrySize);
            var l = sorted[i];
            l.Id.WriteTo(span);
            BinaryPrimitives.WriteUInt16BigEndian(span[32..], checked((ushort)l.Sequence));
            BinaryPrimitives.WriteInt32BigEndian(span[34..], l.FileNumber);
            BinaryPrimitives.WriteUInt32BigEndian(span[38..], l.Block);
            BinaryPrimitives.WriteInt32BigEndian(span[42..], l.Offset);
            BinaryPrimitives.WriteInt32BigEndian(span[46..], l.Length);
        }

        return TapeCompression.Compress(raw);
    }

    public static IReadOnlyList<ChunkLocation> Decode(ReadOnlySpan<byte> compressed)
    {
        var raw = TapeCompression.Decompress(compressed);
        if (raw.Length % EntrySize != 0)
        {
            throw new InvalidDataException($"A chunk location table has {raw.Length} bytes, no multiple of {EntrySize}.");
        }

        var result = new ChunkLocation[raw.Length / EntrySize];
        for (var i = 0; i < result.Length; i++)
        {
            var span = raw.AsSpan(i * EntrySize, EntrySize);
            result[i] = new ChunkLocation(
                ChunkId.FromBytes(span[..32]),
                BinaryPrimitives.ReadUInt16BigEndian(span[32..]),
                BinaryPrimitives.ReadInt32BigEndian(span[34..]),
                BinaryPrimitives.ReadUInt32BigEndian(span[38..]),
                BinaryPrimitives.ReadInt32BigEndian(span[42..]),
                BinaryPrimitives.ReadInt32BigEndian(span[46..]));
        }

        return result;
    }
}

/// <summary><c>ENTR</c>: one file of a copy (its name, then its bytes).</summary>
public static class TapeEntries
{
    public static byte[] Encode(string name, ReadOnlySpan<byte> content)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var body = new byte[2 + nameBytes.Length + content.Length];
        BinaryPrimitives.WriteUInt16BigEndian(body, checked((ushort)nameBytes.Length));
        nameBytes.CopyTo(body, 2);
        content.CopyTo(body.AsSpan(2 + nameBytes.Length));
        return body;
    }

    public static (string Name, byte[] Content) Decode(ReadOnlySpan<byte> body)
    {
        var length = BinaryPrimitives.ReadUInt16BigEndian(body);
        return (System.Text.Encoding.UTF8.GetString(body.Slice(2, length)), body[(2 + length)..].ToArray());
    }

    /// <summary><c>CHNK</c>: a chunk id and its stored record.</summary>
    public static byte[] EncodeChunk(ChunkId id, ReadOnlySpan<byte> stored)
    {
        var body = new byte[32 + stored.Length];
        id.WriteTo(body);
        stored.CopyTo(body.AsSpan(32));
        return body;
    }

    public static (ChunkId Id, ReadOnlyMemory<byte> Stored) DecodeChunk(byte[] body) =>
        body.Length > 32 ? (ChunkId.FromBytes(body.AsSpan(0, 32)), body.AsMemory(32)) : throw new InvalidDataException("A chunk record is shorter than its id.");
}

/// <summary>The catalog (<c>CATL</c>, zstd-compressed JSON) of a media set, written after every run and before every tape change.</summary>
public sealed record TapeCatalog
{
    public int Version { get; init; } = 1;

    public required Guid SetId { get; init; }

    public required Guid PoolId { get; init; }

    public required string PoolName { get; init; }

    public required DateTimeOffset SetCreatedUtc { get; init; }

    public required DateTimeOffset WrittenUtc { get; init; }

    public required IReadOnlyList<TapeCatalogMedium> Media { get; init; }

    public required IReadOnlyList<TapeCatalogFile> Files { get; init; }

    public required IReadOnlyList<TapeCatalogCopy> Copies { get; init; }
}

public sealed record TapeCatalogMedium(int Sequence, Guid MediumId, string Name, string? Barcode);

/// <param name="StartBlock">The tape's logical block address of the file's first block (for LOCATE).</param>
public sealed record TapeCatalogFile(int Sequence, int FileNumber, long StartBlock, ulong FileId, string Type, long Blocks, long Bytes, Guid? CopyId);

/// <param name="Kind"><c>restore-point</c> or <c>sql</c>.</param>
/// <param name="Manifest">The original manifest (JSON).</param>
/// <param name="NeededSequences">The tapes its chunks are on.</param>
public sealed record TapeCatalogCopy(
    Guid CopyId,
    string Kind,
    Guid OriginalId,
    Guid RepositoryId,
    string VmId,
    string VmName,
    string? JobName,
    DateTimeOffset CreatedUtc,
    int Sequence,
    int FileNumber,
    IReadOnlyList<int> NeededSequences,
    long StoredBytes,
    string Manifest);

public static class TapeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException($"An empty {typeof(T).Name}.");

    public static byte[] SerializeCompressed<T>(T value) => TapeCompression.Compress(Serialize(value));

    public static T DeserializeCompressed<T>(ReadOnlySpan<byte> compressed) => Deserialize<T>(TapeCompression.Decompress(compressed));
}

public static class TapeCompression
{
    public static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        using var compressor = new ZstdSharp.Compressor(3);
        return compressor.Wrap(raw).ToArray();
    }

    public static byte[] Decompress(ReadOnlySpan<byte> compressed)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(compressed).ToArray();
    }
}
