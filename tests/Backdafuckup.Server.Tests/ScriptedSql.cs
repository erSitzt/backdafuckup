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
        new() { Name = "SQLEXPRESS", InstanceId = "MSSQL16.SQLEXPRESS", SetupVersion = "16.0.1000.6", ServiceName = "MSSQL$SQLEXPRESS", ServiceState = "Running", ServiceAccount = @"NT Service\MSSQL$SQLEXPRESS" },
    ];

    public List<SqlVolumeInfo> Volumes() => [new() { Root = @"C:\", FreeBytes = 50L << 30, TotalBytes = 100L << 30 }];

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

    [GeneratedRegex(@"^CREATE DATABASE \[(?<name>(?:[^\]]|\]\])+)\] ON (?<files>.+) FOR ATTACH$")]
    private static partial Regex AttachStatement();

    [GeneratedRegex(@"\(FILENAME = N'((?:[^']|'')*)'\)")]
    private static partial Regex FileName();

    [GeneratedRegex(@"^ALTER DATABASE \[(?<name>(?:[^\]]|\]\])+)\] SET OFFLINE WITH ROLLBACK IMMEDIATE$")]
    private static partial Regex OfflineStatement();

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
