using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Xen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Engine;

/// <summary>Picks queued sessions from the database and runs them within the configured concurrency limits.</summary>
public sealed class SessionRunnerService(
    IDbContextFactory<AppDbContext> dbFactory,
    SessionQueue queue,
    RunningSessions running,
    SessionTracker tracker,
    SessionEventWriter events,
    BackupSessionRunner backups,
    RestoreSessionRunner restores,
    MaintenanceSessionRunner maintenance,
    Agents.AgentDeploymentRunner deployments,
    FileRestore.FileRestoreSessionRunner fileRestores,
    Sql.SqlPublishSessionRunner sqlPublishes,
    Sql.SqlRestoreSessionRunner sqlRestores,
    Tape.TapeSessionRunner tapes,
    Tape.TapeBackupRunner tapeBackups,
    Tape.TapeRestoreRunner tapeRestores,
    Tape.TapeCatalogRebuild tapeRebuild,
    ScaleOut.RepositoryMoveRunner moves,
    StartupRecovery recovery,
    Telemetry.BackupMetrics metrics,
    IOptions<EngineOptions> options,
    IOptions<Sql.SqlRestoreOptions> sqlOptions,
    TimeProvider time,
    ILogger<SessionRunnerService> logger) : BackgroundService
{
    private readonly List<Task> _active = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await recovery.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Startup recovery failed; continuing.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Dispatching sessions failed.");
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await queue.Signal.ReadAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Periodic poll.
            }
        }

        await Task.WhenAll(_active.ToArray()).ConfigureAwait(false);
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        _active.RemoveAll(t => t.IsCompleted);
        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
        var queued = await db.Sessions.Where(s => s.State == SessionState.Queued).OrderBy(s => s.CreatedUtc)
            .ToListAsync(stoppingToken).ConfigureAwait(false);
        foreach (var session in queued)
        {
            if (running.IsRunning(session.Id) || !HasCapacity(session))
            {
                continue;
            }

            session.State = SessionState.Running;
            session.StartedUtc = time.GetUtcNow();
            session.ServerInstanceId = options.Value.ServerInstanceId;
            await db.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
            var cts = running.Register(session.Id, session.Kind, stoppingToken);
            _active.Add(Task.Run(() => RunAsync(session, cts.Token), CancellationToken.None));
        }
    }

    private bool HasCapacity(SessionEntity session) => session.Kind switch
    {
        SessionKind.Backup => running.Count(SessionKind.Backup) < options.Value.MaxConcurrentBackupSessions,
        SessionKind.Restore => running.Count(SessionKind.Restore) < options.Value.MaxConcurrentRestoreSessions,
        SessionKind.RepositoryMaintenance => running.Count(SessionKind.RepositoryMaintenance) == 0 && running.Count(SessionKind.Backup) == 0,
        SessionKind.AgentDeployment => running.Count(SessionKind.AgentDeployment) < 2,
        SessionKind.FileRestore => running.Count(SessionKind.FileRestore) < options.Value.MaxConcurrentFileRestoreSessions,
        SessionKind.SqlPublish or SessionKind.SqlExport => running.Count(SessionKind.SqlPublish) + running.Count(SessionKind.SqlExport) < sqlOptions.Value.MaxConcurrentPublishes,
        SessionKind.SqlRestore => running.Count(SessionKind.SqlRestore) < sqlOptions.Value.MaxConcurrentRestores,

        // Tape sessions wait for their drive and the robot themselves (FIFO, the holder named in the log).
        SessionKind.TapeMaintenance or SessionKind.TapeBackup or SessionKind.TapeRestore => true,

        // Moves between extents run next to backups (each chain's switch waits for its backup), one at a time.
        SessionKind.RepositoryMove => running.Count(SessionKind.RepositoryMove) == 0,
        _ => false,
    };

    private async Task RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = events.CreateLog(session.Id, null);
        SessionState state;
        string summary;
        try
        {
            await tracker.UpdateSessionAsync(session.Id, _ => { }, CancellationToken.None).ConfigureAwait(false);
            (state, summary) = session.Kind switch
            {
                SessionKind.Backup => await backups.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.Restore => await restores.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.RepositoryMaintenance => await maintenance.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.AgentDeployment => await deployments.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.FileRestore => await fileRestores.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.SqlPublish => await sqlPublishes.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.SqlRestore => await sqlRestores.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.SqlExport => await sqlPublishes.RunExportAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.TapeMaintenance => Tape.TapeCatalogRebuild.IsRebuild(session)
                    ? await tapeRebuild.RunAsync(session, cancellationToken).ConfigureAwait(false)
                    : await tapes.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.TapeBackup => await tapeBackups.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.TapeRestore => await tapeRestores.RunAsync(session, cancellationToken).ConfigureAwait(false),
                SessionKind.RepositoryMove => await moves.RunAsync(session, cancellationToken).ConfigureAwait(false),
                _ => (SessionState.Failed, "Unknown session kind."),
            };

            // The SQL runners say themselves how a stop ended: stopping a publish (Unpublish) is its normal end, and an
            // attach, overwrite or .bak backup that was running when the stop came is finished, so it did not stop.
            if (cancellationToken.IsCancellationRequested && state != SessionState.Failed)
            {
                if (session.Kind is not (SessionKind.SqlPublish or SessionKind.SqlRestore or SessionKind.SqlExport))
                {
                    state = SessionState.Stopped;
                }
                else if (session.Kind != SessionKind.SqlPublish && state is SessionState.Succeeded or SessionState.Warning)
                {
                    log.Info("The stop came during a step that cannot be interrupted; that step was finished, so the session did not stop.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state = SessionState.Stopped;
            summary = "Stopped.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session {Session} failed", session.Id);
            log.Error(ex.Message);
            state = SessionState.Failed;
            summary = ex.Message;
        }
        finally
        {
            running.Unregister(session.Id);
        }

        log.Info($"Session finished: {state}. {summary}");
        metrics.RecordSession(session.Kind, state, time.GetUtcNow() - (session.StartedUtc ?? session.CreatedUtc));
        await events.FlushAsync().ConfigureAwait(false);
        await tracker.RollUpAsync(session.Id, CancellationToken.None).ConfigureAwait(false);
        await tracker.UpdateSessionAsync(session.Id, s =>
        {
            s.State = state;
            s.Summary = summary;
            s.EndedUtc = time.GetUtcNow();
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                s.Progress = 100;
            }
        }, CancellationToken.None).ConfigureAwait(false);

        if (session.Kind == SessionKind.Backup && session.JobId is { } jobId)
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LastRunUtc, session.StartedUtc).SetProperty(j => j.LastResult, ToResult(state)),
                CancellationToken.None).ConfigureAwait(false);

            // Tape jobs that run after this backup job.
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                foreach (var tapeJob in await db.TapeJobs.Where(j => j.AfterJobId == jobId && j.Enabled).Select(j => new { j.Id, j.Name }).ToListAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    try
                    {
                        await queue.EnqueueTapeBackupAsync(tapeJob.Id, $"after {session.Name}", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        logger.LogInformation("Tape job {TapeJob} not queued after {Job}: {Reason}", tapeJob.Name, session.Name, ex.Message);
                    }
                }
            }
        }

        if (session.Kind == SessionKind.TapeBackup && session.Parameters is { } parameters
            && System.Text.Json.JsonSerializer.Deserialize<Tape.TapeBackupRequest>(parameters) is { } tapeRun)
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await db.TapeJobs.Where(j => j.Id == tapeRun.JobId).ExecuteUpdateAsync(
                s => s.SetProperty(j => j.LastRunUtc, session.StartedUtc).SetProperty(j => j.LastResult, ToResult(state)),
                CancellationToken.None).ConfigureAwait(false);
        }

        queue.Wake();
    }

    private static SessionResult ToResult(SessionState state) => state switch
    {
        SessionState.Succeeded => SessionResult.Success,
        SessionState.Warning => SessionResult.Warning,
        SessionState.Stopped => SessionResult.Stopped,
        _ => SessionResult.Failed,
    };
}

/// <summary>Queues backup sessions for jobs whose schedule is due.</summary>
public sealed class JobScheduler(
    IDbContextFactory<AppDbContext> dbFactory,
    SessionQueue queue,
    IOptions<EngineOptions> options,
    TimeProvider time,
    ILogger<JobScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SchedulerInterval, time);
        do
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler tick failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await db.Jobs.Where(j => j.Enabled && j.Schedule.Kind != ScheduleKind.None).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            if (job.NextRunUtc is null)
            {
                job.NextRunUtc = ScheduleCalculator.Next(job.Schedule, now);
                continue;
            }

            if (job.NextRunUtc > now)
            {
                continue;
            }

            var due = job.NextRunUtc.Value;
            job.NextRunUtc = ScheduleCalculator.Next(job.Schedule, now);
            if (now - due > options.Value.MisfireGrace)
            {
                logger.LogWarning("Job {Job}: missed run at {Due} was skipped (server was not running).", job.Name, due);
                continue;
            }

            try
            {
                await queue.EnqueueBackupAsync(job.Id, SessionRunMode.Incremental, "Schedule", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Job {Job}: scheduled run skipped: {Reason}", job.Name, ex.Message);
            }
        }

        // Tape jobs with a schedule, the same way.
        var tapeJobs = await db.TapeJobs.Where(j => j.Enabled && j.Schedule.Kind != ScheduleKind.None).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in tapeJobs)
        {
            if (job.NextRunUtc is null || job.NextRunUtc > now)
            {
                job.NextRunUtc ??= ScheduleCalculator.Next(job.Schedule, now);
                continue;
            }

            var due = job.NextRunUtc.Value;
            job.NextRunUtc = ScheduleCalculator.Next(job.Schedule, now);
            if (now - due > options.Value.MisfireGrace)
            {
                logger.LogWarning("Tape job {Job}: missed run at {Due} was skipped (server was not running).", job.Name, due);
                continue;
            }

            try
            {
                await queue.EnqueueTapeBackupAsync(job.Id, "Schedule", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Tape job {Job}: scheduled run skipped: {Reason}", job.Name, ex.Message);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // History cleanup.
        var cutoff = now.AddDays(-options.Value.SessionHistoryDays);
        await db.Sessions.Where(s => s.EndedUtc != null && s.EndedUtc < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Repairs state after a crash or restart: stale sessions, half-written restore points, interrupted deletes and
/// leftover platform objects recorded in the artifact ledger.
/// </summary>
public sealed class StartupRecovery(
    IDbContextFactory<AppDbContext> dbFactory,
    ConnectionManager connections,
    RepositoryManager repositories,
    IOptions<EngineOptions> options,
    TimeProvider time,
    ILogger<StartupRecovery> logger,
    Tape.TapeRestoreRunner tapeRestores)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instance = options.Value.ServerInstanceId;

        // Tape: nothing waits for an operator after a restart.
        await db.TapeOperatorRequests.Where(r => r.ResolvedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedUtc, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        tapeRestores.DeleteLeftoverCaches(message => logger.LogWarning("{Message}", message));

        var stale = await db.Sessions.Include(s => s.Tasks)
            .Where(s => (s.State == SessionState.Running || s.State == SessionState.Stopping) && s.ServerInstanceId != instance)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in stale)
        {
            if (session.Kind is SessionKind.SqlPublish or SessionKind.SqlExport)
            {
                // The agent gets 404 for the publish now and unpublishes on its own (database first, then the volumes).
                session.State = SessionState.Stopped;
                session.EndedUtc = time.GetUtcNow();
                session.Summary = session.Kind == SessionKind.SqlExport
                    ? "Stopped: the backup service was restarted during the export. The agent in the VM removes the published database and the volumes when it notices (within a minute); a .bak it wrote stays in its folder. A database it cannot remove is removed with this session's account when the agent reports it (noted here)."
                    : "Unpublished: the backup service was restarted. The agent in the VM removes the database and the volumes when it notices (within a minute). A database it cannot remove is removed with this session's account when the agent reports it (noted here).";
                foreach (var task in session.Tasks.Where(t => t.State is SessionState.Running or SessionState.Queued))
                {
                    task.State = SessionState.Stopped;
                    task.Error = "Service restarted";
                    task.EndedUtc = session.EndedUtc;
                }

                db.SessionLog.Add(new SessionLogEntry { SessionId = session.Id, TimestampUtc = time.GetUtcNow(), Severity = LogSeverity.Warning, Message = session.Summary });
                continue;
            }

            if (session.Kind == SessionKind.SqlBackup)
            {
                // A day session of SQL Server backups: the interrupted run is a warning; the next run of the day reopens it.
                // Backup files the agent wrote stay queued in the VM and are sent with that run.
                foreach (var task in session.Tasks.Where(t => t.State is SessionState.Running or SessionState.Queued))
                {
                    task.State = SessionState.Warning;
                    task.Error = "Service restarted during a run";
                    task.EndedUtc = time.GetUtcNow();
                }

                session.State = Sql.SqlBackupService.Worst(session.Tasks.Select(t => t.State));
                session.EndedUtc = time.GetUtcNow();
                session.Summary = "The backup service was restarted during a run; the agents keep their backup files queued and send them with the next run.";
                db.SessionLog.Add(new SessionLogEntry { SessionId = session.Id, TimestampUtc = time.GetUtcNow(), Severity = LogSeverity.Warning, Message = session.Summary });
                continue;
            }

            if (session.Kind == SessionKind.AdExplore)
            {
                // The DC's agent deletes the copy of the directory: the sweeper asks it once it is online, and the agent
                // itself after its idle time.
                session.State = SessionState.Stopped;
                session.EndedUtc = time.GetUtcNow();
                session.Summary = "Closed: the backup service was restarted. The domain controller removes the backup's directory when its agent is reachable.";
                db.SessionLog.Add(new SessionLogEntry { SessionId = session.Id, TimestampUtc = time.GetUtcNow(), Severity = LogSeverity.Info, Message = session.Summary });
                continue;
            }

            if (session.Kind == SessionKind.FileBrowse)
            {
                // A file browser only reads; it simply ends with the service.
                session.State = SessionState.Stopped;
                session.EndedUtc = time.GetUtcNow();
                session.Summary = "Closed: the backup service was restarted.";
                db.SessionLog.Add(new SessionLogEntry { SessionId = session.Id, TimestampUtc = time.GetUtcNow(), Severity = LogSeverity.Info, Message = session.Summary });
                continue;
            }

            if (session.Kind == SessionKind.RepositoryMove)
            {
                // The journal says what happened: switched chains stay switched (the cleanup deletes their source
                // copies), copies of the others are reused by their next move or removed after a day.
                await db.ChainPlacements.Where(p => p.MoveSessionId == session.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.MoveSessionId, (Guid?)null), cancellationToken).ConfigureAwait(false);
            }

            session.State = SessionState.Failed;
            session.EndedUtc = time.GetUtcNow();
            session.Summary = session.Kind switch
            {
                SessionKind.SqlRestore => Sql.SqlRestoreRecovery.Summary(session.Parameters),
                SessionKind.RepositoryMove => "The backup service was restarted during the move. Chains switched before stay on their new extent and their old copies are deleted by the cleanup; "
                    + "the copies of the others are reused when those chains are moved again, or removed after a day. An evacuation continues by itself.",
                _ => "The backup service was restarted while this session was running.",
            };
            if (session.Kind == SessionKind.SqlRestore && Sql.SqlRestoreRecovery.NeedsCleanup(session.Parameters))
            {
                // SqlRestoreCleanup checks the target once its agent is back and removes what no database uses.
                session.Parameters = Sql.SqlRestoreRecovery.WithCleanupPending(session.Parameters, pending: true);
            }
            foreach (var task in session.Tasks.Where(t => t.State is SessionState.Running or SessionState.Queued))
            {
                task.State = SessionState.Failed;
                task.Error = "Service restarted";
                task.EndedUtc = session.EndedUtc;
            }

            db.SessionLog.Add(new SessionLogEntry { SessionId = session.Id, TimestampUtc = time.GetUtcNow(), Severity = LogSeverity.Error, Message = session.Summary });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var point in await db.RestorePoints.Where(r => r.State == RestorePointState.Creating || r.State == RestorePointState.Deleting)
                     .ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
                var manifest = await FindManifestAsync(repository, point.Id, cancellationToken).ConfigureAwait(false);
                if (point.State == RestorePointState.Creating && manifest is not null)
                {
                    VmBackupTask.ApplyManifest(point, manifest.Manifest, manifest.Path);
                    logger.LogInformation("Recovered committed restore point {RestorePoint} of {Vm}.", point.Id, point.VmName);
                }
                else
                {
                    if (manifest is not null)
                    {
                        await repository.DeleteRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false);
                    }

                    db.RestorePoints.Remove(point);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or KeyNotFoundException)
            {
                logger.LogWarning(ex, "Could not recover restore point {RestorePoint}.", point.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await CleanupArtifactsAsync(db, _ => true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes leftover platform objects (e.g. snapshots whose disk XAPI held too long) of sessions that are no
    /// longer running. Called periodically while the service runs.
    /// </summary>
    public async Task RetryPendingArtifactsAsync(RunningSessions running, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settled = time.GetUtcNow() - TimeSpan.FromMinutes(5);
        await CleanupArtifactsAsync(db, a => a.UpdatedUtc < settled && (a.SessionId is not { } session || !running.IsRunning(session)), cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupArtifactsAsync(AppDbContext db, Func<ProviderArtifactEntity, bool> filter, CancellationToken cancellationToken)
    {
        var open = (await db.Artifacts.Where(a => a.State != ArtifactState.Done).ToListAsync(cancellationToken).ConfigureAwait(false)).Where(filter).ToList();
        if (open.Count == 0)
        {
            return;
        }

        foreach (var group in open.GroupBy(a => a.ConnectionId))
        {
            IHypervisorSession session;
            try
            {
                session = await connections.OpenAsync(group.Key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Cleanup of {Count} leftover object(s) postponed: connection unavailable ({Error}).", group.Count(), ex.Message);
                continue;
            }

            await using (session.ConfigureAwait(false))
            {
                var log = new LoggerOperationLog(logger);
                foreach (var artifact in group.OrderByDescending(a => a.CreatedUtc))
                {
                    var record = new ProviderArtifact(artifact.Id, artifact.Kind, artifact.NativeId, artifact.NativeRef, artifact.SessionId, artifact.RestorePointId, artifact.State, artifact.Data);
                    try
                    {
                        if (artifact.State == ArtifactState.CommitPending)
                        {
                            await session.CommitArtifactAsync(record, log, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await session.CleanupArtifactAsync(record, log, cancellationToken).ConfigureAwait(false);
                        }

                        artifact.State = ArtifactState.Done;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        artifact.Attempts++;
                        artifact.LastError = ex.Message;
                    }

                    artifact.UpdatedUtc = time.GetUtcNow();
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Core.Repository.ManifestInfo?> FindManifestAsync(Core.Repository.IBackupRepository repository, Guid id, CancellationToken cancellationToken)
    {
        await foreach (var info in repository.ListRestorePointsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (info.RestorePointId == id)
            {
                return info;
            }
        }

        return null;
    }

    private sealed class LoggerOperationLog(ILogger logger) : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null) =>
            logger.Log(severity == LogSeverity.Error ? LogLevel.Error : severity == LogSeverity.Warning ? LogLevel.Warning : LogLevel.Information, "Recovery: {Message}", message);
    }
}

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddBackupEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EngineOptions>(configuration.GetSection("Engine"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<Telemetry.BackupMetrics>();
        services.Configure<Telemetry.MetricsOptions>(configuration.GetSection("Metrics"));
        services.AddSingleton<Telemetry.StateMetrics>();
        services.AddHostedService<Telemetry.MetricsStateService>();
        // Tests register their own providers before this; each kind is added only when none of it is there.
        if (services.All(d => d.ServiceType != typeof(IHypervisorProvider)))
        {
            var xen = configuration.GetSection("Xen").Get<XenOptions>() ?? new XenOptions();
            services.AddSingleton<IHypervisorProvider>(new XenProvider(xen));
        }

        if (!services.Any(d => d.ServiceType == typeof(IHypervisorProvider) && d.ImplementationInstance is IHypervisorProvider { Kind: HypervisorKind.Kubernetes }))
        {
            services.AddSingleton<IHypervisorProvider>(sp => new Backdafuckup.Kubernetes.KubernetesProvider(
                channels: sp.GetRequiredService<Backdafuckup.Core.DataMover.IMoverChannels>(), images: sp.GetRequiredService<Movers.MoverRegistry>(),
                usage: sp.GetRequiredService<Clusters.KubernetesUsageSources>()));
        }

        services.Configure<Agents.AgentOptions>(configuration.GetSection("Agent"));
        services.AddSingleton<Agents.AgentRegistry>();
        services.AddSingleton<Agents.AgentSettingsStore>();
        services.AddSingleton<Movers.MoverChannels>();
        services.AddSingleton<Backdafuckup.Core.DataMover.IMoverChannels>(sp => sp.GetRequiredService<Movers.MoverChannels>());
        services.AddSingleton<Movers.MoverImage>();
        services.TryAddSingleton(new Movers.RegistryTransport());
        services.TryAddSingleton(new Clusters.KubernetesUsageTransport());
        services.AddSingleton<Clusters.KubernetesUsageSources>();
        services.AddSingleton<Movers.MoverRegistry>();
        services.AddSingleton<Agents.AgentService>();
        services.AddSingleton<Agents.AgentPackages>();
        services.AddSingleton<Agents.GuestProcessingService>();
        services.TryAddSingleton<Backdafuckup.Smb.IRemoteWindowsConnector, Backdafuckup.Smb.SmbRemoteWindowsConnector>();
        services.AddSingleton<Agents.AgentDeployer>();
        services.AddSingleton<Agents.IAgentInstaller>(sp => sp.GetRequiredService<Agents.AgentDeployer>());
        services.AddSingleton<Agents.AgentDeploymentRunner>();
        services.AddSingleton<Clusters.ClusterAgents>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<RepositoryManager>();
        services.AddSingleton<RestorePointAccess>();
        services.TryAddSingleton<ScaleOut.IVolumeSpace, ScaleOut.DriveVolumeSpace>();
        services.AddSingleton<ScaleOut.RepositoryPlacement>();
        services.AddSingleton<ScaleOut.ChainLocks>();
        services.AddSingleton<ScaleOut.ScaleOutAdmin>();
        services.AddSingleton<ScaleOut.ChainMover>();
        services.AddSingleton<ScaleOut.RepositoryMoveRunner>();
        services.AddSingleton<ScaleOut.ScaleOutBalancer>();
        services.AddHostedService(sp => sp.GetRequiredService<ScaleOut.ScaleOutBalancer>());
        services.AddSingleton<RunningSessions>();
        services.AddSingleton<SessionQueue>();
        services.AddSingleton<SessionTracker>();
        services.AddSingleton<SessionEventWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionEventWriter>());
        services.TryAddSingleton<ILiveNotifier, NullLiveNotifier>();
        services.AddSingleton<BackupSessionRunner>();
        services.AddSingleton<RestoreSessionRunner>();
        services.AddSingleton<MaintenanceSessionRunner>();
        services.Configure<Tape.TapeOptions>(configuration.GetSection("Tape"));
        services.Configure<ScaleOut.ScaleOutOptions>(configuration.GetSection("ScaleOut"));
        services.TryAddSingleton<Backdafuckup.Tape.Devices.ITapeHost, Backdafuckup.Tape.Devices.LocalTapeHost>();
        services.AddSingleton<Tape.TapeAgents>();
        services.AddSingleton<Tape.TapeHosts>();
        services.AddSingleton<Tape.TapeLocks>();
        services.AddSingleton<Tape.TapeLibraries>();
        services.AddSingleton<Tape.TapeSessionRunner>();
        services.AddSingleton<Tape.TapeBackupRunner>();
        services.AddSingleton<Tape.TapeRestoreRunner>();
        services.AddSingleton<Tape.TapeCatalogService>();
        services.AddSingleton<Tape.TapeOperatorRequests>();
        services.AddSingleton<Tape.TapeCatalogRebuild>();
        services.AddSingleton<Tape.TapeMailSlotWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<Tape.TapeMailSlotWatcher>());
        services.AddSingleton<StartupRecovery>();
        services.AddHostedService<PendingArtifactRetryService>();
        services.AddSingleton<JobScheduler>();
        services.AddHostedService<SessionRunnerService>();
        services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());

        services.Configure<FileRestore.FileRestoreOptions>(configuration.GetSection("FileRestore"));
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<FileRestore.FileRestoreOptions>>().Value;
            return new FileLevel.ChunkCache(o.CacheBytes, Math.Max(2, Environment.ProcessorCount / 2));
        });
        services.AddSingleton(sp => new FileLevel.FileLevelWorkers(Math.Max(1, sp.GetRequiredService<IOptions<FileRestore.FileRestoreOptions>>().Value.WorkerThreads)));
        services.AddSingleton<FileRestore.FileRestoreMounts>();
        services.AddSingleton<FileRestore.FileRestoreDownloads>();
        services.AddSingleton<FileRestore.FileRestoreSessionRunner>();
        services.AddSingleton<FileRestore.AgentTransfers>();
        services.AddSingleton<Sql.SqlJobs>();
        services.AddSingleton<Ad.AdJobs>();
        services.Configure<Ad.AdExploreOptions>(configuration.GetSection("AdExplore"));
        services.AddSingleton<Ad.AdExplores>();
        services.AddHostedService<Ad.AdExploreSweeper>();
        services.Configure<Sql.SqlBackupOptions>(configuration.GetSection("SqlBackup"));
        services.AddSingleton<Sql.SqlBackupService>();
        services.AddHostedService(sp => sp.GetRequiredService<Sql.SqlBackupService>());
        services.Configure<Sql.SqlRestoreOptions>(configuration.GetSection("SqlRestore"));
        services.AddSingleton<Sql.SqlPublishSessionRunner>();
        services.AddSingleton<Sql.SqlRestoreSessionRunner>();
        services.AddSingleton<Publishing.DiskPublishes>();
        services.TryAddSingleton<Backdafuckup.Smb.ISmbShareConnector, Backdafuckup.Smb.SmbShareConnector>();
        services.TryAddSingleton<Backdafuckup.Ssh.ISshConnector, Backdafuckup.Ssh.SshConnector>();
        services.AddHostedService<FileRestore.FileRestoreSweeper>();
        services.AddSingleton<Sql.SqlCatalog>();
        services.AddSingleton<Sql.SqlExportFiles>();
        services.AddSingleton<Sql.SqlBackupDownloads>();
        services.AddSingleton<Sql.SqlPublishLeftovers>();
        services.AddSingleton<Sql.SqlRestoreCleanup>();
        services.AddHostedService(sp => sp.GetRequiredService<Sql.SqlRestoreCleanup>());
        services.AddHostedService<Sql.SqlExportSweeper>();
        services.AddSingleton<Sql.SqlCatalogBackfill>();
        services.AddHostedService(sp => sp.GetRequiredService<Sql.SqlCatalogBackfill>());
        return services;
    }

    private static void TryAddSingleton<TService>(this IServiceCollection services, TService instance)
        where TService : class
    {
        if (services.All(d => d.ServiceType != typeof(TService)))
        {
            services.AddSingleton(instance);
        }
    }

    private static void TryAddSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        if (services.All(d => d.ServiceType != typeof(TService)))
        {
            services.AddSingleton<TService, TImplementation>();
        }
    }
}

/// <summary>Every 10 minutes: finishes snapshot cleanups that could not be completed during a backup.</summary>
public sealed class PendingArtifactRetryService(StartupRecovery recovery, RunningSessions running, ILogger<PendingArtifactRetryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await recovery.RetryPendingArtifactsAsync(running, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retrying pending snapshot cleanups failed.");
            }
        }
    }
}

