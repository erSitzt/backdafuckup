using System.Text.Json.Nodes;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Server.Sql;
using Backdafuckup.Storage;
using Backdafuckup.Testing.Xapi;
using Backdafuckup.Xen;
using Backdafuckup.Xen.Xapi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// The whole engine against real PostgreSQL (Testcontainers), a real chunk repository and the fake XCP-ng pool
/// (XAPI + TLS NBD). Covers full and CBT-incremental backups, retention with garbage collection, restore and
/// crash recovery.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EngineIntegrationTests : IAsyncLifetime
{
    private const long DiskSize = 6L * 1024 * 1024;
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-engine-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeXenPool _pool = new();
    private readonly FakeRemoteWindowsConnector _remote = new();
    private IHost? _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _host!.Services;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Ct);
        _host = BuildHost();
        await Services.MigrateCatalogAsync(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Ct);
        await _host.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            await Services.GetRequiredService<RepositoryManager>().DisposeAsync();
            _host.Dispose();
        }

        await _pool.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_incremental_retention_and_restore_end_to_end()
    {
        var (vmUuid, disks) = _pool.AddVm("fileserver", DiskSize, DiskSize / 2);
        var (jobId, repositoryId) = await SeedAsync(vmUuid, retention: 2);
        var queue = Services.GetRequiredService<SessionQueue>();

        // 1st run: full.
        var first = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, first.State);
        var rp1 = await LatestRestorePointAsync();
        Assert.Equal(RestorePointKind.Full, rp1.Kind);
        Assert.All(rp1.Disks, d => Assert.NotNull(d.ChangeTokenValue));
        var contentAtRp1 = _pool.DiskContent(disks[0]);

        // 2nd run: incremental, reads only changed blocks. The pool reports them in the other bit order than XenServer
        // documents: the default decoding (whole bitmap bytes) still finds them, and the restore below proves it.
        _pool.ChangedBlocksBitOrder = CbtBitOrder.LeastSignificantBitFirst;
        _pool.WriteDisk(disks[0], 2 * CbtBitmap.BlockSize, new byte[CbtBitmap.BlockSize].Select((_, i) => (byte)i).ToArray());
        var second = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, second.State);
        var rp2 = await LatestRestorePointAsync();
        Assert.Equal(RestorePointKind.Incremental, rp2.Kind);
        Assert.All(rp2.Disks, d => Assert.Equal(DiskBackupMode.Cbt, d.Mode));
        Assert.True(rp2.Disks.Single(d => d.SourceDiskId == disks[0]).ReadBytes <= 1024 * 1024);
        Assert.Equal(0, rp2.Disks.Single(d => d.SourceDiskId == disks[1]).ReadBytes);

        // The superseded change tracking metadata of rp1 was released in the pool.
        Assert.Equal(2, _pool.Records("VDI").Count(v => v.Str("type") == "cbt_metadata"));

        // 3rd run: retention 2 removes rp1 and queues garbage collection.
        _pool.WriteDisk(disks[1], 0, [1, 2, 3]);
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        await WaitForAsync(async db => await db.Sessions.AnyAsync(s => s.Kind == SessionKind.RepositoryMaintenance && s.State == SessionState.Succeeded, Ct));
        await using (var db = await DbAsync())
        {
            Assert.Equal(2, await db.RestorePoints.CountAsync(r => r.State == RestorePointState.Available, Ct));
            Assert.False(await db.RestorePoints.AnyAsync(r => r.Id == rp1.Id, Ct));
        }

        // Restore rp2 to an alternate location; the restored disk equals the state at rp2.
        var contentAtRp2 = (byte[])contentAtRp1.Clone();
        new byte[CbtBitmap.BlockSize].Select((_, i) => (byte)i).ToArray().CopyTo(contentAtRp2, 2 * CbtBitmap.BlockSize);
        var targetSr = _pool.Uuid("SR", _pool.SecondSrRef);
        var restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto
            {
                RestorePointId = rp2.Id,
                Mode = RestoreModeDto.AlternateLocation,
                VmName = "fileserver-restored",
                DiskStorage = rp2.Disks.ToDictionary(d => d.DiskKey, _ => targetSr),
            },
            "fileserver",
            "test",
            Ct));
        Assert.Equal(SessionState.Succeeded, restore.State);
        var restoredVm = _pool.Records("VM").Single(v => v.Str("name_label") == "fileserver-restored");
        var restoredDisk = _pool.Records("VBD").Where(b => b.Str("VM") == RefOf("VM", restoredVm.Str("uuid"))).Select(b => b.Str("VDI"))
            .Select(r => _pool.Records("VDI").Single(v => v.Str("uuid") == _pool.Uuid("VDI", r)))
            .Single(v => v.Long("virtual_size") == DiskSize);
        Assert.Equal(contentAtRp2, _pool.DiskContent(restoredDisk.Str("uuid")));

        // Every restore point still verifies completely after GC.
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(repositoryId, Ct);
        await using (var db = await DbAsync())
        {
            foreach (var point in await db.RestorePoints.ToListAsync(Ct))
            {
                Assert.True((await repository.VerifyAsync(point.Id, VerifyDepth.Full, null, Ct)).Ok);
            }

            var log = await db.SessionLog.Where(l => l.SessionId == second.Id).Select(l => l.Message).ToListAsync(Ct);
            Assert.Contains(log, m => m.Contains("Cbt", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_disk_marked_nobak_is_not_backed_up_and_comes_back_empty_on_restore()
    {
        var (vmUuid, disks) = _pool.AddVm("appserver", DiskSize, DiskSize / 2);
        _pool.RenameDisk(disks[1], "appserver scratch [NOBAK]");
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        var queue = Services.GetRequiredService<SessionQueue>();

        var backup = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Succeeded, backup.State);
        var point = await LatestRestorePointAsync();
        Assert.Equal([disks[0]], point.Disks.Select(d => d.SourceDiskId));
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            var excluded = Assert.Single(reader.Manifest.ExcludedDisks);
            Assert.Equal((disks[1], "appserver scratch [NOBAK]", DiskSize / 2), (excluded.SourceDiskId, excluded.Name, excluded.CapacityBytes));
        }

        // The VM comes back with both disks; the one left out is empty, on the storage of the restored disk.
        var targetSr = _pool.Uuid("SR", _pool.SecondSrRef);
        var restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto
            {
                RestorePointId = point.Id,
                Mode = RestoreModeDto.AlternateLocation,
                VmName = "appserver-restored",
                DiskStorage = point.Disks.ToDictionary(d => d.DiskKey, _ => targetSr),
            },
            "appserver",
            "test",
            Ct));
        Assert.Equal(SessionState.Succeeded, restore.State);
        var restoredVm = _pool.Records("VM").Single(v => v.Str("name_label") == "appserver-restored");
        var restoredDisks = _pool.Records("VBD").Where(b => b.Str("VM") == RefOf("VM", restoredVm.Str("uuid")))
            .Select(b => _pool.Records("VDI").Single(v => v.Str("uuid") == _pool.Uuid("VDI", b.Str("VDI"))))
            .ToList();
        Assert.Equal(2, restoredDisks.Count);
        Assert.All(restoredDisks, d => Assert.Equal(_pool.SecondSrRef, d.Str("SR")));
        var empty = restoredDisks.Single(d => d.Long("virtual_size") == DiskSize / 2);
        Assert.DoesNotContain(_pool.DiskContent(empty.Str("uuid")), b => b != 0);

        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == backup.Id || l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("Disk appserver scratch [NOBAK] (3.0 MiB) is not backed up", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("Disk appserver scratch [NOBAK]: created empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Guest_processing_freezes_the_guest_for_the_snapshot_and_stores_the_guest_metadata()
    {
        var (vmUuid, _) = _pool.AddVm("sql01", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { IncludeUnselectedDatabase = true };
        await agent.StartAsync(Ct);
        var frozenAtSnapshot = false;
        _pool.OnSnapshot = _ => frozenAtSnapshot = agent.Frozen;

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        // The database the SQL writer did not include is only crash-consistent: a warning.
        Assert.Equal(SessionState.Warning, session.State);
        Assert.True(frozenAtSnapshot);
        Assert.False(agent.Frozen);
        Assert.Equal([AgentCommandKinds.PrepareBackup, AgentCommandKinds.Freeze, AgentCommandKinds.Thaw], agent.Commands);
        var point = await LatestRestorePointAsync();
        Assert.Equal("application", point.Consistency);
        Assert.Null(point.ConsistencyReason);
        Assert.True(File.Exists(Path.Combine(point.ManifestPath!, "guest-metadata.zst")));
        Assert.Equal(2, point.SqlDatabaseCount);

        // Guest metadata v2: file kinds, writer components kept after the thaw, consistency per database.
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            Assert.Equal(StoredGuestMetadata.FormatV2, reader.Manifest.Guest!.Format);
            Assert.Equal(2, reader.Manifest.SqlDatabaseCount);
            var stored = await StoredGuestMetadata.ReadAsync(reader, Ct);
            var shop = stored!.SqlDatabases.Single(d => d.Database == "shop");
            Assert.Equal("application", shop.Consistency);
            Assert.Equal("SQL01", shop.Server);
            Assert.Equal(@"D:\SQL\shop.mdf", shop.DataFiles.Single().FullPath());
            Assert.Equal(@"D:\SQL\shop_log.ldf", shop.LogFiles.Single().FullPath());
            var notSelected = stored.SqlDatabases.Single(d => d.Database == "notselected");
            Assert.Equal("crash", notSelected.Consistency);
            Assert.Contains("did not include this database", notSelected.ConsistencyReason, StringComparison.Ordinal);
            var writer = stored.Writers.Single(w => w.Name == "SqlServerWriter");
            Assert.Equal("Stable", writer.State);
            Assert.Equal([@"SQL01\shop", @"SQL01\model"], writer.Components);

            Assert.NotNull(reader.Manifest.VssDocuments);
            await using var blob = await reader.OpenBlobAsync(reader.Manifest.VssDocuments!, Ct);
            var documents = await System.Text.Json.JsonSerializer.DeserializeAsync<StoredVssDocuments>(blob, Backdafuckup.Server.Agents.AgentJson.Options, Ct);
            Assert.Equal("<BACKUP_COMPONENTS/>", documents!.BackupComponentsXml);
            Assert.Contains("WRITER_METADATA", documents.SqlWriterMetadataXml, StringComparison.Ordinal);
        }

        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("VSS prepared by the agent on SQL01 (10.0.0.5)", StringComparison.Ordinal) && m.Contains("2 SQL database(s) in MSSQLSERVER", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("application-consistent snapshot", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains(@"SQL database MSSQLSERVER\notselected is only crash-consistent", StringComparison.Ordinal));

        // Older restore points (count unknown) get the count from their guest metadata.
        await db.RestorePoints.Where(r => r.Id == point.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SqlDatabaseCount, (int?)null), Ct);
        Assert.Equal(1, await Services.GetRequiredService<SqlCatalogBackfill>().RunAsync(Ct));
        Assert.Equal(2, (await LatestRestorePointAsync()).SqlDatabaseCount);
    }

    [Fact]
    public async Task A_freeze_that_outlasts_the_vss_limit_gives_a_crash_consistent_backup_with_a_warning()
    {
        var (vmUuid, _) = _pool.AddVm("sql02", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { ThawVssResult = unchecked((int)0x80042314) };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Warning, session.State);
        var point = await LatestRestorePointAsync();
        Assert.Equal(RestorePointState.Available, point.State);
        Assert.Equal("crash", point.Consistency);
        Assert.Contains("10 s (VSS_E_HOLD_WRITES_TIMEOUT)", point.ConsistencyReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GuestFailurePolicy.CrashConsistentWithWarning, SessionState.Warning)]
    [InlineData(GuestFailurePolicy.FailVm, SessionState.Failed)]
    public async Task Without_an_agent_the_failure_policy_decides(GuestFailurePolicy policy, SessionState expected)
    {
        var (vmUuid, _) = _pool.AddVm("noagent", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, policy);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(expected, session.State);
        await using var db = await DbAsync();
        var points = await db.RestorePoints.Where(r => r.JobId == jobId).ToListAsync(Ct);
        if (policy == GuestFailurePolicy.FailVm)
        {
            Assert.Empty(points);
            Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
            Assert.Contains("No backdafuckup agent is installed in VM vm", (await db.SessionTasks.SingleAsync(t => t.SessionId == session.Id, Ct)).Error, StringComparison.Ordinal);
        }
        else
        {
            var point = Assert.Single(points);
            Assert.Equal("crash", point.Consistency);
            Assert.Contains("No backdafuckup agent is installed", point.ConsistencyReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_push_installation_falls_back_to_the_net48_build_and_waits_for_the_agent()
    {
        var (vmUuid, _) = _pool.AddVm("sql03", DiskSize);
        _pool.SetGuestAddresses(vmUuid, "10.0.0.9", "10.0.0.5");
        _remote.Reachable.Add("10.0.0.5");
        _remote.Runs = exe => System.Text.Encoding.UTF8.GetString(exe).Contains("net48", StringComparison.Ordinal);
        await using var agent = new FakeGuestAgent(Services, vmUuid);
        _remote.OnStarted = () => agent.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        var credentialId = await AddWindowsCredentialAsync(@"CORP\backup-admin", "secret");

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueAgentDeploymentAsync(
            new InstallAgentsDto(await ConnectionOfAsync(jobId), [new JobObjectDto(vmUuid, "sql03")], credentialId), "test", Ct));

        Assert.Equal(SessionState.Succeeded, session.State);
        const string folder = @"C$\Program Files\backdafuckup Agent\";
        Assert.Equal("agent net48", System.Text.Encoding.UTF8.GetString(_remote.Files[folder + "backdafuckup-agent.exe"]));
        var config = System.Text.Encoding.UTF8.GetString(_remote.Files[folder + "agent.json"]);
        Assert.Contains("https://backup.test:9555/", config, StringComparison.Ordinal);
        Assert.Contains("enrollmentSecret", config, StringComparison.Ordinal);
        Assert.Equal(
            ["start BackdafuckupAgent: stopped", "delete BackdafuckupAgent", "start BackdafuckupAgent: running"],
            _remote.Calls.Where(c => c.StartsWith("start", StringComparison.Ordinal) || c.StartsWith("stop", StringComparison.Ordinal) || c.StartsWith("delete", StringComparison.Ordinal)));
        Assert.Contains(@"create BackdafuckupAgent: ""C:\Program Files\backdafuckup Agent\backdafuckup-agent.exe"" run", _remote.Calls);
        var writes = _remote.Calls.Where(c => c.StartsWith("write", StringComparison.Ordinal)).ToList();
        Assert.EndsWith("agent.json", writes[2], StringComparison.Ordinal); // the secret after the program files
        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("No SMB connection to 10.0.0.9:445", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains(@"connected to 10.0.0.5:445 as CORP\backup-admin", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("The net10 build does not keep running on 10.0.0.5", StringComparison.Ordinal) && m.Contains("Trying the net48 build", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("The agent on SQL01 (10.0.0.5) is online", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_windows_password_fails_the_installation_with_the_address()
    {
        var (vmUuid, _) = _pool.AddVm("sql04", DiskSize);
        _pool.SetGuestAddresses(vmUuid, "10.0.0.6");
        _remote.Reachable.Add("10.0.0.6");
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        var credentialId = await AddWindowsCredentialAsync(@".\Administrator", "wrong");

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueAgentDeploymentAsync(
            new InstallAgentsDto(await ConnectionOfAsync(jobId), [new JobObjectDto(vmUuid, "sql04")], credentialId), "test", Ct));

        Assert.Equal(SessionState.Failed, session.State);
        await using var db = await DbAsync();
        var error = (await db.SessionTasks.SingleAsync(t => t.SessionId == session.Id, Ct)).Error;
        Assert.Contains(@"Logon as .\Administrator at 10.0.0.6:445 failed: wrong user name or password", error, StringComparison.Ordinal);
        Assert.Empty(_remote.Services);
    }

    [Fact]
    public async Task A_backup_with_guest_processing_installs_a_missing_agent_with_the_job_credentials()
    {
        var (vmUuid, _) = _pool.AddVm("sql05", DiskSize);
        _pool.SetGuestAddresses(vmUuid, "10.0.0.7");
        _remote.Reachable.Add("10.0.0.7");
        await using var agent = new FakeGuestAgent(Services, vmUuid);
        _remote.OnStarted = () => agent.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        var credentialId = await AddWindowsCredentialAsync(@"CORP\backup-admin", "secret");
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using (var db = await DbAsync())
        {
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.GuestCredentialId, credentialId), Ct);
        }

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.Equal("application", (await LatestRestorePointAsync()).Consistency);
        await using var check = await DbAsync();
        var log = await check.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("installing the agent with the job's Windows credentials", StringComparison.Ordinal));
    }

    private async Task<Guid> AddWindowsCredentialAsync(string user, string password)
    {
        await using var db = await DbAsync();
        var credential = new Credential
        {
            Kind = CredentialKind.Windows,
            Username = user,
            ProtectedSecret = Services.GetRequiredService<CredentialProtector>().Protect(password),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);
        return credential.Id;
    }

    private async Task<Guid> ConnectionOfAsync(Guid jobId)
    {
        await using var db = await DbAsync();
        return await db.Jobs.Where(j => j.Id == jobId).Select(j => j.ConnectionId).SingleAsync(Ct);
    }

    private async Task EnableGuestProcessingAsync(Guid jobId, GuestFailurePolicy policy)
    {
        await using var db = await DbAsync();
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.GuestProcessing, true).SetProperty(j => j.GuestFailurePolicy, policy), Ct);
    }

    [Fact]
    public async Task Disks_run_in_parallel_and_a_failing_disk_reports_its_own_error()
    {
        // HTTP export: the export of the 6 MiB disk breaks off after 3 MiB, the 2 MiB disk is complete.
        var (vmUuid, _) = _pool.AddVm("twodisks", DiskSize, DiskSize / 3);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await using (var db = await DbAsync())
        {
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.TransportMode, Backdafuckup.Core.DataMover.DiskTransportMode.HttpExportOnly), Ct);
        }

        var queue = Services.GetRequiredService<SessionQueue>();
        _pool.TruncateExportAfterBytes = 3 * 1024 * 1024;
        var failed = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Failed, failed.State);
        await using (var db = await DbAsync())
        {
            var log = await db.SessionLog.Where(l => l.SessionId == failed.Id).Select(l => l.Message).ToListAsync(Ct);
            Assert.Contains(log, m => m.Contains("Processing up to 2 of 2 disks in parallel", StringComparison.Ordinal));
            var error = (await db.SessionTasks.SingleAsync(t => t.SessionId == failed.Id, Ct)).Error;
            Assert.Contains("truncated", error, StringComparison.Ordinal);
            Assert.DoesNotContain("cancel", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(await db.RestorePoints.AnyAsync(r => r.State == RestorePointState.Available, Ct));
        }

        _pool.TruncateExportAfterBytes = null;
        var succeeded = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, succeeded.State);
        var point = await LatestRestorePointAsync();
        Assert.Equal([DiskSize, DiskSize / 3], point.Disks.OrderBy(d => d.DiskKey).Select(d => d.CapacityBytes));
        Assert.Equal(DiskSize + (DiskSize / 3), Assert.IsType<TransferMetricsDto>(succeeded.Metrics).ProcessedBytes);
    }

    [Fact]
    public async Task A_held_snapshot_disk_gives_a_warning_and_is_finished_by_the_background_retry()
    {
        var (vmUuid, _) = _pool.AddVm("busy", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 5);
        var queue = Services.GetRequiredService<SessionQueue>();
        _pool.DataDestroyInUse = int.MaxValue;

        var first = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Warning, first.State);
        Assert.Equal(RestorePointState.Available, (await LatestRestorePointAsync()).State);
        Assert.Contains(_pool.Records("VDI"), v => v.Bool("is_a_snapshot") && v.Str("type") == "user");
        await using (var db = await DbAsync())
        {
            Assert.Contains(await db.SessionLog.Where(l => l.SessionId == first.Id).Select(l => l.Message).ToListAsync(Ct), m => m.Contains("kept for now", StringComparison.Ordinal));

            // The background retry only touches objects that have been left alone for a while.
            await db.Artifacts.ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedUtc, DateTimeOffset.UtcNow.AddMinutes(-10)), Ct);
        }

        _pool.DataDestroyInUse = 0;
        await Services.GetRequiredService<StartupRecovery>().RetryPendingArtifactsAsync(Services.GetRequiredService<RunningSessions>(), Ct);

        Assert.DoesNotContain(_pool.Records("VM"), vm => vm.Bool("is_a_snapshot"));
        Assert.Contains(_pool.Records("VDI"), v => v.Str("type") == "cbt_metadata");
        await using (var db = await DbAsync())
        {
            Assert.False(await db.Artifacts.AnyAsync(a => a.State != ArtifactState.Done, Ct));
        }

        // The kept change tracking data makes the next backup incremental.
        var second = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, second.State);
        Assert.Equal(RestorePointKind.Incremental, (await LatestRestorePointAsync()).Kind);
    }

    [Fact]
    public async Task The_same_connection_opens_in_parallel_without_a_version_conflict()
    {
        // Several VMs of a job open the connection at once (automatic agent installations); the status bookkeeping
        // used to fail all but one with "expected to affect 1 row(s), but actually affected 0 row(s)".
        var (vmUuid, _) = _pool.AddVm("parallel", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 1);
        var connectionId = await ConnectionOfAsync(jobId);
        var manager = Services.GetRequiredService<ConnectionManager>();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => manager.OpenAsync(connectionId, Ct), Ct)));
        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }

        await using var db = await DbAsync();
        var connection = await db.Connections.AsNoTracking().SingleAsync(c => c.Id == connectionId, Ct);
        Assert.Equal(ConnectionStatus.Connected, connection.Status);
        Assert.Null(connection.LastError);
        Assert.NotNull(connection.LastConnectedUtc);
    }

    [Fact]
    public async Task Recovery_removes_snapshots_left_behind_by_a_crash()
    {
        var (vmUuid, _) = _pool.AddVm("crashy", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        Guid connectionId;
        await using (var db = await DbAsync())
        {
            connectionId = (await db.Jobs.FindAsync([jobId], Ct))!.ConnectionId;
        }

        // Simulate a crash in the middle of a backup: the snapshot exists, the ledger says "active".
        await using (var session = await Services.GetRequiredService<ConnectionManager>().OpenAsync(connectionId, Ct))
        {
            var backup = await session.BeginBackupAsync(new VmBackupRequest(vmUuid, jobId, Guid.NewGuid(), Guid.NewGuid(), "crash"), NullOperationLog.Instance,
                new DbArtifactLedger(Services.GetRequiredService<IDbContextFactory<AppDbContext>>(), connectionId, null, null, TimeProvider.System), Ct);
            GC.KeepAlive(backup); // never committed or disposed: the process "died"
        }

        Assert.Contains(_pool.Records("VM"), v => v.Bool("is_a_snapshot"));
        await Services.GetRequiredService<StartupRecovery>().RunAsync(Ct);

        Assert.DoesNotContain(_pool.Records("VM"), v => v.Bool("is_a_snapshot"));
        await using var check = await DbAsync();
        Assert.All(await check.Artifacts.ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Catalog"] = _postgres.GetConnectionString(),
            ["Engine:SchedulerInterval"] = "00:10:00",
            ["Agent:PackageDirectory"] = Path.Combine(_root, "agent"),
            ["Agent:ServerUrl"] = "https://backup.test:9555/",
            ["Agent:ServiceStableTime"] = "00:00:00.200",
            ["Agent:ServicePollInterval"] = "00:00:00.050",
            ["Agent:InstallWaitTimeout"] = "00:00:15",
        });
        foreach (var build in new[] { "net10", "net48" })
        {
            Directory.CreateDirectory(Path.Combine(_root, "agent", build));
            File.WriteAllText(Path.Combine(_root, "agent", build, "backdafuckup-agent.exe"), $"agent {build}");
            File.WriteAllText(Path.Combine(_root, "agent", build, "bdfu-vss.dll"), "vss");
        }

        builder.Services.AddSingleton<Backdafuckup.Smb.IRemoteWindowsConnector>(_remote);
        var paths = new ServerPaths(_root, Path.Combine(_root, "data"), Path.Combine(_root, "logs"));
        builder.Services.AddSingleton(paths);
        builder.Services.AddCatalog(builder.Configuration, paths);
        builder.Services.AddSingleton<IHypervisorProvider>(new XenProvider(
            new XenOptions
            {
                XapiRetryAttempts = 1,
                XapiRetryDelay = TimeSpan.FromMilliseconds(10),
                SnapshotReleaseTimeout = TimeSpan.FromMilliseconds(300),
            },
            (_, host) => new XapiTransport(new Uri($"https://{host}/"), _pool.CreateHandler())));
        builder.Services.AddBackupEngine(builder.Configuration);
        return builder.Build();
    }

    private async Task<(Guid JobId, Guid RepositoryId)> SeedAsync(string vmUuid, int retention)
    {
        var protector = Services.GetRequiredService<CredentialProtector>();
        var repoPath = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..6]);
        Guid repositoryUuid;
        await using (var repository = await LocalDirectoryRepository.CreateAsync(repoPath, Ct, 1024 * 1024))
        {
            repositoryUuid = repository.Info.RepositoryId;
        }

        await using var db = await DbAsync();
        var credential = new Credential { Username = "root", ProtectedSecret = protector.Protect("secret"), CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
        var connection = new HypervisorConnection { DisplayName = "lab-" + Guid.NewGuid().ToString("N")[..6], Address = "xcp1", Credential = credential };
        var repositoryEntity = new BackupRepositoryEntity { Name = "repo-" + Guid.NewGuid().ToString("N")[..6], Path = repoPath, RepositoryUuid = repositoryUuid, ChunkSize = 1024 * 1024, CompressionLevel = 3 };
        var job = new BackupJob
        {
            Name = "job-" + Guid.NewGuid().ToString("N")[..6],
            Connection = connection,
            Repository = repositoryEntity,
            RetentionPoints = retention,
            RetryCount = 0,
            Objects = [new BackupJobObject { NativeId = vmUuid, CachedName = "vm" }],
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(Ct);
        return (job.Id, repositoryEntity.Id);
    }

    private async Task<SessionEntity> RunAsync(Task<Guid> enqueue)
    {
        var id = await enqueue;
        SessionEntity? session = null;
        await WaitForAsync(async db =>
        {
            session = await db.Sessions.AsNoTracking().FirstAsync(s => s.Id == id, Ct);
            return session.State is not (SessionState.Queued or SessionState.Running or SessionState.Stopping);
        });
        return session!;
    }

    private async Task<RestorePointEntity> LatestRestorePointAsync()
    {
        await using var db = await DbAsync();
        return await db.RestorePoints.Include(r => r.Disks).AsNoTracking().OrderByDescending(r => r.CreatedUtc).FirstAsync(Ct);
    }

    private async Task WaitForAsync(Func<AppDbContext, Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            await using (var db = await DbAsync())
            {
                if (await condition(db))
                {
                    return;
                }
            }

            await Task.Delay(200, Ct);
        }

        await using var dump = await DbAsync();
        var log = string.Join("\n", await dump.SessionLog.OrderBy(l => l.Id).Select(l => $"{l.Severity}: {l.Message}").ToListAsync(Ct));
        Assert.Fail("Condition not reached in time. Session log:\n" + log);
    }

    private Task<AppDbContext> DbAsync() => Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);

    private string RefOf(string table, string uuid) =>
        _pool.Records(table).Select((r, i) => (r, i)).Any() ? ResolveRef(table, uuid) : string.Empty;

    private string ResolveRef(string table, string uuid)
    {
        using var handler = _pool.CreateHandler();
        using var transport = new XapiTransport(new Uri("https://xcp1/"), handler);
        var session = transport.CallAsync("session.login_with_password", ["root", "secret", "1.0", "test"], Ct).GetAwaiter().GetResult()!.GetValue<string>();
        return transport.CallAsync($"{table}.get_by_uuid", [session, uuid], Ct).GetAwaiter().GetResult()!.GetValue<string>();
    }
}

internal static class PoolRecordExtensions
{
    public static string Str(this JsonObject record, string field) => XapiRecord.Str(record, field);

    public static bool Bool(this JsonObject record, string field) => XapiRecord.Bool(record, field);

    public static long Long(this JsonObject record, string field) => XapiRecord.Long(record, field);
}
