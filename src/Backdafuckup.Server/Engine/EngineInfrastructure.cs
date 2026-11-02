using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Storage;
using Backdafuckup.Xen;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

public sealed class EngineOptions
{
    public int MaxConcurrentBackupSessions { get; set; } = 1;

    public int MaxConcurrentRestoreSessions { get; set; } = 2;

    public TimeSpan SchedulerInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MisfireGrace { get; set; } = TimeSpan.FromHours(1);

    public int SessionHistoryDays { get; set; } = 180;

    /// <summary>Identifies this server process; sessions owned by another instance are stale after a restart.</summary>
    public string ServerInstanceId { get; } = Guid.NewGuid().ToString("N");
}

/// <summary>Pushes live updates to clients (implemented with SignalR by the API layer).</summary>
public interface ILiveNotifier
{
    Task SessionUpdatedAsync(SessionSummaryDto session);

    Task TaskUpdatedAsync(SessionTaskDto task);

    Task LogEntryUpsertedAsync(LogEntryDto entry);
}

public sealed class NullLiveNotifier : ILiveNotifier
{
    public Task SessionUpdatedAsync(SessionSummaryDto session) => Task.CompletedTask;

    public Task TaskUpdatedAsync(SessionTaskDto task) => Task.CompletedTask;

    public Task LogEntryUpsertedAsync(LogEntryDto entry) => Task.CompletedTask;
}

public static class DtoMapping
{
    public static SessionSummaryDto ToDto(this SessionEntity s) => new(
        s.Id, s.Kind.ToString(), s.Name, s.State.ToString(), s.TriggeredBy, s.RunMode.ToString(), s.JobId,
        s.CreatedUtc, s.StartedUtc, s.EndedUtc, s.Progress, s.ReadBytes, s.TransferredBytes, s.Summary);

    public static SessionTaskDto ToDto(this SessionTaskEntity t) => new(
        t.Id, t.SessionId, t.ObjectNativeId, t.ObjectName, t.State.ToString(), t.Attempt, t.Progress, t.ReadBytes,
        t.TransferredBytes, t.StartedUtc, t.EndedUtc, t.RestorePointId, t.Error);

    public static LogEntryDto ToDto(this SessionLogEntry e) => new(e.Id, e.SessionId, e.TaskId, e.TimestampUtc, e.Severity.ToString(), e.Message, e.Key);
}

/// <summary>Opens hypervisor sessions for stored connections (decrypts credentials, applies certificate pins).</summary>
public sealed class ConnectionManager(IEnumerable<IHypervisorProvider> providers, IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector)
{
    public IHypervisorProvider Provider(HypervisorKind kind) =>
        providers.FirstOrDefault(p => p.Kind == kind) ?? throw new NotSupportedException($"No provider for {kind}.");

    public ConnectionSettings BuildSettings(HypervisorConnection connection) => new(
        connection.Id,
        connection.Kind,
        connection.CurrentMasterAddress ?? connection.Address,
        connection.Port,
        new NetworkCredential(connection.Credential.Username, protector.Unprotect(connection.Credential.ProtectedSecret)),
        connection.TrustMode,
        connection.Hosts.Select(h => new PinnedCertificate(h.Address, h.SpkiSha256, h.Sha256Thumbprint, h.CertificatePem)).ToList(),
        new Dictionary<string, string> { [XenConnectionOptions.AllowInsecureNbd] = connection.AllowInsecureNbd ? "true" : "false" });

    public async Task<IHypervisorSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = await db.Connections.Include(c => c.Credential).Include(c => c.Hosts)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection {connectionId} does not exist.");
        try
        {
            var session = await Provider(connection.Kind).OpenAsync(BuildSettings(connection), cancellationToken).ConfigureAwait(false);
            connection.Status = ConnectionStatus.Connected;
            connection.LastConnectedUtc = DateTimeOffset.UtcNow;
            connection.LastError = null;
            if (!string.Equals(session.Platform.MasterAddress, connection.CurrentMasterAddress, StringComparison.OrdinalIgnoreCase))
            {
                connection.CurrentMasterAddress = session.Platform.MasterAddress;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            connection.Status = ConnectionStatus.Error;
            connection.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Owns the open backup repositories (each repository directory is locked by this process).</summary>
public sealed class RepositoryManager(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Lazy<Task<IBackupRepository>>> _open = new();

    public async Task<IBackupRepository> GetAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var lazy = _open.GetOrAdd(repositoryId, id => new Lazy<Task<IBackupRepository>>(() => OpenAsync(id)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _open.TryRemove(repositoryId, out _);
            throw;
        }
    }

    public async Task CloseAsync(Guid repositoryId)
    {
        if (_open.TryRemove(repositoryId, out var lazy) && lazy.IsValueCreated)
        {
            await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _open.Keys.ToArray())
        {
            try
            {
                await CloseAsync(id).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<IBackupRepository> OpenAsync(Guid repositoryId)
    {
        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var entity = await db.Repositories.FindAsync(repositoryId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} does not exist.");
        var repository = await LocalDirectoryRepository.OpenAsync(entity.Path, CancellationToken.None, time).ConfigureAwait(false);
        if (repository.Info.RepositoryId != entity.RepositoryUuid)
        {
            await repository.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The directory {entity.Path} contains a different repository ({repository.Info.RepositoryId}).");
        }

        return repository;
    }
}

/// <summary>Running sessions of this process and their cancellation sources (Stop button).</summary>
public sealed class RunningSessions
{
    private readonly ConcurrentDictionary<Guid, (SessionKind Kind, CancellationTokenSource Cancel)> _running = new();

    public int Count(SessionKind kind) => _running.Values.Count(r => r.Kind == kind);

    public bool IsRunning(Guid sessionId) => _running.ContainsKey(sessionId);

    public CancellationTokenSource Register(Guid sessionId, SessionKind kind, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[sessionId] = (kind, cts);
        return cts;
    }

    public void Unregister(Guid sessionId)
    {
        if (_running.TryRemove(sessionId, out var entry))
        {
            entry.Cancel.Dispose();
        }
    }

    public bool RequestStop(Guid sessionId)
    {
        if (_running.TryGetValue(sessionId, out var entry))
        {
            entry.Cancel.Cancel();
            return true;
        }

        return false;
    }
}

/// <summary>
/// Session log pipeline: operation logs enqueue entries; a background writer persists them in batches
/// (entries with a key update the previous line with that key) and pushes them to live clients.
/// </summary>
public sealed class SessionEventWriter(IDbContextFactory<AppDbContext> dbFactory, ILiveNotifier notifier, TimeProvider time, ILogger<SessionEventWriter> logger) : BackgroundService
{
    private readonly Channel<SessionLogEntry> _entries = Channel.CreateUnbounded<SessionLogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _flushWaiters = new();

    public IOperationLog CreateLog(Guid sessionId, Guid? taskId, string? prefix = null) => new SessionOperationLog(this, sessionId, taskId, prefix);

    public void Write(Guid sessionId, Guid? taskId, LogSeverity severity, string message, string? key)
    {
        _entries.Writer.TryWrite(new SessionLogEntry
        {
            SessionId = sessionId,
            TaskId = taskId,
            Severity = severity,
            Message = message,
            Key = key,
            TimestampUtc = time.GetUtcNow(),
        });
    }

    /// <summary>Waits until everything written so far is persisted.</summary>
    public Task FlushAsync()
    {
        var marker = Guid.NewGuid();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushWaiters[marker] = tcs;
        _entries.Writer.TryWrite(new SessionLogEntry { SessionId = marker, Message = "\0flush" });
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<SessionLogEntry>();
        while (await _entries.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 500 && _entries.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            try
            {
                await PersistAsync(batch.Where(e => e.Message != "\0flush").ToList()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                logger.LogError(ex, "Writing {Count} session log entries failed.", batch.Count);
            }

            foreach (var marker in batch.Where(e => e.Message == "\0flush"))
            {
                if (_flushWaiters.TryRemove(marker.SessionId, out var tcs))
                {
                    tcs.TrySetResult();
                }
            }

            if (stoppingToken.IsCancellationRequested && _entries.Reader.Count == 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _entries.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistAsync(List<SessionLogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        // Collapse keyed entries inside the batch to their latest state.
        var collapsed = new List<SessionLogEntry>();
        var keyed = new Dictionary<(Guid, string), SessionLogEntry>();
        foreach (var entry in entries)
        {
            if (entry.Key is null)
            {
                collapsed.Add(entry);
            }
            else if (keyed.TryGetValue((entry.SessionId, entry.Key), out var existing))
            {
                existing.Message = entry.Message;
                existing.Severity = entry.Severity;
                existing.TimestampUtc = entry.TimestampUtc;
            }
            else
            {
                keyed[(entry.SessionId, entry.Key)] = entry;
                collapsed.Add(entry);
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var saved = new List<SessionLogEntry>();
        foreach (var entry in collapsed)
        {
            if (entry.Key is not null)
            {
                var previous = await db.SessionLog.FirstOrDefaultAsync(e => e.SessionId == entry.SessionId && e.Key == entry.Key).ConfigureAwait(false);
                if (previous is not null)
                {
                    previous.Message = entry.Message;
                    previous.Severity = entry.Severity;
                    previous.TimestampUtc = entry.TimestampUtc;
                    saved.Add(previous);
                    continue;
                }
            }

            db.SessionLog.Add(entry);
            saved.Add(entry);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        foreach (var entry in saved)
        {
            await notifier.LogEntryUpsertedAsync(entry.ToDto()).ConfigureAwait(false);
        }
    }

    private sealed class SessionOperationLog(SessionEventWriter writer, Guid sessionId, Guid? taskId, string? prefix) : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null) =>
            writer.Write(sessionId, taskId, severity, prefix is null ? message : $"{prefix}: {message}", key is null ? null : $"{taskId}:{key}");
    }
}
