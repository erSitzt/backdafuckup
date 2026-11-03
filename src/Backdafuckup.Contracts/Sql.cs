namespace Backdafuckup.Contracts;

/// <summary>A stored SQL Server login (the password is never returned).</summary>
public sealed record SqlCredentialDto(Guid Id, string UserName, string? Description, DateTimeOffset CreatedUtc);

public sealed record SaveSqlCredentialDto(string UserName, string Password, string? Description);

/// <summary>
/// What the agent in a VM finds: SQL Server instances, and with an account (a Windows credential or a SQL login) what
/// that account sees there.
/// </summary>
public sealed record SqlInventoryRequestDto(string VmNativeId, Guid? CredentialId, string? Instance);

public sealed record SqlTargetDto(
    string Host,
    string? Address,
    string? Account,
    IReadOnlyList<SqlTargetInstanceDto> Instances,
    IReadOnlyList<SqlTargetVolumeDto> Volumes);

public sealed record SqlTargetInstanceDto(
    string Name,
    string? InstanceId,
    string? SetupVersion,
    string? Product,
    string? SetupEdition,
    string? ServiceName,
    string? ServiceState,
    string? ServiceAccount,
    string? DefaultDataFolder,
    string? DefaultLogFolder,
    string? BackupFolder,
    bool Connected,
    string? ConnectError,
    string? ProductVersion,
    string? Edition,
    int? EngineEdition,
    string? LoginName,
    bool? IsSysadmin,
    bool? IsDbCreator,
    IReadOnlyList<SqlTargetDatabaseDto> Databases);

public sealed record SqlTargetDatabaseDto(string Name, string State, string? RecoveryModel, bool ReadOnly, IReadOnlyList<SqlTargetFileDto> Files);

public sealed record SqlTargetFileDto(string LogicalName, string PhysicalName, string Type, long SizeBytes);

public sealed record SqlTargetVolumeDto(string Root, string? Label, long FreeBytes, long TotalBytes);

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

/// <summary>
/// Publish a database of a restore point: the agent in the target VM mounts the backup's volumes (reads come from the
/// backup, writes go to a temporary overlay) and attaches the database, until the session is stopped or
/// <see cref="AutoUnpublishMinutes"/> have passed.
/// </summary>
/// <param name="Instance">The database's instance in the backup.</param>
/// <param name="Database">The database's name in the backup.</param>
/// <param name="CredentialId">A Windows credential or a SQL Server login that may create databases on the target instance.</param>
/// <param name="DatabaseName">The name to attach the database as; it must not exist on the target instance.</param>
public sealed record SqlPublishRequestDto(
    Guid RestorePointId,
    string Instance,
    string Database,
    string TargetVmNativeId,
    string TargetInstance,
    Guid CredentialId,
    string DatabaseName,
    int AutoUnpublishMinutes);
