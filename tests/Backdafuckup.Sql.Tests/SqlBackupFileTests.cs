using System.Data.Common;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

/// <summary>The agent's .bak export against a real SQL Server: copy-only with checksums, verified, never over an existing file.</summary>
[Trait("Category", "Sql")]
public sealed class SqlBackupFileTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string Folder = "/var/opt/mssql/data";
    private static readonly SqlAccount Sa = new() { Kind = SqlAccountKinds.Sql, UserName = "sa" };

    [Fact]
    public async Task Backs_up_copy_only_with_checksums_verifies_and_never_replaces_a_file()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE bak_src; ALTER DATABASE bak_src SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE bak_src; CREATE TABLE t (id int); INSERT t VALUES (1), (2), (3);");
        var environment = new ContainerSql(sql);
        var log = new List<string>();
        bool Exists(string path) => sql.FileExistsAsync(path).GetAwaiter().GetResult();
        long Size(string path) => sql.FileSizeAsync(path).GetAwaiter().GetResult();

        var (path, bytes) = SqlBackupFile.Backup(environment, "MSSQLSERVER", Sa, "bak_src", null, "bak_src_20261104_2158.bak", "test export", log.Add, Exists, Size);

        Assert.Equal($"{Folder}/bak_src_20261104_2158.bak", path);
        Assert.True(bytes > 0);
        Assert.Contains(log, l => l.StartsWith("backing up [bak_src] to /var/opt/mssql/data/bak_src_20261104_2158.bak (copy-only, with checksums, compressed)", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("RESTORE VERIFYONLY with checksums passed", StringComparison.Ordinal));
        Assert.Equal("1:1:1:test export", await ScalarAsync(
            "SELECT TOP (1) CONCAT(s.is_copy_only, ':', s.has_backup_checksums, ':', CASE WHEN s.compressed_backup_size < s.backup_size THEN 1 ELSE 0 END, ':', s.description) " +
            $"FROM msdb.dbo.backupset s JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = s.media_set_id WHERE m.physical_device_name = N'{path}' ORDER BY s.backup_set_id DESC"));

        // The same name again: a new file beside it, the first one stays.
        var (second, _) = SqlBackupFile.Backup(environment, "MSSQLSERVER", Sa, "bak_src", Folder, "bak_src_20261104_2158.bak", "test export", log.Add, Exists, Size, "shop (backdafuckup export)");
        Assert.Equal($"{Folder}/bak_src_20261104_2158_2.bak", second);
        Assert.Equal(bytes, Size(path));
        Assert.Equal("bak_src (backdafuckup export)|shop (backdafuckup export)", await ScalarAsync(
            "SELECT STRING_AGG(s.name, '|') WITHIN GROUP (ORDER BY s.backup_set_id) FROM msdb.dbo.backupset s JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = s.media_set_id " +
            $"WHERE m.physical_device_name IN (N'{path}', N'{second}')"));

        // The backup restores: as a new database, with the rows.
        await sql.ExecuteAsync(
            $"RESTORE DATABASE bak_check FROM DISK = N'{second}' WITH MOVE N'bak_src' TO N'{Folder}/bak_check.mdf', MOVE N'bak_src_log' TO N'{Folder}/bak_check_log.ldf', CHECKSUM;");
        Assert.Equal("3", await ScalarAsync("SELECT CAST(COUNT(*) AS varchar) FROM bak_check.dbo.t"));

        // A folder SQL Server cannot write to is explained.
        var error = Assert.Throws<InvalidOperationException>(
            () => SqlBackupFile.Backup(environment, "MSSQLSERVER", Sa, "bak_src", "/nowhere", "x.bak", "test export", log.Add, Exists, Size));
        Assert.Contains("Backing up [bak_src] to /nowhere/x.bak failed", error.Message, StringComparison.Ordinal);
        Assert.Contains("3201", error.Message, StringComparison.Ordinal);
    }

    private async Task<string?> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private sealed class ContainerSql(SqlServerFixture sql) : ISqlEnvironment
    {
        public List<SqlInstanceInfo> Instances() => [new() { Name = "MSSQLSERVER", ServiceName = "MSSQLSERVER", ServiceState = "Running" }];

        public List<SqlVolumeInfo> Volumes() => [];

        public DbConnection Open(string instance, SqlAccount account)
        {
            var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
            connection.Open();
            return connection;
        }
    }
}
