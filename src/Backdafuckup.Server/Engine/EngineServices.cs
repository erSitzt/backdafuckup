using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Xen;
using Microsoft.EntityFrameworkCore;
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
    StartupRecovery recovery,
    IOptions<EngineOptions> options,
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
                _ => (SessionState.Failed, "Unknown session kind."),
            };

            if (cancellationToken.IsCancellationRequested && state != SessionState.Failed)
            {
                state = SessionState.Stopped;
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
    ILogger<StartupRecovery> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var instance = options.Value.ServerInstanceId;

        var stale = await db.Sessions.Include(s => s.Tasks)
            .Where(s => (s.State == SessionState.Running || s.State == SessionState.Stopping) && s.ServerInstanceId != instance)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var session in stale)
        {
            session.State = SessionState.Failed;
            session.EndedUtc = time.GetUtcNow();
            session.Summary = "The backup service was restarted while this session was running.";
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
        await CleanupArtifactsAsync(db, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupArtifactsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var open = await db.Artifacts.Where(a => a.State != ArtifactState.Done).ToListAsync(cancellationToken).ConfigureAwait(false);
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
        if (services.All(d => d.ServiceType != typeof(IHypervisorProvider)))
        {
            var xen = configuration.GetSection("Xen").Get<XenOptions>() ?? new XenOptions();
            services.AddSingleton<IHypervisorProvider>(new XenProvider(xen));
        }

        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<RepositoryManager>();
        services.AddSingleton<RunningSessions>();
        services.AddSingleton<SessionQueue>();
        services.AddSingleton<SessionTracker>();
        services.AddSingleton<SessionEventWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionEventWriter>());
        services.TryAddSingleton<ILiveNotifier, NullLiveNotifier>();
        services.AddSingleton<BackupSessionRunner>();
        services.AddSingleton<RestoreSessionRunner>();
        services.AddSingleton<MaintenanceSessionRunner>();
        services.AddSingleton<StartupRecovery>();
        services.AddSingleton<JobScheduler>();
        services.AddHostedService<SessionRunnerService>();
        services.AddHostedService(sp => sp.GetRequiredService<JobScheduler>());
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
