using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>One chain of a move request; a null target is the extent with the most room when the chain's turn comes.</summary>
public sealed record ChainMoveRequest(Guid? JobId, string ObjectNativeId, Guid FromExtentId, Guid? ToExtentId);

/// <summary>The parameters of a <see cref="SessionKind.RepositoryMove"/> session.</summary>
/// <param name="Mode">Manual, Rebalance, Automatic or Evacuate.</param>
/// <param name="DeadlineUtc">The end of the window: the session pauses between chains then, and the next window continues.</param>
public sealed record RepositoryMoveRequest(Guid ScaleOutId, string Mode, IReadOnlyList<ChainMoveRequest> Chains, Guid? EvacuateExtentId = null, DateTimeOffset? DeadlineUtc = null, long? MaxBytes = null)
{
    public const string Manual = "Manual";
    public const string Rebalance = "Rebalance";
    public const string Automatic = "Automatic";
    public const string Evacuate = "Evacuate";
}

/// <summary>
/// Runs a move session: chains one after another (each a task with its progress), by hand, from a rebalance plan, or all
/// chains of an extent being evacuated. It runs next to backups; one move session at a time.
/// </summary>
public sealed class RepositoryMoveRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    ChainMover mover,
    RepositoryPlacement placement,
    SessionEventWriter events,
    SessionTracker tracker,
    Telemetry.BackupMetrics metrics,
    TimeProvider time)
{
    public static string Serialize(RepositoryMoveRequest request) => JsonSerializer.Serialize(request);

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<RepositoryMoveRequest>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The move session has no parameters.");
        var (moved, deferred, failed, bytes) = (0, 0, 0, 0L);
        var done = new HashSet<(Guid?, string, Guid)>();
        var paused = false;
        for (var pass = 0; pass < (request.Mode == RepositoryMoveRequest.Evacuate ? 3 : 1); pass++)
        {
            var scope = await placement.ScopeAsync(request.ScaleOutId, cancellationToken).ConfigureAwait(false);
            var settings = scope.Settings;
            var work = request.Mode == RepositoryMoveRequest.Evacuate
                ? await EvacuationWorkAsync(request.EvacuateExtentId!.Value, request.ScaleOutId, cancellationToken).ConfigureAwait(false)
                : [.. request.Chains];
            work = [.. work.Where(c => !done.Contains((c.JobId, c.ObjectNativeId, c.FromExtentId)))];
            if (work.Count == 0)
            {
                break;
            }

            if (pass == 0)
            {
                log.Info($"{work.Count} chain(s) of {scope.Repository.Name} to move ({request.Mode.ToLowerInvariant()}).");
            }

            foreach (var item in work)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.DeadlineUtc is { } deadline && time.GetUtcNow() >= deadline)
                {
                    paused = true;
                    break;
                }

                if (request.MaxBytes is { } budget && bytes >= budget)
                {
                    log.Info($"The limit of {VmBackupTask.Format(budget)} per run is reached; the rest follows with the next run.");
                    paused = true;
                    break;
                }

                done.Add((item.JobId, item.ObjectNativeId, item.FromExtentId));
                var (chain, name) = await ResolveAsync(scope, item, cancellationToken).ConfigureAwait(false);
                if (chain is null)
                {
                    log.Warning(name);
                    failed++;
                    continue;
                }

                var task = await TaskProgress.BeginAsync(tracker, session.Id, item.ObjectNativeId, name, time, cancellationToken).ConfigureAwait(false);
                try
                {
                    var result = await mover.MoveAsync(chain, session.Id, settings, log, task.Report, cancellationToken).ConfigureAwait(false);
                    bytes += result.BytesCopied;
                    metrics.RecordMove(scope.Repository.Name, result.BytesCopied);
                    if (result.Deferred)
                    {
                        deferred++;
                        await task.FinishAsync(SessionState.Warning, result.Summary).ConfigureAwait(false);
                    }
                    else
                    {
                        moved++;
                        await task.FinishAsync(SessionState.Succeeded, null).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    log.Warning($"{chain.ObjectName}: not moved: {ex.Message}");
                    await task.FinishAsync(SessionState.Failed, ex.Message).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await task.FinishAsync(SessionState.Stopped, "Stopped").ConfigureAwait(false);
                    throw;
                }
            }

            if (paused)
            {
                break;
            }
        }

        var summary = $"{moved} chain(s) moved, {VmBackupTask.Format(bytes)} copied" +
            (deferred > 0 ? $"; {deferred} left for the next run (their backup was running)" : string.Empty) +
            (failed > 0 ? $"; {failed} not moved (see the log)" : string.Empty) +
            (paused ? "; paused (window end or limit), the rest continues with the next run" : string.Empty) + ".";
        if (request.Mode == RepositoryMoveRequest.Evacuate && !paused)
        {
            summary = await FinishEvacuationAsync(request.EvacuateExtentId!.Value, summary, log, cancellationToken).ConfigureAwait(false);
        }

        log.Info(summary);
        return (failed > 0 && moved == 0 ? SessionState.Failed : failed > 0 || deferred > 0 || log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
    }

    /// <summary>The chain to move, its target chosen now when the request left it open; or null and why not.</summary>
    private async Task<(ChainToMove? Chain, string Name)> ResolveAsync(RepositoryScope scope, ChainMoveRequest item, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var name = await db.RestorePoints.Where(r => r.JobId == item.JobId && r.VmNativeId == item.ObjectNativeId).OrderByDescending(r => r.CreatedUtc).Select(r => r.VmName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? await db.SqlBackups.Where(b => b.JobId == item.JobId && b.VmNativeId == item.ObjectNativeId).Select(b => b.VmName).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? item.ObjectNativeId;
        if (scope.Extent(item.FromExtentId) is not { } from)
        {
            return (null, $"{name}: extent {item.FromExtentId} is not an extent of {scope.Repository.Name}.");
        }

        BackupRepositoryEntity? to;
        if (item.ToExtentId is { } chosen)
        {
            to = scope.Extent(chosen);
        }
        else
        {
            var estimate = await db.RestorePoints.Where(r => r.JobId == item.JobId && r.VmNativeId == item.ObjectNativeId && r.RepositoryId == item.FromExtentId && r.State == RestorePointState.Available)
                .SumAsync(r => r.NewStoredBytes, cancellationToken).ConfigureAwait(false);
            to = placement.Choose(scope, estimate, out var refusals);
            if (to is null)
            {
                return (null, $"{name}: no extent can take it: {string.Join("; ", refusals)}.");
            }
        }

        if (to is null)
        {
            return (null, $"{name}: the target is not an extent of {scope.Repository.Name}.");
        }

        return (new ChainToMove(scope.Repository.Id, item.JobId, item.ObjectNativeId, name, from.Id, to.Id), $"{name} → {to.Name}");
    }

    /// <summary>Every chain with data on the extent: those of jobs as chains, restore points of no job one by one.</summary>
    private async Task<List<ChainMoveRequest>> EvacuationWorkAsync(Guid extentId, Guid scaleOutId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var chains = await ScaleOutChains.LoadAsync(db, scaleOutId, cancellationToken).ConfigureAwait(false);
        return [.. chains.Where(c => c.Parts.Any(p => p.ExtentId == extentId))
            .OrderByDescending(c => c.Parts.First(p => p.ExtentId == extentId).Bytes)
            .Select(c => new ChainMoveRequest(c.JobId, c.ObjectNativeId, extentId, null))];
    }

    /// <summary>When nothing is left on the evacuated extent: it stays sealed and can be removed.</summary>
    private async Task<string> FinishEvacuationAsync(Guid extentId, string summary, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var extent = await db.Repositories.FirstAsync(r => r.Id == extentId, cancellationToken).ConfigureAwait(false);
        var points = await db.RestorePoints.CountAsync(r => r.RepositoryId == extentId, cancellationToken).ConfigureAwait(false);
        var sql = await db.SqlBackups.CountAsync(b => b.RepositoryId == extentId, cancellationToken).ConfigureAwait(false);
        if (points + sql > 0)
        {
            return summary + $" {extent.Name} still holds {points} restore point(s) and {sql} SQL Server backup(s); the evacuation continues later.";
        }

        await db.ChainPlacements.Where(p => p.ExtentId == extentId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (extent.ExtentState == ExtentState.Evacuating)
        {
            extent.ExtentState = ExtentState.Sealed;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        log.Info($"{extent.Name} ({extent.Path}) is evacuated: it holds nothing any more and stays sealed; it can be removed once its last copies are cleaned up.");
        return summary + $" {extent.Name} is evacuated.";
    }
}
