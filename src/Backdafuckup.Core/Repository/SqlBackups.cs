using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backdafuckup.Core.Repository;

public enum SqlBackupKind
{
    /// <summary>Transaction log backups (<c>BACKUP LOG</c>).</summary>
    Log,

    /// <summary>Native full backups of user databases (<c>BACKUP DATABASE</c>), a base of the log chain.</summary>
    Full,

    /// <summary>Full backups of <c>master</c>, <c>msdb</c> and <c>model</c>.</summary>
    SystemFull,
}

/// <summary>
/// The commit record of a SQL backup object (<c>sql-backups/&lt;vm&gt;/&lt;ts&gt;_&lt;id&gt;/manifest.json</c>): the backup
/// files one agent run wrote for one instance of a VM, each stored as chunks with its own index. The manifest holds
/// every fact the restore planner needs, so a catalog rebuild from the repository keeps point-in-time restore.
/// </summary>
public sealed record SqlBackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid SqlBackupId { get; init; }

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required SqlBackupKind Kind { get; init; }

    public ManifestJob? Job { get; init; }

    public Guid? SessionId { get; init; }

    public required ManifestSource Source { get; init; }

    /// <summary>The instance name (<c>MSSQLSERVER</c> for the default instance).</summary>
    public required string Instance { get; init; }

    public string? ProductVersion { get; init; }

    public required ManifestChunking Chunking { get; init; }

    public required IReadOnlyList<SqlBackupFile> Files { get; init; }
}

/// <summary>One backup file (one backup set) of a SQL backup object.</summary>
public sealed record SqlBackupFile
{
    /// <summary>Key of the file within the object (names its index file).</summary>
    public required string Key { get; init; }

    public required string Database { get; init; }

    /// <summary>The file's name as the agent wrote it in the VM (e.g. <c>shop_20261105T100000Z.trn</c>).</summary>
    public required string FileName { get; init; }

    public required long Length { get; init; }

    /// <summary>SHA-256 of the whole file (lower-case hex).</summary>
    public required string Sha256 { get; init; }

    public required ManifestIndexRef Index { get; init; }

    public required SqlBackupSet BackupSet { get; init; }
}

/// <summary>The backup set as <c>msdb.dbo.backupset</c> recorded it.</summary>
public sealed record SqlBackupSet
{
    public required Guid BackupSetUuid { get; init; }

    /// <summary><c>D</c> (full) or <c>L</c> (log).</summary>
    public required string Type { get; init; }

    public required decimal FirstLsn { get; init; }

    public required decimal LastLsn { get; init; }

    public required decimal CheckpointLsn { get; init; }

    public decimal? DatabaseBackupLsn { get; init; }

    public required Guid FamilyGuid { get; init; }

    public required Guid DatabaseGuid { get; init; }

    public Guid? FirstRecoveryForkGuid { get; init; }

    public Guid? LastRecoveryForkGuid { get; init; }

    public decimal? ForkPointLsn { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public required DateTimeOffset FinishUtc { get; init; }

    /// <summary>The server's offset from UTC at backup time in minutes (<c>time_zone</c> × 15); null when SQL Server did not know it.</summary>
    public int? UtcOffsetMinutes { get; init; }

    public bool IsCopyOnly { get; init; }

    public int Position { get; init; } = 1;

    public string? RecoveryModel { get; init; }

    public long BackupSize { get; init; }

    public long? CompressedBackupSize { get; init; }

    /// <summary>Full backups: the database's files when it was backed up.</summary>
    public IReadOnlyList<SqlBackupSetFile>? Files { get; init; }
}

/// <summary>A file of a backed-up database.</summary>
public sealed record SqlBackupSetFile(int FileId, string LogicalName, string PhysicalName, string Type, long SizeBytes);

public sealed record SqlBackupInfo(Guid SqlBackupId, string Path, SqlBackupManifest Manifest);

/// <summary>A file stored by <see cref="ISqlBackupWriter.WriteFileAsync"/>.</summary>
public sealed record SqlBackupStoredFile(string Key, long Length, string Sha256, ManifestIndexRef Index, long NewChunks, long NewStoredBytes);

/// <summary>
/// Writes one SQL backup object: the files first (streamed into chunks), then <see cref="CommitAsync"/> writes the
/// manifest (the commit point). Disposing without commit discards it.
/// </summary>
public interface ISqlBackupWriter : IAsyncDisposable
{
    Guid SqlBackupId { get; }

    Task<SqlBackupStoredFile> WriteFileAsync(string key, Stream content, CancellationToken cancellationToken);

    Task<SqlBackupInfo> CommitAsync(SqlBackupManifest manifest, CancellationToken cancellationToken);
}

public interface ISqlBackupReader : IAsyncDisposable
{
    SqlBackupManifest Manifest { get; }

    /// <summary>
    /// A forward-only stream of the file. Every chunk is verified on read, and the file's SHA-256 at its end; a mismatch
    /// throws <see cref="InvalidDataException"/>.
    /// </summary>
    Task<Stream> OpenFileAsync(string key, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SqlBackupManifest))]
public sealed partial class SqlBackupJsonContext : JsonSerializerContext;
