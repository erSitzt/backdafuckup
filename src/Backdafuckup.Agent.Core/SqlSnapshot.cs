using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The T-SQL snapshot backup of SQL Server 2022 and later around a VM snapshot: suspends the writes of every user
/// database (<c>ALTER DATABASE … SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON</c>, with <c>(MODE = COPY_ONLY)</c> for copy-only
/// backups), each on its own unpooled connection, and after the snapshot records it with <c>BACKUP DATABASE … WITH
/// METADATA_ONLY</c> on that connection, which resumes the writes and appends to the instance's <c>.bkm</c>.
/// <list type="bullet">
/// <item>A regular (not copy-only) full backup starts the log chain of a database in FULL or BULK_LOGGED recovery that
/// had none, and becomes the base for differential backups, also those of other tools. A copy-only one changes neither.</item>
/// <item>Database by database, because a server-wide suspend skips databases closed by AUTO_CLOSE.</item>
/// <item>One connection each, because a session that has suspended a database cannot suspend another one ("Atleast one
/// database was previously suspended", also the error of GROUP).</item>
/// <item>Disposing closes the connections: SQL Server then resumes every database that is still suspended by itself.</item>
/// </list>
/// </summary>
public sealed class SqlSnapshotSuspend : IDisposable
{
    /// <summary>SQL Server 2022 (16.x) introduced the T-SQL snapshot backup.</summary>
    public const int MinimumMajorVersion = 16;

    private readonly ISqlEnvironment _sql;
    private readonly SqlAccount _account;
    private readonly Action<string> _log;
    private readonly Func<string, byte[]> _readMetadata;
    private readonly Action<string> _deleteMetadata;
    private readonly List<Held> _held = new List<Held>();
    private readonly Stopwatch _suspended = new Stopwatch();
    private readonly bool _copyOnly;
    private long _suspendedMs;

    /// <param name="readMetadata">Reads the <c>.bkm</c> SQL Server wrote (tests map the container's path).</param>
    /// <param name="deleteMetadata">Deletes it after reading.</param>
    /// <param name="copyOnly">Copy-only backups (outside the backup chain) instead of regular full backups.</param>
    public SqlSnapshotSuspend(
        ISqlEnvironment sql, SqlAccount account, Action<string> log, Func<string, byte[]>? readMetadata = null, Action<string>? deleteMetadata = null, bool copyOnly = true)
    {
        _sql = sql;
        _account = account;
        _log = log;
        _copyOnly = copyOnly;
        _readMetadata = readMetadata ?? File.ReadAllBytes;
        _deleteMetadata = deleteMetadata ?? File.Delete;
    }

    /// <summary>From the first suspend until the last database resumed (or the connections closed).</summary>
    public long SuspendedMs => _suspended.IsRunning ? _suspended.ElapsedMilliseconds : _suspendedMs;

    /// <summary>
    /// Suspends the user databases of the running instances (or only <paramref name="onlyInstance"/>). Instances older
    /// than SQL Server 2022, stopped ones and failures are reported, not thrown: the other databases still count.
    /// </summary>
    public SqlSnapshotProgress Suspend(IEnumerable<SqlInstanceInfo> instances, string? onlyInstance = null)
    {
        var progress = new SqlSnapshotProgress();
        foreach (var info in instances)
        {
            if (onlyInstance is not null && !string.Equals(onlyInstance, info.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var instance = new SqlSnapshotInstance { Name = info.Name };
            progress.Instances.Add(instance);
            if (info.ServiceState is { } state && state != "Running")
            {
                instance.Error = $"the service {info.ServiceName} is {state.ToLowerInvariant()}";
                continue;
            }

            DbConnection connection;
            try
            {
                connection = _sql.Open(info.Name, _account);
            }
            catch (Exception ex) when (IsConnectError(ex))
            {
                instance.Error = ex.Message;
                continue;
            }

            var held = new Held(info, instance, connection);
            _held.Add(held);
            try
            {
                SuspendInstance(held);
            }
            catch (DbException ex)
            {
                instance.Error = ex.Message;
            }
        }

        return progress;
    }

    /// <summary>
    /// The VM snapshot is taken: records it database by database (each <c>BACKUP</c> resumes its database), reads and
    /// deletes the <c>.bkm</c> per instance, and resumes whatever is left.
    /// </summary>
    public List<SqlSnapshotInstance> Record(string backupName, Guid jobId)
    {
        foreach (var held in _held)
        {
            var suspended = held.Instance.Databases.Where(d => d.State == SqlSnapshotStates.Suspended).ToList();
            if (suspended.Count == 0)
            {
                continue;
            }

            var path = MetadataPath(held, jobId);
            var first = true;
            foreach (var database in suspended)
            {
                var connection = held.Suspends[database.Name];
                try
                {
                    using (var command = Command(connection,
                        "BACKUP DATABASE @database TO DISK = @path WITH METADATA_ONLY, " + (first ? "FORMAT, INIT, MEDIADESCRIPTION = @name, " : "NOFORMAT, NOINIT, ") + "NAME = @name",
                        ("@database", database.Name), ("@path", path), ("@name", backupName)))
                    {
                        command.ExecuteNonQuery();
                    }

                    database.State = SqlSnapshotStates.Recorded;
                    first = false;
                    ReadBackupSet(held, connection, database, backupName);
                }
                catch (DbException ex)
                {
                    database.State = SqlSnapshotStates.Failed;
                    database.Reason = $"BACKUP … WITH METADATA_ONLY failed: {ex.Message}";
                }

                // A failed BACKUP resumes the database too; closing its session makes sure.
                Close(held, database.Name);
            }

            if (!first)
            {
                held.Instance.MetadataPath = path;
                try
                {
                    held.Instance.Metadata = _readMetadata(path);
                    _deleteMetadata(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    held.Instance.Error = $"the snapshot backup metadata {path} could not be read: {ex.Message}";
                }
            }

            _log($"{held.Info.Name}: recorded {suspended.Count(d => d.State == SqlSnapshotStates.Recorded)} of {suspended.Count} database(s) in msdb " +
                $"({(_copyOnly ? "copy-only" : "full backups")}, {path}).");
        }

        Stop();
        return Report();
    }

    /// <summary>No snapshot (or it came too late): resumes every database without recording anything.</summary>
    public List<SqlSnapshotInstance> Resume(string reason)
    {
        foreach (var held in _held)
        {
            foreach (var database in held.Instance.Databases.Where(d => d.State == SqlSnapshotStates.Suspended))
            {
                database.State = SqlSnapshotStates.Failed;
                database.Reason = reason;
                try
                {
                    using (var command = Command(held.Suspends[database.Name], $"ALTER DATABASE {Quote(database.Name)} SET SUSPEND_FOR_SNAPSHOT_BACKUP = OFF"))
                    {
                        command.ExecuteNonQuery();
                    }
                }
                catch (DbException ex)
                {
                    _log($"{held.Info.Name}\\{database.Name}: resuming failed ({ex.Message}); closing its connection resumes it.");
                }

                Close(held, database.Name);
            }
        }

        _log($"Resumed without recording: {reason}");
        Stop();
        return Report();
    }

    public void Dispose()
    {
        foreach (var held in _held)
        {
            foreach (var name in held.Suspends.Keys.ToList())
            {
                Close(held, name);
            }

            held.Connection.Dispose();
        }

        _held.Clear();
        Stop();
    }

    private void SuspendInstance(Held held)
    {
        int major;
        using (var command = Command(held.Connection,
            "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), " +
            "CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))"))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            major = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            held.Instance.ProductVersion = reader.IsDBNull(1) ? null : reader.GetString(1);
            held.DefaultBackupPath = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (major < MinimumMajorVersion)
        {
            held.Instance.Error = $"SQL Server {held.Instance.ProductVersion} has no T-SQL snapshot backup (SQL Server 2022 or later)";
            return;
        }

        // last_log_backup_lsn is NULL while a database has no log chain (also in FULL recovery before its first regular full backup).
        var databases = new List<(string Name, string State, bool ReadOnly, bool IsSnapshot, string? Model, bool Chain, int? LogAge)>();
        using (var command = Command(held.Connection, DatabasesQuery))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                databases.Add((
                    reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), !reader.IsDBNull(2) && reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), !reader.IsDBNull(5) && reader.GetBoolean(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }
        }

        foreach (var (name, state, readOnly, isSnapshot, model, chain, logAge) in databases)
        {
            var database = new SqlSnapshotDatabase { Name = name, RecoveryModel = model, HadLogChain = chain, LogBackupAgeMinutes = logAge };
            held.Instance.Databases.Add(database);
            if (isSnapshot)
            {
                database.Reason = "a database snapshot";
                continue;
            }

            if (state != "ONLINE")
            {
                database.Reason = $"the database is {state.ToLowerInvariant()}";
                continue;
            }

            if (readOnly)
            {
                database.Reason = "the database is read-only (nothing to suspend)";
                continue;
            }

            DbConnection? connection = null;
            try
            {
                connection = _sql.Open(held.Info.Name, _account);
                using (var command = Command(connection, $"ALTER DATABASE {Quote(name)} SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON" + (_copyOnly ? " (MODE = COPY_ONLY)" : string.Empty)))
                {
                    command.ExecuteNonQuery();
                }

                if (!_suspended.IsRunning)
                {
                    _suspended.Start();
                }

                held.Suspends[name] = connection;
                connection = null;
                database.State = SqlSnapshotStates.Suspended;
            }
            catch (Exception ex) when (IsConnectError(ex))
            {
                database.State = SqlSnapshotStates.Failed;
                database.Reason = $"suspending failed: {ex.Message}";
            }
            finally
            {
                connection?.Dispose();
            }
        }

        _log($"{held.Info.Name} (SQL Server {held.Instance.ProductVersion}): suspended {held.Instance.Databases.Count(d => d.State == SqlSnapshotStates.Suspended)} of {databases.Count} user database(s).");
    }

    /// <summary>The user databases with their state, recovery model, log chain and the age of their last log backup.</summary>
    internal const string DatabasesQuery =
        "SELECT d.name, d.state_desc, d.is_read_only, CAST(CASE WHEN d.source_database_id IS NULL THEN 0 ELSE 1 END AS bit), d.recovery_model_desc, " +
        "CAST(CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END AS bit), " +
        "(SELECT DATEDIFF(minute, MAX(b.backup_finish_date), GETDATE()) FROM msdb.dbo.backupset b WHERE b.database_name = d.name AND b.type = 'L') " +
        "FROM sys.databases d LEFT JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.database_id > 4 ORDER BY d.name";

    /// <summary>The recorded backup set in msdb, and whether the database has a log chain now.</summary>
    internal const string BackupSetQuery =
        "SELECT TOP (1) b.position, b.is_copy_only, CONVERT(varchar(30), b.first_lsn), CONVERT(varchar(30), b.last_lsn), CONVERT(varchar(30), b.checkpoint_lsn), " +
        "CONVERT(varchar(30), b.database_backup_lsn), " +
        "(SELECT CAST(CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END AS bit) FROM sys.database_recovery_status r WHERE r.database_id = DB_ID(@database)) " +
        "FROM msdb.dbo.backupset b WHERE b.database_name = @database AND b.name = @name AND b.is_snapshot = 1 ORDER BY b.backup_set_id DESC";

    /// <summary>Position, type and LSNs of the backup set just recorded (for restores with log backups); failures are only logged.</summary>
    private void ReadBackupSet(Held held, DbConnection connection, SqlSnapshotDatabase database, string backupName)
    {
        try
        {
            using (var command = Command(connection, BackupSetQuery, ("@database", database.Name), ("@name", backupName)))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    _log($"{held.Info.Name}\\{database.Name}: the backup set is missing in msdb.");
                    return;
                }

                database.Position = reader.IsDBNull(0) ? (int?)null : Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
                database.CopyOnly = !reader.IsDBNull(1) && reader.GetBoolean(1);
                database.FirstLsn = reader.IsDBNull(2) ? null : reader.GetString(2);
                database.LastLsn = reader.IsDBNull(3) ? null : reader.GetString(3);
                database.CheckpointLsn = reader.IsDBNull(4) ? null : reader.GetString(4);
                database.DatabaseBackupLsn = reader.IsDBNull(5) ? null : reader.GetString(5);
                database.StartedLogChain = !database.HadLogChain && !reader.IsDBNull(6) && reader.GetBoolean(6);
            }
        }
        catch (DbException ex)
        {
            _log($"{held.Info.Name}\\{database.Name}: reading the backup set from msdb failed: {ex.Message}");
        }
    }

    private string MetadataPath(Held held, Guid jobId)
    {
        var folder = held.Info.BackupFolder ?? held.DefaultBackupPath
            ?? throw new InvalidOperationException($"{held.Info.Name} has no backup folder for the snapshot backup metadata.");
        var separator = folder.IndexOf('\\') < 0 && folder.IndexOf('/') >= 0 ? '/' : '\\';
        var name = string.Join("_", held.Info.Name.Split(Path.GetInvalidFileNameChars()));
        return folder.TrimEnd('\\', '/') + separator + $"backdafuckup-{jobId:N}-{name}.bkm";
    }

    private List<SqlSnapshotInstance> Report() => _held.Select(h => h.Instance).ToList();

    private void Stop()
    {
        if (_suspended.IsRunning)
        {
            _suspended.Stop();
            _suspendedMs = _suspended.ElapsedMilliseconds;
        }
    }

    private static void Close(Held held, string database)
    {
        if (held.Suspends.TryGetValue(database, out var connection))
        {
            held.Suspends.Remove(database);
            connection.Dispose();
        }
    }

    private static bool IsConnectError(Exception ex) =>
        ex is DbException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception;

    /// <summary>A database name as a bracketed identifier (QUOTENAME).</summary>
    internal static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    private static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 120;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private sealed class Held
    {
        public Held(SqlInstanceInfo info, SqlSnapshotInstance instance, DbConnection connection)
        {
            Info = info;
            Instance = instance;
            Connection = connection;
        }

        public SqlInstanceInfo Info { get; }

        public SqlSnapshotInstance Instance { get; }

        /// <summary>The instance's connection for the version and the database list.</summary>
        public DbConnection Connection { get; }

        /// <summary>The connection (session) that suspended each database; its end resumes the database.</summary>
        public Dictionary<string, DbConnection> Suspends { get; } = new Dictionary<string, DbConnection>(StringComparer.OrdinalIgnoreCase);

        public string? DefaultBackupPath { get; set; }
    }
}
