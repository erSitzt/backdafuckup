using Backdafuckup.Contracts;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Tape;

namespace Backdafuckup.Server.Api;

/// <summary>
/// Tape libraries and standalone drives: find them on a host, add them, look at their slots and cartridges, and run
/// library actions (load, unload, import, export, a full scan) as tape sessions.
/// </summary>
public static class TapeEndpoints
{
    public static void MapTapeEndpoints(this IEndpointRouteBuilder app)
    {
        var tape = app.MapGroup("/api/tape");

        tape.MapGet("/hosts", async (TapeHosts hosts, CancellationToken ct) => Results.Ok(await hosts.ListAsync(ct)));

        tape.MapGet("/agents", async (TapeAgents agents, CancellationToken ct) => Results.Ok(await agents.ListAsync(ct)));

        tape.MapPost("/agents", (CreateTapeAgentDto request, TapeAgents agents, CancellationToken ct) =>
            Run(async () => Results.Ok(await agents.CreateAsync(request.Name, ct))));

        tape.MapPost("/agents/{id:guid}/token", (Guid id, TapeAgents agents, CancellationToken ct) =>
            Run(async () => Results.Ok(await agents.RenewAsync(id, ct))));

        tape.MapDelete("/agents/{id:guid}", (Guid id, TapeAgents agents, CancellationToken ct) => Run(async () =>
        {
            await agents.DeleteAsync(id, ct);
            return Results.NoContent();
        }));

        tape.MapPost("/hosts/{host}/scan", (string host, TapeLibraries libraries, CancellationToken ct) =>
            Run(async () => Results.Ok(await libraries.ScanAsync(host, ct))));

        tape.MapGet("/libraries", async (TapeLibraries libraries, CancellationToken ct) => Results.Ok(await libraries.ListAsync(ct)));

        tape.MapGet("/libraries/{id:guid}", (Guid id, TapeLibraries libraries, CancellationToken ct) =>
            Run(async () => Results.Ok(await libraries.GetAsync(id, ct))));

        tape.MapPost("/libraries", (CreateTapeLibraryDto request, TapeLibraries libraries, CancellationToken ct) => Run(async () =>
        {
            var created = await libraries.CreateAsync(request, ct);
            return Results.Created($"/api/tape/libraries/{created.Id}", await libraries.GetAsync(created.Id, ct));
        }));

        tape.MapPut("/libraries/{id:guid}", (Guid id, RenameTapeLibraryDto request, TapeLibraries libraries, CancellationToken ct) => Run(async () =>
        {
            await libraries.RenameAsync(id, request.Name, ct);
            return Results.Ok(await libraries.GetAsync(id, ct));
        }));

        tape.MapDelete("/libraries/{id:guid}", (Guid id, TapeLibraries libraries, CancellationToken ct) => Run(async () =>
        {
            await libraries.DeleteAsync(id, ct);
            return Results.NoContent();
        }));

        // Where every cartridge is now (READ ELEMENT STATUS); quick, so not a session.
        tape.MapPost("/libraries/{id:guid}/inventory", (Guid id, TapeLibraries libraries, CancellationToken ct) => Run(async () =>
        {
            await libraries.InventoryAsync(id, "Inventory", initialize: false, log: null, ct);
            return Results.Ok(await libraries.GetAsync(id, ct));
        }));

        tape.MapGet("/libraries/{id:guid}/elements", (Guid id, TapeLibraries libraries, CancellationToken ct) =>
            Run(async () => Results.Ok(await libraries.ElementsAsync(id, ct))));

        tape.MapPost("/libraries/{id:guid}/actions", (Guid id, TapeActionDto request, TapeLibraries libraries, SessionQueue queue, HttpContext http, CancellationToken ct) => Run(async () =>
        {
            var library = await libraries.LoadAsync(id, ct);
            var action = request.Action?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!TapeActions.All.Contains(action))
            {
                throw new TapeRequestException($"Unknown tape action '{request.Action}'; it is one of {string.Join(", ", TapeActions.All)}.");
            }

            var standalone = library.ChangerSerial is null;
            if (standalone && action is TapeActions.Load or TapeActions.Import or TapeActions.Export)
            {
                throw new TapeRequestException($"{library.Name} is a standalone drive without a robot or mail slots: put cartridges in and take them out by hand.");
            }

            if (action is TapeActions.Load or TapeActions.Export && string.IsNullOrWhiteSpace(request.Barcode))
            {
                throw new TapeRequestException($"A {action} needs the barcode of the cartridge.");
            }

            var drive = request.DriveId is { } driveId
                ? library.Drives.FirstOrDefault(d => d.Id == driveId) ?? throw new TapeRequestException($"Drive {driveId} is not a drive of {library.Name}.")
                : action == TapeActions.Unload && library.Drives.Count == 1 ? library.Drives[0] : null;
            if (action == TapeActions.Unload && drive is null)
            {
                throw new TapeRequestException($"Which drive of {library.Name} should be unloaded?");
            }

            var barcode = request.Barcode?.Trim();
            var name = action switch
            {
                TapeActions.Load => $"Load {barcode} ({library.Name}{(drive is null ? string.Empty : ", " + drive.Name)})",
                TapeActions.Unload => $"Unload {drive!.Name} ({library.Name})",
                TapeActions.Export => $"Export {barcode} ({library.Name})",
                TapeActions.Import => $"Import from the mail slots ({library.Name})",
                _ => $"Scan {library.Name}",
            };
            var session = await queue.EnqueueTapeAsync(new TapeSessionRequest(library.Id, action, barcode, drive?.Id), name, http.User.Identity?.Name ?? "user", ct);
            return Results.Ok(new StartedDto(session));
        }));

        tape.MapGet("/media", (Guid? library, TapeLibraries libraries, CancellationToken ct) =>
            Run(async () => Results.Ok(await libraries.MediaAsync(library, ct))));
    }

    /// <summary>
    /// <c>/api/agent/tape-hosts/{id}/control</c>: the tape agent's WebSocket. Like the other agents' interfaces it ignores the
    /// user sign-in; the agent authenticates with its connect token.
    /// </summary>
    public static void MapTapeAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var accept = new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(60) };
        app.MapGet("/api/agent/tape-hosts/{id:guid}/control", async (Guid id, TapeAgents agents, HttpContext http, CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                return Results.Problem("The control connection of a tape agent is a WebSocket.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await agents.AuthenticateAsync(id, http.Request.Headers.Authorization, ct) is not { } agent)
            {
                return Results.Problem(
                    $"There is no tape agent {id}, or its connect token does not match (renewed? copy the new agent.env).", statusCode: StatusCodes.Status401Unauthorized);
            }

            using var socket = await http.WebSockets.AcceptWebSocketAsync(accept);
            await agents.RunControlAsync(agent, socket, $"{http.Connection.RemoteIpAddress}:{http.Connection.RemotePort}", ct);
            return Results.Empty;
        }).AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>Requests that cannot be done answer with their reason; a device that cannot be reached or refuses answers 502 with its message.</summary>
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
