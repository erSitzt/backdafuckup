namespace Backdafuckup.Contracts;

/// <summary>Download the files of one database as a ZIP (with an attach script).</summary>
public sealed record SqlDownloadRequestDto(string Instance, string Database, string? TimeZone);

/// <summary>The SQL Server instances and databases of a restore point opened in a file browser.</summary>
public sealed record SqlMountDto(
    IReadOnlyList<SqlInstanceDto> Instances,
    IReadOnlyList<SqlDatabaseDto> Databases,
    IReadOnlyList<string> Warnings);

/// <summary>A SQL Server instance as the backup's registry (SOFTWARE and SYSTEM hives) describes it.</summary>
public sealed record SqlInstanceDto(
    string Name,
    string? InstanceId,
    string? Version,
    string? Product,
    string? Edition,
    string? ServiceName,
    string? ServiceAccount,
    string? DefaultDataFolder,
    string? DefaultLogFolder,
    string? BackupFolder);

/// <summary>A database with its files in the backup and the reasons it cannot be restored, if any.</summary>
public sealed record SqlDatabaseDto(
    string Instance,
    string Name,
    string Consistency,
    string? ConsistencyReason,
    bool Restorable,
    IReadOnlyList<string> Problems,
    IReadOnlyList<SqlFileDto> Files,
    long TotalBytes);

/// <param name="Kind">"data", "log" or "other".</param>
/// <param name="Volume">The browser's volume id holding the file, if it was found.</param>
/// <param name="GuestPath">The file's path within that volume as the file browser writes it (e.g. "/SQL/shop.mdf"), if it was found.</param>
public sealed record SqlFileDto(
    string Path,
    string Kind,
    bool Primary,
    long? SizeBytes,
    string? Volume,
    string? GuestPath,
    string? Problem);
