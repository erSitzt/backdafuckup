namespace Backdafuckup.Contracts;

public enum RestoreModeDto
{
    OriginalLocation,
    AlternateLocation,
}

/// <summary>Request to restore a whole VM from a restore point.</summary>
public sealed record RestoreRequestDto
{
    public required Guid RestorePointId { get; init; }

    public RestoreModeDto Mode { get; init; } = RestoreModeDto.AlternateLocation;

    /// <summary>Target pool (connection). Defaults to the connection the restore point came from.</summary>
    public Guid? TargetConnectionId { get; init; }

    public string? VmName { get; init; }

    public string? TargetHostId { get; init; }

    /// <summary>Target storage per disk key. Missing disks go to their original storage.</summary>
    public Dictionary<string, string> DiskStorage { get; init; } = [];

    /// <summary>Target network per NIC device.</summary>
    public Dictionary<string, string> NetworkMap { get; init; } = [];

    public bool RegenerateMacs { get; init; } = true;

    public bool PowerOn { get; init; }

    public bool ReplaceExisting { get; init; }

    public bool DeleteReplaced { get; init; }
}
