using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace Backdafuckup.Server.Movers;

/// <summary>
/// <c>/api/agent/movers/{id}/*</c>: the mover pods' side of <see cref="MoverChannels"/>. Like the agents' interface it
/// ignores the user sign-in; each channel authenticates with its one-time token (<c>Authorization: Bearer</c>).
/// </summary>
public static class MoverEndpoints
{
    public static void MapMoverEndpoints(this IEndpointRouteBuilder app)
    {
        var movers = app.MapGroup("/api/agent/movers").AllowAnonymous();

        // Backup: the mover sends its volume.
        movers.MapPut("/{id:guid}/data", async (Guid id, MoverChannels channels, HttpContext http, CancellationToken ct) =>
        {
            var found = channels.Find(id, http.Request.Headers.Authorization);
            if (found is TreeBackupChannel tree)
            {
                return await ReceiveTreeAsync(tree, channels, http, ct);
            }

            if (found is not BackupChannel channel)
            {
                return NotOpen(id);
            }

            if (!channel.TryClaim())
            {
                return Results.Problem($"The mover channel {id} for {channel.Description} is already connected.", statusCode: StatusCodes.Status409Conflict);
            }

            // The mover reads zero regions without sending; heartbeats keep the connection, not a minimum rate.
            if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = null;
            }

            channels.Logger.LogInformation("Mover for {Volume} connected from {Remote}.", channel.Description, http.Connection.RemoteIpAddress);
            try
            {
                var total = await channel.ReceiveAsync(http.Request.Body, ct);
                return Results.Json(new MoverResult(total));
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem($"The backup of {channel.Description} stopped on the server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        }).WithMetadata(new DisableRequestSizeLimitAttribute());

        // Restore: the mover fetches the image, or the files to put into its live volume.
        movers.MapGet("/{id:guid}/data", async (Guid id, MoverChannels channels, HttpContext http, CancellationToken ct) =>
        {
            var channel = channels.Find(id, http.Request.Headers.Authorization);
            if (channel is not (RestoreChannel or FilesChannel))
            {
                return NotOpen(id);
            }

            if (!channel.TryClaim())
            {
                return Results.Problem($"The mover channel {id} for {channel.Description} is already connected.", statusCode: StatusCodes.Status409Conflict);
            }

            channels.Logger.LogInformation("Mover for {Volume} connected from {Remote}.", channel.Description, http.Connection.RemoteIpAddress);
            http.Response.ContentType = "application/octet-stream";
            try
            {
                await (channel is FilesChannel files ? files.ServeAsync(http.Response.Body, ct) : ((RestoreChannel)channel).ServeAsync(http.Response.Body, ct));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
            {
                // The channel has the error; the mover sees a stream without its end.
                http.Abort();
            }

            return Results.Empty;
        });

        // Incremental file tree: the mover fetches the previous run's tree and follows it while it walks the volume.
        movers.MapGet("/{id:guid}/previous", async (Guid id, MoverChannels channels, HttpContext http, CancellationToken ct) =>
        {
            if (channels.Find(id, http.Request.Headers.Authorization) is not TreeBackupChannel { Previous: { } previous } channel)
            {
                return NotOpen(id);
            }

            if (!channel.TryClaimPrevious())
            {
                return Results.Problem($"The previous tree for {channel.Description} was already fetched on channel {id}.", statusCode: StatusCodes.Status409Conflict);
            }

            // The mover reads it as fast as its walk goes: a large file being read holds it back for a while.
            if (http.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate)
            {
                rate.MinDataRate = null;
            }

            http.Response.ContentType = "application/octet-stream";
            try
            {
                await previous.WriteAsync(http.Response.Body, ct);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
            {
                // The mover sees a stream without its end and fails; its report names the cause it saw.
                channels.Logger.LogWarning("Sending the previous tree for {Volume} failed: {Error}", channel.Description, ex.Message);
                http.Abort();
            }

            return Results.Empty;
        });

        // The mover's report: the end of a restore, or an error.
        movers.MapPost("/{id:guid}/result", (Guid id, MoverResult result, MoverChannels channels, HttpContext http) =>
        {
            if (channels.Find(id, http.Request.Headers.Authorization) is not { } channel)
            {
                return NotOpen(id);
            }

            if (result.Error is { } error)
            {
                channels.Logger.LogWarning("Mover for {Volume} failed: {Error}", channel.Description, error);
            }

            channel.Report(result);
            return Results.NoContent();
        });
    }

    /// <summary><c>/api/kubernetes/mover</c>: the registry of the mover image, in the console.</summary>
    public static void MapMoverImageEndpoints(this IEndpointRouteBuilder app)
    {
        var image = app.MapGroup("/api/kubernetes/mover");

        image.MapGet("/", (MoverRegistry registry, CancellationToken ct) => registry.DescribeAsync(ct))
            .WithDescription("The mover image: the registry it is pushed to (without passwords), the image this server ships and the one last pushed.");

        image.MapPut("/", async (SaveMoverImageDto request, MoverRegistry registry, CancellationToken ct) =>
        {
            try
            {
                await registry.SaveAsync(request, ct);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(await registry.DescribeAsync(ct));
        }).WithDescription("Saves the registry of the mover image. A null password keeps the stored one, an empty one removes it.");

        image.MapPost("/push", async (MoverRegistry registry, CancellationToken ct) =>
        {
            try
            {
                var log = await registry.PushAsync(ct);
                return Results.Ok(new MoverPushResultDto(await registry.DescribeAsync(ct), log));
            }
            catch (RegistryException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithDescription("Pushes the mover image this server ships to the registry (blobs it lacks, then the manifest).");
    }

    /// <summary>A mover sends its volume file by file: the stream goes to the channel's consumer.</summary>
    private static async Task<IResult> ReceiveTreeAsync(TreeBackupChannel channel, MoverChannels channels, HttpContext http, CancellationToken ct)
    {
        if (!channel.TryClaim())
        {
            return Results.Problem($"The mover channel {channel.Id} for {channel.Description} is already connected.", statusCode: StatusCodes.Status409Conflict);
        }

        // A file that is slow to read (a busy NFS server) sends nothing for a while; heartbeats keep the connection.
        if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = null;
        }

        channels.Logger.LogInformation("Mover for {Volume} connected from {Remote} to send its files.", channel.Description, http.Connection.RemoteIpAddress);
        try
        {
            await channel.ReceiveAsync(http.Request.Body, ct);
            return Results.Json(new MoverResult(0));
        }
        catch (InvalidDataException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Problem($"The backup of {channel.Description} stopped on the server.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TimeoutException ex)
        {
            // The mover stopped making progress (a hung NFS server): the server gave up on it.
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (IOException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult NotOpen(Guid id) =>
        Results.Problem($"The mover channel {id} is not open (any more), or the token does not match.", statusCode: StatusCodes.Status401Unauthorized);
}
