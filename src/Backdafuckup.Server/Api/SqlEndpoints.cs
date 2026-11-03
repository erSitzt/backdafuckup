using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Sql;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>SQL Server logins (/api/credentials/sql) and what the agent in a VM finds of SQL Server (/api/sql/inventory).</summary>
public static class SqlEndpoints
{
    public static void MapSqlEndpoints(this IEndpointRouteBuilder app)
    {
        var logins = app.MapGroup("/api/credentials/sql");

        logins.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            (await db.Credentials.AsNoTracking().Where(c => c.Kind == CredentialKind.SqlServer).OrderBy(c => c.Username).ToListAsync(ct))
                .Select(c => new SqlCredentialDto(c.Id, c.Username, c.Description, c.CreatedUtc)).ToList());

        logins.MapPost("/", async (SaveSqlCredentialDto request, AppDbContext db, CredentialProtector protector, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Problem("Enter the login name and its password.", statusCode: StatusCodes.Status400BadRequest);
            }

            var now = time.GetUtcNow();
            var credential = new Credential
            {
                Kind = CredentialKind.SqlServer,
                Username = request.UserName.Trim(),
                ProtectedSecret = protector.Protect(request.Password),
                Description = request.Description,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.Credentials.Add(credential);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SqlCredentialDto(credential.Id, credential.Username, credential.Description, credential.CreatedUtc));
        });

        logins.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Credentials.Where(c => c.Id == id && c.Kind == CredentialKind.SqlServer).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/sql/publishes", StartPublishAsync);
        app.MapPost("/api/sql/restores", StartRestoreAsync);

        app.MapPost("/api/sql/inventory", async (
            SqlInventoryRequestDto request, AgentService agents, AgentRegistry registry, SqlJobs jobs, IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, CancellationToken ct) =>
        {
            try
            {
                var agent = await SqlAgentAsync(request.VmNativeId, agents, registry, ct);
                var account = await AccountAsync(request.CredentialId, dbFactory, protector, ct);
                var result = await jobs.RunAsync(agent.Id, new SqlJobRequest { Kind = SqlJobKinds.Inventory, Instance = request.Instance, Account = account }, TimeSpan.FromMinutes(2), ct);
                if (!result.Success || result.Inventory is null)
                {
                    return Results.Problem($"The agent in {agent.HostName} could not list SQL Server: {result.Error ?? "no answer"}", statusCode: StatusCodes.Status409Conflict);
                }

                return Results.Ok(ToDto(agent, account, result.Inventory));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (TimeoutException)
            {
                return Results.Problem("The agent did not answer in time; it may be busy or offline.", statusCode: StatusCodes.Status504GatewayTimeout);
            }
        });
    }

    /// <summary>
    /// Starts a publish session. Checked here so that the wizard gets the reason at once: the restore point, the name,
    /// the time, the account and an agent that can publish; the agent checks the name on the instance once more.
    /// </summary>
    private static async Task<IResult> StartPublishAsync(
        SqlPublishRequestDto request, AppDbContext db, AgentService agents, AgentRegistry registry, SessionQueue queue, Microsoft.Extensions.Options.IOptions<SqlRestoreOptions> options, HttpContext http, CancellationToken ct)
    {
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, ct);
        if (point is null || point.State != RestorePointState.Available)
        {
            return Results.Problem("The restore point is not available (deleted, or still being written).", statusCode: StatusCodes.Status404NotFound);
        }

        var name = request.DatabaseName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 128 || name.Any(char.IsControl))
        {
            return Results.Problem("The database name must have 1 to 128 characters and no control characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var limits = options.Value;
        if (request.AutoUnpublishMinutes < limits.MinAutoUnpublishMinutes || request.AutoUnpublishMinutes > limits.MaxAutoUnpublishMinutes)
        {
            return Results.Problem(
                $"Unpublish after {limits.MinAutoUnpublishMinutes} minutes at the earliest and {limits.MaxAutoUnpublishMinutes / 60} hours at the latest.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.TargetInstance))
        {
            return Results.Problem("Choose the instance to publish to.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await db.Credentials.AnyAsync(c => c.Id == request.CredentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct))
        {
            return Results.Problem("The account no longer exists. Choose another one under Credentials.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var agent = await SqlAgentAsync(request.TargetVmNativeId, agents, registry, ct);
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlPublish))
            {
                return Results.Problem(
                    $"The agent in {agent.HostName} (version {agent.AgentVersion}) cannot publish SQL Server databases yet. It updates itself within a few minutes after a server update.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        var id = await queue.EnqueueSqlPublishAsync(request with { DatabaseName = name }, point.RepositoryId, point.VmName, http.User.Identity?.Name ?? "user", ct);
        return Results.Ok(new StartedDto(id));
    }

    /// <summary>
    /// Starts a database restore. Checked here: the restore point, the name, the file location and its folders, the
    /// account and an agent that can restore databases; the session checks the instance with the account.
    /// </summary>
    private static async Task<IResult> StartRestoreAsync(
        SqlRestoreRequestDto request, AppDbContext db, AgentService agents, AgentRegistry registry, SessionQueue queue, HttpContext http, CancellationToken ct)
    {
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, ct);
        if (point is null || point.State != RestorePointState.Available)
        {
            return Results.Problem("The restore point is not available (deleted, or still being written).", statusCode: StatusCodes.Status404NotFound);
        }

        var name = request.DatabaseName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 128 || name.Any(char.IsControl))
        {
            return Results.Problem("The database name must have 1 to 128 characters and no control characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.TargetInstance))
        {
            return Results.Problem("Choose the instance to restore to.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            // The folders are checked with placeholders for the instance defaults (only the agent knows them).
            SqlRestoreFiles.TargetPaths(name, [(@"C:\x\a.mdf", "data"), (@"C:\x\a.ldf", "log")], request.FileLocation, request.DataFolder, request.LogFolder, @"C:\default", @"C:\default");
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await db.Credentials.AnyAsync(c => c.Id == request.CredentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct))
        {
            return Results.Problem("The account no longer exists. Choose another one under Credentials.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var agent = await SqlAgentAsync(request.TargetVmNativeId, agents, registry, ct);
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlAttachFiles))
            {
                return Results.Problem(
                    $"The agent in {agent.HostName} (version {agent.AgentVersion}) cannot restore SQL Server databases yet. It updates itself within a few minutes after a server update.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        var id = await queue.EnqueueSqlRestoreAsync(request with { DatabaseName = name }, point.RepositoryId, point.VmName, http.User.Identity?.Name ?? "user", ct);
        return Results.Ok(new StartedDto(id));
    }

    /// <summary>The online agent of a VM that runs SQL jobs, or a message that says what is missing.</summary>
    internal static async Task<AgentEntity> SqlAgentAsync(string vmNativeId, AgentService agents, AgentRegistry registry, CancellationToken ct)
    {
        var agent = await agents.FindForVmAsync(vmNativeId, ct)
            ?? throw new InvalidOperationException("No backdafuckup agent is installed in this VM. Install it under Guest Agents.");
        var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
        if (!agents.IsOnline(agent.Id))
        {
            throw new InvalidOperationException($"The agent in {host} is offline (last contact {agent.LastContactUtc.ToLocalTime():g}).");
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlRestore))
        {
            throw new InvalidOperationException($"The agent in {host} (version {agent.AgentVersion}) cannot run SQL Server jobs yet. It updates itself within a few minutes after a server update.");
        }

        return agent;
    }

    /// <summary>A Windows credential (logged on in the guest) or a SQL Server login; null: no account (instances only).</summary>
    internal static async Task<SqlAccount?> AccountAsync(Guid? credentialId, IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, CancellationToken ct)
    {
        if (credentialId is null)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct)
            ?? throw new InvalidOperationException("The account no longer exists. Choose another one under Credentials.");
        return new SqlAccount
        {
            Kind = credential.Kind == CredentialKind.Windows ? SqlAccountKinds.Windows : SqlAccountKinds.Sql,
            UserName = credential.Username,
            Password = protector.Unprotect(credential.ProtectedSecret),
        };
    }

    private static SqlTargetDto ToDto(AgentEntity agent, SqlAccount? account, SqlInventory inventory) => new(
        agent.HostName,
        agent.IpAddresses ?? agent.RemoteAddress,
        account is null ? null : $"{account.UserName} ({(account.Kind == SqlAccountKinds.Windows ? "Windows" : "SQL login")})",
        [.. inventory.Instances.Select(i => new SqlTargetInstanceDto(
            i.Name, i.InstanceId, i.SetupVersion, SqlCatalog.Product(i.ProductVersion ?? i.SetupVersion), i.SetupEdition, i.ServiceName, i.ServiceState, i.ServiceAccount,
            i.DefaultDataFolder, i.DefaultLogFolder, i.BackupFolder, i.Connected, i.ConnectError, i.ProductVersion, i.Edition, i.EngineEdition, i.LoginName, i.IsSysadmin, i.IsDbCreator,
            [.. i.Databases.Select(d => new SqlTargetDatabaseDto(d.Name, d.State, d.RecoveryModel, d.ReadOnly,
                [.. d.Files.Select(f => new SqlTargetFileDto(f.LogicalName, f.PhysicalName, f.Type, f.SizeBytes))]))]))],
        [.. inventory.Volumes.Select(v => new SqlTargetVolumeDto(v.Root, v.Label, v.FreeBytes, v.TotalBytes))]);
}
