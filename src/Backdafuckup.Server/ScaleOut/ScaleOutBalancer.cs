using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>A rebalance plan with the extents it was made from.</summary>
public sealed record RebalancePlan(IReadOnlyList<PlannerExtent> Extents, IReadOnlyList<PlannedMove> Moves, string? Trigger, IReadOnlyDictionary<Guid, string> ExtentNames);

/// <summary>
/// Looks after scale-out repositories in the background: records which extents are online and their space, deletes the
/// copies moves left behind once nobody reads them, keeps evacuations going, and rebalances automatically in the window.
/// </summary>
public sealed class ScaleOutBalancer(
    IDbContextFactory<AppDbContext> dbFactory,
    IVolumeSpace volumes,
    ChainLocks locks,
    ChainMover mover,
    SessionQueue queue,
    TimeProvider time,
    IOptions<ScaleOutOptions> options,
    ILogger<ScaleOutBalancer> logger) : BackgroundService
{
    /// <summary>Copies of a move that never switched (its session gone) are abandoned after this long.</summary>
    private static readonly TimeSpan StaleCopies = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.BalancerInterval <= TimeSpan.Zero)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Looking after the scale-out repositories failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(options.Value.BalancerInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One round: health, cleanup, evacuations, automatic rebalancing.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await CheckExtentsAsync(cancellationToken).ConfigureAwait(false);
        await AbandonStaleCopiesAsync(cancellationToken).ConfigureAwait(false);
        await mover.CleanUpAsync(null, null, cancellationToken).ConfigureAwait(false);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scaleOuts = await db.Repositories.AsNoTracking().Where(r => r.Kind == RepositoryKind.ScaleOut).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var scaleOut in scaleOuts)
        {
            if (await db.Sessions.AnyAsync(s => s.Kind == SessionKind.RepositoryMove && s.RepositoryId == scaleOut.Id
                    && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // An evacuation goes on until the extent is empty.
            var evacuating = await db.Repositories.AsNoTracking().Where(r => r.ScaleOutId == scaleOut.Id && r.ExtentState == ExtentState.Evacuating && !r.Maintenance).ToListAsync(cancellationToken).ConfigureAwait(false);
            var queued = false;
            foreach (var extent in evacuating)
            {
                if (await db.RestorePoints.AnyAsync(r => r.RepositoryId == extent.Id, cancellationToken).ConfigureAwait(false)
                    || await db.SqlBackups.AnyAsync(b => b.RepositoryId == extent.Id, cancellationToken).ConfigureAwait(false))
                {
                    await queue.EnqueueMoveAsync(new RepositoryMoveRequest(scaleOut.Id, RepositoryMoveRequest.Evacuate, [], extent.Id), $"Evacuate {extent.Name}", "Evacuation continues", cancellationToken).ConfigureAwait(false);
                    queued = true;
                    break;
                }
            }

            var settings = scaleOut.ScaleOutSettings ?? new ScaleOutSettings();
            if (queued || !settings.AutoRebalance || ScaleOutWindows.EndIfInside(settings.Window, time.GetUtcNow()) is not { } end)
            {
                continue;
            }

            var plan = await PlanAsync(scaleOut.Id, cancellationToken).ConfigureAwait(false);
            if (plan.Trigger is null || plan.Moves.Count == 0)
            {
                continue;
            }

            logger.LogInformation("Automatic rebalance of {ScaleOut}: {Trigger}; {Moves} chain(s) to move.", scaleOut.Name, plan.Trigger, plan.Moves.Count);
            await queue.EnqueueMoveAsync(
                new RepositoryMoveRequest(scaleOut.Id, RepositoryMoveRequest.Automatic,
                    [.. plan.Moves.Select(m => new ChainMoveRequest(m.Chain.JobId, m.Chain.ObjectNativeId, m.Chain.ExtentId, m.ToExtentId))], DeadlineUtc: end, MaxBytes: settings.MaxBytesPerRun),
                $"Rebalance {scaleOut.Name}", $"Automatic rebalance: {plan.Trigger}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A rebalance plan from the extents' space now (minus what moves still have to free) and the chains' sizes. Chains
    /// that move, run, were moved recently or are due within the hour stay.
    /// </summary>
    public async Task<RebalancePlan> PlanAsync(Guid scaleOutId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var scaleOut = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == scaleOutId && r.Kind == RepositoryKind.ScaleOut, cancellationToken).ConfigureAwait(false)
            ?? throw new ScaleOutRequestException($"Scale-out repository {scaleOutId} does not exist.", StatusCodes.Status404NotFound);
        var settings = scaleOut.ScaleOutSettings ?? new ScaleOutSettings();
        var extentRows = await db.Repositories.AsNoTracking().Where(r => r.ScaleOutId == scaleOutId).OrderBy(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Space a source gets back only after cleanup and garbage collection is counted as free already.
        var pending = await db.RepositoryMoveItems.Where(m => m.ScaleOutId == scaleOutId && m.State == MoveItemState.Switched)
            .GroupBy(m => m.SourceExtentId).Select(g => new { g.Key, Bytes = g.Sum(m => m.BytesWritten) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var extents = extentRows.Select(e =>
        {
            var space = volumes.Probe(e.Path);
            var free = (space?.Free ?? 0) + (pending.FirstOrDefault(p => p.Key == e.Id)?.Bytes ?? 0);
            return new PlannerExtent(e.Id, e.Name, space?.Total ?? 0, Math.Min(free, space?.Total ?? 0), space is not null && e.ExtentState == ExtentState.Active && !e.Maintenance);
        }).ToList();

        var now = time.GetUtcNow();
        var moving = await db.Sessions.Where(s => s.Kind == SessionKind.RepositoryMove && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping))
            .Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var due = await db.Jobs.Where(j => j.RepositoryId == scaleOutId && j.NextRunUtc != null && j.NextRunUtc < now.AddHours(1)).Select(j => j.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var chains = (await ScaleOutChains.LoadAsync(db, scaleOutId, cancellationToken).ConfigureAwait(false))
            .Where(c => c.JobId is not null)
            .Select(c =>
            {
                var home = c.Placement?.ExtentId ?? c.Parts.OrderByDescending(p => p.Bytes).Select(p => p.ExtentId).FirstOrDefault();
                var part = c.Parts.FirstOrDefault(p => p.ExtentId == home);
                var pinned = (c.Placement?.MoveSessionId is { } session && moving.Contains(session))
                    || locks.Holder(c.JobId!.Value, c.ObjectNativeId) is not null
                    || due.Contains(c.JobId.Value)
                    || (c.Placement is { Reason: "moved" } p && p.PlacedUtc > now.AddDays(-settings.CooldownDays));
                return new PlannerChain(c.JobId, c.ObjectNativeId, c.ObjectName, home, part?.Bytes ?? 0, pinned);
            })
            .Where(c => c.ExtentId != Guid.Empty)
            .ToList();
        var trigger = RebalancePlanner.Trigger(extents, settings);
        var moves = RebalancePlanner.Plan(extents, chains, settings);
        return new RebalancePlan(extents, moves, trigger, extentRows.ToDictionary(e => e.Id, e => e.Name));
    }

    /// <summary>Records which extents are reachable, with their size and free space (the console and placement read it).</summary>
    private async Task CheckExtentsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var extents = await db.Repositories.Where(r => r.ScaleOutId != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var extent in extents)
        {
            var space = volumes.Probe(extent.Path);
            var state = space is null ? RepositoryState.Offline : RepositoryState.Online;
            var error = space is null ? $"{extent.Path} cannot be reached" : null;
            if (extent.State != state || extent.LastError != error)
            {
                if (state != extent.State)
                {
                    logger.Log(state == RepositoryState.Online ? LogLevel.Information : LogLevel.Warning, "Extent {Extent} ({Path}) is {State}.", extent.Name, extent.Path, state);
                }

                await db.Repositories.Where(r => r.Id == extent.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.State, state).SetProperty(r => r.LastError, error)
                    .SetProperty(r => r.TotalBytes, space == null ? extent.TotalBytes : space.Value.Total)
                    .SetProperty(r => r.FreeBytes, space == null ? extent.FreeBytes : space.Value.Free), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Copies of moves that never switched and whose session is gone are abandoned (then deleted by the cleanup).</summary>
    private async Task AbandonStaleCopiesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var running = await db.Sessions.Where(s => s.Kind == SessionKind.RepositoryMove && (s.State == SessionState.Running || s.State == SessionState.Queued || s.State == SessionState.Stopping))
            .Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var before = time.GetUtcNow() - StaleCopies;
        await db.RepositoryMoveItems.Where(m => m.State == MoveItemState.Copied && m.UpdatedUtc < before && !running.Contains(m.SessionId))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.State, MoveItemState.Abandoned).SetProperty(m => m.UpdatedUtc, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }
}
