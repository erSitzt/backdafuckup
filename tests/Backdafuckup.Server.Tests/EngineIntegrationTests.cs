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
public sealed partial class EngineIntegrationTests : IAsyncLifetime
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
    public async Task Replacing_the_original_moves_its_jobs_to_the_restored_vm()
    {
        var (vmUuid, _) = _pool.AddVm("erp01", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        var queue = Services.GetRequiredService<SessionQueue>();
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var point = await LatestRestorePointAsync();

        var restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, ReplaceExisting = true, DiskStorage = new Dictionary<string, string>() },
            "erp01", "test", Ct));

        // The original keeps its id (renamed, halted); the restored VM has a new one, and the job now names it (lab check H13).
        Assert.Equal(SessionState.Succeeded, restore.State);
        var restored = _pool.Records("VM").Single(v => v.Str("name_label") == "erp01").Str("uuid");
        Assert.NotEqual(vmUuid, restored);
        await using (var db = await DbAsync())
        {
            var member = await db.JobObjects.SingleAsync(o => o.JobId == jobId, Ct);
            Assert.Equal((restored, "erp01"), (member.NativeId, member.CachedName));
            var log = await db.SessionLog.Where(l => l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
            Assert.Contains(log, m => m.Contains($"now backs up the restored VM 'erp01' ({restored}) instead of the replaced one ({vmUuid}); its next run is a full backup", StringComparison.Ordinal));
        }

        // Its next run backs up the restored VM, as a new chain.
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var next = await LatestRestorePointAsync();
        Assert.Equal((restored, RestorePointKind.Full), (next.VmNativeId, next.Kind));

        // The old restore point to the original location once more: it replaces the VM in use now, not the renamed one.
        restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, ReplaceExisting = true, DiskStorage = new Dictionary<string, string>() },
            "erp01", "test", Ct));
        Assert.Equal(SessionState.Succeeded, restore.State);
        var third = _pool.Records("VM").Single(v => v.Str("name_label") == "erp01").Str("uuid");
        Assert.DoesNotContain(third, new[] { vmUuid, restored });
        Assert.StartsWith("erp01 (replaced", _pool.Records("VM").Single(v => v.Str("uuid") == restored).Str("name_label"), StringComparison.Ordinal);
        await using (var db = await DbAsync())
        {
            Assert.Equal(third, (await db.JobObjects.SingleAsync(o => o.JobId == jobId, Ct)).NativeId);
            var replacements = await db.VmReplacements.OrderBy(r => r.CreatedUtc).Select(r => new { r.ReplacedVmId, r.VmId }).ToListAsync(Ct);
            Assert.Equal([new { ReplacedVmId = vmUuid, VmId = restored }, new { ReplacedVmId = restored, VmId = third }], replacements);
            var log = await db.SessionLog.Where(l => l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
            Assert.Contains(log, m => m.Contains($"The original VM is erp01 ({restored}): it replaced the backed-up VM ({vmUuid})", StringComparison.Ordinal));
        }
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
        Assert.True(agent.LastPrepareCopyOnly);
        var point = await LatestRestorePointAsync();
        Assert.Equal("application", point.Consistency);
        Assert.Null(point.ConsistencyReason);
        Assert.True(File.Exists(Path.Combine(point.ManifestPath!, "guest-metadata.zst")));
        Assert.Equal(2, point.SqlDatabaseCount);
        Assert.False(point.DomainController);
        Assert.Null(point.AdDomain);

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
    public async Task A_domain_controller_gets_a_vss_full_backup_and_its_directory_facts_in_the_restore_point()
    {
        var (vmUuid, _) = _pool.AddVm("dc01", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { DomainController = true, SqlDatabases = [] };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.False(agent.LastPrepareCopyOnly);
        var point = await LatestRestorePointAsync();
        Assert.Equal("application", point.Consistency);
        Assert.True(point.DomainController);
        Assert.Equal("bdfu-lab.local", point.AdDomain);
        Assert.Equal(0, point.SqlDatabaseCount);

        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            Assert.True(reader.Manifest.DomainController);
            Assert.Equal("bdfu-lab.local", reader.Manifest.AdDomain);
            var ad = (await StoredGuestMetadata.ReadAsync(reader, Ct))!.ActiveDirectory!;
            Assert.Equal("application", ad.Consistency);
            Assert.Null(ad.ConsistencyReason);
            Assert.Equal(@"C:\Windows\NTDS\ntds.dit", ad.DitPath);
            Assert.Equal("BDFU-LAB", ad.DomainNetbios);
            Assert.Equal(20480, ad.HighestCommittedUsn);
            Assert.True(ad.RecycleBin);
            Assert.Equal(180, ad.TombstoneLifetimeDays);
        }

        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("DC01 is a domain controller of domain bdfu-lab.local; VSS full backup", StringComparison.Ordinal));

        // Restore points from before the role was recorded get it from their guest metadata.
        await db.RestorePoints.Where(r => r.Id == point.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DomainController, (bool?)null).SetProperty(r => r.AdDomain, (string?)null), Ct);
        Assert.Equal(1, await Services.GetRequiredService<SqlCatalogBackfill>().RunAsync(Ct));
        point = await LatestRestorePointAsync();
        Assert.True(point.DomainController);
        Assert.Equal("bdfu-lab.local", point.AdDomain);
        Assert.Equal(0, point.SqlDatabaseCount);

        // A VM restore gets a new VM generation id: Windows restores AD non-authoritatively.
        var queue = Services.GetRequiredService<SessionQueue>();
        var backedUp = _pool.Records("VM").Single(v => v.Str("name_label") == "dc01").Str("generation_id");
        var restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "dc01-copy", DiskStorage = new Dictionary<string, string>() },
            "dc01", "test", Ct));
        Assert.Equal(SessionState.Succeeded, restore.State);
        var copy = _pool.Records("VM").Single(v => v.Str("name_label") == "dc01-copy");
        Assert.NotEqual(backedUp, copy.Str("generation_id"));
        log = await db.SessionLog.Where(l => l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("dc01 was a domain controller of domain bdfu-lab.local", StringComparison.Ordinal) && m.Contains("non-authoritatively", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains($"has a new VM generation id ({copy.Str("generation_id")}, backup: {backedUp})", StringComparison.Ordinal));

        // A pool that keeps the id, and a copy powered on beside the original: warnings.
        _pool.ImportKeepsGenerationId = true;
        restore = await RunAsync(queue.EnqueueRestoreAsync(
            new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "dc01-copy2", PowerOn = true, DiskStorage = new Dictionary<string, string>() },
            "dc01", "test", Ct));
        Assert.Equal(SessionState.Warning, restore.State);
        log = await db.SessionLog.Where(l => l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains($"kept the VM generation id of the backup ({backedUp})", StringComparison.Ordinal) && m.Contains("USN rollback", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("'dc01-copy2' is a copy of the domain controller dc01 and is powered on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_domain_controller_without_a_vm_generation_id_warns_in_every_backup_and_at_its_restore()
    {
        // A VM created from a template without a generation id has none, and XAPI gives its copies none either.
        var (vmUuid, _) = _pool.AddVm("dc02", DiskSize);
        _pool.RemoveGenerationId(vmUuid);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { DomainController = true, SqlDatabases = [] };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Warning, session.State);
        var point = await LatestRestorePointAsync();
        Assert.Equal("application", point.Consistency);
        Assert.True(point.DomainController);
        Assert.Null(point.Vm!.GenerationId);
        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("Domain controller dc02: its VM has no VM generation id (created from a template without one)", StringComparison.Ordinal)
            && m.Contains($"xe vm-param-get uuid={vmUuid} param-name=generation_id", StringComparison.Ordinal));

        var restore = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueRestoreAsync(
            new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "dc02-copy", DiskStorage = new Dictionary<string, string>() },
            "dc02", "test", Ct));
        Assert.Equal(SessionState.Warning, restore.State);
        var copy = _pool.Records("VM").Single(v => v.Str("name_label") == "dc02-copy");
        Assert.Equal(string.Empty, copy.Str("generation_id"));
        log = await db.SessionLog.Where(l => l.SessionId == restore.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains($"The restored VM 'dc02-copy' ({copy.Str("uuid")}) has no VM generation id", StringComparison.Ordinal)
            && m.Contains("The backed-up VM dc02 had none either", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_domain_controller_whose_freeze_runs_out_keeps_a_consistent_directory_through_its_ifm_copy()
    {
        var (vmUuid, _) = _pool.AddVm("dc03", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { DomainController = true, SqlDatabases = [], ThawVssResult = unchecked((int)0x80042314) };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        // The VM snapshot outlasted the 10 s of VSS: the restore point is crash-consistent, its directory is not.
        Assert.Equal(SessionState.Warning, session.State);
        var point = await LatestRestorePointAsync();
        Assert.Equal("crash", point.Consistency);
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            var ad = (await StoredGuestMetadata.ReadAsync(reader, Ct))!.ActiveDirectory!;
            Assert.Equal("application", ad.Consistency);
            Assert.Contains("the copy made with ntdsutil IFM before the snapshot is consistent (Windows released the writes", ad.ConsistencyReason, StringComparison.Ordinal);
            Assert.Equal(@"C:\ProgramData\backdafuckup\ad-ifm", ad.IfmPath);
        }

        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("a consistent copy of the database (96.0 MB) was made with ntdsutil IFM in 2.5 s before the snapshot; AD records it as a backup", StringComparison.Ordinal));

        // After the snapshot the agent deletes the copy again.
        for (var i = 0; i < 50 && !agent.Commands.Contains(AgentCommandKinds.Cleanup); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Contains(AgentCommandKinds.Cleanup, agent.Commands);
    }

    [Fact]
    public async Task A_domain_controller_without_an_ifm_copy_warns()
    {
        var (vmUuid, _) = _pool.AddVm("dc04", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using var agent = new FakeGuestAgent(Services, vmUuid) { DomainController = true, SqlDatabases = [], IfmError = "not enough free space on C:\\ for the copy of the directory" };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Warning, session.State);
        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("Active Directory: no consistent copy with ntdsutil IFM (not enough free space", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_domain_controller_with_sql_log_backups_stays_copy_only_with_a_warning()
    {
        var (vmUuid, _) = _pool.AddVm("dc02", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using (var db = await DbAsync())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == jobId, Ct);
            job.SqlBackup.Enabled = true;
            await db.SaveChangesAsync(Ct);
        }

        await using var agent = new FakeGuestAgent(Services, vmUuid) { DomainController = true };
        await agent.StartAsync(Ct);
        var queue = Services.GetRequiredService<SessionQueue>();

        // No restore point tells yet whether SQL Server runs here: VSS full.
        var first = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, first.State);
        Assert.False(agent.LastPrepareCopyOnly);
        Assert.Equal(1, (await LatestRestorePointAsync()).SqlDatabaseCount);

        // Now its SQL databases are known, and the job takes their log backups: copy-only, so SQL Server records no full.
        var second = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Warning, second.State);
        Assert.True(agent.LastPrepareCopyOnly);
        Assert.True((await LatestRestorePointAsync()).DomainController);
        await using (var db = await DbAsync())
        {
            var log = await db.SessionLog.Where(l => l.SessionId == second.Id).Select(l => l.Message).ToListAsync(Ct);
            Assert.Contains(log, m => m.Contains("DC01 is a domain controller of domain bdfu-lab.local and also runs SQL Server databases", StringComparison.Ordinal)
                && m.Contains("stays copy-only", StringComparison.Ordinal));
        }
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

    [Fact]
    public async Task Sql_snapshot_suspends_the_databases_for_the_snapshot_and_records_them()
    {
        var (vmUuid, _) = _pool.AddVm("sql01", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await EnableSqlSnapshotAsync(jobId, "bdfu_backup", "Sql-Secret-1");
        await using var agent = new FakeGuestAgent(Services, vmUuid) { IncludeUnselectedDatabase = true, SqlSnapshotCapable = true, SqlSnapshotWait = TimeSpan.FromSeconds(1.5) };
        await agent.StartAsync(Ct);
        var suspendedAtSnapshot = false;
        _pool.OnSnapshot = _ => suspendedAtSnapshot = agent.SqlSuspended;

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        // Every database was recorded: no warning, although files outside SQL Server are crash-consistent.
        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.True(suspendedAtSnapshot);
        Assert.False(agent.SqlSuspended);
        Assert.Equal(SqlJobSignals.SnapshotTaken, agent.LastSignal);
        Assert.Equal(("bdfu_backup", SqlAccountKinds.Sql), (agent.LastSqlAccount!.UserName, agent.LastSqlAccount.Kind));
        Assert.False(agent.LastSqlCopyOnly);
        lock (agent.Commands)
        {
            // VSS gathers the metadata only: no VSS freeze, the VSS backup is ended right away.
            Assert.Equal([AgentCommandKinds.PrepareBackup, AgentCommandKinds.Abort, AgentCommandKinds.SqlJob], agent.Commands);
        }

        var point = await LatestRestorePointAsync();
        Assert.Equal("crash", point.Consistency);
        Assert.Contains("SQL Server databases are application-consistent", point.ConsistencyReason, StringComparison.Ordinal);
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            var stored = await StoredGuestMetadata.ReadAsync(reader, Ct);
            Assert.All(stored!.SqlDatabases, d => Assert.Equal(("application", (string?)null), (d.Consistency, d.ConsistencyReason)));
            var snapshot = await StoredSqlSnapshot.ReadAsync(reader, Ct);
            var instance = Assert.Single(snapshot!.Instances);
            Assert.Equal([0x54, 0x41, 0x50, 0x45], instance.Metadata);
            Assert.All(instance.Databases, d => Assert.Equal(SqlSnapshotStates.Recorded, d.State));
            Assert.Equal(2500, snapshot.SuspendedMs);
        }

        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct);
        // The wait for a running log backup is named apart from the time the writes were suspended.
        Assert.Contains(log, m => m.Contains("waited 1.", StringComparison.Ordinal) && m.Contains(" s for a running SQL Server log or full backup, then suspended the writes of 2", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("suspended the writes of 2 SQL Server database(s)", StringComparison.Ordinal) && m.Contains("MSSQLSERVER (16.0.4135.4): shop, notselected", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("Guest processing (SQL Server snapshot): 2 database(s) recorded in msdb as full snapshot backups (in the backup chain)", StringComparison.Ordinal) && m.Contains("Writes were suspended for 2.5 s", StringComparison.Ordinal));
        Assert.DoesNotContain(log, m => m.Contains("Sql-Secret-1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "FULL", false, null, "This full backup started the log chain of MSSQLSERVER\\shop, MSSQLSERVER\\notselected (FULL recovery, no full backup before)")]
    [InlineData(false, "FULL", true, 3000, "No log backup for more than 24 hours: MSSQLSERVER\\shop (last one 2 days ago), MSSQLSERVER\\notselected (last one 2 days ago).")]
    [InlineData(true, "FULL", true, null, "No log backup for more than 24 hours: MSSQLSERVER\\shop (none yet), MSSQLSERVER\\notselected (none yet).")]
    [InlineData(true, "FULL", false, null, null)]
    [InlineData(false, "FULL", true, 30, null)]
    [InlineData(false, "SIMPLE", false, null, null)]
    public async Task Sql_snapshot_records_full_or_copy_only_backups_and_warns_about_logs_that_only_log_backups_free(
        bool copyOnly, string model, bool hadChain, int? logAgeMinutes, string? warning)
    {
        var (vmUuid, _) = _pool.AddVm("sql05", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await EnableSqlSnapshotAsync(jobId, "bdfu_backup", "Sql-Secret-5");
        await using (var db = await DbAsync())
        {
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.SqlSnapshotCopyOnly, copyOnly), Ct);
        }

        await using var agent = new FakeGuestAgent(Services, vmUuid) { IncludeUnselectedDatabase = true, SqlSnapshotCapable = true, SqlLogFacts = (model, hadChain, logAgeMinutes) };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(copyOnly, agent.LastSqlCopyOnly);
        Assert.Equal(warning is null ? SessionState.Succeeded : SessionState.Warning, session.State);
        await using var context = await DbAsync();
        var log = await context.SessionLog.Where(l => l.SessionId == session.Id).Select(l => new { l.Severity, l.Message }).ToListAsync(Ct);
        Assert.Contains(log, l => l.Message.Contains($"recorded in msdb as {(copyOnly ? "copy-only snapshot backups" : "full snapshot backups (in the backup chain)")}", StringComparison.Ordinal));
        var warnings = log.Where(l => l.Severity == LogSeverity.Warning && l.Message.Contains("Guest processing (SQL Server snapshot): ", StringComparison.Ordinal)).Select(l => l.Message).ToList();
        if (warning is null)
        {
            Assert.Empty(warnings);
        }
        else
        {
            Assert.Contains(warnings, m => m.Contains(warning, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Sql_snapshot_reports_databases_that_could_not_be_suspended()
    {
        var (vmUuid, _) = _pool.AddVm("sql02", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await EnableSqlSnapshotAsync(jobId, "bdfu_backup", "Sql-Secret-2");
        await using var agent = new FakeGuestAgent(Services, vmUuid) { IncludeUnselectedDatabase = true, SqlSnapshotCapable = true };
        agent.SqlDatabasesFailingSuspend.Add("notselected");
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Warning, session.State);
        var point = await LatestRestorePointAsync();
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using var reader = await repository.OpenRestorePointAsync(point.Id, Ct);
        var stored = await StoredGuestMetadata.ReadAsync(reader, Ct);
        Assert.Equal("application", stored!.SqlDatabases.Single(d => d.Database == "shop").Consistency);
        var failed = stored.SqlDatabases.Single(d => d.Database == "notselected");
        Assert.Equal("crash", failed.Consistency);
        Assert.Contains("suspending failed: permission denied", failed.ConsistencyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sql_snapshot_resumes_without_recording_when_the_vm_snapshot_fails()
    {
        var (vmUuid, _) = _pool.AddVm("sql03", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await EnableSqlSnapshotAsync(jobId, "bdfu_backup", "Sql-Secret-3");
        await using var agent = new FakeGuestAgent(Services, vmUuid) { SqlSnapshotCapable = true };
        await agent.StartAsync(Ct);
        _pool.OnSnapshot = _ => throw new Backdafuckup.Testing.Xapi.FakeXapiError("SR_BACKEND_FAILURE_44", "", "There is insufficient space", "");

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Equal(SqlJobSignals.Abort, agent.LastSignal);
        Assert.False(agent.SqlSuspended);
    }

    [Theory]
    [InlineData(false, "cannot suspend SQL Server for a snapshot yet (it updates itself after a server update); VSS is used for this VM.")]
    [InlineData(true, "could not suspend any SQL Server database (MSSQLSERVER: SQL Server 15.0.4382.1 has no T-SQL snapshot backup (SQL Server 2022 or later)); VSS is used for this VM.")]
    public async Task Sql_snapshot_falls_back_to_vss_when_nothing_can_be_suspended(bool capable, string reason)
    {
        // An agent that is not updated yet, or a VM whose SQL Server is older than 2022: VSS as in a VSS job.
        var (vmUuid, _) = _pool.AddVm(capable ? "sql2019" : "sql04", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await EnableSqlSnapshotAsync(jobId, "bdfu_backup", "Sql-Secret-4");
        await using var agent = new FakeGuestAgent(Services, vmUuid) { SqlSnapshotCapable = capable, SqlOlderThan2022 = capable };
        await agent.StartAsync(Ct);
        var frozenAtSnapshot = false;
        _pool.OnSnapshot = _ => frozenAtSnapshot = agent.Frozen;

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.True(frozenAtSnapshot);
        lock (agent.Commands)
        {
            Assert.Equal(
                capable
                    ? [AgentCommandKinds.PrepareBackup, AgentCommandKinds.Abort, AgentCommandKinds.SqlJob, AgentCommandKinds.PrepareBackup, AgentCommandKinds.Freeze, AgentCommandKinds.Thaw]
                    : [AgentCommandKinds.PrepareBackup, AgentCommandKinds.Freeze, AgentCommandKinds.Thaw],
                agent.Commands);
        }

        var point = await LatestRestorePointAsync();
        Assert.Equal("application", point.Consistency);
        await using var db = await DbAsync();
        var log = await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => new { l.Severity, l.Message }).ToListAsync(Ct);
        var line = Assert.Single(log, l => l.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogSeverity.Info, line.Severity);
    }

    [Fact]
    public async Task Sql_snapshot_without_an_account_uses_vss_and_warns()
    {
        var (vmUuid, _) = _pool.AddVm("sql06", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 2);
        await EnableGuestProcessingAsync(jobId, GuestFailurePolicy.CrashConsistentWithWarning);
        await using (var db = await DbAsync())
        {
            // E.g. the account was deleted before deleting was refused: the job has the method but no account.
            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.GuestProcessingMode, GuestProcessingMode.SqlSnapshot).SetProperty(j => j.SqlCredentialId, (Guid?)null), Ct);
        }

        await using var agent = new FakeGuestAgent(Services, vmUuid) { SqlSnapshotCapable = true };
        await agent.StartAsync(Ct);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));

        Assert.Equal(SessionState.Warning, session.State);
        lock (agent.Commands)
        {
            Assert.Equal([AgentCommandKinds.PrepareBackup, AgentCommandKinds.Freeze, AgentCommandKinds.Thaw], agent.Commands);
        }

        Assert.Equal("application", (await LatestRestorePointAsync()).Consistency);
        await using var context = await DbAsync();
        var log = await context.SessionLog.Where(l => l.SessionId == session.Id && l.Severity == LogSeverity.Warning).Select(l => l.Message).ToListAsync(Ct);
        Assert.Contains(log, m => m.Contains("SQL Server snapshot: the job has no SQL Server account", StringComparison.Ordinal) && m.Contains("VSS is used for VM ", StringComparison.Ordinal));
    }

    private async Task EnableSqlSnapshotAsync(Guid jobId, string userName, string password)
    {
        await using var db = await DbAsync();
        var credential = new Credential
        {
            Kind = CredentialKind.SqlServer,
            Username = userName,
            ProtectedSecret = Services.GetRequiredService<CredentialProtector>().Protect(password),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);
        await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.GuestProcessingMode, GuestProcessingMode.SqlSnapshot).SetProperty(j => j.SqlCredentialId, credential.Id), Ct);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_names_what_an_interrupted_sql_restore_may_have_left_in_the_vm(bool overwrite)
    {
        var request = new SqlRestoreRequestDto(Guid.NewGuid(), "SQLEXPRESS", "shop", "vm-1", "SQLEXPRESS", Guid.NewGuid(), overwrite ? "shop" : "shop_copy", SqlRestoreFiles.Original, null, null, overwrite);
        string[] files = overwrite ? [@"D:\SQL\shop.mdf.bdfu-new", @"L:\Log\shop_log.ldf.bdfu-new"] : [@"D:\SQL\shop_copy.mdf", @"L:\Log\shop_copy_log.ldf"];
        Guid planned, early;
        await using (var db = await DbAsync())
        {
            var parameters = System.Text.Json.JsonSerializer.Serialize(request);
            SessionEntity Session(string? withFiles) => new()
            {
                Kind = SessionKind.SqlRestore, Name = "SQL restore", TriggeredBy = "test", State = SessionState.Running, CreatedUtc = DateTimeOffset.UtcNow,
                ServerInstanceId = "an earlier process", Parameters = withFiles,
            };
            var withPlan = Session(Backdafuckup.Server.Sql.SqlRestoreRecovery.WithPlannedFiles(parameters, files));
            var withoutPlan = Session(parameters);
            db.Sessions.AddRange(withPlan, withoutPlan);
            await db.SaveChangesAsync(Ct);
            (planned, early) = (withPlan.Id, withoutPlan.Id);
        }

        await Services.GetRequiredService<StartupRecovery>().RunAsync(Ct);

        await using var check = await DbAsync();
        var recovered = await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == planned, Ct);
        var summary = recovered.Summary!;
        Assert.Contains(string.Join(", ", files), summary, StringComparison.Ordinal);
        Assert.Contains(overwrite ? "check that [shop] is online" : "the backup server checks whether [shop_copy] is attached with the restored files", summary, StringComparison.Ordinal);

        // Only a restore as a new database that wrote files is checked and cleaned up later (SqlRestoreCleanup); the agent
        // finishes or rolls back an overwrite on its own.
        Assert.Equal(!overwrite, recovered.Parameters!.Contains(Backdafuckup.Server.Sql.SqlRestoreRecovery.CleanupPendingJson, StringComparison.Ordinal));
        Assert.DoesNotContain(Backdafuckup.Server.Sql.SqlRestoreRecovery.CleanupPendingJson, (await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == early, Ct)).Parameters!, StringComparison.Ordinal);
        Assert.Equal(
            $"The backup service was restarted before this restore of [{request.DatabaseName}] to instance SQLEXPRESS of the target VM wrote any file; nothing was changed.",
            (await check.Sessions.AsNoTracking().SingleAsync(s => s.Id == early, Ct)).Summary);
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
            ["SqlBackup:TickInterval"] = "01:00:00",
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
