using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.ScaleOut;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>Scale-out repositories: extents, their state, settings, chains, moves and rebalancing.</summary>
public static class ScaleOutEndpoints
{
    public static void MapScaleOutEndpoints(this WebApplication app)
    {
        var repositories = app.MapGroup("/api/repositories");

        repositories.MapPost("/{id:guid}/extents", (Guid id, AddExtentDto request, ScaleOutAdmin admin, RepositoryManager manager, AppDbContext db, TimeProvider time, Sql.SqlCatalogBackfill backfill, CancellationToken ct) => Run(async () =>
        {
            var (scaleOut, extent, existing) = await admin.AddExtentAsync(id, request, ct);
            if (existing)
            {
                var opened = await manager.GetAsync(extent.Id, ct);
                await InfrastructureEndpoints.ImportRestorePointsAsync(extent.Id, opened, db, time, ct);
                await backfill.RecordSnapshotFullsAsync(ct);
            }

            return Results.Ok(new { scaleOutId = scaleOut.Id, extentId = extent.Id });
        }))
        .WithDescription(
            "Adds a directory (on another volume) as an extent of a scale-out repository. On a standalone repository it converts it: the repository becomes the first " +
            "extent of a new scale-out repository (ScaleOutName), its jobs write to the scale-out from then on, and its chains stay where they are. Extents need equal chunk sizes.");

        repositories.MapPut("/{id:guid}/extent", (Guid id, ExtentStateDto request, ScaleOutAdmin admin, CancellationToken ct) => Run(async () =>
        {
            var waiting = await admin.SetExtentStateAsync(id, request, ct);
            return waiting is null ? Results.NoContent() : Results.Accepted(value: new { message = waiting });
        }))
        .WithDescription(
            "Seals or unseals an extent (sealed: no new chains, a chain's next backup starts anew on another extent) and switches its maintenance (nothing is read or " +
            "written; chains there start anew elsewhere, or are skipped, as the scale-out's settings say). 202 while it waits for readers or writers to finish.");

        repositories.MapPut("/{id:guid}/settings", (Guid id, ScaleOutSettingsDto request, AppDbContext db, CancellationToken ct) => Run(async () =>
        {
            var scaleOut = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id && r.Kind == RepositoryKind.ScaleOut, ct)
                ?? throw new ScaleOutRequestException($"Scale-out repository {id} does not exist.", StatusCodes.Status404NotFound);
            scaleOut.ScaleOutSettings = Validate(request);
            await db.SaveChangesAsync(ct);
            return Results.Ok(scaleOut.ScaleOutSettings.ToDto());
        }));

        repositories.MapPost("/{id:guid}/evacuate", (Guid id, AppDbContext db, SessionQueue queue, CancellationToken ct) => Run(async () =>
        {
            var extent = await ExtentAsync(db, id, ct);
            if (!await db.Repositories.AnyAsync(r => r.ScaleOutId == extent.ScaleOutId && r.Id != id && r.ExtentState == ExtentState.Active && !r.Maintenance, ct))
            {
                throw new ScaleOutRequestException($"{extent.Name} cannot be evacuated: its scale-out repository has no other active extent to take its chains.", StatusCodes.Status409Conflict);
            }

            if (extent.Maintenance)
            {
                throw new ScaleOutRequestException($"{extent.Name} is in maintenance; nothing can be read from it. Switch maintenance off first.", StatusCodes.Status409Conflict);
            }

            extent.ExtentState = ExtentState.Evacuating;
            await db.SaveChangesAsync(ct);
            try
            {
                var session = await queue.EnqueueMoveAsync(new RepositoryMoveRequest(extent.ScaleOutId!.Value, RepositoryMoveRequest.Evacuate, [], id), $"Evacuate {extent.Name}", "User", ct);
                return Results.Ok(new StartedDto(session));
            }
            catch (InvalidOperationException)
            {
                // Another move runs: the balancer starts the evacuation when it has finished.
                return Results.Accepted(value: new { message = $"{extent.Name} is evacuated when the move running now has finished." });
            }
        }))
        .WithDescription("Moves every chain off the extent (to the active extents with the most room), then leaves it sealed and empty, so that it can be removed. New chains no longer go there at once; its chains keep their backups there until each is switched.");

        repositories.MapDelete("/{id:guid}/evacuate", (Guid id, AppDbContext db, SessionQueue queue, CancellationToken ct) => Run(async () =>
        {
            var extent = await ExtentAsync(db, id, ct);
            if (extent.ExtentState != ExtentState.Evacuating)
            {
                throw new ScaleOutRequestException($"{extent.Name} is not being evacuated.", StatusCodes.Status409Conflict);
            }

            extent.ExtentState = ExtentState.Sealed;
            await db.SaveChangesAsync(ct);
            var running = await db.Sessions.Where(s => s.Kind == SessionKind.RepositoryMove && s.RepositoryId == extent.ScaleOutId
                && (s.State == SessionState.Queued || s.State == SessionState.Running) && s.Parameters != null && s.Parameters.Contains(id.ToString())).Select(s => s.Id).ToListAsync(ct);
            foreach (var session in running)
            {
                await queue.RequestStopAsync(session, ct);
            }

            return Results.NoContent();
        }))
        .WithDescription("Stops evacuating; the extent stays sealed (chains moved so far stay where they went).");

        repositories.MapPost("/{id:guid}/rebalance/plan", (Guid id, ScaleOutBalancer balancer, CancellationToken ct) => Run(async () =>
        {
            var plan = await balancer.PlanAsync(id, ct);
            var after = RebalancePlanner.FreeAfter(plan.Extents, plan.Moves);
            return Results.Ok(new RebalancePlanDto(
                [.. plan.Moves.Select(m => new PlannedMoveDto(m.Chain.JobId, null, m.Chain.ObjectNativeId, m.Chain.Name, m.Chain.ExtentId, plan.ExtentNames[m.Chain.ExtentId], m.ToExtentId, plan.ExtentNames[m.ToExtentId], m.Bytes))],
                [.. plan.Extents.Select(e => new ExtentFillDto(e.Id, e.Name, e.Total, e.Free, after[e.Id]))],
                plan.Moves.Sum(m => m.Bytes),
                plan.Trigger is null ? "The extents are balanced (within the limits of the settings)." : plan.Moves.Count == 0 ? $"Needed ({plan.Trigger}), but no chain can move now (running, moved recently, due, or too large)." : $"Because {plan.Trigger}."));
        }))
        .WithDescription("What a rebalance would move now (≈ sizes from the restore points' new data), and each extent's free space before and after. Nothing is moved.");

        repositories.MapPost("/{id:guid}/moves", (Guid id, StartMovesDto request, AppDbContext db, SessionQueue queue, CancellationToken ct) => Run(async () =>
        {
            var scaleOut = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id && r.Kind == RepositoryKind.ScaleOut, ct)
                ?? throw new ScaleOutRequestException($"Scale-out repository {id} does not exist.", StatusCodes.Status404NotFound);
            if (request.Chains.Count == 0)
            {
                throw new ScaleOutRequestException("Choose at least one chain.");
            }

            var extents = await db.Repositories.Where(r => r.ScaleOutId == id).Select(r => r.Id).ToListAsync(ct);
            if (request.Chains.Any(c => !extents.Contains(c.FromExtentId) || (c.ToExtentId is { } to && !extents.Contains(to))))
            {
                throw new ScaleOutRequestException($"Chains move only between the extents of {scaleOut.Name}.");
            }

            var mode = request.Rebalance ? RepositoryMoveRequest.Rebalance : RepositoryMoveRequest.Manual;
            try
            {
                var session = await queue.EnqueueMoveAsync(
                    new RepositoryMoveRequest(id, mode, [.. request.Chains.Select(c => new ChainMoveRequest(c.JobId, c.ObjectNativeId, c.FromExtentId, c.ToExtentId))], MaxBytes: scaleOut.ScaleOutSettings?.MaxBytesPerRun),
                    request.Rebalance ? $"Rebalance {scaleOut.Name}" : $"Move {request.Chains.Count} chain(s) of {scaleOut.Name}", "User", ct);
                return Results.Ok(new StartedDto(session));
            }
            catch (InvalidOperationException ex)
            {
                throw new ScaleOutRequestException(ex.Message, StatusCodes.Status409Conflict);
            }
        }))
        .WithDescription("Moves chains between extents now (each keeps its restore point ids; the switch waits for a running backup of the chain).");

        repositories.MapGet("/{id:guid}/chains", (Guid id, Guid? extentId, string? q, int? offset, int? limit, AppDbContext db, ChainLocks locks, CancellationToken ct) => Run(async () =>
            Results.Ok(await ScaleOutChains.ListAsync(db, locks, id, extentId, q, offset ?? 0, Math.Clamp(limit ?? 100, 1, 500), ct))));
    }

    private static async Task<BackupRepositoryEntity> ExtentAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var extent = await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new ScaleOutRequestException($"Repository {id} does not exist.", StatusCodes.Status404NotFound);
        return extent.ScaleOutId is null ? throw new ScaleOutRequestException($"{extent.Name} is not an extent of a scale-out repository.") : extent;
    }

    private static ScaleOutSettings Validate(ScaleOutSettingsDto request)
    {
        static void Range(int value, int min, int max, string what)
        {
            if (value < min || value > max)
            {
                throw new ScaleOutRequestException($"{what} must be between {min} and {max}.");
            }
        }

        Range(request.NewChainMinFreePercent, 0, 50, "The free space kept for new chains (percent)");
        Range(request.LowFreePercent, 1, 50, "The low free space (percent)");
        Range(request.ImbalancePercent, 1, 100, "The imbalance (percent)");
        Range(request.CooldownDays, 0, 365, "The days between moves of a chain");
        Range(request.MoveSwitchWaitMinutes, 1, 24 * 60, "The wait for a running backup (minutes)");
        if (request.MaxMoveMBps is < 1 || request.MaxBytesPerRun is < 1)
        {
            throw new ScaleOutRequestException("Limits must be positive, or empty for no limit.");
        }

        foreach (var time in new[] { request.WindowStart, request.WindowEnd })
        {
            if (!TimeOnly.TryParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            {
                throw new ScaleOutRequestException($"'{time}' is no time of day (HH:mm).");
            }
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ScaleOutRequestException($"The time zone '{request.TimeZoneId}' is unknown.");
        }

        return new ScaleOutSettings
        {
            NewChainMinFreePercent = request.NewChainMinFreePercent,
            FullWhenExtentUnavailable = request.FullWhenExtentUnavailable,
            AutoRebalance = request.AutoRebalance,
            Window = new ScaleOutWindow { Days = [.. request.WindowDays.Distinct()], Start = request.WindowStart, End = request.WindowEnd, TimeZoneId = request.TimeZoneId },
            LowFreePercent = request.LowFreePercent,
            ImbalancePercent = request.ImbalancePercent,
            MaxBytesPerRun = request.MaxBytesPerRun,
            CooldownDays = request.CooldownDays,
            MaxMoveMBps = request.MaxMoveMBps,
            MoveSwitchWaitMinutes = request.MoveSwitchWaitMinutes,
        };
    }

    internal static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ScaleOutRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
