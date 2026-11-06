using System.Collections.Concurrent;
using System.Globalization;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Sql;

public sealed class SqlBackupOptions
{
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>VMs whose SQL Server backups run at the same time (over all jobs).</summary>
    public int MaxParallelVms { get; set; } = 8;

    /// <summary>How long a log or full backup waits in the agent for the instance (the snapshot suspend holds it).</summary>
    public TimeSpan LaneLockWait { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan LogJobTimeout { get; set; } = TimeSpan.FromHours(2);

    public TimeSpan FullJobTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>A scheduled full or system backup missed by more than this (server down) waits for its next time.</summary>
    public TimeSpan MisfireGrace { get; set; } = TimeSpan.FromHours(12);
}

/// <summary>
/// Runs the SQL Server backups of jobs with <see cref="SqlBackupSettings.Enabled"/> in their VMs: log backups every
/// interval, native and system fulls on their schedules, a native full at once for a database whose chain broke or
/// never started. One session per job and day collects the runs. After each run the chain watch updates the SQL
/// catalog's chain state and warns about gaps, other tools' backups, SIMPLE recovery and the RPO.
/// </summary>
public sealed class SqlBackupService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentService agents,
    AgentRegistry registry,
    SqlJobs jobs,
    RepositoryManager repositories,
    CredentialProtector protector,
    SessionTracker tracker,
    SessionEventWriter events,
    SessionQueue queue,
    IOptions<SqlBackupOptions> options,
    IOptions<EngineOptions> engine,
    TimeProvider time,
    ILogger<SqlBackupService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<(Guid Job, string Vm), Task> _running = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionLocks = new();

    /// <summary>Per day session: the runs of all its VMs that started in the same minute, summed for the summary.</summary>
    private readonly ConcurrentDictionary<Guid, (string Slot, int Vms, int Logs, int Fulls, long Bytes)> _slotTotals = new();
    private readonly ConcurrentDictionary<(Guid Job, string Vm), DateOnly> _retained = new();
    private SemaphoreSlim? _slots;

    /// <summary>The worst of the states (Failed over Warning over Succeeded); Running while one still runs.</summary>
    public static SessionState Worst(IEnumerable<SessionState> states)
    {
        var all = states.ToList();
        return all.Contains(SessionState.Running) ? SessionState.Running
            : all.Contains(SessionState.Failed) ? SessionState.Failed
            : all.Contains(SessionState.Warning) || all.Contains(SessionState.Stopped) ? SessionState.Warning
            : SessionState.Succeeded;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxParallelVms));
        using var timer = new PeriodicTimer(options.Value.TickInterval, time);
        try
        {
            do
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Scheduling the SQL Server backups failed; tried again at the next tick.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_running.Values).ConfigureAwait(false);
    }

    /// <summary>Waits until the runs started so far have finished (tests).</summary>
    public Task WaitIdleAsync() => Task.WhenAll(_running.Values);

    /// <summary>Starts the runs that are due (also called by tests).</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var due = await db.Jobs.AsNoTracking().Include(j => j.Objects).Include(j => j.Connection)
            .Where(j => j.Enabled && j.SqlBackup.Enabled)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var states = await db.SqlBackupSchedules.ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in due)
        {
            foreach (var vm in job.Objects.OrderBy(o => o.Order))
            {
                var state = states.FirstOrDefault(s => s.JobId == job.Id && s.VmNativeId == vm.NativeId);
                if (state is null)
                {
                    // First contact: the log backups start at once (a database without chain gets its full then).
                    state = new SqlBackupScheduleEntity
                    {
                        JobId = job.Id,
                        VmNativeId = vm.NativeId,
                        NextLogUtc = now,
                        NextFullUtc = ScheduleCalculator.Next(job.SqlBackup.FullSchedule, now),
                        NextSystemUtc = ScheduleCalculator.Next(job.SqlBackup.SystemSchedule, now),
                    };
                    db.SqlBackupSchedules.Add(state);
                    states.Add(state);
                }

                var work = new Work(
                    Log: state.NextLogUtc is { } log && log <= now,
                    Full: state.NextFullUtc is { } full && full <= now && now - full <= options.Value.MisfireGrace,
                    System: state.NextSystemUtc is { } system && system <= now && now - system <= options.Value.MisfireGrace);
                SkipMissed(job, state, now);
                if (!work.Any || _running.ContainsKey((job.Id, vm.NativeId)))
                {
                    continue;
                }

                var key = (job.Id, vm.NativeId);
                var run = RunGuardedAsync(job, vm, work, cancellationToken);
                _running[key] = run;
                _ = run.ContinueWith(_ => _running.TryRemove(key, out var _), TaskScheduler.Default);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A full or system backup missed by more than the grace waits for its next time.</summary>
    private void SkipMissed(BackupJob job, SqlBackupScheduleEntity state, DateTimeOffset now)
    {
        if (state.NextFullUtc is { } full && now - full > options.Value.MisfireGrace)
        {
            state.NextFullUtc = ScheduleCalculator.Next(job.SqlBackup.FullSchedule, now);
        }

        if (state.NextSystemUtc is { } system && now - system > options.Value.MisfireGrace)
        {
            state.NextSystemUtc = ScheduleCalculator.Next(job.SqlBackup.SystemSchedule, now);
        }
    }

    private async Task RunGuardedAsync(BackupJob job, BackupJobObject vm, Work work, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await _slots!.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(job, vm, work, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The SQL Server backups of {Vm} in job {Job} failed unexpectedly.", vm.CachedName, job.Name);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>One run for one VM: log backups, then the fulls that are due or needed.</summary>
    public async Task RunAsync(BackupJob job, BackupJobObject vm, Work work, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var (sessionId, taskId, previous) = await OpenDayAsync(job, vm, started, cancellationToken).ConfigureAwait(false);
        var log = events.CreateLog(sessionId, taskId, vm.CachedName);
        var run = new RunOutcome();
        try
        {
            await RunCoreAsync(job, vm, work, sessionId, log, run, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlJobLostException ex)
        {
            run.Warn($"The agent restarted during the run ({ex.Message}); backup files it wrote stay queued in the VM and are sent with the next run.");
        }
        catch (TimeoutException)
        {
            run.Fail("The agent did not finish the SQL Server backups in time; files it wrote stay queued in the VM.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            run.Fail(ex.Message);
        }
        finally
        {
            foreach (var (severity, message) in run.Lines)
            {
                log.Write(severity, message);
            }

            await events.FlushAsync().ConfigureAwait(false);
            await ScheduleNextAsync(job, vm, work, started, run, CancellationToken.None).ConfigureAwait(false);
            await CloseDayAsync(job, sessionId, taskId, previous, started, run, CancellationToken.None).ConfigureAwait(false);
        }

        if (_retained.TryGetValue((job.Id, vm.NativeId), out var day) && day == DateOnly.FromDateTime(started.UtcDateTime))
        {
            return;
        }

        try
        {
            await ApplyRetentionAsync(job.Id, vm.NativeId, CancellationToken.None).ConfigureAwait(false);
            _retained[(job.Id, vm.NativeId)] = DateOnly.FromDateTime(started.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DbUpdateException)
        {
            logger.LogWarning(ex, "Removing old SQL Server backups of {Vm} failed; tried again with the next run.", vm.CachedName);
        }
    }

    private async Task RunCoreAsync(BackupJob job, BackupJobObject vm, Work work, Guid sessionId, IOperationLog log, RunOutcome run, CancellationToken cancellationToken)
    {
        var agent = await agents.FindForVmAsync(vm.NativeId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            run.Warn($"No agent runs in {vm.CachedName}: SQL Server backups need the agent in the VM. Install it (Agents) or turn on guest processing with an account to install it.");
            return;
        }

        var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
        if (!agents.IsOnline(agent.Id))
        {
            run.Warn($"The agent in {host} is offline (last contact {agent.LastContactUtc:yyyy-MM-dd HH:mm} UTC): no SQL Server backups now. The log chain stays intact; the next run catches up.");
            return;
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlBackupChain))
        {
            run.Warn($"The agent in {host} is too old for SQL Server log backups; it updates itself when it is idle.");
            return;
        }

        var account = await SqlEndpoints.AccountAsync(job.SqlCredentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            run.Fail("The job has no SQL Server account for the backups (job settings, guest processing).");
            return;
        }

        var repository = await repositories.GetAsync(job.RepositoryId, cancellationToken).ConfigureAwait(false);
        var source = new ManifestSource(job.Connection.Kind, job.Connection.PlatformId ?? job.ConnectionId.ToString(), vm.NativeId, vm.CachedName, null);
        var context = new RunContext(job, vm, agent.Id, host, account, repository, source, sessionId, log, run);

        if (work.Log)
        {
            await LogBackupsAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (work.Full)
        {
            var databases = await ScheduledFullDatabasesAsync(context, cancellationToken).ConfigureAwait(false);
            if (databases.Count == 0)
            {
                run.Info("Scheduled full backup: no database needs one (their base is the SQL Server snapshot of the image backup).");
            }
            else
            {
                await FullBackupsAsync(context, databases, system: false, "scheduled", cancellationToken).ConfigureAwait(false);
            }
        }

        if (work.System)
        {
            await FullBackupsAsync(context, [], system: true, "scheduled", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LogBackupsAsync(RunContext context, CancellationToken cancellationToken)
    {
        var settings = context.Job.SqlBackup;
        DateTimeOffset since;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            since = await db.SqlBackupSchedules.Where(s => s.JobId == context.Job.Id && s.VmNativeId == context.Vm.NativeId)
                .Select(s => s.ForeignCheckedUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? time.GetUtcNow().AddDays(-1);
        }

        var request = new SqlJobRequest
        {
            Kind = SqlJobKinds.LogBackup,
            Account = context.Account,
            BackupJobId = context.Job.Id,
            SessionId = context.SessionId,
            ExcludedDatabases = [.. settings.ExcludedDatabases],
            FullWithoutChain = settings.FullOnChainBreak,
            ForeignSinceUtc = since.UtcDateTime,
            Folder = settings.StagingFolder,
            LockWaitMs = (int)options.Value.LaneLockWait.TotalMilliseconds,
        };
        var (result, receiver) = await RunJobAsync(context, request, options.Value.LogJobTimeout, cancellationToken).ConfigureAwait(false);
        await using (receiver)
        {
            Report(context, result, receiver, "log");
            if (result.Success)
            {
                context.Run.ForeignChecked = true;
            }

            var broken = await WatchChainAsync(context, result, cancellationToken).ConfigureAwait(false);
            if (broken.Count > 0 && settings.FullOnChainBreak)
            {
                await FullBackupsAsync(context, broken, system: false, "to restart the log chain", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FullBackupsAsync(RunContext context, List<string> databases, bool system, string why, CancellationToken cancellationToken)
    {
        var request = new SqlJobRequest
        {
            Kind = SqlJobKinds.FullBackup,
            Account = context.Account,
            BackupJobId = context.Job.Id,
            SessionId = context.SessionId,
            Databases = system ? null : databases,
            SystemDatabases = system,
            Folder = context.Job.SqlBackup.StagingFolder,
            LockWaitMs = (int)options.Value.LaneLockWait.TotalMilliseconds,
        };
        context.Run.Info(system
            ? "Full backup of the system databases (master, msdb, model)."
            : $"Full backup {why}: {string.Join(", ", databases.Select(d => $"[{d}]"))}.");
        var (result, receiver) = await RunJobAsync(context, request, options.Value.FullJobTimeout, cancellationToken).ConfigureAwait(false);
        await using (receiver)
        {
            Report(context, result, receiver, system ? "system" : "full");
            if (!system && result.Success)
            {
                await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var outcome in result.BackupDatabases?.Where(d => d.Action == SqlBackupActions.Full) ?? [])
                {
                    var row = await DatabaseRowAsync(db, context, outcome, cancellationToken).ConfigureAwait(false);
                    SetChain(row, SqlChainState.Ok, null);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<(SqlJobResult Result, SqlBackupReceiver Receiver)> RunJobAsync(RunContext context, SqlJobRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var receiver = new SqlBackupReceiver(
            context.Repository, context.Job.RepositoryId, context.Source, new ManifestJob(context.Job.Id, context.Job.Name), context.SessionId, dbFactory, time);
        try
        {
            var result = await jobs.RunAsync(context.AgentId, request, timeout, cancellationToken, receiver).ConfigureAwait(false);
            return (result, receiver);
        }
        catch
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The job's result as log lines: what was backed up, skipped or failed, and what waits in the VM.</summary>
    private static void Report(RunContext context, SqlJobResult result, SqlBackupReceiver receiver, string kind)
    {
        var run = context.Run;
        foreach (var line in result.Log)
        {
            run.Info(line);
        }

        if (!result.Success)
        {
            run.Fail($"The {(kind == "log" ? "log" : kind == "system" ? "system database" : "full")} backups in {context.Host} failed: {result.Error}");
            return;
        }

        foreach (var failed in result.BackupDatabases?.Where(d => d.Action == SqlBackupActions.Failed) ?? [])
        {
            run.Fail($"{failed.Instance}\\{failed.Database}: {failed.Reason}");
        }

        foreach (var skipped in result.BackupDatabases?.Where(d => d.Action == SqlBackupActions.Skipped && d.Database.Length == 0) ?? [])
        {
            run.Warn($"{skipped.Instance}: {skipped.Reason}.");
        }

        var (bytes, stored) = receiver.Totals;
        run.Logs += receiver.Committed.Count(c => c.Kind == SqlBackupActions.Log);
        run.Fulls += receiver.Committed.Count(c => c.Kind != SqlBackupActions.Log);
        run.Bytes += bytes;
        if (receiver.Committed.Count > 0)
        {
            var names = receiver.Committed.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {(g.Key == SqlBackupActions.Log ? "log" : g.Key == "system" ? "system database" : "full")} backup(s)");
            run.Info($"Stored {string.Join(", ", names)}: {bytes:N0} bytes, {stored:N0} bytes new in the repository.");
        }

        if (result.QueuedFiles > 0)
        {
            run.Warn($"{result.QueuedFiles} backup file(s) wait in {context.Host} for the next run (the upload or its confirmation failed).");
        }
    }

    /// <summary>
    /// Updates the chain state of every database the log job saw; returns the databases whose chain broke (they need a
    /// full backup to restart it).
    /// </summary>
    private async Task<List<string>> WatchChainAsync(RunContext context, SqlJobResult result, CancellationToken cancellationToken)
    {
        var broken = new List<string>();
        if (!result.Success || result.BackupDatabases is null)
        {
            return broken;
        }

        var now = time.GetUtcNow();
        var interval = TimeSpan.FromMinutes(context.Job.SqlBackup.LogIntervalMinutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var outcome in result.BackupDatabases.Where(d => d.Database.Length > 0 && !IsSystem(d.Database)))
        {
            var row = await DatabaseRowAsync(db, context, outcome, cancellationToken).ConfigureAwait(false);
            row.RecoveryModel = outcome.RecoveryModel ?? row.RecoveryModel;
            var name = $"{outcome.Instance}\\{outcome.Database}";
            var foreign = (result.ForeignBackups ?? []).Where(f => f.Instance == outcome.Instance && f.Database == outcome.Database && !f.BackupSet.IsCopyOnly).ToList();
            foreach (var other in foreign.Where(f => f.BackupSet.Type != "L"))
            {
                context.Run.Warn($"{name}: another tool took a {(other.BackupSet.Type == "I" ? "differential" : "full")} backup at {Local(other.BackupSet.Finish)} to {other.BackupSet.Device ?? "an unknown device"} (backup set '{other.BackupSet.Name}'). The log chain continues; switch that backup to COPY_ONLY.");
            }

            var foreignLogs = foreign.Where(f => f.BackupSet.Type == "L").ToList();
            switch (outcome.Action)
            {
                case SqlBackupActions.Skipped when outcome.Reason?.StartsWith("SIMPLE", StringComparison.Ordinal) == true:
                    SetChain(row, SqlChainState.Simple, "SIMPLE recovery: no log backups; it can be restored to the times of its full backups only.");
                    break;
                case SqlBackupActions.Skipped when outcome.Reason?.StartsWith("no log chain", StringComparison.Ordinal) == true:
                    SetChain(row, SqlChainState.NoBase, "No log chain: a full backup has to start it (turned off in the job).");
                    context.Run.Warn($"{name}: {row.ChainReason}");
                    break;
                case SqlBackupActions.Full:
                    SetChain(row, SqlChainState.Ok, null);
                    context.Run.Warn($"{name}: {outcome.Reason}.");
                    break;
                case SqlBackupActions.Log:
                {
                    var sets = await db.SqlBackupSets.AsNoTracking().Where(s => s.SqlDatabaseId == row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                    var chain = new SqlChain(sets.Select(SqlChainSet.From));
                    var newest = sets.Where(s => s.Kind == SqlBackupSetKind.Log).OrderByDescending(s => s.FinishUtc).FirstOrDefault();
                    if (newest is not null && chain.Latest is { } latest && latest >= newest.FinishUtc)
                    {
                        SetChain(row, SqlChainState.Ok, null);
                        break;
                    }

                    var reason = foreignLogs.Count > 0
                        ? $"another tool took log backup(s): {string.Join("; ", foreignLogs.Select(f => $"{Local(f.BackupSet.Finish)} to {f.BackupSet.Device ?? "an unknown device"}"))}. Turn those log backups off; only backdafuckup may back up the log"
                        : chain.Coverage().Gaps.LastOrDefault()?.Reason ?? "no full backup is continued by the log backups";
                    SetChain(row, SqlChainState.Broken, $"The log chain broke: {reason}.");
                    context.Run.Warn($"{name}: {row.ChainReason}");
                    broken.Add(outcome.Database);
                    break;
                }
            }

            if (row.ChainState == SqlChainState.Ok && row.LastLogUtc is { } last && now - last > 2 * interval && outcome.Action != SqlBackupActions.Full)
            {
                context.Run.Warn($"{name}: the last log backup is from {last:yyyy-MM-dd HH:mm} UTC, more than twice the interval ({interval.TotalMinutes:0} min) ago.");
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return broken;
    }

    /// <summary>The user databases a scheduled native full covers: all with FullForAllDatabases, else those without a snapshot base.</summary>
    private async Task<List<string>> ScheduledFullDatabasesAsync(RunContext context, CancellationToken cancellationToken)
    {
        var job = context.Job;
        var snapshotBase = job.GuestProcessing && job.GuestProcessingMode == GuestProcessingMode.SqlSnapshot;
        var recent = time.GetUtcNow().AddDays(-7);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.SqlDatabases.AsNoTracking()
            .Where(d => d.VmNativeId == context.Vm.NativeId && d.LastSeenUtc >= recent)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Where(d => !IsSystem(d.Name) && !job.SqlBackup.ExcludedDatabases.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
            .Where(d => job.SqlBackup.FullForAllDatabases || !snapshotBase || Major(d.ProductVersion) < 16)
            .Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<SqlDatabaseEntity> DatabaseRowAsync(AppDbContext db, RunContext context, SqlBackupDatabaseOutcome outcome, CancellationToken cancellationToken) =>
        await SqlBackupCatalog.DatabaseAsync(
            db, context.Source.PlatformId, context.Vm.NativeId, context.Vm.CachedName, outcome.Instance, outcome.Database,
            Guid.TryParse(outcome.FamilyGuid, out var family) ? family : Guid.Empty, context.Job.Id, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);

    private void SetChain(SqlDatabaseEntity row, SqlChainState state, string? reason)
    {
        if (row.ChainState != state)
        {
            row.ChainChangedUtc = time.GetUtcNow();
        }

        row.ChainState = state;
        row.ChainReason = reason;
        row.LastSeenUtc = time.GetUtcNow();
    }

    private async Task ScheduleNextAsync(BackupJob job, BackupJobObject vm, Work work, DateTimeOffset started, RunOutcome run, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.SqlBackupSchedules.FirstOrDefaultAsync(s => s.JobId == job.Id && s.VmNativeId == vm.NativeId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (work.Log)
        {
            state.NextLogUtc = ScheduleCalculator.NextInterval(job.SqlBackup.LogIntervalMinutes, job.Schedule.TimeZoneId, now);
        }

        if (work.Full)
        {
            state.NextFullUtc = ScheduleCalculator.Next(job.SqlBackup.FullSchedule, now);
        }

        if (work.System)
        {
            state.NextSystemUtc = ScheduleCalculator.Next(job.SqlBackup.SystemSchedule, now);
        }

        if (run.ForeignChecked)
        {
            state.ForeignCheckedUtc = started;
        }

        state.LastRunUtc = started;
        state.LastResult = run.State switch
        {
            SessionState.Failed => SessionResult.Failed,
            SessionState.Warning => SessionResult.Warning,
            _ => SessionResult.Success,
        };
        state.LastMessage = run.Lines.LastOrDefault(l => l.Severity != LogSeverity.Info).Message;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The job's session of the day (created on the first run) and the VM's task in it, both set running.</summary>
    private async Task<(Guid SessionId, Guid TaskId, SessionState? Previous)> OpenDayAsync(BackupJob job, BackupJobObject vm, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var gate = _sessionLocks.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, ScheduleCalculator.FindZone(job.Schedule.TimeZoneId)).DateTime);
            var name = $"SQL Server backups – {job.Name} – {day:yyyy-MM-dd}";
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var session = await db.Sessions.Include(s => s.Tasks)
                .FirstOrDefaultAsync(s => s.Kind == SessionKind.SqlBackup && s.JobId == job.Id && s.Name == name, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                session = new SessionEntity
                {
                    Kind = SessionKind.SqlBackup,
                    JobId = job.Id,
                    RepositoryId = job.RepositoryId,
                    Name = name,
                    TriggeredBy = "Schedule",
                    CreatedUtc = now,
                    StartedUtc = now,
                };
                db.Sessions.Add(session);
            }

            var task = session.Tasks.FirstOrDefault(t => t.ObjectNativeId == vm.NativeId);
            if (task is null)
            {
                task = new SessionTaskEntity { SessionId = session.Id, ObjectNativeId = vm.NativeId, ObjectName = vm.CachedName, State = SessionState.Queued };
                session.Tasks.Add(task);
            }

            var previous = task.State is SessionState.Queued or SessionState.Running ? (SessionState?)null : task.State;
            task.State = SessionState.Running;
            task.StartedUtc ??= now;
            task.EndedUtc = null;
            session.State = SessionState.Running;
            session.EndedUtc = null;
            session.ServerInstanceId = engine.Value.ServerInstanceId;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tracker.UpdateSessionAsync(session.Id, _ => { }, cancellationToken).ConfigureAwait(false);
            return (session.Id, task.Id, previous);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CloseDayAsync(BackupJob job, Guid sessionId, Guid taskId, SessionState? previous, DateTimeOffset started, RunOutcome run, CancellationToken cancellationToken)
    {
        var gate = _sessionLocks.GetOrAdd(job.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            await tracker.UpdateTaskAsync(taskId, t =>
            {
                t.State = Worst(previous is { } p ? [p, run.State] : [run.State]);
                t.EndedUtc = now;
                t.Progress = 100;
                t.TransferredBytes += run.Bytes;
                t.Error = run.State == SessionState.Succeeded ? t.Error : run.Lines.LastOrDefault(l => l.Severity != LogSeverity.Info).Message;
            }, cancellationToken).ConfigureAwait(false);

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var states = await db.SessionTasks.Where(t => t.SessionId == sessionId).Select(t => t.State).ToListAsync(cancellationToken).ConfigureAwait(false);
            // The job's time zone, as its schedule and the console's log times. The VMs of one run (started in the same
            // minute) are summed, so the summary covers all of them, not only the VM that finished last.
            var local = TimeZoneInfo.ConvertTime(started, ScheduleCalculator.FindZone(job.Schedule.TimeZoneId)).ToString("HH:mm", CultureInfo.InvariantCulture);
            (string Slot, int Vms, int Logs, int Fulls, long Bytes) totals = _slotTotals.TryGetValue(sessionId, out var slot) && slot.Slot == local
                ? (local, slot.Vms + 1, slot.Logs + run.Logs, slot.Fulls + run.Fulls, slot.Bytes + run.Bytes)
                : (local, 1, run.Logs, run.Fulls, run.Bytes);
            _slotTotals[sessionId] = totals;
            await tracker.UpdateSessionAsync(sessionId, s =>
            {
                s.State = Worst(states);
                s.EndedUtc = s.State == SessionState.Running ? null : now;
                s.TransferredBytes += run.Bytes;
                s.Progress = 100;
                s.Summary = $"Last run {local}: {totals.Logs} log backup(s), {totals.Fulls} full backup(s), {totals.Bytes:N0} bytes" +
                    (totals.Vms > 1 ? $" ({totals.Vms} VMs)" : string.Empty) +
                    (run.State == SessionState.Succeeded ? "." : $"; {run.Lines.LastOrDefault(l => l.Severity != LogSeverity.Info).Message}");
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes SQL backups the VM's point-in-time window no longer needs. The window is that of its restore points: native
    /// and system fulls back to the oldest kept restore point (plus the newest one before it), log backups back to the
    /// oldest kept base.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(Guid jobId, string vmNativeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return 0;
        }

        var backups = await db.SqlBackups.Where(b => b.JobId == jobId && b.VmNativeId == vmNativeId && b.State == SqlBackupState.Available)
            .OrderBy(b => b.CreatedUtc).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (backups.Count == 0)
        {
            return 0;
        }

        var oldestPoint = await db.RestorePoints.Where(r => r.JobId == jobId && r.VmNativeId == vmNativeId && r.State == RestorePointState.Available)
            .MinAsync(r => (DateTimeOffset?)r.CreatedUtc, cancellationToken).ConfigureAwait(false);
        var fulls = backups.Where(b => b.Kind == SqlBackupKind.Full).ToList();

        // Without restore points the newest native full is the start of the window.
        var window = oldestPoint ?? fulls.LastOrDefault()?.CreatedUtc;
        if (window is null)
        {
            return 0;
        }

        var keep = new HashSet<Guid>();
        foreach (var kind in new[] { SqlBackupKind.Full, SqlBackupKind.SystemFull })
        {
            var ofKind = backups.Where(b => b.Kind == kind).ToList();
            keep.UnionWith(ofKind.Where(b => b.CreatedUtc >= window).Select(b => b.Id));
            foreach (var instance in ofKind.Select(b => b.Instance).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (ofKind.LastOrDefault(b => b.Instance == instance && b.CreatedUtc < window) is { } before)
                {
                    keep.Add(before.Id);
                }
            }
        }

        var oldestBase = backups.Where(b => b.Kind == SqlBackupKind.Full && keep.Contains(b.Id)).Select(b => (DateTimeOffset?)b.CreatedUtc).Min();
        var logsFrom = oldestBase is { } full && full < window ? full : window.Value;
        keep.UnionWith(backups.Where(b => b.Kind == SqlBackupKind.Log && b.CreatedUtc >= logsFrom).Select(b => b.Id));

        var removed = 0;
        foreach (var backup in backups.Where(b => !keep.Contains(b.Id)))
        {
            try
            {
                var repository = await repositories.GetAsync(backup.RepositoryId, cancellationToken).ConfigureAwait(false);
                backup.State = SqlBackupState.Deleting;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await repository.DeleteSqlBackupAsync(backup.Id, cancellationToken).ConfigureAwait(false);
                db.SqlBackups.Remove(backup);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                removed++;
            }
            catch (SqlBackupPinnedException)
            {
                // In use by a restore: removed with a later run.
                backup.State = SqlBackupState.Available;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (removed > 0)
        {
            var repositoryId = backups[0].RepositoryId;
            var name = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "repository";
            try
            {
                await queue.EnqueueMaintenanceAsync(repositoryId, name, "SQL backup retention", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Maintenance is queued already.
            }
        }

        return removed;
    }

    private static bool IsSystem(string database) => database is "master" or "msdb" or "model" or "tempdb";

    private static int Major(string? version) =>
        version is not null && int.TryParse(version.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;

    private static string Local(string time) => $"{time.Replace('T', ' ')} (server time)";

    /// <summary>What is due for a VM.</summary>
    public sealed record Work(bool Log, bool Full, bool System)
    {
        public bool Any => Log || Full || System;
    }

    private sealed record RunContext(
        BackupJob Job, BackupJobObject Vm, Guid AgentId, string Host, SqlAccount Account, IBackupRepository Repository, ManifestSource Source,
        Guid SessionId, IOperationLog Log, RunOutcome Run);

    /// <summary>A run's result, collected while it runs and written to the session at its end.</summary>
    private sealed class RunOutcome
    {
        public List<(LogSeverity Severity, string Message)> Lines { get; } = [];

        public SessionState State { get; private set; } = SessionState.Succeeded;

        public int Logs { get; set; }

        public int Fulls { get; set; }

        public long Bytes { get; set; }

        public bool ForeignChecked { get; set; }

        public void Info(string message) => Lines.Add((LogSeverity.Info, message));

        public void Warn(string message)
        {
            Lines.Add((LogSeverity.Warning, message));
            if (State == SessionState.Succeeded)
            {
                State = SessionState.Warning;
            }
        }

        public void Fail(string message)
        {
            Lines.Add((LogSeverity.Error, message));
            State = SessionState.Failed;
        }
    }
}
