using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

/// <summary>
/// Builds a file tree in the repository from a mover's <see cref="MoverTree"/> stream (spec
/// 2026-11-12-1952-kubernetes-file-level-volumes): file content is cut into chunks of the repository's size (all-zero pieces
/// not stored), each folder becomes a listing when it is left, and the root listing's chunks are the result. Memory grows
/// with the depth and the width of the folders, not with the volume.
/// </summary>
public sealed class FileTreeBuilder(IRepositoryWriter writer, int chunkSize)
{
    private const int MaxIssuesKept = 100;

    private readonly Stack<Folder> _folders = new();
    private readonly Dictionary<ulong, (ChunkStreamRef Content, long Length)> _links = [];
    private readonly List<string> _issues = [];
    private readonly byte[] _piece = new byte[chunkSize];
    private long _files;
    private long _directories;
    private long _bytes;
    private long _issueCount;
    private long _changed;
    private long _stored;

    /// <summary>The last heartbeat of the mover (entries, bytes, the path being read), for progress and stall messages.</summary>
    public TreeHeartbeat? LastHeartbeat { get; private set; }

    /// <summary>Content bytes received so far.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    public sealed record Result(ChunkIndex Root, FileTreeRootMeta RootMeta, FileTreeStats Stats, IReadOnlyList<string> Issues);

    public async Task<Result> BuildAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new MoverTreeReader(stream);
        var root = await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        _folders.Push(new Folder([], root));
        FileState? file = null;
        while (true)
        {
            var record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (file is not null && record is not (TreePiece or TreeHole or TreeFileEndRecord or TreeFileAbort or TreeHeartbeat))
            {
                throw new InvalidDataException($"The mover sent {record.GetType().Name} in the middle of the file {Path(file.Name)}.");
            }

            switch (record)
            {
                case TreeEnter enter:
                    _folders.Push(new Folder(enter.Name, enter.Stat));
                    break;
                case TreeLeave:
                    if (_folders.Count < 2)
                    {
                        throw new InvalidDataException("The mover left the root folder.");
                    }

                    var done = _folders.Pop();
                    var listing = await WriteListingAsync(done, cancellationToken).ConfigureAwait(false);
                    _directories++;
                    var parent = _folders.Peek();
                    parent.Add(Entry(done.Name, FileTreeEntryType.Directory, done.Stat), listing, done.Files, done.Bytes);
                    break;
                case TreeFileStart start:
                    file = new FileState(start.Name, start.Stat);
                    break;
                case TreePiece piece:
                    await AppendAsync(file ?? throw Outside("a piece"), piece.Data, cancellationToken).ConfigureAwait(false);
                    break;
                case TreeHole hole:
                    await AppendZerosAsync(file ?? throw Outside("a hole"), hole.Length, cancellationToken).ConfigureAwait(false);
                    break;
                case TreeFileEndRecord end:
                    await EndFileAsync(file ?? throw Outside("the end of a file"), end, cancellationToken).ConfigureAwait(false);
                    file = null;
                    break;
                case TreeFileAbort abort:
                    Unreadable((file ?? throw Outside("an aborted file")).Name, file.Stat, FileTreeEntryType.File, abort.Reason);
                    file = null;
                    break;
                case TreeHardLink link:
                    if (_links.TryGetValue(link.Stat.Inode, out var first))
                    {
                        _folders.Peek().AddFile(new FileTreeEntry
                        {
                            Name = link.Name, Type = FileTreeEntryType.File, Mode = link.Stat.Mode, Uid = link.Stat.Uid, Gid = link.Stat.Gid, MtimeNs = link.Stat.MtimeNs,
                            CtimeNs = link.Stat.CtimeNs, Inode = link.Stat.Inode, Links = link.Stat.Links, Size = first.Length, Content = first.Content,
                        });
                        _files++;
                    }
                    else
                    {
                        Unreadable(link.Name, link.Stat, FileTreeEntryType.File, "another name of a file that was not read");
                    }

                    break;
                case TreeSymlink symlink:
                    _folders.Peek().AddOther(Entry(symlink.Name, FileTreeEntryType.Symlink, symlink.Stat) with { LinkTarget = symlink.Target });
                    break;
                case TreeSpecial special:
                    _folders.Peek().AddOther(Entry(special.Name, (FileTreeEntryType)special.Type, special.Stat) with { Rdev = special.Rdev });
                    break;
                case TreeIssue issue:
                    Unreadable(issue.Name, null, FileTreeEntryType.File, issue.Reason);
                    break;
                case TreeHeartbeat heartbeat:
                    LastHeartbeat = heartbeat;
                    break;
                case TreeUnchanged unchanged:
                    throw new InvalidDataException($"The mover sent {Path(unchanged.Name)} as unchanged, but this run has no previous tree.");
                case TreeEnd:
                    if (_folders.Count != 1)
                    {
                        throw new InvalidDataException($"The mover's stream ended inside the folder {Path([])}.");
                    }

                    return await FinishAsync(root, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AppendAsync(FileState file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, chunkSize - file.Filled);
            data.Span[..n].CopyTo(_piece.AsSpan(file.Filled));
            file.Filled += n;
            file.Length += n;
            data = data[n..];
            Interlocked.Add(ref _bytes, n);
            if (file.Filled == chunkSize)
            {
                await FlushPieceAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AppendZerosAsync(FileState file, long length, CancellationToken cancellationToken)
    {
        file.Holes = true;
        while (length > 0)
        {
            if (file.Filled == 0 && length >= chunkSize)
            {
                // A whole piece of zeros: not stored, not even hashed.
                file.Ids.Add(ChunkId.Zero);
                file.Length += chunkSize;
                length -= chunkSize;
                continue;
            }

            var n = (int)Math.Min(length, chunkSize - file.Filled);
            Array.Clear(_piece, file.Filled, n);
            file.Filled += n;
            file.Length += n;
            length -= n;
            if (file.Filled == chunkSize)
            {
                await FlushPieceAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushPieceAsync(FileState file, CancellationToken cancellationToken)
    {
        file.Ids.Add(await ChunkStreams.PutPieceAsync(_piece.AsMemory(0, file.Filled), PutAsync, cancellationToken).ConfigureAwait(false));
        file.Filled = 0;
    }

    private async Task EndFileAsync(FileState file, TreeFileEndRecord end, CancellationToken cancellationToken)
    {
        if (end.Length != file.Length)
        {
            throw new InvalidDataException($"The mover sent {file.Length} bytes of {Path(file.Name)} but says {end.Length}.");
        }

        ChunkStreamRef content;
        if (file.Ids.Count == 0 && file.Filled <= ChunkStreams.InlineLimit)
        {
            content = ChunkStreamRef.OfInline(_piece.AsSpan(0, file.Filled).ToArray());
        }
        else
        {
            if (file.Filled > 0)
            {
                await FlushPieceAsync(file, cancellationToken).ConfigureAwait(false);
            }

            content = await ChunkStreams.FromIdsAsync(file.Length, [.. file.Ids], chunkSize, PutAsync, cancellationToken).ConfigureAwait(false);
        }

        var flags = (end.End == TreeFileEnd.Complete ? FileTreeEntryFlags.None : FileTreeEntryFlags.ChangedWhileRead)
                    | (end.Racy ? FileTreeEntryFlags.Racy : FileTreeEntryFlags.None)
                    | (end.Holes || file.Holes ? FileTreeEntryFlags.SparseSeen : FileTreeEntryFlags.None);
        if (end.End != TreeFileEnd.Complete)
        {
            _changed++;
        }

        _folders.Peek().AddFile(Entry(file.Name, FileTreeEntryType.File, file.Stat) with { Flags = flags, Size = file.Length, Content = content });
        _files++;
        if (file.Stat.Links > 1)
        {
            _links[file.Stat.Inode] = (content, file.Length);
        }
    }

    private void Unreadable(byte[] name, TreeStat? stat, FileTreeEntryType type, string reason)
    {
        var entry = stat is { } s ? Entry(name, type, s) : new FileTreeEntry { Name = name, Type = type };
        _folders.Peek().AddOther(entry with { Flags = FileTreeEntryFlags.Unreadable, Reason = reason, Size = 0 });
        _issueCount++;
        if (_issues.Count < MaxIssuesKept)
        {
            _issues.Add($"{Path(name)}: {reason}");
        }
    }

    private async Task<ChunkStreamRef> WriteListingAsync(Folder folder, CancellationToken cancellationToken) =>
        await ChunkStreams.WriteAsync(FileTreeListing.Serialize(folder.Entries), chunkSize, PutAsync, allowInline: false, cancellationToken).ConfigureAwait(false);

    private async Task<Result> FinishAsync(TreeStat root, CancellationToken cancellationToken)
    {
        // The root listing's pieces, all of them in the root index (no indirect id list).
        var listing = FileTreeListing.Serialize(_folders.Peek().Entries);
        var ids = new ChunkId[(listing.Length + chunkSize - 1) / chunkSize];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = await ChunkStreams.PutPieceAsync(listing.AsMemory(i * chunkSize, Math.Min(chunkSize, listing.Length - (i * chunkSize))), PutAsync, cancellationToken)
                .ConfigureAwait(false);
        }

        var folder = _folders.Peek();
        return new Result(
            new ChunkIndex(chunkSize, listing.Length, ids),
            new FileTreeRootMeta(root.Mode, root.Uid, root.Gid, root.MtimeNs),
            new FileTreeStats
            {
                Files = _files, Directories = _directories, Bytes = folder.Bytes, Issues = _issueCount, ChangedWhileRead = _changed, ReadBytes = Bytes,
                NewStoredBytes = Interlocked.Read(ref _stored),
            },
            _issues);
    }

    /// <summary>Stores a chunk, counting what was new in the repository.</summary>
    private async ValueTask PutAsync(ChunkId id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var result = await writer.PutChunkAsync(id, data, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ChunkPutOutcome.Stored)
        {
            Interlocked.Add(ref _stored, result.StoredBytes);
        }
    }

    private static FileTreeEntry Entry(byte[] name, FileTreeEntryType type, TreeStat stat) => new()
    {
        Name = name, Type = type, Mode = stat.Mode, Uid = stat.Uid, Gid = stat.Gid, MtimeNs = stat.MtimeNs, CtimeNs = stat.CtimeNs, Inode = stat.Inode, Links = stat.Links,
        Size = type == FileTreeEntryType.Directory ? 0 : stat.Size,
    };

    private string Path(byte[] name) =>
        string.Join('/', _folders.Reverse().Skip(1).Select(f => Encoding.UTF8.GetString(f.Name)).Append(Encoding.UTF8.GetString(name)).Where(p => p.Length > 0)) is { Length: > 0 } path
            ? path
            : "/";

    private static InvalidDataException Outside(string what) => new($"The mover sent {what} outside a file.");

    private sealed class FileState(byte[] name, TreeStat stat)
    {
        public byte[] Name { get; } = name;

        public TreeStat Stat { get; } = stat;

        public List<ChunkId> Ids { get; } = [];

        public int Filled { get; set; }

        public long Length { get; set; }

        public bool Holes { get; set; }
    }

    private sealed class Folder(byte[] name, TreeStat stat)
    {
        public byte[] Name { get; } = name;

        public TreeStat Stat { get; } = stat;

        public List<FileTreeEntry> Entries { get; } = [];

        public long Files { get; private set; }

        public long Bytes { get; private set; }

        public void AddFile(FileTreeEntry entry)
        {
            Entries.Add(entry);
            Files++;
            Bytes += entry.Size;
        }

        public void AddOther(FileTreeEntry entry) => Entries.Add(entry);

        public void Add(FileTreeEntry directory, ChunkStreamRef listing, long files, long bytes)
        {
            Entries.Add(directory with { Listing = listing, SubtreeFiles = files, SubtreeBytes = bytes });
            Files += files;
            Bytes += bytes;
        }
    }
}
