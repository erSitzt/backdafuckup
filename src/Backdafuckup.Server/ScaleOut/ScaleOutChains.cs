using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>What of one chain is on one extent.</summary>
internal sealed record ChainPart(Guid ExtentId, int RestorePoints, int SqlBackups, long Bytes, DateTimeOffset? NewestUtc);

/// <summary>A chain of a scale-out repository (a job's data of one object; job null for points without a job), where it is and where it goes.</summary>
internal sealed record ScaleOutChain(Guid? JobId, string ObjectNativeId, string ObjectName, IReadOnlyList<ChainPart> Parts, ChainPlacementEntity? Placement)
{
    public long Bytes => Parts.Sum(p => p.Bytes);

    public DateTimeOffset? NewestUtc => Parts.Max(p => p.NewestUtc);
}

/// <summary>The chains of a scale-out repository, from its restore points, SQL Server backups and placements.</summary>
internal static class ScaleOutChains
{
    public static async Task<List<ScaleOutChain>> LoadAsync(AppDbContext db, Guid scaleOutId, CancellationToken cancellationToken)
    {
        var extentIds = await db.Repositories.Where(r => r.ScaleOutId == scaleOutId).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var points = await db.RestorePoints.AsNoTracking()
            .Where(r => extentIds.Contains(r.RepositoryId) && r.State != RestorePointState.Creating)
            .GroupBy(r => new { r.JobId, r.VmNativeId, r.RepositoryId })
            .Select(g => new { g.Key.JobId, g.Key.VmNativeId, g.Key.RepositoryId, Count = g.Count(), Bytes = g.Sum(r => r.NewStoredBytes), Newest = g.Max(r => r.CreatedUtc), Name = g.OrderByDescending(r => r.CreatedUtc).Select(r => r.VmName).First() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var sql = await db.SqlBackups.AsNoTracking()
            .Where(b => extentIds.Contains(b.RepositoryId))
            .GroupBy(b => new { b.JobId, b.VmNativeId, b.RepositoryId })
            .Select(g => new { g.Key.JobId, g.Key.VmNativeId, g.Key.RepositoryId, Count = g.Count(), Bytes = g.Sum(b => b.NewStoredBytes), Newest = g.Max(b => b.CreatedUtc), Name = g.OrderByDescending(b => b.CreatedUtc).Select(b => b.VmName).First() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var placements = await db.ChainPlacements.AsNoTracking().Where(p => p.ScaleOutId == scaleOutId).ToListAsync(cancellationToken).ConfigureAwait(false);

        var keys = points.Select(p => (p.JobId, p.VmNativeId)).Concat(sql.Select(s => (s.JobId, s.VmNativeId)))
            .Concat(placements.Select(p => ((Guid?)p.JobId, p.ObjectNativeId))).Distinct().ToList();
        var chains = new List<ScaleOutChain>(keys.Count);
        foreach (var (jobId, objectId) in keys)
        {
            var parts = extentIds.Select(e =>
            {
                var p = points.FirstOrDefault(x => x.JobId == jobId && x.VmNativeId == objectId && x.RepositoryId == e);
                var s = sql.FirstOrDefault(x => x.JobId == jobId && x.VmNativeId == objectId && x.RepositoryId == e);
                return p is null && s is null ? null : new ChainPart(e, p?.Count ?? 0, s?.Count ?? 0, (p?.Bytes ?? 0) + (s?.Bytes ?? 0), Max(p?.Newest, s?.Newest));
            }).OfType<ChainPart>().ToList();
            var name = points.Where(x => x.JobId == jobId && x.VmNativeId == objectId).OrderByDescending(x => x.Newest).Select(x => x.Name).FirstOrDefault()
                ?? sql.Where(x => x.JobId == jobId && x.VmNativeId == objectId).Select(x => x.Name).FirstOrDefault()
                ?? objectId;
            chains.Add(new ScaleOutChain(jobId, objectId, name, parts, placements.FirstOrDefault(p => p.JobId == jobId && p.ObjectNativeId == objectId)));
        }

        return chains;
    }

    public static async Task<ChainPageDto> ListAsync(AppDbContext db, ChainLocks locks, Guid scaleOutId, Guid? extentId, string? q, int offset, int limit, CancellationToken cancellationToken)
    {
        if (!await db.Repositories.AnyAsync(r => r.Id == scaleOutId && r.Kind == RepositoryKind.ScaleOut, cancellationToken).ConfigureAwait(false))
        {
            throw new ScaleOutRequestException($"Scale-out repository {scaleOutId} does not exist.", StatusCodes.Status404NotFound);
        }

        var names = await db.Repositories.Where(r => r.ScaleOutId == scaleOutId).ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken).ConfigureAwait(false);
        var jobs = await db.Jobs.ToDictionaryAsync(j => j.Id, j => j.Name, cancellationToken).ConfigureAwait(false);
        var moving = await db.Sessions.Where(s => s.Kind == SessionKind.RepositoryMove && (s.State == SessionState.Running || s.State == SessionState.Queued || s.State == SessionState.Stopping))
            .Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chains = (await LoadAsync(db, scaleOutId, cancellationToken).ConfigureAwait(false))
            .Where(c => extentId is null || c.Parts.Any(p => p.ExtentId == extentId) || c.Placement?.ExtentId == extentId)
            .Where(c => string.IsNullOrWhiteSpace(q) || c.ObjectName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (c.JobId is { } j && jobs.TryGetValue(j, out var jn) && jn.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.ObjectName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.JobId)
            .ToList();
        var page = chains.Skip(offset).Take(limit).Select(c =>
        {
            var busy = c.Placement?.MoveSessionId is { } session && moving.Contains(session) ? "being moved"
                : c.JobId is { } job && locks.Holder(job, c.ObjectNativeId) is { } holder ? holder
                : null;
            return new ChainDto(
                c.JobId, c.JobId is { } id ? jobs.GetValueOrDefault(id) : null, c.ObjectNativeId, c.ObjectName,
                c.Placement?.ExtentId, c.Placement is { } p ? names.GetValueOrDefault(p.ExtentId) : null, c.Placement?.Reason, c.Placement?.PlacedUtc,
                [.. c.Parts.Select(p => new ChainExtentDto(p.ExtentId, names.GetValueOrDefault(p.ExtentId) ?? p.ExtentId.ToString(), p.RestorePoints, p.SqlBackups, p.Bytes))],
                c.NewestUtc, busy);
        }).ToList();
        return new ChainPageDto(page, chains.Count);
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
