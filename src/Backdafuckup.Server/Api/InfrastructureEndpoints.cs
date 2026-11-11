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

        connections.MapGet("/", async (AppDbContext db, string? kind, CancellationToken ct) =>
        {
            var query = db.Connections.Include(c => c.Credential).Include(c => c.Hosts).AsQueryable();
            if (!string.IsNullOrEmpty(kind))
            {
                if (!Enum.TryParse<HypervisorKind>(kind, ignoreCase: true, out var parsed))
                {
                    return Results.Problem($"Unknown connection kind '{kind}' (XenServer or Kubernetes).", statusCode: StatusCodes.Status400BadRequest);
                }

                query = query.Where(c => c.Kind == parsed);
            }

            return Results.Ok((await query.OrderBy(c => c.DisplayName).ToListAsync(ct)).Select(c => c.ToDto()));
        }).WithDescription("All connections: pools and Kubernetes clusters. kind=XenServer or kind=Kubernetes returns one kind only.");

        connections.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct) is { } c ? Results.Ok(c.ToDto()) : Results.NotFound());

        connections.MapPost("/probe", async (ProbeRequestDto request, ConnectionManager manager, CancellationToken ct) =>
        {
            if (!Enum.TryParse<HypervisorKind>(request.Kind, ignoreCase: true, out var kind))
            {
                return Results.Problem($"Unknown connection kind '{request.Kind}' (XenServer or Kubernetes).", statusCode: StatusCodes.Status400BadRequest);
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
            if (!Enum.TryParse<HypervisorKind>(request.Kind, ignoreCase: true, out var kind))
            {
                return Results.Problem($"Unknown connection kind '{request.Kind}' (XenServer or Kubernetes).", statusCode: StatusCodes.Status400BadRequest);
            }

            var noun = kind == HypervisorKind.Kubernetes ? "cluster" : "pool";
            if (await db.Connections.AnyAsync(c => c.DisplayName == request.DisplayName, ct))
            {
                return Results.Problem($"A connection named '{request.DisplayName}' already exists.", statusCode: StatusCodes.Status409Conflict);
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
                new Dictionary<string, string>());
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

        connections.MapPost("/{id:guid}/rescan", async (Guid id, AppDbContext db, ConnectionManager manager, TimeProvider time, CancellationToken ct) =>
        {
            return await TryRefreshInventoryAsync(id, db, manager, time, ct) is { } error
                ? Results.Problem(error, statusCode: StatusCodes.Status502BadGateway)
                : Results.NoContent();
        });

        connections.MapGet("/{id:guid}/inventory", async (Guid id, AppDbContext db, CancellationToken ct) =>
            (await db.Inventory.Where(i => i.ConnectionId == id).OrderBy(i => i.Kind).ThenBy(i => i.Name).ToListAsync(ct)).Select(i => i.ToDto()));

        app.MapGet("/api/kubernetes/setup.yaml", (bool? restore, bool? download) =>
            {
                var manifest = Backdafuckup.Kubernetes.KubernetesSetup.Manifest(restore != false);
                return download == true
                    ? Results.File(System.Text.Encoding.UTF8.GetBytes(manifest), "application/yaml", Backdafuckup.Kubernetes.KubernetesSetup.FileName)
                    : Results.Text(manifest, "application/yaml");
            })
            .WithDescription("The objects a Kubernetes cluster needs for backdafuckup: work namespace, ServiceAccount with a token, backup and (unless restore=false) restore roles. download=true returns it as a file.");

        var repositories = app.MapGroup("/api/repositories");

        repositories.MapGet("/", async (AppDbContext db, CancellationToken ct) => await ListRepositoriesAsync(db, ct));

        repositories.MapPost("/", async (CreateRepositoryDto request, AppDbContext db, TimeProvider time, Sql.SqlCatalogBackfill backfill, CancellationToken ct) =>
        {
            if (await db.Repositories.AnyAsync(r => r.Name == request.Name, ct))
            {
                return Results.Problem($"A repository named '{request.Name}' already exists.", statusCode: StatusCodes.Status409Conflict);
            }

            var path = Path.GetFullPath(request.Path.Trim());
            var import = File.Exists(Path.Combine(path, LocalDirectoryRepository.DescriptorFileName));
            LocalDirectoryRepository repository;
            try
            {
                repository = import
                    ? await LocalDirectoryRepository.OpenAsync(path, ct, time)
                    : await LocalDirectoryRepository.CreateAsync(path, ct, request.ChunkSizeKiB * 1024, request.CompressionLevel, time);
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
                    await ImportRestorePointsAsync(entity.Id, repository, db, time, ct);
                }

                created = entity;
            }

            // Only now: reading the restore points' SQL Server snapshots opens the repository again, which its owner
            // lock refuses while it is open here.
            if (import)
            {
                await backfill.RecordSnapshotFullsAsync(ct);
            }

            return Results.Created($"/api/repositories/{created.Id}", created.ToDto(0, 0));
        });

        repositories.MapPost("/{id:guid}/gc", async (Guid id, AppDbContext db, SessionQueue queue, CancellationToken ct) =>
            await db.Repositories.FindAsync([id], ct) is { } repository
                ? Results.Ok(new StartedDto(await queue.EnqueueMaintenanceAsync(id, repository.Name, "User", ct)))
                : Results.NotFound());

        repositories.MapPost("/{id:guid}/rescan", async (Guid id, AppDbContext db, RepositoryManager manager, TimeProvider time, Sql.SqlCatalogBackfill backfill, CancellationToken ct) =>
        {
            var repository = await manager.GetAsync(id, ct);
            var imported = await ImportRestorePointsAsync(id, repository, db, time, ct);
            await backfill.RecordSnapshotFullsAsync(ct);
            return Results.Ok(new { imported });
        });

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

    public static async Task<List<RepositoryDto>> ListRepositoriesAsync(AppDbContext db, CancellationToken ct)
    {
        var repositories = await db.Repositories.OrderBy(r => r.Name).ToListAsync(ct);
        var stats = await db.RestorePoints.Where(r => r.State == RestorePointState.Available)
            .GroupBy(r => r.RepositoryId).Select(g => new { g.Key, Count = g.Count(), Bytes = g.Sum(r => r.LogicalBytes) }).ToListAsync(ct);
        foreach (var repository in repositories)
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

        return repositories.Select(r =>
        {
            var s = stats.FirstOrDefault(x => x.Key == r.Id);
            return r.ToDto(s?.Count ?? 0, s?.Bytes ?? 0);
        }).ToList();
    }

    /// <summary>
    /// Adds restore points and SQL backups found in the repository but missing in the catalog (disaster recovery of the
    /// server). The snapshot fulls of imported restore points are recorded by <see cref="Sql.SqlCatalogBackfill"/>.
    /// </summary>
    private static async Task<int> ImportRestorePointsAsync(Guid repositoryId, IBackupRepository repository, AppDbContext db, TimeProvider time, CancellationToken ct)
    {
        var known = await db.RestorePoints.Where(r => r.RepositoryId == repositoryId).Select(r => r.Id).ToListAsync(ct);
        var imported = 0;
        await foreach (var info in repository.ListRestorePointsAsync(ct))
        {
            if (known.Contains(info.RestorePointId))
            {
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

        var knownSqlBackups = await db.SqlBackups.Where(b => b.RepositoryId == repositoryId).Select(b => b.Id).ToListAsync(ct);
        await foreach (var info in repository.ListSqlBackupsAsync(ct))
        {
            if (knownSqlBackups.Contains(info.SqlBackupId))
            {
                continue;
            }

            await Sql.SqlBackupCatalog.RecordSqlBackupAsync(db, repositoryId, info, time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
            imported++;
        }

        return imported;
    }

    /// <summary>
    /// Reads the inventory; a failure marks the connection as failed with the message (also right after it was added: it
    /// stays, shows the error and can be rescanned or removed). Returns the error, or null.
    /// </summary>
    private static async Task<string?> TryRefreshInventoryAsync(Guid connectionId, AppDbContext db, ConnectionManager manager, TimeProvider time, CancellationToken ct)
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

        // Status fields only, without the row version: background services touch the same row.
        await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.PlatformName, inventory.Platform.PlatformName)
            .SetProperty(c => c.ProductVersion, inventory.Platform.ProductVersion)
            .SetProperty(c => c.Status, ConnectionStatus.Connected)
            .SetProperty(c => c.LastError, (string?)null)
            .SetProperty(c => c.LastConnectedUtc, now), ct);
    }
}
