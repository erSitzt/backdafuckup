using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Format;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Tape;

/// <summary>A queued import of a tape copy into a repository (the session's parameters).</summary>
public sealed record TapeRestoreRequest(Guid CopyId, Guid RepositoryId);

/// <summary>
/// Brings a tape copy back into a repository: reads its restore-point file (its files and where its chunks are), then
/// the chunks tape by tape and file by file, stores them as they were stored (checked, not compressed again) and commits
/// the restore point under a new id. It belongs to no job, so job retention never deletes it.
/// </summary>
public sealed class TapeRestoreRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeLocks locks,
    TapeSessionRunner robot,
    RepositoryManager repositories,
    CredentialProtector protector,
    SessionEventWriter events)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<TapeRestoreRequest>(session.Parameters ?? "null") ?? throw new InvalidOperationException("The tape session has no parameters.");
        TapeCopyEntity copy;
        MediaSetEntity set;
        MediaPoolEntity pool;
        List<TapeMediumEntity> media;
        List<TapeFileEntity> files;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            copy = await db.TapeCopies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CopyId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tape copy {request.CopyId} does not exist.");
            set = await db.MediaSets.AsNoTracking().FirstAsync(s => s.Id == copy.SetId, cancellationToken).ConfigureAwait(false);
            pool = await db.MediaPools.AsNoTracking().FirstAsync(p => p.Id == set.PoolId, cancellationToken).ConfigureAwait(false);
            media = await db.TapeMedia.AsNoTracking().Where(m => m.SetId == set.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            files = await db.TapeFiles.AsNoTracking().Where(f => f.SetId == set.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var library = await libraries.LoadAsync(pool.LibraryId, cancellationToken).ConfigureAwait(false);
        var missing = copy.NeededSequences.Where(s => media.FirstOrDefault(m => m.Sequence == s) is not { Location: not TapeLocation.Offline } m || m.LibraryId != library.Id).ToList();
        if (missing.Count > 0)
        {
            var names = missing.Select(s => media.FirstOrDefault(m => m.Sequence == s)?.Barcode ?? $"#{s}");
            throw new IOException($"{copy.VmName} needs tape(s) {string.Join(", ", names)}, which are not in {library.Name}: import them from the mail slots first.");
        }

        var repository = await repositories.GetAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
        if (repository is not IRepositoryTransfer transfer)
        {
            throw new InvalidOperationException("This repository cannot take copies from tape.");
        }

        var free = repository.GetStorageStats().FreeBytes;
        if (free < copy.StoredBytes + (copy.StoredBytes / 20))
        {
            throw new IOException($"The repository has {VmBackupTask.Format(free)} free; {copy.VmName} needs up to {VmBackupTask.Format(copy.StoredBytes)} (+5 %).");
        }

        var cipher = await CipherAsync(set, cancellationToken).ConfigureAwait(false);
        var holder = $"Restore {copy.VmName} from tape";
        var drive = library.Drives.OrderBy(d => locks.Drive(d.Id).Snapshot().Holder is null ? 0 : 1).ThenBy(d => d.ElementAddress).First();
        await using var lease = await locks.Drive(drive.Id).AcquireAsync(holder, h => log.Info($"Waiting for {drive.Name}: '{h}' uses it."), cancellationToken).ConfigureAwait(false);
        // Blocks are never larger than the pool's block size; a larger buffer only costs transfer where no residual is reported.
        var blockSize = pool.BlockSize;
        var tapes = new TapeReaderSession(library, drive, media, libraries, robot, holder, log);
        var newId = Guid.CreateVersion7();
        try
        {
            // The restore-point file: its entries, then where its chunks are.
            var reader = await tapes.AtAsync(copy.Sequence, copy.FileStartBlock, cancellationToken).ConfigureAwait(false);
            var entries = new List<(string Name, byte[] Content)>();
            IReadOnlyList<ChunkLocation> locations = [];
            await foreach (var record in new TapeFileReader(reader, blockSize, cipher).ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (record.Type == TapeFormat.Entry)
                {
                    entries.Add(TapeEntries.Decode(record.Body));
                }
                else if (record.Type == TapeFormat.Locations)
                {
                    locations = ChunkLocations.Decode(record.Body);
                }
            }

            var manifestJson = entries.FirstOrDefault(e => e.Name == "manifest.json").Content
                ?? throw new InvalidDataException($"The restore-point file of {copy.VmName} has no manifest.");
            var manifest = JsonSerializer.Deserialize(manifestJson, CoreJsonContext.Default.RestorePointManifest)!;
            log.Info($"Restore-point file of {copy.VmName} read from tape #{copy.Sequence}: {entries.Count - 2} file(s), {locations.Count} chunk(s) on tape(s) {string.Join(", ", locations.Select(l => l.Sequence).Distinct().Order().Select(s => $"#{s}"))}.");

            await using var import = transfer.BeginImport(newId, manifest);
            long chunks = 0;
            foreach (var group in locations.GroupBy(l => (l.Sequence, l.FileNumber)).OrderBy(g => g.Key.Sequence).ThenBy(g => g.Key.FileNumber))
            {
                var file = files.FirstOrDefault(f => f.Sequence == group.Key.Sequence && f.FileNumber == group.Key.FileNumber)
                    ?? throw new InvalidDataException($"Tape #{group.Key.Sequence} file {group.Key.FileNumber} is not in the set's catalog.");
                var needed = group.ToDictionary(l => l.Id);
                var first = group.OrderBy(l => l.Block).ThenBy(l => l.Offset).First();
                var blocks = await tapes.AtAsync(group.Key.Sequence, file.StartBlock + first.Block, cancellationToken).ConfigureAwait(false);
                await foreach (var record in new TapeFileReader(blocks, blockSize, cipher).ReadFromAsync(file.FileId, new RecordPosition(first.Block, first.Offset), cancellationToken).ConfigureAwait(false))
                {
                    if (record.Type != TapeFormat.Chunk)
                    {
                        continue;
                    }

                    var (id, stored) = TapeEntries.DecodeChunk(record.Body);
                    if (needed.Remove(id))
                    {
                        await import.PutEncodedChunkAsync(id, stored, cancellationToken).ConfigureAwait(false);
                        chunks++;
                        if (needed.Count == 0)
                        {
                            break;
                        }
                    }
                }

                if (needed.Count > 0)
                {
                    throw new InvalidDataException($"{needed.Count} chunk(s) of {copy.VmName} were not in tape #{group.Key.Sequence} file {group.Key.FileNumber}.");
                }
            }

            foreach (var (name, content) in entries.Where(e => e.Name is not (TapeCopyInfo.FileName or "manifest.json")))
            {
                await import.WriteFileAsync(name, content, cancellationToken).ConfigureAwait(false);
            }

            var committed = await import.CommitAsync(m => m with { Job = null, BaseRestorePointId = null }, cancellationToken).ConfigureAwait(false);
            await CatalogAsync(committed, copy, set, request.RepositoryId, session.Id, cancellationToken).ConfigureAwait(false);
            var summary = $"{copy.VmName} (restore point of {copy.CreatedUtc:yyyy-MM-dd HH:mm} UTC) is back in the repository as {newId}: {chunks} chunk(s) read from {tapes.Loaded.Count} tape(s).";
            log.Info(summary);
            return (log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
        }
        finally
        {
            await tapes.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ITapeRecordCipher?> CipherAsync(MediaSetEntity set, CancellationToken cancellationToken)
    {
        if (set.KeyFingerprint is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Fingerprint == set.KeyFingerprint, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The set is encrypted with key {set.KeyFingerprint}, which this server does not have: add it under Tape → Keys with its passphrase or key file.");
        var kek = Convert.FromBase64String(protector.Unprotect(key.ProtectedKek));
        return new AesGcmRecordCipher(TapeKeys.SetKey(kek, set.Id, TapeKeys.SoftwarePurpose), set.Id);
    }

    private async Task CatalogAsync(ManifestInfo committed, TapeCopyEntity copy, MediaSetEntity set, Guid repositoryId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var manifest = committed.Manifest;
        var entity = new RestorePointEntity
        {
            Id = committed.RestorePointId,
            RepositoryId = repositoryId,
            JobId = null,
            JobName = $"Imported from tape (set of {set.CreatedUtc:yyyy-MM-dd})",
            SessionId = sessionId,
            ImportedFromTapeCopyId = copy.Id,
            ConnectionId = await db.Connections.Where(c => c.PlatformId == manifest.Source.PlatformId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            PlatformId = manifest.Source.PlatformId,
            VmNativeId = manifest.Source.VmId,
            VmName = manifest.Vm.Name,
        };
        VmBackupTask.ApplyManifest(entity, manifest, committed.Path);
        db.RestorePoints.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The tapes of a set for reading: loads the one asked for into the held drive and places it at a block.</summary>
internal sealed class TapeReaderSession(
    TapeLibraryEntity library, TapeDriveEntity drive, IReadOnlyList<TapeMediumEntity> media, TapeLibraries libraries, TapeSessionRunner robot, string holder, IOperationLog log) : IAsyncDisposable
{
    private OpenedDrive? _drive;
    private int? _sequence;

    public HashSet<int> Loaded { get; } = [];

    public async Task<DriveBlocks> AtAsync(int sequence, long block, CancellationToken cancellationToken)
    {
        if (_sequence != sequence)
        {
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
            var medium = media.First(m => m.Sequence == sequence);
            await robot.LoadHeldAsync(library, drive, medium.Barcode, holder, log, cancellationToken).ConfigureAwait(false);
            _drive = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
            await _drive.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
            _sequence = sequence;
            Loaded.Add(sequence);
        }

        await _drive!.Drive.LocateBlockAsync(block, cancellationToken).ConfigureAwait(false);
        return new DriveBlocks(_drive.Drive);
    }

    public async ValueTask DisposeAsync() => await UnloadAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task UnloadAsync(CancellationToken cancellationToken)
    {
        if (_drive is null)
        {
            return;
        }

        await _drive.DisposeAsync().ConfigureAwait(false);
        _drive = null;
        _sequence = null;
        await robot.UnloadHeldAsync(library, drive, export: false, holder, log, cancellationToken).ConfigureAwait(false);
    }
}
