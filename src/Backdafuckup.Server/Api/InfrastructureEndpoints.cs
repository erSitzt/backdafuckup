using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Storage;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

public static class InfrastructureEndpoints
{
    public static void MapInfrastructureEndpoints(this IEndpointRouteBuilder app)
    {
        var connections = app.MapGroup("/api/connections");
        Clusters.KubernetesUsageSources.Map(connections);

        connections.MapGet("/", async (AppDbContext db, Clusters.ClusterAgents agents, string? kind, CancellationToken ct) =>
        {
            var query = db.Connections.Include(c => c.Credential).Include(c => c.Hosts).AsQueryable();
            if (!ConnectionKinds.TryParseList(kind, out var kinds, out var error))
            {
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }

            if (kinds is not null)
            {
                var wanted = kinds.ToList();
                query = query.Where(c => wanted.Contains(c.Kind));
            }

            return Results.Ok((await query.OrderBy(c => c.DisplayName).ToListAsync(ct)).Select(c => WithAgent(c, agents)));
        }).WithDescription("All connections: pools, Kubernetes clusters and vCenters. kind=XenServer, kind=Kubernetes or kind=VSphere returns those kinds only (several separated by commas, e.g. kind=XenServer,VSphere).");

        connections.MapGet("/{id:guid}", async (Guid id, AppDbContext db, Clusters.ClusterAgents agents, CancellationToken ct) =>
            await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct) is { } c ? Results.Ok(WithAgent(c, agents)) : Results.NotFound());

        connections.MapPost("/probe", async (ProbeRequestDto request, ConnectionManager manager, CancellationToken ct) =>
        {
            if (!ConnectionKinds.TryParse(request.Kind, out var kind))
            {
                return Results.Problem($"Unknown connection kind '{request.Kind}' ({ConnectionKinds.Accepted}).", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                return Results.Ok((await manager.Provider(kind).ProbeCertificateAsync(request.Address.Trim(), request.Port, ct)).ToDto());
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            {
                return Results.Problem($"Cannot connect to {request.Address}:{request.Port}: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        connections.MapPost("/", async (CreateConnectionDto request, AppDbContext db, ConnectionManager manager, CredentialProtector protector, TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            if (!ConnectionKinds.TryParse(request.Kind, out var kind))
            {
                return Results.Problem($"Unknown connection kind '{request.Kind}' ({ConnectionKinds.Accepted}).", statusCode: StatusCodes.Status400BadRequest);
            }

            var noun = ConnectionKinds.Noun(kind);
            if (await db.Connections.AnyAsync(c => c.DisplayName == request.DisplayName, ct))
            {
                return Results.Problem($"A connection named '{request.DisplayName}' already exists.", statusCode: StatusCodes.Status409Conflict);
            }

            // A cluster behind its agent: nothing to test yet; the setup manifest brings the agent, which then connects.
            if (kind == HypervisorKind.Kubernetes && string.Equals(request.Access, nameof(ConnectionAccess.Agent), StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    return Results.Problem("Give the cluster a name.", statusCode: StatusCodes.Status400BadRequest);
                }

                var created = time.GetUtcNow();
                var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var behindAgent = new HypervisorConnection
                {
                    Kind = kind,
                    DisplayName = request.DisplayName.Trim(),
                    Address = Backdafuckup.Core.DataMover.ClusterTunnel.ApiServerName,
                    Port = 443,
                    Access = ConnectionAccess.Agent,
                    Status = ConnectionStatus.Unknown,
                    LastError = "Waiting for the agent: apply the cluster's setup manifest.",
                    Credential = new Credential { Username = "agent", ProtectedSecret = protector.Protect(token), CreatedUtc = created, UpdatedUtc = created },
                };
                db.Connections.Add(behindAgent);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/connections/{behindAgent.Id}", behindAgent.ToDto() with { Agent = new ConnectionAgentDto(false, null, null, null, null, null, null) });
            }

            var pins = request.TrustedCertificates.Select(c => new PinnedCertificate(c.HostAddress, c.SpkiSha256, c.Sha256Thumbprint, string.Empty)).ToList();
            if (kind == HypervisorKind.Kubernetes)
            {
                if (CaPin(request.CaCertificatePem) is not { } ca)
                {
                    return Results.Problem("Give the cluster's CA certificate (PEM): the ca.crt of the token secret, e.g. kubectl -n backdafuckup-system get secret backdafuckup-token -o jsonpath='{.data.ca\\.crt}' | base64 -d.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                pins = [ca];
            }

            var settings = new ConnectionSettings(
                Guid.Empty, kind, request.Address.Trim(), request.Port,
                new System.Net.NetworkCredential(request.UserName, request.Password), TlsTrustMode.Pinned,
                pins,
                ConnectionManager.KindOptions(kind, allowInsecureNbd: request.AllowInsecureNbd));
            var result = await manager.Provider(kind).TestConnectionAsync(settings, ct);
            if (result.UntrustedHosts.Count > 0)
            {
                return Results.Json(new UntrustedCertificatesDto(result.Error ?? "Untrusted certificate", result.UntrustedHosts.Select(p => p.ToDto()).ToList()), statusCode: StatusCodes.Status409Conflict);
            }

            if (!result.Success || result.Platform is null)
            {
                return Results.Problem(result.Error ?? "Connection failed.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await db.Connections.AnyAsync(c => c.PlatformId == result.Platform.PlatformId, ct))
            {
                return Results.Problem($"The {noun} '{result.Platform.PlatformName}' ({result.Platform.MasterAddress}) is already registered.", statusCode: StatusCodes.Status409Conflict);
            }

            var now = time.GetUtcNow();
            var user = http.User.Identity?.Name;
            var connection = new HypervisorConnection
            {
                Kind = kind,
                DisplayName = request.DisplayName,
                Address = request.Address.Trim(),
                Port = request.Port,
                CurrentMasterAddress = result.Platform.MasterAddress,
                PlatformId = result.Platform.PlatformId,
                PlatformName = result.Platform.PlatformName,
                ProductName = result.Platform.ProductName,
                ProductVersion = result.Platform.ProductVersion,
                AllowInsecureNbd = kind == HypervisorKind.XenServer && request.AllowInsecureNbd,
                Status = ConnectionStatus.Connected,
                LastConnectedUtc = now,
                Credential = new Credential { Username = request.UserName, ProtectedSecret = protector.Protect(request.Password), CreatedUtc = now, UpdatedUtc = now },
            };

            // Pins: what the user confirmed plus the certificates of all pool hosts reported by the trusted master.
            foreach (var pin in request.TrustedCertificates.Select(c => new PinnedCertificate(c.HostAddress, c.SpkiSha256, c.Sha256Thumbprint, string.Empty))
                         .Concat(result.HostPins).GroupBy(p => p.HostAddress, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                connection.Hosts.Add(new ConnectionHost { Address = pin.HostAddress, SpkiSha256 = pin.SpkiSha256, Sha256Thumbprint = pin.Sha256Thumbprint, CertificatePem = pin.Pem, TrustedUtc = now, TrustedBy = user });
            }

            db.Connections.Add(connection);
            await db.SaveChangesAsync(ct);
            await TryRefreshInventoryAsync(connection.Id, db, manager, time, ct);
            await db.Entry(connection).ReloadAsync(ct);
            return Results.Created($"/api/connections/{connection.Id}", connection.ToDto());
        });

        connections.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.Jobs.AnyAsync(j => j.ConnectionId == id, ct))
            {
                return Results.Problem("The connection is still used by backup jobs.", statusCode: StatusCodes.Status409Conflict);
            }

            var connection = await db.Connections.FindAsync([id], ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            // The connection first: it refers to its credential, which goes after it.
            await db.Inventory.Where(i => i.ConnectionId == id).ExecuteDeleteAsync(ct);
            db.Connections.Remove(connection);
            await db.SaveChangesAsync(ct);
            await db.Credentials.Where(c => c.Id == connection.CredentialId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        connections.MapPost("/{id:guid}/agent", async (Guid id, AppDbContext db, CredentialProtector protector, Clusters.ClusterAgents agents, TimeProvider time, CancellationToken ct) =>
        {
            var connection = await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            if (connection.Kind != HypervisorKind.Kubernetes)
            {
                return Results.Problem("Only Kubernetes clusters are reached through an agent.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (connection.Access == ConnectionAccess.Agent)
            {
                return Results.Ok(WithAgent(connection, agents));
            }

            // From now on through the agent: its connect token replaces the stored ServiceAccount token and the pins.
            var now = time.GetUtcNow();
            var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            connection.Access = ConnectionAccess.Agent;
            connection.Address = Backdafuckup.Core.DataMover.ClusterTunnel.ApiServerName;
            connection.Port = 443;
            connection.Credential.Username = "agent";
            connection.Credential.ProtectedSecret = protector.Protect(token);
            connection.Credential.UpdatedUtc = now;
            connection.Hosts.Clear();
            connection.Status = ConnectionStatus.Unknown;
            connection.LastError = "Waiting for the agent: apply the cluster's setup manifest with the agent.";
            await db.SaveChangesAsync(ct);
            return Results.Ok(WithAgent(connection, agents));
        }).WithDescription(
            "Reaches a Kubernetes cluster through its agent from now on (jobs and restore points stay): the stored token and pins are replaced by a connect token, " +
            "which the cluster's setup manifest with the agent (GET /api/kubernetes/setup.yaml?connectionId=…) holds. Going back to direct access means adding the token again.");

        // The platforms of a cluster set by hand, or (null) detected again; the product name follows at once.
        connections.MapPut("/{id:guid}/platforms", async (Guid id, SetConnectionPlatformsDto request, AppDbContext db, Clusters.ClusterAgents agents, CancellationToken ct) =>
        {
            var connection = await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            if (connection.Kind != HypervisorKind.Kubernetes)
            {
                return Results.Problem("Only Kubernetes clusters have platforms.", statusCode: StatusCodes.Status400BadRequest);
            }

            var ids = request.Override?.Select(i => i.Trim()).Where(i => i.Length > 0 && i != "kubernetes").Distinct(StringComparer.Ordinal).ToList();
            if (ids is not null && KubernetesPlatformMapping.Check(ids) is { } problem)
            {
                return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            }

            connection.PlatformOverride = ids;
            connection.ProductName = Backdafuckup.Kubernetes.KubernetesPlatformSet.Of(ids ?? connection.DetectedPlatforms?.Select(d => d.Id)).Display;
            await db.SaveChangesAsync(ct);
            return Results.Ok(WithAgent(connection, agents));
        });

        connections.MapPost("/{id:guid}/rescan", async (
            Guid id, AppDbContext db, ConnectionManager manager, TimeProvider time, [Microsoft.AspNetCore.Mvc.FromServices] Movers.MoverRegistry images, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (await TryRefreshInventoryAsync(id, db, manager, time, ct) is { } error)
            {
                return Results.Problem(error, statusCode: StatusCodes.Status502BadGateway);
            }

            await UpdateNodeAgentAsync(id, db, manager, images, loggers.CreateLogger("Backdafuckup.Server.NodeAgents"), ct);
            return Results.NoContent();
        });

        connections.MapGet("/{id:guid}/inventory", async (Guid id, AppDbContext db, CancellationToken ct) =>
            (await db.Inventory.Where(i => i.ConnectionId == id).OrderBy(i => i.Kind).ThenBy(i => i.Name).ToListAsync(ct)).Select(i => i.ToDto()));

        // The platform catalog, for setting a cluster's platforms by hand.
        app.MapGet("/api/kubernetes/platforms", () => Backdafuckup.Kubernetes.KubernetesPlatformCatalog.Builtin.Profiles.Where(p => !p.Always).Select(KubernetesPlatformMapping.ToDto));

        app.MapGet("/api/kubernetes/setup.yaml", async (
                bool? restore, bool? download, Guid? connectionId, bool? nodeAgent, AppDbContext db, CredentialProtector protector, Agents.AgentSettingsStore settings,
                [Microsoft.AspNetCore.Mvc.FromServices] Movers.MoverRegistry images, CancellationToken ct) =>
            {
                Backdafuckup.Kubernetes.ClusterAgentManifest? agent = null;
                Backdafuckup.Kubernetes.NodeAgentManifest? node = null;
                HypervisorConnection? cluster = null;
                if (connectionId is { } id)
                {
                    var connection = cluster = await db.Connections.AsNoTracking().Include(c => c.Credential).FirstOrDefaultAsync(c => c.Id == id && c.Kind == HypervisorKind.Kubernetes, ct);
                    if (connection is null)
                    {
                        return Results.NotFound();
                    }

                    if (connection.Access == ConnectionAccess.Agent)
                    {
                        var url = await settings.GetServerUrlAsync(ct);
                        if (string.IsNullOrWhiteSpace(url))
                        {
                            return Results.Problem("Set the address under which the cluster reaches this server first (Guest agents → Settings → server address).", statusCode: StatusCodes.Status409Conflict);
                        }

                        Backdafuckup.Core.DataMover.MoverDeployment mover;
                        try
                        {
                            mover = await images.EnsurePushedAsync(null, ct);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or Movers.RegistryException or HttpRequestException)
                        {
                            return Results.Problem($"The agent runs the mover image, which is not in a registry the cluster can pull from yet (Kubernetes Clusters → Mover image): {ex.Message}",
                                statusCode: StatusCodes.Status409Conflict);
                        }

                        agent = new Backdafuckup.Kubernetes.ClusterAgentManifest(
                            connection.Id, url.TrimEnd('/') + "/", settings.ServerPins(), protector.Unprotect(connection.Credential.ProtectedSecret), mover.Image, mover.PullSecretName, mover.DockerConfigJson);
                    }
                }

                // The node agent: its kubelet directory from the cluster's platforms (the base's without a cluster).
                if (nodeAgent == true)
                {
                    var url = await settings.GetServerUrlAsync(ct);
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return Results.Problem("Set the address under which the cluster reaches this server first (Guest agents → Settings → server address): the node agents send the files they read there.",
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    Backdafuckup.Core.DataMover.MoverDeployment mover;
                    try
                    {
                        mover = await images.EnsurePushedAsync(null, ct);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Movers.RegistryException or HttpRequestException)
                    {
                        return Results.Problem($"The node agent runs the mover image, which is not in a registry the cluster can pull from yet (Kubernetes Clusters → Mover image): {ex.Message}",
                            statusCode: StatusCodes.Status409Conflict);
                    }

                    var platforms = Backdafuckup.Kubernetes.KubernetesPlatformSet.Of(cluster?.PlatformOverride ?? cluster?.DetectedPlatforms?.Select(d => d.Id));
                    node = new Backdafuckup.Kubernetes.NodeAgentManifest(url.TrimEnd('/') + "/", settings.ServerPins(), mover.Image, platforms.KubeletRootDir, mover.PullSecretName, mover.DockerConfigJson);
                }

                var manifest = Backdafuckup.Kubernetes.KubernetesSetup.Manifest(restore != false, agent, node);
                return download == true
                    ? Results.File(System.Text.Encoding.UTF8.GetBytes(manifest), "application/yaml", Backdafuckup.Kubernetes.KubernetesSetup.FileName)
                    : Results.Text(manifest, "application/yaml");
            })
            .WithDescription(
                "The objects a Kubernetes cluster needs for backdafuckup: work namespace, ServiceAccount with a token, backup and (unless restore=false) restore roles. " +
                "With connectionId of a cluster reached through its agent also the agent (Deployment, connect token, pull secret); the mover image is pushed first if needed. " +
                "nodeAgent=true adds the node agent (a DaemonSet in backdafuckup-node that reads pod volumes and the kubelets' usage numbers; its kubelet directory from the cluster's platforms). " +
                "download=true returns it as a file.");

        var repositories = app.MapGroup("/api/repositories");

        repositories.MapGet("/", async (AppDbContext db, RepositoryManager manager, CancellationToken ct) => await ListRepositoriesAsync(db, manager, ct));

        repositories.MapPost("/", async (CreateRepositoryDto request, AppDbContext db, TimeProvider time, Sql.SqlCatalogBackfill backfill, CancellationToken ct) =>
        {
            if (await db.Repositories.AnyAsync(r => r.Name == request.Name, ct))
            {
                return Results.Problem($"A repository named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);
            }

            if (request.PackSizeMiB is < 16 or > 512)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(CreateRepositoryDto.PackSizeMiB)] = ["The pack size must be between 16 and 512 MiB."] });
            }

            var path = Path.GetFullPath(request.Path.Trim());
            var import = File.Exists(Path.Combine(path, LocalDirectoryRepository.DescriptorFileName));
            LocalDirectoryRepository repository;
            try
            {
                repository = import
                    ? await LocalDirectoryRepository.OpenAsync(path, ct, time)
                    : await LocalDirectoryRepository.CreateAsync(path, ct, request.ChunkSizeKiB * 1024, request.CompressionLevel, time, request.PackSizeMiB * 1024L * 1024);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                return Results.Problem($"The directory cannot be used: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }

            BackupRepositoryEntity created;
            await using (repository)
            {
                if (await db.Repositories.AnyAsync(r => r.RepositoryUuid == repository.Info.RepositoryId, ct))
                {
                    return Results.Problem("This repository is already registered.", statusCode: StatusCodes.Status409Conflict);
                }

                var stats = repository.GetStorageStats();
                var entity = new BackupRepositoryEntity
                {
                    Name = request.Name,
                    Path = path,
                    RepositoryUuid = repository.Info.RepositoryId,
                    ChunkSize = repository.Info.ChunkSize,
                    CompressionLevel = repository.Info.CompressionLevel,
                    MaxConcurrentTasks = request.MaxConcurrentTasks,
                    TotalBytes = stats.TotalBytes,
                    FreeBytes = stats.FreeBytes,
                };
                db.Repositories.Add(entity);
                await db.SaveChangesAsync(ct);
                if (import)
                {
                    _ = await ImportRestorePointsAsync(entity.Id, repository, db, time, ct);
                }

                created = entity;
            }

            // Only now: reading the restore points' SQL Server snapshots opens the repository again, which its owner
            // lock refuses while it is open here.
            if (import)
            {
                await backfill.RecordSnapshotFullsAsync(ct);
            }

            var (format, packSize) = ReadFormat(created.Path);
            return Results.Created($"/api/repositories/{created.Id}", created.ToDto(0, 0, format, packSize));
        });

        repositories.MapPost("/{id:guid}/gc", async (Guid id, AppDbContext db, SessionQueue queue, CancellationToken ct) =>
        {
            if (await db.Repositories.FindAsync([id], ct) is not { } repository)
            {
                return Results.NotFound();
            }

            // A scale-out repository: one maintenance session per extent (in maintenance ones are left out).
            var targets = repository.Kind == RepositoryKind.ScaleOut
                ? await db.Repositories.Where(r => r.ScaleOutId == id && !r.Maintenance).OrderBy(r => r.Name).ToListAsync(ct)
                : [repository];
            Guid? first = null;
            foreach (var target in targets)
            {
                var queued = await queue.EnqueueMaintenanceAsync(target.Id, target.Name, "User", ct);
                first ??= queued;
            }

            return first is { } started ? Results.Ok(new StartedDto(started)) : Results.Problem($"{repository.Name} has no extent that is not in maintenance.", statusCode: StatusCodes.Status409Conflict);
        });

        repositories.MapPost("/{id:guid}/packs", async (Guid id, ConvertRepositoryDto request, AppDbContext db, RepositoryManager manager, SessionQueue queue, CancellationToken ct) =>
        {
            if (await db.Repositories.FindAsync([id], ct) is not { } repository)
            {
                return Results.NotFound();
            }

            if (request.PackSizeMiB is < 16 or > 512)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(ConvertRepositoryDto.PackSizeMiB)] = ["The pack size must be between 16 and 512 MiB."] });
            }

            var (format, _) = ReadFormat(repository.Path);
            var open = manager.TryGetOpen(id);
            if (format == 2 && open is { LooseChunksMayExist: false })
            {
                return Results.Problem($"Repository '{repository.Name}' ({repository.Path}) already uses pack files.", statusCode: StatusCodes.Status409Conflict);
            }

            var session = await queue.EnqueueMaintenanceAsync(id, repository.Name, "User", ct, new MaintenanceRequest(ConvertToPacks: true, request.PackSizeMiB * 1024L * 1024));
            return Results.Ok(new StartedDto(session));
        })
        .WithDescription(
            "Converts a repository with one file per chunk (format 1) to pack files (format 2) in a maintenance session: new chunks go into packs at once, " +
            "the existing ones are packed in runs of at most Engine:PackLooseMaxDuration, newest restore points first. Older versions of backdafuckup cannot open " +
            "a converted repository. On a converted repository whose packing is not finished it queues the next run.");

        repositories.MapPost("/{id:guid}/rescan", async (Guid id, AppDbContext db, RepositoryManager manager, TimeProvider time, Sql.SqlCatalogBackfill backfill, CancellationToken ct) =>
        {
            var ids = await db.Repositories.Where(r => r.ScaleOutId == id && !r.Maintenance).Select(r => r.Id).ToListAsync(ct);
            if (ids.Count == 0)
            {
                ids.Add(id);
            }

            var (imported, elsewhere) = (0, 0);
            foreach (var repositoryId in ids)
            {
                var repository = await manager.GetAsync(repositoryId, ct);
                var (added, other) = await ImportRestorePointsAsync(repositoryId, repository, db, time, ct);
                (imported, elsewhere) = (imported + added, elsewhere + other);
            }

            await backfill.RecordSnapshotFullsAsync(ct);
            return Results.Ok(new { imported, elsewhere });
        })
        .WithDescription("Adds restore points and SQL Server backups found in the directory (or each extent) but missing in the catalog. elsewhere: copies of ones the catalog has in another repository (left by a move, or a copied directory); they are not added.");

        repositories.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, RepositoryManager manager, FileRestore.FileRestoreMounts mounts, CancellationToken ct) =>
        {
            // Checked first: a published database is the more urgent reason (the jobs may be removed meanwhile).
            if (await db.Sessions.AnyAsync(
                    s => s.RepositoryId == id && (s.Kind == SessionKind.SqlPublish || s.Kind == SessionKind.SqlRestore || s.Kind == SessionKind.SqlExport) &&
                         (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping),
                    ct))
            {
                return Results.Problem("SQL Server databases from this repository are published, being restored or exported; unpublish or stop them under Sessions first.", statusCode: StatusCodes.Status409Conflict);
            }

            if (await db.Jobs.AnyAsync(j => j.RepositoryId == id, ct))
            {
                return Results.Problem("The repository is still used by backup jobs.", statusCode: StatusCodes.Status409Conflict);
            }

            if (await db.Repositories.FindAsync([id], ct) is not { } entity)
            {
                return Results.NotFound();
            }

            if (entity.Kind == RepositoryKind.ScaleOut && await db.Repositories.AnyAsync(r => r.ScaleOutId == id, ct))
            {
                return Results.Problem($"{entity.Name} still has extents: evacuate and remove them first.", statusCode: StatusCodes.Status409Conflict);
            }

            if (entity.ScaleOutId is { } scaleOutId)
            {
                // An extent goes only empty: its restore points would otherwise vanish from the catalog.
                var reason =
                    await db.RestorePoints.AnyAsync(r => r.RepositoryId == id, ct) || await db.SqlBackups.AnyAsync(b => b.RepositoryId == id, ct)
                        ? $"{entity.Name} still holds restore points or SQL Server backups: evacuate it first."
                    : await db.ChainPlacements.AnyAsync(p => p.ExtentId == id, ct)
                        ? $"Chains are still placed on {entity.Name}: evacuate it first."
                    : await db.RepositoryMoveItems.AnyAsync(m => (m.SourceExtentId == id || m.TargetExtentId == id) && (m.State == MoveItemState.Copied || m.State == MoveItemState.Switched || m.State == MoveItemState.Abandoned), ct)
                        ? $"Copies of a move on {entity.Name} are not cleaned up yet; try again in a few minutes."
                    : await db.Sessions.AnyAsync(s => s.Kind == SessionKind.RepositoryMove && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping), ct)
                        ? "A move between extents is running; remove the extent when it has finished."
                    : !await db.Repositories.AnyAsync(r => r.ScaleOutId == scaleOutId && r.Id != id, ct)
                        ? $"{entity.Name} is the last extent of its scale-out repository."
                    : null;
                if (reason is not null)
                {
                    return Results.Problem(reason, statusCode: StatusCodes.Status409Conflict);
                }
            }

            if (mounts.HasMounts(id))
            {
                return Results.Problem("File browsers are open on restore points of this repository; close them under Sessions first.", statusCode: StatusCodes.Status409Conflict);
            }

            await manager.CloseAsync(id);
            await db.RestorePoints.Where(r => r.RepositoryId == id).ExecuteDeleteAsync(ct);
            await db.SqlBackups.Where(b => b.RepositoryId == id).ExecuteDeleteAsync(ct);
            await db.Repositories.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent(); // The data on disk is kept; it can be imported again.
        });
    }

    public static async Task<List<RepositoryDto>> ListRepositoriesAsync(AppDbContext db, RepositoryManager manager, CancellationToken ct)
    {
        var repositories = await db.Repositories.OrderBy(r => r.Name).ToListAsync(ct);
        var stats = await db.RestorePoints.Where(r => r.State == RestorePointState.Available)
            .GroupBy(r => r.RepositoryId).Select(g => new { g.Key, Count = g.Count(), Bytes = g.Sum(r => r.LogicalBytes) }).ToListAsync(ct);
        var chains = await db.ChainPlacements.GroupBy(p => p.ExtentId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var repository in repositories.Where(r => r.Kind != RepositoryKind.ScaleOut))
        {
            try
            {
                var drive = new DriveInfo(repository.Path);
                repository.TotalBytes = drive.TotalSize;
                repository.FreeBytes = drive.AvailableFreeSpace;
                repository.State = Directory.Exists(repository.Path) ? RepositoryState.Online : RepositoryState.Offline;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                repository.State = RepositoryState.Offline;
            }
        }

        // A scale-out repository adds up its extents: all of them for the size, the active ones for the free space.
        foreach (var scaleOut in repositories.Where(r => r.Kind == RepositoryKind.ScaleOut))
        {
            var extents = repositories.Where(r => r.ScaleOutId == scaleOut.Id).ToList();
            scaleOut.TotalBytes = extents.Sum(e => e.TotalBytes ?? 0);
            scaleOut.FreeBytes = extents.Where(e => e.ExtentState == ExtentState.Active && !e.Maintenance && e.State == RepositoryState.Online).Sum(e => e.FreeBytes ?? 0);
            scaleOut.State = extents.Count > 0 && extents.All(e => e.State == RepositoryState.Online) ? RepositoryState.Online
                : extents.Any(e => e.State == RepositoryState.Online) ? RepositoryState.Error
                : RepositoryState.Offline;
            scaleOut.LastError = extents.Where(e => e.State != RepositoryState.Online).Select(e => $"extent {e.Name} ({e.Path}) is offline").FirstOrDefault();
        }

        return repositories.Select(r =>
        {
            if (r.Kind == RepositoryKind.ScaleOut)
            {
                var members = repositories.Where(e => e.ScaleOutId == r.Id).Select(e => e.Id).ToList();
                return r.ToDto(
                    stats.Where(x => members.Contains(x.Key)).Sum(x => x.Count), stats.Where(x => members.Contains(x.Key)).Sum(x => x.Bytes),
                    chains: chains.Where(c => members.Contains(c.Key)).Sum(c => c.Count), extents: members.Count);
            }

            var s = stats.FirstOrDefault(x => x.Key == r.Id);
            var (format, packSize) = ReadFormat(r.Path);
            var packing = format == 2 && manager.TryGetOpen(r.Id) is { LooseChunksMayExist: true };
            return r.ToDto(s?.Count ?? 0, s?.Bytes ?? 0, format, packSize, packing, chains.FirstOrDefault(c => c.Key == r.Id)?.Count ?? 0);
        }).ToList();
    }

    /// <summary>Format version and pack size from a repository's repo.json, read without opening it; (0, 0) when unreadable.</summary>
    private static (int Format, long PackSize) ReadFormat(string root)
    {
        try
        {
            var descriptor = System.Text.Json.JsonSerializer.Deserialize(
                File.ReadAllBytes(Path.Combine(root, LocalDirectoryRepository.DescriptorFileName)), Backdafuckup.Core.Repository.CoreJsonContext.Default.RepositoryDescriptor);
            return descriptor is null ? (0, 0) : (descriptor.FormatVersion, descriptor.Packs?.TargetSize ?? 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Adds restore points and SQL backups found in the repository but missing in the catalog (disaster recovery of the
    /// server). The snapshot fulls of imported restore points are recorded by <see cref="Sql.SqlCatalogBackfill"/>.
    /// </summary>
    internal static async Task<(int Imported, int Elsewhere)> ImportRestorePointsAsync(Guid repositoryId, IBackupRepository repository, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        // Ids are global: one catalogued in another repository is a copy (a move's, or a copied directory) and is never added twice.
        var known = (await db.RestorePoints.Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var here = (await db.RestorePoints.Where(r => r.RepositoryId == repositoryId).Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var imported = 0;
        var elsewhere = 0;
        await foreach (var info in repository.ListRestorePointsAsync(ct))
        {
            if (known.Contains(info.RestorePointId))
            {
                elsewhere += here.Contains(info.RestorePointId) ? 0 : 1;
                continue;
            }

            var job = info.Manifest.Job is { } j ? await db.Jobs.FindAsync([j.Id], ct) : null;
            var entity = new RestorePointEntity
            {
                Id = info.RestorePointId,
                RepositoryId = repositoryId,
                JobId = job?.Id,
                JobName = info.Manifest.Job?.Name ?? "Imported",
                ConnectionId = await db.Connections.Where(c => c.PlatformId == info.Manifest.Source.PlatformId).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct),
                PlatformId = info.Manifest.Source.PlatformId,
                VmNativeId = info.Manifest.Source.VmId,
                VmName = info.Manifest.Vm.Name,
            };
            VmBackupTask.ApplyManifest(entity, info.Manifest, info.Path);
            db.RestorePoints.Add(entity);
            imported++;
        }

        await db.SaveChangesAsync(ct);

        var knownSqlBackups = (await db.SqlBackups.Select(b => b.Id).ToListAsync(ct)).ToHashSet();
        var sqlHere = (await db.SqlBackups.Where(b => b.RepositoryId == repositoryId).Select(b => b.Id).ToListAsync(ct)).ToHashSet();
        await foreach (var info in repository.ListSqlBackupsAsync(ct))
        {
            if (knownSqlBackups.Contains(info.SqlBackupId))
            {
                elsewhere += sqlHere.Contains(info.SqlBackupId) ? 0 : 1;
                continue;
            }

            await Sql.SqlBackupCatalog.RecordSqlBackupAsync(db, repositoryId, info, time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            imported++;
        }

        return (imported, elsewhere);
    }

    /// <summary>
    /// Reads the inventory; a failure marks the connection as failed with the message (also right after it was added: it
    /// stays, shows the error and can be rescanned or removed). Returns the error, or null.
    /// </summary>
    /// <summary>A connection for the console; a cluster behind its agent with the agent's state.</summary>
    internal static ConnectionDto WithAgent(HypervisorConnection connection, Clusters.ClusterAgents agents) =>
        connection.Access == ConnectionAccess.Agent
            ? connection.ToDto() with { Agent = agents.Describe(connection.Id) ?? new ConnectionAgentDto(false, null, null, null, null, null, null) }
            : connection.ToDto();

    internal static async Task<string?> TryRefreshInventoryAsync(Guid connectionId, AppDbContext db, ConnectionManager manager, TimeProvider time, CancellationToken ct)
    {
        try
        {
            await RefreshInventoryAsync(connectionId, db, manager, time, ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Status fields only, without the row version: background services touch the same row.
            var name = await db.Connections.Where(c => c.Id == connectionId).Select(c => c.DisplayName).FirstOrDefaultAsync(ct);
            var error = $"Reading the inventory of {name ?? connectionId.ToString()} failed: {ex.Message}";
            await db.Connections.Where(c => c.Id == connectionId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, ConnectionStatus.Error).SetProperty(c => c.LastError, error), ct);
            return error;
        }
    }

    /// <summary>
    /// Brings a cluster's node agent to this server's mover image, when it has one and no session of a cluster runs (its pods
    /// restart, and a read in progress would fail). Best effort: a failure is logged, the rescan still succeeded.
    /// </summary>
    private static async Task UpdateNodeAgentAsync(Guid id, AppDbContext db, ConnectionManager manager, Movers.MoverRegistry images, ILogger logger, CancellationToken ct)
    {
        if (!await db.Connections.AnyAsync(c => c.Id == id && c.Kind == HypervisorKind.Kubernetes, ct)
            || await db.Sessions.AnyAsync(s => s.ObjectKind == RestorePointObjectKinds.KubernetesNamespace && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping), ct))
        {
            return;
        }

        try
        {
            await using var cluster = await manager.OpenAsync(id, ct);
            if (cluster is not Backdafuckup.Kubernetes.IKubernetesAgentUpdater updater)
            {
                return;
            }

            var mover = await images.EnsurePushedAsync(null, ct);
            if (await updater.UpdateNodeAgentImageAsync(mover.Image, ct) is { } previous)
            {
                logger.LogInformation("The node agent of connection {Connection} ran {Previous}; its DaemonSet now runs {Image}.", id, previous, mover.Image);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The node agent of connection {Connection} could not be brought to this server's version: {Error}", id, ex.Message);
        }
    }

    /// <summary>The cluster CA as a pin, or null when the PEM is missing, unreadable or not a CA certificate.</summary>
    private static PinnedCertificate? CaPin(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return null;
        }

        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem.Trim());
            return Backdafuckup.Kubernetes.KubernetesClients.IsAuthority(certificate) ? Backdafuckup.Core.Tls.CertificatePinning.ToPin("cluster CA", certificate) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static async Task RefreshInventoryAsync(Guid connectionId, AppDbContext db, ConnectionManager manager, TimeProvider time, CancellationToken ct)
    {
        await using var session = await manager.OpenAsync(connectionId, ct);
        var inventory = await session.GetInventoryAsync(ct);
        var now = time.GetUtcNow();

        // A cluster added through its agent learns which cluster it is at its first contact.
        var platform = inventory.Platform;
        if (await db.Connections.AnyAsync(c => c.Id == connectionId && c.PlatformId == null, ct) && !string.IsNullOrEmpty(platform.PlatformId))
        {
            var other = await db.Connections.Where(c => c.Id != connectionId && c.PlatformId == platform.PlatformId).Select(c => c.DisplayName).FirstOrDefaultAsync(ct);
            if (other is not null)
            {
                throw new InvalidOperationException($"This cluster ({platform.PlatformName}) is connected already, as '{other}'. Remove one of the two connections.");
            }

            await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.PlatformId, platform.PlatformId)
                .SetProperty(c => c.ProductName, platform.ProductName), ct);
        }

        var existing = await db.Inventory.Where(i => i.ConnectionId == connectionId).ToListAsync(ct);
        db.Inventory.RemoveRange(existing.Where(e => !inventory.Items.Any(i => i.Kind == e.Kind && i.NativeId == e.NativeId)));
        foreach (var item in inventory.Items)
        {
            var entry = existing.FirstOrDefault(e => e.Kind == item.Kind && e.NativeId == item.NativeId);
            if (entry is null)
            {
                entry = new InventoryEntry { ConnectionId = connectionId, Kind = item.Kind, NativeId = item.NativeId, Name = item.Name };
                db.Inventory.Add(entry);
            }

            entry.Name = item.Name;
            entry.ParentNativeId = item.ParentNativeId;
            entry.Properties = new Dictionary<string, string>(item.Properties);
            entry.RefreshedUtc = now;
        }

        await db.SaveChangesAsync(ct);

        // A cluster's platforms and the product name they give it ("RKE2 · Rancher"), at every scan.
        if (inventory.Platforms is { } detected)
        {
            var list = detected.ToList();
            await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.DetectedPlatforms, list)
                .SetProperty(c => c.ProductName, platform.ProductName), ct);
        }

        // Status fields only, without the row version: background services touch the same row.
        await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.PlatformName, inventory.Platform.PlatformName)
            .SetProperty(c => c.ProductVersion, inventory.Platform.ProductVersion)
            .SetProperty(c => c.Status, ConnectionStatus.Connected)
            .SetProperty(c => c.LastError, (string?)null)
            .SetProperty(c => c.LastConnectedUtc, now), ct);
    }
}
