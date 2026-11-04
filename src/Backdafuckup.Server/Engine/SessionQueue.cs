using System.Text.Json;
using System.Threading.Channels;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>Creates queued sessions (the database is the queue) and wakes up the session runner.</summary>
public sealed class SessionQueue(IDbContextFactory<AppDbContext> dbFactory, RunningSessions running, ILiveNotifier notifier, TimeProvider time)
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<bool> Signal => _signal.Reader;

    public void Wake() => _signal.Writer.TryWrite(true);

    public async Task<Guid> EnqueueBackupAsync(Guid jobId, SessionRunMode runMode, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.Jobs.FindAsync([jobId], cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job {jobId} does not exist.");
        var active = await db.Sessions.AnyAsync(
            s => s.JobId == jobId && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping),
            cancellationToken).ConfigureAwait(false);
        if (active)
        {
            throw new InvalidOperationException($"Job '{job.Name}' is already queued or running.");
        }

        var session = new SessionEntity
        {
            Kind = SessionKind.Backup,
            JobId = jobId,
            RepositoryId = job.RepositoryId,
            Name = job.Name,
            TriggeredBy = triggeredBy,
            RunMode = runMode,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> EnqueueRestoreAsync(RestoreRequestDto request, string vmName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.Restore,
            Name = $"Restore {vmName}",
            TriggeredBy = triggeredBy,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a SQL publish; its repository is recorded so that the repository cannot be removed meanwhile.</summary>
    public async Task<Guid> EnqueueSqlPublishAsync(SqlPublishRequestDto request, Guid repositoryId, string vmName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.SqlPublish,
            Name = $"Publish SQL database {request.Database} ({vmName})",
            TriggeredBy = triggeredBy,
            RepositoryId = repositoryId,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a SQL database export as .bak; its repository is recorded like for a publish.</summary>
    public async Task<Guid> EnqueueSqlExportAsync(SqlExportRequestDto request, Guid repositoryId, string vmName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.SqlExport,
            Name = $"Export SQL database {request.Database} ({vmName}) as .bak",
            TriggeredBy = triggeredBy,
            RepositoryId = repositoryId,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a SQL database restore; its repository is recorded like for a publish.</summary>
    public async Task<Guid> EnqueueSqlRestoreAsync(SqlRestoreRequestDto request, Guid repositoryId, string vmName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.SqlRestore,
            Name = $"Restore SQL database {request.Database} ({vmName})",
            TriggeredBy = triggeredBy,
            RepositoryId = repositoryId,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a file-level restore (files and folders of one restore point to a target).</summary>
    public async Task<Guid> EnqueueFileRestoreAsync(FileRestoreRequestDto request, string vmName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.FileRestore,
            Name = $"Restore files {vmName}",
            TriggeredBy = triggeredBy,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues a push installation of the guest agent into the given VMs.</summary>
    public async Task<Guid> EnqueueAgentDeploymentAsync(InstallAgentsDto request, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = new SessionEntity
        {
            Kind = SessionKind.AgentDeployment,
            Name = request.Vms.Count == 1 ? $"Install agent {request.Vms[0].Name}" : $"Install agent ({request.Vms.Count} VMs)",
            TriggeredBy = triggeredBy,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
            Parameters = JsonSerializer.Serialize(request),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues garbage collection of a repository unless one is already waiting.</summary>
    public async Task<Guid> EnqueueMaintenanceAsync(Guid repositoryId, string repositoryName, string triggeredBy, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var queued = await db.Sessions.FirstOrDefaultAsync(
            s => s.Kind == SessionKind.RepositoryMaintenance && s.RepositoryId == repositoryId && s.State == SessionState.Queued,
            cancellationToken).ConfigureAwait(false);
        if (queued is not null)
        {
            return queued.Id;
        }

        var session = new SessionEntity
        {
            Kind = SessionKind.RepositoryMaintenance,
            RepositoryId = repositoryId,
            Name = $"Maintenance {repositoryName}",
            TriggeredBy = triggeredBy,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
        };
        return await AddAsync(db, session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RequestStopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return false;
        }

        if (session.State == SessionState.Queued)
        {
            session.State = SessionState.Stopped;
            session.EndedUtc = time.GetUtcNow();
            session.Summary = "Stopped before it started.";
        }
        else if (session.State == SessionState.Running && running.RequestStop(sessionId))
        {
            session.State = SessionState.Stopping;
        }
        else
        {
            return false;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
        return true;
    }

    private async Task<Guid> AddAsync(AppDbContext db, SessionEntity session, CancellationToken cancellationToken)
    {
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
        Wake();
        return session.Id;
    }
}

/// <summary>Updates session and task rows and pushes the changes to live clients.</summary>
public sealed class SessionTracker(IDbContextFactory<AppDbContext> dbFactory, ILiveNotifier notifier, TimeProvider time)
{
    public async Task UpdateSessionAsync(Guid sessionId, Action<SessionEntity> update, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        update(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
    }

    public async Task<SessionTaskEntity> AddTaskAsync(Guid sessionId, string objectNativeId, string objectName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = new SessionTaskEntity { SessionId = sessionId, ObjectNativeId = objectNativeId, ObjectName = objectName, State = SessionState.Queued };
        db.SessionTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.TaskUpdatedAsync(task.ToDto()).ConfigureAwait(false);
        return task;
    }

    public async Task UpdateTaskAsync(Guid taskId, Action<SessionTaskEntity> update, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var task = await db.SessionTasks.FindAsync([taskId], cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        update(task);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.TaskUpdatedAsync(task.ToDto()).ConfigureAwait(false);
    }

    /// <summary>Recomputes session progress and byte counters from its tasks.</summary>
    public async Task RollUpAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var session = await db.Sessions.FindAsync([sessionId], cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        var tasks = await db.SessionTasks.Where(t => t.SessionId == sessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (tasks.Count > 0)
        {
            session.Progress = tasks.Average(t => t.Progress);
            session.ReadBytes = tasks.Sum(t => t.ReadBytes);
            session.TransferredBytes = tasks.Sum(t => t.TransferredBytes);
            if (tasks.Any(t => t.Metrics is not null))
            {
                // Session rates refer to the run time of the session (VMs may run in parallel).
                var started = session.StartedUtc ?? session.CreatedUtc;
                var seconds = Math.Max(0, ((session.EndedUtc ?? time.GetUtcNow()) - started).TotalSeconds);
                var running = tasks.Where(t => t.State == SessionState.Running).Select(t => t.Metrics);
                session.Metrics = TransferMetricsDto.Combine(tasks.Select(t => t.Metrics), seconds) with
                {
                    CurrentBytesPerSecond = running.Sum(m => m?.CurrentBytesPerSecond ?? 0),
                };
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
    }
}
