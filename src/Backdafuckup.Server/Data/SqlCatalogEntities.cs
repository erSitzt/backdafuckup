using Backdafuckup.Core.Repository;

namespace Backdafuckup.Server.Data;

public enum SqlChainState
{
    /// <summary>Nothing known yet (no backup set recorded by us).</summary>
    Unknown,

    /// <summary>A base and an unbroken chain of log backups since then.</summary>
    Ok,

    /// <summary>FULL or BULK_LOGGED without a base we hold: a native full is needed first.</summary>
    NoBase,

    /// <summary>The chain broke (gap, foreign log backup, new recovery fork); a native full restarts it.</summary>
    Broken,

    /// <summary>SIMPLE recovery: only the restore points and native fulls themselves can be restored.</summary>
    Simple,
}

/// <summary>
/// A database of a SQL Server instance in a backed-up VM, the key of its backup chain. Identified by VM, instance, name
/// and <c>family_guid</c> (which every restored copy keeps; <c>database_guid</c> changes with each restore).
/// </summary>
public sealed class SqlDatabaseEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string PlatformId { get; set; }

    public required string VmNativeId { get; set; }

    public required string VmName { get; set; }

    /// <summary>The instance name (<c>MSSQLSERVER</c> for the default instance).</summary>
    public required string Instance { get; set; }

    public required string Name { get; set; }

    /// <summary><c>msdb.dbo.backupset.family_guid</c>; <see cref="Guid.Empty"/> while only restore points of older versions know the database.</summary>
    public Guid FamilyGuid { get; set; }

    /// <summary>The <c>database_guid</c> seen last (it changes when the database is restored).</summary>
    public Guid? DatabaseGuid { get; set; }

    /// <summary>The recovery fork of the last backup set (<c>last_recovery_fork_guid</c>).</summary>
    public Guid? RecoveryForkGuid { get; set; }

    public string? RecoveryModel { get; set; }

    public string? ProductVersion { get; set; }

    public Guid? JobId { get; set; }

    public SqlChainState ChainState { get; set; }

    /// <summary>Why the chain is not <see cref="SqlChainState.Ok"/> (named with time, device, LSNs).</summary>
    public string? ChainReason { get; set; }

    public DateTimeOffset? ChainChangedUtc { get; set; }

    public DateTimeOffset? LastLogUtc { get; set; }

    public DateTimeOffset? LastFullUtc { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}

public enum SqlBackupSetKind
{
    /// <summary>The T-SQL snapshot backup of an image backup (restore point plus its <c>.bkm</c>).</summary>
    SnapshotFull,

    /// <summary><c>BACKUP DATABASE</c> by the agent, in a SQL backup object.</summary>
    NativeFull,

    /// <summary><c>BACKUP LOG</c> by the agent, in a SQL backup object.</summary>
    Log,

    /// <summary>A full backup of <c>master</c>, <c>msdb</c> or <c>model</c>.</summary>
    SystemFull,
}

public enum SqlBackupState
{
    Available,
    Deleting,
}

/// <summary>A SQL backup object in a repository (one agent run for one instance), see <see cref="SqlBackupManifest"/>.</summary>
public sealed class SqlBackupEntity
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }

    public Guid? JobId { get; set; }

    public Guid? SessionId { get; set; }

    public required string PlatformId { get; set; }

    public required string VmNativeId { get; set; }

    public required string VmName { get; set; }

    public required string Instance { get; set; }

    public SqlBackupKind Kind { get; set; }

    public SqlBackupState State { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public string? ManifestPath { get; set; }

    public int FileCount { get; set; }

    public long Bytes { get; set; }

    public long NewStoredBytes { get; set; }
}

/// <summary>
/// One backup set of a database: a snapshot full (in a restore point), or a native full or log backup (a file of a
/// SQL backup object). The restore planner works on these rows alone.
/// </summary>
public sealed class SqlBackupSetEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SqlDatabaseId { get; set; }

    public SqlBackupSetKind Kind { get; set; }

    public Guid RepositoryId { get; set; }

    /// <summary>The SQL backup object holding the file (native fulls and logs).</summary>
    public Guid? SqlBackupId { get; set; }

    public string? FileKey { get; set; }

    public string? FileName { get; set; }

    /// <summary>The restore point holding the data files and the <c>.bkm</c> (snapshot fulls).</summary>
    public Guid? RestorePointId { get; set; }

    public Guid BackupSetUuid { get; set; }

    public decimal FirstLsn { get; set; }

    public decimal LastLsn { get; set; }

    public decimal CheckpointLsn { get; set; }

    public decimal? DatabaseBackupLsn { get; set; }

    public Guid? DatabaseGuid { get; set; }

    public Guid? FirstRecoveryForkGuid { get; set; }

    public Guid? LastRecoveryForkGuid { get; set; }

    public decimal? ForkPointLsn { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset FinishUtc { get; set; }

    /// <summary>SQL Server's offset from UTC at backup time in minutes; null when unknown (STOPAT then cannot be converted).</summary>
    public int? UtcOffsetMinutes { get; set; }

    public bool IsCopyOnly { get; set; }

    /// <summary>Position of the backup set in its file (<c>RESTORE … WITH FILE = n</c>).</summary>
    public int Position { get; set; } = 1;

    /// <summary>Native fulls: the database's files when it was backed up (logical and original physical names).</summary>
    public List<SqlBackupSetFile>? Files { get; set; }

    public long Size { get; set; }

    public string? Sha256 { get; set; }

    public string? ProductVersion { get; set; }

    public Guid? JobId { get; set; }

    public Guid? SessionId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>When the SQL Server backups of a job's VM run next, and how the last run went.</summary>
public sealed class SqlBackupScheduleEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid JobId { get; set; }

    public required string VmNativeId { get; set; }

    public DateTimeOffset? NextLogUtc { get; set; }

    public DateTimeOffset? NextFullUtc { get; set; }

    public DateTimeOffset? NextSystemUtc { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    /// <summary>Since when other tools' backups in msdb have been checked (the start of the last successful run).</summary>
    public DateTimeOffset? ForeignCheckedUtc { get; set; }

    public SessionResult? LastResult { get; set; }

    public string? LastMessage { get; set; }
}

/// <summary>
/// One run of the SQL Server backups of one VM, for the run list of its day session: what it backed up, its result and
/// all its lines (the agent's per-database lines too; the session log keeps one line per run and the warnings).
/// </summary>
public sealed class SqlBackupRunEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    public Guid JobId { get; set; }

    public required string VmNativeId { get; set; }

    public required string VmName { get; set; }

    public DateTimeOffset StartedUtc { get; set; }

    public DateTimeOffset EndedUtc { get; set; }

    public SessionState State { get; set; }

    public int Logs { get; set; }

    public int Fulls { get; set; }

    public long Bytes { get; set; }

    public long NewStoredBytes { get; set; }

    /// <summary>The last warning or error of the run, for the list.</summary>
    public string? Message { get; set; }

    public List<SqlBackupRunLine> Lines { get; set; } = [];
}

/// <summary>A line of a <see cref="SqlBackupRunEntity"/> (the severity stored as its name).</summary>
public sealed record SqlBackupRunLine(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Backdafuckup.Core.Operations.LogSeverity>))]
    Backdafuckup.Core.Operations.LogSeverity Severity,
    string Message);
