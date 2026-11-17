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
internal sealed record TapeWriteResult(int Copied, int SqlCopied, long NewBytes, long ReusedChunks, IReadOnlyList<string> Tapes, long BytesWritten = 0);

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
    TapeOperatorRequests requests,
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
    private int _copiedSql;
    private long _bytesWritten;
    private readonly TransferClock _clock = new();
    private long _newBytes;
    private long _reused;

    private IOperationLog Log => context.Log;

    private MediaSetEntity Set => context.Set;

    /// <summary>Room for files on the current tape, keeping the reserve before its end.</summary>
    private long Available => _remaining - Reserve(_tape?.CapacityBytes);

    public async Task CopyRestorePointAsync(RestorePointEntity point, IBackupRepository repository, CancellationToken cancellationToken)
    {
        var (transfer, locator) = Transfer(repository, point.VmName);
        using var pin = repository.Pin(point.Id);
        var source = new TapeCopySource(
            TapeFileTypes.RestorePoint, point.Id, point.RepositoryId, point.JobName, point.VmNativeId, point.VmName, point.ObjectKind, point.CreatedUtc,
            await transfer.ReadRestorePointFilesAsync(point.Id, cancellationToken).ConfigureAwait(false),
            await transfer.ListRestorePointChunksAsync(point.Id, cancellationToken).ConfigureAwait(false));
        await CopyAsync(source, locator, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A SQL Server backup object (log or full) of a VM whose restore points go to the set.</summary>
    public async Task CopySqlBackupAsync(SqlBackupEntity backup, IBackupRepository repository, CancellationToken cancellationToken)
    {
        var (transfer, locator) = Transfer(repository, backup.VmName);
        var source = new TapeCopySource(
            TapeFileTypes.Sql, backup.Id, backup.RepositoryId, null, backup.VmNativeId, backup.VmName, $"sql-{backup.Kind}".ToLowerInvariant(), backup.CreatedUtc,
            await transfer.ReadSqlBackupFilesAsync(backup.Id, cancellationToken).ConfigureAwait(false),
            await transfer.ListSqlBackupChunksAsync(backup.Id, cancellationToken).ConfigureAwait(false));
        await CopyAsync(source, locator, cancellationToken).ConfigureAwait(false);
    }

    private static (IRepositoryTransfer Transfer, IChunkLocator Locator) Transfer(IBackupRepository repository, string vmName) =>
        repository is IRepositoryTransfer transfer && repository is IChunkLocator locator
            ? (transfer, locator)
            : throw new InvalidOperationException($"The repository of {vmName} cannot copy to tape.");

    private async Task CopyAsync(TapeCopySource source, IChunkLocator locator, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var driveBefore = _clock.Elapsed;
        var writtenBefore = _bytesWritten;
        var files = source.Files;
        var ids = source.Chunks;
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
        var info = new TapeCopyInfo(copyId, source.OriginalId, source.RepositoryId, source.JobName, source.VmId, source.VmName, source.CreatedUtc, Set.Id, context.Pool.Name);
        var records = new List<(string Type, byte[] Body)> { (TapeFormat.Entry, TapeEntries.Encode(TapeCopyInfo.FileName, TapeJson.Serialize(info))) };
        records.AddRange(files.Select(f => (TapeFormat.Entry, TapeEntries.Encode(f.Name, f.Content))));
        records.Add((TapeFormat.Locations, ChunkLocations.Encode(ids.Select(id => locations[id]))));
        var size = records.Sum(r => (long)TapeFormat.RecordHeaderSize + r.Body.Length + (_cipher?.Overhead ?? 0)) + (8L * context.Pool.BlockSize);
        TapeFileEntity? restorePointFile = null;
        for (var attempt = 0; restorePointFile is null; attempt++)
        {
            if (attempt > 3)
            {
                throw new IOException($"The {source.Kind} file of {source.VmName} ({Format(size)}) did not fit on {attempt} tapes.");
            }

            await EnsureTapeAsync(cancellationToken).ConfigureAwait(false);
            if (Available < size)
            {
                await ChangeTapeAsync($"the {source.Kind} file of {source.VmName} ({Format(size)}) does not fit on {_tape!.Barcode} ({Format(Available)} left)", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var (file, start, number) = await BeginFileAsync(source.Kind, copyId, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var (type, body) in records)
                {
                    await file.WriteRecordAsync(type, body, encrypt: true, cancellationToken).ConfigureAwait(false);
                }

                var summary = await file.FinishAsync(synchronize: true, cancellationToken).ConfigureAwait(false);
                restorePointFile = Finished(summary, source.Kind, start, number, copyId);
            }
            catch (TapeFullException ex)
            {
                await TapeFullAsync($"{ex.Message} The {source.Kind} file of {source.VmName} is cut off and is written again on the next tape.", cancellationToken).ConfigureAwait(false);
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
                Kind = source.Kind,
                OriginalId = source.OriginalId,
                RepositoryId = source.RepositoryId,
                JobName = source.JobName,
                VmId = source.VmId,
                VmName = source.VmName,
                ObjectKind = source.ObjectKind,
                CreatedUtc = source.CreatedUtc,
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

        if (source.Kind == TapeFileTypes.Sql)
        {
            _copiedSql++;
        }
        else
        {
            _copied++;
        }

        _newBytes += newBytes;
        _reused += reused;
        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        var onTape = _bytesWritten - writtenBefore;
        Log.Info($"{source.VmName} ({(source.Kind == TapeFileTypes.Sql ? $"SQL Server backup ({source.ObjectKind})" : "restore point")} of {source.CreatedUtc:yyyy-MM-dd HH:mm} UTC) is on tape: {Format(newBytes)} of new chunks, {reused} chunk(s) the set had already; tapes {string.Join(", ", needed.Select(s => $"#{s}"))}. " +
            $"{Format(onTape)} written in {seconds:0.0} s ({Format((long)(onTape / Math.Max(0.001, seconds)))}/s); waiting for the drive took {(_clock.Elapsed - driveBefore).TotalSeconds:0.0} s of it, the rest went to reading the repository.");
    }

    /// <summary>The catalog on the current tape, then the tape back into its slot.</summary>
    public async Task<TapeWriteResult> FinishAsync(CancellationToken cancellationToken)
    {
        if (_tape is not null)
        {
            await WriteCatalogAsync(cancellationToken).ConfigureAwait(false);
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TapeWriteResult(_copied, _copiedSql, _newBytes, _reused, _tapesUsed, _bytesWritten);
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
        var writer = await TapeFileWriter.BeginAsync(new DriveBlocks(_drive.Drive, _clock), context.Pool.BlockSize, header, _cipher, cancellationToken).ConfigureAwait(false);
        return (writer, position.Block, _fileNumber);
    }

    private TapeFileEntity Finished(TapeFileSummary summary, string type, long startBlock, int fileNumber, Guid? copyId)
    {
        _remaining -= summary.Bytes;
        _bytesWritten += summary.Bytes;
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
        if (context.Library.ChangerSerial is null)
        {
            await EnsureStandaloneTapeAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        while (_tape is null)
        {
            var (candidate, appending) = await ChooseAsync(cancellationToken).ConfigureAwait(false);
            var barcode = candidate.Barcode;
            var (_, medium) = await robot.LoadHeldAsync(context.Library, context.Drive, barcode, context.Holder, Log, cancellationToken).ConfigureAwait(false);
            _drive = await libraries.OpenDriveAsync(context.Library, context.Drive, cancellationToken).ConfigureAwait(false);
            await _drive.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
            var label = await TapeLabelReader.ReadAsync(_drive.Drive, cancellationToken).ConfigureAwait(false);

            // A tape marked free in the console (its barcode typed) is written over, whatever is on it.
            var released = !appending && candidate.State == TapeMediumState.Free && candidate.SetId is null;
            if (label.Foreign && !released)
            {
                Log.Warning($"{barcode} holds data that is not backdafuckup's ({label.Reason}); it is never written to.");
                await MarkAsync(candidate.Id, TapeMediumState.Foreign, cancellationToken).ConfigureAwait(false);
                await UnloadAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (appending)
            {
                if (label.Label?.Set is not { } set || set.Id != Set.Id || set.Sequence != candidate.Sequence)
                {
                    Log.Warning($"{barcode} should be tape #{candidate.Sequence} of the set, but its label says otherwise; it is not written to.");
                    await MarkAsync(candidate.Id, TapeMediumState.Foreign, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            else
            {
                if (label.Label is { } old && old.Set is not null && !released && !(candidate.State == TapeMediumState.Expired && old.Set.PoolId == context.Pool.Id))
                {
                    Log.Warning($"{barcode} belongs to set {old.Set.Id} of pool {old.Set.PoolName} that has not expired; it is not written to.");
                    await MarkAsync(candidate.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (medium.Worm && !label.Blank)
                {
                    Log.Warning($"{barcode} is a WORM tape with data on it; it cannot be labelled again.");
                    await MarkAsync(candidate.Id, TapeMediumState.Full, cancellationToken).ConfigureAwait(false);
                    await UnloadAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

            }

            await StartOnTapeAsync(candidate, medium, appending, candidate.Barcode, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The tape in the drive is the one to write: at its end of data, or labelled for the set first.</summary>
    private async Task StartOnTapeAsync(TapeMediumEntity candidate, MediumInfo medium, bool appending, string name, CancellationToken cancellationToken)
    {
        if (appending)
        {
            await _drive!.Drive.SpaceToEndOfDataAsync(cancellationToken).ConfigureAwait(false);
            var position = await _drive.Drive.ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            if (position.File is { } files && files != candidate.FileCount)
            {
                Log.Info($"{name} has {files} complete file(s), the catalog knows {candidate.FileCount}: files of a run that did not finish are left in place and ignored.");
                candidate.FileCount = (int)files;
            }

            _remaining = medium.RemainingBytes ?? ((medium.CapacityBytes ?? candidate.CapacityBytes ?? 0) - candidate.BytesWritten);
        }
        else
        {
            name = await LabelAsync(candidate, medium, cancellationToken).ConfigureAwait(false);
        }

        if (context.Pool.Encryption == TapeEncryption.Hardware)
        {
            var capabilities = await _drive!.Drive.ReadEncryptionCapabilitiesAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"{context.Drive.Name} cannot encrypt, but pool {context.Pool.Name} asks the drive to.");
            await _drive.Drive.SetEncryptionAsync(capabilities, TapeKeys.SetKey(context.Kek!, Set.Id, TapeKeys.HardwarePurpose), TapeKeys.KeyAssociatedData(context.Fingerprint!, Set.Id), cancellationToken).ConfigureAwait(false);
        }

        _tape = candidate;
        _fileNumber = candidate.FileCount;
        _tapesUsed.Add(name);
        Log.Info($"Writing to {name} (tape #{candidate.Sequence} of the set) in {context.Drive.Name}: {Format(Math.Max(0, Available))} available.");
    }

    /// <summary>
    /// A standalone drive: asks the operator for the set's last tape, or for a blank, free or expired cartridge, and waits
    /// until one is in the drive. A wrong cartridge is ejected and named in the request.
    /// </summary>
    private async Task EnsureStandaloneTapeAsync(CancellationToken cancellationToken)
    {
        TapeMediumEntity? wanted;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            wanted = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == Set.Id && m.State == TapeMediumState.Appendable).OrderByDescending(m => m.Sequence)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (wanted is not null && _skipped.Contains(wanted.Id))
        {
            wanted = null;
        }

        _drive ??= await libraries.OpenDriveAsync(context.Library, context.Drive, cancellationToken).ConfigureAwait(false);
        var where = $"{context.Drive.Name} of {context.Library.Name}";
        var message = wanted is not null
            ? $"Insert tape {wanted.LabelName ?? wanted.Barcode} (#{wanted.Sequence} of the set of pool {context.Pool.Name}) into {where}."
            : $"Insert a blank, free or expired {(context.Pool.WormOnly ? "WORM " : string.Empty)}cartridge for pool {context.Pool.Name} into {where}; it becomes tape #{Set.Media + 1} of the set.";
        TapeMediumEntity? chosen = null;
        MediumInfo? info = null;
        Log.Info($"Waiting for the operator: {message}");
        await requests.WaitAsync(context.SessionId, context.Library.Id, context.Drive.Id, message, async (say, token) =>
        {
            if (await _drive.Drive.TestReadyAsync(token).ConfigureAwait(false) != DriveReadiness.Ready)
            {
                return false; // Empty, loading, or a cartridge ejected and not taken out yet.
            }

            info = await _drive.Drive.ReadMediumAsync(token).ConfigureAwait(false);
            await _drive.Drive.PrepareAsync(token).ConfigureAwait(false);
            var label = await TapeLabelReader.ReadAsync(_drive.Drive, token).ConfigureAwait(false);
            var medium = await StandaloneMediumAsync(info, label, token).ConfigureAwait(false);
            string? refusal;
            if (wanted is not null)
            {
                refusal = label.Label?.Set is { } set && set.Id == Set.Id && set.Sequence == wanted.Sequence
                    ? null
                    : $"The cartridge inserted is {Describe(label, medium)}, not tape {wanted.LabelName ?? wanted.Barcode}; it was ejected.";
                medium = refusal is null ? wanted : medium;
            }
            else
            {
                refusal = await UsableAsync(label, info, medium, token).ConfigureAwait(false);
            }

            if (refusal is not null)
            {
                Log.Info(refusal);
                say(refusal);
                await _drive.Drive.UnloadAsync(token).ConfigureAwait(false);
                return false;
            }

            chosen = medium;
            return true;
        }, cancellationToken).ConfigureAwait(false);

        await StartOnTapeAsync(chosen!, info!, wanted is not null, chosen!.LabelName ?? chosen.Barcode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a cartridge in a standalone drive cannot become a new tape of the set, or null when it can.</summary>
    private async Task<string?> UsableAsync(TapeLabelRead label, MediumInfo info, TapeMediumEntity medium, CancellationToken cancellationToken)
    {
        if (info.Worm != context.Pool.WormOnly)
        {
            return $"The cartridge inserted is {(info.Worm ? "a WORM cartridge" : "no WORM cartridge")}, pool {context.Pool.Name} takes {(context.Pool.WormOnly ? "WORM cartridges only" : "rewritable cartridges")}; it was ejected.";
        }

        if (label.Blank || (medium.State == TapeMediumState.Free && medium.SetId is null && !info.Worm))
        {
            return null;
        }

        if (label.Foreign)
        {
            return $"The cartridge inserted holds data that is not backdafuckup's ({label.Reason}); it was ejected. Mark it free in the console to use it.";
        }

        if (info.Worm)
        {
            return "The WORM cartridge inserted has data on it; it was ejected.";
        }

        if (label.Label!.Set is not { } old)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var set = await db.MediaSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == old.Id, cancellationToken).ConfigureAwait(false);
        return set is { State: MediaSetState.Expired or MediaSetState.Overwritten } && set.PoolId == context.Pool.Id
            ? null
            : $"The cartridge inserted is {label.Label.Name}, tape #{old.Sequence} of a set of pool {old.PoolName} that is kept; it was ejected.";
    }

    private static string Describe(TapeLabelRead label, TapeMediumEntity medium) =>
        label.Blank ? "blank" : label.Label is { } l ? $"{l.Name}{(l.Set is { } s ? $" (tape #{s.Sequence} of set {s.Id} of pool {s.PoolName})" : string.Empty)}" : $"{medium.Barcode} with other data";

    /// <summary>The catalog's entry of the cartridge in a standalone drive, found by the serial in its memory (or created).</summary>
    private async Task<TapeMediumEntity> StandaloneMediumAsync(MediumInfo info, TapeLabelRead label, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var serial = info.MediumSerial ?? throw new IOException($"The cartridge in {context.Drive.Name} has no serial in its memory; it cannot be told apart from others.");
        var medium = await db.TapeMedia.FirstOrDefaultAsync(m => m.MediumSerial == serial, cancellationToken).ConfigureAwait(false);
        if (medium is null)
        {
            var name = label.Label?.Name ?? $"SN-{serial}";
            if (await db.TapeMedia.AnyAsync(m => m.Barcode == name, cancellationToken).ConfigureAwait(false))
            {
                name = $"{name}-{serial}";
            }

            medium = new TapeMediumEntity
            {
                Barcode = name,
                MediumSerial = serial,
                Kind = info.Cleaning ? TapeMediumKind.Cleaning : info.Worm ? TapeMediumKind.Worm : TapeMediumKind.Data,
                State = label.Blank ? TapeMediumState.Blank : label.Foreign ? TapeMediumState.Foreign : label.Label?.Set is null ? TapeMediumState.Free : TapeMediumState.Full,
                LabelName = label.Label?.Name,
                FirstSeenUtc = time.GetUtcNow(),
            };
            db.TapeMedia.Add(medium);
        }

        medium.LibraryId = context.Library.Id;
        medium.Location = TapeLocation.Drive;
        medium.CapacityBytes = info.CapacityBytes ?? medium.CapacityBytes;
        medium.RemainingBytes = info.RemainingBytes ?? medium.RemainingBytes;
        medium.SeenUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return medium;
    }

    /// <summary>The set's last tape if it can take more, else a new one: expired tapes of the pool, free, blank, unread.</summary>
    private async Task<(TapeMediumEntity Medium, bool Appending)> ChooseAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var last = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == Set.Id).OrderByDescending(m => m.Sequence).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (last is { State: TapeMediumState.Appendable, Location: TapeLocation.Slot or TapeLocation.Drive } && last.LibraryId == context.Library.Id && !_skipped.Contains(last.Id))
        {
            _skipped.Add(last.Id);
            return (last, true);
        }

        var kind = context.Pool.WormOnly ? TapeMediumKind.Worm : TapeMediumKind.Data;
        var usable = new[] { TapeMediumState.Expired, TapeMediumState.Free, TapeMediumState.Blank, TapeMediumState.Unknown };
        var media = await db.TapeMedia.AsNoTracking()
            .Where(m => m.LibraryId == context.Library.Id && (m.Location == TapeLocation.Slot || m.Location == TapeLocation.Drive) && m.Kind == kind && usable.Contains(m.State))
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

    /// <summary>Labels the tape in the drive as the set's next one; returns its name (its barcode, or a name to write on a standalone cartridge).</summary>
    private async Task<string> LabelAsync(TapeMediumEntity candidate, MediumInfo medium, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var set = await db.MediaSets.FirstAsync(s => s.Id == Set.Id, cancellationToken).ConfigureAwait(false);
        var sequence = set.Media + 1;
        var standalone = context.Library.ChangerSerial is null;
        var name = standalone ? $"{string.Concat(context.Pool.Name.Where(char.IsLetterOrDigit))}-{Set.Id.ToString("N")[^6..].ToUpperInvariant()}-{sequence}" : candidate.Barcode;
        var label = new TapeLabel
        {
            MediumId = Guid.CreateVersion7(),
            Barcode = standalone ? null : candidate.Barcode,
            Name = name,
            BlockSize = context.Pool.BlockSize,
            Worm = medium.Worm,
            CreatedUtc = time.GetUtcNow(),
            Server = Environment.MachineName,
            Set = new TapeLabelSet(Set.Id, sequence, context.Pool.Id, context.Pool.Name, Set.CreatedUtc),
            Encryption = context.Fingerprint is null ? null : TapeKeys.Describe(context.Pool.Encryption == TapeEncryption.Hardware ? "hardware" : "software", context.Fingerprint, context.Kdf!),
        };

        await _drive!.Drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        var header = new TapeFileHeader { Type = TapeFileTypes.Label, FileId = NewFileId(), SetId = Set.Id, Sequence = sequence, FileNumber = 0, CreatedUtc = label.CreatedUtc };
        var writer = await TapeFileWriter.BeginAsync(new DriveBlocks(_drive.Drive, _clock), context.Pool.BlockSize, header, null, cancellationToken).ConfigureAwait(false);
        await writer.WriteRecordAsync(TapeFormat.Label, TapeJson.Serialize(label), encrypt: false, cancellationToken).ConfigureAwait(false);
        var summary = await writer.FinishAsync(synchronize: true, cancellationToken).ConfigureAwait(false);
        if (standalone)
        {
            // The name in the cartridge memory too: an inventory of the drive finds the tape by it.
            await _drive.Drive.WriteAttributesAsync([(Backdafuckup.Tape.Scsi.MamAttributes.Barcode, 1, System.Text.Encoding.ASCII.GetBytes(name.PadRight(32)[..32]))], cancellationToken).ConfigureAwait(false);
        }

        var tracked = await db.TapeMedia.FirstAsync(m => m.Id == candidate.Id, cancellationToken).ConfigureAwait(false);
        if (standalone && tracked.Barcode != name && !await db.TapeMedia.AnyAsync(m => m.Barcode == name && m.Id != tracked.Id, cancellationToken).ConfigureAwait(false))
        {
            tracked.Barcode = name;
            candidate.Barcode = name;
        }

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
        candidate.LabelName = name;
        _remaining = (medium.RemainingBytes ?? medium.CapacityBytes ?? 0) - summary.Bytes;
        Log.Info($"{name} labelled as tape #{sequence} of set {Set.Id} (pool {context.Pool.Name}{(medium.Worm ? ", WORM" : string.Empty)})." +
            (standalone ? $" Write \"{name}\" on the cartridge." : string.Empty));
        return name;
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
        if (context.Library.ChangerSerial is null)
        {
            if (_drive is not null)
            {
                await _drive.Drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_tape is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                await db.TapeMedia.Where(m => m.Id == _tape.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Location, TapeLocation.Offline), cancellationToken).ConfigureAwait(false);
                Log.Info($"{_tape.LabelName ?? _tape.Barcode} ejected from {context.Drive.Name}: take it out.");
            }

            _tape = null;
            return;
        }

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
}

/// <summary>What one copy takes from its repository.</summary>
internal sealed record TapeCopySource(
    string Kind, Guid OriginalId, Guid RepositoryId, string? JobName, string VmId, string VmName, string? ObjectKind, DateTimeOffset CreatedUtc,
    IReadOnlyList<RepositoryFile> Files, IReadOnlyList<ChunkId> Chunks);

/// <summary><c>copy.json</c>, the first entry of a restore-point file: what was copied, from where.</summary>
public sealed record TapeCopyInfo(
    Guid CopyId, Guid OriginalId, Guid RepositoryId, string? JobName, string VmId, string VmName, DateTimeOffset CreatedUtc, Guid SetId, string PoolName)
{
    public const string FileName = "copy.json";
}
