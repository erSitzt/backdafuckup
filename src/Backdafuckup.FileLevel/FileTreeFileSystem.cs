using Backdafuckup.Core.Repository;

namespace Backdafuckup.FileLevel;

/// <summary>
/// A volume backed up file by file (a file tree, spec 2026-11-12-1952-kubernetes-file-level-volumes) as a guest file system:
/// browsing, downloads and restores read it like a Unix file system of a disk image. Listings are read from the repository
/// as they are needed and the most recent ones kept. Not thread-safe (one instance per thread, like the others).
/// </summary>
public sealed class FileTreeFileSystem(ChunkStreamRef root, FileTreeRootMeta rootMeta, IChunkReader chunks, int chunkSize) : IGuestFileSystem
{
    /// <summary>The <see cref="GuestVolume.FileSystem"/> of a file tree.</summary>
    public const string TypeName = "files";

    private const int ListingsKept = 64;

    private readonly Dictionary<ChunkId, List<FileTreeEntry>> _listings = [];
    private readonly LinkedList<ChunkId> _recent = new();

    public GuestFileSystemFamily Family => GuestFileSystemFamily.Unix;

    public string Type => TypeName;

    public GuestEntry Stat(GuestPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            return new GuestEntry
            {
                Name = string.Empty, Kind = GuestEntryKind.Directory, Mode = rootMeta.Mode, UserId = rootMeta.Uid, GroupId = rootMeta.Gid, ModifiedUtc = Time(rootMeta.MtimeNs),
            };
        }

        return ToGuest(Find(path));
    }

    public IReadOnlyList<GuestEntry> List(GuestPath directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var listing = directory.IsRoot
            ? root
            : Find(directory) is { Type: FileTreeEntryType.Directory, Listing: { } child } ? child : throw new IOException($"{directory} is not a folder.");
        return [.. Listing(listing).Select(ToGuest)];
    }

    public Stream OpenRead(GuestPath path)
    {
        var entry = Find(path);
        if (entry.Type != FileTreeEntryType.File)
        {
            throw new IOException($"{path} is not a file.");
        }

        if (entry.Flags.HasFlag(FileTreeEntryFlags.Unreadable) || entry.Content is null)
        {
            throw new GuestFileUnsupportedException(path.ToString(), $"it was not read at the backup: {entry.Reason}");
        }

        return new ChunkStreamReader(entry.Content, chunks, chunkSize);
    }

    public byte[]? GetSecurityDescriptor(GuestPath path) => null;

    public void Dispose()
    {
    }

    private FileTreeEntry Find(GuestPath path)
    {
        var listing = root;
        FileTreeEntry? entry = null;
        foreach (var (component, index) in path.Components.Select((c, i) => (c, i)))
        {
            entry = Listing(listing).FirstOrDefault(e => string.Equals(e.DisplayName, component, StringComparison.Ordinal))
                    ?? throw new FileNotFoundException($"{path} is not in the backup.");
            if (index < path.Components.Count - 1)
            {
                listing = entry is { Type: FileTreeEntryType.Directory, Listing: { } child } ? child : throw new IOException($"{path} goes through {entry.DisplayName}, which is no folder.");
            }
        }

        return entry ?? throw new FileNotFoundException($"{path} is not in the backup.");
    }

    private List<FileTreeEntry> Listing(ChunkStreamRef listing)
    {
        var key = listing.Key();
        if (_listings.TryGetValue(key, out var entries))
        {
            _recent.Remove(key);
            _recent.AddFirst(key);
            return entries;
        }

        entries = FileTreeListing.Deserialize(ChunkStreams.ReadAll(listing, chunks, chunkSize));
        _listings[key] = entries;
        _recent.AddFirst(key);
        if (_recent.Count > ListingsKept)
        {
            _listings.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }

        return entries;
    }

    private static GuestEntry ToGuest(FileTreeEntry e) => new()
    {
        Name = e.DisplayName,
        Kind = e.Type switch
        {
            FileTreeEntryType.Directory => GuestEntryKind.Directory,
            FileTreeEntryType.File => GuestEntryKind.File,
            FileTreeEntryType.Symlink => GuestEntryKind.Symlink,
            _ => GuestEntryKind.Special,
        },
        Size = e.Type == FileTreeEntryType.Directory ? 0 : e.Size,
        ModifiedUtc = Time(e.MtimeNs),
        CreatedUtc = Time(e.CtimeNs),
        Mode = e.Mode,
        UserId = e.Uid,
        GroupId = e.Gid,
        LinkTarget = e.LinkTarget is null ? null : System.Text.Encoding.UTF8.GetString(e.LinkTarget),
        FileId = (long)e.Inode,
        LinkCount = (int)Math.Clamp(e.Links, 1, int.MaxValue),
        Unsupported = e.Flags.HasFlag(FileTreeEntryFlags.Unreadable)
            ? $"not read at the backup: {e.Reason}"
            : !e.Name.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(e.DisplayName))
                ? "its name is not UTF-8 (only shown)"
                : null,
    };

    private static DateTime? Time(long ns) => ns == 0 ? null : DateTime.UnixEpoch.AddTicks(ns / 100);

    /// <summary>A file's content from its chunk stream, read piece by piece as it is needed (seekable).</summary>
    private sealed class ChunkStreamReader(ChunkStreamRef stream, IChunkReader chunks, int chunkSize) : Stream
    {
        private readonly ChunkId[] _ids = ChunkStreams.Ids(stream, chunks, chunkSize);
        private readonly byte[] _piece = new byte[stream.Inline is null ? chunkSize : 0];
        private int _loaded = -1;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => stream.Length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length || buffer.Length == 0)
            {
                return 0;
            }

            if (stream.Inline is { } inline)
            {
                var n = (int)Math.Min(buffer.Length, inline.Length - _position);
                inline.AsSpan((int)_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            var index = (int)(_position / chunkSize);
            var within = (int)(_position % chunkSize);
            var pieceLength = (int)Math.Min(chunkSize, Length - ((long)index * chunkSize));
            var take = Math.Min(buffer.Length, pieceLength - within);
            if (_ids[index].IsZero)
            {
                buffer[..take].Clear();
            }
            else
            {
                if (_loaded != index)
                {
                    chunks.Read(_ids[index], _piece.AsSpan(0, pieceLength));
                    _loaded = index;
                }

                _piece.AsSpan(within, take).CopyTo(buffer);
            }

            _position += take;
            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
