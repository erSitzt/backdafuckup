using System.Security.Cryptography;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Format;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Tape;

/// <summary>What a tape writer needs from its session.</summary>
internal sealed record TapeWriteContext(
    TapeLibraryEntity Library,
    TapeDriveEntity Drive,
    MediaPoolEntity Pool,
    MediaSetEntity Set,
    byte[]? Kek,
    TapeKdfParameters? Kdf,
    string? Fingerprint,
    OpenSetIndex Index,
    string Holder,
    Guid SessionId,
    IOperationLog Log);

/// <param name="Copied">Restore points copied.</param>
/// <param name="NewBytes">Bytes of chunks written (the set had the others already).</param>
internal sealed record TapeWriteResult(int Copied, long NewBytes, long ReusedChunks, IReadOnlyList<string> Tapes);

/// <summary>
/// Writes one run's copies into a media set. It picks tapes (the set's last one if it has room, then expired tapes of the
/// pool, then free and blank ones; never foreign data), labels new ones, appends archive files with the chunks the set
/// lacks and a restore-point file per copy, and changes the tape when one is full. A copy is committed with a
/// synchronizing filemark before the database records it; a file cut off by the end of the tape is written again on
/// the next one.
/// </summary>
internal sealed class TapeSetWriter(
    TapeWriteContext context,
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeSessionRunner robot,
    TapeOptions options,
    TimeProvider time) : IAsyncDisposable
{
    private readonly List<TapeFileEntity> _files = [];
    private readonly HashSet<Guid> _skipped = [];
    private readonly List<string> _tapesUsed = [];
    private OpenedDrive? _drive;
    private TapeMediumEntity? _tape;
    private long _remaining;
    private int _fileNumber;
    private AesGcmRecordCipher? _cipher;
    private int _copied;
    private long _newBytes;
    private long _reused;

    private IOperationLog Log => context.Log;

    private MediaSetEntity Set => context.Set;

    /// <summary>Room for files on the current tape, keeping the reserve before its end.</summary>
    private long Available => _remaining - Reserve(_tape?.CapacityBytes);

    public async Task CopyRestorePointAsync(RestorePointEntity point, IBackupRepository repository, CancellationToken cancellationToken)
    {
        if (repository is not IRepositoryTransfer transfer || repository is not IChunkLocator locator)
        {
            throw new InvalidOperationException($"The repository of {point.VmName} cannot copy restore points to tape.");
        }

        using var pin = repository.Pin(point.Id);
        var files = await transfer.ReadRestorePointFilesAsync(point.Id, cancellationToken).ConfigureAwait(false);
        var ids = await transfer.ListRestorePointChunksAsync(point.Id, cancellationToken).ConfigureAwait(false);
        var locations = new Dictionary<ChunkId, ChunkLocation>(ids.Count);
        var queue = new List<ChunkId>();
        foreach (var id in ids)
        {
            if (context.Index.TryFind(id, out var known))
            {
                locations[id] = known;
            }
            else
            {
                queue.Add(id);
            }
        }

        var reused = ids.Count - queue.Count;
        long newBytes = 0;
        while (queue.Count > 0)
        {
            await EnsureTapeAsync(cancellationToken).ConfigureAwait(false);
            if (Available < 4L * context.Pool.BlockSize)
            {
                await ChangeTapeAsync($"{_tape!.Barcode} has no room left", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var (file, start, number) = await BeginFileAsync(TapeFileTypes.Archive, null, cancellationToken).ConfigureAwait(false);
            var written = new List<ChunkLocation>();
            var stopped = false;
            try
            {
                var limit = Available - (2L * context.Pool.BlockSize);
                await foreach (var chunk in locator.ReadStoredAsync(locator.PlanRead(queue), cancellationToken).ConfigureAwait(false))
                {
                    var body = TapeEntries.EncodeChunk(chunk.Id, chunk.Record.Span);
                    var length = TapeFormat.RecordHeaderSize + body.Length + (_cipher?.Overhead ?? 0);
                    if (file.EarlyWarning || file.BytesWritten + length >= limit)
                    {
                        stopped = true;
                        break;
                    }

                    var position = await file.WriteRecordAsync(TapeFormat.Chunk, body, encrypt: true, cancellationToken).ConfigureAwait(false);
                    written.Add(new ChunkLocation(chunk.Id, _tape!.Sequence!.Value, number, position.Block, position.Offset, length));
                }

                await file.WriteRecordAsync(TapeFormat.ArchiveIndex, ChunkLocations.Encode(written), encrypt: true, cancellationToken).ConfigureAwait(false);
                var summary = await file.FinishAsync(synchronize: false, cancellationToken).ConfigureAwait(false);
                Finished(summary, TapeFileTypes.Archive, start, number, null);
                foreach (var location in written)
                {
                    context.Index.Add(location);
                    locations[location.Id] = location;
                    newBytes += location.Length;
                }

                var done = written.Select(l => l.Id).ToHashSet();
                queue.RemoveAll(done.Contains);
                if (stopped || file.EarlyWarning)
                {
                    await ChangeTapeAsync(file.EarlyWarning ? $"{_tape!.Barcode} reached its early-warning zone" : $"{_tape!.Barcode} is nearly full", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (TapeFullException ex)
            {
                await TapeFullAsync($"{ex.Message} The archive file {number} is cut off and is written again on the next tape.", cancellationToken).ConfigureAwait(false);
            }
        }

        // The restore-point file: its files, then where every chunk is. Built first, started only where it fits whole.
        var copyId = Guid.CreateVersion7();
        var manifest = files[0].Content;
        var info = new TapeCopyInfo(copyId, point.Id, point.RepositoryId, point.JobName, point.VmNativeId, point.VmName, point.CreatedUtc, Set.Id, context.Pool.Name);
        var records = new List<(string Type, byte[] Body)> { (TapeFormat.Entry, TapeEntries.Encode(TapeCopyInfo.FileName, TapeJson.Serialize(info))) };
        records.AddRange(files.Select(f => (TapeFormat.Entry, TapeEntries.Encode(f.Name, f.Content))));
        records.Add((TapeFormat.Locations, ChunkLocations.Encode(ids.Select(id => locations[id]))));
        var size = records.Sum(r => (long)TapeFormat.RecordHeaderSize + r.Body.Length + (_cipher?.Overhead ?? 0)) + (8L * context.Pool.BlockSize);
        TapeFileEntity? restorePointFile = null;
        for (var attempt = 0; restorePointFile is null; attempt++)
        {
            if (attempt > 3)
            {
                throw new IOException($"The restore-point file of {point.VmName} ({Format(size)}) did not fit on {attempt} tapes.");
            }

            await EnsureTapeAsync(cancellationToken).ConfigureAwait(false);
            if (Available < size)
            {
                await ChangeTapeAsync($"the restore-point file of {point.VmName} ({Format(size)}) does not fit on {_tape!.Barcode} ({Format(Available)} left)", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var (file, start, number) = await BeginFileAsync(TapeFileTypes.RestorePoint, copyId, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var (type, body) in records)
                {
                    await file.WriteRecordAsync(type, body, encrypt: true, cancellationToken).ConfigureAwait(false);
                }

                var summary = await file.FinishAsync(synchronize: true, cancellationToken).ConfigureAwait(false);
                restorePointFile = Finished(summary, TapeFileTypes.RestorePoint, start, number, copyId);
            }
            catch (TapeFullException ex)
            {
                await TapeFullAsync($"{ex.Message} The restore-point file of {point.VmName} is cut off and is written again on the next tape.", cancellationToken).ConfigureAwait(false);
            }
        }

        // Committed on tape: now the database, and the set's chunk index.
        var needed = locations.Values.Select(l => l.Sequence).Append(restorePointFile.Sequence).Distinct().Order().ToList();
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.TapeCopies.Add(new TapeCopyEntity
            {
                Id = copyId,
                SetId = Set.Id,
                PoolId = context.Pool.Id,
                Kind = TapeFileTypes.RestorePoint,
                OriginalId = point.Id,
                RepositoryId = point.RepositoryId,
                JobName = point.JobName,
                VmId = point.VmNativeId,
                VmName = point.VmName,
                ObjectKind = point.ObjectKind,
                CreatedUtc = point.CreatedUtc,
                CopiedUtc = time.GetUtcNow(),
                SessionId = context.SessionId,
                Sequence = restorePointFile.Sequence,
                FileNumber = restorePointFile.FileNumber,
                FileStartBlock = restorePointFile.StartBlock,
                FileId = restorePointFile.FileId,
                NeededSequences = needed,
                StoredBytes = locations.Values.Sum(l => (long)l.Length),
                Manifest = System.Text.Encoding.UTF8.GetString(manifest),
            });
            db.TapeFiles.AddRange(_files);
            _files.Clear();
            await SaveTapeAsync(db, cancellationToken).ConfigureAwait(false);
            var set = await db.MediaSets.FirstAsync(s => s.Id == Set.Id, cancellationToken).ConfigureAwait(false);
            set.IndexSegments = [.. context.Index.CommitPending()];
            set.Bytes += newBytes;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _copied++;
        _newBytes += newBytes;
        _reused += reused;
        Log.Info($"{point.VmName} (restore point of {point.CreatedUtc:yyyy-MM-dd HH:mm} UTC) is on tape: {Format(newBytes)} of new chunks, {reused} chunk(s) the set had already; tapes {string.Join(", ", needed.Select(s => $"#{s}"))}.");
    }

    /// <summary>The catalog on the current tape, then the tape back into its slot.</summary>
    public async Task<TapeWriteResult> FinishAsync(CancellationToken cancellationToken)
    {
        if (_tape is not null)
        {
            await WriteCatalogAsync(cancellationToken).ConfigureAwait(false);
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TapeWriteResult(_copied, _newBytes, _reused, _tapesUsed);
    }

    public async ValueTask DisposeAsync()
    {
        _cipher?.Dispose();
        if (_drive is not null)
        {
            await _drive.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Kept free before the end of a tape: files are only started where they fit, even without early warning.</summary>
    internal long Reserve(long? capacity) => Math.Max(options.ReserveMiB * 1024L * 1024, (capacity ?? 0) / 400);

    private static string Format(long bytes) => VmBackupTask.Format(bytes);

    private async Task<(TapeFileWriter Writer, long StartBlock, int FileNumber)> BeginFileAsync(string type, Guid? copyId, CancellationToken cancellationToken)
    {
        var position = await _drive!.Drive.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
        var header = new TapeFileHeader
        {
            Type = type, FileId = NewFileId(), SetId = Set.Id, Sequence = _tape!.Sequence!.Value, FileNumber = _fileNumber, CreatedUtc = time.GetUtcNow(), CopyId = copyId,
        };
        var writer = await TapeFileWriter.BeginAsync(new DriveBlocks(_drive.Drive), context.Pool.BlockSize, header, _cipher, cancellationToken).ConfigureAwait(false);
        return (writer, position.Block, _fileNumber);
    }

    private TapeFileEntity Finished(TapeFileSummary summary, string type, long startBlock, int fileNumber, Guid? copyId)
    {
        _remaining -= summary.Bytes;
        _fileNumber = fileNumber + 1;
        _tape!.FileCount = _fileNumber;
        _tape.BytesWritten += summary.Bytes;
        _tape.LastWrittenUtc = time.GetUtcNow();
        var file = new TapeFileEntity
        {
            SetId = Set.Id, Sequence = _tape.Sequence!.Value, FileNumber = fileNumber, StartBlock = startBlock, FileId = summary.FileId, Type = type, Blocks = summary.Blocks, Bytes = summary.Bytes, CopyId = copyId,
        };
        _files.Add(file);
        return file;
    }

    /// <summary>A tape in the drive that this run can write to: the set's last one, or a new one labelled for the set.</summary>
    private async Task EnsureTapeAsync(CancellationToken cancellationToken)
    {
        _cipher ??= context.Pool.Encryption == TapeEncryption.Software
            ? new AesGcmRecordCipher(TapeKeys.SetKey(context.Kek ?? throw new InvalidOperationException($"Pool {context.Pool.Name} encrypts, but has no key."), Set.Id, TapeKeys.SoftwarePurpose), Set.Id)
            : null;
        while (_tape is null)
        {
            var (candidate, appending) = await ChooseAsync(cancellationToken).ConfigureAwait(false);
            var barcode = candidate.Barcode;
            var (_, medium) = await robot.LoadHeldAsync(context.Library, context.Drive, barcode, context.Holder, Log, cancellationToken).ConfigureAwait(false);
            _drive = await libraries.OpenDriveAsync(context.Library, context.Drive, cancellationToken).ConfigureAwait(false);
            await _drive.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
            var label = await ReadLabelAsync(cancellationToken).ConfigureAwait(false);
            if (label is { Foreign: true })
            {
                Log.Warning($"{barcode} holds data that is not backdafuckup's; it is never written to.");
                await MarkAsync(candidate.Id, TapeMediumState.Foreign, cancellationToken).ConfigureAwait(false);
                await UnloadAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (appending)
            {
                if (label?.Label?.Set is not { } set || set.Id != Set.Id || set.Sequence != candidate.Sequence)
                {
                    Log.Warning($"{barcode} should be tape #{candidate.Sequence} of the set, but its label says otherwise; it is not written to.");
                    await MarkAsync(candidate.Id, TapeMediumState.Foreign, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await _drive.Drive.SpaceToEndOfDataAsync(cancellationToken).ConfigureAwait(false);
                var position = await _drive.Drive.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
                if (position.File is { } files && files != candidate.FileCount)
                {
                    Log.Info($"{barcode} has {files} complete file(s), the catalog knows {candidate.FileCount}: files of a run that did not finish are left in place and ignored.");
                    candidate.FileCount = (int)files;
                }

                _remaining = medium.RemainingBytes ?? ((medium.CapacityBytes ?? candidate.CapacityBytes ?? 0) - candidate.BytesWritten);
            }
            else
            {
                if (label?.Label is { } old && old.Set is not null && !(candidate.State == TapeMediumState.Expired && old.Set.PoolId == context.Pool.Id))
                {
                    Log.Warning($"{barcode} belongs to set {old.Set.Id} of pool {old.Set.PoolName} that has not expired; it is not written to.");
                    await MarkAsync(candidate.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (medium.Worm && label is not null)
                {
                    Log.Warning($"{barcode} is a WORM tape with data on it; it cannot be labelled again.");
                    await MarkAsync(candidate.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await LabelAsync(candidate, medium, cancellationToken).ConfigureAwait(false);
            }

            if (context.Pool.Encryption == TapeEncryption.Hardware)
            {
                var capabilities = await _drive.Drive.ReadEncryptionCapabilitiesAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException($"{context.Drive.Name} cannot encrypt, but pool {context.Pool.Name} asks the drive to.");
                await _drive.Drive.SetEncryptionAsync(capabilities, TapeKeys.SetKey(context.Kek!, Set.Id, TapeKeys.HardwarePurpose), TapeKeys.KeyAssociatedData(context.Fingerprint!, Set.Id), cancellationToken).ConfigureAwait(false);
            }

            _tape = candidate;
            _fileNumber = candidate.FileCount;
            _tapesUsed.Add(barcode);
            Log.Info($"Writing to {barcode} (tape #{candidate.Sequence} of the set) in {context.Drive.Name}: {Format(Math.Max(0, Available))} available.");
        }
    }

    /// <summary>The set's last tape if it can take more, else a new one: expired tapes of the pool, free, blank, unread.</summary>
    private async Task<(TapeMediumEntity Medium, bool Appending)> ChooseAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var last = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == Set.Id).OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (last is { State: TapeMediumState.Appendable } && last.LibraryId == context.Library.Id && last.Location != TapeLocation.Offline && !_skipped.Contains(last.Id))
        {
            _skipped.Add(last.Id);
            return (last, true);
        }

        var kind = context.Pool.WormOnly ? TapeMediumKind.Worm : TapeMediumKind.Data;
        var usable = new[] { TapeMediumState.Expired, TapeMediumState.Free, TapeMediumState.Blank, TapeMediumState.Unknown };
        var media = await db.TapeMedia.AsNoTracking()
            .Where(m => m.LibraryId == context.Library.Id && m.Location != TapeLocation.Offline && m.Kind == kind && usable.Contains(m.State))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var choice = media
            .Where(m => !_skipped.Contains(m.Id) && (m.State != TapeMediumState.Expired || m.PoolId == context.Pool.Id))
            .OrderBy(m => Array.IndexOf(usable, m.State))
            .ThenBy(m => m.Barcode, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new IOException(
                $"No tape in {context.Library.Name} can take tape #{Set.Media + 1} of the set: put in blank, free or expired {(context.Pool.WormOnly ? "WORM " : string.Empty)}cartridges (import them from the mail slots).");
        _skipped.Add(choice.Id);
        return (choice, false);
    }

    private async Task LabelAsync(TapeMediumEntity candidate, MediumInfo medium, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var set = await db.MediaSets.FirstAsync(s => s.Id == Set.Id, cancellationToken).ConfigureAwait(false);
        var sequence = set.Media + 1;
        var label = new TapeLabel
        {
            MediumId = Guid.CreateVersion7(),
            Barcode = candidate.Barcode,
            Name = candidate.Barcode,
            BlockSize = context.Pool.BlockSize,
            Worm = medium.Worm,
            CreatedUtc = time.GetUtcNow(),
            Server = Environment.MachineName,
            Set = new TapeLabelSet(Set.Id, sequence, context.Pool.Id, context.Pool.Name, Set.CreatedUtc),
            Encryption = context.Fingerprint is null ? null : TapeKeys.Describe(context.Pool.Encryption == TapeEncryption.Hardware ? "hardware" : "software", context.Fingerprint, context.Kdf!),
        };

        await _drive!.Drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        var header = new TapeFileHeader { Type = TapeFileTypes.Label, FileId = NewFileId(), SetId = Set.Id, Sequence = sequence, FileNumber = 0, CreatedUtc = label.CreatedUtc };
        var writer = await TapeFileWriter.BeginAsync(new DriveBlocks(_drive.Drive), context.Pool.BlockSize, header, null, cancellationToken).ConfigureAwait(false);
        await writer.WriteRecordAsync(TapeFormat.Label, TapeJson.Serialize(label), encrypt: false, cancellationToken).ConfigureAwait(false);
        var summary = await writer.FinishAsync(synchronize: true, cancellationToken).ConfigureAwait(false);

        var tracked = await db.TapeMedia.FirstAsync(m => m.Id == candidate.Id, cancellationToken).ConfigureAwait(false);
        tracked.State = TapeMediumState.Appendable;
        tracked.SetId = Set.Id;
        tracked.PoolId = context.Pool.Id;
        tracked.Sequence = sequence;
        tracked.MediumId = label.MediumId;
        tracked.LabelName = label.Name;
        tracked.FileCount = 1;
        tracked.BytesWritten = summary.Bytes;
        tracked.LabelledUtc = label.CreatedUtc;
        tracked.CapacityBytes = medium.CapacityBytes ?? tracked.CapacityBytes;
        set.Media = sequence;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Set.Media = sequence;

        candidate.State = TapeMediumState.Appendable;
        candidate.SetId = Set.Id;
        candidate.PoolId = context.Pool.Id;
        candidate.Sequence = sequence;
        candidate.FileCount = 1;
        candidate.BytesWritten = summary.Bytes;
        candidate.CapacityBytes = tracked.CapacityBytes;
        _remaining = (medium.RemainingBytes ?? medium.CapacityBytes ?? 0) - summary.Bytes;
        Log.Info($"{candidate.Barcode} labelled as tape #{sequence} of set {Set.Id} (pool {context.Pool.Name}{(medium.Worm ? ", WORM" : string.Empty)}).");
    }

    /// <summary>The label of the tape in the drive: null when blank, <see cref="LabelRead.Foreign"/> when it holds other data.</summary>
    private async Task<LabelRead?> ReadLabelAsync(CancellationToken cancellationToken)
    {
        var drive = _drive!.Drive;
        await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        var probe = new byte[Math.Max(context.Pool.BlockSize, 1024 * 1024)];
        var first = await drive.ReadBlockAsync(probe, cancellationToken).ConfigureAwait(false);
        if (first.Kind == ReadOutcomeKind.EndOfData)
        {
            return null;
        }

        await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await new TapeFileReader(new DriveBlocks(drive), probe.Length).ReadAllAsync(cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
            var label = records.FirstOrDefault(r => r.Type == TapeFormat.Label) is { } record ? TapeJson.Deserialize<TapeLabel>(record.Body) : null;
            return label is { Format: TapeLabel.FormatName } ? new LabelRead(label, false) : new LabelRead(null, true);
        }
        catch (Exception ex) when (ex is InvalidDataException or TornFileException or System.Text.Json.JsonException)
        {
            return new LabelRead(null, true);
        }
    }

    private async Task ChangeTapeAsync(string reason, CancellationToken cancellationToken)
    {
        Log.Info($"Changing the tape: {reason}.");
        try
        {
            await WriteCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TapeFullException)
        {
            Log.Warning($"{_tape!.Barcode} had no room for the set's catalog; the next tape carries it.");
        }

        await MarkAsync(_tape!.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
        await UnloadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TapeFullAsync(string message, CancellationToken cancellationToken)
    {
        Log.Warning(message);
        await MarkAsync(_tape!.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
        await UnloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The set's catalog as the database knows it after the last commit, as a file on the current tape.</summary>
    private async Task WriteCatalogAsync(CancellationToken cancellationToken)
    {
        TapeCatalog catalog;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var media = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == Set.Id).OrderBy(m => m.Sequence).ToListAsync(cancellationToken).ConfigureAwait(false);
            var files = await db.TapeFiles.AsNoTracking().Where(f => f.SetId == Set.Id).OrderBy(f => f.Sequence).ThenBy(f => f.FileNumber).ToListAsync(cancellationToken).ConfigureAwait(false);
            var copies = await db.TapeCopies.AsNoTracking().Where(c => c.SetId == Set.Id).OrderBy(c => c.CopiedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
            catalog = new TapeCatalog
            {
                SetId = Set.Id,
                PoolId = context.Pool.Id,
                PoolName = context.Pool.Name,
                SetCreatedUtc = Set.CreatedUtc,
                WrittenUtc = time.GetUtcNow(),
                Media = media.Select(m => new TapeCatalogMedium(m.Sequence!.Value, m.MediumId ?? Guid.Empty, m.LabelName ?? m.Barcode, m.Barcode)).ToList(),
                Files = files.Select(f => new TapeCatalogFile(f.Sequence, f.FileNumber, f.StartBlock, f.FileId, f.Type, f.Blocks, f.Bytes, f.CopyId)).ToList(),
                Copies = copies.Select(c => new TapeCatalogCopy(
                    c.Id, c.Kind, c.OriginalId, c.RepositoryId, c.VmId, c.VmName, c.JobName, c.CreatedUtc, c.Sequence, c.FileNumber, c.NeededSequences, c.StoredBytes, c.Manifest)).ToList(),
            };
        }

        var (file, start, number) = await BeginFileAsync(TapeFileTypes.Catalog, null, cancellationToken).ConfigureAwait(false);
        await file.WriteRecordAsync(TapeFormat.Catalog, TapeJson.SerializeCompressed(catalog), encrypt: true, cancellationToken).ConfigureAwait(false);
        var summary = await file.FinishAsync(synchronize: true, cancellationToken).ConfigureAwait(false);
        Finished(summary, TapeFileTypes.Catalog, start, number, null);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            db.TapeFiles.AddRange(_files);
            _files.Clear();
            await SaveTapeAsync(db, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The current tape's counters into <paramref name="db"/> (saved by the caller).</summary>
    private async Task SaveTapeAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (_tape is null)
        {
            return;
        }

        var tracked = await db.TapeMedia.FirstAsync(m => m.Id == _tape.Id, cancellationToken).ConfigureAwait(false);
        tracked.FileCount = _tape.FileCount;
        tracked.BytesWritten = _tape.BytesWritten;
        tracked.LastWrittenUtc = _tape.LastWrittenUtc;
    }

    private async Task MarkAsync(Guid mediumId, TapeMediumState state, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await SaveTapeAsync(db, cancellationToken).ConfigureAwait(false);
        var tracked = await db.TapeMedia.FirstAsync(m => m.Id == mediumId, cancellationToken).ConfigureAwait(false);
        tracked.State = state;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UnloadAsync(CancellationToken cancellationToken)
    {
        if (_drive is not null)
        {
            if (context.Pool.Encryption == TapeEncryption.Hardware)
            {
                try
                {
                    await _drive.Drive.ClearEncryptionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Log.Warning($"Clearing the drive's key failed: {ex.Message}");
                }
            }

            await _drive.DisposeAsync().ConfigureAwait(false);
            _drive = null;
        }

        await robot.UnloadHeldAsync(context.Library, context.Drive, export: false, context.Holder, Log, cancellationToken).ConfigureAwait(false);
        _tape = null;
    }

    private static ulong NewFileId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes) | 1;
    }

    private sealed record LabelRead(TapeLabel? Label, bool Foreign);
}

/// <summary><c>copy.json</c>, the first entry of a restore-point file: what was copied, from where.</summary>
public sealed record TapeCopyInfo(
    Guid CopyId, Guid OriginalId, Guid RepositoryId, string? JobName, string VmId, string VmName, DateTimeOffset CreatedUtc, Guid SetId, string PoolName)
{
    public const string FileName = "copy.json";
}
