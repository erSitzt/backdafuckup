using System.Data.Common;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// The agent's T-SQL snapshot backup against a real SQL Server 2022: suspend database by database (also a closed
/// AUTO_CLOSE database), reads go on and writes wait, the record in msdb with the .bkm, and resuming without a record.
/// </summary>
[Trait("Category", "Sql")]
public sealed class SqlSnapshotTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string Folder = "/var/opt/mssql/data";
    private static readonly SqlAccount Sa = new() { Kind = SqlAccountKinds.Sql, UserName = "sa" };

    [Fact]
    public async Task Suspends_every_writable_user_database_and_records_the_snapshot_in_msdb()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await CreateDatabasesAsync("rec");
        var environment = new ContainerSql(sql);
        var log = new List<string>();
        var deleted = new List<string>();
        using var suspend = new SqlSnapshotSuspend(environment, Sa, log.Add, path => sql.ReadFileAsync(path).GetAwaiter().GetResult(), path =>
        {
            deleted.Add(path);
            sql.DeleteFileAsync(path).GetAwaiter().GetResult();
        });

        var progress = suspend.Suspend(environment.Instances());

        var instance = Assert.Single(progress.Instances);
        Assert.Null(instance.Error);
        Assert.StartsWith("16.", instance.ProductVersion, StringComparison.Ordinal);
        Assert.Equal(SqlSnapshotStates.Suspended, State(instance, "rec_a"));
        Assert.Equal((SqlSnapshotStates.Suspended, (string?)null), (State(instance, "rec_closed"), Reason(instance, "rec_closed")));
        Assert.Equal((SqlSnapshotStates.Skipped, "the database is read-only (nothing to suspend)"), (State(instance, "rec_ro"), Reason(instance, "rec_ro")));
        Assert.Equal((SqlSnapshotStates.Skipped, "the database is offline"), (State(instance, "rec_off"), Reason(instance, "rec_off")));
        Assert.Equal(["rec_a", "rec_closed"], await SuspendedAsync("rec_"));

        // Reads go on, writes wait (the client gives up; SQL Server commits the write after the resume).
        Assert.Equal(3, await ScalarAsync("SELECT COUNT(*) FROM rec_a.dbo.t", 10));
        await Assert.ThrowsAsync<SqlException>(() => ScalarAsync("INSERT rec_a.dbo.t VALUES (4); SELECT 1", 2));

        var result = suspend.Record("backdafuckup restore point test", Guid.Parse("00000000-0000-0000-0000-00000000abcd"));

        var recorded = Assert.Single(result);
        Assert.Equal(SqlSnapshotStates.Recorded, State(recorded, "rec_a"));
        Assert.Equal(SqlSnapshotStates.Recorded, State(recorded, "rec_closed"));
        Assert.Equal($"{Folder}/backdafuckup-0000000000000000000000000000abcd-MSSQLSERVER.bkm", recorded.MetadataPath);
        Assert.Equal("TAPE"u8.ToArray(), recorded.Metadata![..4]);
        Assert.Equal([recorded.MetadataPath!], deleted);
        Assert.Empty(await SuspendedAsync("rec_"));
        Assert.True(suspend.SuspendedMs >= 2000, $"suspended for {suspend.SuspendedMs} ms");
        // The container is shared by the tests of this class: other user databases count too.
        Assert.Contains(log, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^MSSQLSERVER: recorded (\d+) of \1 database\(s\) in msdb \(copy-only, "));

        // msdb: a copy-only snapshot backup per recorded database, named after the restore point.
        var history = await RowsAsync(
            "SELECT database_name + ':' + type + ':' + CAST(is_copy_only AS varchar) + ':' + CAST(is_snapshot AS varchar) FROM msdb.dbo.backupset " +
            "WHERE name = N'backdafuckup restore point test' AND database_name LIKE N'rec[_]%' ORDER BY database_name");
        Assert.Equal(["rec_a:D:1:1", "rec_closed:D:1:1"], history);
        Assert.Equal(5, await ScalarAsync("INSERT rec_a.dbo.t VALUES (5); SELECT COUNT(*) FROM rec_a.dbo.t", 30));
    }

    [Fact]
    public async Task Resumes_without_a_record_when_there_is_no_snapshot_and_when_the_connection_closes()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await CreateDatabasesAsync("res");
        var environment = new ContainerSql(sql);
        var log = new List<string>();

        using (var suspend = new SqlSnapshotSuspend(environment, Sa, log.Add))
        {
            suspend.Suspend(environment.Instances());
            Assert.Equal(["res_a", "res_closed"], await SuspendedAsync("res_"));
            var result = suspend.Resume("the VM snapshot failed");
            Assert.All(Single(result).Databases.Where(d => d.Name is "res_a" or "res_closed"), d => Assert.Equal((SqlSnapshotStates.Failed, "the VM snapshot failed"), (d.State, d.Reason)));
            Assert.Empty(await SuspendedAsync("res_"));
        }

        // The dead-man switch: the agent dies (its connection closes) while SQL Server is suspended.
        using (var suspend = new SqlSnapshotSuspend(environment, Sa, log.Add))
        {
            suspend.Suspend(environment.Instances());
            Assert.Equal(["res_a", "res_closed"], await SuspendedAsync("res_"));
        }

        for (var i = 0; i < 50 && (await SuspendedAsync("res_")).Count > 0; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Empty(await SuspendedAsync("res_"));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM msdb.dbo.backupset WHERE database_name LIKE N'res[_]%'", 30));
        Assert.Equal(4, await ScalarAsync("INSERT res_a.dbo.t VALUES (4); SELECT COUNT(*) FROM res_a.dbo.t", 30));
    }

    private static SqlSnapshotInstance Single(List<SqlSnapshotInstance> instances) => Assert.Single(instances);

    private static string State(SqlSnapshotInstance instance, string database) => instance.Databases.Single(d => d.Name == database).State;

    private static string? Reason(SqlSnapshotInstance instance, string database) => instance.Databases.Single(d => d.Name == database).Reason;

    /// <summary>
    /// Four user databases: a writable one with rows, one closed by AUTO_CLOSE, a read-only and an offline one.
    /// </summary>
    private async Task CreateDatabasesAsync(string prefix)
    {
        await sql.ExecuteAsync(
            $"CREATE DATABASE {prefix}_a; CREATE DATABASE {prefix}_closed; CREATE DATABASE {prefix}_ro; CREATE DATABASE {prefix}_off;");
        await sql.ExecuteAsync($"USE {prefix}_a; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2), (3);");
        await sql.ExecuteAsync(
            $"ALTER DATABASE {prefix}_closed SET AUTO_CLOSE ON; ALTER DATABASE {prefix}_ro SET READ_ONLY; ALTER DATABASE {prefix}_off SET OFFLINE WITH ROLLBACK IMMEDIATE;");
    }

    private async Task<List<string>> SuspendedAsync(string prefix) =>
        await RowsAsync($"SELECT db_name FROM sys.dm_server_suspend_status WHERE db_name LIKE N'{prefix}%' ORDER BY db_name");

    private async Task<int?> ScalarAsync(string text, int timeoutSeconds)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = timeoutSeconds;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> RowsAsync(string text)
    {
        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>The container as the agent's SQL environment: one default instance, unpooled connections as sa (like the agent).</summary>
    private sealed class ContainerSql(SqlServerFixture sql) : ISqlEnvironment
    {
        public List<SqlInstanceInfo> Instances() =>
            [new() { Name = "MSSQLSERVER", ServiceName = "MSSQLSERVER", ServiceState = "Running", BackupFolder = Folder }];

        public List<SqlVolumeInfo> Volumes() => [];

        public DbConnection Open(string instance, SqlAccount account)
        {
            var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
            connection.Open();
            return connection;
        }
    }
}
