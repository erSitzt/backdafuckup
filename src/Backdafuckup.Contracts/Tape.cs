namespace Backdafuckup.Contracts;

/// <summary>Where tape devices can be attached: this server (<c>local</c>) or a tape agent.</summary>
public sealed record TapeHostDto(string Id, string Name, bool Online);

/// <summary>A changer or drive found on a host.</summary>
/// <param name="Kind"><c>changer</c> or <c>drive</c>.</param>
/// <param name="Connection">How it is attached, for people: the iSCSI target, or the SCSI address.</param>
/// <param name="Library">The library it already belongs to, if any.</param>
public sealed record TapeDeviceDto(string Kind, string Serial, string Vendor, string Product, string Revision, string Path, string Connection, string? Library);

/// <summary>A drive element of a changer and the drive found for it.</summary>
/// <param name="MappedBy"><c>dvcid</c> (the changer names the drive), <c>order</c> (guessed: check it), or <c>none</c>.</param>
public sealed record TapeScanDriveDto(int Element, string? Serial, string MappedBy);

public sealed record TapeScanChangerDto(TapeDeviceDto Changer, int Slots, int MailSlots, IReadOnlyList<TapeScanDriveDto> Drives);

/// <summary>What a host has: changers with their drives, drives without a changer, and what could not be read.</summary>
public sealed record TapeScanDto(string Host, IReadOnlyList<TapeScanChangerDto> Changers, IReadOnlyList<TapeDeviceDto> StandaloneDrives, IReadOnlyList<string> Problems);

/// <param name="Element">The drive's element in the changer; null for a standalone drive.</param>
public sealed record TapeDriveMappingDto(string Serial, int? Element, string? Name);

/// <summary>Adds a library (a changer and its drives) or a standalone drive (no changer, one drive).</summary>
public sealed record CreateTapeLibraryDto(string Name, string Host, string? ChangerSerial, IReadOnlyList<TapeDriveMappingDto> Drives);

public sealed record RenameTapeLibraryDto(string Name);

/// <param name="Holder">What uses the drive now ("Load BDF000L09"), null when free.</param>
/// <param name="Waiting">What waits for it, in order.</param>
public sealed record TapeDriveDto(Guid Id, string Name, string Serial, string Vendor, string Product, int? Element, string? Barcode, string? Holder, DateTimeOffset? HolderSinceUtc, IReadOnlyList<string> Waiting);

public sealed record TapeLibraryDto(
    Guid Id,
    string Name,
    string Host,
    string HostName,
    string? ChangerSerial,
    string Vendor,
    string Product,
    int Slots,
    int MailSlots,
    int Media,
    int MailSlotsFull,
    DateTimeOffset? InventoryUtc,
    string? LastError,
    IReadOnlyList<TapeDriveDto> Drives);

/// <param name="Kind"><c>Data</c>, <c>Worm</c> or <c>Cleaning</c>.</param>
/// <param name="Location"><c>Slot</c>, <c>MailSlot</c>, <c>Drive</c> or <c>Offline</c>.</param>
/// <param name="Drive">The drive's name when it is in one.</param>
public sealed record TapeMediumDto(
    Guid Id,
    string Barcode,
    Guid? LibraryId,
    string? Library,
    string Kind,
    string Location,
    int? Address,
    string? Drive,
    int? HomeSlot,
    string? MediumSerial,
    long? CapacityBytes,
    long? RemainingBytes,
    DateTimeOffset SeenUtc,
    string State = "Unknown",
    string? Pool = null,
    Guid? SetId = null,
    int? Sequence = null,
    long BytesWritten = 0);

/// <summary>One element of a library for the slot map: a slot, mail slot or drive, with what is in it.</summary>
/// <param name="Type"><c>Slot</c>, <c>MailSlot</c> or <c>Drive</c>.</param>
public sealed record TapeElementDto(string Type, int Address, int Number, string? Barcode, string? MediumKind, Guid? DriveId, string? DriveName);

/// <summary>A library action, run as a tape session.</summary>
/// <param name="Action"><c>load</c>, <c>unload</c>, <c>import</c>, <c>export</c> or <c>scan</c> (the robot checks every element, then an inventory).</param>
/// <param name="Barcode">For load and export.</param>
/// <param name="DriveId">For load (optional: the first free drive) and unload.</param>
public sealed record TapeActionDto(string Action, string? Barcode = null, Guid? DriveId = null);

public static class TapeActions
{
    public const string Load = "load";
    public const string Unload = "unload";
    public const string Import = "import";
    public const string Export = "export";
    public const string Scan = "scan";

    /// <summary>Load a cartridge, read its label, set what the catalog knows of it (blank, free, which set, foreign).</summary>
    public const string Identify = "identify";

    public static readonly IReadOnlyList<string> All = [Load, Unload, Import, Export, Scan, Identify];
}

/// <summary>A tape agent for the console.</summary>
/// <param name="Libraries">Names of the libraries attached to it.</param>
public sealed record TapeAgentDto(
    Guid Id,
    string Name,
    bool Online,
    string? HostName,
    string? Version,
    DateTimeOffset? LastSeenUtc,
    string? Address,
    int Devices,
    IReadOnlyList<string> Libraries,
    DateTimeOffset CreatedUtc);

public sealed record CreateTapeAgentDto(string Name);

/// <summary>Shown once: the agent's settings file with its connect token.</summary>
/// <param name="EnvironmentFile">The content of /etc/backdafuckup-tape-agent/agent.env (mode 600, owner root).</param>
public sealed record TapeAgentSecretDto(TapeAgentDto Agent, string EnvironmentFile);


/// <summary>A tape key: only its fingerprint is ever shown.</summary>
/// <param name="Pools">Pools that encrypt with it.</param>
public sealed record TapeKeyDto(Guid Id, string Name, string Fingerprint, DateTimeOffset CreatedUtc, bool KeyFileDownloaded, IReadOnlyList<string> Pools);

/// <summary>A key from a passphrase (at least 12 characters); the passphrase is never stored.</summary>
public sealed record CreateTapeKeyDto(string Name, string Passphrase);

/// <param name="NewSet"><c>EveryRun</c>, <c>Daily</c>, <c>Weekly</c>, <c>Monthly</c> or <c>Never</c>.</param>
/// <param name="RetentionDays">How long a set is kept after it closed; null for ever.</param>
/// <param name="Encryption"><c>None</c>, <c>Software</c> or <c>Hardware</c>.</param>
/// <param name="Export"><c>Never</c>, <c>WhenSetCloses</c> or <c>AfterEveryRun</c>.</param>
public sealed record MediaPoolRequestDto(
    string Name,
    Guid LibraryId,
    string NewSet = "Weekly",
    string TimeZoneId = "UTC",
    int? RetentionDays = 28,
    bool WormOnly = false,
    string Encryption = "None",
    Guid? KeyId = null,
    string Export = "Never",
    int BlockSizeKiB = 256);

/// <param name="Media">Tapes in the pool's sets, by state.</param>
public sealed record MediaPoolDto(
    Guid Id,
    string Name,
    Guid LibraryId,
    string Library,
    string NewSet,
    string TimeZoneId,
    int? RetentionDays,
    bool WormOnly,
    string Encryption,
    Guid? KeyId,
    string? KeyFingerprint,
    string Export,
    int BlockSizeKiB,
    MediaSetDto? OpenSet,
    IReadOnlyDictionary<string, int> Media);

/// <param name="State"><c>Open</c>, <c>Closed</c>, <c>Expired</c> or <c>Overwritten</c>.</param>
public sealed record MediaSetDto(Guid Id, Guid PoolId, string Pool, string State, DateTimeOffset CreatedUtc, DateTimeOffset? ClosedUtc, DateTimeOffset? ExpiresUtc, int Media, long Bytes, int Copies, string? KeyFingerprint);

/// <param name="Mode"><c>NewestPerVm</c> or <c>EveryNew</c>.</param>
/// <param name="Schedule">As for backup jobs; kind <c>None</c> runs only on demand or after <paramref name="AfterJobId"/>.</param>
public sealed record TapeJobRequestDto(string Name, Guid PoolId, IReadOnlyList<Guid> SourceJobIds, string Mode = "NewestPerVm", bool IncludeSqlBackups = true, ScheduleDto? Schedule = null, Guid? AfterJobId = null, bool Enabled = true);

public sealed record TapeJobDto(
    Guid Id,
    string Name,
    bool Enabled,
    Guid PoolId,
    string Pool,
    IReadOnlyList<Guid> SourceJobIds,
    IReadOnlyList<string> SourceJobs,
    string Mode,
    bool IncludeSqlBackups,
    ScheduleDto Schedule,
    Guid? AfterJobId,
    DateTimeOffset? LastRunUtc,
    string? LastResult,
    DateTimeOffset? NextRunUtc);

/// <summary>A tape a copy needs, and where it is now.</summary>
public sealed record TapeNeededDto(int Sequence, string Barcode, string Location, string? Library);

/// <summary>A restore point on tape.</summary>
public sealed record TapeCopyDto(
    Guid Id,
    Guid OriginalId,
    string VmId,
    string VmName,
    string? JobName,
    string? ObjectKind,
    DateTimeOffset CreatedUtc,
    DateTimeOffset CopiedUtc,
    Guid SetId,
    string SetState,
    string Pool,
    long StoredBytes,
    bool Encrypted,
    IReadOnlyList<TapeNeededDto> Tapes);

public sealed record TapeRestoreRequestDto(Guid RepositoryId);

/// <summary>Marks a cartridge <c>Free</c> (written over on its next use) or <c>Damaged</c> (never used); its barcode typed to confirm.</summary>
public sealed record TapeMediumStateDto(string State, string Barcode);

