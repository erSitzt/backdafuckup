using System.Collections.Concurrent;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Removes SQL Server databases that publishes left attached in a VM after their session had ended: a server restart
/// during the publish, a Stop before the volumes were mounted, or a drop that failed in the agent. The agent reports them
/// in its poll from its publish journal; this runs drop-published with the account of the publish's session (the agent
/// never stores one) and notes the result in that session's log. A failed or impossible removal is tried again after
/// <see cref="RetryAfter"/>.
/// </summary>
public sealed class SqlPublishLeftovers(
    IDbContextFactory<AppDbContext> dbFactory,
    SqlJobs jobs,
    CredentialProtector protector,
    SessionEventWriter events,
    TimeProvider time,
    ILogger<SqlPublishLeftovers> logger)
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DropTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(Guid Agent, Guid Publish), DateTimeOffset> _attempts = new();
    private readonly ConcurrentDictionary<(Guid Agent, Guid Publish), Task> _running = new();

    /// <summary>Takes the leftovers of an agent's poll; each one is handled at most every <see cref="RetryAfter"/>.</summary>
    public void Report(Guid agentId, IReadOnlyList<LeftoverPublishDatabase>? leftovers)
    {
        foreach (var leftover in leftovers ?? [])
        {
            var key = (agentId, leftover.PublishId);
            var now = time.GetUtcNow();
            if (_running.ContainsKey(key) || (_attempts.TryGetValue(key, out var last) && now - last < RetryAfter))
            {
                continue;
            }

            _attempts[key] = now;
            var task = Task.Run(() => RemoveAsync(agentId, leftover, CancellationToken.None));
            _running[key] = task;
            _ = task.ContinueWith(_ => _running.TryRemove(key, out var _), TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the removals started so far (tests).</summary>
    public Task WaitIdleAsync() => Task.WhenAll(_running.Values);

    private async Task RemoveAsync(Guid agentId, LeftoverPublishDatabase leftover, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
            var host = agent is null ? $"agent {agentId}" : $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
            var session = await db.Sessions.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == leftover.PublishId && (s.Kind == SessionKind.SqlPublish || s.Kind == SessionKind.SqlExport), cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                logger.LogWarning(
                    "The agent in {Host} reports SQL Server database {Database} left attached by publish {PublishId} (files below {Folder}), which no session on this server knows (deleted, or published by an older version). Remove it in SQL Server.",
                    host, leftover.Database, leftover.PublishId, leftover.Folder);
                return;
            }

            if (session.State is SessionState.Running or SessionState.Stopping or SessionState.Queued)
            {
                // Still the publish's own business: its unpublish removes the database.
                return;
            }

            var (instance, credentialId) = session.Kind == SessionKind.SqlExport
                ? JsonSerializer.Deserialize<SqlExportRequestDto>(session.Parameters ?? "null") is { } export ? (export.TargetInstance, export.CredentialId) : (null, Guid.Empty)
                : JsonSerializer.Deserialize<SqlPublishRequestDto>(session.Parameters ?? "null") is { } publish ? (publish.TargetInstance, publish.CredentialId) : (null, Guid.Empty);
            var name = instance is not null && leftover.Database.StartsWith(instance + "\\", StringComparison.OrdinalIgnoreCase)
                ? leftover.Database[(instance.Length + 1)..]
                : leftover.Database[(leftover.Database.IndexOf('\\', StringComparison.Ordinal) + 1)..];
            instance ??= leftover.Database.Split('\\')[0];
            var account = credentialId == Guid.Empty ? null : await SqlEndpoints.AccountAsync(credentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false);
            var log = events.CreateLog(session.Id, null, host);
            if (account is null)
            {
                log.Warning($"The database {SqlCatalog.QuoteName(name)} stayed attached to instance {instance} after this publish ended, and its SQL account no longer exists: remove the database in SQL Server.");
                await events.FlushAsync().ConfigureAwait(false);
                return;
            }

            var result = await jobs.RunAsync(
                agentId,
                new SqlJobRequest { Kind = SqlJobKinds.DropPublished, Instance = instance, Account = account, Database = name, PublishFolder = leftover.Folder },
                DropTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                log.Info($"The database {SqlCatalog.QuoteName(name)} that stayed attached to instance {instance} after this publish ended was removed now (as {account.UserName}).");
                _attempts.TryRemove((agentId, leftover.PublishId), out _);
            }
            else
            {
                log.Warning($"The database {SqlCatalog.QuoteName(name)} stayed attached to instance {instance} after this publish ended; removing it failed: {result.Error}. Tried again in {RetryAfter.TotalMinutes:0} minutes.");
            }

            await events.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Removing the left-over database {Database} of publish {PublishId} in agent {AgentId} failed; tried again in {Minutes} minutes.", leftover.Database, leftover.PublishId, agentId, RetryAfter.TotalMinutes);
        }
    }
}
