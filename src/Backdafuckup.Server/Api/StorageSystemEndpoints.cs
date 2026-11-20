using Backdafuckup.Contracts;
using Backdafuckup.Core.Tls;
using Backdafuckup.Server.Arrays;

namespace Backdafuckup.Server.Api;

/// <summary>Storage systems for backups from storage snapshots: add, test, the volume mapping, leftovers.</summary>
public static class StorageSystemEndpoints
{
    public static void MapStorageSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var systems = app.MapGroup("/api/storage-systems");
        systems.MapGet("/", (StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.ListAsync(ct))));
        systems.MapPost("/probe", (ProbeRequestDto request, CancellationToken ct) => Run(async () =>
        {
            try
            {
                var (certificate, valid) = await CertificatePinning.ProbeAsync(request.Address.Trim(), request.Port, ct);
                using (certificate)
                {
                    return Results.Ok(CertificatePinning.ToProbe(request.Address.Trim(), certificate, valid).ToDto());
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
            {
                return Results.Problem($"No TLS connection to {request.Address}:{request.Port}: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        })).WithDescription("The array's certificate, for the administrator to compare and trust (its public-key pin goes into the create request).");
        systems.MapPost("/", (CreateStorageSystemDto request, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.CreateAsync(request, ct))))
            .WithDescription("Adds a storage system: signs in with the account, reads the array's facts and maps its volumes to the datastores and SRs on them.");
        systems.MapPost("/{id:guid}/test", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.TestAsync(id, ct))));
        systems.MapDelete("/{id:guid}", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        }));
        systems.MapGet("/{id:guid}/volumes", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.VolumesAsync(id, ct))));
        systems.MapPost("/{id:guid}/volumes/refresh", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.RefreshMappingAsync(id, ct))))
            .WithDescription("Maps the array's volumes to the datastores and SRs of all pools and vCenters by the WWNs of their LUNs.");
        systems.MapPut("/{id:guid}/volumes/{volumeId:guid}", (Guid id, Guid volumeId, SetStorageVolumeDto request, StorageSystems service, CancellationToken ct) => Run(async () =>
        {
            await service.SetVolumeAsync(id, volumeId, request.Enabled, ct);
            return Results.NoContent();
        })).WithDescription("Turns backups from snapshots of a volume on or off.");
        systems.MapPut("/{id:guid}/options", (Guid id, StorageSystemOptionsDto request, StorageSystems service, CancellationToken ct) => Run(async () =>
            Results.Ok(await service.SetOptionsAsync(id, request, ct))))
            .WithDescription("Sets how the server uses the array's snapshots: expiry, batches, experimental readers, verify runs.");
        systems.MapPost("/{id:guid}/readers/{reader}/reset", (Guid id, string reader, ArrayReaderStates states, CancellationToken ct) => Run(async () =>
                Results.Ok(await states.ResetAsync(id, reader, ct))))
            .WithDescription("Turns an experimental reader (lvhd, vmfs6) back on after a mismatch switched it off; verify mode starts again.");
        systems.MapGet("/{id:guid}/proxy-access", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.ProxyAccessAsync(id, ct))));
        systems.MapPost("/{id:guid}/proxy-access/plan", (Guid id, StorageHostRequestDto request, StorageSystems service, CancellationToken ct) => Run(async () =>
            Results.Ok(await service.PlanHostAsync(id, request, ct))));
        systems.MapPost("/{id:guid}/proxy-access/hosts", (Guid id, StorageHostRequestDto request, StorageSystems service, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct) =>
            Run(async () => Results.Ok(await service.CreateHostAsync(id, request, user.Identity?.Name, ct))));
        systems.MapGet("/{id:guid}/leftovers", (Guid id, StorageSystems service, CancellationToken ct) => Run(async () => Results.Ok(await service.LeftoversAsync(id, ct))))
            .WithDescription("This server's snapshots and presentations on the array, and whether the cleanup knows them.");
    }

    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StorageSystemRequestException ex)
        {
            return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        }
    }
}
