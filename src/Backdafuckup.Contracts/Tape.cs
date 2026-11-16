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
    DateTimeOffset SeenUtc);

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

    public static readonly IReadOnlyList<string> All = [Load, Unload, Import, Export, Scan];
}
