using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Hosting;
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

        // 2nd run: incremental, reads only changed blocks.
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
        });
        var paths = new ServerPaths(_root, Path.Combine(_root, "data"), Path.Combine(_root, "logs"));
        builder.Services.AddSingleton(paths);
        builder.Services.AddCatalog(builder.Configuration, paths);
        builder.Services.AddSingleton<IHypervisorProvider>(new XenProvider(
            new XenOptions { CbtBitOrder = CbtBitOrder.MostSignificantBitFirst, XapiRetryAttempts = 1, XapiRetryDelay = TimeSpan.FromMilliseconds(10) },
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
