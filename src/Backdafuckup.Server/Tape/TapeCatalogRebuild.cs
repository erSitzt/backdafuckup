using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Format;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Tape;

/// <summary>
/// Rebuilds the tape catalog from the tapes, for a lost database or tapes of another server: reads the label of every
/// tape in the library the catalog does not know, then for every set it does not know the newest catalog on the set's
/// highest tape there, and the copies written after that catalog. Pools, sets, tapes, files and copies come back; the
/// sets as closed (their index for appending is gone), kept for their pool's retention from now.
/// </summary>
public sealed class TapeCatalogRebuild(
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeSessionRunner robot,
    CredentialProtector protector,
    SessionEventWriter events,
    TimeProvider time)
{
    public static bool IsRebuild(SessionEntity session) =>
        session.Parameters is { } parameters && JsonSerializer.Deserialize<TapeSessionRequest>(parameters)?.Action == TapeActions.Rebuild;

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<TapeSessionRequest>(session.Parameters ?? "null") ?? throw new InvalidOperationException("The tape session has no parameters.");
        var library = await libraries.LoadAsync(request.LibraryId, cancellationToken).ConfigureAwait(false);
        if (library.ChangerSerial is null)
        {
            throw new TapeRequestException($"{library.Name} is a standalone drive: read its tapes with bdfu-tape-recover (catalog), or put them into a library and rebuild there.");
        }

        HashSet<Guid> known;
        List<TapeMediumEntity> media;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            known = [.. await db.MediaSets.Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false)];
            media = await db.TapeMedia.AsNoTracking()
                .Where(m => m.LibraryId == library.Id && (m.Location == TapeLocation.Slot || m.Location == TapeLocation.Drive) && m.Kind != TapeMediumKind.Cleaning)
                .OrderBy(m => m.Barcode).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        media = media.Where(m => m.State == TapeMediumState.Unknown || (m.SetId is { } set && !known.Contains(set))).ToList();
        if (media.Count == 0)
        {
            var nothing = $"Every tape in {library.Name} is known: nothing to rebuild. Tapes put in since the last inventory are found by an inventory first.";
            log.Info(nothing);
            return (SessionState.Succeeded, nothing);
        }

        log.Info($"{media.Count} tape(s) of {library.Name} to read: {string.Join(", ", media.Select(m => m.Barcode))}.");
        const string holder = "Rebuild the tape catalog";
        var (drive, lease) = await robot.ChooseDriveAsync(library, null, holder, log, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            // 1. Every unknown tape's label: what it is, and which sets this server does not know.
            var sets = new Dictionary<Guid, List<(string Barcode, TapeLabel Label)>>();
            foreach (var medium in media)
            {
                await robot.LoadHeldAsync(library, drive, medium.Barcode, holder, log, cancellationToken).ConfigureAwait(false);
                TapeLabelRead read;
                await using (var opened = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false))
                {
                    await opened.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
                    read = await TapeLabelReader.ReadAsync(opened.Drive, cancellationToken).ConfigureAwait(false);
                }

                log.Info(await libraries.ApplyLabelAsync(medium.Barcode, read, cancellationToken).ConfigureAwait(false));
                if (read.Label is { Set: { } set } label && !known.Contains(set.Id))
                {
                    (sets.TryGetValue(set.Id, out var list) ? list : sets[set.Id] = []).Add((medium.Barcode, label));
                }

                await robot.UnloadHeldAsync(library, drive, export: false, holder, log, cancellationToken).ConfigureAwait(false);
            }

            // 2. Each unknown set from its highest tape here: the newest catalog, and the copies after it.
            var rebuilt = 0;
            var copies = 0;
            foreach (var (setId, tapes) in sets)
            {
                var (barcode, label) = tapes.MaxBy(t => t.Label.Set!.Sequence);
                var found = await ReadSetAsync(library, drive, barcode, label, holder, log, cancellationToken).ConfigureAwait(false);
                if (found is null)
                {
                    continue;
                }

                copies += await SaveAsync(library, label, tapes, found.Value.Catalog, found.Value.Files, found.Value.Extra, log, cancellationToken).ConfigureAwait(false);
                rebuilt++;
            }

            var skipped = sets.Count - rebuilt;
            var summary = sets.Count == 0
                ? $"{media.Count} tape(s) read; none of them belongs to a set this server does not know."
                : $"{rebuilt} set(s) rebuilt from {media.Count} tape(s) with {copies} copie(s)" + (skipped > 0 ? $"; {skipped} set(s) could not be read (see the log)." : ".");
            log.Info(summary);
            return (log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
        }
    }

    /// <summary>The set's files on its highest tape here, its newest catalog there, and the copies written after it; null when its key is missing.</summary>
    private async Task<(TapeCatalog? Catalog, IReadOnlyList<TapeFileFound> Files, List<ExtraCopy> Extra)?> ReadSetAsync(
        TapeLibraryEntity library, TapeDriveEntity drive, string barcode, TapeLabel label, string holder, WarningTrackingLog log, CancellationToken cancellationToken)
    {
        var set = label.Set!;
        byte[]? kek = null;
        if (label.Encryption is { } encryption)
        {
            kek = await KekAsync(encryption.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (kek is null)
            {
                log.Warning($"Set {set.Id} of pool {set.PoolName} is encrypted with key {encryption.Fingerprint}, which this server does not have. " +
                    "Add it under Tape → Media pools → Keys → Add existing key (its key file, or its passphrase), then rebuild again.");
                return null;
            }
        }

        var cipher = kek is not null && label.Encryption!.Mode == "software" ? new AesGcmRecordCipher(TapeKeys.SetKey(kek, set.Id, TapeKeys.SoftwarePurpose), set.Id) : null;
        await robot.LoadHeldAsync(library, drive, barcode, holder, log, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var opened = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
            await opened.Drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
            if (kek is not null && label.Encryption!.Mode == "hardware")
            {
                var capabilities = await opened.Drive.ReadEncryptionCapabilitiesAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException($"Set {set.Id} was encrypted by the drive, but {drive.Name} cannot decrypt.");
                await opened.Drive.SetEncryptionAsync(capabilities, TapeKeys.SetKey(kek, set.Id, TapeKeys.HardwarePurpose), TapeKeys.KeyAssociatedData(label.Encryption.Fingerprint, set.Id), cancellationToken).ConfigureAwait(false);
            }

            var files = await TapeSetReader.ScanAsync(opened.Drive, label.BlockSize, cancellationToken).ConfigureAwait(false);
            var catalog = await TapeSetReader.ReadCatalogAsync(opened.Drive, files, label.BlockSize, cipher, cancellationToken).ConfigureAwait(false);
            log.Info(catalog is null
                ? $"{barcode} (#{set.Sequence} of set {set.Id}, pool {set.PoolName}): {files.Count} file(s), no complete catalog."
                : $"{barcode} (#{set.Sequence} of set {set.Id}, pool {set.PoolName}): catalog of {catalog.WrittenUtc:yyyy-MM-dd HH:mm} UTC with {catalog.Media.Count} tape(s) and {catalog.Copies.Count} copie(s).");

            // Copies after the catalog (a run that did not get to write it): from their own files.
            var listed = catalog?.Copies.Select(c => c.CopyId).ToHashSet() ?? [];
            var extra = new List<ExtraCopy>();
            var volumes = new OneTape(opened.Drive, set.Sequence);
            foreach (var file in files.Where(f => f.Header.Type is TapeFileTypes.RestorePoint or TapeFileTypes.Sql && f.Header.CopyId is { } id && !listed.Contains(id)))
            {
                try
                {
                    var (entries, locations) = await TapeSetReader.ReadCopyAsync(volumes, label.BlockSize, cipher, set.Sequence, file.StartBlock, cancellationToken).ConfigureAwait(false);
                    var info = TapeJson.Deserialize<TapeCopyInfo>(entries.First(e => e.Name == TapeCopyInfo.FileName).Content);
                    var manifest = System.Text.Encoding.UTF8.GetString(entries.First(e => e.Name == "manifest.json").Content);
                    extra.Add(new ExtraCopy(file, info, manifest, [.. locations.Select(l => l.Sequence).Distinct().Order()], locations.Sum(l => (long)l.Length)));
                }
                catch (Exception ex) when (ex is TornFileException or InvalidDataException or InvalidOperationException)
                {
                    log.Info($"File {file.FileNumber} on {barcode} ({file.Header.Type}) is incomplete and left out: {ex.Message}");
                }
            }

            if (extra.Count > 0)
            {
                log.Info($"{extra.Count} copie(s) on {barcode} after its last catalog, read from their own files.");
            }

            return (catalog, files, extra);
        }
        finally
        {
            if (kek is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(kek);
            }

            await robot.UnloadHeldAsync(library, drive, export: false, holder, log, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<int> SaveAsync(
        TapeLibraryEntity library, TapeLabel label, List<(string Barcode, TapeLabel Label)> tapes, TapeCatalog? catalog, IReadOnlyList<TapeFileFound> found, List<ExtraCopy> extra,
        WarningTrackingLog log, CancellationToken cancellationToken)
    {
        var labelSet = label.Set!;
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pool = await db.MediaPools.FirstOrDefaultAsync(p => p.Id == labelSet.PoolId, cancellationToken).ConfigureAwait(false);
        if (pool is null)
        {
            var name = labelSet.PoolName;
            if (await db.MediaPools.AnyAsync(p => p.Name == name, cancellationToken).ConfigureAwait(false))
            {
                name = $"{name} ({labelSet.PoolId.ToString()[..8]})";
            }

            var fingerprint = label.Encryption?.Fingerprint;
            pool = new MediaPoolEntity
            {
                Id = labelSet.PoolId,
                Name = name,
                LibraryId = library.Id,
                BlockSize = label.BlockSize,
                WormOnly = label.Worm,
                Encryption = label.Encryption is null ? TapeEncryption.None : label.Encryption.Mode == "hardware" ? TapeEncryption.Hardware : TapeEncryption.Software,
                KeyId = fingerprint is null ? null : await db.TapeKeys.Where(k => k.Fingerprint == fingerprint).Select(k => (Guid?)k.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
                CreatedUtc = now,
            };
            db.MediaPools.Add(pool);
            log.Info($"Pool {name} created from the tapes; check when it starts new sets and how long it keeps them.");
        }

        // Files: the catalog's, and those on the highest tape after it.
        var files = (catalog?.Files ?? []).ToList();
        files.AddRange(found.Where(f => !files.Any(x => x.Sequence == labelSet.Sequence && x.FileNumber == f.FileNumber))
            .Select(f => new TapeCatalogFile(labelSet.Sequence, f.FileNumber, f.StartBlock, f.Header.FileId, f.Header.Type, 0, 0, f.Header.CopyId)));
        var highest = Math.Max(catalog?.Media.Max(m => m.Sequence) ?? 0, labelSet.Sequence);
        db.MediaSets.Add(new MediaSetEntity
        {
            Id = labelSet.Id,
            PoolId = pool.Id,
            State = MediaSetState.Closed,
            CreatedUtc = catalog?.SetCreatedUtc ?? labelSet.CreatedUtc,
            ClosedUtc = now,
            ExpiresUtc = pool.RetentionDays is { } days ? now.AddDays(days) : null,
            KeyFingerprint = label.Encryption?.Fingerprint,
            Media = highest,
            Bytes = files.Sum(f => f.Bytes),
        });

        // Tapes: those read here, and the catalog's others as offline.
        for (var sequence = 1; sequence <= highest; sequence++)
        {
            var listed = catalog?.Media.FirstOrDefault(m => m.Sequence == sequence);
            var here = tapes.FirstOrDefault(t => t.Label.Set!.Sequence == sequence).Barcode;
            var medium = here is not null
                ? await db.TapeMedia.FirstAsync(m => m.Barcode == here, cancellationToken).ConfigureAwait(false)
                : listed is null ? null : await db.TapeMedia.FirstOrDefaultAsync(m => (listed.MediumId != Guid.Empty && m.MediumId == listed.MediumId) || m.Barcode == (listed.Barcode ?? listed.Name), cancellationToken).ConfigureAwait(false);
            if (medium is null)
            {
                if (listed is null)
                {
                    log.Warning($"Tape #{sequence} of set {labelSet.Id} is neither here nor in the catalog that was read; copies on it cannot be brought back until it is.");
                    continue;
                }

                var barcode = listed.Barcode ?? listed.Name;
                medium = new TapeMediumEntity
                {
                    Barcode = barcode,
                    LibraryId = library.Id,
                    Kind = label.Worm ? TapeMediumKind.Worm : TapeLibraries.KindFromBarcode(barcode),
                    Location = TapeLocation.Offline,
                    MediumId = listed.MediumId == Guid.Empty ? null : listed.MediumId,
                    LabelName = listed.Name,
                    FirstSeenUtc = now,
                    SeenUtc = now,
                };
                db.TapeMedia.Add(medium);
                log.Info($"{barcode} (#{sequence} of the set) is not in {library.Name}: listed as offline.");
            }

            var onIt = files.Where(f => f.Sequence == sequence).ToList();
            medium.SetId = labelSet.Id;
            medium.PoolId = pool.Id;
            medium.Sequence = sequence;
            medium.State = TapeMediumState.Full;
            medium.FileCount = onIt.Count == 0 ? medium.FileCount : onIt.Max(f => f.FileNumber) + 1;
            medium.BytesWritten = onIt.Sum(f => f.Bytes);
            medium.LabelName ??= listed?.Name;
        }

        db.TapeFiles.AddRange(files.Select(f => new TapeFileEntity
        {
            SetId = labelSet.Id, Sequence = f.Sequence, FileNumber = f.FileNumber, StartBlock = f.StartBlock, FileId = f.FileId, Type = f.Type, Blocks = f.Blocks, Bytes = f.Bytes, CopyId = f.CopyId,
        }));

        var copies = (catalog?.Copies ?? []).Select(c => new TapeCopyEntity
        {
            Id = c.CopyId,
            SetId = labelSet.Id,
            PoolId = pool.Id,
            Kind = c.Kind,
            OriginalId = c.OriginalId,
            RepositoryId = c.RepositoryId,
            JobName = c.JobName,
            VmId = c.VmId,
            VmName = c.VmName,
            ObjectKind = ObjectKindOf(c.Kind, c.Manifest),
            CreatedUtc = c.CreatedUtc,
            CopiedUtc = catalog!.WrittenUtc,
            Sequence = c.Sequence,
            FileNumber = c.FileNumber,
            NeededSequences = [.. c.NeededSequences],
            StoredBytes = c.StoredBytes,
            Manifest = c.Manifest,
        }).Concat(extra.Select(e => new TapeCopyEntity
        {
            Id = e.Info.CopyId,
            SetId = labelSet.Id,
            PoolId = pool.Id,
            Kind = e.File.Header.Type,
            OriginalId = e.Info.OriginalId,
            RepositoryId = e.Info.RepositoryId,
            JobName = e.Info.JobName,
            VmId = e.Info.VmId,
            VmName = e.Info.VmName,
            ObjectKind = ObjectKindOf(e.File.Header.Type, e.Manifest),
            CreatedUtc = e.Info.CreatedUtc,
            CopiedUtc = e.File.Header.CreatedUtc,
            Sequence = labelSet.Sequence,
            FileNumber = e.File.FileNumber,
            NeededSequences = [.. e.Needed],
            StoredBytes = e.StoredBytes,
            Manifest = e.Manifest,
        })).ToList();
        foreach (var copy in copies)
        {
            var file = files.First(f => f.Sequence == copy.Sequence && f.FileNumber == copy.FileNumber);
            copy.FileStartBlock = file.StartBlock;
            copy.FileId = file.FileId;
        }

        db.TapeCopies.AddRange(copies);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        log.Info($"Set {labelSet.Id} of pool {pool.Name}: {highest} tape(s), {copies.Count} copie(s) back in the catalog (closed; kept {(pool.RetentionDays is { } d ? $"{d} days from now" : "for ever")}).");
        return copies.Count;
    }

    /// <summary>What the console calls a copy: a VM, a namespace or a cluster's objects; for SQL Server backups their kind.</summary>
    private static string? ObjectKindOf(string kind, string manifest)
    {
        try
        {
            if (kind == TapeFileTypes.Sql)
            {
                var sql = JsonSerializer.Deserialize(manifest, SqlBackupJsonContext.Default.SqlBackupManifest);
                return sql is null ? null : $"sql-{sql.Kind}".ToLowerInvariant();
            }

            var point = JsonSerializer.Deserialize(manifest, CoreJsonContext.Default.RestorePointManifest);
            return point?.Kubernetes switch
            {
                null => RestorePointObjectKinds.Vm,
                { ClusterScope: true } => RestorePointObjectKinds.KubernetesCluster,
                _ => RestorePointObjectKinds.KubernetesNamespace,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]?> KekAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Fingerprint == fingerprint, cancellationToken).ConfigureAwait(false);
        return key is null ? null : Convert.FromBase64String(protector.Unprotect(key.ProtectedKek));
    }

    private sealed record ExtraCopy(TapeFileFound File, TapeCopyInfo Info, string Manifest, IReadOnlyList<int> Needed, long StoredBytes);

    /// <summary>The loaded tape only: copy files are read where they are.</summary>
    private sealed class OneTape(TapeDrive drive, int sequence) : ITapeVolumes
    {
        public async Task<DriveBlocks> AtAsync(int wanted, long block, CancellationToken cancellationToken)
        {
            if (wanted != sequence)
            {
                throw new InvalidOperationException($"Only tape #{sequence} is loaded.");
            }

            await drive.LocateBlockAsync(block, cancellationToken).ConfigureAwait(false);
            return new DriveBlocks(drive);
        }
    }
}
