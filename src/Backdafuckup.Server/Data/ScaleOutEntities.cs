namespace Backdafuckup.Server.Data;

/// <summary>Placement and balancing settings of a scale-out repository (stored as JSON on its row).</summary>
public sealed class ScaleOutSettings
{
    /// <summary>New chains go only to extents with at least this much free space (percent of the volume).</summary>
    public int NewChainMinFreePercent { get; set; } = 5;

    /// <summary>
    /// A chain whose extent is in maintenance or offline starts anew with a full backup on another extent; false: the
    /// VM is skipped with a warning until its extent is back.
    /// </summary>
    public bool FullWhenExtentUnavailable { get; set; } = true;

    /// <summary>Moves chains by itself in <see cref="Window"/> when an extent runs low or the extents are too unequal.</summary>
    public bool AutoRebalance { get; set; }

    public ScaleOutWindow Window { get; set; } = new();

    /// <summary>An active extent with less free space than this (percent) triggers an automatic rebalance.</summary>
    public int LowFreePercent { get; set; } = 10;

    /// <summary>A spread of the used percentage between active extents above this triggers an automatic rebalance.</summary>
    public int ImbalancePercent { get; set; } = 15;

    /// <summary>At most this many bytes are moved per rebalance run; null: no limit.</summary>
    public long? MaxBytesPerRun { get; set; }

    /// <summary>A chain moved within this many days is not moved again by a rebalance.</summary>
    public int CooldownDays { get; set; } = 7;

    /// <summary>Moves read at most this many MB/s; null: no limit.</summary>
    public int? MaxMoveMBps { get; set; }

    /// <summary>How long a move waits for a running backup of the chain before it leaves the chain for its next run.</summary>
    public int MoveSwitchWaitMinutes { get; set; } = 360;
}

/// <summary>When automatic rebalancing may run: on these days, from <see cref="Start"/> to <see cref="End"/> (may cross midnight).</summary>
public sealed class ScaleOutWindow
{
    public List<DayOfWeek> Days { get; set; } = [.. Enum.GetValues<DayOfWeek>()];

    /// <summary>Local time "HH:mm".</summary>
    public string Start { get; set; } = "22:00";

    /// <summary>Local time "HH:mm"; before <see cref="Start"/> means the next day.</summary>
    public string End { get; set; } = "06:00";

    public string TimeZoneId { get; set; } = "UTC";
}

/// <summary>
/// Where a chain's new data goes: one job's restore points of one object (VM, namespace, cluster scope) and that VM's
/// SQL Server backups. The restore points themselves say where existing data is.
/// </summary>
public sealed class ChainPlacementEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ScaleOutId { get; set; }

    public Guid JobId { get; set; }

    /// <summary>The VM's native id, the namespace name, or the cluster scope member.</summary>
    public required string ObjectNativeId { get; set; }

    public Guid ExtentId { get; set; }

    public DateTimeOffset PlacedUtc { get; set; }

    /// <summary>Why it is there: "new", "adopted", "moved", "replaced: …".</summary>
    public required string Reason { get; set; }

    /// <summary>The move session working on the chain, if any.</summary>
    public Guid? MoveSessionId { get; set; }

    public long? EstimateBytes { get; set; }
}

public enum MoveItemType
{
    RestorePoint,
    SqlBackup,
}

public enum MoveItemState
{
    /// <summary>Copied to the target and committed there; the catalog still names the source.</summary>
    Copied,

    /// <summary>The catalog names the target; the source copy is still to be deleted.</summary>
    Switched,

    SourceDeleted,

    /// <summary>The catalog row changed meanwhile (deleted by retention); the target copy is still to be deleted.</summary>
    Abandoned,

    TargetDeleted,

    Failed,
}

/// <summary>The journal of a move: one row per copied restore point or SQL Server backup.</summary>
public sealed class RepositoryMoveItemEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    public Guid ScaleOutId { get; set; }

    public Guid? JobId { get; set; }

    public required string ObjectNativeId { get; set; }

    public MoveItemType ObjectType { get; set; }

    public Guid ObjectId { get; set; }

    public Guid SourceExtentId { get; set; }

    public Guid TargetExtentId { get; set; }

    public MoveItemState State { get; set; }

    public long BytesWritten { get; set; }

    /// <summary>Where the copy's manifest is on the target (for the catalog row after the switch).</summary>
    public string? TargetPath { get; set; }

    public string? Error { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
