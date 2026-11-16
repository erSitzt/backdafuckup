namespace Backdafuckup.Server.Data;

/// <summary>A tape library (a changer with its drives), or a standalone drive without a changer.</summary>
public sealed class TapeLibraryEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    /// <summary>Where the devices are attached: <see cref="TapeHostIds.Local"/>, or a tape agent's id.</summary>
    public string Host { get; set; } = TapeHostIds.Local;

    /// <summary>Serial number of the changer; null for a standalone drive.</summary>
    public string? ChangerSerial { get; set; }

    public string Vendor { get; set; } = string.Empty;

    public string Product { get; set; } = string.Empty;

    /// <summary>Element addresses from the changer (contiguous per type, SMC-3 element address assignment page).</summary>
    public int FirstSlot { get; set; }

    public int Slots { get; set; }

    public int FirstMailSlot { get; set; }

    public int MailSlots { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? InventoryUtc { get; set; }

    /// <summary>What went wrong at the last inventory or action, or what it found odd (cartridges without a barcode).</summary>
    public string? LastError { get; set; }

    public List<TapeDriveEntity> Drives { get; set; } = [];

    public uint Version { get; set; }
}

public static class TapeHostIds
{
    /// <summary>The devices are attached to the backup server itself.</summary>
    public const string Local = "local";

    /// <summary>The host id of a library behind a tape agent: the agent's id.</summary>
    public static string Agent(Guid agentId) => agentId.ToString("D");
}

/// <summary>A tape agent: a program on another Linux host that forwards the SCSI commands of its tape devices.</summary>
public sealed class TapeAgentEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    /// <summary>The connect token, protected with the server's key ring; compared in fixed time.</summary>
    public required string ProtectedToken { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    public string? HostName { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>Where it connected from last (address:port).</summary>
    public string? LastAddress { get; set; }
}

public sealed class TapeDriveEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid LibraryId { get; set; }

    public required string Name { get; set; }

    public required string Serial { get; set; }

    public string Vendor { get; set; } = string.Empty;

    public string Product { get; set; } = string.Empty;

    /// <summary>The drive's element address in its changer (null for a standalone drive).</summary>
    public int? ElementAddress { get; set; }

    /// <summary>Barcode of the cartridge in the drive at the last inventory or load.</summary>
    public string? Barcode { get; set; }
}

public enum TapeMediumKind
{
    Data,
    Worm,
    Cleaning,
}

public enum TapeLocation
{
    Slot,
    MailSlot,
    Drive,

    /// <summary>Not in a library (exported, taken out, or not found at the last inventory).</summary>
    Offline,
}

/// <summary>A cartridge the product has seen in one of its libraries, by its barcode.</summary>
public sealed class TapeMediumEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Barcode { get; set; }

    /// <summary>The library it is in, or was last seen in.</summary>
    public Guid? LibraryId { get; set; }

    public TapeMediumKind Kind { get; set; }

    public TapeLocation Location { get; set; }

    /// <summary>Element address of its slot, mail slot or drive.</summary>
    public int? Address { get; set; }

    /// <summary>The slot it came from, so that it goes back there after a drive.</summary>
    public int? HomeSlot { get; set; }

    /// <summary>From the cartridge memory (MAM), once it was in a drive.</summary>
    public string? MediumSerial { get; set; }

    public long? CapacityBytes { get; set; }

    public long? RemainingBytes { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset SeenUtc { get; set; }
}
