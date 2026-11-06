using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Cleans up after restores as a new database that a server restart interrupted (the startup recovery marks them). Once
/// the target VM's agent is reachable it checks the instance with the session's account: if the database is attached
/// with the restored files, only the session was interrupted (it becomes a warning); otherwise the files the restore
/// wrote are deleted, except those a database uses. A database still RESTORING or RECOVERING is waited for (the agent's
/// own job from before the restart ends first). Each session is handled once; the session's log says what happened.
/// </summary>
public sealed class SqlRestoreCleanup(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentService agents,
    SqlJobs jobs,
    CredentialProtector protector,
    SessionEventWriter events,
    SessionTracker tracker,
    IOptions<SqlRestoreOptions> options,
    TimeProvider time,
    ILogger<SqlRestoreCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.Value.CleanupStartDelay, time, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Checking SQL Server restores interrupted by a restart failed; tried again in a minute.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Handles the marked sessions whose target agent is reachable (also called by tests).</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        List<SessionEntity> sessions;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var since = time.GetUtcNow() - options.Value.CleanupGiveUpAfter;
            sessions = await db.Sessions.AsNoTracking()
                .Where(s => s.Kind == SessionKind.SqlRestore && s.State == SessionState.Failed && s.Parameters != null && s.Parameters.Contains(SqlRestoreRecovery.CleanupPendingJson))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var expired in sessions.Where(s => s.EndedUtc < since).ToList())
            {
                // Given up: the summary already names the files.
                sessions.Remove(expired);
                await tracker.UpdateSessionAsync(expired.Id, s => s.Parameters = SqlRestoreRecovery.WithCleanupPending(s.Parameters, pending: false), CancellationToken.None).ConfigureAwait(false);
            }
        }

        foreach (var session in sessions)
        {
            await CleanUpAsync(session, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CleanUpAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<SqlRestoreRequestDto>(session.Parameters!);
        var planned = SqlRestoreRecovery.Planned(session.Parameters);
        if (request is null || request.Overwrite || planned.Count == 0)
        {
            await DoneAsync(session.Id, null, null).ConfigureAwait(false);
            return;
        }

        var agent = await agents.FindForVmAsync(request.TargetVmNativeId, cancellationToken).ConfigureAwait(false);
        if (agent is null || !agents.IsOnline(agent.Id))
        {
            return;
        }

        var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
        var log = events.CreateLog(session.Id, null, host);
        var name = SqlCatalog.QuoteName(request.DatabaseName);
        var where = $"{host}, instance {request.TargetInstance}";
        var account = await SqlEndpoints.AccountAsync(request.CredentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            log.Warning($"The account of this restore no longer exists, so {where} cannot be checked: if {name} is not attached there, delete {string.Join(", ", planned)}.");
            await DoneAsync(session.Id, null, null).ConfigureAwait(false);
            return;
        }

        var check = await jobs.RunAsync(
            agent.Id, new SqlJobRequest { Kind = SqlJobKinds.Inventory, Instance = request.TargetInstance, Account = account }, TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);
        var instance = check.Inventory?.Instances.FirstOrDefault(i => string.Equals(i.Name, request.TargetInstance, StringComparison.OrdinalIgnoreCase));
        if (!check.Success || instance is null)
        {
            // Tried again at the next minute (SQL Server still starting, or the instance stopped).
            logger.LogInformation("Checking {Where} for the interrupted restore {Session} failed: {Error}", where, session.Id, check.Error ?? "instance not listed");
            return;
        }

        var database = instance.Databases.FirstOrDefault(d => string.Equals(d.Name, request.DatabaseName, StringComparison.OrdinalIgnoreCase));
        if (database is { State: "RESTORING" or "RECOVERING" })
        {
            return;
        }

        var used = instance.Databases.SelectMany(d => d.Files).Select(f => f.PhysicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (database is { State: "ONLINE" } && database.Files.Select(f => f.PhysicalName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(planned))
        {
            var finished = $"Restored as {name} on {where}: the restore had finished when the backup service was restarted, only the session was interrupted. " +
                "Check the database's owner and users (the log lacks their description).";
            log.Info($"Checked after the restart: {name} is attached with the restored files.");
            await DoneAsync(session.Id, SessionState.Warning, finished).ConfigureAwait(false);
            return;
        }

        var delete = planned.Where(f => !used.Contains(f)).ToList();
        var kept = planned.Where(used.Contains).ToList();
        log.Info($"Checked after the restart: {name} is {(database is null ? "not attached" : "not attached with the restored files")}; deleting the files this restore wrote that no database uses ({delete.Count}).");
        var deleted = delete.Count == 0;
        if (delete.Count > 0)
        {
            try
            {
                var result = await jobs.RunAsync(
                    agent.Id, new SqlJobRequest { Kind = SqlJobKinds.DeleteFiles, Account = account, Files = delete }, TimeSpan.FromMinutes(30), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var line in result.Log)
                {
                    log.Info(line);
                }

                deleted = result.Success;
                if (!result.Success)
                {
                    log.Warning($"Delete them by hand: {result.Error}");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                log.Warning($"The files could not be deleted ({ex.Message}); delete them by hand: {string.Join(", ", delete)}");
            }
        }

        var summary = $"The backup service was restarted during the restore of {name} to {where}; {name} was not attached. " +
            (deleted ? "The files this restore wrote were deleted again." : $"Not all files this restore wrote could be deleted (see the log): {string.Join(", ", delete)}.") +
            (kept.Count > 0 ? $" Kept, since a database uses them: {string.Join(", ", kept)}." : string.Empty);
        await DoneAsync(session.Id, SessionState.Failed, summary).ConfigureAwait(false);
    }

    private async Task DoneAsync(Guid sessionId, SessionState? state, string? summary)
    {
        await events.FlushAsync().ConfigureAwait(false);
        await tracker.UpdateSessionAsync(
            sessionId,
            s =>
            {
                s.Parameters = SqlRestoreRecovery.WithCleanupPending(s.Parameters, pending: false);
                s.State = state ?? s.State;
                s.Summary = summary ?? s.Summary;
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
