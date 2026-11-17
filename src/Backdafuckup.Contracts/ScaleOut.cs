namespace Backdafuckup.Contracts;

/// <summary>
/// Adds an extent (a directory on another volume) to a scale-out repository. On a standalone repository it converts it:
/// the repository becomes the first extent of a new scale-out repository named <see cref="ScaleOutName"/>, its jobs then
/// write to the scale-out, and its chains stay where they are.
/// </summary>
public sealed record AddExtentDto
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    /// <summary>Required when converting a standalone repository.</summary>
    public string? ScaleOutName { get; init; }

    /// <summary>Size of the pack files of a new directory, 16–512 MiB.</summary>
    public int PackSizeMiB { get; init; } = 64;
}

/// <summary>Seal or unseal an extent, switch maintenance on or off; null leaves it as it is.</summary>
public sealed record ExtentStateDto(bool? Sealed = null, bool? Maintenance = null);

/// <summary>Placement and balancing settings of a scale-out repository.</summary>
public sealed record ScaleOutSettingsDto
{
    public int NewChainMinFreePercent { get; init; } = 5;

    public bool FullWhenExtentUnavailable { get; init; } = true;

    public bool AutoRebalance { get; init; }

    public IReadOnlyList<DayOfWeek> WindowDays { get; init; } = [.. Enum.GetValues<DayOfWeek>()];

    /// <summary>"HH:mm", local to <see cref="TimeZoneId"/>.</summary>
    public string WindowStart { get; init; } = "22:00";

    /// <summary>"HH:mm"; before the start means the next day.</summary>
    public string WindowEnd { get; init; } = "06:00";

    public string TimeZoneId { get; init; } = "UTC";

    public int LowFreePercent { get; init; } = 10;

    public int ImbalancePercent { get; init; } = 15;

    public long? MaxBytesPerRun { get; init; }

    public int CooldownDays { get; init; } = 7;

    public int? MaxMoveMBps { get; init; }

    public int MoveSwitchWaitMinutes { get; init; } = 360;
}

/// <summary>A chain of a scale-out repository: one job's restore points of one object, and where they are.</summary>
public sealed record ChainDto(
    Guid? JobId,
    string? JobName,
    string ObjectNativeId,
    string ObjectName,
    Guid? PlacedExtentId,
    string? PlacedExtentName,
    string? Reason,
    DateTimeOffset? PlacedUtc,
    IReadOnlyList<ChainExtentDto> Extents,
    DateTimeOffset? NewestUtc,
    string? Busy);

/// <summary>What of a chain is on one extent: restore points, SQL Server backups and their size (≈, new data of each).</summary>
public sealed record ChainExtentDto(Guid ExtentId, string ExtentName, int RestorePoints, int SqlBackups, long Bytes);

public sealed record ChainPageDto(IReadOnlyList<ChainDto> Items, int Total);

/// <summary>One chain to move: from where to where (null: the extent with the most room).</summary>
public sealed record ChainMoveDto(Guid? JobId, string ObjectNativeId, Guid FromExtentId, Guid? ToExtentId);

/// <summary>Moves chains now: by hand, or a rebalance plan shown before (<see cref="Rebalance"/>).</summary>
public sealed record StartMovesDto(IReadOnlyList<ChainMoveDto> Chains, bool Rebalance = false);

/// <summary>A rebalance plan: the moves and each active extent's fill before and after (expected).</summary>
public sealed record RebalancePlanDto(IReadOnlyList<PlannedMoveDto> Moves, IReadOnlyList<ExtentFillDto> Extents, long Bytes, string? Note);

public sealed record PlannedMoveDto(Guid? JobId, string? JobName, string ObjectNativeId, string ObjectName, Guid FromExtentId, string FromExtentName, Guid ToExtentId, string ToExtentName, long Bytes);

public sealed record ExtentFillDto(Guid ExtentId, string Name, long TotalBytes, long FreeBefore, long FreeAfter);
