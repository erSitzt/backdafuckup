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

    /// <summary>SERVERPROPERTY('ProductVersion') of the instance (SQL Server 2022 Express by default).</summary>
    public string ProductVersion { get; set; } = "16.0.4305.5";

    /// <summary>The instance's backup folder (registry BackupDirectory): where BACKUP … METADATA_ONLY writes the .bkm.</summary>
    public string? BackupFolder { get; set; }

    /// <summary>The databases whose writes are suspended for a snapshot right now.</summary>
    public HashSet<string> Suspended { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    private (object? Scalar, DataTable? Table) Execute(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        lock (_lock)
        {
            Statements.Add(text);
            string Name() => (string)parameters["@name"]!;
            switch (text)
            {
                case "SELECT DB_ID(@name)":
                case "SELECT database_id FROM sys.databases WHERE name = @name":
                    return (_databases.TryGetValue(Name(), out var found) ? found.Id : DBNull.Value, null);
                case "SELECT state_desc FROM sys.databases WHERE name = @name":
                    return (_databases.ContainsKey(Name()) ? "ONLINE" : null, null);
                case "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(SERVERPROPERTY('EngineEdition') AS int), SUSER_SNAME(), IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator')":
                {
                    var table = Table(("version", typeof(string)), ("edition", typeof(string)), ("engine", typeof(int)), ("login", typeof(string)), ("sysadmin", typeof(int)), ("dbcreator", typeof(int)));
                    table.Rows.Add(ProductVersion, "Express Edition (64-bit)", 4, Accounts[^1].UserName, 0, 1);
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

                case "SELECT name, state_desc, is_read_only, CAST(CASE WHEN source_database_id IS NULL THEN 0 ELSE 1 END AS bit) FROM sys.databases WHERE database_id > 4 ORDER BY name":
                {
                    var table = Table(("name", typeof(string)), ("state", typeof(string)), ("ro", typeof(bool)), ("snapshot", typeof(bool)));
                    foreach (var name in _databases.Keys.Order(StringComparer.OrdinalIgnoreCase))
                    {
                        table.Rows.Add(name, "ONLINE", false, false);
                    }

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

            if (OfflineStatement().Match(text) is { Success: true })
            {
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
                }

                return (null, null);
            }

            if (text.StartsWith("BACKUP DATABASE @database TO DISK = @path WITH METADATA_ONLY, ", StringComparison.Ordinal))
            {
                var database = (string)parameters["@database"]!;
                Assert.True(Suspended.Remove(database), $"{database} was not suspended when it was recorded");
                File.AppendAllText((string)parameters["@path"]!, $"TAPE {database};");
                return (null, null);
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

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET SUSPEND_FOR_SNAPSHOT_BACKUP = (?<on>ON \(MODE = COPY_ONLY\)|OFF)$")]
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
