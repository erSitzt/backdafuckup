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
        return new StoredGuestMetadata
        {
            Volumes = prepared.Volumes,
            Writers = writers,
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
