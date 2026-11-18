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
        {
            if (await CredentialUse.DescribeAsync(db, id, ct) is { } used)
            {
                return Results.Problem(used, statusCode: StatusCodes.Status409Conflict);
            }

            return await db.Credentials.Where(c => c.Id == id && c.Kind == CredentialKind.SqlServer).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/sql/publishes", StartPublishAsync);
        app.MapPost("/api/sql/restores", StartRestoreAsync);
        app.MapGet("/api/sql/databases", ListDatabasesAsync);
        app.MapGet("/api/sql/databases/{id:guid}", DatabaseAsync);
        app.MapGet("/api/sql/databases/{id:guid}/plan", PlanAsync);
        app.MapGet("/api/sql/backup-days/{sessionId:guid}", BackupDayAsync);
        app.MapGet("/api/sql/schedules", ListSchedulesAsync)
            .WithDescription("The SQL Server backups that jobs schedule, one row per job and VM: next log, native full and system backup, and the last run.");
        app.MapGet("/api/sql/backup-runs/{id:guid}/lines", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.SqlBackupRuns.AsNoTracking().Where(r => r.Id == id).Select(r => r.Lines).FirstOrDefaultAsync(ct) is { } lines
                ? Results.Ok(lines.Select(l => new SqlBackupRunLineDto(l.Severity.ToString(), l.Message)).ToList())
                : Results.NotFound());
        app.MapPost("/api/sql/exports", StartExportAsync);

        // The .bak of a native full or system database backup: a one-time link, then the file from the repository.
        app.MapPost("/api/sql/backup-sets/{id:guid}/link", async (Guid id, AppDbContext db, SqlBackupDownloads downloads, HttpContext http, CancellationToken ct) =>
            await DownloadableSets(db).FirstOrDefaultAsync(s => s.Id == id, ct) is { } set
                ? Results.Ok(new FileDownloadTicketDto(downloads.Issue(id, http.User.Identity?.Name ?? string.Empty), set.FileName ?? "backup.bak", (int)SqlBackupDownloads.LinkLifetime.TotalSeconds))
                : Results.Problem("This backup is not in the repository any more (removed by retention) or is not a full backup of the agent.", statusCode: StatusCodes.Status404NotFound));
        app.MapGet("/api/sql/backup-sets/downloads/{token}", DownloadBackupAsync);

        // The .bak of an export session on the server: its name and size, a one-time link, the download.
        app.MapGet("/api/sql/exports/{sessionId:guid}", (Guid sessionId, SqlExportFiles files) =>
            files.Find(sessionId) is { } file ? Results.Ok(file) : Results.NotFound());
        app.MapPost("/api/sql/exports/{sessionId:guid}/link", (Guid sessionId, SqlExportFiles files, HttpContext http) =>
            files.Find(sessionId) is { } file && files.IssueLink(sessionId, http.User.Identity?.Name ?? string.Empty) is { } url
                ? Results.Ok(new FileDownloadTicketDto(url, file.FileName, 60))
                : Results.Problem("The .bak of this export is not on the backup server (not downloaded, or older than 24 hours).", statusCode: StatusCodes.Status404NotFound));
        app.MapGet("/api/sql/exports/downloads/{token}", (string token, SqlExportFiles files, HttpContext http) =>
            files.Redeem(token, http.User.Identity?.Name ?? string.Empty) is { } file
                ? Results.File(file.Path, "application/octet-stream", file.Name, enableRangeProcessing: true)
                : Results.Problem("This download link has expired or was used already. Start the download again on the session page.", statusCode: StatusCodes.Status404NotFound));

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
        if (request.SqlDatabaseId is { } databaseId)
        {
            // At a point in time: the base must be a restore point (its files are mounted, nothing is copied).
            try
            {
                var chain = await SqlRestoreSessionRunner.ChooseChainAsync(db, databaseId, request.PointInTimeUtc, ct);
                if (chain.Base.RestorePointId is not { } basePoint)
                {
                    return Results.Problem(
                        $"The base of this point in time is a full backup, not a restore point with the SQL Server snapshot: it cannot be published without copying. Restore it as a new database instead.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                request = request with { RestorePointId = basePoint, Instance = chain.Database.Instance, Database = chain.Database.Name };
            }
            catch (Exception ex) when (ex is SqlChainException or KeyNotFoundException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }

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
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlPublish)
                || (request.SqlDatabaseId is not null && !registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlRestoreChain)))
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
    /// Starts a .bak export. Checked here: the restore point, the staging instance, the folder, the account and an agent
    /// that can publish and back up; the session checks the instance with the account.
    /// </summary>
    private static async Task<IResult> StartExportAsync(
        SqlExportRequestDto request, AppDbContext db, AgentService agents, AgentRegistry registry, SessionQueue queue, HttpContext http, CancellationToken ct)
    {
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, ct);
        if (point is null || point.State != RestorePointState.Available)
        {
            return Results.Problem("The restore point is not available (deleted, or still being written).", statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(request.TargetInstance))
        {
            return Results.Problem("Choose the instance that makes the backup.", statusCode: StatusCodes.Status400BadRequest);
        }

        var folder = request.Folder?.Trim();
        if (!string.IsNullOrEmpty(folder) && !FileRestore.AgentTargetPaths.IsLocalAbsolute(folder.Length == 2 ? folder + "\\" : folder))
        {
            return Results.Problem($"'{folder}' is not a local folder in the VM; enter it as D:\\Backup.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await db.Credentials.AnyAsync(c => c.Id == request.CredentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct))
        {
            return Results.Problem("The account no longer exists. Choose another one under Credentials.", statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var agent = await SqlAgentAsync(request.TargetVmNativeId, agents, registry, ct);
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlPublish) || !registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlBackup))
            {
                return Results.Problem(
                    $"The agent in {agent.HostName} (version {agent.AgentVersion}) cannot export SQL Server databases yet. It updates itself within a few minutes after a server update.",
                    statusCode: StatusCodes.Status409Conflict);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        var id = await queue.EnqueueSqlExportAsync(request with { Folder = string.IsNullOrEmpty(folder) ? null : folder }, point.RepositoryId, point.VmName, http.User.Identity?.Name ?? "user", ct);
        return Results.Ok(new StartedDto(id));
    }

    /// <summary>
    /// Starts a database restore. Checked here: the restore point, the name, the file location and its folders, the
    /// account and an agent that can restore databases; the session checks the instance with the account.
    /// </summary>
    private static async Task<IResult> StartRestoreAsync(
        SqlRestoreRequestDto request, AppDbContext db, AgentService agents, AgentRegistry registry, SessionQueue queue, HttpContext http, CancellationToken ct)
    {
        RestorePointEntity? point;
        if (request.SqlDatabaseId is { } databaseId)
        {
            // To a point in time: the source comes from the SQL catalog, and the chain must reach the time now already.
            Sql.SqlRestoreSessionRunner.ChainChoice chain;
            try
            {
                chain = await Sql.SqlRestoreSessionRunner.ChooseChainAsync(db, databaseId, request.PointInTimeUtc, ct);
            }
            catch (Exception ex) when (ex is Sql.SqlChainException or KeyNotFoundException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            request = request with { Instance = chain.Database.Instance, Database = chain.Database.Name, RestorePointId = chain.Base.RestorePointId ?? Guid.Empty };
            point = new RestorePointEntity
            {
                Id = request.RestorePointId,
                RepositoryId = chain.Base.RepositoryId,
                JobName = string.Empty,
                PlatformId = chain.Database.PlatformId,
                VmNativeId = chain.Database.VmNativeId,
                VmName = chain.Database.VmName,
            };
        }
        else
        {
            point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, ct);
            if (point is null || point.State != RestorePointState.Available)
            {
                return Results.Problem("The restore point is not available (deleted, or still being written).", statusCode: StatusCodes.Status404NotFound);
            }
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

        // Overwrite only the database the backup was taken of: SQL Server would take any database's files.
        if (request.Overwrite && (request.TargetVmNativeId != point.VmNativeId
                                  || !string.Equals(request.TargetInstance, request.Instance, StringComparison.OrdinalIgnoreCase)
                                  || !string.Equals(name, request.Database, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(
                $"Only the original database can be overwritten: {request.Instance}\\{request.Database} in {point.VmName}. Restore it as a new database to put it anywhere else.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var agent = await SqlAgentAsync(request.TargetVmNativeId, agents, registry, ct);
            var capability = request.SqlDatabaseId is not null ? AgentProtocol.CapabilitySqlRestoreChain
                : request.Overwrite ? AgentProtocol.CapabilitySqlReplaceFiles : AgentProtocol.CapabilitySqlAttachFiles;
            if (!registry.HasCapability(agent.Id, capability))
            {
                return Results.Problem(
                    $"The agent in {agent.HostName} (version {agent.AgentVersion}) cannot {(request.Overwrite ? "overwrite" : "restore")} SQL Server databases yet. It updates itself within a few minutes after a server update.",
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

    /// <summary>A "SQL Server backups" day session: counts and bytes of its backups, each VM's schedule, the VMs' databases.</summary>
    /// <summary>The SQL Server backups jobs schedule, per job and VM, soonest first.</summary>
    private static async Task<IResult> ListSchedulesAsync(AppDbContext db, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking().Include(j => j.Objects).Where(j => j.SqlBackup.Enabled).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();
        var schedules = await db.SqlBackupSchedules.AsNoTracking().Where(s => ids.Contains(s.JobId)).ToListAsync(ct);
        var days = (await db.Sessions.AsNoTracking().Where(s => s.Kind == SessionKind.SqlBackup && s.JobId != null && ids.Contains(s.JobId.Value))
                .GroupBy(s => s.JobId!.Value).Select(g => new { JobId = g.Key, Id = g.OrderByDescending(s => s.CreatedUtc).Select(s => s.Id).First() }).ToListAsync(ct))
            .ToDictionary(d => d.JobId, d => d.Id);
        var rows = schedules.Select(s =>
        {
            var job = jobs.First(j => j.Id == s.JobId);
            var name = job.Objects.FirstOrDefault(o => o.NativeId == s.VmNativeId)?.CachedName ?? s.VmNativeId;
            return new SqlScheduleDto(
                job.Id, job.Name, job.Enabled, s.VmNativeId, name, job.SqlBackup.LogIntervalMinutes, s.NextLogUtc, s.NextFullUtc, s.NextSystemUtc, s.LastRunUtc,
                s.LastResult?.ToString(), s.LastMessage, days.TryGetValue(job.Id, out var day) ? day : null);
        });
        return Results.Ok(rows
            .OrderBy(r => new[] { r.NextLogUtc, r.NextFullUtc, r.NextSystemUtc }.Where(t => t is not null).DefaultIfEmpty(DateTimeOffset.MaxValue).Min())
            .ThenBy(r => r.JobName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.VmName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<IResult> BackupDayAsync(Guid sessionId, AppDbContext db, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.Kind == SessionKind.SqlBackup, ct);
        if (session?.JobId is not { } jobId)
        {
            return Results.NotFound();
        }

        var current = !await db.Sessions.AnyAsync(s => s.JobId == jobId && s.Kind == SessionKind.SqlBackup && s.CreatedUtc > session.CreatedUtc, ct);
        var interval = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => (int?)j.SqlBackup.LogIntervalMinutes).FirstOrDefaultAsync(ct);
        var tasks = await db.SessionTasks.AsNoTracking().Where(t => t.SessionId == sessionId).Select(t => new { t.ObjectNativeId, t.ObjectName }).ToListAsync(ct);
        var schedules = await db.SqlBackupSchedules.AsNoTracking().Where(s => s.JobId == jobId).ToListAsync(ct);
        var sets = await db.SqlBackupSets.AsNoTracking().Where(s => s.SessionId == sessionId)
            .Join(db.SqlDatabases, s => s.SqlDatabaseId, d => d.Id, (s, d) => new { s.Kind, s.Size, d.VmNativeId })
            .ToListAsync(ct);
        var stored = await db.SqlBackups.AsNoTracking().Where(b => b.SessionId == sessionId).SumAsync(b => (long?)b.NewStoredBytes, ct) ?? 0;

        // The session's VMs: those with a task (a run that day), then those the job schedules without one yet.
        var vmIds = tasks.Select(t => t.ObjectNativeId).Concat(schedules.Select(s => s.VmNativeId)).Distinct().ToList();
        var databases = await db.SqlDatabases.AsNoTracking().Where(d => vmIds.Contains(d.VmNativeId))
            .OrderBy(d => d.VmName).ThenBy(d => d.Instance).ThenBy(d => d.Name).ToListAsync(ct);
        var usable = (await SqlRestoreSessionRunner.UsableSetsAsync(db, [.. databases.Select(d => d.Id)], ct)).ToLookup(s => s.SqlDatabaseId);
        var names = tasks.GroupBy(t => t.ObjectNativeId).ToDictionary(g => g.Key, g => g.First().ObjectName);
        foreach (var d in databases)
        {
            names.TryAdd(d.VmNativeId, d.VmName);
        }

        var vms = vmIds.Select(vm =>
        {
            var schedule = schedules.FirstOrDefault(s => s.VmNativeId == vm);
            var own = sets.Where(s => s.VmNativeId == vm).ToList();
            return new SqlBackupDayVmDto(
                vm, names.GetValueOrDefault(vm, vm), own.Count(s => s.Kind == SqlBackupSetKind.Log), own.Count(s => s.Kind != SqlBackupSetKind.Log), own.Sum(s => s.Size),
                schedule?.LastRunUtc, schedule?.LastResult?.ToString(), schedule?.LastMessage, schedule?.NextLogUtc, schedule?.NextFullUtc, schedule?.NextSystemUtc);
        }).OrderBy(v => v.VmName, StringComparer.OrdinalIgnoreCase).ToList();

        // The runs without their lines (those come per VM run when opened): VMs started in the same minute form one run.
        var runs = await db.SqlBackupRuns.AsNoTracking().Where(r => r.SessionId == sessionId).OrderBy(r => r.StartedUtc)
            .Select(r => new { r.Id, r.VmName, r.StartedUtc, r.EndedUtc, r.State, r.Logs, r.Fulls, r.Bytes, r.NewStoredBytes, r.Message })
            .ToListAsync(ct);
        var groups = runs
            .GroupBy(r => new DateTimeOffset(r.StartedUtc.UtcTicks - r.StartedUtc.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero))
            .OrderByDescending(g => g.Key)
            .Select(g => new SqlBackupRunDto(
                g.Min(r => r.StartedUtc), g.Max(r => r.EndedUtc), SqlBackupService.Worst(g.Select(r => r.State)).ToString(),
                g.Sum(r => r.Logs), g.Sum(r => r.Fulls), g.Sum(r => r.Bytes), g.Sum(r => r.NewStoredBytes),
                [.. g.OrderBy(r => r.VmName, StringComparer.OrdinalIgnoreCase).Select(r => new SqlBackupRunVmDto(r.Id, r.VmName, r.State.ToString(), r.Logs, r.Fulls, r.Bytes, r.NewStoredBytes, r.Message))]))
            .ToList();

        return Results.Ok(new SqlBackupDayDto(
            sets.Count(s => s.Kind == SqlBackupSetKind.Log), sets.Count(s => s.Kind == SqlBackupSetKind.NativeFull), sets.Count(s => s.Kind == SqlBackupSetKind.SystemFull),
            sets.Sum(s => s.Size), stored, interval ?? 60, current, vms,
            [.. databases.Select(d => ToDto(d, new SqlChain(usable[d.Id].Select(SqlChainSet.From)).Coverage().Spans))])
        {
            Runs = groups,
        });
    }

    /// <summary>The databases of the SQL catalog with their chain state and the span of time they can be restored to.</summary>
    private static async Task<IResult> ListDatabasesAsync(AppDbContext db, CancellationToken ct)
    {
        var databases = await db.SqlDatabases.AsNoTracking().OrderBy(d => d.VmName).ThenBy(d => d.Instance).ThenBy(d => d.Name).ToListAsync(ct);
        var sets = (await SqlRestoreSessionRunner.UsableSetsAsync(db, [.. databases.Select(d => d.Id)], ct)).ToLookup(s => s.SqlDatabaseId);
        return Results.Ok(databases.Select(d => ToDto(d, new SqlChain(sets[d.Id].Select(SqlChainSet.From)).Coverage().Spans)).ToList());
    }

    private static async Task<IResult> DatabaseAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var database = await db.SqlDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (database is null)
        {
            return Results.NotFound();
        }

        var sets = await SqlRestoreSessionRunner.UsableSetsAsync(db, [id], ct);
        var (spans, gaps) = new SqlChain(sets.Select(SqlChainSet.From)).Coverage();
        var points = sets
            .Where(s => spans.Any(span => span.FromUtc <= s.FinishUtc && s.FinishUtc <= span.ToUtc))
            .OrderBy(s => s.FinishUtc)
            .Select(s => new SqlBackupPointDto(s.FinishUtc, s.Kind.ToString(), s.Size))
            .ToList();
        var fulls = await DownloadableSets(db).Where(s => s.SqlDatabaseId == id).OrderByDescending(s => s.FinishUtc).Take(100).ToListAsync(ct);
        return Results.Ok(new SqlCatalogDatabaseDetailDto(
            ToDto(database, spans),
            [.. spans.Select(s => new SqlCoverageSpanDto(s.FromUtc, s.ToUtc, s.BaseKind.ToString(), s.Logs))],
            [.. gaps.Select(g => new SqlCoverageGapDto(g.FromUtc, g.ToUtc, g.Reason))],
            points)
        {
            Fulls = [.. fulls.Select(s => new SqlBackupFileDto(s.Id, s.Kind.ToString(), s.FinishUtc, DownloadName(database, s), s.Size))],
        });
    }

    /// <summary>The native full and system database backups whose SQL backup object is available.</summary>
    private static IQueryable<SqlBackupSetEntity> DownloadableSets(AppDbContext db) =>
        db.SqlBackupSets.AsNoTracking().Where(s =>
            (s.Kind == SqlBackupSetKind.NativeFull || s.Kind == SqlBackupSetKind.SystemFull) && s.SqlBackupId != null && s.FileKey != null
            && db.SqlBackups.Any(b => b.Id == s.SqlBackupId && b.State == SqlBackupState.Available));

    private static string DownloadName(SqlDatabaseEntity database, SqlBackupSetEntity set) =>
        set.FileName is { Length: > 0 } name ? name : $"{database.Name}_{set.FinishUtc.UtcDateTime:yyyyMMddTHHmmssZ}.bak";

    /// <summary>
    /// Streams the .bak of a native full or system database backup from the repository (pinned meanwhile). An error after
    /// the first byte aborts the connection, so the browser reports a failed download instead of saving a short file.
    /// </summary>
    private static async Task<IResult> DownloadBackupAsync(
        string token, AppDbContext db, SqlBackupDownloads downloads, RestorePointAccess access, ILoggerFactory loggers, HttpContext http, CancellationToken ct)
    {
        var user = http.User.Identity?.Name ?? string.Empty;
        var set = downloads.Redeem(token, user) is { } id ? await DownloadableSets(db).FirstOrDefaultAsync(s => s.Id == id, ct) : null;
        if (set is null)
        {
            return Results.Problem("This download link has expired or was used already, or the backup was removed since. Start the download again on the database's page.", statusCode: StatusCodes.Status404NotFound);
        }

        var database = await db.SqlDatabases.AsNoTracking().FirstAsync(d => d.Id == set.SqlDatabaseId, ct);
        var logger = loggers.CreateLogger("Backdafuckup.Server.Sql.SqlBackupDownloads");
        using var pinned = await access.PinSqlBackupAsync(set.SqlBackupId!.Value, set.RepositoryId, ct);
        await using var reader = await pinned.Repository.OpenSqlBackupAsync(set.SqlBackupId.Value, ct);
        await using var stream = await reader.OpenFileAsync(set.FileKey!, ct);
        var name = DownloadName(database, set);
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = set.Size;
        http.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
        logger.LogInformation("{User} downloads {File} ({Bytes} bytes) of {Instance}\\{Database} on {Vm}.", user, name, set.Size, database.Instance, database.Name, database.VmName);
        try
        {
            await stream.CopyToAsync(http.Response.Body, 1 << 20, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The download of {File} of {Instance}\\{Database} on {Vm} by {User} failed: {Message}", name, database.Instance, database.Name, database.VmName, user, ex.Message);
            http.Abort();
        }

        return Results.Empty;
    }

    /// <summary>What a restore to <paramref name="at"/> (null: the latest point) applies; 400 with the reason if nothing reaches it.</summary>
    private static async Task<IResult> PlanAsync(Guid id, DateTimeOffset? at, AppDbContext db, RestorePointAccess access, CancellationToken ct)
    {
        SqlRestoreSessionRunner.ChainChoice chain;
        try
        {
            chain = await SqlRestoreSessionRunner.ChooseChainAsync(db, id, at, ct);
        }
        catch (Exception ex) when (ex is SqlChainException or KeyNotFoundException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        List<SqlPlanFileDto> files;
        if (chain.Base.Files is { } baseFiles)
        {
            files = [.. baseFiles.OrderBy(f => f.Type == "LOG" ? 1 : 0).ThenBy(f => f.FileId).Select(f => new SqlPlanFileDto(f.PhysicalName, f.Type == "LOG" ? "log" : "data", f.SizeBytes))];
        }
        else
        {
            // A snapshot base: the files as the restore point's guest metadata lists them.
            using var pinned = await access.PinRestorePointAsync(chain.Base.RestorePointId!.Value, chain.Base.RepositoryId, ct);
            await using var reader = await pinned.Repository.OpenRestorePointAsync(chain.Base.RestorePointId!.Value, ct);
            var stored = await StoredGuestMetadata.ReadAsync(reader, ct);
            var guest = stored?.SqlDatabases.FirstOrDefault(d =>
                string.Equals(d.Instance, chain.Database.Instance, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Database, chain.Database.Name, StringComparison.OrdinalIgnoreCase));
            // The writer may list the files without kind (the restore classifies them by their first page): the extension tells for the preview.
            static string FullPath(GuestSqlFile f) => f.Path.TrimEnd('\\') + "\\" + f.Spec;
            files = guest is null ? [] :
            [
                .. guest.DataFiles.Select(f => new SqlPlanFileDto(FullPath(f), "data", null)),
                .. guest.OtherFiles.Where(f => !f.Spec.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase)).Select(f => new SqlPlanFileDto(FullPath(f), "data", null)),
                .. guest.LogFiles.Select(f => new SqlPlanFileDto(FullPath(f), "log", null)),
                .. guest.OtherFiles.Where(f => f.Spec.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase)).Select(f => new SqlPlanFileDto(FullPath(f), "log", null)),
            ];
        }

        return Results.Ok(new SqlRestorePlanDto(
            chain.Base.Kind.ToString(), chain.Base.FinishUtc, chain.Base.RestorePointId, chain.Base.Size, chain.Logs.Count, chain.Logs.Sum(l => l.Size),
            chain.Plan.StopAtLocal, chain.Plan.ReachedUtc, files));
    }

    private static SqlCatalogDatabaseDto ToDto(SqlDatabaseEntity d, IReadOnlyList<SqlCoverage> spans) => new(
        d.Id, d.VmNativeId, d.VmName, d.Instance, d.Name, d.RecoveryModel, d.ProductVersion, d.ChainState.ToString(), d.ChainReason, d.LastLogUtc, d.LastFullUtc,
        spans.Count == 0 ? null : spans[0].FromUtc, spans.Count == 0 ? null : spans[^1].ToUtc, d.JobId);

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
