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
    RepositoryManager repositories,
    CredentialProtector protector,
    SessionEventWriter events,
    ServerPaths paths,
    IOptions<TapeOptions> options,
    TimeProvider time)
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
        if (library.ChangerSerial is null)
        {
            throw new InvalidOperationException($"Pool {pool.Name} uses the standalone drive {library.Name}; tape jobs need a library with a changer in this version.");
        }

        var (kek, kdf, fingerprint) = await KeyAsync(pool, cancellationToken).ConfigureAwait(false);
        await ExpireSetsAsync(pool, log, cancellationToken).ConfigureAwait(false);
        var set = await OpenSetAsync(pool, fingerprint, log, cancellationToken).ConfigureAwait(false);
        var points = await SelectAsync(job, set, log, cancellationToken).ConfigureAwait(false);
        if (points.Count == 0)
        {
            return (log.Warned ? SessionState.Warning : SessionState.Succeeded, "Nothing to copy: the set has every restore point the job asks for.");
        }

        log.Info($"{points.Count} restore point(s) to copy into set {set.Id} of pool {pool.Name} ({string.Join(", ", points.Select(p => p.VmName).Distinct())}).");
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
        await using (var writer = new TapeSetWriter(context, dbFactory, libraries, robot, options.Value, time))
        {
            foreach (var point in points)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Core.Repository.IBackupRepository repository;
                try
                {
                    repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
                {
                    log.Warning($"{point.VmName}: its repository cannot be read ({ex.Message}); it is copied with the next run.");
                    failed++;
                    continue;
                }

                await writer.CopyRestorePointAsync(point, repository, cancellationToken).ConfigureAwait(false);
            }

            result = await writer.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        var summary = $"{result.Copied} restore point(s) copied to {string.Join(", ", result.Tapes.Distinct())}: {VmBackupTask.Format(result.NewBytes)} new, {result.ReusedChunks} chunk(s) the set had already." +
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

    /// <summary>The pool's open set, or a new one when the policy asks for it (the old one closes).</summary>
    private async Task<MediaSetEntity> OpenSetAsync(MediaPoolEntity pool, string? fingerprint, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var open = await db.MediaSets.FirstOrDefaultAsync(s => s.PoolId == pool.Id && s.State == MediaSetState.Open, cancellationToken).ConfigureAwait(false);
        if (open is not null && (NeedsNewSet(pool, open, now) || open.KeyFingerprint != fingerprint))
        {
            open.State = MediaSetState.Closed;
            open.ClosedUtc = now;
            open.ExpiresUtc = pool.RetentionDays is { } days ? now.AddDays(days) : null;
            open.IndexSegments = null;
            foreach (var medium in await db.TapeMedia.Where(m => m.SetId == open.Id && m.State == TapeMediumState.Appendable).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                medium.State = TapeMediumState.Full;
            }

            OpenSetIndex.Delete(IndexDirectory(open.Id));
            log.Info($"Set {open.Id} closed ({pool.NewSet} sets); it is kept {(pool.RetentionDays is { } d ? $"{d} days, until {open.ExpiresUtc:yyyy-MM-dd}" : "for ever")}.");
            open = null;
        }

        if (open is null)
        {
            open = new MediaSetEntity { PoolId = pool.Id, State = MediaSetState.Open, CreatedUtc = now, KeyFingerprint = fingerprint, IndexSegments = [] };
            db.MediaSets.Add(open);
            log.Info($"New set {open.Id} for pool {pool.Name}{(fingerprint is null ? string.Empty : $", encrypted with key {fingerprint}")}.");
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return open;
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
