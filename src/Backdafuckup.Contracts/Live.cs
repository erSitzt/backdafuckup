namespace Backdafuckup.Contracts;

public sealed record SessionSummaryDto(
    Guid Id,
    string Kind,
    string Name,
    string State,
    string TriggeredBy,
    string RunMode,
    Guid? JobId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    double Progress,
    long ReadBytes,
    long TransferredBytes,
    string? Summary);

public sealed record SessionTaskDto(
    Guid Id,
    Guid SessionId,
    string ObjectNativeId,
    string ObjectName,
    string State,
    int Attempt,
    double Progress,
    long ReadBytes,
    long TransferredBytes,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    Guid? RestorePointId,
    string? Error);

public sealed record LogEntryDto(long Id, Guid SessionId, Guid? TaskId, DateTimeOffset TimestampUtc, string Severity, string Message, string? Key);

/// <summary>Server-to-client methods of the live SignalR hub (/hubs/live).</summary>
public interface ILiveClient
{
    Task SessionUpdated(SessionSummaryDto session);

    Task TaskUpdated(SessionTaskDto task);

    Task LogEntryUpserted(LogEntryDto entry);
}

public static class LiveGroups
{
    public const string Dashboard = "dashboard";

    public static string Session(Guid sessionId) => $"session:{sessionId}";
}
