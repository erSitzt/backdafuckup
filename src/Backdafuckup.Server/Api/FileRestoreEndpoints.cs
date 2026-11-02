using Backdafuckup.Contracts;
using Backdafuckup.FileLevel;
using Backdafuckup.Server.FileRestore;

namespace Backdafuckup.Server.Api;

/// <summary>File-level restore: browse a restore point and download files (/api/file-restore).</summary>
public static class FileRestoreEndpoints
{
    public static void MapFileRestoreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/file-restore");

        group.MapPost("/mounts", async (OpenFileMountRequestDto request, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            await Handle(async () => Results.Ok(mounts.ToDto(await mounts.OpenAsync(request.RestorePointId, User(http), ct)))));

        group.MapGet("/mounts", (FileRestoreMounts mounts, HttpContext http) =>
            Results.Ok(mounts.ForUser(User(http)).Select(mounts.ToDto).ToList()));

        group.MapGet("/mounts/{id:guid}", (Guid id, FileRestoreMounts mounts, HttpContext http) =>
            Handle(() => Task.FromResult(Results.Ok(mounts.ToDto(mounts.Get(id, User(http)))))));

        group.MapDelete("/mounts/{id:guid}", (Guid id, FileRestoreMounts mounts, HttpContext http) =>
            Handle(async () =>
            {
                await mounts.CloseAsync(mounts.Get(id, User(http)), $"closed by {User(http)}");
                return Results.NoContent();
            }));

        group.MapGet("/mounts/{id:guid}/entries", (Guid id, string volume, string? path, int? offset, int? limit, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                var mount = mounts.Get(id, User(http));
                return Results.Ok(await mounts.ListAsync(mount, volume, GuestPath.Parse(path), offset ?? 0, limit ?? 500, ct));
            }));

        group.MapGet("/mounts/{id:guid}/file", (Guid id, string volume, string path, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                var mount = mounts.Get(id, User(http));
                await mounts.StreamFileAsync(mount, volume, GuestPath.Parse(path), http.Response, ct);
                return Results.Empty;
            }));
    }

    private static string User(HttpContext http) => http.User.Identity?.Name ?? "user";

    /// <summary>Maps the expected failures to problem details; the response may already be streaming for downloads.</summary>
    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MountNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (MountLimitException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (FileNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (GuestFileUnsupportedException ex)
        {
            return Results.Problem($"{ex.Path} cannot be restored: {ex.Reason}.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (IOException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
