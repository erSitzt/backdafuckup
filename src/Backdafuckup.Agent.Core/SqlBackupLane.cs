using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>The backup lane's access to the staging folders (the guest's file system; replaceable in tests).</summary>
public class SqlBackupFiles
{
    public virtual Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

    public virtual bool Exists(string path) => File.Exists(path);

    public virtual void Delete(string path) => File.Delete(path);

    public virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <summary>Creates a new file (never replaces one).</summary>
    public virtual Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

    public virtual void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

/// <summary>Where the backup lane sends finished backup files (the server, over the job's HTTPS channel).</summary>
public interface ISqlBackupSink
{
    /// <summary>Sends a finished backup file; true if the server already holds it (an earlier upload was committed).</summary>
    Task<bool> UploadAsync(SqlBackupUpload upload, Stream content, CancellationToken cancellationToken);

    /// <summary>Stores what this job uploaded for good; returns the committed file ids.</summary>
    Task<IReadOnlyList<string>> CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The SQL Server backups of the log chain in the guest (<see cref="SqlJobKinds.LogBackup"/>,
/// <see cref="SqlJobKinds.FullBackup"/>): BACKUP into a staging folder, the backup set from msdb, upload, delete after
/// the server's commit. Files whose upload fails stay queued for the next run, so the chain has no gap when the server
/// is away. Runs beside the agent's work slot; the instance lock keeps it apart from the snapshot suspend.
/// </summary>
public sealed class SqlBackupLane
{
    /// <summary>Our backup sets are named <c>backdafuckup …</c>; any other in msdb belongs to another tool.</summary>
    public const string NamePrefix = "backdafuckup";

    public const string InstanceQuery =
        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int), " +
        "CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";

    public const string DatabasesQuery =
        "SELECT d.name, d.database_id, d.state_desc, d.is_read_only, CAST(CASE WHEN d.source_database_id IS NULL THEN 0 ELSE 1 END AS bit), " +
        "CAST(CASE WHEN d.replica_id IS NULL THEN 0 ELSE 1 END AS bit), d.recovery_model_desc, " +
        "CAST(CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END AS bit), CONVERT(nvarchar(36), r.family_guid), " +
        "CONVERT(nvarchar(36), r.database_guid), CONVERT(nvarchar(36), r.recovery_fork_guid) " +
        "FROM sys.databases d LEFT JOIN sys.database_recovery_status r ON r.database_id = d.database_id ORDER BY d.name";

    public const string BackupSetByDeviceQuery =
        "SELECT TOP (1) " + SqlBackupSets.Columns + " FROM msdb.dbo.backupset b JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id " +
        "WHERE m.physical_device_name = @path ORDER BY b.backup_set_id DESC";

    public const string ForeignQuery =
        "SELECT b.database_name, " + SqlBackupSets.Columns + ", m.physical_device_name FROM msdb.dbo.backupset b " +
        "JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = b.media_set_id " +
        "WHERE b.backup_finish_date >= DATEADD(minute, DATEDIFF(minute, GETUTCDATE(), GETDATE()), @since) " +
        "AND (b.name IS NULL OR b.name NOT LIKE N'%backdafuckup%') ORDER BY b.backup_set_id";

    public const string FilesQuery =
        "SELECT file_id, name, physical_name, type_desc, CAST(size AS bigint) * 8192 FROM sys.master_files WHERE database_id = DB_ID(@database) ORDER BY file_id";

    public const string AllocatedQuery =
        "SELECT SUM(CAST(size AS bigint)) * 8192 FROM sys.master_files WHERE database_id = DB_ID(@database) AND type = 0";

    private static readonly string[] SystemDatabases = { "master", "msdb", "model" };

    private readonly ISqlEnvironment _sql;
    private readonly SqlInstanceLocks _locks;
    private readonly SqlBackupQueue _queue;
    private readonly ISqlBackupSink _sink;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _utcNow;
    private readonly SqlBackupFiles _files;
    private readonly List<string> _uploaded = new List<string>();
    private readonly List<string> _known = new List<string>();

    public SqlBackupLane(
        ISqlEnvironment sql, SqlInstanceLocks locks, SqlBackupQueue queue, ISqlBackupSink sink, Action<string> log, Func<DateTime>? utcNow = null, SqlBackupFiles? files = null)
    {
        _files = files ?? new SqlBackupFiles();
        _sql = sql;
        _locks = locks;
        _queue = queue;
        _sink = sink;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public async Task RunAsync(SqlJobRequest request, SqlJobResult result, CancellationToken cancellationToken)
    {
        var account = request.Account ?? throw new InvalidOperationException("The job names no account.");
        var instances = _sql.Instances()
            .Where(i => string.Equals(i.ServiceState, "Running", StringComparison.OrdinalIgnoreCase))
            .Where(i => request.Instance is null || string.Equals(i.Name, request.Instance, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (request.Instance is not null && instances.Count == 0)
        {
            throw new InvalidOperationException($"SQL Server instance {request.Instance} is not running (or not installed) in this VM.");
        }

        result.BackupDatabases = new List<SqlBackupDatabaseOutcome>();
        if (request.Kind == SqlJobKinds.LogBackup)
        {
            result.ForeignBackups = new List<SqlForeignBackup>();
        }

        // Files of earlier runs first: they keep the chain without gaps.
        Resolve(account);
        foreach (var entry in _queue.Entries.Where(e => e.IsComplete))
        {
            await UploadAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        var wait = TimeSpan.FromMilliseconds(request.LockWaitMs > 0 ? request.LockWaitMs : 60000);
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var held = _locks.TryAcquire(instance.Name, wait, request.Kind == SqlJobKinds.LogBackup ? "a log backup" : "a full backup", cancellationToken))
            {
                if (held is null)
                {
                    var reason = $"another backup of {instance.Name} ({_locks.Owner(instance.Name) ?? "the SQL Server snapshot"}) still ran after {wait.TotalSeconds:0} s";
                    _log($"{instance.Name}: skipped, {reason}.");
                    result.BackupDatabases.Add(new SqlBackupDatabaseOutcome { Instance = instance.Name, Action = SqlBackupActions.Skipped, Reason = reason });
                    continue;
                }

                await BackUpInstanceAsync(request, account, instance, result, cancellationToken).ConfigureAwait(false);
            }
        }

        await CommitAsync(result, cancellationToken).ConfigureAwait(false);
    }

    private async Task BackUpInstanceAsync(SqlJobRequest request, SqlAccount account, SqlInstanceInfo instance, SqlJobResult result, CancellationToken cancellationToken)
    {
        using (var connection = _sql.Open(instance.Name, account))
        {
            string? version;
            int edition;
            string? defaultBackupPath;
            using (var command = Command(connection, InstanceQuery))
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                version = reader.IsDBNull(0) ? null : reader.GetString(0);
                edition = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                defaultBackupPath = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var baseFolder = request.Folder ?? instance.BackupFolder ?? defaultBackupPath
                ?? throw new InvalidOperationException($"{instance.Name} has no backup folder for the staging files; set one in the job.");
            var folder = Combine(baseFolder, NamePrefix);
            _files.CreateDirectory(folder);
            var compress = edition != 4;
            var databases = Discover(connection, instance.Name);

            if (request.Kind == SqlJobKinds.LogBackup && request.ForeignSinceUtc is { } since)
            {
                result.ForeignBackups!.AddRange(Foreign(connection, instance.Name, since));
            }

            foreach (var database in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = Plan(request, database);
                result.BackupDatabases!.Add(outcome);
                if (outcome.Action == SqlBackupActions.Skipped)
                {
                    continue;
                }

                var kind = database.System ? "system" : outcome.Action;
                var entry = new SqlBackupQueueEntry
                {
                    Instance = instance.Name,
                    ProductVersion = version,
                    Database = database.Name,
                    Kind = kind,
                    Path = Combine(folder, FileName(database.Name, kind, _utcNow())),
                    StartedUtc = _utcNow(),
                    BackupJobId = request.BackupJobId,
                    SessionId = request.SessionId,
                };
                _queue.Add(entry);
                try
                {
                    compress = BackUp(connection, entry, request, compress, outcome);
                    entry.BackupSet = ReadBackupSet(connection, entry.Path)
                        ?? throw new InvalidOperationException($"msdb does not list the backup {entry.Path}.");
                    if (kind != SqlBackupActions.Log)
                    {
                        entry.BackupSet.Files = Files(connection, database.Name);
                    }
                    (entry.Sha256, entry.Length) = Hash(entry.Path);
                    _queue.Update(entry);
                    outcome.Path = entry.Path;
                    outcome.Bytes = entry.Length;
                    _log($"{instance.Name}\\{database.Name}: {Describe(kind)} {Path.GetFileName(entry.Path)} ({entry.Length:N0} bytes, LSN {entry.BackupSet.FirstLsn}–{entry.BackupSet.LastLsn}).");
                }
                catch (Exception ex) when (ex is DbException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    outcome.Action = SqlBackupActions.Failed;
                    outcome.Reason = Explain(ex, entry.Path);
                    _log($"{instance.Name}\\{database.Name}: the {Describe(kind)} failed: {outcome.Reason}");
                    TryDelete(entry.Path);
                    _queue.Remove(new[] { entry.Id });
                    continue;
                }

                await UploadAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What to do with a database in this job.</summary>
    private static SqlBackupDatabaseOutcome Plan(SqlJobRequest request, DiscoveredDatabase database)
    {
        var outcome = new SqlBackupDatabaseOutcome
        {
            Instance = database.Instance,
            Database = database.Name,
            RecoveryModel = database.RecoveryModel,
            HasLogChain = database.HasLogChain,
            FamilyGuid = database.FamilyGuid,
            DatabaseGuid = database.DatabaseGuid,
            RecoveryForkGuid = database.RecoveryForkGuid,
        };

        void Skip(string reason)
        {
            outcome.Action = SqlBackupActions.Skipped;
            outcome.Reason = reason;
        }

        var wanted = request.Kind == SqlJobKinds.FullBackup
            ? database.System ? request.SystemDatabases : request.Databases?.Contains(database.Name, StringComparer.OrdinalIgnoreCase) == true
            : !database.System && request.ExcludedDatabases?.Contains(database.Name, StringComparer.OrdinalIgnoreCase) != true;
        if (!wanted || database.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
        {
            Skip(database.System ? "system database" : "not part of this job");
            return outcome;
        }

        if (database.Snapshot)
        {
            Skip("database snapshot");
        }
        else if (database.Replica)
        {
            Skip("part of an availability group (not supported)");
        }
        else if (!string.Equals(database.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
        {
            Skip($"the database is {database.State.ToLowerInvariant()}");
        }
        else if (request.Kind == SqlJobKinds.FullBackup)
        {
            outcome.Action = SqlBackupActions.Full;
        }
        else if (database.ReadOnly)
        {
            Skip("read-only (no log to back up)");
        }
        else if (string.Equals(database.RecoveryModel, "SIMPLE", StringComparison.OrdinalIgnoreCase))
        {
            Skip("SIMPLE recovery (no log backups)");
        }
        else if (database.HasLogChain)
        {
            outcome.Action = SqlBackupActions.Log;
        }
        else if (request.FullWithoutChain)
        {
            outcome.Action = SqlBackupActions.Full;
            outcome.Reason = "no log chain yet (new database, or after SIMPLE recovery): a full backup starts it";
        }
        else
        {
            Skip("no log chain yet: a full backup has to start it");
        }

        return outcome;
    }

    /// <summary>Runs the BACKUP (and the verify of a full); returns whether COMPRESSION is still to be used.</summary>
    private bool BackUp(DbConnection connection, SqlBackupQueueEntry entry, SqlJobRequest request, bool compress, SqlBackupDatabaseOutcome outcome)
    {
        var log = entry.Kind == SqlBackupActions.Log;
        if (!log)
        {
            WarnAboutSpace(connection, entry);
        }

        var description = $"job={request.BackupJobId:D};session={request.SessionId:D}";
        var name = NamePrefix + " " + (log ? "log" : entry.Kind == "system" ? "system" : "full");
        while (true)
        {
            var text = (log ? "BACKUP LOG" : "BACKUP DATABASE") + " @database TO DISK = @path WITH FORMAT, INIT, CHECKSUM, NAME = @name, DESCRIPTION = @description" +
                (compress ? ", COMPRESSION" : string.Empty);
            try
            {
                using (var command = Command(connection, text, ("@database", entry.Database), ("@path", entry.Path), ("@name", name), ("@description", description)))
                {
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }

                break;
            }
            catch (DbException ex) when (compress && SqlAttach.ErrorNumber(ex) == 1844)
            {
                // This edition cannot compress backups: the repository compresses them instead.
                compress = false;
            }
        }

        if (!log)
        {
            using (var command = Command(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", ("@path", entry.Path)))
            {
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }
        }

        return compress;
    }

    private void WarnAboutSpace(DbConnection connection, SqlBackupQueueEntry entry)
    {
        try
        {
            long allocated;
            using (var command = Command(connection, AllocatedQuery, ("@database", entry.Database)))
            {
                var value = command.ExecuteScalar();
                allocated = value is null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            var volume = _sql.Volumes()
                .Where(v => entry.Path.StartsWith(v.Root, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.Root.Length)
                .FirstOrDefault();
            if (volume is not null && allocated > volume.FreeBytes)
            {
                _log($"{entry.Instance}\\{entry.Database}: the data files take {allocated:N0} bytes, {volume.Root} has {volume.FreeBytes:N0} bytes free; " +
                    "the full backup needs less if the files are not full or the backup is compressed, but may run out of space.");
            }
        }
        catch (DbException)
        {
            // Only a hint; the backup itself reports a full disk.
        }
    }

    /// <summary>Queue entries whose BACKUP was interrupted: msdb decides whether the file is a backup.</summary>
    private void Resolve(SqlAccount account)
    {
        foreach (var group in _queue.Entries.Where(e => !e.IsComplete).GroupBy(e => e.Instance, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using (var connection = _sql.Open(group.Key, account))
                {
                    foreach (var entry in group)
                    {
                        var set = _files.Exists(entry.Path) ? ReadBackupSet(connection, entry.Path) : null;
                        if (set is null || set.FirstLsn.Length == 0)
                        {
                            TryDelete(entry.Path);
                            _queue.Remove(new[] { entry.Id });
                            _log($"{entry.Instance}\\{entry.Database}: the {Describe(entry.Kind)} of {entry.StartedUtc:yyyy-MM-dd HH:mm} UTC was interrupted before SQL Server finished it; its partial file was deleted.");
                            continue;
                        }

                        entry.BackupSet = set;
                        (entry.Sha256, entry.Length) = Hash(entry.Path);
                        _queue.Update(entry);
                        _log($"{entry.Instance}\\{entry.Database}: the {Describe(entry.Kind)} {Path.GetFileName(entry.Path)} had finished before the agent stopped; it is sent now.");
                    }
                }
            }
            catch (Exception ex) when (ex is DbException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                _log($"{group.Key}: queued backups whose state is unknown stay queued ({ex.Message}).");
            }
        }
    }

    private async Task UploadAsync(SqlBackupQueueEntry entry, CancellationToken cancellationToken)
    {
        var upload = new SqlBackupUpload
        {
            FileId = entry.Id,
            Instance = entry.Instance,
            ProductVersion = entry.ProductVersion,
            Database = entry.Database,
            Kind = entry.Kind,
            FileName = Path.GetFileName(entry.Path),
            Length = entry.Length,
            Sha256 = entry.Sha256!,
            BackupSet = entry.BackupSet!,
            BackupJobId = entry.BackupJobId,
            SessionId = entry.SessionId,
        };
        try
        {
            using (var stream = _files.OpenRead(entry.Path))
            {
                if (await _sink.UploadAsync(upload, stream, cancellationToken).ConfigureAwait(false))
                {
                    _known.Add(entry.Id);
                }
                else
                {
                    _uploaded.Add(entry.Id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _log($"{entry.Instance}\\{entry.Database}: sending {Path.GetFileName(entry.Path)} to the backup server failed ({ex.Message}); it stays queued in the VM for the next run.");
        }
    }

    private async Task CommitAsync(SqlJobResult result, CancellationToken cancellationToken)
    {
        var done = new List<string>(_known);
        if (_uploaded.Count > 0)
        {
            try
            {
                done.AddRange(await _sink.CommitAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is System.Net.Http.HttpRequestException
                || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                _log($"the backup server did not confirm the {_uploaded.Count} file(s) sent ({ex.Message}); they stay queued in the VM for the next run.");
            }
        }

        var entries = _queue.Entries.Where(e => done.Contains(e.Id)).ToList();
        foreach (var entry in entries)
        {
            TryDelete(entry.Path);
        }

        _queue.Remove(entries.Select(e => e.Id));
        result.CommittedFiles = entries.Count;
        result.QueuedFiles = _queue.Entries.Count;
    }

    private List<DiscoveredDatabase> Discover(DbConnection connection, string instance)
    {
        var databases = new List<DiscoveredDatabase>();
        using (var command = Command(connection, DatabasesQuery))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                bool Flag(int i) => !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i), CultureInfo.InvariantCulture);
                var id = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                databases.Add(new DiscoveredDatabase
                {
                    Instance = instance,
                    Name = reader.GetString(0),
                    System = id <= 4,
                    State = Text(2) ?? "UNKNOWN",
                    ReadOnly = Flag(3),
                    Snapshot = Flag(4),
                    Replica = Flag(5),
                    RecoveryModel = Text(6),
                    HasLogChain = Flag(7),
                    FamilyGuid = Text(8),
                    DatabaseGuid = Text(9),
                    RecoveryForkGuid = Text(10),
                });
            }
        }

        return databases;
    }

    private static List<SqlForeignBackup> Foreign(DbConnection connection, string instance, DateTime sinceUtc)
    {
        var foreign = new List<SqlForeignBackup>();
        using (var command = Command(connection, ForeignQuery, ("@since", DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var set = SqlBackupSets.Read(reader, 1);
                set.Device = reader.IsDBNull(1 + SqlBackupSets.ColumnCount) ? null : reader.GetString(1 + SqlBackupSets.ColumnCount);
                foreign.Add(new SqlForeignBackup { Instance = instance, Database = reader.GetString(0), BackupSet = set });
            }
        }

        return foreign;
    }

    private static List<SqlBackupFileFacts> Files(DbConnection connection, string database)
    {
        var files = new List<SqlBackupFileFacts>();
        using (var command = Command(connection, FilesQuery, ("@database", database)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                files.Add(new SqlBackupFileFacts
                {
                    FileId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                    LogicalName = reader.GetString(1),
                    PhysicalName = reader.GetString(2),
                    Type = reader.GetString(3),
                    SizeBytes = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                });
            }
        }

        return files;
    }

    private static SqlBackupSetFacts? ReadBackupSet(DbConnection connection, string path)
    {
        using (var command = Command(connection, BackupSetByDeviceQuery, ("@path", path)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            var set = SqlBackupSets.Read(reader, 0);
            set.Device = path;
            return set;
        }
    }

    private (string Sha256, long Length) Hash(string path)
    {
        using (var stream = _files.OpenRead(path))
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(stream);
            return (BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant(), stream.Length);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (_files.Exists(path))
            {
                _files.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log($"{path} could not be deleted: {ex.Message}");
        }
    }

    private static string Explain(Exception ex, string path)
    {
        if (ex is DbException db)
        {
            switch (SqlAttach.ErrorNumber(db))
            {
                case 3201:
                case 3202:
                    return $"SQL Server cannot write {path}: {db.Message} (is the volume full, or may the SQL Server service not write to the folder?)";
                case 4214:
                    return "SQL Server has no full backup to continue the log chain from (error 4214); the next full backup starts it.";
                case 3041:
                    return db.Message;
            }
        }

        return ex.Message;
    }

    private static string Describe(string kind) => kind == SqlBackupActions.Log ? "log backup" : kind == "system" ? "system database backup" : "full backup";

    internal static string FileName(string database, string kind, DateTime utc)
    {
        var safe = new string(database.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
        var stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return kind == SqlBackupActions.Log ? $"{safe}_{stamp}.trn" : $"{safe}_{stamp}_{kind}.bak";
    }

    private static string Combine(string folder, string name)
    {
        var separator = folder.IndexOf('\\') < 0 && folder.IndexOf('/') >= 0 ? '/' : '\\';
        return folder.TrimEnd('\\', '/') + separator + name;
    }

    private static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
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

    private sealed class DiscoveredDatabase
    {
        public string Instance { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public bool System { get; set; }

        public string State { get; set; } = string.Empty;

        public bool ReadOnly { get; set; }

        public bool Snapshot { get; set; }

        public bool Replica { get; set; }

        public string? RecoveryModel { get; set; }

        public bool HasLogChain { get; set; }

        public string? FamilyGuid { get; set; }

        public string? DatabaseGuid { get; set; }

        public string? RecoveryForkGuid { get; set; }
    }
}
