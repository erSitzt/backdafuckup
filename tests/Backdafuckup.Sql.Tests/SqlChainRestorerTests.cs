using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// The agent's restore to a point in time against a real SQL Server 2022: from the SQL Server snapshot's .bkm onto the
/// files in place, from a native full backup, and a chain with a gap that is cleaned up.
/// </summary>
[Trait("Category", "Sql")]
public sealed class SqlChainRestorerTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string F = Spike.Folder;
    private const string Skip = "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.";

    [Fact]
    public async Task A_snapshot_base_and_log_backups_restore_to_the_second()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, Skip);
        var (point, l1, l2) = await PrepareAsync("rs");
        var (database, bkm) = Snapshot;

        var log = await RestoreAsync("rs_restored", new SqlChainRestoreRequest
        {
            Metadata = await sql.ReadFileAsync(bkm),
            Position = database.Position!.Value,
            Logs = [l1, l2],
            StopAt = point,
            Moves =
            [
                new() { OriginalPath = $"{F}/rs_main.mdf", TargetPath = $"{F}/rs_s_main.mdf" },
                new() { OriginalPath = $"{F}/rs_journal.ldf", TargetPath = $"{F}/rs_s_journal.ldf" },
            ],
        });

        Assert.Equal(5, await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM rs_restored.dbo.t"));
        Assert.Equal("ONLINE", await Spike.ScalarAsync(sql, "SELECT state_desc FROM sys.databases WHERE name = N'rs_restored'"));
        Assert.Contains(log, l => l.Contains("restored the SQL Server snapshot as [rs_restored]", StringComparison.Ordinal) && l.Contains("rs_main → /var/opt/mssql/data/rs_s_main.mdf", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains($"applied 2 log backup(s)", StringComparison.Ordinal) && l.Contains($"at {point}", StringComparison.Ordinal));
        Assert.Empty(await sql.ListFilesAsync($"{F}/{SqlBackupLane.NamePrefix}"));
    }

    [Fact]
    public async Task A_native_full_and_log_backups_restore_into_new_files()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, Skip);
        await sql.ExecuteAsync("CREATE DATABASE rn; ALTER DATABASE rn SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE rn; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2);");
        await sql.ExecuteAsync($"BACKUP DATABASE rn TO DISK = N'{F}/rn_full.bak' WITH INIT, CHECKSUM;");
        await sql.ExecuteAsync("INSERT rn.dbo.t VALUES (3);");
        await sql.ExecuteAsync($"BACKUP LOG rn TO DISK = N'{F}/rn_l1.trn' WITH INIT, CHECKSUM;");

        await RestoreAsync("rn_restored", new SqlChainRestoreRequest
        {
            BaseFile = File("rn_full.bak"),
            Logs = [File("rn_l1.trn")],
            Moves =
            [
                new() { OriginalPath = $"{F}/rn.mdf", TargetPath = $"{F}/rn_restored.mdf" },
                new() { OriginalPath = $"{F}/rn_log.ldf", TargetPath = $"{F}/rn_restored_log.ldf" },
            ],
        });

        Assert.Equal(3, await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM rn_restored.dbo.t"));
        Assert.True(await sql.FileExistsAsync($"{F}/rn_restored.mdf"));
    }

    [Fact]
    public async Task A_gap_in_the_chain_fails_and_leaves_no_database_and_no_files()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, Skip);
        await sql.ExecuteAsync("CREATE DATABASE rg; ALTER DATABASE rg SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE rg; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1);");
        await sql.ExecuteAsync($"BACKUP DATABASE rg TO DISK = N'{F}/rg_full.bak' WITH INIT;");
        await sql.ExecuteAsync("INSERT rg.dbo.t VALUES (2);");
        await sql.ExecuteAsync($"BACKUP LOG rg TO DISK = N'{F}/rg_l1.trn' WITH INIT;");
        await sql.ExecuteAsync("INSERT rg.dbo.t VALUES (3);");
        await sql.ExecuteAsync($"BACKUP LOG rg TO DISK = N'{F}/rg_l2.trn' WITH INIT;");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RestoreAsync("rg_restored", new SqlChainRestoreRequest
        {
            BaseFile = File("rg_full.bak"),
            Logs = [File("rg_l2.trn")],
            Moves =
            [
                new() { OriginalPath = $"{F}/rg.mdf", TargetPath = $"{F}/rg_restored.mdf" },
                new() { OriginalPath = $"{F}/rg_log.ldf", TargetPath = $"{F}/rg_restored_log.ldf" },
            ],
        }));

        Assert.Contains("the log backups do not continue each other (the chain has a gap)", error.Message, StringComparison.Ordinal);
        Assert.Null(await Spike.ScalarAsync(sql, "SELECT DB_ID(N'rg_restored')"));
        Assert.False(await sql.FileExistsAsync($"{F}/rg_restored.mdf"));
        Assert.False(await sql.FileExistsAsync($"{F}/rg_restored_log.ldf"));
    }

    private (SqlSnapshotDatabase Database, string Bkm) Snapshot { get; set; }

    /// <summary>A database with its own logical names, a snapshot of it, two log backups and a point in time between rows 5 and 6.</summary>
    private async Task<(string Point, SqlChainFile L1, SqlChainFile L2)> PrepareAsync(string name)
    {
        await sql.ExecuteAsync(
            $"CREATE DATABASE {name} ON PRIMARY (NAME = N'{name}_main', FILENAME = N'{F}/{name}_main.mdf') LOG ON (NAME = N'{name}_journal', FILENAME = N'{F}/{name}_journal.ldf'); " +
            $"ALTER DATABASE {name} SET RECOVERY FULL;");
        await sql.ExecuteAsync($"USE {name}; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2), (3);");
        Snapshot = await Spike.SnapshotAsync(sql, name, [($"{name}_main.mdf", $"{name}_s_main.mdf"), ($"{name}_journal.ldf", $"{name}_s_journal.ldf")], $"{name}_snap", copyOnly: false);
        await sql.ExecuteAsync($"INSERT {name}.dbo.t VALUES (4);");
        await sql.ExecuteAsync($"BACKUP LOG {name} TO DISK = N'{F}/{name}_l1.trn' WITH INIT, CHECKSUM;");
        await sql.ExecuteAsync($"INSERT {name}.dbo.t VALUES (5);");
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        var point = (string)(await Spike.ScalarAsync(sql, "SELECT CONVERT(varchar(23), SYSDATETIME(), 126)"))!;
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        await sql.ExecuteAsync($"INSERT {name}.dbo.t VALUES (6);");
        await sql.ExecuteAsync($"BACKUP LOG {name} TO DISK = N'{F}/{name}_l2.trn' WITH INIT, CHECKSUM;");
        return (point, File($"{name}_l1.trn"), File($"{name}_l2.trn"));
    }

    /// <summary>A backup file in the container's data folder; the test's "download" copies it into the staging folder.</summary>
    private static SqlChainFile File(string name) => new() { FileId = name, FileName = name, Length = 0, Sha256 = string.Empty };

    private async Task<List<string>> RestoreAsync(string database, SqlChainRestoreRequest chain)
    {
        var log = new List<string>();
        var restorer = new SqlChainRestorer(
            new Spike.ContainerSql(sql),
            new ContainerFiles(sql),
            async (file, path, _) => await sql.CopyFileAsync($"{F}/{file.FileId}", path),
            (_, _) => { },
            log.Add);
        await restorer.RestoreAsync(
            new SqlJobRequest { Kind = SqlJobKinds.RestoreChain, Instance = "MSSQLSERVER", Account = Spike.Sa, Database = database, Folder = F, Chain = chain },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);
        return log;
    }

    private sealed class ContainerFiles(SqlServerFixture sql) : SqlBackupFiles
    {
        public override Stream OpenRead(string path) => new MemoryStream(sql.ReadFileAsync(path).GetAwaiter().GetResult(), writable: false);

        public override bool Exists(string path) => sql.FileExistsAsync(path).GetAwaiter().GetResult();

        public override void Delete(string path) => sql.DeleteFileAsync(path).GetAwaiter().GetResult();

        public override void CreateDirectory(string path) => sql.CreateDirectoryAsync(path).GetAwaiter().GetResult();

        public override Stream Create(string path) => new Upload(sql, path);

        public override void DeleteDirectory(string path) => sql.DeleteDirectoryAsync(path).GetAwaiter().GetResult();

        /// <summary>Written into the container when disposed.</summary>
        private sealed class Upload(SqlServerFixture sql, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    sql.WriteFileAsync(path, ToArray()).GetAwaiter().GetResult();
                }

                base.Dispose(disposing);
            }
        }
    }
}
