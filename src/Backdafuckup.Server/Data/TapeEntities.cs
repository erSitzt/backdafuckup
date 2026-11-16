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

    /// <summary>What the product knows of its content (from its label, or from writing it).</summary>
    public TapeMediumState State { get; set; } = TapeMediumState.Unknown;

    /// <summary>The id in its label.</summary>
    public Guid? MediumId { get; set; }

    /// <summary>The name in its label (the barcode, or what to write on a cartridge without one).</summary>
    public string? LabelName { get; set; }

    public Guid? PoolId { get; set; }

    public Guid? SetId { get; set; }

    /// <summary>Its number in its set, from 1.</summary>
    public int? Sequence { get; set; }

    /// <summary>Files on it, the label counted: the number the next file gets.</summary>
    public int FileCount { get; set; }

    /// <summary>Bytes written to it (blocks with their headers).</summary>
    public long BytesWritten { get; set; }

    public DateTimeOffset? LabelledUtc { get; set; }

    public DateTimeOffset? LastWrittenUtc { get; set; }
}

public enum TapeMediumState
{
    /// <summary>Not read yet.</summary>
    Unknown,

    /// <summary>Nothing on it: usable.</summary>
    Blank,

    /// <summary>Labelled by backdafuckup, in no set: usable.</summary>
    Free,

    /// <summary>In an open set, room left: the next run appends to it.</summary>
    Appendable,

    /// <summary>In a set, nothing more is written to it (full, or its set closed).</summary>
    Full,

    /// <summary>Its set expired: it can be labelled again (never a WORM tape).</summary>
    Expired,

    Damaged,

    /// <summary>Data that is not backdafuckup's: never written to.</summary>
    Foreign,
}

/// <summary>A tape key: the key-encryption key from a passphrase (the passphrase itself is never stored).</summary>
public sealed class TapeKeyEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public required string Fingerprint { get; set; }

    /// <summary>The KEK (base64), protected with the server's key ring.</summary>
    public required string ProtectedKek { get; set; }

    public int MemoryKiB { get; set; }

    public int Iterations { get; set; }

    public int Parallelism { get; set; }

    public required string Salt { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Whether the key file was downloaded (it is offered once).</summary>
    public bool KeyFileDownloaded { get; set; }
}

public enum TapeNewSet
{
    EveryRun,
    Daily,
    Weekly,
    Monthly,
    Never,
}

public enum TapeEncryption
{
    None,

    /// <summary>AES-256-GCM per record, by the server (passphrase key).</summary>
    Software,

    /// <summary>LTO drive encryption (application-managed), the key from the passphrase key.</summary>
    Hardware,
}

public enum TapeExport
{
    Never,
    WhenSetCloses,
    AfterEveryRun,
}

/// <summary>Tapes of one library used for the same purpose, with the rules for sets, retention and encryption.</summary>
public sealed class MediaPoolEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public Guid LibraryId { get; set; }

    public int BlockSize { get; set; } = 256 * 1024;

    public TapeNewSet NewSet { get; set; } = TapeNewSet.Weekly;

    /// <summary>For daily, weekly and monthly sets: where days and weeks begin.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>How long a set is kept after it closed; null keeps it for ever.</summary>
    public int? RetentionDays { get; set; } = 28;

    public bool WormOnly { get; set; }

    public TapeEncryption Encryption { get; set; }

    public Guid? KeyId { get; set; }

    public TapeExport Export { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

public enum MediaSetState
{
    Open,
    Closed,
    Expired,

    /// <summary>Its tapes were labelled again: nothing of it is left.</summary>
    Overwritten,
}

/// <summary>Tapes written together; chunks are stored once per set, so a restore needs only tapes of one set.</summary>
public sealed class MediaSetEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid PoolId { get; set; }

    public MediaSetState State { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? ClosedUtc { get; set; }

    public DateTimeOffset? ExpiresUtc { get; set; }

    public string? KeyFingerprint { get; set; }

    /// <summary>The committed segments of its chunk index (only for an open set).</summary>
    public List<string>? IndexSegments { get; set; }

    /// <summary>Tapes in it.</summary>
    public int Media { get; set; }

    public long Bytes { get; set; }
}

public enum TapeJobMode
{
    /// <summary>The newest restore point of every VM that the open set does not have yet.</summary>
    NewestPerVm,

    /// <summary>Every restore point the open set does not have yet.</summary>
    EveryNew,
}

/// <summary>A job that copies restore points of backup jobs to tape.</summary>
public sealed class TapeJobEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public Guid PoolId { get; set; }

    /// <summary>Backup jobs whose restore points it copies.</summary>
    public List<Guid> SourceJobIds { get; set; } = [];

    public TapeJobMode Mode { get; set; }

    public bool IncludeSqlBackups { get; set; } = true;

    public JobSchedule Schedule { get; set; } = new();

    /// <summary>Runs after this backup job (Succeeded or Warning) instead of, or besides, the schedule.</summary>
    public Guid? AfterJobId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    public SessionResult? LastResult { get; set; }

    /// <summary>When the schedule runs it next (set by the scheduler).</summary>
    public DateTimeOffset? NextRunUtc { get; set; }
}

/// <summary>A restore point (or SQL backup object) on tape: where its file is and which tapes its chunks need.</summary>
public sealed class TapeCopyEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SetId { get; set; }

    public Guid PoolId { get; set; }

    /// <summary><c>restore-point</c> or <c>sql</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The restore point or SQL backup copied (no foreign key: it may be deleted from disk later).</summary>
    public Guid OriginalId { get; set; }

    public Guid RepositoryId { get; set; }

    public string? JobName { get; set; }

    public required string VmId { get; set; }

    public required string VmName { get; set; }

    public string? ObjectKind { get; set; }

    /// <summary>When the original restore point was taken.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset CopiedUtc { get; set; }

    public Guid? SessionId { get; set; }

    /// <summary>Its restore-point file: the tape (sequence), the file number and its first block.</summary>
    public int Sequence { get; set; }

    public int FileNumber { get; set; }

    public long FileStartBlock { get; set; }

    public ulong FileId { get; set; }

    public List<int> NeededSequences { get; set; } = [];

    public long StoredBytes { get; set; }

    /// <summary>The original manifest (JSON).</summary>
    public required string Manifest { get; set; }
}

/// <summary>A file on a tape of a set: archive, restore point, SQL or catalog.</summary>
public sealed class TapeFileEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SetId { get; set; }

    public int Sequence { get; set; }

    public int FileNumber { get; set; }

    public long StartBlock { get; set; }

    public ulong FileId { get; set; }

    public required string Type { get; set; }

    public long Blocks { get; set; }

    public long Bytes { get; set; }

    public Guid? CopyId { get; set; }
}
