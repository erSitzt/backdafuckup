using System.Net;
using Backdafuckup.Core.Operations;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.InstantRecovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Backdafuckup.Server.Tests;

/// <summary>Instant recovery in the catalog (spec 2, task 5): resume after a restart, its restore point kept, its objects left alone.</summary>
public sealed partial class EngineIntegrationTests
{
    private const string IrExportPath = "/bdfu/ir-test";

    private static readonly NfsCaller Esx = new(new AuthSys(0, "esx01", 0, 0, []), new IPEndPoint(IPAddress.Loopback, 900));

    [Fact]
    public async Task An_instant_recovery_survives_a_restart_of_the_service_and_keeps_its_restore_point()
    {
        var (vmUuid, disks) = _pool.AddVm("ir-vm", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 1);
        var queue = Services.GetRequiredService<SessionQueue>();
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var point = await LatestRestorePointAsync();
        var disk = Assert.Single(point.Disks);
        var content = _pool.DiskContent(disks[0]);

        // As the runner leaves an instant recovery when the process ends: running in the catalog, its namespace on disk.
        var nfs = Services.GetRequiredService<NfsServerHost>();
        var overlay = Path.Combine(nfs.OverlayDirectory, "ir-test");
        const long exportId = 0x7701;
        using (var fs = InstantRecoveryFileSystem.Open(overlay, exportId, nfs.HandleKey, _ => new UnreadBase(disk.CapacityBytes), new InstantRecoveryFsOptions { FreeSpaceReserveBytes = 0 }))
        {
            fs.CreateDirectory("ir-vm");
            fs.CreateBaseFile("ir-vm/disk0-flat.vmdk", InstantRecoverySessionRunner.BaseKey(point.Id, disk.DiskKey), disk.CapacityBytes);
        }

        Guid sessionId, artifactId;
        await using (var db = await DbAsync())
        {
            var connectionId = (await db.Jobs.FindAsync([jobId], Ct))!.ConnectionId;
            var session = new SessionEntity
            {
                Kind = SessionKind.InstantRecovery, Name = "Instant recovery ir-vm", TriggeredBy = "test", State = SessionState.Running,
                CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5), StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-5), ServerInstanceId = "an earlier process",
            };
            db.Sessions.Add(session);
            db.InstantRecoveries.Add(new InstantRecoveryEntity
            {
                SessionId = session.Id, ConnectionId = connectionId, HostId = "host-1", HostName = "esx01", DatastoreName = "bdfu-ir-test", DatastoreId = "datastore-9",
                ExportPath = IrExportPath, ExportId = exportId, AllowedClients = ["127.0.0.1"], OverlayDirectory = overlay, State = InstantRecoveryState.Running,
                CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
                Vms = [new InstantRecoveryVmEntity { RestorePointId = point.Id, Name = "ir-vm (instant)", Directory = "ir-vm", VmId = "vm-77", DiskFiles = new() { [disk.DiskKey] = "ir-vm/disk0.vmdk" } }],
            });
            var artifact = new ProviderArtifactEntity
            {
                ConnectionId = connectionId, SessionId = session.Id, Kind = "vsphere.nas-datastore", NativeId = "datastore-9", State = ArtifactState.Active,
                CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow.AddHours(-1),
            };
            db.Artifacts.Add(artifact);
            await db.SaveChangesAsync(Ct);
            (sessionId, artifactId) = (session.Id, artifact.Id);
        }

        // Startup: it is queued again (not failed), its runner serves the export, and the cleanup leaves its datastore alone.
        await Services.GetRequiredService<StartupRecovery>().RunAsync(Ct);
        var export = await WaitForExportAsync();
        var file = await LookupAsync(export, "ir-vm", "disk0-flat.vmdk");
        var before = await ReadAllAsync(export, file, disk.CapacityBytes);
        Assert.True(content.AsSpan().SequenceEqual(before));
        var written = Enumerable.Repeat((byte)0xab, 4096).ToArray();
        await export.FileSystem.WriteAsync(Esx, file, 65536, written, NfsStable.FileSync, Ct);
        await using (var db = await DbAsync())
        {
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct);
            Assert.Equal(SessionState.Running, session.State);
            Assert.True(session.StartedUtc < DateTimeOffset.UtcNow.AddMinutes(-4), "the resumed session keeps when it started");
            Assert.Equal(ArtifactState.Active, (await db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId, Ct)).State);
            Assert.Equal((sessionId, "ir-vm (instant)"), await InstantRecoveryCatalog.FindUseAsync(db, point.Id, Ct));
        }

        // Retention (1 point) keeps the restore point the instant recovery runs from.
        _pool.WriteDisk(disks[0], 0, [9, 9, 9]);
        var second = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, second.State);
        await using (var db = await DbAsync())
        {
            Assert.True(await db.RestorePoints.AnyAsync(r => r.Id == point.Id, Ct));
            Assert.Contains(await db.SessionLog.Where(l => l.SessionId == second.Id).Select(l => l.Message).ToListAsync(Ct),
                m => m.Contains("runs an instant recovery and is removed after it ends", StringComparison.Ordinal));
        }

        // The service stops: the session stays running in the catalog. The next start resumes it with what ESXi wrote.
        await _host!.StopAsync(Ct);
        await Services.GetRequiredService<RepositoryManager>().DisposeAsync();
        _host.Dispose();
        _host = BuildHost();
        await using (var db = await DbAsync())
        {
            Assert.Equal(SessionState.Running, (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, Ct)).State);
            var messages = await db.SessionLog.Where(l => l.SessionId == sessionId).Select(l => l.Message).ToListAsync(Ct);
            Assert.True(messages.Any(m => m.Contains("the instant recovery's datastore is unavailable until the service runs again", StringComparison.Ordinal)), string.Join(" | ", messages));
        }

        await _host.StartAsync(Ct);
        export = await WaitForExportAsync();
        file = await LookupAsync(export, "ir-vm", "disk0-flat.vmdk");
        var after = await ReadAllAsync(export, file, disk.CapacityBytes);
        Assert.True(written.AsSpan().SequenceEqual(after.AsSpan(65536, 4096)));
        Assert.True(content.AsSpan(0, 65536).SequenceEqual(after.AsSpan(0, 65536)));
        Assert.True(content.AsSpan(65536 + 4096).SequenceEqual(after.AsSpan(65536 + 4096)));
        await using (var db = await DbAsync())
        {
            Assert.Equal(ArtifactState.Active, (await db.Artifacts.AsNoTracking().SingleAsync(a => a.Id == artifactId, Ct)).State);
            Assert.True(await db.RestorePoints.AnyAsync(r => r.Id == point.Id, Ct));
        }
    }

    /// <summary>A base for setting up the namespace only; nothing reads it.</summary>
    private sealed class UnreadBase(long size) : IBaseImage
    {
        public long Size => size;

        public long AllocatedBytes => 0;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => throw new InvalidOperationException("Not read while setting up.");
    }

    private async Task<NfsExport> WaitForExportAsync()
    {
        var nfs = Services.GetRequiredService<NfsServerHost>();
        for (var i = 0; i < 300; i++)
        {
            if (nfs.Exports.FindByPath(IrExportPath) is { } export)
            {
                return export;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"Export {IrExportPath} did not come up; NFS status: {nfs.Status.Error}");
    }

    private static async Task<ReadOnlyMemory<byte>> LookupAsync(NfsExport export, params string[] path)
    {
        var handle = export.FileSystem.RootHandle;
        foreach (var name in path)
        {
            handle = (await export.FileSystem.LookupAsync(Esx, handle, name, Ct)).Handle;
        }

        return handle;
    }

    private static async Task<byte[]> ReadAllAsync(NfsExport export, ReadOnlyMemory<byte> handle, long size)
    {
        var data = new byte[size];
        for (long offset = 0; offset < size;)
        {
            var read = await export.FileSystem.ReadAsync(Esx, handle, offset, (int)Math.Min(1024 * 1024, size - offset), Ct);
            read.Data.CopyTo(data.AsMemory((int)offset));
            offset += read.Data.Length;
            if (read.Data.Length == 0)
            {
                break;
            }
        }

        return data;
    }
}
