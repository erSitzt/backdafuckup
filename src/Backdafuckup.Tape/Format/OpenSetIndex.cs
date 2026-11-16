using System.Buffers.Binary;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Tape.Format;

/// <summary>
/// Where the chunks of an open media set are, for deduplication while writing (<c>BDCI</c>): sorted segment files in
/// the set's own directory, one per committed group, merged into one when there are more than
/// <see cref="MaxSegments"/>. Only segments the caller counts as committed are used; others (a crash between writing a
/// segment and committing the group) are deleted. Lookups read one bucket of each segment from disk.
/// </summary>
public sealed class OpenSetIndex
{
    public const int MaxSegments = 8;
    private const int EntrySize = 50;
    private const int HeaderSize = 16;
    private const int BucketBits = 12;
    private static ReadOnlySpan<byte> Magic => "BDCI"u8;

    private readonly string _directory;
    private readonly List<Segment> _segments = [];
    private readonly Dictionary<ChunkId, ChunkLocation> _pending = [];
    private int _next;

    /// <param name="committed">Segment names in the order they were committed, as recorded with the set.</param>
    public OpenSetIndex(string directory, IEnumerable<string> committed)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        var keep = committed.ToList();
        foreach (var file in Directory.EnumerateFiles(directory, "*.bdci").Concat(Directory.EnumerateFiles(directory, "*.tmp")))
        {
            if (!keep.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }

        foreach (var name in keep)
        {
            _segments.Add(Segment.Open(Path.Combine(directory, name)));
            _next = Math.Max(_next, int.Parse(name.AsSpan(4, 6), System.Globalization.CultureInfo.InvariantCulture) + 1);
        }
    }

    /// <summary>The committed segments, oldest first (what the caller records with the set).</summary>
    public IReadOnlyList<string> Segments => _segments.Select(s => Path.GetFileName(s.Path)).ToList();

    public int PendingCount => _pending.Count;

    /// <summary>The chunk's location in this set: written in this group, or in a committed one.</summary>
    public bool TryFind(ChunkId id, out ChunkLocation location)
    {
        if (_pending.TryGetValue(id, out location))
        {
            return true;
        }

        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            if (_segments[i].TryFind(id, out location))
            {
                return true;
            }
        }

        location = default;
        return false;
    }

    /// <summary>Records a chunk written in the current group.</summary>
    public void Add(ChunkLocation location) => _pending[location.Id] = location;

    /// <summary>Forgets the current group (it was torn, or its tape changed before it was committed).</summary>
    public void DiscardPending() => _pending.Clear();

    /// <summary>
    /// Writes the current group's chunks as a new segment and returns the committed segment list to record with the
    /// set. The segment is used from now on; it was durably written before this returns.
    /// </summary>
    public IReadOnlyList<string> CommitPending()
    {
        if (_pending.Count > 0)
        {
            var name = $"seg-{_next++:D6}.bdci";
            Segment.Write(Path.Combine(_directory, name), _pending.Values.OrderBy(l => l.Id));
            _segments.Add(Segment.Open(Path.Combine(_directory, name)));
            _pending.Clear();
        }

        if (_segments.Count > MaxSegments)
        {
            var name = $"seg-{_next++:D6}.bdci";
            var merged = _segments.SelectMany(s => s.ReadAll()).GroupBy(l => l.Id).Select(g => g.Last()).OrderBy(l => l.Id);
            Segment.Write(Path.Combine(_directory, name), merged);
            var old = _segments.ToList();
            _segments.Clear();
            _segments.Add(Segment.Open(Path.Combine(_directory, name)));
            foreach (var segment in old)
            {
                File.Delete(segment.Path);
            }
        }

        return Segments;
    }

    /// <summary>Removes the set's index (the set closed).</summary>
    public static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Bucket(ChunkId id) => (int)(id.Words.A >> (64 - BucketBits));

    private static void WriteEntry(Span<byte> span, ChunkLocation l)
    {
        l.Id.WriteTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[32..], checked((ushort)l.Sequence));
        BinaryPrimitives.WriteInt32BigEndian(span[34..], l.FileNumber);
        BinaryPrimitives.WriteUInt32BigEndian(span[38..], l.Block);
        BinaryPrimitives.WriteInt32BigEndian(span[42..], l.Offset);
        BinaryPrimitives.WriteInt32BigEndian(span[46..], l.Length);
    }

    private static ChunkLocation ReadEntry(ReadOnlySpan<byte> span) => new(
        ChunkId.FromBytes(span[..32]),
        BinaryPrimitives.ReadUInt16BigEndian(span[32..]),
        BinaryPrimitives.ReadInt32BigEndian(span[34..]),
        BinaryPrimitives.ReadUInt32BigEndian(span[38..]),
        BinaryPrimitives.ReadInt32BigEndian(span[42..]),
        BinaryPrimitives.ReadInt32BigEndian(span[46..]));

    /// <summary>One sorted segment: header (magic, version, count), entries; the bucket starts are kept in memory.</summary>
    private sealed class Segment
    {
        private int[] _starts = [];
        private long _count;

        public required string Path { get; init; }

        public static void Write(string path, IEnumerable<ChunkLocation> sorted)
        {
            var temp = path + ".tmp";
            long count = 0;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[HeaderSize]);
                var entry = new byte[EntrySize];
                foreach (var location in sorted)
                {
                    WriteEntry(entry, location);
                    stream.Write(entry);
                    count++;
                }

                var header = new byte[HeaderSize];
                Magic.CopyTo(header);
                header[4] = 1;
                BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(8), count);
                stream.Position = 0;
                stream.Write(header);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }

        public static Segment Open(string path)
        {
            using var handle = File.OpenHandle(path);
            var header = new byte[HeaderSize];
            if (RandomAccess.Read(handle, header, 0) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] != 1)
            {
                throw new InvalidDataException($"{path} is not a chunk index segment of an open tape set.");
            }

            var count = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(8));
            if (RandomAccess.GetLength(handle) != HeaderSize + (count * EntrySize))
            {
                throw new InvalidDataException($"{path} is cut off.");
            }

            // Bucket starts: the first entry of each bucket (from the ids' top 12 bits), read with one pass.
            var starts = new int[(1 << BucketBits) + 1];
            Array.Fill(starts, -1);
            var buffer = new byte[EntrySize * 4096];
            for (long done = 0; done < count;)
            {
                var n = (int)Math.Min(4096, count - done);
                RandomAccess.Read(handle, buffer.AsSpan(0, n * EntrySize), HeaderSize + (done * EntrySize));
                for (var i = 0; i < n; i++)
                {
                    var bucket = Bucket(ChunkId.FromBytes(buffer.AsSpan(i * EntrySize, 32)));
                    if (starts[bucket] < 0)
                    {
                        starts[bucket] = checked((int)(done + i));
                    }
                }

                done += n;
            }

            starts[^1] = checked((int)count);
            for (var b = starts.Length - 2; b >= 0; b--)
            {
                if (starts[b] < 0)
                {
                    starts[b] = starts[b + 1];
                }
            }

            return new Segment { Path = path, _starts = starts, _count = count };
        }

        public bool TryFind(ChunkId id, out ChunkLocation location)
        {
            var bucket = Bucket(id);
            var from = _starts[bucket];
            var to = _starts[bucket + 1];
            location = default;
            if (to <= from)
            {
                return false;
            }

            var buffer = new byte[(to - from) * EntrySize];
            using (var handle = File.OpenHandle(Path))
            {
                RandomAccess.Read(handle, buffer, HeaderSize + ((long)from * EntrySize));
            }

            int lo = 0, hi = to - from - 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                var candidate = ChunkId.FromBytes(buffer.AsSpan(mid * EntrySize, 32));
                var cmp = candidate.CompareTo(id);
                if (cmp == 0)
                {
                    location = ReadEntry(buffer.AsSpan(mid * EntrySize, EntrySize));
                    return true;
                }

                if (cmp < 0)
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

        public IEnumerable<ChunkLocation> ReadAll()
        {
            using var handle = File.OpenHandle(Path);
            var buffer = new byte[EntrySize * 4096];
            for (long done = 0; done < _count;)
            {
                var n = (int)Math.Min(4096, _count - done);
                RandomAccess.Read(handle, buffer.AsSpan(0, n * EntrySize), HeaderSize + (done * EntrySize));
                for (var i = 0; i < n; i++)
                {
                    yield return ReadEntry(buffer.AsSpan(i * EntrySize, EntrySize));
                }

                done += n;
            }
        }
    }
}
