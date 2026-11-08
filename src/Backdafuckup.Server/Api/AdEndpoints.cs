using Backdafuckup.Contracts;
using Backdafuckup.Server.Ad;

namespace Backdafuckup.Server.Api;

/// <summary>
/// Active Directory: open a restore point's directory on a live DC (explore session), browse and search it, and compare
/// objects with the live directory. Explore sessions belong to the user who opened them.
/// </summary>
public static class AdEndpoints
{
    public static void MapAdEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ad");

        group.MapGet("/restore-points/{id:guid}/targets", (Guid id, AdExplores explores, CancellationToken ct) =>
            Handle(async () => Results.Ok(await explores.TargetsAsync(id, ct))));

        group.MapPost("/explores", (OpenAdExploreDto request, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(explores.ToDto(await explores.OpenAsync(request, User(http), ct)))));

        group.MapGet("/explores", (AdExplores explores, HttpContext http) =>
            Results.Ok(explores.ForUser(User(http)).Select(explores.ToDto).ToList()));

        group.MapGet("/explores/{id:guid}", (Guid id, AdExplores explores, HttpContext http) =>
            Handle(() => Task.FromResult(Results.Ok(explores.ToDto(explores.Get(id, User(http)))))));

        group.MapDelete("/explores/{id:guid}", (Guid id, AdExplores explores, HttpContext http) =>
            Handle(async () =>
            {
                await explores.CloseByUserAsync(explores.Get(id, User(http)), User(http));
                return Results.NoContent();
            }));

        group.MapGet("/explores/{id:guid}/tree", (Guid id, string? dn, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(await explores.TreeAsync(explores.Get(id, User(http)), dn, ct))));

        group.MapPost("/explores/{id:guid}/search", (Guid id, AdSearchDto search, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(await explores.SearchAsync(explores.Get(id, User(http)), search, ct))));

        group.MapGet("/explores/{id:guid}/objects/{guid:guid}", (Guid id, Guid guid, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(await explores.ObjectAsync(explores.Get(id, User(http)), guid, ct))));

        // The preview writes nothing; the restore needs the live domain typed as confirmation.
        group.MapPost("/explores/{id:guid}/restore/plan", (Guid id, AdRestoreRequestDto request, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(await explores.PlanAsync(explores.Get(id, User(http)), request, ct))));

        group.MapPost("/explores/{id:guid}/restore", (Guid id, AdRestoreRequestDto request, AdExplores explores, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(new StartedDto(await explores.RestoreAsync(explores.Get(id, User(http)), request, User(http), ct)))));
    }

    private static string User(HttpContext http) => http.User.Identity?.Name ?? "user";

    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (TimeoutException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
