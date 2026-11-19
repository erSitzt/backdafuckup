using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.InstantRecovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Api;

/// <summary>Instant VM recovery (vSphere): start, list, migrate to production storage, stop; and the NFS server's state.</summary>
public static class InstantRecoveryEndpoints
{
    public static void MapInstantRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instant-recoveries");

        group.MapGet("/", async (bool? all, AppDbContext db, NfsServerHost nfs, CancellationToken ct) =>
        {
            var query = db.InstantRecoveries.AsNoTracking().Include(r => r.Vms).AsQueryable();
            if (all != true)
            {
                query = query.Where(r => InstantRecoveryCatalog.Active.Contains(r.State) || r.UpdatedUtc > DateTimeOffset.UtcNow.AddDays(-1));
            }

            var recoveries = await query.OrderByDescending(r => r.CreatedUtc).Take(200).ToListAsync(ct);
            return Results.Ok(await ToDtosAsync(db, nfs, recoveries, ct));
        }).WithDescription("Instant recoveries that are not over or ended in the last day (all=true: every one, newest first, up to 200).");

        group.MapGet("/{sessionId:guid}", async (Guid sessionId, AppDbContext db, NfsServerHost nfs, CancellationToken ct) =>
            await db.InstantRecoveries.AsNoTracking().Include(r => r.Vms).FirstOrDefaultAsync(r => r.SessionId == sessionId, ct) is { } recovery
                ? Results.Ok((await ToDtosAsync(db, nfs, [recovery], ct))[0])
                : Results.Problem($"There is no instant recovery of session {sessionId}.", statusCode: StatusCodes.Status404NotFound));

        group.MapGet("/server", async (NfsServerHost nfs, IOptions<InstantRecoveryOptions> options, AppDbContext db, CancellationToken ct) =>
        {
            var status = nfs.Status;
            var running = await InstantRecoveryCatalog.ActiveSessionIds(db).CountAsync(ct);
            var o = options.Value;
            return Results.Ok(new InstantRecoveryServerDto(
                status.Enabled, status.Listening, status.ListenAddress, string.IsNullOrWhiteSpace(o.AdvertiseAddress) ? null : o.AdvertiseAddress.Trim(),
                status.PortmapperPort, status.NfsPort, status.MountPort, status.Portmapper ?? "none", status.Error, running, Math.Max(1, o.MaxConcurrentSessions), Math.Max(1, o.MaxVmsPerSession)));
        }).WithDescription("The NFS server that instant recoveries are served from: whether it is on and listening, its ports, and how many instant recoveries run.");

        group.MapGet("/host", async (Guid connectionId, string hostId, Engine.ConnectionManager connections, NfsServerHost nfs, IOptions<InstantRecoveryOptions> options, CancellationToken ct) =>
        {
            IHypervisorSession session;
            try
            {
                session = await connections.OpenAsync(connectionId, ct);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException or HttpRequestException or IOException)
            {
                return Results.Problem($"Connection {connectionId} cannot be opened: {ex.Message}", statusCode: StatusCodes.Status409Conflict);
            }

            await using (session)
            {
                if (session is not IInstantRecoveryPlatform platform)
                {
                    return Results.Problem($"{session.Platform.ProductName} '{session.Platform.PlatformName}' cannot run instant recoveries; only vCenter can.", statusCode: StatusCodes.Status400BadRequest);
                }

                InstantRecoveryHost host;
                try
                {
                    host = await platform.GetHostAsync(hostId, ct);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
                }

                var problems = host.Problems.ToList();
                string? mountFrom = null;
                try
                {
                    mountFrom = InstantRecoverySessionRunner.RemoteHostFor(options.Value, host.Name, host.Addresses);
                }
                catch (InvalidOperationException ex)
                {
                    problems.Add(ex.Message);
                }

                var status = nfs.Status;
                return Results.Ok(new InstantRecoveryHostDto(
                    host.Id, host.Name, host.Addresses, host.NfsMounts, host.NfsMountLimit, mountFrom, status.NfsPort, status.MountPort, status.PortmapperPort, problems));
            }
        }).WithDescription("Checks an ESXi host for an instant recovery: its VMkernel addresses (the NFS clients this server's firewall must let in), its NFS mount " +
                           "limit, the address of this server it would mount from, and what keeps it from taking one now.");

        group.MapPost("/", async (StartInstantRecoveryDto request, AppDbContext db, InstantRecoveryControl control, IOptions<InstantRecoveryOptions> options, HttpContext http, CancellationToken ct) =>
        {
            var o = options.Value;
            if (!o.Enabled)
            {
                return Results.Problem(
                    "Instant recovery is off on this backup server: set InstantRecovery:Enabled to true in its configuration, restart the service, and let the ESXi hosts' " +
                    "VMkernel addresses reach it on TCP/UDP 111, TCP 2049 and TCP/UDP 20048 (see the admin guide).", statusCode: StatusCodes.Status409Conflict);
            }

            if (request.Vms.Count == 0 || string.IsNullOrWhiteSpace(request.HostId))
            {
                return Results.Problem("Choose the ESXi host and at least one restore point.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ConnectionId, ct) is not { } connection)
            {
                return Results.Problem($"Connection {request.ConnectionId} does not exist.", statusCode: StatusCodes.Status404NotFound);
            }

            if (connection.Kind != HypervisorKind.VSphere)
            {
                return Results.Problem($"'{connection.DisplayName}' is {Engine.ConnectionKinds.Platform(connection.Kind)}: instant recovery runs VMs on vSphere only.", statusCode: StatusCodes.Status400BadRequest);
            }

            var ids = request.Vms.Select(v => v.RestorePointId).ToList();
            var points = await db.RestorePoints.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            var vms = new List<InstantRecoveryVmRequest>();
            foreach (var vm in request.Vms)
            {
                if (!points.TryGetValue(vm.RestorePointId, out var point))
                {
                    return Results.Problem($"Restore point {vm.RestorePointId} does not exist.", statusCode: StatusCodes.Status404NotFound);
                }

                if (point.State != RestorePointState.Available)
                {
                    return Results.Problem($"The restore point of {point.VmName} from {point.CreatedUtc:yyyy-MM-dd HH:mm} UTC is {point.State}, not available.", statusCode: StatusCodes.Status409Conflict);
                }

                if (point.ObjectKind != RestorePointObjectKinds.Vm
                    || (point.ConnectionId is { } source && await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == source, ct) is { Kind: not HypervisorKind.VSphere } from))
                {
                    return Results.Problem($"{point.VmName} was not backed up from vSphere: instant recovery runs vSphere VMs only.", statusCode: StatusCodes.Status400BadRequest);
                }

                var name = string.IsNullOrWhiteSpace(vm.Name) ? $"{point.VmName} (instant)" : vm.Name.Trim();
                if (name.Length > 80)
                {
                    return Results.Problem($"The VM name '{name}' is longer than 80 characters.", statusCode: StatusCodes.Status400BadRequest);
                }

                vms.Add(new InstantRecoveryVmRequest(point.Id, name, vm.NetworkMap));
            }

            if (vms.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != vms.Count)
            {
                return Results.Problem("Two VMs would get the same name; give them different names.", statusCode: StatusCodes.Status400BadRequest);
            }

            var running = await InstantRecoveryCatalog.ActiveSessionIds(db).CountAsync(ct);
            if (running >= Math.Max(1, o.MaxConcurrentSessions))
            {
                return Results.Problem($"{running} instant recoveries run already, as many as InstantRecovery:MaxConcurrentSessions allows; migrate or stop one first.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                var sessionId = await control.StartAsync(new InstantRecoveryRequest(connection.Id, request.HostId.Trim(), vms)
                {
                    FolderId = string.IsNullOrWhiteSpace(request.FolderId) ? null : request.FolderId,
                    ResourcePoolId = string.IsNullOrWhiteSpace(request.ResourcePoolId) ? null : request.ResourcePoolId,
                    ConnectNics = request.ConnectNics,
                    KeepDiskIdentity = request.KeepDiskIdentity,
                    OverlayLimitBytes = request.OverlayLimitBytes is > 0 ? request.OverlayLimitBytes : null,
                }, http.User.Identity?.Name ?? "user", ct);
                return Results.Ok(new StartedDto(sessionId));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithDescription("Starts an instant recovery of vSphere restore points on an ESXi host; returns its session. The VMs' NICs start disconnected unless asked.");

        group.MapPost("/{sessionId:guid}/stop", async (Guid sessionId, InstantRecoveryControl control, AppDbContext db, CancellationToken ct) =>
            await control.RequestStopAsync(sessionId, ct)
                ? Results.Accepted()
                : await NotRunningAsync(db, sessionId, "stopped", ct))
            .WithDescription("Stops a running instant recovery: its VMs are powered off and removed from vCenter (no file is deleted), then its datastore is unmounted. " +
                             "Refused while a VM has files on another datastore.");

        group.MapPost("/{sessionId:guid}/migrate", async (Guid sessionId, MigrateInstantRecoveryDto request, InstantRecoveryControl control, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DatastoreId))
            {
                return Results.Problem("Choose the datastore to migrate to.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await db.InstantRecoveries.AsNoTracking().FirstOrDefaultAsync(r => r.SessionId == sessionId, ct) is { } recovery && recovery.DatastoreId == request.DatastoreId)
            {
                return Results.Problem($"{recovery.DatastoreName} is the instant recovery's own datastore; choose production storage.", statusCode: StatusCodes.Status400BadRequest);
            }

            return await control.RequestMigrationAsync(sessionId, new InstantRecoveryMigration(request.DatastoreId, request.HostId, request.ResourcePoolId, request.Thin), ct)
                ? Results.Accepted()
                : await NotRunningAsync(db, sessionId, "migrated", ct);
        }).WithDescription("Moves the VMs of a running instant recovery to production storage by Storage vMotion; then its datastore goes.");
    }

    private static async Task<IResult> NotRunningAsync(AppDbContext db, Guid sessionId, string verb, CancellationToken ct) =>
        await db.InstantRecoveries.AsNoTracking().FirstOrDefaultAsync(r => r.SessionId == sessionId, ct) is { } recovery
            ? Results.Problem($"The instant recovery is {recovery.State}; only a running one can be {verb}.", statusCode: StatusCodes.Status409Conflict)
            : Results.Problem($"There is no instant recovery of session {sessionId}.", statusCode: StatusCodes.Status404NotFound);

    private static async Task<List<InstantRecoveryDto>> ToDtosAsync(AppDbContext db, NfsServerHost nfs, IReadOnlyList<InstantRecoveryEntity> recoveries, CancellationToken ct)
    {
        var connectionIds = recoveries.Select(r => r.ConnectionId).Distinct().ToList();
        var connections = await db.Connections.AsNoTracking().Where(c => connectionIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
        var pointIds = recoveries.SelectMany(r => r.Vms).Select(v => v.RestorePointId).Distinct().ToList();
        var points = await db.RestorePoints.AsNoTracking().Where(p => pointIds.Contains(p.Id)).Select(p => new { p.Id, p.VmName, p.CreatedUtc }).ToDictionaryAsync(p => p.Id, ct);
        return [.. recoveries.Select(r => new InstantRecoveryDto(
            r.SessionId, r.State.ToString(), r.ConnectionId, connections.GetValueOrDefault(r.ConnectionId), r.HostId, r.HostName, r.DatastoreName, r.RemoteHost, r.ExportPath,
            r.CreatedUtc, r.UpdatedUtc, r.Message,
            nfs.Exports.All.FirstOrDefault(e => e.Id == (ulong)r.ExportId)?.FileSystem is Backdafuckup.InstantRecovery.InstantRecoveryFileSystem fs ? fs.OverlayBytes : null,
            r.OverlayLimitBytes,
            [.. r.Vms.OrderBy(v => v.Directory, StringComparer.Ordinal).Select(v => new InstantRecoveryVmDto(
                v.RestorePointId, v.Name, v.VmId, points.GetValueOrDefault(v.RestorePointId)?.VmName, points.GetValueOrDefault(v.RestorePointId)?.CreatedUtc))],
            r.MigrationTarget is { } m ? new MigrateInstantRecoveryDto(m.DatastoreId, m.HostId, m.ResourcePoolId, m.Thin) : null))];
    }
}
