using System.Data.Common;
using System.Security.Cryptography;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// The agent's backup lane against a real SQL Server 2022: log backups of databases with a chain, a full backup that
/// starts a missing chain, system databases, other tools' backups in msdb, the durable queue across a lost server and an
/// interrupted run, and the instance lock against the snapshot suspend.
/// </summary>
[Trait("Category", "Sql")]
public sealed class SqlBackupLaneTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    private const string Data = "/var/opt/mssql/data";
    private static readonly SqlAccount Sa = new() { Kind = SqlAccountKinds.Sql, UserName = "sa" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_log_backup_run_starts_missing_chains_with_a_full_and_backs_up_the_logs_of_the_others()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE lane_a; ALTER DATABASE lane_a SET RECOVERY FULL; CREATE DATABASE lane_simple; ALTER DATABASE lane_simple SET RECOVERY SIMPLE;");
        await sql.ExecuteAsync("USE lane_a; CREATE TABLE dbo.t (id int); INSERT dbo.t VALUES (1);");
        var (lane, sink, queue, folder, log) = await LaneAsync();

        // First run: lane_a has no chain yet, so it gets a full backup that starts one.
        var first = await RunAsync(lane, Request(SqlJobKinds.LogBackup, fullWithoutChain: true));

        var a = first.BackupDatabases!.Single(d => d.Database == "lane_a");
        Assert.Equal((SqlBackupActions.Full, "FULL", false), (a.Action, a.RecoveryModel, a.HasLogChain));
        Assert.Contains("no log chain yet", a.Reason, StringComparison.Ordinal);
        var simple = first.BackupDatabases!.Single(d => d.Database == "lane_simple");
        Assert.Equal((SqlBackupActions.Skipped, "SIMPLE recovery (no log backups)"), (simple.Action, simple.Reason));
        Assert.DoesNotContain(first.BackupDatabases!, d => d.Database is "master" or "msdb" or "model" && d.Action != SqlBackupActions.Skipped);
        var full = Assert.Single(sink.Committed, u => u.Database == "lane_a");
        Assert.Equal(("full", "D", false, false), (full.Kind, full.BackupSet.Type, full.BackupSet.IsCopyOnly, full.BackupSet.IsSnapshot));
        Assert.Equal("backdafuckup full", full.BackupSet.Name);
        // Where a restore puts the files: logical and original physical names from sys.master_files.
        Assert.Equal([(1, "lane_a", "ROWS"), (2, "lane_a_log", "LOG")], full.BackupSet.Files!.Select(f => (f.FileId, f.LogicalName, f.Type)));
        Assert.All(full.BackupSet.Files!, f => Assert.StartsWith("/var/opt/mssql/data/lane_a", f.PhysicalName, StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(sink.Content[full.FileId])), full.Sha256);
        Assert.Equal((first.CommittedFiles, first.QueuedFiles), (sink.Committed.Count, 0));
        Assert.Empty(queue().Entries);
        Assert.Empty(await sql.ListFilesAsync(folder));

        // Second run: a log backup that continues the full (first_lsn ≤ full.last_lsn < last_lsn).
        await sql.ExecuteAsync("INSERT lane_a.dbo.t VALUES (2);");
        sink.Committed.Clear();
        var second = await RunAsync(lane, Request(SqlJobKinds.LogBackup, fullWithoutChain: true));

        Assert.Equal(SqlBackupActions.Log, second.BackupDatabases!.Single(d => d.Database == "lane_a").Action);
        var logBackup = Assert.Single(sink.Committed, u => u.Database == "lane_a");
        Assert.Equal(("log", "L", "backdafuckup log"), (logBackup.Kind, logBackup.BackupSet.Type, logBackup.BackupSet.Name));
        Assert.EndsWith(".trn", logBackup.FileName, StringComparison.Ordinal);
        Assert.True(decimal.Parse(logBackup.BackupSet.FirstLsn) <= decimal.Parse(full.BackupSet.LastLsn) && decimal.Parse(full.BackupSet.LastLsn) < decimal.Parse(logBackup.BackupSet.LastLsn));
        Assert.Equal(full.BackupSet.FamilyGuid, logBackup.BackupSet.FamilyGuid);
        Assert.Equal(0, logBackup.BackupSet.TimeZone);
        Assert.Contains(log, l => l.Contains("lane_a: log backup lane_a_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restored_copy_the_server_holds_no_full_of_gets_a_full_backup_instead_of_a_log_backup()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE lane_r; ALTER DATABASE lane_r SET RECOVERY FULL;");
        await sql.ExecuteAsync($"BACKUP DATABASE lane_r TO DISK = N'{Data}/lane_r_source.bak' WITH INIT;");
        await sql.ExecuteAsync(
            $"RESTORE DATABASE lane_rc FROM DISK = N'{Data}/lane_r_source.bak' WITH MOVE N'lane_r' TO N'{Data}/lane_rc.mdf', MOVE N'lane_r_log' TO N'{Data}/lane_rc_log.ldf', RECOVERY;");
        try
        {
            var (lane, sink, _, _, _) = await LaneAsync();

            // SQL Server continues lane_r's log chain in the copy, but the server holds a full of lane_r only.
            var request = Request(SqlJobKinds.LogBackup, fullWithoutChain: true, only: "lane_rc");
            request.DatabasesWithBase = [@"MSSQLSERVER\lane_r"];
            var result = await RunAsync(lane, request);

            var copy = result.BackupDatabases!.Single(d => d.Database == "lane_rc");
            Assert.Equal((SqlBackupActions.Full, true), (copy.Action, copy.HasLogChain));
            Assert.StartsWith("new to the backups", copy.Reason, StringComparison.Ordinal);
            Assert.Equal([("lane_rc", "full")], sink.Committed.Select(c => (c.Database, c.Kind)));

            // Once the server holds that full, the log is backed up.
            sink.Committed.Clear();
            request = Request(SqlJobKinds.LogBackup, fullWithoutChain: true, only: "lane_rc");
            request.DatabasesWithBase = [@"MSSQLSERVER\lane_r", @"mssqlserver\LANE_RC"];
            result = await RunAsync(lane, request);

            Assert.Equal(SqlBackupActions.Log, result.BackupDatabases!.Single(d => d.Database == "lane_rc").Action);
            Assert.Equal([("lane_rc", "log")], sink.Committed.Select(c => (c.Database, c.Kind)));
        }
        finally
        {
            await sql.ExecuteAsync("DROP DATABASE lane_rc; DROP DATABASE lane_r;");
        }
    }

    [Fact]
    public async Task Files_whose_upload_fails_stay_queued_and_go_first_in_the_next_run()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE lane_q; ALTER DATABASE lane_q SET RECOVERY FULL;");
        await sql.ExecuteAsync($"BACKUP DATABASE lane_q TO DISK = N'{Data}/lane_q_base.bak' WITH INIT, NAME = N'backdafuckup test base';");
        var (lane, sink, queue, folder, log) = await LaneAsync();

        // The server is away: the log backup is taken (the log gets truncated, the chain goes on) and waits in the VM.
        sink.Down = true;
        var offline = await RunAsync(lane, Request(SqlJobKinds.LogBackup, only: "lane_q"));

        Assert.Equal(SqlBackupActions.Log, offline.BackupDatabases!.Single(d => d.Database == "lane_q").Action);
        Assert.Equal((0, 1), (offline.CommittedFiles, offline.QueuedFiles));
        var waiting = Assert.Single(queue().Entries);
        Assert.True(waiting.IsComplete);
        Assert.True(await sql.FileExistsAsync(waiting.Path));
        Assert.Contains(log, l => l.Contains("stays queued in the VM for the next run", StringComparison.Ordinal));

        // Back again: the queued file goes first, then the new log backup; both are committed and deleted.
        sink.Down = false;
        var online = await RunAsync(lane, Request(SqlJobKinds.LogBackup, only: "lane_q"));

        Assert.Equal((2, 0), (online.CommittedFiles, online.QueuedFiles));
        Assert.Equal(waiting.Id, sink.Committed[0].FileId);
        var (older, newer) = (sink.Committed[0].BackupSet, sink.Committed[1].BackupSet);
        Assert.Equal(older.LastLsn, newer.FirstLsn);
        Assert.Empty(queue().Entries);
        Assert.Empty(await sql.ListFilesAsync(folder));

        // A file the server already committed (its answer got lost) is only deleted.
        sink.KnownOnUpload = true;
        await sql.ExecuteAsync("CHECKPOINT;");
        var again = await RunAsync(lane, Request(SqlJobKinds.LogBackup, only: "lane_q"));
        Assert.Equal((1, 0), (again.CommittedFiles, again.QueuedFiles));
        Assert.Empty(await sql.ListFilesAsync(folder));
    }

    [Fact]
    public async Task An_interrupted_backup_is_resolved_through_msdb_and_never_reported_without_its_backup_set()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE lane_i; ALTER DATABASE lane_i SET RECOVERY FULL;");
        await sql.ExecuteAsync($"BACKUP DATABASE lane_i TO DISK = N'{Data}/lane_i_base.bak' WITH INIT, NAME = N'backdafuckup test base';");
        var (lane, sink, queue, folder, log) = await LaneAsync();

        // The agent died after SQL Server finished one backup but before it was recorded in the queue, and during another.
        var finished = $"{folder}/lane_i_finished.trn";
        await sql.ExecuteAsync($"BACKUP LOG lane_i TO DISK = N'{finished}' WITH INIT, NAME = N'backdafuckup log';");
        var partial = $"{folder}/lane_i_partial.trn";
        await sql.WriteFileAsync(partial, [1, 2, 3]);
        queue().Add(new SqlBackupQueueEntry { Instance = "MSSQLSERVER", Database = "lane_i", Kind = SqlBackupActions.Log, Path = finished, StartedUtc = DateTime.UtcNow });
        queue().Add(new SqlBackupQueueEntry { Instance = "MSSQLSERVER", Database = "lane_i", Kind = SqlBackupActions.Log, Path = partial, StartedUtc = DateTime.UtcNow });

        var result = await RunAsync(lane, Request(SqlJobKinds.LogBackup, only: "lane_i"));

        Assert.Equal(2, result.CommittedFiles);
        Assert.Equal(["lane_i_finished.trn", Path.GetFileName(result.BackupDatabases!.Single(d => d.Database == "lane_i").Path!)], sink.Committed.Select(c => c.FileName));
        Assert.False(await sql.FileExistsAsync(partial));
        Assert.Contains(log, l => l.Contains("was interrupted before SQL Server finished it; its partial file was deleted", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("had finished before the agent stopped; it is sent now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Other_tools_backups_are_reported_and_system_databases_get_full_backups()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync("CREATE DATABASE lane_f; ALTER DATABASE lane_f SET RECOVERY FULL;");
        var since = DateTime.UtcNow.AddSeconds(-2);
        await sql.ExecuteAsync($"BACKUP DATABASE lane_f TO DISK = N'{Data}/lane_f_other.bak' WITH INIT;");
        await sql.ExecuteAsync("BACKUP LOG lane_f TO DISK = N'NUL';");
        var (lane, sink, _, _, _) = await LaneAsync();

        var result = await RunAsync(lane, Request(SqlJobKinds.LogBackup, only: "lane_f", foreignSince: since));

        var foreign = result.ForeignBackups!.Where(f => f.Database == "lane_f").ToList();
        Assert.Equal(["D", "L"], foreign.Select(f => f.BackupSet.Type));
        Assert.Equal([$"{Data}/lane_f_other.bak", "NUL"], foreign.Select(f => f.BackupSet.Device));
        Assert.All(foreign, f => Assert.False(f.BackupSet.IsCopyOnly));

        var system = await RunAsync(lane, Request(SqlJobKinds.FullBackup, systemDatabases: true));
        Assert.Equal(["master", "model", "msdb"], sink.Committed.Where(c => c.Kind == "system").Select(c => c.Database).Order(StringComparer.Ordinal));
        Assert.All(system.BackupDatabases!.Where(d => d.Database is not ("master" or "model" or "msdb")), d => Assert.Equal(SqlBackupActions.Skipped, d.Action));
    }

    [Fact]
    public async Task A_backup_waits_for_the_snapshot_and_skips_the_instance_when_it_holds_on()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        var locks = new SqlInstanceLocks();
        var (lane, sink, _, _, log) = await LaneAsync(locks);
        using (locks.TryAcquire("MSSQLSERVER", TimeSpan.Zero, "the SQL Server snapshot", TestContext.Current.CancellationToken))
        {
            var request = Request(SqlJobKinds.LogBackup);
            request.LockWaitMs = 200;
            var result = await RunAsync(lane, request);

            var skipped = Assert.Single(result.BackupDatabases!);
            Assert.Equal((SqlBackupActions.Skipped, "another backup of MSSQLSERVER (the SQL Server snapshot) still ran after 0 s"), (skipped.Action, skipped.Reason));
            Assert.Empty(sink.Committed);
        }
    }

    private async Task<(Setup Lane, FakeSink Sink, Func<SqlBackupQueue> Queue, string Folder, List<string> Log)> LaneAsync(SqlInstanceLocks? locks = null)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var agent = Path.Combine(Path.GetTempPath(), "bdfu-lane-tests", id);
        Directory.CreateDirectory(agent);
        var staging = $"{Data}/lane-{id}";
        var folder = $"{staging}/{SqlBackupLane.NamePrefix}";
        await sql.CreateDirectoryAsync(folder);
        var log = new List<string>();
        var queue = SqlBackupQueue.Load(agent, log.Add);
        var sink = new FakeSink();
        return (new Setup(new SqlBackupLane(new ContainerSql(sql), locks ?? new SqlInstanceLocks(), queue, sink, log.Add, files: new ContainerFiles(sql)), staging), sink, () => queue, folder, log);
    }

    private static async Task<SqlJobResult> RunAsync(Setup setup, SqlJobRequest request)
    {
        request.Folder = setup.Staging;
        var result = new SqlJobResult();
        await setup.Lane.RunAsync(request, result, TestContext.Current.CancellationToken);
        return result;
    }

    private sealed record Setup(SqlBackupLane Lane, string Staging);

    private static SqlJobRequest Request(string kind, bool fullWithoutChain = false, string? only = null, DateTime? foreignSince = null, bool systemDatabases = false) => new SqlJobRequest
    {
        Kind = kind,
        Account = Sa,
        Instance = "MSSQLSERVER",
        FullWithoutChain = fullWithoutChain,
        ExcludedDatabases = only is null ? null : ["lane_a", "lane_simple", "lane_q", "lane_i", "lane_f", "lane_r", "lane_rc"],
        ForeignSinceUtc = foreignSince,
        SystemDatabases = systemDatabases,
        BackupJobId = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        LockWaitMs = 5000,
    }.Only(only);

    private sealed class FakeSink : ISqlBackupSink
    {
        private readonly List<SqlBackupUpload> _pending = [];

        public bool Down { get; set; }

        public bool KnownOnUpload { get; set; }

        public List<SqlBackupUpload> Committed { get; } = [];

        public Dictionary<string, byte[]> Content { get; } = [];

        public async Task<bool> UploadAsync(SqlBackupUpload upload, Stream content, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("Connection refused (10.0.0.1:9555)");
            }

            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            Assert.Equal(upload.Length, memory.Length);
            Content[upload.FileId] = memory.ToArray();
            if (KnownOnUpload)
            {
                return true;
            }

            _pending.Add(upload);
            return false;
        }

        public Task<IReadOnlyList<string>> CommitAsync(CancellationToken cancellationToken)
        {
            Committed.AddRange(_pending);
            var ids = _pending.Select(p => p.FileId).ToList();
            _pending.Clear();
            return Task.FromResult<IReadOnlyList<string>>(ids);
        }
    }

    /// <summary>The staging folders live in the container: read and delete through it (as the agent does in the VM).</summary>
    private sealed class ContainerFiles(SqlServerFixture sql) : SqlBackupFiles
    {
        public override Stream OpenRead(string path) => new MemoryStream(sql.ReadFileAsync(path).GetAwaiter().GetResult(), writable: false);

        public override bool Exists(string path) => sql.FileExistsAsync(path).GetAwaiter().GetResult();

        public override void Delete(string path) => sql.DeleteFileAsync(path).GetAwaiter().GetResult();

        public override void CreateDirectory(string path) => sql.CreateDirectoryAsync(path).GetAwaiter().GetResult();
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

internal static class LaneRequestExtensions
{
    /// <summary>Limits a log backup job to one database by excluding the other test databases.</summary>
    public static SqlJobRequest Only(this SqlJobRequest request, string? database)
    {
        if (database is not null)
        {
            request.ExcludedDatabases = request.ExcludedDatabases!.Where(d => d != database).ToList();
        }

        if (request.Kind == SqlJobKinds.FullBackup)
        {
            request.ExcludedDatabases = null;
        }

        return request;
    }
}
