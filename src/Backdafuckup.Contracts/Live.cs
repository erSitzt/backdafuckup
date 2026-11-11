using System.Text.Json.Serialization;

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
    string? Summary,
    TransferMetricsDto? Metrics = null,
    string? ObjectKind = null);

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
    string? Error,
    TransferMetricsDto? Metrics = null);

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

/// <summary>
/// Transfer statistics of a disk, a VM (task) or a session. The categories Unchanged + Zero + Deduplicated + New
/// add up to Processed. Rates refer to <see cref="Seconds"/>: the transfer time of a disk or VM, the run time of a session.
/// </summary>
public sealed record TransferMetricsDto
{
    /// <summary>Provisioned size of all disks.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Part of the disks dealt with so far (read, or skipped as unchanged).</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>Bytes received from the source.</summary>
    public long ReadBytes { get; init; }

    /// <summary>Not read or not processed because change tracking reports no change.</summary>
    public long UnchangedBytes { get; init; }

    /// <summary>Empty (all-zero) data, not stored.</summary>
    public long ZeroBytes { get; init; }

    /// <summary>Data that already was in the repository.</summary>
    public long DedupedBytes { get; init; }

    /// <summary>New data before compression.</summary>
    public long NewDataBytes { get; init; }

    /// <summary>Written to the repository (after compression).</summary>
    public long StoredBytes { get; init; }

    public double Seconds { get; init; }

    /// <summary>Read rate over the last seconds (0 when nothing is transferred).</summary>
    public double CurrentBytesPerSecond { get; init; }

    /// <summary>Share of time the source kept the transfer busy (0..1).</summary>
    public double SourceLoad { get; init; }

    /// <summary>Share of time spent hashing and compressing (0..1).</summary>
    public double ProcessingLoad { get; init; }

    /// <summary>Share of time spent writing to the repository (0..1).</summary>
    public double TargetLoad { get; init; }

    /// <summary>Per-disk details (VM tasks only).</summary>
    public List<DiskMetricsDto>? Disks { get; init; }

    /// <summary>Processed bytes per second (Veeam-style processing rate).</summary>
    [JsonIgnore]
    public double ProcessingRate => Seconds > 0 ? ProcessedBytes / Seconds : 0;

    [JsonIgnore]
    public double ReadRate => Seconds > 0 ? ReadBytes / Seconds : 0;

    /// <summary>Non-empty data seen / new data (null without data, infinity if everything was already stored).</summary>
    [JsonIgnore]
    public double? DedupeRatio => DedupedBytes + NewDataBytes == 0 ? null
        : NewDataBytes == 0 ? double.PositiveInfinity
        : (DedupedBytes + NewDataBytes) / (double)NewDataBytes;

    /// <summary>New data / stored bytes.</summary>
    [JsonIgnore]
    public double? CompressionRatio => StoredBytes > 0 ? NewDataBytes / (double)StoredBytes : null;

    /// <summary>The busiest stage ("Source", "Processing", "Target"), or null before any data was moved.</summary>
    [JsonIgnore]
    public string? Bottleneck =>
        Math.Max(SourceLoad, Math.Max(ProcessingLoad, TargetLoad)) <= 0 ? null
        : SourceLoad >= ProcessingLoad && SourceLoad >= TargetLoad ? "Source"
        : ProcessingLoad >= TargetLoad ? "Processing"
        : "Target";

    /// <summary>Sums the parts; rates refer to <paramref name="seconds"/>; loads are weighted by the parts' transfer time.</summary>
    public static TransferMetricsDto Combine(IEnumerable<TransferMetricsDto?> parts, double seconds)
    {
        var list = parts.OfType<TransferMetricsDto>().ToList();
        var weight = list.Sum(p => p.Seconds);
        double Weighted(Func<TransferMetricsDto, double> load) => weight > 0 ? Math.Round(list.Sum(p => load(p) * p.Seconds) / weight, 3) : 0;
        return new TransferMetricsDto
        {
            TotalBytes = list.Sum(p => p.TotalBytes),
            ProcessedBytes = list.Sum(p => p.ProcessedBytes),
            ReadBytes = list.Sum(p => p.ReadBytes),
            UnchangedBytes = list.Sum(p => p.UnchangedBytes),
            ZeroBytes = list.Sum(p => p.ZeroBytes),
            DedupedBytes = list.Sum(p => p.DedupedBytes),
            NewDataBytes = list.Sum(p => p.NewDataBytes),
            StoredBytes = list.Sum(p => p.StoredBytes),
            Seconds = Math.Round(seconds, 3),
            CurrentBytesPerSecond = list.Sum(p => p.CurrentBytesPerSecond),
            SourceLoad = Weighted(p => p.SourceLoad),
            ProcessingLoad = Weighted(p => p.ProcessingLoad),
            TargetLoad = Weighted(p => p.TargetLoad),
        };
    }
}

/// <summary>Statistics of one disk of a VM backup.</summary>
public sealed record DiskMetricsDto
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public long CapacityBytes { get; init; }

    /// <summary>Pending, Running, Done or Failed.</summary>
    public string State { get; init; } = "Pending";

    /// <summary>Full or Cbt.</summary>
    public string? Mode { get; init; }

    public string? FullReason { get; init; }

    public string? Transport { get; init; }

    public TransferMetricsDto Metrics { get; init; } = new();
}
