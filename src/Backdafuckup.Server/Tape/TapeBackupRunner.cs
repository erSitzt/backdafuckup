using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Format;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

/// <summary>A queued run of a tape job (the session's parameters).</summary>
public sealed record TapeBackupRequest(Guid JobId);

/// <summary>
/// Runs a tape job: decides the media set (the pool's policy), what to copy (the newest restore point per VM, or every
/// new one, that the set lacks), waits for a drive of the pool's library and writes the copies with
/// <see cref="TapeSetWriter"/>.
/// </summary>
public sealed class TapeBackupRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    TapeLibraries libraries,
    TapeLocks locks,
    TapeSessionRunner robot,
    CredentialProtector protector,
    SessionEventWriter events,
    ServerPaths paths,
    IOptions<TapeOptions> options,
    TimeProvider time,
    Telemetry.BackupMetrics metrics,
    TapeOperatorRequests requests,
    SessionTracker tracker,
    RestorePointAccess access)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<TapeBackupRequest>(session.Parameters ?? "null") ?? throw new InvalidOperationException("The tape session has no parameters.");
        TapeJobEntity job;
        MediaPoolEntity pool;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            job = await db.TapeJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == request.JobId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Tape job {request.JobId} does not exist any more.");
            pool = await db.MediaPools.AsNoTracking().FirstAsync(p => p.Id == job.PoolId, cancellationToken).ConfigureAwait(false);
        }

        var library = await libraries.LoadAsync(pool.LibraryId, cancellationToken).ConfigureAwait(false);
        var (kek, kdf, fingerprint) = await KeyAsync(pool, cancellationToken).ConfigureAwait(false);
        await ExpireSetsAsync(pool, log, cancellationToken).ConfigureAwait(false);
        var (set, closed) = await OpenSetAsync(pool, fingerprint, log, cancellationToken).ConfigureAwait(false);
        if (closed is not null)
        {
            await ExportClosedAsync(pool, library, closed.Value, $"Tape job {job.Name}", log, cancellationToken).ConfigureAwait(false);
        }

        var points = await SelectAsync(job, set, log, cancellationToken).ConfigureAwait(false);
        var sqlBackups = job.IncludeSqlBackups ? await SelectSqlAsync(set, points, cancellationToken).ConfigureAwait(false) : [];
        if (points.Count == 0 && sqlBackups.Count == 0)
        {
            return (log.Warned ? SessionState.Warning : SessionState.Succeeded, "Nothing to copy: the set has every restore point the job asks for.");
        }

        log.Info($"{points.Count} restore point(s) and {sqlBackups.Count} SQL Server backup(s) to copy into set {set.Id} of pool {pool.Name} ({string.Join(", ", points.Select(p => p.VmName).Concat(sqlBackups.Select(s => s.VmName)).Distinct())}).");
        var holder = $"Tape job {job.Name}";
        var drive = library.Drives.OrderBy(d => d.ElementAddress).First();
        foreach (var candidate in library.Drives.OrderBy(d => d.ElementAddress))
        {
            if (locks.Drive(candidate.Id).Snapshot().Holder is null)
            {
                drive = candidate;
                break;
            }
        }

        await using var lease = await locks.Drive(drive.Id).AcquireAsync(holder, h => log.Info($"Waiting for {drive.Name}: '{h}' uses it."), cancellationToken).ConfigureAwait(false);
        var index = new OpenSetIndex(IndexDirectory(set.Id), set.IndexSegments ?? []);
        var context = new TapeWriteContext(library, drive, pool, set, kek, kdf, fingerprint, index, holder, session.Id, log);
        TapeWriteResult result;
        var failed = 0;
        await using (var writer = new TapeSetWriter(context, dbFactory, libraries, robot, requests, options.Value, time))
        {
            // One task per restore point (its progress the bytes written of those the set lacked), one for the SQL Server backups.
            TaskProgress? current = null;
            try
            {
                foreach (var point in points)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = await TaskProgress.BeginAsync(tracker, session.Id, point.VmNativeId, point.VmName, time, cancellationToken).ConfigureAwait(false);
                    // Pinned where the catalog says now: the rows were read at the start, and the point may have moved since.
                    PinnedObject pinned;
                    try
                    {
                        pinned = await access.PinRestorePointAsync(point, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log.Warning($"{point.VmName}: its repository cannot be read ({ex.Message}); it is copied with the next run.");
                        await current.FinishAsync(SessionState.Failed, $"Its repository cannot be read: {ex.Message}").ConfigureAwait(false);
                        failed++;
                        continue;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                    {
                        log.Warning($"{point.VmName}: {ex.Message} It is not copied.");
                        await current.FinishAsync(SessionState.Failed, ex.Message).ConfigureAwait(false);
                        failed++;
                        continue;
                    }

                    using (pinned)
                    {
                        await writer.CopyRestorePointAsync(point, pinned.Repository, current.Report, cancellationToken).ConfigureAwait(false);
                    }

                    await current.FinishAsync(SessionState.Succeeded, null).ConfigureAwait(false);
                }

                // SQL Server backups after the restore points they belong to, oldest first, so that a log chain on tape grows in order.
                if (sqlBackups.Count > 0)
                {
                    current = await TaskProgress.BeginAsync(tracker, session.Id, "sql-backups", $"SQL Server backups ({sqlBackups.Count})", time, cancellationToken).ConfigureAwait(false);
                    var done = 0;
                    string? skipped = null;
                    foreach (var backup in sqlBackups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        PinnedObject pinned;
                        try
                        {
                            pinned = await access.PinSqlBackupAsync(backup.Id, backup.RepositoryId, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            log.Warning($"{backup.VmName}: the repository of its SQL Server backups cannot be read ({ex.Message}); they are copied with the next run.");
                            skipped = $"{sqlBackups.Count - done} not copied: the repository cannot be read ({ex.Message}).";
                            failed++;
                            break;
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                        {
                            log.Info($"{backup.VmName}: {ex.Message} It is not copied.");
                            current.Report(++done, sqlBackups.Count);
                            continue;
                        }

                        using (pinned)
                        {
                            await writer.CopySqlBackupAsync(backup, pinned.Repository, cancellationToken).ConfigureAwait(false);
                        }

                        current.Report(++done, sqlBackups.Count);
                    }

                    await current.FinishAsync(skipped is null ? SessionState.Succeeded : SessionState.Warning, skipped).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (current is { Finished: false })
            {
                await current.FinishAsync(cancellationToken.IsCancellationRequested ? SessionState.Stopped : SessionState.Failed, ex.Message).ConfigureAwait(false);
                throw;
            }

            result = await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        metrics.RecordTape(pool.Name, result.BytesWritten, 0);
        if (pool.Export == TapeExport.AfterEveryRun)
        {
            await robot.ExportFromSlotsAsync(library, result.Tapes.Distinct().ToList(), holder, log, cancellationToken).ConfigureAwait(false);
        }

        var summary = $"{result.Copied} restore point(s){(result.SqlCopied > 0 ? $" and {result.SqlCopied} SQL Server backup(s)" : string.Empty)} copied to {string.Join(", ", result.Tapes.Distinct())}: {VmBackupTask.Format(result.NewBytes)} new, {result.ReusedChunks} chunk(s) the set had already." +
            (failed > 0 ? $" {failed} not copied." : string.Empty);
        log.Info(summary);
        return (log.Warned || failed > 0 ? SessionState.Warning : SessionState.Succeeded, summary);
    }

    internal string IndexDirectory(Guid setId) => Path.Combine(paths.DataDirectory, "tape", "open-sets", setId.ToString("N"));

    /// <summary>The pool's key (its KEK), when it encrypts.</summary>
    private async Task<(byte[]? Kek, TapeKdfParameters? Kdf, string? Fingerprint)> KeyAsync(MediaPoolEntity pool, CancellationToken cancellationToken)
    {
        if (pool.Encryption == TapeEncryption.None)
        {
            return (null, null, null);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.TapeKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == pool.KeyId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Pool {pool.Name} encrypts, but its key is gone.");
        return (Convert.FromBase64String(protector.Unprotect(key.ProtectedKek)), new TapeKdfParameters(key.MemoryKiB, key.Iterations, key.Parallelism, Convert.FromBase64String(key.Salt)), key.Fingerprint);
    }

    /// <summary>Closed sets past their retention expire; their tapes can be labelled again (WORM tapes never).</summary>
    private async Task ExpireSetsAsync(MediaPoolEntity pool, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var expired = await db.MediaSets.Where(s => s.PoolId == pool.Id && s.State == MediaSetState.Closed && s.ExpiresUtc != null && s.ExpiresUtc <= now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var set in expired)
        {
            set.State = MediaSetState.Expired;
            var media = await db.TapeMedia.Where(m => m.SetId == set.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var medium in media.Where(m => m.Kind != TapeMediumKind.Worm))
            {
                medium.State = TapeMediumState.Expired;
            }

            log.Info($"Set {set.Id} of pool {pool.Name} expired ({set.ExpiresUtc:yyyy-MM-dd}); {media.Count(m => m.Kind != TapeMediumKind.Worm)} tape(s) can be written again.");
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes the pool's open set now: the next run starts a new one. Its tapes are exported by the pool's rule.</summary>
    public async Task<bool> CloseOpenSetAsync(Guid poolId, IOperationLog log, CancellationToken cancellationToken)
    {
        MediaPoolEntity pool;
        Guid? closed;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            pool = await db.MediaPools.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poolId, cancellationToken).ConfigureAwait(false)
                ?? throw new TapeRequestException($"Media pool {poolId} does not exist.", StatusCodes.Status404NotFound);
            var open = await db.MediaSets.FirstOrDefaultAsync(s => s.PoolId == pool.Id && s.State == MediaSetState.Open, cancellationToken).ConfigureAwait(false);
            closed = open is null ? null : await CloseAsync(db, pool, open, log, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (closed is { } setId)
        {
            await ExportClosedAsync(pool, await libraries.LoadAsync(pool.LibraryId, cancellationToken).ConfigureAwait(false), setId, $"Close a set of {pool.Name}", log, cancellationToken).ConfigureAwait(false);
        }

        return closed is not null;
    }

    private async Task ExportClosedAsync(MediaPoolEntity pool, TapeLibraryEntity library, Guid setId, string holder, IOperationLog log, CancellationToken cancellationToken)
    {
        if (pool.Export != TapeExport.WhenSetCloses)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var barcodes = await db.TapeMedia.Where(m => m.SetId == setId && m.LibraryId == library.Id && m.Location == TapeLocation.Slot).OrderBy(m => m.Sequence).Select(m => m.Barcode)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await robot.ExportFromSlotsAsync(library, barcodes, holder, log, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> CloseAsync(AppDbContext db, MediaPoolEntity pool, MediaSetEntity open, IOperationLog log, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        open.State = MediaSetState.Closed;
        open.ClosedUtc = now;
        open.ExpiresUtc = pool.RetentionDays is { } days ? now.AddDays(days) : null;
        open.IndexSegments = null;
        foreach (var medium in await db.TapeMedia.Where(m => m.SetId == open.Id && m.State == TapeMediumState.Appendable).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            medium.State = TapeMediumState.Full;
        }

        OpenSetIndex.Delete(IndexDirectory(open.Id));
        log.Info($"Set {open.Id} of pool {pool.Name} closed; it is kept {(pool.RetentionDays is { } d ? $"{d} days, until {open.ExpiresUtc:yyyy-MM-dd}" : "for ever")}.");
        return open.Id;
    }

    /// <summary>The pool's open set, or a new one when the policy asks for it (the old one closes and is returned).</summary>
    private async Task<(MediaSetEntity Set, Guid? Closed)> OpenSetAsync(MediaPoolEntity pool, string? fingerprint, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var open = await db.MediaSets.FirstOrDefaultAsync(s => s.PoolId == pool.Id && s.State == MediaSetState.Open, cancellationToken).ConfigureAwait(false);
        Guid? closed = null;
        if (open is not null && (NeedsNewSet(pool, open, now) || open.KeyFingerprint != fingerprint))
        {
            closed = await CloseAsync(db, pool, open, log, cancellationToken).ConfigureAwait(false);
            open = null;
        }

        if (open is null)
        {
            open = new MediaSetEntity { PoolId = pool.Id, State = MediaSetState.Open, CreatedUtc = now, KeyFingerprint = fingerprint, IndexSegments = [] };
            db.MediaSets.Add(open);
            log.Info($"New set {open.Id} for pool {pool.Name}{(fingerprint is null ? string.Empty : $", encrypted with key {fingerprint}")}.");
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (open, closed);
    }

    internal static bool NeedsNewSet(MediaPoolEntity pool, MediaSetEntity open, DateTimeOffset now)
    {
        if (pool.NewSet == TapeNewSet.EveryRun)
        {
            return true;
        }

        if (pool.NewSet == TapeNewSet.Never)
        {
            return false;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(pool.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date;
        var start = pool.NewSet switch
        {
            TapeNewSet.Daily => day,
            TapeNewSet.Weekly => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            _ => new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
        };
        var startUtc = new DateTimeOffset(start, zone.GetUtcOffset(start));
        return open.CreatedUtc < startUtc;
    }

    /// <summary>
    /// The SQL Server backups (logs and fulls) of the VMs whose restore points are in the set or come with this run, from
    /// the oldest of those restore points on, that the set does not have yet: each run adds the newer logs, so that a
    /// restore from tape reaches as far as the last run.
    /// </summary>
    private async Task<List<SqlBackupEntity>> SelectSqlAsync(MediaSetEntity set, IReadOnlyList<RestorePointEntity> points, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var inSet = await db.TapeCopies.AsNoTracking().Where(c => c.SetId == set.Id).Select(c => new { c.Kind, c.OriginalId, c.VmId, c.RepositoryId, c.CreatedUtc })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // By logical repository: the extents of a scale-out count as one (a VM's points and logs may be on different ones).
        var logical = await ScaleOut.RepositoryGroups.LogicalAsync(db, cancellationToken).ConfigureAwait(false);
        Guid Logical(Guid id) => logical.GetValueOrDefault(id, id);
        var vms = inSet.Where(c => c.Kind == TapeFileTypes.RestorePoint).Select(c => (c.VmId, RepositoryId: Logical(c.RepositoryId), c.CreatedUtc))
            .Concat(points.Select(p => (VmId: p.VmNativeId, RepositoryId: Logical(p.RepositoryId), p.CreatedUtc)))
            .GroupBy(v => (v.VmId, v.RepositoryId))
            .Select(g => (g.Key.VmId, g.Key.RepositoryId, Since: g.Min(v => v.CreatedUtc)))
            .ToList();
        var copied = inSet.Where(c => c.Kind == TapeFileTypes.Sql).Select(c => c.OriginalId).ToHashSet();
        var result = new List<SqlBackupEntity>();
        foreach (var (vmId, repositoryId, since) in vms)
        {
            var members = logical.Where(l => l.Value == repositoryId).Select(l => l.Key).Append(repositoryId).Distinct().ToList();
            var backups = await db.SqlBackups.AsNoTracking()
                .Where(b => b.VmNativeId == vmId && members.Contains(b.RepositoryId) && b.State == SqlBackupState.Available && b.CreatedUtc >= since)
                .OrderBy(b => b.CreatedUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            result.AddRange(backups.Where(b => !copied.Contains(b.Id)));
        }

        return [.. result.OrderBy(b => b.CreatedUtc)];
    }

    /// <summary>The restore points of the source jobs that the set does not have: the newest per VM, or every one.</summary>
    private async Task<List<RestorePointEntity>> SelectAsync(TapeJobEntity job, MediaSetEntity set, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var sources = job.SourceJobIds;
        var points = await db.RestorePoints.AsNoTracking()
            .Where(r => r.JobId != null && sources.Contains(r.JobId.Value) && r.State == RestorePointState.Available)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var copied = (await db.TapeCopies.Where(c => c.SetId == set.Id).Select(c => c.OriginalId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var selected = job.Mode == TapeJobMode.EveryNew
            ? points.Where(p => !copied.Contains(p.Id))
            : points.GroupBy(p => (p.JobId, p.VmNativeId)).Select(g => g.OrderByDescending(p => p.CreatedUtc).First()).Where(p => !copied.Contains(p.Id));
        if (points.Count == 0)
        {
            log.Warning($"The source jobs of {job.Name} have no restore points.");
        }

        return selected.OrderBy(p => p.VmName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.CreatedUtc).ToList();
    }
}
