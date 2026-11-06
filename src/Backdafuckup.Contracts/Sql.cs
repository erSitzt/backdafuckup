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
    int AutoUnpublishMinutes,
    Guid? SqlDatabaseId = null,
    DateTimeOffset? PointInTimeUtc = null);

/// <summary>
/// Restore a database of a restore point as a new database: the agent in the target VM writes its files
/// (<see cref="SqlRestoreFiles"/> names them after <see cref="DatabaseName"/>) and attaches them.
/// </summary>
/// <param name="FileLocation"><see cref="SqlRestoreFiles.Original"/>, <see cref="SqlRestoreFiles.Default"/> or <see cref="SqlRestoreFiles.Folder"/>.</param>
/// <param name="DataFolder">For <see cref="SqlRestoreFiles.Folder"/>: the folder of the data files.</param>
/// <param name="LogFolder">For <see cref="SqlRestoreFiles.Folder"/>: the folder of the log files (default: the data folder).</param>
/// <summary>
/// Restore a database of a restore point: as a new database (<see cref="DatabaseName"/> must be free), or with
/// <see cref="Overwrite"/> over the original database (same VM, instance and name; its files go back where they are,
/// the previous ones are deleted unless <see cref="KeepReplacedFiles"/>).
/// With <see cref="SqlDatabaseId"/> it is a restore to a point in time (<see cref="PointInTimeUtc"/>, null: the latest
/// backup): the server picks the base (a restore point's SQL Server snapshot or a native full) and the log backups;
/// <see cref="RestorePointId"/> is then ignored.
/// </summary>
public sealed record SqlRestoreRequestDto(
    Guid RestorePointId,
    string Instance,
    string Database,
    string TargetVmNativeId,
    string TargetInstance,
    Guid CredentialId,
    string DatabaseName,
    string FileLocation,
    string? DataFolder,
    string? LogFolder,
    bool Overwrite = false,
    bool KeepReplacedFiles = false,
    Guid? SqlDatabaseId = null,
    DateTimeOffset? PointInTimeUtc = null);

/// <summary>
/// Export a database of a restore point as a .bak: it is published on <see cref="TargetInstance"/> (a staging instance,
/// same or newer version), backed up copy-only with checksums into <see cref="Folder"/> in that VM (null: the instance's
/// backup folder), verified, and unpublished. With <see cref="Download"/> the file can also be downloaded from the
/// console for 24 hours.
/// </summary>
public sealed record SqlExportRequestDto(
    Guid RestorePointId,
    string Instance,
    string Database,
    string TargetVmNativeId,
    string TargetInstance,
    Guid CredentialId,
    string? Folder,
    bool Download);

/// <summary>The .bak of an export session kept on the server for download.</summary>
public sealed record SqlExportFileDto(string FileName, long SizeBytes, DateTimeOffset ExpiresUtc);

/// <summary>Where the files of a restored database go and how they are named (shared by the console and the server).</summary>
public static class SqlRestoreFiles
{
    /// <summary>The folders the files had in the backup.</summary>
    public const string Original = "original";

    /// <summary>The target instance's default data and log folders.</summary>
    public const string Default = "default";

    /// <summary>Folders chosen in the wizard.</summary>
    public const string Folder = "folder";

    /// <summary>A file name stem from a database name: characters Windows does not allow in file names become "_".</summary>
    public static string Stem(string database)
    {
        var invalid = "<>:\"/\\|?*";
        var stem = new string([.. database.Select(c => c < 32 || invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        return stem.Length == 0 ? "database" : stem;
    }

    /// <summary>
    /// The target path of each file, in the given order (primary data file first): <c>name.mdf</c>,
    /// <c>name_2.ndf</c>, … for data files and <c>name_log.ldf</c>, <c>name_log_2.ldf</c>, … for log files.
    /// </summary>
    /// <param name="files">Path in the backup (e.g. <c>D:\SQL\shop.mdf</c>) and kind ("data" or "log") of each file.</param>
    /// <exception cref="ArgumentException">A folder is missing or not an absolute local path.</exception>
    public static IReadOnlyList<string> TargetPaths(
        string database, IReadOnlyList<(string Path, string Kind)> files, string location, string? dataFolder, string? logFolder, string? defaultData, string? defaultLog)
    {
        var stem = Stem(database);
        var paths = new List<string>();
        int data = 0, logs = 0;
        foreach (var (path, kind) in files)
        {
            var isLog = kind == "log";
            var name = isLog
                ? (++logs == 1 ? $"{stem}_log.ldf" : $"{stem}_log_{logs}.ldf")
                : (++data == 1 ? $"{stem}.mdf" : $"{stem}_{data}.ndf");
            var folder = location switch
            {
                Original => path[..Math.Max(0, path.LastIndexOf('\\'))],
                Default => isLog ? defaultLog ?? defaultData : defaultData,
                Folder => isLog ? (string.IsNullOrWhiteSpace(logFolder) ? dataFolder : logFolder) : dataFolder,
                _ => throw new ArgumentException($"Unknown file location '{location}'."),
            };
            if (string.IsNullOrWhiteSpace(folder))
            {
                throw new ArgumentException(location == Default
                    ? "The instance names no default folder for its databases; choose a folder."
                    : $"No folder for the {(isLog ? "log" : "data")} files.");
            }

            folder = folder.Trim().TrimEnd('\\');
            if (folder.Length < 2 || !char.IsAsciiLetter(folder[0]) || folder[1] != ':' || (folder.Length > 2 && folder[2] != '\\') || folder.Contains("\\..", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{folder} is not an absolute local folder (like D:\\SQL\\Data).");
            }

            paths.Add(folder + "\\" + name);
        }

        return paths;
    }
}

// ---- SQL catalog (log chain, point-in-time restore) ----

/// <summary>A "SQL Server backups" day session: what its runs backed up, each VM's schedule and the databases of its VMs.
/// <c>Current</c>: the newest day session of the job, the one the schedule and the RPO belong to.</summary>
public sealed record SqlBackupDayDto(
    int LogBackups,
    int FullBackups,
    int SystemFullBackups,
    long Bytes,
    long NewStoredBytes,
    int LogIntervalMinutes,
    bool Current,
    IReadOnlyList<SqlBackupDayVmDto> Vms,
    IReadOnlyList<SqlCatalogDatabaseDto> Databases)
{
    /// <summary>The runs of the day, newest first: the VMs that started in the same minute form one run.</summary>
    public IReadOnlyList<SqlBackupRunDto> Runs { get; init; } = [];
}

/// <summary>A run of a day session: its VMs (in parallel) and their totals.</summary>
public sealed record SqlBackupRunDto(
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string State,
    int LogBackups,
    int FullBackups,
    long Bytes,
    long NewStoredBytes,
    IReadOnlyList<SqlBackupRunVmDto> Vms);

/// <summary>One VM in a run; its lines come from <c>GET /api/sql/backup-runs/{Id}/lines</c>.</summary>
public sealed record SqlBackupRunVmDto(Guid Id, string VmName, string State, int LogBackups, int FullBackups, long Bytes, long NewStoredBytes, string? Message);

/// <summary>A line of a VM's run.</summary>
public sealed record SqlBackupRunLineDto(string Severity, string Message);

/// <summary>One VM of a day session: what it backed up that day, its last result and its next runs.</summary>
public sealed record SqlBackupDayVmDto(
    string VmNativeId,
    string VmName,
    int LogBackups,
    int FullBackups,
    long Bytes,
    DateTimeOffset? LastRunUtc,
    string? LastResult,
    string? LastMessage,
    DateTimeOffset? NextLogUtc,
    DateTimeOffset? NextFullUtc,
    DateTimeOffset? NextSystemUtc);

/// <summary>A database of a backed-up SQL Server instance with the state of its log chain.</summary>
public sealed record SqlCatalogDatabaseDto(
    Guid Id,
    string VmNativeId,
    string VmName,
    string Instance,
    string Name,
    string? RecoveryModel,
    string? ProductVersion,
    string ChainState,
    string? ChainReason,
    DateTimeOffset? LastLogUtc,
    DateTimeOffset? LastFullUtc,
    DateTimeOffset? CoveredFromUtc,
    DateTimeOffset? CoveredToUtc,
    Guid? JobId);

/// <summary>A span of time the database can be restored to, from one base on.</summary>
public sealed record SqlCoverageSpanDto(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string BaseKind, int Logs);

/// <summary>Time not covered between two spans, and why.</summary>
public sealed record SqlCoverageGapDto(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Reason);

public sealed record SqlCatalogDatabaseDetailDto(
    SqlCatalogDatabaseDto Database, IReadOnlyList<SqlCoverageSpanDto> Spans, IReadOnlyList<SqlCoverageGapDto> Gaps, IReadOnlyList<SqlBackupPointDto> Points)
{
    /// <summary>The native full and system database backups the agent took, newest first: downloadable as .bak.</summary>
    public IReadOnlyList<SqlBackupFileDto> Fulls { get; init; } = [];
}

/// <summary>A .bak in the repository: kind "NativeFull" or "SystemFull", when it finished, its file name and length.</summary>
public sealed record SqlBackupFileDto(Guid Id, string Kind, DateTimeOffset FinishUtc, string FileName, long Bytes);

/// <summary>The end of a backup within the restorable time (a natural point to restore to): kind "SnapshotFull", "NativeFull" or "Log".</summary>
public sealed record SqlBackupPointDto(DateTimeOffset FinishUtc, string Kind, long Bytes);

/// <summary>A file of the database in the base of a restore: its path in the backup and its kind ("data" or "log").</summary>
public sealed record SqlPlanFileDto(string Path, string Kind, long? SizeBytes);

/// <summary>What a restore to a point in time applies: the base, the log backups and STOPAT.</summary>
public sealed record SqlRestorePlanDto(
    string BaseKind,
    DateTimeOffset BaseUtc,
    Guid? RestorePointId,
    long BaseBytes,
    int Logs,
    long LogBytes,
    string? StopAtLocal,
    DateTimeOffset ReachedUtc,
    IReadOnlyList<SqlPlanFileDto> Files);
