using System.Data.Common;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

/// <summary>The agent's attach, description and drop against a real SQL Server (the statements, quoting and error numbers).</summary>
[Trait("Category", "Sql")]
public sealed class SqlAttachTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string Folder = "/var/opt/mssql/data";
    private static readonly SqlAccount Sa = new() { Kind = SqlAccountKinds.Sql, UserName = "sa" };

    [Fact]
    public async Task Detached_files_are_attached_described_and_dropped_without_touching_them()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");

        // A database with a user whose login is gone and TRUSTWORTHY on, detached: its files are what a restore writes.
        await sql.ExecuteAsync(
            $"CREATE DATABASE att_src ON (NAME = att_src, FILENAME = '{Folder}/att_src.mdf', SIZE = 8MB) LOG ON (NAME = att_src_log, FILENAME = '{Folder}/att_src_log.ldf', SIZE = 4MB); " +
            "CREATE LOGIN shop_app WITH PASSWORD = N'App-Pa55word!', CHECK_POLICY = OFF;");
        await sql.ExecuteAsync("USE att_src; CREATE USER shop_app FOR LOGIN shop_app; CREATE TABLE t (id int); INSERT t VALUES (42);");
        await sql.ExecuteAsync("USE master; ALTER DATABASE att_src SET TRUSTWORTHY ON; EXEC sp_detach_db 'att_src'; DROP LOGIN shop_app;");
        var environment = new ContainerSql(sql);
        string[] files = [$"{Folder}/att_src.mdf", $"{Folder}/att_src_log.ldf"];
        var log = new List<string>();
        var granted = new List<string>();
        var attached = false;

        // A name with "]" in it, to see the quoting in CREATE DATABASE and in the three-part name of the description.
        const string name = "att]restored";
        SqlAttach.AttachFiles(environment, "MSSQLSERVER", Sa, name, files, (path, _) => granted.Add(path), "test", log.Add, () => attached = true, describe: true);

        Assert.True(attached);
        Assert.Equal(files, granted);
        Assert.Contains("attached database [att]]restored] to instance MSSQLSERVER (ONLINE), as sa.", log);
        Assert.Contains(log, l => l.Contains("owner sa, TRUSTWORTHY off (SQL Server turns it off when attaching)", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("users in [att]]restored] without a login on instance MSSQLSERVER: shop_app (sql user).", StringComparison.Ordinal));
        Assert.Equal(42, await ScalarAsync("SELECT id FROM [att]]restored].dbo.t"));

        // The drop refuses a folder that does not hold the files, and leaves the files alone (offline first).
        Assert.Throws<InvalidOperationException>(() => SqlPublishDatabase.Drop(environment, "MSSQLSERVER", Sa, name, "/var/opt/mssql/dat", log.Add));
        SqlPublishDatabase.Drop(environment, "MSSQLSERVER", Sa, name, Folder, log.Add);
        Assert.Contains(log, l => l.StartsWith("removed database [att]]restored] from instance MSSQLSERVER", StringComparison.Ordinal));
        Assert.Null(await ScalarAsync("SELECT DB_ID(N'att]restored')"));

        SqlAttach.AttachFiles(environment, "MSSQLSERVER", Sa, "att_again", files, (_, _) => { }, "test", log.Add, () => { }, describe: false);
        Assert.Equal(42, await ScalarAsync("SELECT id FROM att_again.dbo.t"));

        // A taken name is refused before anything happens; a missing file is explained with SQL Server's error.
        var taken = Assert.Throws<InvalidOperationException>(() => SqlAttach.AttachFiles(environment, "MSSQLSERVER", Sa, "att_again", files, (_, _) => { }, "test", log.Add, () => { }, describe: false));
        Assert.Contains("has a database [att_again] already", taken.Message, StringComparison.Ordinal);
        var missing = Assert.Throws<InvalidOperationException>(
            () => SqlAttach.AttachFiles(environment, "MSSQLSERVER", Sa, "att_missing", [$"{Folder}/att_nothing.mdf"], (_, _) => { }, "test", log.Add, () => attached = false, describe: false));
        Assert.Matches("SQL Server error (5120|5133|5110|1813)", missing.Message);
        Assert.Null(await ScalarAsync("SELECT DB_ID(N'att_missing')"));

        await sql.ExecuteAsync("DROP DATABASE att_again;");
    }

    private async Task<object?> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>The container as the agent's SQL environment: one running default instance, connections as sa.</summary>
    [Fact]
    public async Task A_dbcreator_login_that_attaches_a_database_cannot_open_it_and_is_told_why()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");

        // Files of a database that sa owns (dbo has the SID 0x01), attached by a login that is only dbcreator.
        await sql.ExecuteAsync(
            $"CREATE DATABASE own_src ON (NAME = own_src, FILENAME = '{Folder}/own_src.mdf', SIZE = 8MB) LOG ON (NAME = own_src_log, FILENAME = '{Folder}/own_src_log.ldf', SIZE = 4MB);");
        await sql.ExecuteAsync("USE own_src; CREATE TABLE t (id int); INSERT t VALUES (7);");
        await sql.ExecuteAsync(
            "USE master; EXEC sp_detach_db 'own_src'; CREATE LOGIN bdfu_creator WITH PASSWORD = N'Creator-Pa55word!', CHECK_POLICY = OFF; ALTER SERVER ROLE dbcreator ADD MEMBER bdfu_creator;");
        var account = new SqlAccount { Kind = SqlAccountKinds.Sql, UserName = "bdfu_creator" };
        var environment = new ContainerSql(sql, sql.ConnectionStringFor("bdfu_creator", "Creator-Pa55word!"));
        var log = new List<string>();

        SqlAttach.AttachFiles(environment, "MSSQLSERVER", account, "own_restored", [$"{Folder}/own_src.mdf", $"{Folder}/own_src_log.ldf"], (_, _) => { }, "test", log.Add, () => { }, describe: true);

        // Attached, owned by the login in master, but dbo inside is still sa: the login cannot open it, a sysadmin can.
        Assert.True(log.Any(l => l.StartsWith("bdfu_creator cannot open [own_restored] itself: SQL Server made it the owner", StringComparison.Ordinal)
            && l.Contains("ALTER AUTHORIZATION ON DATABASE::[own_restored] TO [bdfu_creator]", StringComparison.Ordinal)), string.Join("\n", log));
        Assert.DoesNotContain(log, l => l.StartsWith("the attached database could not be described", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("database [own_restored]: owner bdfu_creator,", StringComparison.Ordinal));
        Assert.Equal("sa", await ScalarTextAsync("SELECT SUSER_SNAME(sid) FROM own_restored.sys.database_principals WHERE name = 'dbo'"));
        Assert.Equal(7, await ScalarIntAsync("SELECT id FROM own_restored.dbo.t"));
        await using (var connection = new SqlConnection(sql.ConnectionStringFor("bdfu_creator", "Creator-Pa55word!")))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM own_restored.dbo.t";
            await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        // After the sysadmin's ALTER AUTHORIZATION the login can open it.
        await sql.ExecuteAsync("ALTER AUTHORIZATION ON DATABASE::[own_restored] TO [bdfu_creator];");
        await using (var connection = new SqlConnection(sql.ConnectionStringFor("bdfu_creator", "Creator-Pa55word!")))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM own_restored.dbo.t";
            Assert.Equal(7, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        // As sa (sysadmin) the account can open what it attached.
        await sql.ExecuteAsync("USE master; EXEC sp_detach_db 'own_restored';");
        log.Clear();
        SqlAttach.AttachFiles(new ContainerSql(sql), "MSSQLSERVER", Sa, "own_again", [$"{Folder}/own_src.mdf", $"{Folder}/own_src_log.ldf"], (_, _) => { }, "test", log.Add, () => { }, describe: false);
        Assert.DoesNotContain(log, l => l.Contains("cannot open", StringComparison.Ordinal));
        await sql.ExecuteAsync("DROP DATABASE own_again;");
    }

    private async Task<int?> ScalarIntAsync(string text)
    {
        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync() as int?;
    }

    private async Task<string?> ScalarTextAsync(string text)
    {
        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return await command.ExecuteScalarAsync() as string;
    }

    private sealed class ContainerSql(SqlServerFixture sql, string? connectionString = null) : ISqlEnvironment
    {
        public List<SqlInstanceInfo> Instances() => [new() { Name = "MSSQLSERVER", ServiceName = "MSSQLSERVER", ServiceState = "Running" }];

        public List<SqlVolumeInfo> Volumes() => [];

        public DbConnection Open(string instance, SqlAccount account)
        {
            var connection = new SqlConnection(connectionString ?? sql.AdminConnectionString);
            connection.Open();
            return connection;
        }
    }
}
