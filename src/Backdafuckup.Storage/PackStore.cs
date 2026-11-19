using System.Collections.Concurrent;
using System.Globalization;
using Backdafuckup.Core.Repository;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Storage;

/// <summary>
/// The packs of a format 2 repository: the registry of pack files, the index (chunk id → pack and offset) and the
/// writing and sealing of packs.
/// <para>
/// The index has three layers, searched newest first: <c>live</c> (records of packs not yet in an index segment, in
/// memory), delta segments (one per commit or batch of sealed packs, in memory) and the base segment (on disk, only its
/// tables in memory). The pack registry is authoritative: entries of packs that are gone are ignored, so deleting a
/// pack never waits for an index rewrite.
/// </para>
/// </summary>
internal sealed class PackStore : IDisposable
{
    public const int MaxDeltas = 16;
    private const int PersistAfterSealedPacks = 64;

    private readonly string _root;
    private readonly string _packsDirectory;
    private readonly string _incomingDirectory;
    private readonly string _indexDirectory;
    private readonly string _tempDirectory;
    private readonly Guid _repositoryId;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, PackFile> _registry = new();
    private readonly ConcurrentDictionary<ChunkId, PackLocation> _live = new();
    private readonly ConcurrentDictionary<PackFile, PackTrailerEntry[]> _unpersisted = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly PackHandleCache _handles = new(256);
    private readonly List<DiskSegment> _retiredBases = [];
    private Snapshot _snapshot = new(null, []);
    private long _seq;

    public PackStore(string root, Guid repositoryId, long targetSize, TimeProvider time)
    {
        _root = root;
        _repositoryId = repositoryId;
        TargetSize = targetSize;
        _time = time;
        _packsDirectory = Path.Combine(root, "packs");
        _incomingDirectory = Path.Combine(root, "incoming");
        _indexDirectory = Path.Combine(root, "index");
        _tempDirectory = Path.Combine(root, "tmp");
    }

    public long TargetSize { get; }

    public string PacksDirectory => _packsDirectory;

    public int DeltaCount => Volatile.Read(ref _snapshot).Deltas.Length;

    public long IndexEntries
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return (snapshot.Base?.EntryCount ?? 0) + snapshot.Deltas.Sum(d => (long)d.Entries.Length) + _live.Count;
        }
    }

    /// <summary>Bytes the index keeps in memory (approximate).</summary>
    public long IndexMemoryBytes
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return (snapshot.Base?.MemoryBytes ?? 0) + snapshot.Deltas.Sum(d => (long)d.Entries.Length * 48) + (_live.Count * 120L);
        }
    }

    public IEnumerable<PackFile> Packs => _registry.Values;

    public static void CreateDirectories(string root)
    {
        foreach (var sub in new[] { "packs", "incoming", "index" })
        {
            Directory.CreateDirectory(Path.Combine(root, sub));
        }
    }

    /// <summary>Tests only: runs before an appender is created (to hold a writer inside its first put).</summary>
    internal Action? CreatingAppender { get; set; }

    public PackAppender CreateAppender()
    {
        CreatingAppender?.Invoke();
        return new(this, TargetSize);
    }

    // ---- Opening and recovery ----

    /// <summary>Builds the registry and the index; returns what it found and repaired.</summary>
    /// <param name="maxChunkSize">The repository's chunk size: no record of a pack holds more (bounds the salvage of unsealed packs).</param>
    public async Task<List<string>> OpenAsync(int maxChunkSize, CancellationToken cancellationToken)
    {
        var report = new List<string>();
        CreateDirectories(_root);

        // Packs that were never sealed (a writer or GC stopped before sealing them) are salvaged, not deleted: every
        // complete record, checked against its id, goes into a sealed pack that the trailer scan below indexes. A commit
        // seals what it refers to, but if a writer ever breaks that rule, a restart must cost disk space, not a chunk of
        // a committed restore point. Chunks that nothing refers to go with the next garbage collection.
        int salvagedPacks = 0, salvagedChunks = 0, abandoned = 0;
        foreach (var file in Directory.EnumerateFiles(_incomingDirectory).ToList())
        {
            switch (SalvageIncoming(file, maxChunkSize, report))
            {
                case > 0 and var chunks:
                    salvagedPacks++;
                    salvagedChunks += chunks;
                    break;
                case 0:
                    abandoned++;
                    break;
            }
        }

        if (salvagedPacks > 0)
        {
            report.Add($"Sealed {salvagedPacks} unsealed pack(s) of interrupted writes from '{_incomingDirectory}' into '{_packsDirectory}': " +
                       $"{salvagedChunks} complete chunk(s), each checked against its id; those no restore point refers to go with the next garbage collection.");
        }

        if (abandoned > 0)
        {
            report.Add($"Removed {abandoned} unsealed pack(s) of interrupted writes from '{_incomingDirectory}' (no complete chunk in them).");
        }

        foreach (var file in Directory.EnumerateFiles(_packsDirectory, "*" + PackFormat.Extension, SearchOption.AllDirectories))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id))
            {
                continue;
            }

            var info = new FileInfo(file);
            _registry[id] = new PackFile(id, file, PackState.Sealed, info.Length, info.LastWriteTimeUtc);
        }

        foreach (var pack in _registry.Values)
        {
            pack.Sealed.TrySetResult();
        }

        // Segments: the newest base, and the deltas written after it.
        PackFile? Resolve(Guid id) => _registry.GetValueOrDefault(id);
        var segments = Directory.EnumerateFiles(_indexDirectory, "*" + PackSegmentFormat.Extension).Select(ParseSegmentName).Where(s => s is not null).Select(s => s!.Value).ToList();
        DiskSegment? baseSegment = null;
        var deltas = new List<MemorySegment>();
        try
        {
            var newestBase = segments.Where(s => s.IsBase).OrderByDescending(s => s.Seq).FirstOrDefault();
            if (newestBase.Path is not null)
            {
                baseSegment = DiskSegment.Open(newestBase.Path, Resolve);
            }

            var baseSeq = baseSegment?.Seq ?? 0;
            foreach (var delta in segments.Where(s => !s.IsBase && s.Seq > baseSeq).OrderByDescending(s => s.Seq))
            {
                deltas.Add(MemorySegment.Load(delta.Path, Resolve));
            }

            foreach (var stale in segments.Where(s => (s.IsBase && s.Seq != baseSeq) || (!s.IsBase && s.Seq <= baseSeq)))
            {
                File.Delete(stale.Path);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A damaged index is rebuilt from the packs, which describe themselves.
            report.Add($"The index in '{_indexDirectory}' is damaged ({ex.Message}); it is rebuilt from the packs' trailers.");
            baseSegment?.Dispose();
            baseSegment = null;
            deltas.Clear();
            foreach (var segment in segments)
            {
                File.Delete(segment.Path);
            }
        }

        var gone = (baseSegment is null ? [] : baseSegment.PackIds.Where((_, i) => baseSegment.Packs[i] is null))
            .Concat(deltas.SelectMany(d => d.PackIds.Where((_, i) => d.Packs[i] is null)))
            .Distinct()
            .ToList();
        if (gone.Count > 0)
        {
            report.Add(
                $"The index names {gone.Count} pack(s) that are not in '{_packsDirectory}' ({string.Join(", ", gone.Take(5).Select(g => g.ToString("N") + PackFormat.Extension))}" +
                $"{(gone.Count > 5 ? ", …" : "")}); their chunks are missing. Run a verify of the restore points to see which are affected.");
        }

        _seq = segments.Count == 0 ? 0 : segments.Max(s => s.Seq);
        Volatile.Write(ref _snapshot, new Snapshot(baseSegment, [.. deltas]));

        // Packs no segment covers (sealed after the last index write, e.g. before a crash): read their trailers.
        var covered = new HashSet<PackFile>();
        foreach (var pack in baseSegment?.Packs ?? [])
        {
            if (pack is not null)
            {
                covered.Add(pack);
            }
        }

        foreach (var pack in deltas.SelectMany(d => d.Packs))
        {
            if (pack is not null)
            {
                covered.Add(pack);
            }
        }

        var recovered = 0;
        foreach (var pack in _registry.Values.Where(p => !covered.Contains(p)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var handle = File.OpenHandle(pack.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var repositoryId = ReadHeaderOf(handle, pack.Path);
                if (repositoryId != _repositoryId)
                {
                    report.Add($"Pack '{pack.Path}' belongs to repository {repositoryId}, not {_repositoryId}; it is left alone and not used.");
                    pack.State = PackState.Dead;
                    continue;
                }

                var entries = PackFormat.ReadTrailer(handle, pack.Path);
                foreach (var entry in entries)
                {
                    _live[entry.Id] = new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength);
                }

                _unpersisted[pack] = entries;
                recovered++;
            }
            catch (InvalidDataException ex)
            {
                report.Add($"{ex.Message} Its chunks are not used; the pack is kept for inspection (see the admin guide, 'Repository format and pack files').");
                pack.State = PackState.Dead;
            }
        }

        if (recovered > 0)
        {
            report.Add($"Indexed {recovered} pack(s) that the index did not cover yet.");
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _snapshot).Deltas.Length > MaxDeltas)
        {
            await MergeAsync(cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    // ---- Writing ----

    public OpenPack CreateOpenPack(PackAppender owner)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_incomingDirectory, id.ToString("N") + PackFormat.Extension);
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        try
        {
            var header = new byte[PackFormat.HeaderSize];
            PackFormat.WriteHeader(header, _repositoryId);
            RandomAccess.Write(handle, header, 0);
        }
        catch
        {
            handle.Dispose();
            File.Delete(path);
            throw;
        }

        var pack = new PackFile(id, path, PackState.Open, PackFormat.HeaderSize, default) { Owner = owner };
        _registry[id] = pack;
        return new OpenPack(pack, handle);
    }

    public void DiscardOpenPack(OpenPack open)
    {
        open.Handle.Dispose();
        open.Pack.State = PackState.Dead;
        open.Pack.Owner = null;
        _registry.TryRemove(open.Pack.Id, out _);
        _handles.Forget(open.Pack);
        TryDelete(open.Pack.Path);
        open.Pack.Sealed.TrySetException(new InvalidOperationException($"Pack '{open.Pack.Path}' was discarded empty."));
    }

    /// <summary>Records a chunk's location right after its record was appended (under the chunk's stripe lock).</summary>
    public void AddLive(ChunkId id, PackLocation location) => _live[id] = location;

    /// <summary>Writes the trailer and footer, flushes to disk and moves the pack into <c>packs/</c>.</summary>
    public async Task SealAsync(OpenPack open)
    {
        var pack = open.Pack;
        try
        {
            await Task.Yield();
            var tail = PackFormat.BuildTail(open.Entries, open.Position);
            RandomAccess.Write(open.Handle, tail, open.Position);
            RandomAccess.FlushToDisk(open.Handle);
            open.Handle.Dispose();

            var directory = Path.Combine(_packsDirectory, pack.Name[..2]);
            var created = !Directory.Exists(directory);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, pack.Name + PackFormat.Extension);
            File.Move(pack.Path, destination);
            AtomicFile.SyncDirectory(directory);
            if (created)
            {
                AtomicFile.SyncDirectory(_packsDirectory);
            }

            pack.Path = destination;
            pack.Length = open.Position + tail.Length;
            pack.SealedUtc = File.GetLastWriteTimeUtc(destination); // like loose chunks: GC compares file times
            pack.Owner = null;
            pack.State = PackState.Sealed;
            _unpersisted[pack] = [.. open.Entries];
            pack.Sealed.TrySetResult();
        }
        catch (Exception ex)
        {
            open.Handle.Dispose();
            pack.State = PackState.Dead;
            pack.Owner = null;
            var error = new IOException($"Sealing pack '{pack.Path}' failed: {ex.Message}", ex);
            pack.Sealed.TrySetException(error);
            throw error;
        }

        if (_unpersisted.Count >= PersistAfterSealedPacks)
        {
            await PersistAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a delta segment for every sealed pack whose records are only in memory.</summary>
    public async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var packs = _unpersisted.Keys.ToList();
            foreach (var dead in packs.Where(p => p.State == PackState.Dead))
            {
                _unpersisted.TryRemove(dead, out _);
            }

            packs.RemoveAll(p => p.State == PackState.Dead);
            if (packs.Count == 0)
            {
                return;
            }

            var entries = new List<(PackTrailerEntry Entry, PackFile Pack)>();
            foreach (var pack in packs)
            {
                foreach (var entry in _unpersisted[pack])
                {
                    entries.Add((entry, pack));
                }
            }

            entries.Sort((a, b) => a.Entry.Id.CompareTo(b.Entry.Id));
            var seq = ++_seq;
            var path = SegmentPath(isBase: false, seq);
            var segmentEntries = new List<SegmentEntry>(entries.Count);
            var packTable = new List<PackFile>();
            using (var writer = new PackSegmentWriter(Path.Combine(_tempDirectory, $"D-{seq}.{Guid.NewGuid():N}.tmp"), PackSegmentFormat.KindDelta, entries.Count))
            {
                ChunkId? last = null;
                foreach (var (entry, pack) in entries)
                {
                    if (last == entry.Id)
                    {
                        continue;
                    }

                    writer.Add(entry.Id, pack, entry.Offset, entry.StoredLength, entry.RawLength);
                    last = entry.Id;
                }

                writer.Finish(seq);
                packTable.AddRange(writer.Packs);
                File.Move(writer.TempPath, path);
            }

            AtomicFile.SyncDirectory(_indexDirectory);
            var ordinals = packTable.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
            ChunkId? previous = null;
            foreach (var (entry, pack) in entries)
            {
                if (previous == entry.Id)
                {
                    continue;
                }

                segmentEntries.Add(new SegmentEntry(entry.Id, ordinals[pack], entry.Offset, entry.StoredLength, entry.RawLength));
                previous = entry.Id;
            }

            var delta = new MemorySegment(seq, path, [.. segmentEntries], [.. packTable]);
            var snapshot = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, new Snapshot(snapshot.Base, [delta, .. snapshot.Deltas]));

            // Only now leave the live layer: a lookup in between finds the record in both places.
            foreach (var (entry, pack) in entries)
            {
                _live.TryRemove(new KeyValuePair<ChunkId, PackLocation>(entry.Id, new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength)));
            }

            foreach (var pack in packs)
            {
                _unpersisted.TryRemove(pack, out _);
            }

            if (Volatile.Read(ref _snapshot).Deltas.Length > MaxDeltas)
            {
                MergeDeltasLocked();
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    // ---- Lookups and reads ----

    /// <summary>A copy of <paramref name="id"/> that new writes may deduplicate against (open or sealed pack).</summary>
    public bool TryLocateForDedup(ChunkId id, out PackLocation location) => TryLocate(id, LocateFor.Dedup, except: null, out location);

    /// <summary>A copy that can be read (also from a pack GC is repacking).</summary>
    public bool TryLocate(ChunkId id, out PackLocation location) => TryLocate(id, LocateFor.Read, except: null, out location);

    /// <summary>A durable copy (sealed pack) of <paramref name="id"/>.</summary>
    public bool TryLocateDurable(ChunkId id, out PackLocation location) => TryLocate(id, LocateFor.Durable, except: null, out location);

    /// <summary>A durable copy outside <paramref name="except"/> that is not being repacked (GC: does another copy survive?).</summary>
    public bool HasCopyOutside(ChunkId id, PackFile except) => TryLocate(id, LocateFor.Durable, except, out _);

    public int ReadStored(PackLocation location, Span<byte> destination)
    {
        using var lease = _handles.Acquire(location.Pack);
        PackFormat.ReadExactly(lease.Handle, destination[..(int)location.StoredLength], location.Offset, location.Pack.Path);
        return (int)location.StoredLength;
    }

    public async ValueTask<int> ReadStoredAsync(PackLocation location, Memory<byte> destination, CancellationToken cancellationToken)
    {
        using var lease = _handles.Acquire(location.Pack);
        var length = (int)location.StoredLength;
        var read = 0;
        while (read < length)
        {
            var n = await RandomAccess.ReadAsync(lease.Handle, destination.Slice(read, length - read), location.Offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidDataException($"Pack '{location.Pack.Path}' ends at {location.Offset + read}, inside a record.");
            }

            read += n;
        }

        return length;
    }

    /// <summary>Reads a contiguous range of a pack (several neighbouring records at once).</summary>
    public async ValueTask ReadRangeAsync(PackFile pack, long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        using var lease = _handles.Acquire(pack);
        var read = 0;
        while (read < destination.Length)
        {
            var n = await RandomAccess.ReadAsync(lease.Handle, destination[read..], offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidDataException($"Pack '{pack.Path}' ends at {offset + read}, expected {destination.Length - read} more bytes.");
            }

            read += n;
        }
    }

    public PackTrailerEntry[] ReadTrailer(PackFile pack)
    {
        using var lease = _handles.Acquire(pack);
        return PackFormat.ReadTrailer(lease.Handle, pack.Path);
    }

    /// <summary>Every index entry of a sealed or retiring pack, newest layer first (an id can appear in several packs).</summary>
    public IEnumerable<(ChunkId Id, PackLocation Location)> AllEntries()
    {
        foreach (var (id, location) in _live)
        {
            if (location.Pack.State is PackState.Sealed or PackState.Retiring)
            {
                yield return (id, location);
            }
        }

        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var delta in snapshot.Deltas)
        {
            foreach (var entry in delta.Entries)
            {
                if (delta.Packs[entry.Pack] is { State: PackState.Sealed or PackState.Retiring } pack)
                {
                    yield return (entry.Id, new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength));
                }
            }
        }

        if (snapshot.Base is { } baseSegment)
        {
            foreach (var entry in baseSegment.ReadAll())
            {
                if (baseSegment.Packs[entry.Pack] is { State: PackState.Sealed or PackState.Retiring } pack)
                {
                    yield return (entry.Id, new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength));
                }
            }
        }
    }

    /// <summary>Distinct chunks in readable packs (for tests and diagnostics; reads the whole index).</summary>
    public long CountDistinctChunks()
    {
        var ids = new HashSet<ChunkId>();
        foreach (var (id, location) in _live)
        {
            if (location.Pack.Readable)
            {
                ids.Add(id);
            }
        }

        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var delta in snapshot.Deltas)
        {
            foreach (var entry in delta.Entries)
            {
                if (delta.Packs[entry.Pack] is { Readable: true })
                {
                    ids.Add(entry.Id);
                }
            }
        }

        if (snapshot.Base is { } baseSegment)
        {
            foreach (var entry in baseSegment.ReadAll())
            {
                if (baseSegment.Packs[entry.Pack] is { Readable: true })
                {
                    ids.Add(entry.Id);
                }
            }
        }

        return ids.Count;
    }

    // ---- GC support ----

    /// <summary>Deletes a pack whose records were copied or are unreferenced; its index entries are ignored from now on.</summary>
    public long DeletePack(PackFile pack)
    {
        pack.State = PackState.Dead;
        _registry.TryRemove(pack.Id, out _);
        _unpersisted.TryRemove(pack, out _);
        _handles.Forget(pack);
        return TryDelete(pack.Path) ? pack.Length : 0;
    }

    /// <summary>
    /// Writes a new base segment with every live entry of the base and the deltas (dropping entries of deleted packs
    /// and duplicates) and removes the merged segments.
    /// </summary>
    public async Task MergeAsync(CancellationToken cancellationToken)
    {
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot.Base is null && snapshot.Deltas.Length == 0)
            {
                return;
            }

            if (snapshot.Base is not null && snapshot.Deltas.Length == 0 && snapshot.Base.Packs.All(p => p is { State: PackState.Sealed }))
            {
                return;
            }

            var seq = ++_seq;
            var expected = (snapshot.Base?.EntryCount ?? 0) + snapshot.Deltas.Sum(d => (long)d.Entries.Length);
            var path = SegmentPath(isBase: true, seq);
            using (var writer = new PackSegmentWriter(Path.Combine(_tempDirectory, $"B-{seq}.{Guid.NewGuid():N}.tmp"), PackSegmentFormat.KindBase, Math.Max(1, expected)))
            {
                ChunkId? last = null;
                foreach (var (id, pack, entry) in MergedEntries(snapshot))
                {
                    if (last == id)
                    {
                        continue;
                    }

                    writer.Add(id, pack, entry.Offset, entry.StoredLength, entry.RawLength);
                    last = id;
                }

                writer.Finish(seq);
                File.Move(writer.TempPath, path);
            }

            AtomicFile.SyncDirectory(_indexDirectory);
            var newBase = DiskSegment.Open(path, id => _registry.GetValueOrDefault(id));
            Volatile.Write(ref _snapshot, new Snapshot(newBase, []));

            // Lookups that took the old snapshot may still read the old base: close it at the next merge, not now.
            foreach (var retired in _retiredBases)
            {
                retired.Dispose();
            }

            _retiredBases.Clear();
            if (snapshot.Base is not null)
            {
                _retiredBases.Add(snapshot.Base);
                TryDelete(snapshot.Base.Path);
            }

            foreach (var delta in snapshot.Deltas)
            {
                TryDelete(delta.Path);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public void Dispose()
    {
        Volatile.Read(ref _snapshot).Base?.Dispose();
        foreach (var retired in _retiredBases)
        {
            retired.Dispose();
        }

        _handles.Dispose();
        _indexLock.Dispose();
    }

    private bool TryLocate(ChunkId id, LocateFor purpose, PackFile? except, out PackLocation location)
    {
        static bool Accept(PackFile pack, LocateFor purpose, PackFile? except) =>
            pack != except && purpose switch
            {
                LocateFor.Dedup => pack.UsableForDedup,
                LocateFor.Durable => pack.State == PackState.Sealed,
                _ => pack.Readable,
            };

        if (_live.TryGetValue(id, out location) && Accept(location.Pack, purpose, except))
        {
            return true;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        foreach (var delta in snapshot.Deltas)
        {
            if (delta.TryFind(id, out var entry) && delta.Packs[entry.Pack] is { } pack && Accept(pack, purpose, except))
            {
                location = new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength);
                return true;
            }
        }

        if (snapshot.Base is { } baseSegment && baseSegment.TryFind(id, out var found) && baseSegment.Packs[found.Pack] is { } basePack && Accept(basePack, purpose, except))
        {
            location = new PackLocation(basePack, found.Offset, found.StoredLength, found.RawLength);
            return true;
        }

        location = default;
        return false;
    }

    /// <summary>Merges the delta segments into one (they stay in memory; the base is untouched).</summary>
    private void MergeDeltasLocked()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var seq = ++_seq;
        var path = SegmentPath(isBase: false, seq);
        var merged = new List<(ChunkId Id, PackFile Pack, SegmentEntry Entry)>();
        foreach (var item in MergedEntries(new Snapshot(null, snapshot.Deltas)))
        {
            if (merged.Count == 0 || merged[^1].Id != item.Id)
            {
                merged.Add(item);
            }
        }

        PackFile[] packTable;
        using (var writer = new PackSegmentWriter(Path.Combine(_tempDirectory, $"D-{seq}.{Guid.NewGuid():N}.tmp"), PackSegmentFormat.KindDelta, Math.Max(1, merged.Count)))
        {
            foreach (var (id, pack, entry) in merged)
            {
                writer.Add(id, pack, entry.Offset, entry.StoredLength, entry.RawLength);
            }

            writer.Finish(seq);
            packTable = [.. writer.Packs];
            File.Move(writer.TempPath, path);
        }

        AtomicFile.SyncDirectory(_indexDirectory);
        var ordinals = packTable.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var entries = merged.Select(m => new SegmentEntry(m.Id, ordinals[m.Pack], m.Entry.Offset, m.Entry.StoredLength, m.Entry.RawLength)).ToArray();
        Volatile.Write(ref _snapshot, new Snapshot(snapshot.Base, [new MemorySegment(seq, path, entries, packTable)]));
        foreach (var delta in snapshot.Deltas)
        {
            TryDelete(delta.Path);
        }
    }

    /// <summary>All entries of live packs in id order; for equal ids the newest layer comes first.</summary>
    private static IEnumerable<(ChunkId Id, PackFile Pack, SegmentEntry Entry)> MergedEntries(Snapshot snapshot)
    {
        var sources = new List<IEnumerator<(ChunkId Id, PackFile? Pack, SegmentEntry Entry)>>();
        foreach (var delta in snapshot.Deltas)
        {
            sources.Add(delta.Entries.Select(e => (e.Id, delta.Packs[e.Pack], e)).GetEnumerator());
        }

        if (snapshot.Base is { } baseSegment)
        {
            sources.Add(baseSegment.ReadAll().Select(e => (e.Id, baseSegment.Packs[e.Pack], e)).GetEnumerator());
        }

        try
        {
            // Priority = (id, layer): the newest layer wins for equal ids.
            var queue = new PriorityQueue<int, (ChunkId Id, int Layer)>(Comparer<(ChunkId Id, int Layer)>.Create((a, b) =>
            {
                var c = a.Id.CompareTo(b.Id);
                return c != 0 ? c : a.Layer.CompareTo(b.Layer);
            }));
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i].MoveNext())
                {
                    queue.Enqueue(i, (sources[i].Current.Id, i));
                }
            }

            while (queue.TryDequeue(out var layer, out _))
            {
                var (id, pack, entry) = sources[layer].Current;
                if (pack is { State: not PackState.Dead })
                {
                    yield return (id, pack, entry);
                }

                if (sources[layer].MoveNext())
                {
                    queue.Enqueue(layer, (sources[layer].Current.Id, layer));
                }
            }
        }
        finally
        {
            foreach (var source in sources)
            {
                source.Dispose();
            }
        }
    }

    private string SegmentPath(bool isBase, long seq) =>
        Path.Combine(_indexDirectory, $"{(isBase ? 'B' : 'D')}-{seq.ToString("D20", CultureInfo.InvariantCulture)}{PackSegmentFormat.Extension}");

    private static (string Path, bool IsBase, long Seq)? ParseSegmentName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Length < 3 || name[1] != '-' || name[0] is not ('B' or 'D') || !long.TryParse(name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            return null;
        }

        return (path, name[0] == 'B', seq);
    }

    /// <summary>
    /// Seals an unsealed pack's complete records (cut after the last one that decodes and matches its id) and moves it
    /// into <c>packs/</c>. Returns the number of records; 0 when it had none (it is deleted); null when it is left alone.
    /// </summary>
    private int? SalvageIncoming(string file, int maxChunkSize, List<string> report)
    {
        try
        {
            int count;
            using (var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Guid owner;
                try
                {
                    owner = RandomAccess.GetLength(handle) < PackFormat.HeaderSize ? Guid.Empty : ReadHeaderOf(handle, file);
                }
                catch (InvalidDataException)
                {
                    owner = Guid.Empty;
                }

                if (owner != Guid.Empty && owner != _repositoryId)
                {
                    report.Add($"Unsealed pack '{file}' belongs to repository {owner}, not {_repositoryId}; it is left alone.");
                    return null;
                }

                var entries = owner == Guid.Empty ? [] : PackFormat.Salvage(handle, maxChunkSize);
                if (entries.Count == 0)
                {
                    count = 0;
                }
                else
                {
                    var end = (long)entries[^1].Offset + entries[^1].StoredLength;
                    var tail = PackFormat.BuildTail(entries, end);
                    RandomAccess.Write(handle, tail, end);
                    RandomAccess.SetLength(handle, end + tail.Length);
                    RandomAccess.FlushToDisk(handle);
                    count = entries.Count;
                }
            }

            if (count == 0)
            {
                File.Delete(file);
                return 0;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(name, "N", out _))
            {
                name = Guid.NewGuid().ToString("N");
            }

            var directory = Path.Combine(_packsDirectory, name[..2]);
            Directory.CreateDirectory(directory);
            File.Move(file, Path.Combine(directory, name + PackFormat.Extension));
            AtomicFile.SyncDirectory(directory);
            AtomicFile.SyncDirectory(_packsDirectory);
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Add($"Unsealed pack '{file}' could not be salvaged ({ex.Message}); it is left in place and tried again at the next open.");
            return null;
        }
    }

    private static Guid ReadHeaderOf(SafeFileHandle handle, string path)
    {
        var header = new byte[PackFormat.HeaderSize];
        PackFormat.ReadExactly(handle, header, 0, path);
        return PackFormat.ReadHeader(header, path);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private enum LocateFor
    {
        Dedup,
        Read,
        Durable,
    }

    private sealed record Snapshot(DiskSegment? Base, MemorySegment[] Deltas);
}
