using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// Spikes for log backups and point-in-time restore (spec 2026-11-05-1932, task 2). They record what SQL Server does
/// into the file named by BDFU_SPIKE_OUT; run with BDFU_SQL_TESTS=1 and BDFU_SQL_SPIKES=1.
/// </summary>
internal static class Spike
{
    public const string Folder = "/var/opt/mssql/data";
    public static readonly SqlAccount Sa = new() { Kind = SqlAccountKinds.Sql, UserName = "sa" };
    private static readonly object Lock = new();

    public static bool Enabled => SqlServerFixture.Enabled && Environment.GetEnvironmentVariable("BDFU_SQL_SPIKES") == "1";

    public const string SkipReason = "Spikes run only with BDFU_SQL_TESTS=1 and BDFU_SQL_SPIKES=1.";

    public static void Out(string spike, string line)
    {
        var path = Environment.GetEnvironmentVariable("BDFU_SPIKE_OUT") ?? Path.Combine(Path.GetTempPath(), "bdfu-spikes.txt");
        lock (Lock)
        {
            File.AppendAllText(path, $"[{spike}] {line}{Environment.NewLine}");
        }
    }

    public static async Task<List<Dictionary<string, object?>>> QueryAsync(SqlServerFixture sql, string text, int timeoutSeconds = 120)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = timeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task DumpAsync(SqlServerFixture sql, string spike, string title, string text, params string[] columns)
    {
        try
        {
            var rows = await QueryAsync(sql, text);
            Out(spike, $"{title}: {rows.Count} row(s)");
            foreach (var row in rows)
            {
                var keys = columns.Length == 0 ? row.Keys.ToArray() : columns;
                Out(spike, "  " + string.Join(", ", keys.Select(k => $"{k}={Format(row.GetValueOrDefault(k))}")));
            }
        }
        catch (SqlException e)
        {
            Out(spike, $"{title}: error {e.Number}: {e.Message}");
        }
    }

    /// <summary>Runs a statement and records success or the error number; returns the error number (0 = success).</summary>
    public static async Task<int> TryAsync(SqlServerFixture sql, string spike, string title, string text, int timeoutSeconds = 300)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = text;
            command.CommandTimeout = timeoutSeconds;
            await command.ExecuteNonQueryAsync();
            Out(spike, $"{title}: ok ({watch.ElapsedMilliseconds} ms)");
            return 0;
        }
        catch (SqlException e)
        {
            Out(spike, $"{title}: error {e.Number} after {watch.ElapsedMilliseconds} ms: {e.Message.Replace(Environment.NewLine, " ", StringComparison.Ordinal)}");
            return e.Number;
        }
    }

    public static async Task<object?> ScalarAsync(SqlServerFixture sql, string text)
    {
        var rows = await QueryAsync(sql, text);
        return rows.Count == 0 ? null : rows[0].Values.First();
    }

    public static string BackupSetColumns =>
        "backup_set_id, backup_set_uuid, type, is_snapshot, is_copy_only, position, first_lsn, last_lsn, checkpoint_lsn, database_backup_lsn, " +
        "differential_base_lsn, family_guid, database_guid, last_recovery_fork_guid, first_recovery_fork_guid, fork_point_lsn, backup_start_date, " +
        "backup_finish_date, time_zone, name, description, backup_size, compressed_backup_size";

    public static Task DumpBackupSetsAsync(SqlServerFixture sql, string spike, string database) =>
        DumpAsync(sql, spike, $"msdb backupset of {database}",
            $"SELECT {BackupSetColumns}, m.physical_device_name FROM msdb.dbo.backupset s JOIN msdb.dbo.backupmediafamily m ON m.media_set_id = s.media_set_id " +
            $"WHERE s.database_name = N'{database}' ORDER BY s.backup_set_id");

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b.Length > 16 ? b[..16] : b),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>The agent's T-SQL snapshot backup with the database's files copied while suspended; returns the .bkm path.</summary>
    public static async Task<(SqlSnapshotDatabase Database, string Bkm)> SnapshotAsync(
        SqlServerFixture sql, string database, IReadOnlyList<(string From, string To)> copies, string bkmName, bool copyOnly)
    {
        var environment = new ContainerSql(sql);
        var log = new List<string>();
        using var suspend = new SqlSnapshotSuspend(
            environment, Sa, log.Add, path => sql.ReadFileAsync(path).GetAwaiter().GetResult(), path => sql.DeleteFileAsync(path).GetAwaiter().GetResult(), copyOnly);
        suspend.Suspend(environment.Instances());
        foreach (var (from, to) in copies)
        {
            await sql.CopyFileAsync($"{Folder}/{from}", $"{Folder}/{to}");
        }

        var instance = Assert.Single(suspend.Record($"backdafuckup {bkmName}", Guid.NewGuid()));
        var recorded = instance.Databases.Single(d => d.Name == database);
        Assert.Equal(SqlSnapshotStates.Recorded, recorded.State);
        var bkm = $"{Folder}/{bkmName}.bkm";
        await sql.WriteFileAsync(bkm, instance.Metadata!);
        return (recorded, bkm);
    }

    public sealed class ContainerSql(SqlServerFixture sql) : ISqlEnvironment
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

[Trait("Category", "Sql")]
public sealed class PointInTimeSpikes2022(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string F = Spike.Folder;

    [Fact]
    public async Task Spike_2a_2b_bkm_file_list_header_and_the_lsn_rules()
    {
        Assert.SkipUnless(Spike.Enabled, Spike.SkipReason);
        const string s = "2a/2b";
        await sql.ExecuteAsync(
            $"CREATE DATABASE spk ON PRIMARY (NAME = N'spk_main', FILENAME = N'{F}/spk_main.mdf'), " +
            $"FILEGROUP fg2 (NAME = N'spk_second', FILENAME = N'{F}/spk_second.ndf') " +
            $"LOG ON (NAME = N'spk_journal', FILENAME = N'{F}/spk_journal.ldf'); ALTER DATABASE spk SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE spk; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2), (3);");
        await Spike.DumpAsync(sql, s, "recovery status before",
            "SELECT d.name, d.recovery_model_desc, r.database_guid, r.family_guid, r.recovery_fork_guid, r.last_log_backup_lsn " +
            "FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name = N'spk'");

        var (database, bkm) = await Spike.SnapshotAsync(
            sql, "spk", [("spk_main.mdf", "spk_s_main.mdf"), ("spk_second.ndf", "spk_s_second.ndf"), ("spk_journal.ldf", "spk_s_journal.ldf")], "spk_snap", copyOnly: false);
        Spike.Out(s, $"snapshot facts: position={database.Position} first={database.FirstLsn} last={database.LastLsn} checkpoint={database.CheckpointLsn} dbbackup={database.DatabaseBackupLsn} started_chain={database.StartedLogChain}");
        await Spike.DumpAsync(sql, s, "FILELISTONLY on .bkm", $"RESTORE FILELISTONLY FROM DISK = N'{bkm}' WITH FILE = {database.Position}",
            "LogicalName", "PhysicalName", "Type", "FileGroupName", "FileId", "Size", "BackupSizeInBytes");
        await Spike.DumpAsync(sql, s, "HEADERONLY on .bkm", $"RESTORE HEADERONLY FROM DISK = N'{bkm}'",
            "DatabaseName", "Position", "BackupType", "IsSnapshot", "IsCopyOnly", "FirstLSN", "LastLSN", "CheckpointLSN", "DatabaseBackupLSN",
            "BackupSetGUID", "FamilyGUID", "RecoveryForkID", "BackupStartDate", "BackupFinishDate", "BackupName");

        await sql.ExecuteAsync("INSERT spk.dbo.t VALUES (4);");
        await sql.ExecuteAsync($"BACKUP LOG spk TO DISK = N'{F}/spk_l1.trn' WITH INIT, CHECKSUM, NAME = N'backdafuckup log';");
        await sql.ExecuteAsync($"BACKUP DATABASE spk TO DISK = N'{F}/spk_native.bak' WITH INIT, CHECKSUM, NAME = N'backdafuckup full';");
        await sql.ExecuteAsync("INSERT spk.dbo.t VALUES (5);");
        await sql.ExecuteAsync($"BACKUP LOG spk TO DISK = N'{F}/spk_l2.trn' WITH INIT, CHECKSUM, NAME = N'backdafuckup log';");
        await sql.ExecuteAsync("INSERT spk.dbo.t VALUES (6);");
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        var point = (string)(await Spike.ScalarAsync(sql, "SELECT CONVERT(varchar(23), SYSDATETIME(), 126)"))!;
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        await sql.ExecuteAsync("INSERT spk.dbo.t VALUES (7);");
        await sql.ExecuteAsync($"BACKUP LOG spk TO DISK = N'{F}/spk_l3.trn' WITH INIT, CHECKSUM, NAME = N'backdafuckup log';");
        Spike.Out(s, $"point in time (local) = {point}");
        await Spike.DumpBackupSetsAsync(sql, s, "spk");
        await Spike.DumpAsync(sql, s, "recovery status after",
            "SELECT r.database_guid, r.family_guid, r.recovery_fork_guid, r.last_log_backup_lsn FROM sys.database_recovery_status r WHERE r.database_id = DB_ID(N'spk')");
        await Spike.DumpAsync(sql, s, "FILELISTONLY on native full", $"RESTORE FILELISTONLY FROM DISK = N'{F}/spk_native.bak'", "LogicalName", "PhysicalName", "Type", "FileId");

        // Snapshot base: does the log right after it apply, and does skipping it fail?
        var moveSnap = $"MOVE N'spk_main' TO N'{F}/spk_s_main.mdf', MOVE N'spk_second' TO N'{F}/spk_s_second.ndf', MOVE N'spk_journal' TO N'{F}/spk_s_journal.ldf'";
        await Spike.TryAsync(sql, s, "snapshot base: RESTORE … METADATA_ONLY NORECOVERY",
            $"RESTORE DATABASE spk_r1 FROM DISK = N'{bkm}' WITH METADATA_ONLY, FILE = {database.Position}, {moveSnap}, NORECOVERY;");
        await Spike.TryAsync(sql, s, "snapshot base: skip l1, apply l2 first", $"RESTORE LOG spk_r1 FROM DISK = N'{F}/spk_l2.trn' WITH NORECOVERY;");
        await Spike.TryAsync(sql, s, "snapshot base: l1", $"RESTORE LOG spk_r1 FROM DISK = N'{F}/spk_l1.trn' WITH NORECOVERY, CHECKSUM;");
        await Spike.TryAsync(sql, s, "snapshot base: l2", $"RESTORE LOG spk_r1 FROM DISK = N'{F}/spk_l2.trn' WITH NORECOVERY, CHECKSUM;");
        await Spike.TryAsync(sql, s, "snapshot base: l3 STOPAT", $"RESTORE LOG spk_r1 FROM DISK = N'{F}/spk_l3.trn' WITH STOPAT = '{point}', RECOVERY, CHECKSUM;");
        Spike.Out(s, $"snapshot base: rows = {await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM spk_r1.dbo.t")} (expected 6)");

        // Native base (taken after l1): l1 should be too early, l2 the first.
        var moveNative = $"MOVE N'spk_main' TO N'{F}/spk_n_main.mdf', MOVE N'spk_second' TO N'{F}/spk_n_second.ndf', MOVE N'spk_journal' TO N'{F}/spk_n_journal.ldf'";
        await Spike.TryAsync(sql, s, "native base: RESTORE NORECOVERY", $"RESTORE DATABASE spk_r2 FROM DISK = N'{F}/spk_native.bak' WITH {moveNative}, NORECOVERY, CHECKSUM;");
        await Spike.TryAsync(sql, s, "native base: l1 (before the full)", $"RESTORE LOG spk_r2 FROM DISK = N'{F}/spk_l1.trn' WITH NORECOVERY;");
        await Spike.TryAsync(sql, s, "native base: l2", $"RESTORE LOG spk_r2 FROM DISK = N'{F}/spk_l2.trn' WITH NORECOVERY, CHECKSUM;");
        await Spike.TryAsync(sql, s, "native base: l3 STOPAT", $"RESTORE LOG spk_r2 FROM DISK = N'{F}/spk_l3.trn' WITH STOPAT = '{point}', RECOVERY, CHECKSUM;");
        Spike.Out(s, $"native base: rows = {await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM spk_r2.dbo.t")} (expected 6)");
        await Spike.DumpAsync(sql, s, "restored databases",
            "SELECT d.name, r.database_guid, r.family_guid, r.recovery_fork_guid, r.last_log_backup_lsn FROM sys.databases d " +
            "JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name LIKE N'spk%' ORDER BY d.name");
        await Spike.DumpAsync(sql, s, "msdb restorehistory", "SELECT destination_database_name, restore_type, stop_at, backup_set_id FROM msdb.dbo.restorehistory WHERE destination_database_name LIKE N'spk%' ORDER BY restore_history_id");

        // After a restore with RECOVERY: does the restored database take log backups at once (new fork)?
        await Spike.TryAsync(sql, s, "log backup of the restored database", $"BACKUP LOG spk_r2 TO DISK = N'{F}/spk_r2_l.trn' WITH INIT;");
        await Spike.DumpBackupSetsAsync(sql, s, "spk_r2");
    }

    [Fact]
    public async Task Spike_2c_restored_files_swapped_under_an_offline_original()
    {
        Assert.SkipUnless(Spike.Enabled, Spike.SkipReason);
        const string s = "2c";
        await sql.ExecuteAsync("CREATE DATABASE swp; ALTER DATABASE swp SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE swp; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2), (3); CREATE USER spike_user WITHOUT LOGIN;");
        await sql.ExecuteAsync($"BACKUP DATABASE swp TO DISK = N'{F}/swp_full.bak' WITH INIT, CHECKSUM;");
        await sql.ExecuteAsync("INSERT swp.dbo.t VALUES (4);");
        await sql.ExecuteAsync($"BACKUP LOG swp TO DISK = N'{F}/swp_1.trn' WITH INIT, CHECKSUM;");
        await sql.ExecuteAsync("INSERT swp.dbo.t VALUES (5);");
        await Spike.DumpAsync(sql, s, "original before", "SELECT d.name, d.owner_sid, d.user_access_desc, d.state_desc, r.database_guid, r.family_guid, r.recovery_fork_guid, r.last_log_backup_lsn FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name = N'swp'");

        // Stage: restore under another name into <original>.bdfu-new, recover, detach.
        await Spike.TryAsync(sql, s, "stage restore",
            $"RESTORE DATABASE swp_stage FROM DISK = N'{F}/swp_full.bak' WITH MOVE N'swp' TO N'{F}/swp.mdf.bdfu-new', MOVE N'swp_log' TO N'{F}/swp_log.ldf.bdfu-new', NORECOVERY;" +
            $"RESTORE LOG swp_stage FROM DISK = N'{F}/swp_1.trn' WITH RECOVERY;");
        await Spike.DumpAsync(sql, s, "stage", "SELECT d.name, r.database_guid, r.family_guid, r.recovery_fork_guid FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name = N'swp_stage'");
        await Spike.TryAsync(sql, s, "detach stage", "EXEC sp_detach_db N'swp_stage';");

        // The final log backup in single-user mode, then offline: all on one connection.
        var finalSteps = new StringBuilder()
            .Append("ALTER DATABASE swp SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ")
            .Append($"BACKUP LOG swp TO DISK = N'{F}/swp_final.trn' WITH INIT, CHECKSUM; ")
            .Append("ALTER DATABASE swp SET OFFLINE WITH ROLLBACK IMMEDIATE;")
            .ToString();
        await Spike.TryAsync(sql, s, "single user + final log backup + offline", finalSteps);
        await Spike.DumpAsync(sql, s, "original offline", "SELECT name, user_access_desc, state_desc FROM sys.databases WHERE name = N'swp'");

        await sql.MoveFileAsync($"{F}/swp.mdf", $"{F}/swp.mdf.bdfu-replaced");
        await sql.MoveFileAsync($"{F}/swp_log.ldf", $"{F}/swp_log.ldf.bdfu-replaced");
        await sql.MoveFileAsync($"{F}/swp.mdf.bdfu-new", $"{F}/swp.mdf");
        await sql.MoveFileAsync($"{F}/swp_log.ldf.bdfu-new", $"{F}/swp_log.ldf");
        var online = await Spike.TryAsync(sql, s, "SET ONLINE with the restored files", "ALTER DATABASE swp SET ONLINE;");
        if (online == 0)
        {
            await Spike.DumpAsync(sql, s, "original after swap", "SELECT d.name, d.owner_sid, d.user_access_desc, d.state_desc, r.database_guid, r.family_guid, r.recovery_fork_guid, r.last_log_backup_lsn FROM sys.databases d JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.name = N'swp'");
            Spike.Out(s, $"rows after swap = {await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM swp.dbo.t")} (restored state: 4)");
            await Spike.TryAsync(sql, s, "MULTI_USER", "ALTER DATABASE swp SET MULTI_USER;");
            await Spike.TryAsync(sql, s, "DBCC CHECKDB", "DBCC CHECKDB (swp) WITH NO_INFOMSGS;");
            await Spike.TryAsync(sql, s, "log backup after swap", $"BACKUP LOG swp TO DISK = N'{F}/swp_after.trn' WITH INIT;");
        }
        else
        {
            // Back to the original files, as the rollback does.
            await sql.MoveFileAsync($"{F}/swp.mdf", $"{F}/swp.mdf.bdfu-new");
            await sql.MoveFileAsync($"{F}/swp_log.ldf", $"{F}/swp_log.ldf.bdfu-new");
            await sql.MoveFileAsync($"{F}/swp.mdf.bdfu-replaced", $"{F}/swp.mdf");
            await sql.MoveFileAsync($"{F}/swp_log.ldf.bdfu-replaced", $"{F}/swp_log.ldf");
            await Spike.TryAsync(sql, s, "rollback: SET ONLINE with the original files", "ALTER DATABASE swp SET ONLINE;");
        }

        await Spike.DumpBackupSetsAsync(sql, s, "swp");
    }

    [Fact]
    public async Task Spike_2d_log_and_full_backups_against_the_snapshot_suspend()
    {
        Assert.SkipUnless(Spike.Enabled, Spike.SkipReason);
        const string s = "2d";
        await sql.ExecuteAsync("CREATE DATABASE cc; ALTER DATABASE cc SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE cc; CREATE TABLE dbo.t (id int IDENTITY, pad char(4000) NOT NULL DEFAULT 'x'); INSERT dbo.t DEFAULT VALUES;");
        for (var i = 0; i < 16; i++)
        {
            await sql.ExecuteAsync("INSERT cc.dbo.t (pad) SELECT pad FROM cc.dbo.t;");
        }

        Spike.Out(s, $"rows = {await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM cc.dbo.t")}");
        await sql.ExecuteAsync($"BACKUP DATABASE cc TO DISK = N'{F}/cc_base.bak' WITH INIT;");

        // 1: a native full is running, then the suspend comes.
        var full = Spike.TryAsync(sql, s, "1: slow native full", $"BACKUP DATABASE cc TO DISK = N'{F}/cc_full.bak' WITH INIT, BUFFERCOUNT = 2, MAXTRANSFERSIZE = 65536;");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await using (var suspend = await OpenAsync())
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON;", 30);
                Spike.Out(s, $"1: suspend during the full: ok after {watch.ElapsedMilliseconds} ms; full finished before: {full.IsCompleted}");
                var finished = await Task.WhenAny(full, Task.Delay(10_000, TestContext.Current.CancellationToken)) == full;
                Spike.Out(s, $"1: full finishes while suspended: {finished}");
                await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = OFF;", 30);
            }
            catch (SqlException e)
            {
                Spike.Out(s, $"1: suspend during the full: error {e.Number} after {watch.ElapsedMilliseconds} ms: {e.Message}");
            }
        }

        await full;

        // 2 and 3: suspended, then a log backup or a native full.
        await sql.ExecuteAsync("UPDATE cc.dbo.t SET pad = 'y' WHERE id % 10 = 0;");
        await using (var suspend = await OpenAsync())
        {
            await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON;", 30);
            await Spike.TryAsync(sql, s, "2: log backup while suspended (10 s timeout)", $"BACKUP LOG cc TO DISK = N'{F}/cc_l1.trn' WITH INIT;", 10);
            await Spike.TryAsync(sql, s, "3: native full while suspended (10 s timeout)", $"BACKUP DATABASE cc TO DISK = N'{F}/cc_full2.bak' WITH INIT;", 10);
            await Spike.TryAsync(sql, s, "3b: copy-only full while suspended (10 s timeout)", $"BACKUP DATABASE cc TO DISK = N'{F}/cc_full3.bak' WITH INIT, COPY_ONLY;", 10);
            await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = OFF;", 30);
        }

        // 4: a log backup is running, then the suspend comes.
        await sql.ExecuteAsync("UPDATE cc.dbo.t SET pad = 'z';");
        var logBackup = Spike.TryAsync(sql, s, "4: slow log backup", $"BACKUP LOG cc TO DISK = N'{F}/cc_l2.trn' WITH INIT, BUFFERCOUNT = 2, MAXTRANSFERSIZE = 65536;");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await using (var suspend = await OpenAsync())
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON;", 30);
                Spike.Out(s, $"4: suspend during the log backup: ok after {watch.ElapsedMilliseconds} ms; log backup finished before: {logBackup.IsCompleted}");
                await ExecAsync(suspend, "ALTER DATABASE cc SET SUSPEND_FOR_SNAPSHOT_BACKUP = OFF;", 30);
            }
            catch (SqlException e)
            {
                Spike.Out(s, $"4: suspend during the log backup: error {e.Number} after {watch.ElapsedMilliseconds} ms: {e.Message}");
            }
        }

        await logBackup;
        await Spike.DumpBackupSetsAsync(sql, s, "cc");
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecAsync(SqlConnection connection, string text, int timeoutSeconds)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync();
    }
}

[Trait("Category", "Sql")]
public sealed class PointInTimeSpikesBerlin(SqlServerBerlinFixture sql) : IClassFixture<SqlServerBerlinFixture>
{
    private const string F = Spike.Folder;

    [Fact]
    public async Task Spike_2e_local_times_in_msdb_and_stopat()
    {
        Assert.SkipUnless(Spike.Enabled, Spike.SkipReason);
        const string s = "2e";
        await Spike.DumpAsync(sql, s, "clock", "SELECT SYSDATETIMEOFFSET() AS local_offset, SYSUTCDATETIME() AS utc, CURRENT_TIMEZONE() AS zone");
        await sql.ExecuteAsync("CREATE DATABASE tz; ALTER DATABASE tz SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE tz; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1);");
        await sql.ExecuteAsync($"BACKUP DATABASE tz TO DISK = N'{F}/tz_full.bak' WITH INIT;");
        await sql.ExecuteAsync("INSERT tz.dbo.t VALUES (2);");
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        var local = (string)(await Spike.ScalarAsync(sql, "SELECT CONVERT(varchar(23), SYSDATETIME(), 126)"))!;
        var utc = (string)(await Spike.ScalarAsync(sql, "SELECT CONVERT(varchar(23), SYSUTCDATETIME(), 126)"))!;
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        await sql.ExecuteAsync("INSERT tz.dbo.t VALUES (3);");
        await sql.ExecuteAsync($"BACKUP LOG tz TO DISK = N'{F}/tz_l1.trn' WITH INIT;");
        Spike.Out(s, $"point: local {local}, utc {utc}");
        await Spike.DumpBackupSetsAsync(sql, s, "tz");
        await Spike.DumpAsync(sql, s, "HEADERONLY of the log", $"RESTORE HEADERONLY FROM DISK = N'{F}/tz_l1.trn'", "BackupStartDate", "BackupFinishDate", "FirstLSN", "LastLSN");

        foreach (var (name, stopAt) in new[] { ("tz_local", local), ("tz_utc", utc) })
        {
            var error = await Spike.TryAsync(sql, s, $"{name}: restore",
                $"RESTORE DATABASE {name} FROM DISK = N'{F}/tz_full.bak' WITH MOVE N'tz' TO N'{F}/{name}.mdf', MOVE N'tz_log' TO N'{F}/{name}_log.ldf', NORECOVERY;" +
                $"RESTORE LOG {name} FROM DISK = N'{F}/tz_l1.trn' WITH STOPAT = '{stopAt}', RECOVERY;");
            if (error == 0)
            {
                Spike.Out(s, $"{name}: STOPAT {stopAt} → rows = {await Spike.ScalarAsync(sql, $"SELECT COUNT(*) FROM {name}.dbo.t")} (2 means the time is read as local)");
            }
        }
    }
}

[Trait("Category", "Sql")]
public sealed class PointInTimeSpikes2019(SqlServer2019Fixture sql) : IClassFixture<SqlServer2019Fixture>
{
    private const string F = Spike.Folder;

    [Fact]
    public async Task Spike_2f_native_base_and_backup_set_facts_on_2019()
    {
        Assert.SkipUnless(Spike.Enabled, Spike.SkipReason);
        const string s = "2f";
        await Spike.DumpAsync(sql, s, "version", "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64)) AS version, CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS major, CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition");
        await sql.ExecuteAsync("CREATE DATABASE old; ALTER DATABASE old SET RECOVERY FULL;");
        await sql.ExecuteAsync("USE old; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1), (2);");
        await Spike.TryAsync(sql, s, "snapshot suspend on 2019", "ALTER DATABASE old SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON;");
        await Spike.TryAsync(sql, s, "log backup without a base", $"BACKUP LOG old TO DISK = N'{F}/old_l0.trn' WITH INIT;");
        await Spike.TryAsync(sql, s, "native full", $"BACKUP DATABASE old TO DISK = N'{F}/old_full.bak' WITH INIT, CHECKSUM, COMPRESSION, NAME = N'backdafuckup full', DESCRIPTION = N'job=1;session=2';");
        await Spike.TryAsync(sql, s, "verify", $"RESTORE VERIFYONLY FROM DISK = N'{F}/old_full.bak' WITH CHECKSUM;");
        await sql.ExecuteAsync("INSERT old.dbo.t VALUES (3);");
        await sql.ExecuteAsync($"BACKUP LOG old TO DISK = N'{F}/old_l1.trn' WITH INIT, CHECKSUM, COMPRESSION, NAME = N'backdafuckup log';");
        await sql.ExecuteAsync("INSERT old.dbo.t VALUES (4);");
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        var point = (string)(await Spike.ScalarAsync(sql, "SELECT CONVERT(varchar(23), SYSDATETIME(), 126)"))!;
        await Task.Delay(1100, TestContext.Current.CancellationToken);
        await sql.ExecuteAsync("INSERT old.dbo.t VALUES (5);");
        await sql.ExecuteAsync($"BACKUP LOG old TO DISK = N'{F}/old_l2.trn' WITH INIT, CHECKSUM, COMPRESSION, NAME = N'backdafuckup log';");
        await Spike.DumpBackupSetsAsync(sql, s, "old");
        await Spike.DumpAsync(sql, s, "FILELISTONLY", $"RESTORE FILELISTONLY FROM DISK = N'{F}/old_full.bak'", "LogicalName", "PhysicalName", "Type", "FileId");
        await Spike.TryAsync(sql, s, "restore chain",
            $"RESTORE DATABASE old_r FROM DISK = N'{F}/old_full.bak' WITH MOVE N'old' TO N'{F}/old_r.mdf', MOVE N'old_log' TO N'{F}/old_r_log.ldf', NORECOVERY, CHECKSUM;" +
            $"RESTORE LOG old_r FROM DISK = N'{F}/old_l1.trn' WITH NORECOVERY, CHECKSUM;" +
            $"RESTORE LOG old_r FROM DISK = N'{F}/old_l2.trn' WITH STOPAT = '{point}', RECOVERY, CHECKSUM;");
        Spike.Out(s, $"rows = {await Spike.ScalarAsync(sql, "SELECT COUNT(*) FROM old_r.dbo.t")} (expected 4)");
    }
}
