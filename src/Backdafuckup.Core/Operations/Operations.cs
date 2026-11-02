namespace Backdafuckup.Core.Operations;

public enum LogSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// User-visible operation log of a session/task (shown live in the UI, Veeam style).
/// Entries with the same <c>key</c> replace each other (e.g. a progress line that updates in place).
/// </summary>
public interface IOperationLog
{
    void Write(LogSeverity severity, string message, string? key = null);
}

public static class OperationLogExtensions
{
    public static void Info(this IOperationLog log, string message, string? key = null) => log.Write(LogSeverity.Info, message, key);

    public static void Warning(this IOperationLog log, string message, string? key = null) => log.Write(LogSeverity.Warning, message, key);

    public static void Error(this IOperationLog log, string message, string? key = null) => log.Write(LogSeverity.Error, message, key);
}

public sealed class NullOperationLog : IOperationLog
{
    public static readonly NullOperationLog Instance = new();

    public void Write(LogSeverity severity, string message, string? key = null)
    {
    }
}

public enum ArtifactState
{
    /// <summary>Exists while an operation is running; must be cleaned up if the operation dies.</summary>
    Active,

    /// <summary>The owning restore point was committed; the artifact must be committed (e.g. data_destroy).</summary>
    CommitPending,

    /// <summary>Must be removed.</summary>
    CleanupPending,

    Done,
}

/// <summary>A platform object created by us (snapshot, task, restored disk...) tracked for crash recovery.</summary>
public sealed record ProviderArtifact(
    Guid Id,
    string Kind,
    string NativeId,
    string? NativeRef,
    Guid? SessionId,
    Guid? RestorePointId,
    ArtifactState State,
    string? Data);

/// <summary>Durable ledger of artifacts. Write before waiting on the platform, so a crash never leaks objects.</summary>
public interface IArtifactLedger
{
    Task<Guid> RecordAsync(string kind, string nativeId, string? nativeRef, string? data, CancellationToken cancellationToken);

    Task UpdateAsync(Guid artifactId, ArtifactState state, string? nativeId, CancellationToken cancellationToken);
}

/// <summary>In-memory ledger for tests and tools.</summary>
public sealed class InMemoryArtifactLedger : IArtifactLedger
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, ProviderArtifact> _items = [];

    public IReadOnlyList<ProviderArtifact> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items.Values];
            }
        }
    }

    public Task<Guid> RecordAsync(string kind, string nativeId, string? nativeRef, string? data, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        lock (_lock)
        {
            _items[id] = new ProviderArtifact(id, kind, nativeId, nativeRef, null, null, ArtifactState.Active, data);
        }

        return Task.FromResult(id);
    }

    public Task UpdateAsync(Guid artifactId, ArtifactState state, string? nativeId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(artifactId, out var item))
            {
                _items[artifactId] = item with { State = state, NativeId = nativeId ?? item.NativeId };
            }
        }

        return Task.CompletedTask;
    }
}
