using System.Text.Json;
using Backdafuckup.Server.Agents;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// The <c>guest-metadata</c> blob of a restore point: what the agent reported about the guest. Format v1 holds the
/// volumes, writers and a flat file list per SQL database; v2 adds the file kinds, whether the database was part of the
/// VSS backup, and its own consistency. Both read into this class.
/// </summary>
public sealed class StoredGuestMetadata
{
    public const string FormatV1 = "backdafuckup-guest-v1";

    public const string FormatV2 = "backdafuckup-guest-v2";

    public const string SqlWriterName = "SqlServerWriter";

    public List<GuestVolume> Volumes { get; set; } = [];

    public List<StoredSqlDatabase> SqlDatabases { get; set; } = [];

    public List<GuestWriter> Writers { get; set; } = [];

    /// <summary>On a domain controller: its directory database and facts at backup time, with their consistency.</summary>
    public StoredAdDatabase? ActiveDirectory { get; set; }

    public const string NtdsWriterName = "NTDS";

    /// <summary>
    /// The metadata stored for a backup: the writers keep the components of the prepare step and take the state of the
    /// thaw step; each SQL database gets its own consistency.
    /// </summary>
    /// <param name="sqlSnapshot">
    /// SQL Server snapshot instead of VSS: a database is application-consistent exactly when the agent recorded it.
    /// </param>
    public static StoredGuestMetadata Build(GuestBackupInfo prepared, ThawReport? thawed, string pointConsistency, string? pointReason, IReadOnlyList<SqlSnapshotInstance>? sqlSnapshot = null)
    {
        var writers = prepared.Writers.Select(w =>
        {
            var after = thawed?.Writers.FirstOrDefault(t => string.Equals(t.WriterId, w.WriterId, StringComparison.OrdinalIgnoreCase));
            return new GuestWriter
            {
                Name = w.Name,
                WriterId = w.WriterId,
                InstanceName = w.InstanceName,
                State = after?.State ?? w.State,
                Failure = after?.Failure ?? w.Failure,
                Components = w.Components,
            };
        }).ToList();
        if (thawed is not null)
        {
            writers.AddRange(thawed.Writers.Where(t => prepared.Writers.All(w => !string.Equals(w.WriterId, t.WriterId, StringComparison.OrdinalIgnoreCase))));
        }

        var sqlWriter = thawed?.Writers.FirstOrDefault(w => string.Equals(w.Name, SqlWriterName, StringComparison.OrdinalIgnoreCase));
        var ntdsWriter = thawed?.Writers.FirstOrDefault(w => string.Equals(w.Name, NtdsWriterName, StringComparison.OrdinalIgnoreCase));
        return new StoredGuestMetadata
        {
            Volumes = prepared.Volumes,
            Writers = writers,
            ActiveDirectory = prepared.ActiveDirectory is { } ad
                ? StoredAdDatabase.From(ad, AdConsistency(ntdsWriter, thawed is not null, sqlSnapshot is not null, pointConsistency, pointReason))
                : null,
            SqlDatabases = prepared.SqlDatabases.Select(d =>
            {
                var (consistency, reason) = sqlSnapshot is not null
                    ? SnapshotConsistency(d, sqlSnapshot)
                    : DatabaseConsistency(d, sqlWriter, thawed is not null, pointConsistency, pointReason);
                return StoredSqlDatabase.From(d, consistency, reason);
            }).ToList(),
        };
    }

    public static async Task<StoredGuestMetadata?> ReadAsync(IRestorePointReader reader, CancellationToken cancellationToken)
    {
        var blob = reader.Manifest.Guest;
        if (blob is null)
        {
            return null;
        }

        await using var stream = await reader.OpenBlobAsync(blob, cancellationToken).ConfigureAwait(false);
        var stored = await JsonSerializer.DeserializeAsync<StoredGuestMetadata>(stream, AgentJson.Options, cancellationToken).ConfigureAwait(false) ?? new StoredGuestMetadata();
        if (blob.Format == FormatV1)
        {
            // v1 knew no per-database consistency: the restore point's applies.
            foreach (var database in stored.SqlDatabases)
            {
                database.Consistency = reader.Manifest.Consistency;
                database.ConsistencyReason = reader.Manifest.ConsistencyReason;
            }
        }

        return stored;
    }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, AgentJson.Options);

    /// <summary>
    /// The directory is application-consistent when the NTDS writer took part in the VSS freeze and ended stable.
    /// The SQL Server snapshot suspends only SQL Server, so the directory is then crash-consistent (ESE recovers it).
    /// </summary>
    private static (string Consistency, string? Reason) AdConsistency(GuestWriter? ntds, bool thawed, bool sqlSnapshot, string pointConsistency, string? pointReason)
    {
        if (pointConsistency != "application")
        {
            return ("crash", pointReason ?? "the VM snapshot is crash-consistent");
        }

        if (sqlSnapshot)
        {
            return ("crash", "the SQL Server snapshot suspends only SQL Server; the directory was not frozen by its writer");
        }

        if (thawed && ntds is null)
        {
            return ("crash", "the NTDS writer reported no state after the snapshot");
        }

        if (ntds is not null && (ntds.Failure is not null || !string.Equals(ntds.State, "Stable", StringComparison.OrdinalIgnoreCase)))
        {
            return ("crash", $"the NTDS writer ended in state {ntds.State}{(ntds.Failure is null ? string.Empty : $" ({ntds.Failure})")}");
        }

        return ("application", null);
    }

    private static (string Consistency, string? Reason) SnapshotConsistency(GuestSqlDatabase database, IReadOnlyList<SqlSnapshotInstance> instances)
    {
        var instance = instances.FirstOrDefault(i => string.Equals(i.Name, database.Instance, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            return ("crash", $"the SQL Server snapshot did not cover the instance {database.Instance}");
        }

        var recorded = instance.Databases.FirstOrDefault(d => string.Equals(d.Name, database.Database, StringComparison.OrdinalIgnoreCase));
        if (recorded is null)
        {
            return ("crash", database.Database is "master" or "model" or "msdb"
                ? "system databases cannot be suspended for a SQL Server snapshot; restore them from a native SQL Server backup"
                : $"the SQL Server snapshot did not cover this database{(instance.Error is null ? string.Empty : $" ({instance.Error})")}");
        }

        return recorded.State == SqlSnapshotStates.Recorded
            ? ("application", null)
            : ("crash", $"SQL Server snapshot: {recorded.Reason ?? recorded.State}");
    }

    private static (string Consistency, string? Reason) DatabaseConsistency(
        GuestSqlDatabase database, GuestWriter? sqlWriter, bool thawed, string pointConsistency, string? pointReason)
    {
        if (pointConsistency != "application")
        {
            return ("crash", pointReason ?? "the VM snapshot is crash-consistent");
        }

        if (database.InBackup == false)
        {
            return ("crash", "the SQL Server writer did not include this database in the VSS backup");
        }

        if (thawed && sqlWriter is null)
        {
            return ("crash", "the SQL Server writer reported no state after the snapshot");
        }

        if (sqlWriter is not null && (sqlWriter.Failure is not null || !string.Equals(sqlWriter.State, "Stable", StringComparison.OrdinalIgnoreCase)))
        {
            return ("crash", $"the SQL Server writer ended in state {sqlWriter.State}{(sqlWriter.Failure is null ? string.Empty : $" ({sqlWriter.Failure})")}");
        }

        return ("application", null);
    }
}

/// <summary>A domain controller's directory database as stored in the guest metadata (see <see cref="GuestAdDatabase"/>).</summary>
public sealed class StoredAdDatabase
{
    public string? DitPath { get; set; }

    public string? LogPath { get; set; }

    public string? SysvolPath { get; set; }

    public string? DomainDns { get; set; }

    public string? DomainNetbios { get; set; }

    public string? Forest { get; set; }

    public string? DomainDn { get; set; }

    public string? DsaDn { get; set; }

    public string? InvocationId { get; set; }

    public long? HighestCommittedUsn { get; set; }

    public int? DomainFunctionalLevel { get; set; }

    public int? ForestFunctionalLevel { get; set; }

    public bool? RecycleBin { get; set; }

    public int? TombstoneLifetimeDays { get; set; }

    public int? DeletedObjectLifetimeDays { get; set; }

    /// <summary>What the agent could not read at backup time.</summary>
    public string? Error { get; set; }

    /// <summary>The folder in the guest with the consistent copy made with <c>ntdsutil … ifm create full</c> before the snapshot.</summary>
    public string? IfmPath { get; set; }

    public DateTime? IfmCreatedUtc { get; set; }

    public double? IfmSeconds { get; set; }

    public long? IfmBytes { get; set; }

    /// <summary>Why no IFM copy was made.</summary>
    public string? IfmError { get; set; }

    /// <summary>"application" or "crash".</summary>
    public string Consistency { get; set; } = "crash";

    public string? ConsistencyReason { get; set; }

    public static StoredAdDatabase From(GuestAdDatabase ad, (string Consistency, string? Reason) consistency) => new()
    {
        DitPath = ad.DitPath,
        LogPath = ad.LogPath,
        SysvolPath = ad.SysvolPath,
        DomainDns = ad.DomainDns,
        DomainNetbios = ad.DomainNetbios,
        Forest = ad.Forest,
        DomainDn = ad.DomainDn,
        DsaDn = ad.DsaDn,
        InvocationId = ad.InvocationId,
        HighestCommittedUsn = ad.HighestCommittedUsn,
        DomainFunctionalLevel = ad.DomainFunctionalLevel,
        ForestFunctionalLevel = ad.ForestFunctionalLevel,
        RecycleBin = ad.RecycleBin,
        TombstoneLifetimeDays = ad.TombstoneLifetimeDays,
        DeletedObjectLifetimeDays = ad.DeletedObjectLifetimeDays,
        Error = ad.Error,
        IfmPath = ad.IfmPath,
        IfmCreatedUtc = ad.IfmCreatedUtc,
        IfmSeconds = ad.IfmSeconds,
        IfmBytes = ad.IfmBytes,
        IfmError = ad.IfmError,
        Consistency = ad.IfmPath is not null && ad.IfmError is null ? "application" : consistency.Consistency,
        ConsistencyReason = ad.IfmPath is not null && ad.IfmError is null
            ? consistency.Consistency == "application" ? null : $"the copy made with ntdsutil IFM before the snapshot is consistent ({consistency.Reason})"
            : consistency.Reason,
    };
}

/// <summary>A SQL Server database as stored in the guest metadata.</summary>
public sealed class StoredSqlDatabase
{
    public string Instance { get; set; } = string.Empty;

    public string? Server { get; set; }

    public string Database { get; set; } = string.Empty;

    public List<string> Files { get; set; } = [];

    public List<GuestSqlFile> DataFiles { get; set; } = [];

    public List<GuestSqlFile> LogFiles { get; set; } = [];

    public List<GuestSqlFile> OtherFiles { get; set; } = [];

    public bool? InBackup { get; set; }

    public int ComponentFlags { get; set; }

    /// <summary>"application" or "crash".</summary>
    public string Consistency { get; set; } = "crash";

    public string? ConsistencyReason { get; set; }

    public static StoredSqlDatabase From(GuestSqlDatabase d, string consistency, string? reason) => new()
    {
        Instance = d.Instance,
        Server = d.Server,
        Database = d.Database,
        Files = d.Files,
        DataFiles = d.DataFiles,
        LogFiles = d.LogFiles,
        OtherFiles = d.OtherFiles,
        InBackup = d.InBackup,
        ComponentFlags = d.ComponentFlags,
        Consistency = consistency,
        ConsistencyReason = reason,
    };
}

/// <summary>
/// The SQL Server snapshot of a restore point (blob <c>sql-snapshot</c>): per instance the recorded databases and the
/// snapshot backup metadata (<c>.bkm</c>), with which <c>RESTORE DATABASE … WITH METADATA_ONLY, NORECOVERY</c> can
/// start a restore that log backups continue.
/// </summary>
public sealed class StoredSqlSnapshot
{
    public const string Format = "backdafuckup-sql-snapshot-v1";

    public List<SqlSnapshotInstance> Instances { get; set; } = [];

    /// <summary>How long the databases' writes were suspended.</summary>
    public long SuspendedMs { get; set; }

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, AgentJson.Options);

    public static async Task<StoredSqlSnapshot?> ReadAsync(IRestorePointReader reader, CancellationToken cancellationToken)
    {
        if (reader.Manifest.SqlSnapshot is not { } blob)
        {
            return null;
        }

        await using var stream = await reader.OpenBlobAsync(blob, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<StoredSqlSnapshot>(stream, AgentJson.Options, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The VSS documents of a restore point (blob <c>vss-documents</c>): the SQL Server writer's metadata and the Backup
/// Components Document. Not used for restores yet; kept so that a later restore through the writer (with NORECOVERY for
/// log backups) is possible for restore points taken from now on.
/// </summary>
public sealed class StoredVssDocuments
{
    public const string Format = "backdafuckup-vss-v1";

    public string? SqlWriterMetadataXml { get; set; }

    public string? BackupComponentsXml { get; set; }

    public static StoredVssDocuments? From(GuestBackupInfo prepared, ThawReport? thawed) =>
        prepared.SqlWriterMetadataXml is null && thawed?.BackupComponentsXml is null
            ? null
            : new StoredVssDocuments { SqlWriterMetadataXml = prepared.SqlWriterMetadataXml, BackupComponentsXml = thawed?.BackupComponentsXml };

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, AgentJson.Options);
}
