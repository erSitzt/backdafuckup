using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Sql;
using Backdafuckup.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Scale-out moves: SQL Server backups follow their chain, readers keep source copies, interrupted moves resume, automatic rebalancing.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_moved_chain_takes_its_sql_server_backups_along_and_the_next_log_backup_follows_it()
    {
        var client = await SignedInClientAsync();
        var image = WindowsTestDisk.Create([new WindowsTestDisk.Volume('C', 8, new Dictionary<string, byte[]>())]);
        var backupFolder = Path.Combine(_root, "sql-lane");
        Directory.CreateDirectory(backupFolder);
        var sql = new ScriptedSqlEnvironment(_ => []) { BackupFolder = backupFolder };
        sql.AddDatabase("shop", @"D:\SQL\shop.mdf", @"L:\Log\shop_log.ldf");
        var login = await Read<SqlCredentialDto>(client.PostAsJsonAsync("/api/credentials/sql", new SaveSqlCredentialDto("bdfu_backup", "Backup-1", null), Ct));
        (AgentEntity Agent, CancellationTokenSource Stop, Task Running)? started = null;
        var none = new ScheduleDto("None", "20:00", [], "UTC");
        var point = await BackUpImageAsync(
            client,
            "sql07",
            image,
            async vm => started = await StartAgentAsync(vm, null, sql, null, [new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "shop", InBackup = true }]),
            job => job with
            {
                GuestProcessingMode = GuestProcessingModes.SqlSnapshot,
                SqlCredentialId = login.Id,
                SqlBackup = new SqlBackupSettingsDto(Enabled: true, LogIntervalMinutes: 15, FullSchedule: none, SystemSchedule: none),
            });
        var (_, stop, running) = started ?? throw new InvalidOperationException("The agent did not start.");
        var service = _factory!.Services.GetRequiredService<SqlBackupService>();
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        async Task LogBackupAsync()
        {
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                await db.SqlBackupSchedules.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextLogUtc, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }

            await service.TickAsync(Ct);
            await service.WaitIdleAsync();
        }

        try
        {
            // A log backup continues the snapshot full, both on Local.
            await LogBackupAsync();
            var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
            var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = Path.Combine(_root, "extent2"), ScaleOutName = "Scale" }, Ct);
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
            var repositories = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!;
            var (scale, e2) = (repositories.Single(r => r.Name == "Scale"), repositories.Single(r => r.Name == "E2"));
            Guid logBackupId;
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                logBackupId = (await db.SqlBackups.SingleAsync(Ct)).Id;
                Assert.Equal(local.Id, (await db.SqlBackups.SingleAsync(Ct)).RepositoryId);
            }

            // The move takes the restore point and the log backup with their ids; the catalog's chain names the new extent.
            var move = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/repositories/{scale.Id}/moves",
                new StartMovesDto([new ChainMoveDto(point.JobId, point.VmNativeId, local.Id, e2.Id)]), Ct))).SessionId);
            var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{move.Session.Id}/log", Ct))!.Select(l => l.Message).ToList();
            Assert.True(move.Session.State == "Succeeded", move.Session.Summary + "\n" + string.Join("\n", log));
            Assert.Contains(log, l => l.Contains("1 restore point(s) and 1 SQL Server backup(s) moved from Local to E2", StringComparison.Ordinal));
            Guid shopId;
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                var moved = await db.SqlBackups.SingleAsync(Ct);
                Assert.Equal((logBackupId, e2.Id), (moved.Id, moved.RepositoryId));
                Assert.Equal(e2.Id, (await db.RestorePoints.SingleAsync(r => r.Id == point.Id, Ct)).RepositoryId);
                Assert.All(await db.SqlBackupSets.ToListAsync(Ct), s => Assert.Equal(e2.Id, s.RepositoryId));
                shopId = (await db.SqlDatabases.SingleAsync(d => d.Name == "shop", Ct)).Id;
            }

            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "repo", "sql-backups"), "*", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "repo", "restore-points"), "manifest.json", SearchOption.AllDirectories));

            // A point-in-time restore reads the moved chain: the snapshot full's files and the log backup.
            var plan = (await client.GetFromJsonAsync<SqlRestorePlanDto>($"/api/sql/databases/{shopId}/plan", Ct))!;
            Assert.Equal(("SnapshotFull", (Guid?)point.Id, 1), (plan.BaseKind, plan.RestorePointId, plan.Logs));

            // The next log backup goes where its chain is now.
            await LogBackupAsync();
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                Assert.Equal([e2.Id, e2.Id], await db.SqlBackups.Select(b => b.RepositoryId).ToListAsync(Ct));
                Assert.Equal(2, new SqlChain((await db.SqlBackupSets.Where(s => s.SqlDatabaseId == shopId).ToListAsync(Ct)).Select(SqlChainSet.From)).Plan(null).Logs.Count);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task An_interrupted_move_resumes_from_its_copies_and_a_reader_keeps_the_source_copy_until_it_lets_go()
    {
        var client = await SignedInClientAsync();
        var image = RandomNumberGenerator.GetBytes(4 << 20);
        var first = await BackUpImageAsync(client, "vm1", image);
        var jobId = first.JobId!.Value;
        var changed = (byte[])image.Clone();
        RandomNumberGenerator.Fill(changed.AsSpan(0, 256 << 10));
        _pool.WriteDisk(DiskOf(first.VmNativeId, changed.LongLength), 0, changed);
        var second = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{jobId}/run", new RunJobDto(), Ct))).SessionId);
        Assert.Equal("Succeeded", second.Session.State);
        var points = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={jobId}", Ct))!.OrderBy(p => p.CreatedUtc).ToList();
        Assert.Equal(2, points.Count);

        var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
        var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = Path.Combine(_root, "extent2"), ScaleOutName = "Scale" }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        var repositories = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!;
        var (scale, e2) = (repositories.Single(r => r.Name == "Scale"), repositories.Single(r => r.Name == "E2"));
        async Task<(SessionDetailDto Detail, List<string> Log)> MoveAsync()
        {
            var detail = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/repositories/{scale.Id}/moves",
                new StartMovesDto([new ChainMoveDto(jobId, first.VmNativeId, local.Id, e2.Id)]), Ct))).SessionId);
            return (detail, (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{detail.Session.Id}/log", Ct))!.Select(l => l.Message).ToList());
        }

        var dbFactory = _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var mover = _factory.Services.GetRequiredService<Backdafuckup.Server.ScaleOut.ChainMover>();

        // The move breaks off right before the switch (as if the service stopped): the copies stay, the points stay on Local.
        mover.BeforeSwitch = _ => throw new IOException("Simulated failure before the switch.");
        try
        {
            var broken = await MoveAsync();
            Assert.Equal("Failed", broken.Detail.Session.State);
            Assert.Equal("Failed", Assert.Single(broken.Detail.Tasks).State);
        }
        finally
        {
            mover.BeforeSwitch = null;
        }

        await using (var db = await dbFactory.CreateDbContextAsync(Ct))
        {
            Assert.All(await db.RestorePoints.Where(r => r.JobId == jobId).ToListAsync(Ct), p => Assert.Equal(local.Id, p.RepositoryId));
            Assert.Equal([MoveItemState.Copied, MoveItemState.Copied], await db.RepositoryMoveItems.Select(m => m.State).ToListAsync(Ct));
        }

        // A reader holds the older point on Local. The next move copies nothing again, switches both points, and leaves
        // the held copy for later.
        var access = _factory.Services.GetRequiredService<Backdafuckup.Server.Engine.RestorePointAccess>();
        var held = await access.PinRestorePointAsync(points[0].Id, local.Id, Ct);
        try
        {
            var (resumed, log) = await MoveAsync();
            Assert.True(resumed.Session.State == "Succeeded", resumed.Session.Summary + "\n" + string.Join("\n", log));
            Assert.Contains(log, l => l.Contains("2 restore point(s) and 0 SQL Server backup(s) moved from Local to E2, 0.0 KB copied; 1 source cop(ies) still in use are deleted later.", StringComparison.Ordinal));
            Assert.All((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={jobId}", Ct))!, p => Assert.Equal(e2.Id, p.RepositoryId));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "repo", "restore-points"), "manifest.json", SearchOption.AllDirectories));

            // The reader still reads its copy.
            await using var reader = await held.Repository.OpenRestorePointAsync(points[0].Id, Ct);
            Assert.Equal(points[0].Id, reader.Manifest.RestorePointId);
        }
        finally
        {
            held.Dispose();
        }

        // Once the reader lets go, the cleanup deletes it.
        await _factory.Services.GetRequiredService<Backdafuckup.Server.ScaleOut.ScaleOutBalancer>().TickAsync(Ct);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "repo", "restore-points"), "manifest.json", SearchOption.AllDirectories));
        await using (var db = await dbFactory.CreateDbContextAsync(Ct))
        {
            Assert.Equal([MoveItemState.SourceDeleted, MoveItemState.SourceDeleted], await db.RepositoryMoveItems.Select(m => m.State).ToListAsync(Ct));
        }

        foreach (var point in points)
        {
            Assert.True((await Read<VerifyResultDto>(client.PostAsync($"/api/restore-points/{point.Id}/verify?full=true", null, Ct))).Ok);
        }
    }

    [Fact]
    public async Task The_balancer_rebalances_in_its_window_within_the_limit_per_run_and_continues_with_the_next_run()
    {
        var client = await SignedInClientAsync();
        var big = await BackUpImageAsync(client, "big", RandomNumberGenerator.GetBytes(4 << 20));
        var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
        var (smallVm, smallDisks) = _pool.AddVm("small", 2 << 20);
        _pool.WriteDisk(smallDisks[0], 0, RandomNumberGenerator.GetBytes(2 << 20));
        var bigJob = (await client.GetFromJsonAsync<JobDto>($"/api/jobs/{big.JobId}", Ct))!;
        var smallJob = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Small", ConnectionId = bigJob.ConnectionId, RepositoryId = local.Id, RetentionPoints = 7, Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(smallVm, "small")], RetryCount = 0,
        }, Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{smallJob.Id}/run", new RunJobDto(), Ct))).SessionId)).Session.State);
        var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = Path.Combine(_root, "extent2"), ScaleOutName = "Scale" }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        var repositories = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!;
        var (scale, e2) = (repositories.Single(r => r.Name == "Scale"), repositories.Single(r => r.Name == "E2"));

        // Local is nearly full, E2 nearly empty; no job is due soon (that would keep its chains in place).
        _volumes.Set(Path.Combine(_root, "repo"), 200L << 30, 10L << 30);
        _volumes.Set(Path.Combine(_root, "extent2"), 200L << 30, 190L << 30);
        var dbFactory = _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync(Ct))
        {
            await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.NextRunUtc, (DateTimeOffset?)null), Ct);
        }

        var balancer = _factory.Services.GetRequiredService<Backdafuckup.Server.ScaleOut.ScaleOutBalancer>();
        async Task<List<SessionSummaryDto>> MovesAsync() =>
            [.. (await client.GetFromJsonAsync<List<SessionSummaryDto>>("/api/sessions?kind=RepositoryMove", Ct))!.Where(s => s.Kind == "RepositoryMove")];
        async Task SettingsAsync(IReadOnlyList<DayOfWeek> days) => Assert.True((await client.PutAsJsonAsync($"/api/repositories/{scale.Id}/settings", new ScaleOutSettingsDto
        {
            AutoRebalance = true, WindowDays = days, WindowStart = "00:00", WindowEnd = "00:00", MaxBytesPerRun = 5L << 20,
        }, Ct)).IsSuccessStatusCode);

        // Outside its window (not today) it does nothing.
        var today = DateTimeOffset.UtcNow.DayOfWeek;
        await SettingsAsync([.. Enum.GetValues<DayOfWeek>().Where(d => d != today)]);
        await balancer.TickAsync(Ct);
        Assert.Empty(await MovesAsync());

        // In the window: the plan fits one chain into 5 MiB, the one that evens out the extents most (the bigger one).
        await SettingsAsync([.. Enum.GetValues<DayOfWeek>()]);
        await balancer.TickAsync(Ct);
        var run = Assert.Single(await MovesAsync());
        var detail = await WaitForSessionAsync(client, run.Id);
        Assert.True(detail.Session.State == "Succeeded", detail.Session.Summary);
        Assert.Equal("Automatic rebalance: Local has 5 % free, less than 10 %", detail.Session.TriggeredBy);
        Assert.Equal(e2.Id, (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={big.JobId}", Ct))!.Single().RepositoryId);
        Assert.Equal(local.Id, (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={smallJob.Id}", Ct))!.Single().RepositoryId);

        // The next run takes the other chain; the moved one rests (cooldown).
        await balancer.TickAsync(Ct);
        var next = (await MovesAsync()).Single(s => s.Id != run.Id);
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, next.Id)).Session.State);
        Assert.Equal(e2.Id, (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={smallJob.Id}", Ct))!.Single().RepositoryId);
        await balancer.TickAsync(Ct);
        Assert.Equal(2, (await MovesAsync()).Count);
    }
}
