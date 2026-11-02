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

        agent.MapPost("/poll", async (AgentPollRequest request, AgentService agents, AgentRegistry registry, AgentPackages packages, HttpContext http, CancellationToken ct) =>
        {
            if (await agents.AuthenticateAsync(http.Request.Headers[AgentProtocol.KeyHeader], ct) is not { } agentId)
            {
                return Results.Unauthorized();
            }

            await agents.UpdateAsync(agentId, request.Host ?? new AgentHostInfo(), http.Connection.RemoteIpAddress?.ToString(), ct);

            // A different set of files on the server (after a server update): offer the update, but never during a backup
            // operation or while commands wait (a restart between freeze and thaw would break the freeze).
            if (request.Host is { FilesHash.Length: > 0 } host && packages.Get(host.Build) is { } build && build.FilesHash != host.FilesHash
                && !registry.IsBusy(agentId))
            {
                return Results.Json(new AgentPollResponse { Update = build.ToOffer() }, AgentJson.Options);
            }

            var command = await registry.NextCommandAsync(agentId, agents.Options.PollHold, ct);
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

        var console = app.MapGroup("/api/agents");

        console.MapGet("/", async (AppDbContext db, AgentService agents, AgentPackages packages, CancellationToken ct) =>
        {
            var vms = await db.Inventory.AsNoTracking().Where(i => i.Kind == InventoryKind.Vm).ToListAsync(ct);
            var list = await db.Agents.AsNoTracking().OrderBy(a => a.HostName).ToListAsync(ct);
            return list.Select(a => a.ToDto(agents.IsOnline(a.Id), vms.FirstOrDefault(v => v.NativeId == a.VmNativeId)?.Name, packages.Get(a.Build)?.FilesHash)).ToList();
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
                    result = await freeze.ThawAsync(ct);
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

    private static AgentDto ToDto(this AgentEntity a, bool online, string? vmName, string? currentFilesHash) => new()
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
    };
}
