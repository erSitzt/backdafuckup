using System.Buffers;
using System.Text;

namespace Backdafuckup.Core.Repository;

/// <summary>
/// A volume backed up file by file (spec 2026-11-12-1952-kubernetes-file-level-volumes): a tree of directory listings
/// in the chunk store. Each directory is one listing (<see cref="FileTreeListing"/>), stored as a chunk stream; each file's
/// content is a chunk stream of its own, starting at a chunk boundary, so files and whole unchanged subtrees deduplicate
/// across runs, namespaces and clusters. The root listing's chunks are the restore point's <c>tree-&lt;key&gt;.idx</c>.
/// </summary>
public enum FileTreeEntryType : byte
{
    Directory = 1,
    File = 2,
    Symlink = 3,
    Fifo = 4,
    Socket = 5,
    CharDevice = 6,
    BlockDevice = 7,
}

[Flags]
public enum FileTreeEntryFlags
{
    None = 0,

    /// <summary>The file changed while it was read (its stat before and after differ): the content may be torn.</summary>
    ChangedWhileRead = 1,

    /// <summary>Changed within moments of being read: the next run reads it again, whatever its stat says.</summary>
    Racy = 2,

    /// <summary>Not read (permission denied, vanished, …): <see cref="FileTreeEntry.Reason"/> says why; no content.</summary>
    Unreadable = 4,

    /// <summary>Holes were seen while reading (sparse): the content is stored dense, with zero pieces not stored.</summary>
    SparseSeen = 8,
}

/// <summary>
/// Bytes in the repository: up to <see cref="ChunkStreams.InlineLimit"/> inline, else as pieces of the chunk size with their
/// chunk ids (an all-zero piece is <see cref="ChunkId.Zero"/>, not stored), and beyond <see cref="MaxDirect"/> pieces the
/// id list stored as a stream itself.
/// </summary>
public sealed class ChunkStreamRef
{
    public const int MaxDirect = 64;

    private ChunkStreamRef(long length, byte[]? inline, ChunkId[]? ids, ChunkStreamRef? idList)
    {
        Length = length;
        Inline = inline;
        Ids = ids;
        IdList = idList;
    }

    public static ChunkStreamRef Empty { get; } = new(0, [], null, null);

    public long Length { get; }

    public byte[]? Inline { get; }

    /// <summary>The pieces' chunk ids (direct).</summary>
    public ChunkId[]? Ids { get; }

    /// <summary>The pieces' chunk ids, stored as a stream of 32-byte ids (indirect).</summary>
    public ChunkStreamRef? IdList { get; }

    public static ChunkStreamRef OfInline(byte[] data) => data.Length == 0 ? Empty : new(data.Length, data, null, null);

    public static ChunkStreamRef OfIds(long length, ChunkId[] ids) => new(length, null, ids, null);

    public static ChunkStreamRef OfIdList(long length, ChunkStreamRef idList) => new(length, null, null, idList);

    internal void Write(TreeBufferWriter w)
    {
        if (Inline is not null)
        {
            w.Byte(0);
            w.Bytes(Inline);
        }
        else if (Ids is not null)
        {
            w.Byte(1);
            w.UInt((ulong)Length);
            w.UInt((ulong)Ids.Length);
            Span<byte> id = stackalloc byte[ChunkId.Size];
            foreach (var chunk in Ids)
            {
                chunk.WriteTo(id);
                w.Raw(id);
            }
        }
        else
        {
            w.Byte(2);
            w.UInt((ulong)Length);
            IdList!.Write(w);
        }
    }

    internal static ChunkStreamRef Read(ref TreeSpanReader r)
    {
        switch (r.Byte())
        {
            case 0:
                return OfInline(r.Bytes());
            case 1:
            {
                var length = (long)r.UInt();
                var ids = new ChunkId[checked((int)r.UInt())];
                for (var i = 0; i < ids.Length; i++)
                {
                    ids[i] = ChunkId.FromBytes(r.Raw(ChunkId.Size));
                }

                return OfIds(length, ids);
            }

            case 2:
            {
                var length = (long)r.UInt();
                return OfIdList(length, Read(ref r));
            }

            default:
                throw new InvalidDataException("A file tree has a stream reference of an unknown kind.");
        }
    }

    /// <summary>A key for this reference (the hash of its encoding), to tell listings apart in a walk.</summary>
    public ChunkId Key()
    {
        var w = new TreeBufferWriter();
        Write(w);
        return ChunkId.Compute(w.Written);
    }
}

/// <summary>One entry of a directory listing.</summary>
public sealed record FileTreeEntry
{
    /// <summary>The name as the file system has it (bytes, not necessarily UTF-8).</summary>
    public required byte[] Name { get; init; }

    public required FileTreeEntryType Type { get; init; }

    public FileTreeEntryFlags Flags { get; init; }

    /// <summary>The permission bits (<c>st_mode &amp; 07777</c>).</summary>
    public int Mode { get; init; }

    public long Uid { get; init; }

    public long Gid { get; init; }

    /// <summary>Modification time, nanoseconds since 1970 (UTC). Access times are not kept: they would change every listing.</summary>
    public long MtimeNs { get; init; }

    /// <summary>Change time of the inode, nanoseconds since 1970: with size, mtime and inode it tells whether a file changed.</summary>
    public long CtimeNs { get; init; }

    public ulong Inode { get; init; }

    public long Links { get; init; } = 1;

    public long Size { get; init; }

    /// <summary>A file's content; null for other types and for unreadable files.</summary>
    public ChunkStreamRef? Content { get; init; }

    /// <summary>A directory's listing.</summary>
    public ChunkStreamRef? Listing { get; init; }

    /// <summary>A directory's files and their bytes, all levels down (for folder sizes and progress estimates).</summary>
    public long SubtreeFiles { get; init; }

    public long SubtreeBytes { get; init; }

    public byte[]? LinkTarget { get; init; }

    /// <summary>A device's number.</summary>
    public long Rdev { get; init; }

    /// <summary>Why the entry was not read (<see cref="FileTreeEntryFlags.Unreadable"/>).</summary>
    public string? Reason { get; init; }

    /// <summary>The name for display (bytes that are no UTF-8 become U+FFFD).</summary>
    public string DisplayName => Encoding.UTF8.GetString(Name);
}

/// <summary>The listing format <c>BDTL</c> v1: entries sorted by their name bytes (ordinal), integers as varints.</summary>
public static class FileTreeListing
{
    private const byte Version = 1;

    private static ReadOnlySpan<byte> Magic => "BDTL"u8;

    public static byte[] Serialize(IEnumerable<FileTreeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sorted = entries.ToList();
        sorted.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Name.AsSpan().SequenceEqual(sorted[i - 1].Name))
            {
                throw new ArgumentException($"A directory listing has the name {sorted[i].DisplayName} twice.", nameof(entries));
            }
        }

        var w = new TreeBufferWriter();
        w.Raw(Magic);
        w.Byte(Version);
        w.UInt((ulong)sorted.Count);
        foreach (var e in sorted)
        {
            if (e.Name.Length == 0 || e.Name.AsSpan().IndexOfAny((byte)'/', (byte)0) >= 0 || e.Name.AsSpan().SequenceEqual("."u8) || e.Name.AsSpan().SequenceEqual(".."u8))
            {
                throw new ArgumentException($"\"{e.DisplayName}\" is no file name.", nameof(entries));
            }

            w.Bytes(e.Name);
            w.Byte((byte)e.Type);
            w.UInt((ulong)e.Flags);
            w.UInt((ulong)e.Mode);
            w.Int(e.Uid);
            w.Int(e.Gid);
            w.Int(e.MtimeNs);
            w.Int(e.CtimeNs);
            w.UInt(e.Inode);
            w.Int(e.Links);
            w.Int(e.Size);
            if (e.Flags.HasFlag(FileTreeEntryFlags.Unreadable))
            {
                w.Bytes(Encoding.UTF8.GetBytes(e.Reason ?? string.Empty));
                continue;
            }

            switch (e.Type)
            {
                case FileTreeEntryType.File:
                    (e.Content ?? ChunkStreamRef.Empty).Write(w);
                    break;
                case FileTreeEntryType.Directory:
                    (e.Listing ?? throw new ArgumentException($"The directory {e.DisplayName} has no listing.", nameof(entries))).Write(w);
                    w.Int(e.SubtreeFiles);
                    w.Int(e.SubtreeBytes);
                    break;
                case FileTreeEntryType.Symlink:
                    w.Bytes(e.LinkTarget ?? []);
                    break;
                case FileTreeEntryType.CharDevice or FileTreeEntryType.BlockDevice:
                    w.Int(e.Rdev);
                    break;
            }
        }

        return w.Written.ToArray();
    }

    public static List<FileTreeEntry> Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || !data[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("A file tree listing is damaged (no BDTL header).");
        }

        if (data[4] != Version)
        {
            throw new InvalidDataException($"A file tree listing has version {data[4]}; this version reads version {Version}.");
        }

        var r = new TreeSpanReader(data[5..]);
        var count = checked((int)r.UInt());
        var entries = new List<FileTreeEntry>(Math.Min(count, 65536));
        for (var i = 0; i < count; i++)
        {
            var name = r.Bytes();
            var type = (FileTreeEntryType)r.Byte();
            var flags = (FileTreeEntryFlags)r.UInt();
            var mode = (int)r.UInt();
            var uid = r.Int();
            var gid = r.Int();
            var mtime = r.Int();
            var ctime = r.Int();
            var inode = r.UInt();
            var links = r.Int();
            var size = r.Int();
            ChunkStreamRef? content = null, listing = null;
            long files = 0, bytes = 0, rdev = 0;
            byte[]? target = null;
            string? reason = null;
            if (flags.HasFlag(FileTreeEntryFlags.Unreadable))
            {
                reason = Encoding.UTF8.GetString(r.Bytes());
            }
            else
            {
                switch (type)
                {
                    case FileTreeEntryType.File:
                        content = ChunkStreamRef.Read(ref r);
                        break;
                    case FileTreeEntryType.Directory:
                        listing = ChunkStreamRef.Read(ref r);
                        files = r.Int();
                        bytes = r.Int();
                        break;
                    case FileTreeEntryType.Symlink:
                        target = r.Bytes();
                        break;
                    case FileTreeEntryType.CharDevice or FileTreeEntryType.BlockDevice:
                        rdev = r.Int();
                        break;
                    case FileTreeEntryType.Fifo or FileTreeEntryType.Socket:
                        break;
                    default:
                        throw new InvalidDataException($"A file tree listing has an entry of unknown type {(int)type}.");
                }
            }

            entries.Add(new FileTreeEntry
            {
                Name = name, Type = type, Flags = flags, Mode = mode, Uid = uid, Gid = gid, MtimeNs = mtime, CtimeNs = ctime, Inode = inode, Links = links,
                Size = size, Content = content, Listing = listing, SubtreeFiles = files, SubtreeBytes = bytes, LinkTarget = target, Rdev = rdev, Reason = reason,
            });
        }

        if (!r.AtEnd)
        {
            throw new InvalidDataException("A file tree listing has bytes after its last entry.");
        }

        return entries;
    }
}

/// <summary>Writing and reading chunk streams (<see cref="ChunkStreamRef"/>).</summary>
public static class ChunkStreams
{
    /// <summary>Content up to this size is kept inside the listing instead of a chunk of its own.</summary>
    public const int InlineLimit = 512;

    /// <summary>Stores a chunk of the stream; it is not called for all-zero pieces.</summary>
    public delegate ValueTask PutChunk(ChunkId id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="data"/> as a stream: inline when small (and allowed), else in pieces of the chunk size.</summary>
    public static async ValueTask<ChunkStreamRef> WriteAsync(ReadOnlyMemory<byte> data, int chunkSize, PutChunk put, bool allowInline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(put);
        if (allowInline && data.Length <= InlineLimit)
        {
            return ChunkStreamRef.OfInline(data.ToArray());
        }

        var ids = new ChunkId[(data.Length + chunkSize - 1) / chunkSize];
        for (var i = 0; i < ids.Length; i++)
        {
            var piece = data.Slice(i * chunkSize, Math.Min(chunkSize, data.Length - (i * chunkSize)));
            ids[i] = await PutPieceAsync(piece, put, cancellationToken).ConfigureAwait(false);
        }

        return await FromIdsAsync(data.Length, ids, chunkSize, put, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores one piece (unless it is all zeros) and returns its id.</summary>
    public static async ValueTask<ChunkId> PutPieceAsync(ReadOnlyMemory<byte> piece, PutChunk put, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(put);
        if (!piece.Span.ContainsAnyExcept((byte)0))
        {
            return ChunkId.Zero;
        }

        var id = ChunkId.Compute(piece.Span);
        await put(id, piece, cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>The reference of a stream whose pieces are stored: direct, or with its id list stored as a stream when long.</summary>
    public static async ValueTask<ChunkStreamRef> FromIdsAsync(long length, ChunkId[] ids, int chunkSize, PutChunk put, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.LongLength != (length + chunkSize - 1) / chunkSize)
        {
            throw new ArgumentException($"A stream of {length} bytes has {ids.Length} pieces of {chunkSize} bytes.", nameof(ids));
        }

        if (ids.Length <= ChunkStreamRef.MaxDirect)
        {
            return ChunkStreamRef.OfIds(length, ids);
        }

        var list = new byte[ids.Length * ChunkId.Size];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i].WriteTo(list.AsSpan(i * ChunkId.Size, ChunkId.Size));
        }

        return ChunkStreamRef.OfIdList(length, await WriteAsync(list, chunkSize, put, allowInline: false, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The ids of a stream's pieces (reading its id list if it is stored indirectly); empty for inline content.</summary>
    public static ChunkId[] Ids(ChunkStreamRef stream, IChunkReader chunks, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Inline is not null)
        {
            return [];
        }

        if (stream.Ids is not null)
        {
            return stream.Ids;
        }

        var list = ReadAll(stream.IdList!, chunks, chunkSize);
        if (list.Length % ChunkId.Size != 0)
        {
            throw new InvalidDataException("A file tree's id list is damaged (not a whole number of ids).");
        }

        var ids = new ChunkId[list.Length / ChunkId.Size];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = ChunkId.FromBytes(list.AsSpan(i * ChunkId.Size, ChunkId.Size));
        }

        return ids;
    }

    /// <summary>Reads a whole stream (listings and id lists, which are small enough).</summary>
    public static byte[] ReadAll(ChunkStreamRef stream, IChunkReader chunks, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(chunks);
        if (stream.Inline is not null)
        {
            return stream.Inline;
        }

        var data = new byte[checked((int)stream.Length)];
        var ids = Ids(stream, chunks, chunkSize);
        for (var i = 0; i < ids.Length; i++)
        {
            var piece = data.AsSpan(i * chunkSize, (int)Math.Min(chunkSize, stream.Length - ((long)i * chunkSize)));
            if (ids[i].IsZero)
            {
                continue;
            }

            if (chunks.Read(ids[i], piece) != piece.Length)
            {
                throw new InvalidDataException($"Chunk {ids[i]} of a file tree has not the length its stream says.");
            }
        }

        return data;
    }

    /// <summary>Every chunk a stream references: its pieces and, when stored indirectly, the chunks of its id list.</summary>
    public static IEnumerable<ChunkId> Chunks(ChunkStreamRef stream, IChunkReader chunks, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.IdList is { } list)
        {
            foreach (var id in Chunks(list, chunks, chunkSize))
            {
                yield return id;
            }
        }

        foreach (var id in Ids(stream, chunks, chunkSize).Where(i => !i.IsZero))
        {
            yield return id;
        }
    }
}

/// <summary>Walks a file tree for garbage collection and verification.</summary>
public static class FileTreeWalker
{
    /// <summary>
    /// Visits every chunk the tree under <paramref name="root"/> references (listings, contents, id lists). A listing
    /// already in <paramref name="walkedListings"/> (from this tree or another) is not walked again: identical subtrees are
    /// one listing. That set is kept apart from any set of chunk ids on purpose: a file whose bytes equal a listing must
    /// not stop that listing's subtree from being walked.
    /// </summary>
    public static void Visit(ChunkStreamRef root, IChunkReader chunks, int chunkSize, Action<ChunkId> visit, ISet<ChunkId> walkedListings)
    {
        ArgumentNullException.ThrowIfNull(visit);
        ArgumentNullException.ThrowIfNull(walkedListings);
        var pending = new Stack<ChunkStreamRef>();
        pending.Push(root);
        while (pending.TryPop(out var listing))
        {
            if (!walkedListings.Add(listing.Key()))
            {
                continue;
            }

            foreach (var id in ChunkStreams.Chunks(listing, chunks, chunkSize))
            {
                visit(id);
            }

            foreach (var entry in FileTreeListing.Deserialize(ChunkStreams.ReadAll(listing, chunks, chunkSize)))
            {
                if (entry.Content is { } content)
                {
                    foreach (var id in ChunkStreams.Chunks(content, chunks, chunkSize))
                    {
                        visit(id);
                    }
                }
                else if (entry.Listing is { } child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>Every entry under <paramref name="root"/> with its path (parts joined by '/'), depth first, in listing order.</summary>
    public static IEnumerable<(string Path, FileTreeEntry Entry)> Enumerate(ChunkStreamRef root, IChunkReader chunks, int chunkSize, string prefix = "")
    {
        foreach (var entry in FileTreeListing.Deserialize(ChunkStreams.ReadAll(root, chunks, chunkSize)))
        {
            var path = prefix.Length == 0 ? entry.DisplayName : $"{prefix}/{entry.DisplayName}";
            yield return (path, entry);
            if (entry.Listing is { } child)
            {
                foreach (var nested in Enumerate(child, chunks, chunkSize, path))
                {
                    yield return nested;
                }
            }
        }
    }
}

/// <summary>Varint writer for the tree formats.</summary>
internal sealed class TreeBufferWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    public ReadOnlySpan<byte> Written => _buffer.WrittenSpan;

    public void Byte(byte value) => _buffer.Write([value]);

    public void Raw(ReadOnlySpan<byte> value) => _buffer.Write(value);

    public void UInt(ulong value)
    {
        Span<byte> span = stackalloc byte[10];
        var n = 0;
        while (value >= 0x80)
        {
            span[n++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[n++] = (byte)value;
        _buffer.Write(span[..n]);
    }

    /// <summary>Signed, zig-zag encoded (small negative numbers stay short).</summary>
    public void Int(long value) => UInt((ulong)((value << 1) ^ (value >> 63)));

    public void Bytes(ReadOnlySpan<byte> value)
    {
        UInt((ulong)value.Length);
        _buffer.Write(value);
    }
}

/// <summary>Varint reader for the tree formats; every read checks the bounds.</summary>
internal ref struct TreeSpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool AtEnd => _position == _data.Length;

    public byte Byte()
    {
        if (_position >= _data.Length)
        {
            throw new InvalidDataException("A file tree record ends early.");
        }

        return _data[_position++];
    }

    public ReadOnlySpan<byte> Raw(int length)
    {
        if (length < 0 || _position + length > _data.Length)
        {
            throw new InvalidDataException("A file tree record ends early.");
        }

        var span = _data.Slice(_position, length);
        _position += length;
        return span;
    }

    public ulong UInt()
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

        throw new InvalidDataException("A file tree record has a number that is too long.");
    }

    public long Int()
    {
        var raw = UInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    public byte[] Bytes() => Raw(checked((int)UInt())).ToArray();
}
