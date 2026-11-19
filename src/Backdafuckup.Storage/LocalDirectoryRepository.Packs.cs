using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage;

/// <summary>Format 2: packs, their GC, reading in storage order and the conversion of format 1 repositories.</summary>
public sealed partial class LocalDirectoryRepository : IChunkLocator
{
    private readonly ConditionalWeakTable<ChunkReadPlan, PlanSources> _planSources = [];
    private IReadOnlyList<string> _openReport = [];

    /// <summary>The repository's format: 1 (one file per chunk) or 2 (packs).</summary>
    public int FormatVersion => _descriptor.FormatVersion;

    /// <summary>Size at which a pack is sealed (format 2), else 0.</summary>
    public long PackTargetSize => _descriptor.Packs?.TargetSize ?? 0;

    /// <summary>What opening the repository found and repaired (format 2), for the server's log.</summary>
    public IReadOnlyList<string> OpenReport => _openReport;

    /// <summary>GC repacks a pack once at least this share of its bytes is unreferenced.</summary>
    public int RepackUnusedPercent { get; set; } = 20;

    /// <summary>GC copies at most this many referenced bytes out of packs per run (packs without references are always deleted).</summary>
    public long RepackMaxBytesPerRun { get; set; } = 256L * 1024 * 1024 * 1024;

    /// <summary>True while loose chunk files (format 1, or not yet packed) may exist.</summary>
    public bool LooseChunksMayExist => _packs is null || _looseMayExist;

    /// <summary>The packs and their index (format 2), for tests.</summary>
    internal PackStore? Packs => _packs;

    /// <summary>Distinct chunks stored (packed and loose); reads the whole index and lists every loose file.</summary>
    public long CountStoredChunks()
    {
        ThrowIfDisposed();
        var count = _packs?.CountDistinctChunks() ?? 0;
        if (LooseChunksMayExist && Directory.Exists(_chunksDirectory))
        {
            count += Directory.EnumerateFiles(_chunksDirectory, "*", SearchOption.AllDirectories)
                .Count(f => Path.GetFileName(f) is { Length: ChunkId.Size * 2 } name && IsHex(name) && (_packs is null || !_packs.TryLocate(ChunkId.Parse(name), out _)));
        }

        return count;
    }

    /// <summary>Numbers about the packs and their index (format 2), else null.</summary>
    public RepositoryPackStats? GetPackStats()
    {
        if (_packs is not { } packs)
        {
            return null;
        }

        long count = 0, bytes = 0;
        foreach (var pack in packs.Packs)
        {
            if (pack.State is PackState.Sealed or PackState.Retiring)
            {
                count++;
                bytes += pack.Length;
            }
        }

        return new RepositoryPackStats(count, bytes, packs.IndexEntries, packs.IndexMemoryBytes, LooseChunksMayExist, _lastUnusedBytes);
    }

    // ---- Conversion of format 1 repositories ----

    /// <summary>
    /// Switches a format 1 repository to format 2: <c>repo.json</c> is rewritten (older versions then refuse to open
    /// it), and new chunks go into packs from now on. Loose chunks stay readable until
    /// <see cref="PackLooseChunksAsync"/> has packed them.
    /// </summary>
    public async Task UpgradeToPacksAsync(long packTargetSize, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        CheckPackTargetSize(packTargetSize, _descriptor.ChunkSize, allowLoose: false);
        await _gcMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_packs is not null)
            {
                return;
            }

            PackStore.CreateDirectories(Root);
            var descriptor = _descriptor with { FormatVersion = RepositoryDescriptor.PackFormatVersion, Packs = new RepositoryPacks(packTargetSize) };
            var store = new PackStore(Root, Info.RepositoryId, packTargetSize, _time);
            _openReport = await store.OpenAsync(_descriptor.ChunkSize, cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.SerializeToUtf8Bytes(descriptor, CoreJsonContext.Default.RepositoryDescriptor);
            await AtomicFile.WriteAsync(_tempDirectory, Path.Combine(Root, DescriptorFileName), json, overwrite: true, cancellationToken).ConfigureAwait(false);
            AtomicFile.SyncDirectory(Root);
            _descriptor = descriptor;
            _looseMayExist = true;
            _packs = store;
        }
        finally
        {
            _gcMutex.Release();
        }
    }

    /// <summary>
    /// Moves loose chunks that restore points or SQL backups reference into packs, newest objects first, so that the
    /// chunks of one restore point lie together. A loose file is deleted only once its packed copy is durable.
    /// Stops after <paramref name="maxDuration"/>; calling it again continues where it stopped.
    /// </summary>
    public async Task<PackLooseResult> PackLooseChunksAsync(TimeSpan maxDuration, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var packs = _packs ?? throw new InvalidOperationException($"Repository '{Root}' has format version 1; convert it to pack files first.");
        await _gcMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        var appender = packs.CreateAppender();
        var waiting = new Dictionary<PackFile, List<string>>();
        long packed = 0, removedDuplicates = 0, kept = 0;
        var finished = true;
        try
        {
            var deadline = _time.GetUtcNow() + maxDuration;
            var buffer = new byte[ChunkCodec.HeaderSize + _descriptor.ChunkSize + 1024];
            foreach (var (what, ids) in ObjectsNewestFirst(cancellationToken))
            {
                await foreach (var id in ids.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (_time.GetUtcNow() >= deadline)
                    {
                        finished = false;
                        break;
                    }

                    var loose = ChunkPath(id.ToHex());
                    OpenPack? full = null;
                    lock (_stripes[id.Stripe(StripeCount)])
                    {
                        if (packs.TryLocateDurable(id, out _))
                        {
                            // Chunks that several restore points share come by many times: count each file once.
                            if (File.Exists(loose) && TryDeleteFile(loose))
                            {
                                removedDuplicates++;
                            }

                            continue;
                        }

                        if (packs.TryLocate(id, out _) || !File.Exists(loose))
                        {
                            continue; // In an open pack (sealed soon), or not loose at all.
                        }

                        int length;
                        try
                        {
                            using var handle = File.OpenHandle(loose, FileMode.Open, FileAccess.Read, FileShare.Read);
                            var fileLength = RandomAccess.GetLength(handle);
                            if (fileLength > buffer.Length)
                            {
                                throw new ChunkCorruptException(id, $"file too large (file '{loose}')");
                            }

                            length = (int)fileLength;
                            PackFormat.ReadExactly(handle, buffer.AsSpan(0, length), 0, loose);
                            var (_, raw, payload) = ChunkCodec.ReadHeader(id, buffer);
                            if (length != ChunkCodec.HeaderSize + payload)
                            {
                                throw new ChunkCorruptException(id, $"file length {length} does not match its header (file '{loose}')");
                            }

                            var location = appender.Append(id, buffer.AsMemory(0, length), (uint)raw, out full);
                            packs.AddLive(id, location);
                            if (!waiting.TryGetValue(location.Pack, out var files))
                            {
                                waiting[location.Pack] = files = [];
                            }

                            files.Add(loose);
                            packed++;
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException)
                        {
                            // Damaged or unreadable: left in place, verify reports it.
                            kept++;
                            progress?.Report($"{what}: chunk {id} stays loose: {ex.Message}");
                            continue;
                        }
                    }

                    if (full is not null)
                    {
                        await appender.SealAsync(full).ConfigureAwait(false);
                        DeleteLooseOfSealed(waiting);
                    }

                    if (packed % 10_000 == 1)
                    {
                        progress?.Report($"Packed {packed} loose chunk(s) of '{Root}' ({removedDuplicates} already packed copies removed)");
                    }
                }

                if (!finished)
                {
                    break;
                }
            }
        }
        finally
        {
            await appender.FlushAsync().ConfigureAwait(false);
            await packs.PersistAsync(CancellationToken.None).ConfigureAwait(false);
            DeleteLooseOfSealed(waiting);
            _gcMutex.Release();
        }

        var remaining = Directory.Exists(_chunksDirectory) && Directory.EnumerateFiles(_chunksDirectory, "*", SearchOption.AllDirectories).Any();
        if (!remaining)
        {
            _looseMayExist = false;
        }

        return new PackLooseResult(packed, removedDuplicates, kept, finished, remaining);
    }

    // ---- Reading in storage order ----

    public ChunkReadPlan PlanRead(IEnumerable<ChunkId> ids)
    {
        ThrowIfDisposed();
        var seen = new HashSet<ChunkId>();
        var packed = new List<(PackLocation Location, ChunkId Id)>();
        var loose = new List<(ChunkId Id, string Path, int Stored, int Raw)>();
        var missing = new List<ChunkId>();
        var header = new byte[ChunkCodec.HeaderSize];
        foreach (var id in ids)
        {
            if (id.IsZero || !seen.Add(id))
            {
                continue;
            }

            if (_packs is { } packs && packs.TryLocate(id, out var location))
            {
                packed.Add((location, id));
                continue;
            }

            var path = ChunkPath(id.ToHex());
            if (LooseChunksMayExist && File.Exists(path))
            {
                try
                {
                    using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    PackFormat.ReadExactly(handle, header, 0, path);
                    var (_, raw, payload) = ChunkCodec.ReadHeader(id, header);
                    loose.Add((id, path, ChunkCodec.HeaderSize + payload, raw));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    // Listed as missing; reading it would fail the same way.
                }
            }

            missing.Add(id);
        }

        // Packs in the order they were sealed (roughly their order on disk), records by offset.
        var packOrder = packed.Select(p => p.Location.Pack).Distinct().OrderBy(p => p.SealedUtc).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        var ordinal = packOrder.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var chunks = new List<PlannedChunk>(packed.Count + loose.Count);
        foreach (var (location, id) in packed.OrderBy(p => ordinal[p.Location.Pack]).ThenBy(p => p.Location.Offset))
        {
            chunks.Add(new PlannedChunk(id, ordinal[location.Pack], location.Offset, (int)location.StoredLength, (int)location.RawLength));
        }

        var sources = packOrder.Select(p => p.Path).ToList();
        foreach (var (id, path, stored, raw) in loose)
        {
            chunks.Add(new PlannedChunk(id, sources.Count, 0, stored, raw));
            sources.Add(path);
        }

        var plan = new ChunkReadPlan(chunks, sources, missing);
        _planSources.AddOrUpdate(plan, new PlanSources([.. packOrder]));
        return plan;
    }

    public async IAsyncEnumerable<StoredChunk> ReadStoredAsync(ChunkReadPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_planSources.TryGetValue(plan, out var sources))
        {
            throw new ArgumentException("The plan was not made by this repository.", nameof(plan));
        }

        const int maxRead = 16 * 1024 * 1024;
        const int maxGap = 1024 * 1024;
        var runs = new List<(int Start, int End)>();
        for (var i = 0; i < plan.Chunks.Count;)
        {
            var start = i;
            var first = plan.Chunks[i];
            var end = first.Offset + first.StoredLength;
            i++;
            if (first.Source < sources.Packs.Length)
            {
                while (i < plan.Chunks.Count)
                {
                    var next = plan.Chunks[i];
                    if (next.Source != first.Source || next.Offset - end > maxGap || next.Offset + next.StoredLength - first.Offset > maxRead)
                    {
                        break;
                    }

                    end = next.Offset + next.StoredLength;
                    i++;
                }
            }

            runs.Add((start, i));
        }

        // Read one run ahead while the caller handles the current one.
        Task<byte[]>? pending = runs.Count > 0 ? ReadRunAsync(plan, sources, runs[0], cancellationToken) : null;
        for (var r = 0; r < runs.Count; r++)
        {
            var buffer = await pending!.ConfigureAwait(false);
            pending = r + 1 < runs.Count ? ReadRunAsync(plan, sources, runs[r + 1], cancellationToken) : null;
            var (start, end) = runs[r];
            var origin = plan.Chunks[start].Offset;
            for (var c = start; c < end; c++)
            {
                var chunk = plan.Chunks[c];
                yield return new StoredChunk(chunk.Id, buffer.AsMemory((int)(chunk.Offset - origin), chunk.StoredLength), chunk.RawLength);
            }
        }
    }

    // ---- GC of packs ----

    private async Task<PackGcResult> CollectPacksAsync(PackStore packs, ChunkMarkSet marked, DateTime cutoff, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        // Referenced bytes per pack, from the index (records whose id was dropped as a duplicate count as unreferenced).
        var live = new Dictionary<PackFile, long>();
        long scanned = 0;
        foreach (var (id, location) in packs.AllEntries())
        {
            if ((++scanned & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (location.Pack.State != PackState.Sealed || (!marked.Contains(id) && !IsProtected(id)))
            {
                continue;
            }

            live[location.Pack] = live.GetValueOrDefault(location.Pack) + PackFormat.RecordIdSize + location.StoredLength + PackFormat.TrailerEntrySize;
        }

        var fixedBytes = PackFormat.HeaderSize + PackFormat.FooterSize;
        var empty = new List<PackFile>();
        var sparse = new List<(PackFile Pack, long Live)>();
        var small = new List<(PackFile Pack, long Live)>();
        long unused = 0;
        foreach (var pack in packs.Packs.Where(p => p.State == PackState.Sealed).ToList())
        {
            var used = live.TryGetValue(pack, out var bytes) ? bytes + fixedBytes : 0;
            var dead = Math.Max(0, pack.Length - used);
            if (pack.SealedUtc > cutoff)
            {
                unused += dead;
            }
            else if (used == 0)
            {
                empty.Add(pack);
            }
            else if (dead * 100 >= (long)RepackUnusedPercent * pack.Length && dead > 0)
            {
                sparse.Add((pack, used));
            }
            else if (pack.Length < packs.TargetSize / 4)
            {
                small.Add((pack, used));
            }
            else
            {
                unused += dead;
            }
        }

        // Tiny packs (e.g. of frequent SQL log backups) are combined, but only when there are at least two of them.
        if (small.Count >= 2)
        {
            sparse.AddRange(small);
        }
        else
        {
            unused += small.Sum(s => Math.Max(0, s.Pack.Length - s.Live));
        }

        var candidates = new List<PackFile>(empty);
        long budget = RepackMaxBytesPerRun;
        foreach (var (pack, used) in sparse.OrderByDescending(s => s.Pack.Length - s.Live))
        {
            if (used > budget)
            {
                unused += Math.Max(0, pack.Length - used);
                continue;
            }

            budget -= used;
            candidates.Add(pack);
        }

        if (candidates.Count == 0)
        {
            _lastUnusedBytes = unused;
            await packs.MergeAsync(cancellationToken).ConfigureAwait(false);
            return new PackGcResult(scanned, 0, 0, 0, 0, unused);
        }

        progress?.Report($"Repacking {candidates.Count} pack(s) of '{packs.PacksDirectory}' ({empty.Count} without referenced chunks)");
        foreach (var pack in candidates)
        {
            pack.State = PackState.Retiring;
        }

        long dropped = 0, repacked = 0, freed = 0, deletedPacks = 0;
        var appender = packs.CreateAppender();
        var buffer = new byte[ChunkCodec.HeaderSize + _descriptor.ChunkSize + 1024];
        var copied = false;
        try
        {
            foreach (var pack in candidates)
            {
                PackTrailerEntry[] entries;
                try
                {
                    entries = packs.ReadTrailer(pack);
                }
                catch (InvalidDataException ex)
                {
                    progress?.Report($"{ex.Message} The pack is kept.");
                    pack.State = PackState.Sealed;
                    continue;
                }

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OpenPack? full = null;
                    lock (_stripes[entry.Id.Stripe(StripeCount)])
                    {
                        if (!marked.Contains(entry.Id) && !IsProtected(entry.Id))
                        {
                            dropped++;
                            continue;
                        }

                        if (packs.HasCopyOutside(entry.Id, pack))
                        {
                            continue;
                        }

                        var stored = packs.ReadStored(new PackLocation(pack, entry.Offset, entry.StoredLength, entry.RawLength), buffer);
                        var location = appender.Append(entry.Id, buffer.AsMemory(0, stored), entry.RawLength, out full);
                        packs.AddLive(entry.Id, location);
                        repacked += stored;
                        copied = true;
                    }

                    if (full is not null)
                    {
                        await appender.SealAsync(full).ConfigureAwait(false);
                    }
                }
            }

            // The copies are durable and indexed before a single old pack goes.
            await appender.FlushAsync().ConfigureAwait(false);
            await packs.PersistAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pack in candidates.Where(p => p.State == PackState.Retiring))
            {
                freed += packs.DeletePack(pack);
                deletedPacks++;
            }
        }
        catch
        {
            if (copied)
            {
                await appender.FlushAsync().ConfigureAwait(false);
            }

            foreach (var pack in candidates.Where(p => p.State == PackState.Retiring))
            {
                pack.State = PackState.Sealed;
            }

            throw;
        }

        await packs.MergeAsync(cancellationToken).ConfigureAwait(false);
        _lastUnusedBytes = unused;
        progress?.Report($"Deleted {deletedPacks} pack(s), copied {repacked} bytes of referenced chunks into new packs.");
        return new PackGcResult(scanned, dropped, Math.Max(0, freed - repacked), deletedPacks, repacked, unused);
    }

    private long _lastUnusedBytes;

    private async Task OpenPacksAsync(CancellationToken cancellationToken)
    {
        var store = new PackStore(Root, Info.RepositoryId, _descriptor.Packs!.TargetSize, _time);
        try
        {
            _openReport = await store.OpenAsync(_descriptor.ChunkSize, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            store.Dispose();
            throw;
        }

        _looseMayExist = Directory.Exists(_chunksDirectory) && Directory.EnumerateFiles(_chunksDirectory, "*", SearchOption.AllDirectories).Any();
        _packs = store;
    }

    private void DeleteLooseOfSealed(Dictionary<PackFile, List<string>> waiting)
    {
        foreach (var pack in waiting.Keys.Where(p => p.State == PackState.Sealed).ToList())
        {
            foreach (var file in waiting[pack])
            {
                TryDeleteFile(file);
            }

            waiting.Remove(pack);
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // In use; the next round removes it.
        }
    }

    /// <summary>Restore points and SQL backups, newest first, each with the chunks it references.</summary>
    private IEnumerable<(string What, IAsyncEnumerable<ChunkId> Ids)> ObjectsNewestFirst(CancellationToken cancellationToken)
    {
        foreach (var (id, directory) in _restorePoints.ToArray().OrderByDescending(r => Path.GetFileName(r.Value), StringComparer.Ordinal))
        {
            yield return ($"Restore point {id}", RestorePointIdsAsync(directory, cancellationToken));
        }

        foreach (var (id, directory) in _sqlBackups.ToArray().OrderByDescending(r => Path.GetFileName(r.Value), StringComparer.Ordinal))
        {
            yield return ($"SQL backup {id}", SqlBackupIdsAsync(directory, cancellationToken));
        }
    }

    private async IAsyncEnumerable<ChunkId> RestorePointIdsAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifest = await TryReadManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            yield break;
        }

        await using var reader = new RestorePointReader(this, directory, manifest);
        foreach (var disk in manifest.AllImages)
        {
            foreach (var id in (await reader.ReadDiskIndexAsync(disk.Key, cancellationToken).ConfigureAwait(false)).Entries)
            {
                if (!id.IsZero)
                {
                    yield return id;
                }
            }
        }

        foreach (var tree in manifest.FileTrees)
        {
            var root = await reader.ReadFileTreeIndexAsync(tree.Key, cancellationToken).ConfigureAwait(false);
            var ids = new List<ChunkId>();
            FileTreeWalker.Visit(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), Chunks, _descriptor.ChunkSize, ids.Add, new HashSet<ChunkId>());
            foreach (var id in ids)
            {
                yield return id;
            }
        }
    }

    private async IAsyncEnumerable<ChunkId> SqlBackupIdsAsync(string directory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var manifest = await TryReadSqlBackupManifestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            yield break;
        }

        foreach (var file in manifest.Files)
        {
            foreach (var id in (await ReadSqlBackupIndexAsync(directory, manifest, file, cancellationToken).ConfigureAwait(false)).Entries)
            {
                if (!id.IsZero)
                {
                    yield return id;
                }
            }
        }
    }

    private async Task<byte[]> ReadRunAsync(ChunkReadPlan plan, PlanSources sources, (int Start, int End) run, CancellationToken cancellationToken)
    {
        var first = plan.Chunks[run.Start];
        var last = plan.Chunks[run.End - 1];
        var length = (int)(last.Offset + last.StoredLength - first.Offset);
        var buffer = new byte[length];
        if (first.Source < sources.Packs.Length)
        {
            var pack = sources.Packs[first.Source];
            if (pack.State == PackState.Dead && _packs is { } packs)
            {
                // Repacked since the plan was made: read each chunk where it lies now.
                foreach (var chunk in plan.Chunks.Skip(run.Start).Take(run.End - run.Start))
                {
                    if (!packs.TryLocate(chunk.Id, out var moved) || moved.StoredLength != chunk.StoredLength)
                    {
                        throw new ChunkMissingException(chunk.Id, Root);
                    }

                    await packs.ReadStoredAsync(moved, buffer.AsMemory((int)(chunk.Offset - first.Offset), chunk.StoredLength), cancellationToken).ConfigureAwait(false);
                }

                return buffer;
            }

            await _packs!.ReadRangeAsync(pack, first.Offset, buffer, cancellationToken).ConfigureAwait(false);
            return buffer;
        }

        var path = plan.Sources[first.Source];
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);
            var read = 0;
            while (read < length)
            {
                var n = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), read, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new ChunkCorruptException(first.Id, $"truncated (file '{path}')");
                }

                read += n;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Packed meanwhile.
            if (_packs is { } packs && packs.TryLocate(first.Id, out var moved) && moved.StoredLength == length)
            {
                await packs.ReadStoredAsync(moved, buffer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new ChunkMissingException(first.Id, Root);
            }
        }

        return buffer;
    }

    private static void CheckPackTargetSize(long packTargetSize, int chunkSize, bool allowLoose)
    {
        if (allowLoose && packTargetSize == 0)
        {
            return;
        }

        if (packTargetSize < 4L * (chunkSize + ChunkCodec.HeaderSize + PackFormat.RecordIdSize + PackFormat.TrailerEntrySize) || packTargetSize > PackFormat.MaxPackSize)
        {
            throw new ArgumentOutOfRangeException(nameof(packTargetSize), $"The pack size must hold at least four chunks and be at most {PackFormat.MaxPackSize / (1024 * 1024)} MiB.");
        }
    }

    private sealed record PlanSources(PackFile[] Packs);

    private readonly record struct PackGcResult(long Scanned, long Dropped, long Freed, long PacksDeleted, long BytesRepacked, long UnusedBytes);
}

/// <param name="Packs">Sealed packs.</param>
/// <param name="PackBytes">Their total size.</param>
/// <param name="IndexEntries">Chunks in the index.</param>
/// <param name="IndexMemoryBytes">Memory the index uses (approximate).</param>
/// <param name="LooseChunksMayExist">Whether loose chunk files may still exist (conversion not finished).</param>
/// <param name="UnusedBytes">Unreferenced bytes left in packs after the last GC.</param>
public sealed record RepositoryPackStats(long Packs, long PackBytes, long IndexEntries, long IndexMemoryBytes, bool LooseChunksMayExist, long UnusedBytes);

/// <param name="Packed">Loose chunks moved into packs.</param>
/// <param name="RemovedDuplicates">Loose files removed because a packed copy already existed.</param>
/// <param name="KeptDamaged">Loose files left in place because they could not be read or were damaged.</param>
/// <param name="Finished">False when the time limit stopped it.</param>
/// <param name="LooseChunksRemain">Whether loose chunk files remain (unreferenced ones are removed by GC).</param>
public sealed record PackLooseResult(long Packed, long RemovedDuplicates, long KeptDamaged, bool Finished, bool LooseChunksRemain);
