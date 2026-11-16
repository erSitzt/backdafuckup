using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Tape;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>Tape keys, media pools, sets, tape jobs and copies (restore points on tape), and bringing copies back.</summary>
public static class TapeCatalogEndpoints
{
    public static void MapTapeCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var tape = app.MapGroup("/api/tape");

        tape.MapGet("/keys", async (TapeCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.KeysAsync(ct)));
        tape.MapPost("/keys", (CreateTapeKeyDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () => Results.Ok(await catalog.CreateKeyAsync(request, ct))));
        tape.MapPost("/keys/{id:guid}/file", (Guid id, TapeCatalogService catalog, CancellationToken ct) => Run(async () =>
            Results.File(Backdafuckup.Tape.Format.TapeJson.Serialize(await catalog.TakeKeyFileAsync(id, ct)), "application/json", $"bdfu-tape-key-{id:N}.json")));
        tape.MapDelete("/keys/{id:guid}", (Guid id, TapeCatalogService catalog, CancellationToken ct) => Run(async () =>
        {
            await catalog.DeleteKeyAsync(id, ct);
            return Results.NoContent();
        }));

        tape.MapGet("/pools", async (TapeCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.PoolsAsync(ct)));
        tape.MapPost("/pools", (MediaPoolRequestDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () => Results.Ok(await catalog.SavePoolAsync(null, request, ct))));
        tape.MapPut("/pools/{id:guid}", (Guid id, MediaPoolRequestDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () => Results.Ok(await catalog.SavePoolAsync(id, request, ct))));
        tape.MapDelete("/pools/{id:guid}", (Guid id, TapeCatalogService catalog, CancellationToken ct) => Run(async () =>
        {
            await catalog.DeletePoolAsync(id, ct);
            return Results.NoContent();
        }));

        tape.MapGet("/sets", async (Guid? pool, TapeCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.SetsAsync(pool, ct)));

        // A new set from the next run on: the open set closes now (and its tapes go out by the pool's export rule).
        tape.MapPost("/pools/{id:guid}/new-set", (Guid id, TapeBackupRunner runner, ILoggerFactory loggers, CancellationToken ct) => Run(async () =>
        {
            var log = new TapeLoggerLog(loggers.CreateLogger("Backdafuckup.Tape"));
            return await runner.CloseOpenSetAsync(id, log, ct) ? Results.NoContent() : Results.Problem("The pool has no open set.", statusCode: StatusCodes.Status409Conflict);
        }));

        tape.MapPost("/media/{id:guid}/state", (Guid id, TapeMediumStateDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () =>
        {
            await catalog.SetMediumStateAsync(id, request, ct);
            return Results.NoContent();
        }));

        tape.MapGet("/jobs", async (TapeCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.JobsAsync(ct)));
        tape.MapPost("/jobs", (TapeJobRequestDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () => Results.Ok(await catalog.SaveJobAsync(null, request, ct))));
        tape.MapPut("/jobs/{id:guid}", (Guid id, TapeJobRequestDto request, TapeCatalogService catalog, CancellationToken ct) => Run(async () => Results.Ok(await catalog.SaveJobAsync(id, request, ct))));
        tape.MapDelete("/jobs/{id:guid}", (Guid id, TapeCatalogService catalog, CancellationToken ct) => Run(async () =>
        {
            await catalog.DeleteJobAsync(id, ct);
            return Results.NoContent();
        }));
        tape.MapPost("/jobs/{id:guid}/run", (Guid id, SessionQueue queue, HttpContext http, CancellationToken ct) => Run(async () =>
        {
            try
            {
                return Results.Ok(new StartedDto(await queue.EnqueueTapeBackupAsync(id, http.User.Identity?.Name ?? "user", ct)));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }));

        tape.MapGet("/copies", async (string? vm, Guid? restorePoint, TapeCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.CopiesAsync(vm, restorePoint, ct)));
        tape.MapPost("/copies/{id:guid}/restore", (Guid id, TapeRestoreRequestDto request, AppDbContext db, SessionQueue queue, HttpContext http, CancellationToken ct) => Run(async () =>
        {
            var copy = await db.TapeCopies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw new TapeRequestException($"Tape copy {id} does not exist.", StatusCodes.Status404NotFound);
            var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RepositoryId, ct)
                ?? throw new TapeRequestException($"Repository {request.RepositoryId} does not exist.", StatusCodes.Status404NotFound);
            var manifest = System.Text.Json.JsonSerializer.Deserialize(copy.Manifest, Core.Repository.CoreJsonContext.Default.RestorePointManifest)!;
            if (manifest.Chunking.ChunkSize != repository.ChunkSize)
            {
                throw new TapeRequestException($"{copy.VmName} was backed up with blocks of {manifest.Chunking.ChunkSize / 1024} KiB; repository {repository.Name} uses {repository.ChunkSize / 1024} KiB. Choose a repository with the same block size.");
            }

            return Results.Ok(new StartedDto(await queue.EnqueueTapeRestoreAsync(id, repository.Id, copy.VmName, http.User.Identity?.Name ?? "user", ct)));
        }));
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TapeRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
