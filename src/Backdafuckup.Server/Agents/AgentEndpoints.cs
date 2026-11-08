using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Agents;

public static class AgentEndpoints
{
    /// <summary>
    /// <c>/api/agent/*</c>: the interface of the agents. It ignores the user sign-in and authenticates with the agent key
    /// (<see cref="AgentProtocol.KeyHeader"/>) alone; the enrollment with the enrollment secret.
    /// <c>/api/agents/*</c>: management in the console (signed-in users).
    /// </summary>
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/api/agent").AllowAnonymous();

        agent.MapPost("/enroll", async (AgentEnrollRequest request, AgentService agents, HttpContext http, CancellationToken ct) =>
            await agents.EnrollAsync(request, http.Connection.RemoteIpAddress?.ToString(), ct) is { } response
                ? Results.Json(response, AgentJson.Options)
                : Results.Problem("The enrollment secret is not valid (renewed in the console?).", statusCode: StatusCodes.Status403Forbidden));

        agent.MapPost("/poll", async (
            AgentPollRequest request, AgentService agents, AgentRegistry registry, AgentPackages packages, Sql.SqlJobs sqlJobs, Publishing.DiskPublishes publishes,
            FileRestore.AgentTransfers transfers, Sql.SqlPublishLeftovers leftovers, IHostApplicationLifetime lifetime, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            await agents.UpdateAsync(agentId, request.Host ?? new AgentHostInfo(), http.Connection.RemoteIpAddress?.ToString(), ct);
            registry.SetCapabilities(agentId, request.Host?.Capabilities);
            registry.SetReportedWork(agentId, request.Busy);
            if (registry.ObserveRun(agentId, request.RunId))
            {
                // A new process: what the previous one ran is gone (its journals clean up the guest at its start).
                sqlJobs.AgentRestarted(agentId);
                publishes.AgentRestarted(agentId);
                transfers.AgentRestarted(agentId);
            }

            // Databases that publishes left attached after their session ended: removed with the session's account.
            leftovers.Report(agentId, request.LeftoverDatabases);

            // A different set of files on the server (after a server update): offer the update, but never during a backup
            // operation or while commands wait (a restart between freeze and thaw would break the freeze).
            if (request.Host is { FilesHash.Length: > 0 } host && packages.Get(host.Build) is { } build && build.FilesHash != host.FilesHash
                && !registry.IsBusy(agentId) && request.Busy is null && !sqlJobs.BackupRunning(agentId))
            {
                return Results.Json(new AgentPollResponse { Update = build.ToOffer() }, AgentJson.Options);
            }

            // The long poll ends when the service stops: otherwise the shutdown waits for it (up to its timeout of 30 s).
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            AgentCommand? command;
            try
            {
                command = await registry.NextCommandAsync(agentId, agents.Options.PollHold, poll.Token);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Results.NoContent();
            }

            return command is null ? Results.NoContent() : Results.Json(new AgentPollResponse { Command = command }, AgentJson.Options);
        });

        agent.MapPost("/result", async (AgentCommandResult result, AgentService agents, AgentRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            registry.Complete(agentId, result);
            return Results.NoContent();
        });

        agent.MapGet("/files/{build}/{name}", async (string build, string name, AgentService agents, AgentPackages packages, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is null)
            {
                return Results.Unauthorized();
            }

            return packages.FilePath(build, name) is { } path ? Results.File(path, "application/octet-stream") : Results.NotFound();
        });

        MapTransfers(agent);
        MapPublishes(agent);

        var console = app.MapGroup("/api/agents");

        console.MapGet("/", async (AppDbContext db, AgentService agents, AgentRegistry registry, AgentPackages packages, CancellationToken ct) =>
        {
            var vms = await db.Inventory.AsNoTracking().Where(i => i.Kind == InventoryKind.Vm).ToListAsync(ct);
            var list = await db.Agents.AsNoTracking().OrderBy(a => a.HostName).ToListAsync(ct);
            return list.Select(a => a.ToDto(
                agents.IsOnline(a.Id), vms.FirstOrDefault(v => v.NativeId == a.VmNativeId)?.Name, packages.Get(a.Build)?.FilesHash, registry.HasCapability(a.Id, AgentProtocol.CapabilityRestoreFiles),
                registry.HasCapability(a.Id, AgentProtocol.CapabilitySqlRestore),
                registry.HasCapability(a.Id, AgentProtocol.CapabilitySqlPublish),
                registry.HasCapability(a.Id, AgentProtocol.CapabilitySqlAttachFiles))).ToList();
        });

        console.MapGet("/settings", async (AgentSettingsStore settings, AgentPackages packages, CancellationToken ct) => new AgentSettingsDto
        {
            ServerUrl = await settings.GetServerUrlAsync(ct),
            EnrollmentSecret = await settings.GetEnrollmentSecretAsync(ct),
            ServerPins = [.. settings.ServerPins()],
            Builds = new[] { AgentProtocol.BuildNet10, AgentProtocol.BuildNet48 }.Select(packages.Get).OfType<AgentBuild>()
                .Select(b => new AgentBuildDto(b.Name, b.Version, b.Files.Sum(f => f.Size), b.FilesHash)).ToList(),
            PackageDirectory = packages.Root,
        });

        console.MapPut("/settings", async ([FromBody] AgentSettingsUpdateDto request, AgentSettingsStore settings, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(request.ServerUrl?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            {
                return Results.Problem("Enter the address as https://host:port/, as the guests reach the backup server.", statusCode: StatusCodes.Status400BadRequest);
            }

            await settings.SetServerUrlAsync(url.ToString(), ct);
            return Results.NoContent();
        });

        console.MapPost("/settings/renew-secret", async (AgentSettingsStore settings, CancellationToken ct) =>
        {
            await settings.RenewEnrollmentSecretAsync(ct);
            return Results.NoContent();
        });

        console.MapGet("/package", async (AgentSettingsStore settings, AgentPackages packages, HttpContext http, CancellationToken ct) =>
        {
            if (packages.Get(AgentProtocol.BuildNet10) is null && packages.Get(AgentProtocol.BuildNet48) is null)
            {
                return Results.Problem($"No agent build found in {packages.Root} (scripts/build-agent.sh).", statusCode: StatusCodes.Status404NotFound);
            }

            AgentConfig config;
            try
            {
                config = await settings.CreateAgentConfigAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"backdafuckup-agent-setup.zip\"";
            await packages.WriteSetupZipAsync(http.Response.Body, config, ct);
            return Results.Empty;
        });

        console.MapPost("/install", async (InstallAgentsDto request, AppDbContext db, AgentSettingsStore settings, AgentPackages packages, SessionQueue queue, HttpContext http, CancellationToken ct) =>
        {
            if (request.Vms is not { Count: > 0 })
            {
                return Results.Problem("Select at least one VM.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!await db.Credentials.AnyAsync(c => c.Id == request.CredentialId && c.Kind == CredentialKind.Windows, ct))
            {
                return Results.Problem("Select Windows credentials (Settings → Windows credentials).", statusCode: StatusCodes.Status400BadRequest);
            }

            if (await settings.GetServerUrlAsync(ct) is null)
            {
                return Results.Problem("Set the address under which the guests reach this server first (Guest agents → Settings).", statusCode: StatusCodes.Status409Conflict);
            }

            if (packages.Get(AgentProtocol.BuildNet10) is null && packages.Get(AgentProtocol.BuildNet48) is null)
            {
                return Results.Problem($"No agent build found in {packages.Root} (scripts/build-agent.sh).", statusCode: StatusCodes.Status409Conflict);
            }

            var sessionId = await queue.EnqueueAgentDeploymentAsync(request, http.User.Identity?.Name ?? "unknown", ct);
            return Results.Ok(new StartedDto(sessionId));
        });

        var credentials = app.MapGroup("/api/credentials");

        credentials.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            (await db.Credentials.AsNoTracking().Where(c => c.Kind == CredentialKind.Windows).OrderBy(c => c.Username).ToListAsync(ct))
                .Select(c => new WindowsCredentialDto(c.Id, c.Username, c.Description, c.CreatedUtc)).ToList());

        credentials.MapPost("/", async (SaveWindowsCredentialDto request, AppDbContext db, CredentialProtector protector, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Problem("User name and password are required (DOMAIN\\user, user@domain or .\\user for a local account).", statusCode: StatusCodes.Status400BadRequest);
            }

            var now = time.GetUtcNow();
            var credential = new Credential
            {
                Kind = CredentialKind.Windows,
                Username = request.UserName.Trim(),
                ProtectedSecret = protector.Protect(request.Password),
                Description = request.Description,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.Credentials.Add(credential);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new WindowsCredentialDto(credential.Id, credential.Username, credential.Description, credential.CreatedUtc));
        });

        credentials.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await Api.CredentialUse.DescribeAsync(db, id, ct) is { } used)
            {
                return Results.Problem(used, statusCode: StatusCodes.Status409Conflict);
            }

            var deleted = await db.Credentials.Where(c => c.Id == id && c.Kind == CredentialKind.Windows).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        console.MapPost("/{id:guid}/test", async (Guid id, AppDbContext db, AgentService agents, AgentRegistry registry, CancellationToken ct) =>
        {
            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (agent is null)
            {
                return Results.NotFound();
            }

            if (!agents.IsOnline(id))
            {
                return Results.Problem($"The agent on {agent.HostName} ({agent.RemoteAddress}) is offline.", statusCode: StatusCodes.Status409Conflict);
            }

            // Prepare, freeze and thaw at once: writes are held only for a moment; no VM snapshot is taken.
            var lines = new List<string>();
            var quiescer = new AgentQuiescer(registry, agents.Options, id, $"agent on {agent.HostName} ({agent.RemoteAddress})");
            try
            {
                GuestFreezeResult result;
                await using (var freeze = await quiescer.FreezeAsync(new ListOperationLog(lines), ct))
                {
                    result = await freeze.ThawAsync(snapshotTaken: false, ct);
                }

                var writers = string.Join(", ", result.Writers.Select(w => w.Failure is null ? $"{w.Name} ({w.State})" : $"{w.Name} ({w.State}: {w.Failure})"));
                return Results.Ok(result.ApplicationConsistent
                    ? new AgentTestResultDto(true, $"VSS works: froze in {result.FreezeTime.TotalSeconds:0.0} s, writes held {result.HeldTime.TotalSeconds:0.0} s. Writers: {writers}.", lines)
                    : new AgentTestResultDto(false, $"VSS did not complete: {result.Reason} Writers: {writers}.", lines));
            }
            catch (GuestQuiesceException ex)
            {
                return Results.Ok(new AgentTestResultDto(false, ex.Message, lines));
            }
        });

        console.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            // Forgets the agent: its key stops working; a still installed agent enrolls again with the enrollment secret.
            var deleted = await db.Agents.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    private sealed class ListOperationLog(List<string> lines) : Backdafuckup.Core.Operations.IOperationLog
    {
        public void Write(Backdafuckup.Core.Operations.LogSeverity severity, string message, string? key = null)
        {
            lock (lines)
            {
                lines.Add($"{severity}: {message}");
            }
        }
    }

    /// <summary>File restores into the guest (<see cref="AgentProtocol.TransfersPath"/>); only for the agent the transfer belongs to.</summary>
    private static void MapTransfers(RouteGroupBuilder agent)
    {
        static async Task<(IResult? Error, FileRestore.AgentTransfer? Transfer)> FindAsync(Guid id, AgentService agents, FileRestore.AgentTransfers transfers, HttpContext http, CancellationToken ct)
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return (Results.Unauthorized(), null);
            }

            return transfers.Get(id, agentId) is { } transfer
                ? (null, transfer)
                : (Results.Problem("The restore is not running (any more): stopped, finished, or the server was restarted.", statusCode: StatusCodes.Status404NotFound), null);
        }

        // SQL jobs: the agent fetches the request (with the account's password, only over this pinned HTTPS channel) and
        // posts the result. A job belongs to one agent; 404 after it ended or the server restarted.
        agent.MapGet("/sql-jobs/{id:guid}", async (Guid id, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            return jobs.Get(id, agentId) is { } request
                ? Results.Json(request, AgentJson.Options)
                : Results.Problem("The SQL job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
        });

        agent.MapPost("/sql-jobs/{id:guid}/progress", async (Guid id, SqlSnapshotProgress progress, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            return jobs.Progress(id, agentId, progress) ? Results.NoContent() : Results.Problem("The SQL job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
        });

        // Polled by a snapshot-suspend job every 200 ms while SQL Server's writes are suspended; 404 tells it to resume.
        agent.MapGet("/sql-jobs/{id:guid}/signal", async (Guid id, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            return jobs.Signal(id, agentId) is { } signal
                ? Results.Json(new SqlJobSignal { Signal = signal }, AgentJson.Options)
                : Results.Problem("The SQL job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
        });

        // A backup-database job with upload sends its .bak here; it is filed under the export session (UploadId).
        agent.MapPut("/sql-jobs/{id:guid}/file", async (Guid id, string? name, AgentService agents, Sql.SqlJobs jobs, Sql.SqlExportFiles files, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            if (jobs.Get(id, agentId) is not { Kind: SqlJobKinds.BackupDatabase, Upload: true, UploadId: { } session })
            {
                return Results.Problem("The SQL job is not running (any more) or sends no file.", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                await files.SaveAsync(session, name ?? string.Empty, http.Request.Body, http.Request.ContentLength, ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (IOException ex)
            {
                return Results.Problem($"The backup server could not store the file: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // A log or full backup job sends each backup file here, streamed into the repository (checked by length and SHA-256).
        agent.MapPut("/sql-jobs/{id:guid}/backups/{fileId}", async (Guid id, string fileId, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            if (jobs.Receiver(id, agentId) is not { } receiver)
            {
                return Results.Problem("The SQL backup job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
            }

            SqlBackupUpload? upload;
            try
            {
                upload = System.Text.Json.JsonSerializer.Deserialize<SqlBackupUpload>(
                    Convert.FromBase64String(http.Request.Headers[AgentProtocol.BackupUploadHeader].ToString()), AgentJson.Options);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                return Results.Problem($"The upload's description is unreadable: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }

            if (upload is null || upload.FileId != fileId)
            {
                return Results.Problem("The upload's description does not match the file.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                return Results.Json(await receiver.ReceiveAsync(upload, http.Request.Body, ct), AgentJson.Options);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
            {
                return Results.Problem($"The backup server could not store {upload.FileName}: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());

        // A restore job downloads the backup files it applies (from the repository, checked chunk by chunk and by SHA-256).
        agent.MapGet("/sql-jobs/{id:guid}/files/{fileId}", async (Guid id, string fileId, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            if (jobs.File(id, agentId, fileId) is not { } file)
            {
                return Results.Problem("The SQL job is not running (any more) or has no such file.", statusCode: StatusCodes.Status404NotFound);
            }

            await using var reader = await file.Repository.OpenSqlBackupAsync(file.SqlBackupId, ct);
            await using var stream = await reader.OpenFileAsync(file.Key, ct);
            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = file.Length;
            await stream.CopyToAsync(http.Response.Body, 1 << 20, ct);
            return Results.Empty;
        });

        agent.MapPost("/sql-jobs/{id:guid}/backups/commit", async (Guid id, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            if (jobs.Receiver(id, agentId) is not { } receiver)
            {
                return Results.Problem("The SQL backup job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                return Results.Json(await receiver.CommitAsync(ct), AgentJson.Options);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or DbUpdateException)
            {
                return Results.Problem($"The backup server could not commit the backup files: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        agent.MapPost("/sql-jobs/{id:guid}/result", async (Guid id, SqlJobResult result, AgentService agents, Sql.SqlJobs jobs, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            return jobs.Complete(id, agentId, result) ? Results.NoContent() : Results.Problem("The SQL job is not running (any more).", statusCode: StatusCodes.Status404NotFound);
        });

        agent.MapGet("/transfers/{id:guid}", async (Guid id, AgentService agents, FileRestore.AgentTransfers transfers, HttpContext http, CancellationToken ct) =>
        {
            var (error, transfer) = await FindAsync(id, agents, transfers, http, ct);
            if (transfer is null)
            {
                return error!;
            }

            transfer.LastHeartbeat = transfers.Time.GetUtcNow();
            return Results.Json(transfer.Info, AgentJson.Options);
        });

        agent.MapGet("/transfers/{id:guid}/items", async (Guid id, int? offset, int? limit, AgentService agents, FileRestore.AgentTransfers transfers, HttpContext http, CancellationToken ct) =>
        {
            var (error, transfer) = await FindAsync(id, agents, transfers, http, ct);
            return transfer is null ? error! : Results.Json(transfers.Page(transfer, offset ?? 0, limit ?? 500), AgentJson.Options);
        });

        agent.MapGet("/transfers/{id:guid}/items/{index:int}/content", async (Guid id, int index, AgentService agents, FileRestore.AgentTransfers transfers, HttpContext http, CancellationToken ct) =>
        {
            var (error, transfer) = await FindAsync(id, agents, transfers, http, ct);
            if (transfer is null)
            {
                return error!;
            }

            try
            {
                await transfers.StreamContentAsync(transfer, index, http.Response, ct);
                return Results.Empty;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FileNotFoundException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (FileLevel.GuestFileUnsupportedException ex)
            {
                return Results.Problem($"{ex.Path} cannot be restored: {ex.Reason}.", statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                return Results.Problem($"Reading it from the backup failed: {ex.Message}", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        agent.MapPost("/transfers/{id:guid}/progress", async (Guid id, FileTransferProgress progress, AgentService agents, FileRestore.AgentTransfers transfers, HttpContext http, CancellationToken ct) =>
        {
            var (error, transfer) = await FindAsync(id, agents, transfers, http, ct);
            return transfer is null ? error! : Results.Json(transfers.Report(transfer, progress), AgentJson.Options);
        });
    }

    /// <summary>
    /// Publishes (<see cref="AgentProtocol.PublishesPath"/>): what to publish, the disk content block by block, and the
    /// agent's reports. Only for the agent the publish belongs to; 404 after it ended or the server restarted, which
    /// makes the agent unpublish.
    /// </summary>
    private static void MapPublishes(RouteGroupBuilder agent)
    {
        static async Task<(IResult? Error, Publishing.DiskPublish? Publish)> FindAsync(Guid id, AgentService agents, Publishing.DiskPublishes publishes, HttpContext http, CancellationToken ct)
        {
            if (await agents.AuthenticateCachedAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return (Results.Unauthorized(), null);
            }

            return publishes.Get(id, agentId) is { } publish
                ? (null, publish)
                : (Results.Problem("The publish is not running (any more): stopped, or the server was restarted.", statusCode: StatusCodes.Status404NotFound), null);
        }

        agent.MapGet("/publishes/{id:guid}", async (Guid id, AgentService agents, Publishing.DiskPublishes publishes, HttpContext http, CancellationToken ct) =>
        {
            var (error, publish) = await FindAsync(id, agents, publishes, http, ct);
            if (publish is null)
            {
                return error!;
            }

            publish.LastHeartbeat = publishes.Time.GetUtcNow();
            return Results.Json(publish.Info, AgentJson.Options);
        });

        // A publish at a point in time downloads the log backups it applies (from the repository, checked like a restore job's).
        agent.MapGet("/publishes/{id:guid}/files/{fileId}", async (Guid id, string fileId, AgentService agents, Publishing.DiskPublishes publishes, HttpContext http, CancellationToken ct) =>
        {
            var (error, publish) = await FindAsync(id, agents, publishes, http, ct);
            if (publish is null)
            {
                return error!;
            }

            if (publish.Files is not { } files || !files.TryGetValue(fileId, out var file))
            {
                return Results.Problem("The publish has no such file.", statusCode: StatusCodes.Status404NotFound);
            }

            publish.LastHeartbeat = publishes.Time.GetUtcNow();
            await using var reader = await file.Repository.OpenSqlBackupAsync(file.SqlBackupId, ct);
            await using var stream = await reader.OpenFileAsync(file.Key, ct);
            http.Response.ContentType = "application/octet-stream";
            http.Response.ContentLength = file.Length;
            await stream.CopyToAsync(http.Response.Body, 1 << 20, ct);
            return Results.Empty;
        });

        agent.MapGet("/publishes/{id:guid}/disks/{disk:int}/blocks/{block:long}", async (Guid id, int disk, long block, AgentService agents, Publishing.DiskPublishes publishes, HttpContext http, CancellationToken ct) =>
        {
            var (error, publish) = await FindAsync(id, agents, publishes, http, ct);
            if (publish is null)
            {
                return error!;
            }

            try
            {
                http.Response.ContentType = "application/octet-stream";
                await publishes.CopyBlockAsync(publish, disk, block, http.Response.Body, ct);
                return Results.Empty;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
            {
                publish.Log($"Reading block {block} of disk {disk} from the backup failed: {ex.Message}");
                return Results.Problem($"Reading it from the backup failed: {ex.Message}", statusCode: StatusCodes.Status422UnprocessableEntity);
            }
        });

        agent.MapPost("/publishes/{id:guid}/status", async (Guid id, PublishStatus status, AgentService agents, Publishing.DiskPublishes publishes, HttpContext http, CancellationToken ct) =>
        {
            var (error, publish) = await FindAsync(id, agents, publishes, http, ct);
            return publish is null ? error! : Results.Json(publishes.Report(publish, status), AgentJson.Options);
        });
    }

    private static AgentDto ToDto(this AgentEntity a, bool online, string? vmName, string? currentFilesHash, bool canRestoreFiles, bool canRunSql, bool canPublishSql, bool canRestoreSql) => new()
    {
        Id = a.Id,
        HostName = a.HostName,
        DnsName = a.DnsName,
        VmNativeId = a.VmNativeId,
        VmName = vmName,
        OsName = a.OsName,
        OsVersion = a.OsVersion,
        Build = a.Build,
        Version = a.AgentVersion,
        VssProvider = a.VssProvider,
        IpAddresses = a.IpAddresses,
        RemoteAddress = a.RemoteAddress,
        Online = online,
        UpToDate = currentFilesHash is null || currentFilesHash == a.FilesHash,
        EnrolledUtc = a.EnrolledUtc,
        LastContactUtc = a.LastContactUtc,
        CanRestoreFiles = canRestoreFiles,
        CanRunSql = canRunSql,
        CanPublishSql = canPublishSql,
        CanRestoreSql = canRestoreSql,
        DomainController = a.DomainRole is 4 or 5,
        Domain = a.Domain,
    };
}
