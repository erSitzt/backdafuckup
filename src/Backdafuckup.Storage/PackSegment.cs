using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Backdafuckup.Core.Repository;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Storage;

/// <summary>
/// Index segment format v1 (<c>index/B-&lt;seq&gt;.bdpi</c> base, <c>index/D-&lt;seq&gt;.bdpi</c> deltas): chunk id →
/// pack and offset, sorted by id. The lookup tables follow the entries, so a segment is written in one pass.
/// <code>
/// entries     per chunk 48 bytes: id (32), pack ordinal (4), offset (4), stored length (4), raw length (4)
/// pack table  per pack 16 bytes: the pack's id
/// fan-out     2^bits × 4 bytes: end of each bucket (entries whose id starts with the bucket's bits)
/// bloom       words × 8 bytes (base segments only)
/// footer      96 bytes: "BDPI", version 1, kind (0 delta, 1 base), fan-out bits, bloom hashes, sequence (8),
///             entry count (8), pack count (4), bloom words (4), SHA-256 of the entries (32),
///             SHA-256 of pack table + fan-out + bloom + the footer's first 64 bytes (32)
/// </code>
/// </summary>
internal static class PackSegmentFormat
{
    public const int EntrySize = 48;
    public const int PackIdSize = 16;
    public const int FooterSize = 96;
    public const byte Version = 1;
    public const byte KindDelta = 0;
    public const byte KindBase = 1;
    public const int BloomHashes = 7;
    public const int BloomBitsPerEntry = 10;
    public const string Extension = ".bdpi";

    private static readonly uint Magic = BinaryPrimitives.ReadUInt32BigEndian("BDPI"u8);

    public static int FanoutBits(long expectedEntries)
    {
        if (expectedEntries <= 32)
        {
            return 0;
        }

        var bits = 64 - BitOperations.LeadingZeroCount((ulong)((expectedEntries - 1) / 32));
        return Math.Min(bits, 24);
    }

    public static int Bucket(ChunkId id, int bits) => bits == 0 ? 0 : (int)(id.Words.A >> (64 - bits));

    public static void WriteEntry(Span<byte> destination, in SegmentEntry entry)
    {
        entry.Id.WriteTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[32..], (uint)entry.Pack);
        BinaryPrimitives.WriteUInt32BigEndian(destination[36..], entry.Offset);
        BinaryPrimitives.WriteUInt32BigEndian(destination[40..], entry.StoredLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[44..], entry.RawLength);
    }

    public static SegmentEntry ReadEntry(ReadOnlySpan<byte> source) => new(
        ChunkId.FromBytes(source[..32]),
        (int)BinaryPrimitives.ReadUInt32BigEndian(source[32..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[36..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[40..]),
        BinaryPrimitives.ReadUInt32BigEndian(source[44..]));

    public static SegmentFooter ReadFooter(ReadOnlySpan<byte> footer, string path)
    {
        if (footer.Length < FooterSize || BinaryPrimitives.ReadUInt32BigEndian(footer) != Magic)
        {
            throw new InvalidDataException($"Index segment '{path}' has no valid footer.");
        }

        if (footer[4] != Version)
        {
            throw new InvalidDataException($"Index segment '{path}' has version {footer[4]}, which this version does not know.");
        }

        var result = new SegmentFooter(
            footer[5],
            footer[6],
            footer[7],
            BinaryPrimitives.ReadInt64BigEndian(footer[8..]),
            BinaryPrimitives.ReadInt64BigEndian(footer[16..]),
            (int)BinaryPrimitives.ReadUInt32BigEndian(footer[24..]),
            (int)BinaryPrimitives.ReadUInt32BigEndian(footer[28..]),
            footer.Slice(32, 32).ToArray(),
            footer.Slice(64, 32).ToArray());
        if (result.FanoutBits > 24 || result.EntryCount < 0 || result.PackCount < 0 || result.BloomWords < 0)
        {
            throw new InvalidDataException($"Index segment '{path}' has an invalid footer.");
        }

        return result;
    }

    public static void WriteFooterStart(Span<byte> footer, byte kind, int fanoutBits, int bloomHashes, long seq, long entryCount, int packCount, int bloomWords, ReadOnlySpan<byte> entriesHash)
    {
        footer[..FooterSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(footer, Magic);
        footer[4] = Version;
        footer[5] = kind;
        footer[6] = (byte)fanoutBits;
        footer[7] = (byte)bloomHashes;
        BinaryPrimitives.WriteInt64BigEndian(footer[8..], seq);
        BinaryPrimitives.WriteInt64BigEndian(footer[16..], entryCount);
        BinaryPrimitives.WriteUInt32BigEndian(footer[24..], (uint)packCount);
        BinaryPrimitives.WriteUInt32BigEndian(footer[28..], (uint)bloomWords);
        entriesHash.CopyTo(footer.Slice(32, 32));
    }
}

internal readonly record struct SegmentFooter(byte Kind, int FanoutBits, int BloomHashes, long Seq, long EntryCount, int PackCount, int BloomWords, byte[] EntriesHash, byte[] TailHash);

/// <summary>One index entry; <see cref="Pack"/> is an ordinal into its segment's pack table.</summary>
internal readonly record struct SegmentEntry(ChunkId Id, int Pack, uint Offset, uint StoredLength, uint RawLength);

/// <summary>A bloom filter over chunk ids (which are SHA-256 values, so their words are already uniform hashes).</summary>
internal sealed class ChunkBloomFilter(ulong[] words, int hashes)
{
    public ulong[] Words { get; } = words;

    public int Hashes { get; } = hashes;

    public static ChunkBloomFilter Create(long expectedEntries)
    {
        var bits = Math.Max(64, expectedEntries * PackSegmentFormat.BloomBitsPerEntry);
        return new ChunkBloomFilter(new ulong[(bits + 63) / 64], PackSegmentFormat.BloomHashes);
    }

    public void Add(ChunkId id)
    {
        var (_, h1, h2, _) = id.Words;
        h2 |= 1;
        var m = (ulong)Words.Length * 64;
        for (var i = 0; i < Hashes; i++)
        {
            var bit = (h1 + ((ulong)i * h2)) % m;
            Words[bit / 64] |= 1UL << (int)(bit % 64);
        }
    }

    public bool MayContain(ChunkId id)
    {
        var (_, h1, h2, _) = id.Words;
        h2 |= 1;
        var m = (ulong)Words.Length * 64;
        for (var i = 0; i < Hashes; i++)
        {
            var bit = (h1 + ((ulong)i * h2)) % m;
            if ((Words[bit / 64] & (1UL << (int)(bit % 64))) == 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Writes a segment file in one pass from entries in id order.</summary>
internal sealed class PackSegmentWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly IncrementalHash _entriesHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Dictionary<PackFile, int> _packOrdinals = [];
    private readonly List<PackFile> _packs = [];
    private readonly uint[] _bucketCounts;
    private readonly ChunkBloomFilter? _bloom;
    private readonly byte[] _entry = new byte[PackSegmentFormat.EntrySize];
    private readonly byte _kind;
    private ChunkId _last;
    private long _count;
    private bool _finished;

    public PackSegmentWriter(string tempPath, byte kind, long expectedEntries)
    {
        TempPath = tempPath;
        _kind = kind;
        FanoutBits = PackSegmentFormat.FanoutBits(expectedEntries);
        _bucketCounts = new uint[1 << FanoutBits];
        _bloom = kind == PackSegmentFormat.KindBase ? ChunkBloomFilter.Create(expectedEntries) : null;
        _stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
    }

    public string TempPath { get; }

    public int FanoutBits { get; }

    public long Count => _count;

    public IReadOnlyList<PackFile> Packs => _packs;

    public void Add(ChunkId id, PackFile pack, uint offset, uint stored, uint raw)
    {
        if (_count > 0 && id.CompareTo(_last) <= 0)
        {
            throw new InvalidOperationException($"Index entries must be written in increasing id order ({id} after {_last}).");
        }

        if (!_packOrdinals.TryGetValue(pack, out var ordinal))
        {
            ordinal = _packs.Count;
            _packOrdinals[pack] = ordinal;
            _packs.Add(pack);
        }

        PackSegmentFormat.WriteEntry(_entry, new SegmentEntry(id, ordinal, offset, stored, raw));
        _stream.Write(_entry);
        _entriesHash.AppendData(_entry);
        _bucketCounts[PackSegmentFormat.Bucket(id, FanoutBits)]++;
        _bloom?.Add(id);
        _last = id;
        _count++;
    }

    /// <summary>Writes the tables and footer, flushes to disk and closes the file.</summary>
    public (long Seq, IReadOnlyList<PackFile> Packs) Finish(long seq)
    {
        _finished = true;
        using var tail = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var packBytes = new byte[_packs.Count * PackSegmentFormat.PackIdSize];
        for (var i = 0; i < _packs.Count; i++)
        {
            _packs[i].Id.TryWriteBytes(packBytes.AsSpan(i * PackSegmentFormat.PackIdSize, PackSegmentFormat.PackIdSize), bigEndian: true, out _);
        }

        _stream.Write(packBytes);
        tail.AppendData(packBytes);

        var fanout = new byte[_bucketCounts.Length * 4];
        uint end = 0;
        for (var b = 0; b < _bucketCounts.Length; b++)
        {
            end += _bucketCounts[b];
            BinaryPrimitives.WriteUInt32BigEndian(fanout.AsSpan(b * 4), end);
        }

        _stream.Write(fanout);
        tail.AppendData(fanout);

        var bloomWords = _bloom?.Words.Length ?? 0;
        if (_bloom is not null)
        {
            var bloomBytes = new byte[bloomWords * 8];
            for (var i = 0; i < bloomWords; i++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(bloomBytes.AsSpan(i * 8), _bloom.Words[i]);
            }

            _stream.Write(bloomBytes);
            tail.AppendData(bloomBytes);
        }

        var footer = new byte[PackSegmentFormat.FooterSize];
        PackSegmentFormat.WriteFooterStart(footer, _kind, FanoutBits, _bloom?.Hashes ?? 0, seq, _count, _packs.Count, bloomWords, _entriesHash.GetHashAndReset());
        tail.AppendData(footer.AsSpan(0, 64));
        tail.GetHashAndReset().CopyTo(footer.AsSpan(64, 32));
        _stream.Write(footer);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        return (seq, _packs);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _entriesHash.Dispose();
        if (!_finished)
        {
            try
            {
                File.Delete(TempPath);
            }
            catch (IOException)
            {
                // Left for the temp purge.
            }
        }
    }
}

/// <summary>A small segment held entirely in memory (deltas).</summary>
internal sealed class MemorySegment(long seq, string path, SegmentEntry[] entries, PackFile?[] packs, Guid[]? packIds = null)
{
    /// <summary>The ids of the pack table (also of packs that are gone, whose <see cref="Packs"/> entry is null).</summary>
    public Guid[] PackIds { get; } = packIds ?? [.. packs.Select(p => p!.Id)];

    public long Seq { get; } = seq;

    public string Path { get; } = path;

    public SegmentEntry[] Entries { get; } = entries;

    public PackFile?[] Packs { get; } = packs;

    public bool TryFind(ChunkId id, out SegmentEntry entry)
    {
        int lo = 0, hi = Entries.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var c = Entries[mid].Id.CompareTo(id);
            if (c == 0)
            {
                entry = Entries[mid];
                return true;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        entry = default;
        return false;
    }

    /// <summary>Reads and checks a whole segment file; pack ids unknown to <paramref name="resolve"/> become null.</summary>
    public static MemorySegment Load(string path, Func<Guid, PackFile?> resolve)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < PackSegmentFormat.FooterSize)
        {
            throw new InvalidDataException($"Index segment '{path}' is too short.");
        }

        var footer = PackSegmentFormat.ReadFooter(bytes.AsSpan(bytes.Length - PackSegmentFormat.FooterSize), path);
        var entriesLength = footer.EntryCount * PackSegmentFormat.EntrySize;
        var tableLength = ((long)footer.PackCount * PackSegmentFormat.PackIdSize) + ((1L << footer.FanoutBits) * 4) + ((long)footer.BloomWords * 8);
        if (entriesLength + tableLength + PackSegmentFormat.FooterSize != bytes.LongLength)
        {
            throw new InvalidDataException($"Index segment '{path}' has {bytes.LongLength} bytes, its footer describes {entriesLength + tableLength + PackSegmentFormat.FooterSize}.");
        }

        if (!SHA256.HashData(bytes.AsSpan(0, (int)entriesLength)).AsSpan().SequenceEqual(footer.EntriesHash))
        {
            throw new InvalidDataException($"Index segment '{path}' has damaged entries (SHA-256 mismatch).");
        }

        CheckTail(bytes.AsSpan((int)entriesLength, (int)tableLength), bytes.AsSpan(bytes.Length - PackSegmentFormat.FooterSize, 64), footer, path);
        var packIds = ReadPackIds(bytes.AsSpan((int)entriesLength, footer.PackCount * PackSegmentFormat.PackIdSize));
        var packs = packIds.Select(resolve).ToArray();
        var entries = new SegmentEntry[footer.EntryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = PackSegmentFormat.ReadEntry(bytes.AsSpan(i * PackSegmentFormat.EntrySize, PackSegmentFormat.EntrySize));
            if ((uint)entries[i].Pack >= (uint)packs.Length)
            {
                throw new InvalidDataException($"Index segment '{path}' has an entry for pack {entries[i].Pack} of {packs.Length}.");
            }
        }

        return new MemorySegment(footer.Seq, path, entries, packs, packIds);
    }

    internal static void CheckTail(ReadOnlySpan<byte> table, ReadOnlySpan<byte> footerStart, SegmentFooter footer, string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(table);
        hash.AppendData(footerStart);
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(footer.TailHash))
        {
            throw new InvalidDataException($"Index segment '{path}' has damaged tables (SHA-256 mismatch).");
        }
    }

    internal static Guid[] ReadPackIds(ReadOnlySpan<byte> table)
    {
        var ids = new Guid[table.Length / PackSegmentFormat.PackIdSize];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = new Guid(table.Slice(i * PackSegmentFormat.PackIdSize, PackSegmentFormat.PackIdSize), bigEndian: true);
        }

        return ids;
    }
}

/// <summary>
/// The base segment: only its tables stay in memory (fan-out, bloom filter, pack table); a lookup reads one bucket of
/// entries from the file.
/// </summary>
internal sealed class DiskSegment : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly uint[] _fanout;
    private readonly ChunkBloomFilter? _bloom;

    private DiskSegment(string path, SafeFileHandle handle, SegmentFooter footer, uint[] fanout, ChunkBloomFilter? bloom, PackFile?[] packs, Guid[] packIds)
    {
        Path = path;
        _handle = handle;
        Seq = footer.Seq;
        EntryCount = footer.EntryCount;
        FanoutBits = footer.FanoutBits;
        EntriesHash = footer.EntriesHash;
        _fanout = fanout;
        _bloom = bloom;
        Packs = packs;
        PackIds = packIds;
    }

    public string Path { get; }

    public long Seq { get; }

    public long EntryCount { get; }

    public int FanoutBits { get; }

    public byte[] EntriesHash { get; }

    public PackFile?[] Packs { get; }

    public Guid[] PackIds { get; }

    /// <summary>Bytes this segment keeps in memory.</summary>
    public long MemoryBytes => (_fanout.LongLength * 4) + ((_bloom?.Words.LongLength ?? 0) * 8) + (Packs.LongLength * 24);

    public static DiskSegment Open(string path, Func<Guid, PackFile?> resolve)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length < PackSegmentFormat.FooterSize)
            {
                throw new InvalidDataException($"Index segment '{path}' is too short.");
            }

            var footerBytes = new byte[PackSegmentFormat.FooterSize];
            PackFormat.ReadExactly(handle, footerBytes, length - PackSegmentFormat.FooterSize, path);
            var footer = PackSegmentFormat.ReadFooter(footerBytes, path);
            var entriesLength = footer.EntryCount * PackSegmentFormat.EntrySize;
            var tableLength = ((long)footer.PackCount * PackSegmentFormat.PackIdSize) + ((1L << footer.FanoutBits) * 4) + ((long)footer.BloomWords * 8);
            if (entriesLength + tableLength + PackSegmentFormat.FooterSize != length)
            {
                throw new InvalidDataException($"Index segment '{path}' has {length} bytes, its footer describes {entriesLength + tableLength + PackSegmentFormat.FooterSize}.");
            }

            var table = new byte[tableLength];
            PackFormat.ReadExactly(handle, table, entriesLength, path);
            MemorySegment.CheckTail(table, footerBytes.AsSpan(0, 64), footer, path);
            var packTableLength = footer.PackCount * PackSegmentFormat.PackIdSize;
            var packIds = new Guid[footer.PackCount];
            for (var i = 0; i < packIds.Length; i++)
            {
                packIds[i] = new Guid(table.AsSpan(i * PackSegmentFormat.PackIdSize, PackSegmentFormat.PackIdSize), bigEndian: true);
            }

            var packs = packIds.Select(resolve).ToArray();
            var fanout = new uint[1 << footer.FanoutBits];
            for (var b = 0; b < fanout.Length; b++)
            {
                fanout[b] = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(packTableLength + (b * 4)));
            }

            if (fanout.Length > 0 && fanout[^1] != footer.EntryCount)
            {
                throw new InvalidDataException($"Index segment '{path}' has a fan-out table that does not add up to its {footer.EntryCount} entries.");
            }

            ChunkBloomFilter? bloom = null;
            if (footer.BloomWords > 0)
            {
                var words = new ulong[footer.BloomWords];
                var bloomStart = packTableLength + (fanout.Length * 4);
                for (var i = 0; i < words.Length; i++)
                {
                    words[i] = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(bloomStart + (i * 8)));
                }

                bloom = new ChunkBloomFilter(words, footer.BloomHashes);
            }

            return new DiskSegment(path, handle, footer, fanout, bloom, packs, packIds);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool TryFind(ChunkId id, out SegmentEntry entry)
    {
        entry = default;
        if (EntryCount == 0 || (_bloom is not null && !_bloom.MayContain(id)))
        {
            return false;
        }

        var bucket = PackSegmentFormat.Bucket(id, FanoutBits);
        var start = bucket == 0 ? 0u : _fanout[bucket - 1];
        var end = _fanout[bucket];
        var count = (int)(end - start);
        if (count <= 0)
        {
            return false;
        }

        var length = count * PackSegmentFormat.EntrySize;
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            PackFormat.ReadExactly(_handle, buffer.AsSpan(0, length), start * (long)PackSegmentFormat.EntrySize, Path);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                var candidate = PackSegmentFormat.ReadEntry(buffer.AsSpan(mid * PackSegmentFormat.EntrySize, PackSegmentFormat.EntrySize));
                var c = candidate.Id.CompareTo(id);
                if (c == 0)
                {
                    if ((uint)candidate.Pack >= (uint)Packs.Length)
                    {
                        throw new InvalidDataException($"Index segment '{Path}' has an entry for pack {candidate.Pack} of {Packs.Length}.");
                    }

                    entry = candidate;
                    return true;
                }

                if (c < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Streams all entries in id order and checks the entries' SHA-256 at the end.</summary>
    public IEnumerable<SegmentEntry> ReadAll()
    {
        const int batch = 4096;
        var buffer = new byte[batch * PackSegmentFormat.EntrySize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long done = 0; done < EntryCount;)
        {
            var n = (int)Math.Min(batch, EntryCount - done);
            PackFormat.ReadExactly(_handle, buffer.AsSpan(0, n * PackSegmentFormat.EntrySize), done * PackSegmentFormat.EntrySize, Path);
            hash.AppendData(buffer, 0, n * PackSegmentFormat.EntrySize);
            for (var i = 0; i < n; i++)
            {
                var entry = PackSegmentFormat.ReadEntry(buffer.AsSpan(i * PackSegmentFormat.EntrySize, PackSegmentFormat.EntrySize));
                if ((uint)entry.Pack >= (uint)Packs.Length)
                {
                    throw new InvalidDataException($"Index segment '{Path}' has an entry for pack {entry.Pack} of {Packs.Length}.");
                }

                yield return entry;
            }

            done += n;
        }

        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(EntriesHash))
        {
            throw new InvalidDataException($"Index segment '{Path}' has damaged entries (SHA-256 mismatch).");
        }
    }

    public void Dispose() => _handle.Dispose();
}
