using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// SQL Server as the agent sees it, in memory: one running instance SQLEXPRESS whose databases are a name and a file
/// list. It understands the statements the agent sends for publishing; an attach reads every file through
/// <paramref name="readFile"/> (in the tests: from the published volume, through the iSCSI target).
/// </summary>
internal sealed partial class ScriptedSqlEnvironment(Func<string, byte[]> readFile) : ISqlEnvironment
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (int Id, List<string> Files)> _databases = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId = 5;

    public List<string> Statements { get; } = [];

    public List<SqlAccount> Accounts { get; } = [];

    /// <summary>The content of each file, as SQL Server read it at the attach.</summary>
    public Dictionary<string, byte[]> AttachedFiles { get; } = [];

    /// <summary>The next DROP DATABASE statements that fail (e.g. SQL Server is shutting down).</summary>
    public int FailDrops { get; set; }

    /// <summary>Makes the next attach fail with this SQL Server error number.</summary>
    public int? FailAttachWith { get; set; }

    /// <summary>
    /// If set, the first statement that matches waits for the gate (and sets <see cref="HoldReached"/> first): keeps a job
    /// running, e.g. to stop the session or restart the agent meanwhile. With <see cref="HoldFails"/> it then fails.
    /// </summary>
    public (Func<string, bool> When, ManualResetEventSlim Gate)? Hold { get; set; }

    public ManualResetEventSlim HoldReached { get; } = new();

    /// <summary>The held statement fails when the gate opens (its connection broke).</summary>
    public bool HoldFails { get; set; }

    /// <summary>SERVERPROPERTY('ProductVersion') of the instance (SQL Server 2022 Express by default).</summary>
    public string ProductVersion { get; set; } = "16.0.4305.5";

    /// <summary>The instance's backup folder (registry BackupDirectory): where BACKUP … METADATA_ONLY writes the .bkm.</summary>
    public string? BackupFolder { get; set; }

    /// <summary>The account is sysadmin on the instance (inventory).</summary>
    public bool Sysadmin { get; set; }

    /// <summary>Makes the next ALTER DATABASE … SET ONLINE fail with this SQL Server error number.</summary>
    public int? FailOnlineWith { get; set; }

    /// <summary>The .bak files written by BACKUP DATABASE … COPY_ONLY: path → database.</summary>
    public Dictionary<string, string> Backups { get; } = [];

    /// <summary>The backup set name (NAME) of each .bak: path → name.</summary>
    public Dictionary<string, string> BackupSetNames { get; } = [];

    /// <summary>The databases that are offline.</summary>
    public HashSet<string> Offline { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The content of each file of a database as SQL Server read it when the database came online.</summary>
    public Dictionary<string, byte[]> OnlineFiles { get; } = [];

    /// <summary>The databases whose writes are suspended for a snapshot right now.</summary>
    public HashSet<string> Suspended { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per suspended database: suspended for a copy-only backup (MODE = COPY_ONLY).</summary>
    public Dictionary<string, bool> SuspendedCopyOnly { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The databases with a log chain (FULL recovery and a regular full backup); all databases are in FULL recovery.</summary>
    public HashSet<string> LogChains { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The snapshot backup sets recorded with BACKUP … METADATA_ONLY: database, name, copy-only, position.</summary>
    public List<(string Database, string Name, bool CopyOnly, int Position)> SnapshotBackups { get; } = [];

    /// <summary>Database users of attached databases that have no login on the instance (name, type_desc).</summary>
    public List<(string Name, string Type)> OrphanedUsers { get; } = [];

    public void AddDatabase(string name, params string[] files)
    {
        lock (_lock)
        {
            _databases[name] = (_nextId++, [.. files]);
        }
    }

    public List<string> DatabaseFiles(string name)
    {
        lock (_lock)
        {
            return _databases.TryGetValue(name, out var database) ? [.. database.Files] : [];
        }
    }

    public List<SqlInstanceInfo> Instances() =>
    [
        new()
        {
            Name = "SQLEXPRESS", InstanceId = "MSSQL16.SQLEXPRESS", SetupVersion = "16.0.1000.6", ServiceName = "MSSQL$SQLEXPRESS", ServiceState = "Running",
            ServiceAccount = @"NT Service\MSSQL$SQLEXPRESS", DefaultDataFolder = @"D:\Data", DefaultLogFolder = @"L:\Logs", BackupFolder = BackupFolder,
        },
    ];

    public List<SqlVolumeInfo> Volumes() =>
    [
        new() { Root = @"C:\", FreeBytes = 50L << 30, TotalBytes = 100L << 30 },
        new() { Root = @"D:\", FreeBytes = 20L << 30, TotalBytes = 40L << 30 },
        new() { Root = @"L:\", FreeBytes = 10L << 30, TotalBytes = 20L << 30 },
    ];

    public DbConnection Open(string instance, SqlAccount account)
    {
        lock (_lock)
        {
            Accounts.Add(account);
        }

        Assert.Equal("SQLEXPRESS", instance);
        return new Connection(this);
    }

    /// <summary>Stable GUIDs per database (family and database GUID), as msdb would report them.</summary>
    public static Guid FamilyGuid(string database) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("family:" + database.ToUpperInvariant())));

    public static Guid DatabaseGuid(string database) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("database:" + database.ToUpperInvariant())));

    /// <summary>A table with the columns of <see cref="SqlBackupSets.Columns"/> and <paramref name="extra"/> after them.</summary>
    private static DataTable BackupSetTable(params (string Name, Type Type)[] extra) =>
        Table([
            ("uuid", typeof(string)), ("type", typeof(string)), ("position", typeof(int)), ("copyOnly", typeof(bool)), ("snapshot", typeof(bool)),
            ("first", typeof(string)), ("last", typeof(string)), ("checkpoint", typeof(string)), ("base", typeof(string)),
            ("family", typeof(string)), ("database", typeof(string)), ("firstFork", typeof(string)), ("lastFork", typeof(string)), ("forkPoint", typeof(string)),
            ("start", typeof(string)), ("finish", typeof(string)), ("timeZone", typeof(int)), ("model", typeof(string)),
            ("size", typeof(string)), ("compressed", typeof(string)), ("name", typeof(string)),
            .. extra]);

    /// <summary>SQL Server's clock (UTC; the scripted server runs in UTC): when backups start and finish.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    private object[] BackupSetRow(
        string database, string type, int position, bool copyOnly, bool snapshot, string first, string last, string checkpoint, string databaseBackupLsn, string name)
    {
        var family = FamilyGuid(database).ToString();
        var now = Now().ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        return
        [
            Guid.NewGuid().ToString(), type, position, copyOnly, snapshot, first, last, checkpoint, databaseBackupLsn,
            family, DatabaseGuid(database).ToString(), family, family, DBNull.Value, now, now, 0, "FULL", "8388608", DBNull.Value, name,
        ];
    }

    private (object? Scalar, DataTable? Table) Execute(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        if (Hold is { } hold && hold.When(text))
        {
            Hold = null;
            HoldReached.Set();
            hold.Gate.Wait(TimeSpan.FromSeconds(120));
            if (HoldFails)
            {
                throw new ScriptedSqlException(10054, "A transport-level error has occurred (the connection was closed).");
            }
        }

        lock (_lock)
        {
            Statements.Add(text);
            string Name() => (string)parameters["@name"]!;
            switch (text)
            {
                case "SELECT DB_ID(@name)":
                case "SELECT database_id FROM sys.databases WHERE name = @name":
                    return (_databases.TryGetValue(Name(), out var found) ? found.Id : DBNull.Value, null);
                case "SELECT HAS_DBACCESS(@name)":
                    // The scripted logins can open what they attach.
                    return (1, null);

                case "SELECT state_desc FROM sys.databases WHERE name = @name":
                    return (_databases.ContainsKey(Name()) ? (Offline.Contains(Name()) ? "OFFLINE" : Restoring.Contains(Name()) ? "RESTORING" : "ONLINE") : null, null);
                case "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int), SUSER_SNAME(), IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator')":
                {
                    var table = Table(("version", typeof(string)), ("edition", typeof(string)), ("engine", typeof(int)), ("login", typeof(string)), ("sysadmin", typeof(int)), ("dbcreator", typeof(int)));
                    table.Rows.Add(ProductVersion, "Express Edition (64-bit)", 4, Accounts[^1].UserName, Sysadmin ? 1 : 0, 1);
                    return (null, table);
                }

                case "SELECT database_id, name, state_desc, recovery_model_desc, is_read_only FROM sys.databases ORDER BY name":
                {
                    var table = Table(("id", typeof(int)), ("name", typeof(string)), ("state", typeof(string)), ("recovery", typeof(string)), ("ro", typeof(bool)));
                    foreach (var (name, database) in _databases.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        table.Rows.Add(database.Id, name, "ONLINE", "SIMPLE", false);
                    }

                    return (null, table);
                }

                case "SELECT database_id, name, physical_name, type_desc, CAST(size AS bigint) * 8192 FROM sys.master_files ORDER BY database_id, file_id":
                {
                    var table = Table(("id", typeof(int)), ("name", typeof(string)), ("path", typeof(string)), ("type", typeof(string)), ("size", typeof(long)));
                    foreach (var database in _databases.Values.OrderBy(d => d.Id))
                    {
                        foreach (var file in database.Files)
                        {
                            table.Rows.Add(database.Id, Path.GetFileNameWithoutExtension(file), file, file.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) ? "LOG" : "ROWS", 8L << 20);
                        }
                    }

                    return (null, table);
                }

                case "SELECT SUSER_SNAME(owner_sid), is_trustworthy_on, is_broker_enabled, recovery_model_desc, compatibility_level FROM sys.databases WHERE name = @name":
                {
                    var table = Table(("owner", typeof(string)), ("trustworthy", typeof(bool)), ("broker", typeof(bool)), ("recovery", typeof(string)), ("level", typeof(byte)));
                    table.Rows.Add(Accounts[^1].UserName, false, false, "FULL", (byte)160);
                    return (null, table);
                }

                case "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))":
                {
                    var table = Table(("major", typeof(int)), ("version", typeof(string)), ("backup", typeof(string)));
                    table.Rows.Add(int.Parse(ProductVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture), ProductVersion, @"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup\");
                    return (null, table);
                }

                case "SELECT d.name, d.state_desc, d.is_read_only, CAST(CASE WHEN d.source_database_id IS NULL THEN 0 ELSE 1 END AS bit), d.recovery_model_desc, " +
                     "CAST(CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END AS bit), " +
                     "(SELECT DATEDIFF(minute, MAX(b.backup_finish_date), GETDATE()) FROM msdb.dbo.backupset b WHERE b.database_name = d.name AND b.type = 'L') " +
                     "FROM sys.databases d LEFT JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.database_id > 4 ORDER BY d.name":
                {
                    var table = Table(("name", typeof(string)), ("state", typeof(string)), ("ro", typeof(bool)), ("snapshot", typeof(bool)), ("model", typeof(string)), ("chain", typeof(bool)), ("logAge", typeof(int)));
                    foreach (var name in _databases.Keys.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        table.Rows.Add(name, "ONLINE", false, false, "FULL", LogChains.Contains(name), DBNull.Value);
                    }

                    return (null, table);
                }

                case "SELECT TOP (1) " + SqlBackupSets.Columns + ", " +
                     "(SELECT CAST(CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END AS bit) FROM sys.database_recovery_status r WHERE r.database_id = DB_ID(@database)) " +
                     "FROM msdb.dbo.backupset b WHERE b.database_name = @database AND b.name = @name AND b.is_snapshot = 1 ORDER BY b.backup_set_id DESC":
                {
                    var table = BackupSetTable(("chain", typeof(bool)));
                    var database = (string)parameters["@database"]!;
                    var set = SnapshotBackups.Last(b => b.Database == database && b.Name == (string)parameters["@name"]!);
                    table.Rows.Add([
                        .. BackupSetRow(database, "D", set.Position, set.CopyOnly, snapshot: true, "41000000012300001", "41000000012600001", "41000000012300001",
                            set.CopyOnly ? "0" : "41000000004500001", (string)parameters["@name"]!),
                        LogChains.Contains(database)]);
                    return (null, table);
                }

                case "SELECT database_id, state_desc, CAST(CASE WHEN source_database_id IS NULL THEN 0 ELSE 1 END AS bit), CAST(CASE WHEN replica_id IS NULL THEN 0 ELSE 1 END AS bit), " +
                     "CAST(CASE WHEN is_published = 1 OR is_subscribed = 1 OR is_merge_published = 1 OR is_distributor = 1 THEN 1 ELSE 0 END AS bit) FROM sys.databases WHERE name = @name":
                {
                    var table = Table(("id", typeof(int)), ("state", typeof(string)), ("snapshot", typeof(bool)), ("group", typeof(bool)), ("replicated", typeof(bool)));
                    if (_databases.TryGetValue(Name(), out var database))
                    {
                        table.Rows.Add(database.Id, Offline.Contains(Name()) ? "OFFLINE" : "ONLINE", false, false, false);
                    }

                    return (null, table);
                }

                case "SELECT (SELECT COUNT(*) FROM sys.databases WHERE source_database_id = @id) + (SELECT COUNT(*) FROM sys.database_mirroring WHERE database_id = @id AND mirroring_guid IS NOT NULL)":
                    return (0, null);
                case "SELECT physical_name, type_desc FROM sys.master_files WHERE database_id = @id ORDER BY file_id":
                {
                    var table = Table(("path", typeof(string)), ("type", typeof(string)));
                    foreach (var file in _databases.Values.Where(d => d.Id == (int)parameters["@id"]!).SelectMany(d => d.Files))
                    {
                        table.Rows.Add(file, file.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) ? "LOG" : "ROWS");
                    }

                    return (null, table);
                }

                case "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))":
                {
                    var table = Table(("edition", typeof(int)), ("backup", typeof(string)));
                    table.Rows.Add(4, BackupFolder ?? @"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup");
                    return (null, table);
                }

                case "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM":
                    Assert.True(File.Exists((string)parameters["@path"]!), "verified a backup that does not exist");
                    return (null, null);
                case "SELECT state_desc, user_access_desc FROM sys.databases WHERE name = @name":
                {
                    var table = Table(("state", typeof(string)), ("access", typeof(string)));
                    table.Rows.Add(Offline.Contains(Name()) ? "OFFLINE" : "ONLINE", SingleUser.Contains(Name()) ? "SINGLE_USER" : "MULTI_USER");
                    return (null, table);
                }

                case "SELECT physical_name FROM sys.master_files WHERE database_id = @id":
                {
                    var table = new DataTable();
                    table.Columns.Add("physical_name", typeof(string));
                    foreach (var file in _databases.Values.Where(d => d.Id == (int)parameters["@id"]!).SelectMany(d => d.Files))
                    {
                        table.Rows.Add(file);
                    }

                    return (null, table);
                }
            }

            if (OrphanQuery().Match(text) is { Success: true })
            {
                var table = Table(("name", typeof(string)), ("type", typeof(string)));
                foreach (var (name, type) in OrphanedUsers)
                {
                    table.Rows.Add(name, type);
                }

                return (null, table);
            }

            if (AttachStatement().Match(text) is { Success: true } attach)
            {
                var name = attach.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (FailAttachWith is { } number)
                {
                    FailAttachWith = null;
                    throw new ScriptedSqlException(number, $"Simulated error {number} attaching {name}.");
                }

                var files = FileName().Matches(attach.Groups["files"].Value).Select(m => m.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).ToList();
                foreach (var file in files)
                {
                    AttachedFiles[file] = readFile(file);
                }

                _databases[name] = (_nextId++, files);
                return (null, null);
            }

            if (UserAccessStatement().Match(text) is { Success: true } access)
            {
                var name = access.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (access.Groups["access"].Value == "SINGLE_USER")
                {
                    SingleUser.Add(name);
                }
                else
                {
                    SingleUser.Remove(name);
                }

                return (null, null);
            }

            if (OfflineStatement().Match(text) is { Success: true } offline)
            {
                Offline.Add(offline.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal));
                return (null, null);
            }

            if (OnlineStatement().Match(text) is { Success: true } online)
            {
                var name = online.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (FailOnlineWith is { } number)
                {
                    FailOnlineWith = null;
                    throw new ScriptedSqlException(number, $"Simulated error {number} bringing {name} online.");
                }

                foreach (var file in _databases[name].Files)
                {
                    OnlineFiles[file] = readFile(file);
                }

                Offline.Remove(name);
                return (null, null);
            }

            if (SuspendStatement().Match(text) is { Success: true } suspend)
            {
                var name = suspend.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal);
                if (suspend.Groups["on"].Value == "OFF")
                {
                    Suspended.Remove(name);
                }
                else
                {
                    Assert.True(Suspended.Add(name), $"{name} is suspended twice");
                    SuspendedCopyOnly[name] = suspend.Groups["on"].Value.Contains("COPY_ONLY", StringComparison.Ordinal);
                }

                return (null, null);
            }

            if (text == "BACKUP DATABASE @database TO DISK = @path WITH COPY_ONLY, CHECKSUM, INIT, FORMAT, NAME = @name, DESCRIPTION = @description")
            {
                // Express: no COMPRESSION. The "backup" is the database's files as SQL Server read them at the attach.
                var database = (string)parameters["@database"]!;
                var path = (string)parameters["@path"]!;
                Assert.False(File.Exists(path), "a backup file was overwritten");
                BackupSetNames[path] = (string)parameters["@name"]!;
                using (var file = File.Create(path))
                {
                    file.Write(System.Text.Encoding.ASCII.GetBytes($"TAPE {database};"));
                    foreach (var name in _databases[database].Files)
                    {
                        file.Write(AttachedFiles[name]);
                    }
                }

                Backups[path] = database;
                return (null, null);
            }

            if (text.StartsWith("BACKUP DATABASE @database TO DISK = @path WITH METADATA_ONLY, ", StringComparison.Ordinal))
            {
                var database = (string)parameters["@database"]!;
                Assert.True(Suspended.Remove(database), $"{database} was not suspended when it was recorded");
                var path = (string)parameters["@path"]!;
                var position = text.Contains("FORMAT, INIT", StringComparison.Ordinal) ? 1 : SnapshotBackups.Count(b => b.Name == (string)parameters["@name"]!) + 1;
                SnapshotBackups.Add((database, (string)parameters["@name"]!, SuspendedCopyOnly[database], position));
                if (!SuspendedCopyOnly[database])
                {
                    LogChains.Add(database);
                    _logStart.TryAdd(database, 41000000012300001m);
                }

                File.AppendAllText(path, $"TAPE {database};");
                return (null, null);
            }

            if (Lane(text, parameters) is { } lane)
            {
                return lane;
            }

            if (Restore(text, parameters) is { } restore)
            {
                return restore;
            }

            if (DropStatement().Match(text) is { Success: true } drop)
            {
                if (FailDrops > 0)
                {
                    FailDrops--;
                    throw new ScriptedSqlException(6005, "SHUTDOWN is in progress.");
                }

                Assert.True(_databases.Remove(drop.Groups["name"].Value.Replace("]]", "]", StringComparison.Ordinal)));
                return (null, null);
            }

            throw new NotSupportedException($"The scripted SQL Server does not know: {text}");
        }
    }

    /// <summary>
    /// The statements of the agent's backup lane (log and full backups). LSNs grow per backup; a log backup starts where
    /// the previous one (or the full that started the chain) ended, as SQL Server does.
    /// </summary>
    private (object? Scalar, DataTable? Table)? Lane(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        string Text(string name) => (string)parameters[name]!;
        string Lsn(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (text)
        {
            case SqlBackupLane.InstanceQuery:
            {
                var table = Table(("version", typeof(string)), ("edition", typeof(int)), ("backup", typeof(string)));
                table.Rows.Add(ProductVersion, 4, @"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup\");
                return (null, table);
            }

            case SqlBackupLane.DatabasesQuery:
            {
                var table = Table(
                    ("name", typeof(string)), ("id", typeof(int)), ("state", typeof(string)), ("ro", typeof(bool)), ("snapshot", typeof(bool)), ("replica", typeof(bool)),
                    ("model", typeof(string)), ("chain", typeof(bool)), ("family", typeof(string)), ("database", typeof(string)), ("fork", typeof(string)));
                foreach (var (name, database) in _databases.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var family = FamilyGuid(name).ToString();
                    table.Rows.Add(name, database.Id, Offline.Contains(name) ? "OFFLINE" : "ONLINE", false, false, false, SimpleRecovery.Contains(name) ? "SIMPLE" : "FULL",
                        LogChains.Contains(name), family, DatabaseGuid(name).ToString(), family);
                }

                return (null, table);
            }

            case SqlBackupLane.ForeignQuery:
            {
                var table = BackupSetTable(("device", typeof(string)));
                table.Columns.Add("database_name", typeof(string)).SetOrdinal(0);
                foreach (var (database, row, device) in ForeignBackups)
                {
                    table.Rows.Add([database, .. row, device]);
                }

                return (null, table);
            }

            case SqlBackupLane.BackupSetByDeviceQuery:
            {
                var table = BackupSetTable();
                if (_laneSets.TryGetValue(Text("@path"), out var row))
                {
                    table.Rows.Add(row);
                }

                return (null, table);
            }

            case SqlBackupLane.AllocatedQuery:
                return (8L << 20, null);

            case SqlBackupLane.FilesQuery:
            {
                var table = Table(("id", typeof(int)), ("name", typeof(string)), ("path", typeof(string)), ("type", typeof(string)), ("size", typeof(long)));
                var id = 1;
                foreach (var file in _databases[Text("@database")].Files)
                {
                    var log = file.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase);
                    table.Rows.Add(log ? 2 : id == 2 ? 3 : id, Path.GetFileNameWithoutExtension(file.Replace('\\', '/')), file, log ? "LOG" : "ROWS", 8L << 20);
                    id++;
                }

                return (null, table);
            }

            case "BACKUP LOG @database TO DISK = @path WITH FORMAT, INIT, CHECKSUM, NAME = @name, DESCRIPTION = @description":
            case "BACKUP DATABASE @database TO DISK = @path WITH FORMAT, INIT, CHECKSUM, NAME = @name, DESCRIPTION = @description":
            {
                var database = Text("@database");
                var path = Text("@path");
                var log = text.StartsWith("BACKUP LOG", StringComparison.Ordinal);
                if (log && !LogChains.Contains(database))
                {
                    throw new ScriptedSqlException(4214, "BACKUP LOG cannot be performed because there is no current database backup.");
                }

                decimal first, last;
                if (log)
                {
                    first = _logStart[database];
                    last = _lsn += 100;
                    _logStart[database] = last;
                }
                else
                {
                    first = _lsn += 100;
                    last = first + 3;
                    if (LogChains.Add(database))
                    {
                        _logStart[database] = first;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"TAPE {(log ? "LOG" : "FULL")} {database} {Lsn(first)}-{Lsn(last)};");
                _laneSets[path] = BackupSetRow(database, log ? "L" : "D", 1, false, snapshot: false, Lsn(first), Lsn(last), Lsn(first), Lsn(first), Text("@name"));
                LaneBackups.Add((database, log ? "L" : "D", path));
                return (null, null);
            }
        }

        return null;
    }

    /// <summary>
    /// The statements of a restore to a point in time: the file list of a .bkm ("TAPE a;TAPE b;", by position) or of a
    /// native full ("TAPE FULL a …"), RESTORE DATABASE (METADATA_ONLY onto files in place, or creating them), RESTORE LOG,
    /// RECOVERY and the detach of an overwrite's staging database.
    /// </summary>
    private (object? Scalar, DataTable? Table)? Restore(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        string Text(string name) => (string)parameters[name]!;
        if (text == SqlChainRestorer.FileListQuery)
        {
            var content = File.ReadAllText(Text("@path"));
            var database = content.StartsWith("TAPE FULL ", StringComparison.Ordinal)
                ? content["TAPE FULL ".Length..].Split(' ')[0]
                : content.Split(';', StringSplitOptions.RemoveEmptyEntries)[(int)parameters["@position"]! - 1]["TAPE ".Length..];
            var table = Table(("LogicalName", typeof(string)), ("PhysicalName", typeof(string)), ("Type", typeof(string)));
            foreach (var file in _databases[database].Files)
            {
                var log = file.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase);
                table.Rows.Add(Path.GetFileNameWithoutExtension(file.Replace('\\', '/')), file, log ? "L" : "D");
            }

            return (null, table);
        }

        if (RestoreDatabaseStatement().Match(text) is { Success: true } restore)
        {
            var database = Text("@database");
            Assert.False(_databases.ContainsKey(database), $"{database} exists already");
            var files = Enumerable.Range(0, parameters.Keys.Count(k => k.StartsWith("@physical", StringComparison.Ordinal))).Select(i => Text($"@physical{i}")).ToList();
            _databases[database] = (_nextId++, files);
            Restoring.Add(database);
            Restores.Add((database, restore.Groups["metadata"].Success ? "snapshot" : "full", Text("@path")));
            return (null, null);
        }

        if (text.StartsWith("RESTORE LOG @database FROM DISK = @path WITH ", StringComparison.Ordinal))
        {
            var database = Text("@database");
            Assert.True(Restoring.Contains(database), $"{database} is not restoring");
            Assert.True(File.Exists(Text("@path")), "a log backup that was not downloaded");
            Restores.Add((database, parameters.TryGetValue("@stopat", out var stopAt) ? $"log STOPAT {stopAt}" : "log", File.ReadAllText(Text("@path"))));
            if (text.Contains("RECOVERY", StringComparison.Ordinal) && !text.Contains("NORECOVERY", StringComparison.Ordinal))
            {
                Restoring.Remove(database);
            }

            return (null, null);
        }

        switch (text)
        {
            case "RESTORE DATABASE @database WITH RECOVERY":
                Restoring.Remove(Text("@database"));
                return (null, null);
            case "EXEC sp_detach_db @dbname = @database":
                Assert.True(_databases.Remove(Text("@database")));
                return (null, null);
        }

        return null;
    }

    /// <summary>Databases being restored (NORECOVERY so far).</summary>
    public HashSet<string> Restoring { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What restores applied: database, kind (snapshot, full, log, log STOPAT …) and the file (or its content for logs).</summary>
    public List<(string Database, string Kind, string File)> Restores { get; } = [];

    [GeneratedRegex(@"^RESTORE DATABASE @database FROM DISK = @path WITH (?<metadata>METADATA_ONLY, )?FILE = @position, MOVE @logical0 TO @physical0(, MOVE @logical\d+ TO @physical\d+)*, NORECOVERY$")]
    private static partial Regex RestoreDatabaseStatement();

    /// <summary>Another tool's log backup of <paramref name="database"/> (e.g. a maintenance plan): it takes the log, so ours no longer continues.</summary>
    public void TakeForeignLogBackup(string database, string device)
    {
        lock (_lock)
        {
            var first = _logStart[database];
            var last = _lsn += 100;
            _logStart[database] = last;
            var lsn = (decimal v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var row = BackupSetRow(database, "L", 1, false, snapshot: false, lsn(first), lsn(last), lsn(first), lsn(first), "Maintenance plan log backup");
            row[^1] = "Maintenance plan log backup";
            ForeignBackups.Add((database, row, device));
        }
    }

    /// <summary>Backups the lane took: database, type (D/L), path.</summary>
    public List<(string Database, string Type, string Path)> LaneBackups { get; } = [];

    /// <summary>Other tools' backup sets the lane's msdb query returns: database, row of <see cref="SqlBackupSets.Columns"/>, device.</summary>
    public List<(string Database, object[] Row, string Device)> ForeignBackups { get; } = [];

    /// <summary>Databases in SIMPLE recovery (all others are FULL).</summary>
    public HashSet<string> SimpleRecovery { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, object[]> _laneSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _logStart = new(StringComparer.OrdinalIgnoreCase);
    private decimal _lsn = 41000000012600001m;

    private static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
        {
            table.Columns.Add(name, type);
        }

        return table;
    }

    [GeneratedRegex(@"^SELECT dp\.name, dp\.type_desc FROM \[(?:[^\]]|\]\])+\]\.sys\.database_principals AS dp LEFT JOIN sys\.server_principals AS sp ON sp\.sid = dp\.sid WHERE sp\.sid IS NULL")]
    private static partial Regex OrphanQuery();

    [GeneratedRegex(@"^CREATE DATABASE \[(?<name>(?:[^\]]|\]\])+)\] ON (?<files>.+) FOR ATTACH$")]
    private static partial Regex AttachStatement();

    [GeneratedRegex(@"\(FILENAME = N'((?:[^']|'')*)'\)")]
    private static partial Regex FileName();

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET OFFLINE WITH ROLLBACK IMMEDIATE$")]
    private static partial Regex OfflineStatement();

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET (?<access>SINGLE_USER(?= WITH ROLLBACK IMMEDIATE)|MULTI_USER)( WITH ROLLBACK IMMEDIATE)?$")]
    private static partial Regex UserAccessStatement();

    /// <summary>Databases in single-user mode (an overwrite's last log backup).</summary>
    public HashSet<string> SingleUser { get; } = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET ONLINE$")]
    private static partial Regex OnlineStatement();

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET SUSPEND_FOR_SNAPSHOT_BACKUP = (?<on>ON \(MODE = COPY_ONLY\)|ON|OFF)$")]
    private static partial Regex SuspendStatement();

    [GeneratedRegex(@"^DROP DATABASE \[(?<name>(?:[^\]]|\]\])+)\]$")]
    private static partial Regex DropStatement();

    /// <summary>Has <c>Number</c> like SqlException, which the agent reads to explain the error.</summary>
    internal sealed class ScriptedSqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class Connection(ScriptedSqlEnvironment sql) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "master";

        public override string DataSource => "SQL01";

        public override string ServerVersion => "16.00.4305";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new Command(sql) { Connection = this };
    }

    private sealed class Command(ScriptedSqlEnvironment sql) : DbCommand
    {
        private readonly ParameterList _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => _parameters;

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery()
        {
            Run();
            return -1;
        }

        public override object? ExecuteScalar() => Run().Scalar;

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => new Parameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            (Run().Table ?? throw new InvalidOperationException($"{CommandText} returns no rows.")).CreateDataReader();

        private (object? Scalar, DataTable? Table) Run() =>
            sql.Execute(CommandText, _parameters.Items.ToDictionary(p => p.ParameterName, p => p.Value));
    }

    private sealed class Parameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        public override int Size { get; set; }

        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        public override bool SourceColumnNullMapping { get; set; }

        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class ParameterList : DbParameterCollection
    {
        public List<DbParameter> Items { get; } = [];

        public override int Count => Items.Count;

        public override object SyncRoot => Items;

        public override int Add(object value)
        {
            Items.Add((DbParameter)value);
            return Items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value!);
            }
        }

        public override void Clear() => Items.Clear();

        public override bool Contains(object value) => Items.Contains((DbParameter)value);

        public override bool Contains(string value) => IndexOf(value) >= 0;

        public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

        public override IEnumerator GetEnumerator() => Items.GetEnumerator();

        public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);

        public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);

        public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);

        public override void Remove(object value) => Items.Remove((DbParameter)value);

        public override void RemoveAt(int index) => Items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));

        protected override DbParameter GetParameter(int index) => Items[index];

        protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];

        protected override void SetParameter(int index, DbParameter value) => Items[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
    }
}
