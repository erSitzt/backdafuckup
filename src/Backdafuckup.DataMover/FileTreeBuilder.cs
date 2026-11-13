using System.Diagnostics;
using System.Globalization;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

/// <summary>
/// Builds a file tree in the repository from a mover's <see cref="MoverTree"/> stream (spec
/// 2026-11-12-1952-kubernetes-file-level-volumes): file content is cut into chunks of the repository's size (all-zero pieces
/// not stored), each folder becomes a listing when it is left, and the root listing's chunks are the result. Memory grows
/// with the depth and the width of the folders, not with the volume.
/// <list type="bullet">
/// <item>Incremental (<see cref="Previous"/>): a file the mover sends as unchanged is looked up in the previous tree with
/// the builder's own cursor; it must be there, reusable, with the same size, times and inode, and its content is taken
/// from there. Anything else is a broken stream.</item>
/// <item>A mover that keeps the connection (heartbeats) but sends nothing new for <see cref="StallTimeout"/> while the
/// builder waits for it is given up on, naming the path it is stuck at. Time the builder spends storing does not count.</item>
/// </list>
/// </summary>
public sealed class FileTreeBuilder(IRepositoryWriter writer, int chunkSize)
{
    private const int MaxIssuesKept = 100;

    private readonly Stack<Folder> _folders = new();
    private readonly Stack<List<FileTreeEntry>?> _base = new();
    private readonly Dictionary<ulong, (ChunkStreamRef Content, long Length)> _links = [];
    private readonly List<string> _issues = [];
    private readonly byte[] _piece = new byte[chunkSize];
    private long _files;
    private long _directories;
    private long _bytes;
    private long _processed;
    private long _unchanged;
    private long _unchangedBytes;
    private long _issueCount;
    private long _changed;
    private long _stored;
    private long _new;
    private long _deduped;
    private long _waitingSince;
    private long _stalledTicks;

    /// <summary>The volume's tree in the previous run: files the mover sends as unchanged take their content from it.</summary>
    public FileTreeBase? Previous { get; init; }

    /// <summary>How long the mover may send nothing new (only heartbeats with the same counts) while the builder waits for it.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The last heartbeat of the mover (entries, bytes, the path being read), for progress and stall messages.</summary>
    public TreeHeartbeat? LastHeartbeat { get; private set; }

    /// <summary>Content bytes received so far (read by the mover in this run).</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Files done so far, read or unchanged.</summary>
    public long Files => Interlocked.Read(ref _files);

    /// <summary>Content bytes done so far: read, holes, and the size of unchanged files and further names of a file.</summary>
    public long ProcessedBytes => Interlocked.Read(ref _processed);

    /// <summary>Files taken from the previous tree without reading them, and their bytes.</summary>
    public long UnchangedFiles => Interlocked.Read(ref _unchanged);

    public long UnchangedBytes => Interlocked.Read(ref _unchangedBytes);

    /// <param name="NewBytes">Data of the chunks the repository did not have yet (before compression).</param>
    /// <param name="DedupedBytes">Data of the chunks it had already.</param>
    public sealed record Result(ChunkIndex Root, FileTreeRootMeta RootMeta, FileTreeStats Stats, IReadOnlyList<string> Issues, long NewBytes = 0, long DedupedBytes = 0);

    public async Task<Result> BuildAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watching = new CancellationTokenSource();
        var watchdog = WatchAsync(stalled, watching.Token);
        try
        {
            return await BuildCoreAsync(stream, stalled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stalled.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Stalled();
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    private async Task<Result> BuildCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new MoverTreeReader(stream);
        Volatile.Write(ref _waitingSince, Stopwatch.GetTimestamp());
        var root = await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _waitingSince, 0);
        _folders.Push(new Folder([], root));
        _base.Push(Previous?.Root());
        FileState? file = null;
        while (true)
        {
            var record = await ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            if (file is not null && record is not (TreePiece or TreeHole or TreeFileEndRecord or TreeFileAbort or TreeHeartbeat))
            {
                throw new InvalidDataException($"The mover sent {record.GetType().Name} in the middle of the file {Path(file.Name)}.");
            }

            switch (record)
            {
                case TreeEnter enter:
                    _folders.Push(new Folder(enter.Name, enter.Stat));
                    _base.Push(_base.Peek() is { } parentBase && FileTreeBase.Find(parentBase, enter.Name) is { Type: FileTreeEntryType.Directory, Listing: { } listed }
                        ? Previous!.Load(listed)
                        : null);
                    break;
                case TreeLeave:
                    if (_folders.Count < 2)
                    {
                        throw new InvalidDataException("The mover left the root folder.");
                    }

                    _base.Pop();
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
                        Interlocked.Increment(ref _files);
                        Interlocked.Add(ref _processed, first.Length);
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
                    Unchanged(unchanged);
                    break;
                case TreeEnd:
                    if (_folders.Count != 1)
                    {
                        throw new InvalidDataException($"The mover's stream ended inside the folder {Path([])}.");
                    }

                    return await FinishAsync(root, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The next record, timing how long the builder waited for it when it brings nothing new.</summary>
    private async ValueTask<TreeRecord> ReadAsync(MoverTreeReader reader, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Volatile.Write(ref _waitingSince, started);
        var record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _waitingSince, 0);
        var progress = record is not TreeHeartbeat heartbeat || LastHeartbeat is not { } last || heartbeat.Entries != last.Entries || heartbeat.Bytes != last.Bytes;
        Volatile.Write(ref _stalledTicks, progress ? 0 : Volatile.Read(ref _stalledTicks) + (Stopwatch.GetTimestamp() - started));
        if (StalledFor() > StallTimeout)
        {
            throw Stalled();
        }

        return record;
    }

    /// <summary>How long the builder has waited for the mover since it last sent something new.</summary>
    private TimeSpan StalledFor()
    {
        var waiting = Volatile.Read(ref _waitingSince);
        var ticks = Volatile.Read(ref _stalledTicks) + (waiting == 0 ? 0 : Stopwatch.GetTimestamp() - waiting);
        return TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }

    /// <summary>Cancels the read the builder waits in once the mover has been stalled too long (a read that never returns).</summary>
    private async Task WatchAsync(CancellationTokenSource stalled, CancellationToken stop)
    {
        var period = TimeSpan.FromTicks(Math.Clamp(StallTimeout.Ticks / 4, TimeSpan.TicksPerMillisecond * 100, TimeSpan.TicksPerSecond * 10));
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (StalledFor() > StallTimeout)
                {
                    await stalled.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The build is over.
        }
    }

    private TimeoutException Stalled()
    {
        var at = LastHeartbeat is { Path.Length: > 0 } heartbeat ? heartbeat.Path : Path([]);
        var minutes = StallTimeout.TotalMinutes >= 1
            ? $"{StallTimeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minute(s)"
            : $"{StallTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} second(s)";
        return new TimeoutException(
            $"The mover has sent nothing new for {minutes}: it is at {at}, after {LastHeartbeat?.Entries ?? Files:N0} entries and " +
            $"{(Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB read. A hung NFS server, or a file whose read does not return?");
    }

    /// <summary>A file the mover found unchanged: its content comes from the previous tree, where it must be as the mover says.</summary>
    private void Unchanged(TreeUnchanged record)
    {
        if (Previous is null)
        {
            throw new InvalidDataException($"The mover sent {Path(record.Name)} as unchanged, but this run has no previous tree.");
        }

        var before = _base.Peek() is { } entries ? FileTreeBase.Find(entries, record.Name) : null;
        var why = before is null ? "the previous tree has no such file"
            : !FileTreeBase.IsReusable(before) ? "its copy then cannot be taken over (not read in full, or changing while it was read)"
            : !new PreviousFile(before.Size, before.MtimeNs, before.CtimeNs, before.Inode).Matches(record.Stat) ? "its size, times or inode were different then"
            : null;
        if (why is not null)
        {
            throw new InvalidDataException($"The mover sent {Path(record.Name)} as unchanged since the previous run, but {why}.");
        }

        _folders.Peek().AddFile(Entry(record.Name, FileTreeEntryType.File, record.Stat) with
        {
            Flags = before!.Flags & FileTreeEntryFlags.SparseSeen, Size = before.Size, Content = before.Content,
        });
        Interlocked.Increment(ref _files);
        Interlocked.Increment(ref _unchanged);
        Interlocked.Add(ref _unchangedBytes, before.Size);
        Interlocked.Add(ref _processed, before.Size);
        if (record.Stat.Links > 1)
        {
            _links[record.Stat.Inode] = (before.Content!, before.Size);
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
            Interlocked.Add(ref _processed, n);
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
                Interlocked.Add(ref _processed, chunkSize);
                continue;
            }

            var n = (int)Math.Min(length, chunkSize - file.Filled);
            Array.Clear(_piece, file.Filled, n);
            file.Filled += n;
            file.Length += n;
            length -= n;
            Interlocked.Add(ref _processed, n);
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
        Interlocked.Increment(ref _files);
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
                Files = Files, Directories = _directories, Bytes = folder.Bytes, Issues = _issueCount, ChangedWhileRead = _changed, ReadBytes = Bytes,
                UnchangedFiles = UnchangedFiles, UnchangedBytes = UnchangedBytes, NewStoredBytes = Interlocked.Read(ref _stored),
            },
            _issues,
            Interlocked.Read(ref _new),
            Interlocked.Read(ref _deduped));
    }

    /// <summary>Stores a chunk, counting what was new in the repository.</summary>
    private async ValueTask PutAsync(ChunkId id, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var result = await writer.PutChunkAsync(id, data, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ChunkPutOutcome.Stored)
        {
            Interlocked.Add(ref _stored, result.StoredBytes);
            Interlocked.Add(ref _new, data.Length);
        }
        else
        {
            Interlocked.Add(ref _deduped, data.Length);
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
