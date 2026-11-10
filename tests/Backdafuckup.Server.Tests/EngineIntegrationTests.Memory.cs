using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Xen.Xapi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

public sealed partial class EngineIntegrationTests
{
    [Fact]
    public async Task A_vm_backed_up_with_memory_keeps_its_ram_image_and_leaves_nothing_in_the_pool()
    {
        var (vmUuid, _) = _pool.AddVm("memory-vm", DiskSize, DiskSize / 2);
        var (jobId, repositoryId) = await SeedAsync(vmUuid, retention: 3);
        await WithMemoryAsync(jobId);

        var queue = Services.GetRequiredService<SessionQueue>();
        var session = await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.Equal(1, _pool.Checkpoints);

        // The restore point holds the disks and the RAM image; the disks match, so there is no warning.
        var point = await LatestRestorePointAsync();
        Assert.Equal(_pool.MemoryImageSize, point.MemoryImageBytes);
        Assert.Equal(("crash", (string?)null), (point.Consistency, point.ConsistencyReason));
        Assert.Equal(2, point.Disks.Count);
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(repositoryId, Ct);
        await using (var reader = await repository.OpenRestorePointAsync(point.Id, Ct))
        {
            Assert.NotNull(reader.Manifest.Memory);
            Assert.Equal(SnapshotDisk.MemoryKey, reader.Manifest.Memory.Key);
            Assert.NotNull(reader.Manifest.MemoryMetadata);
            Assert.Equal(_pool.MemoryImageSize, (await reader.ReadDiskIndexAsync(SnapshotDisk.MemoryKey, Ct)).DiskSize);
        }

        // Garbage collection keeps the RAM image's chunks.
        await repository.CollectGarbageAsync(null, Ct);
        Assert.True((await repository.VerifyAsync(point.Id, VerifyDepth.Full, null, Ct)).Ok);

        // Nothing of the checkpoint is left in the pool: no snapshot VM, no memory image, no disk snapshot (only CBT metadata).
        Assert.DoesNotContain(_pool.Records("VM"), v => v.Bool("is_a_snapshot"));
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Str("type") == "suspend");
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Bool("is_a_snapshot") && v.Str("type") != "cbt_metadata");
        await using var db = await DbAsync();
        Assert.Contains(await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct),
            m => m.Contains("Snapshot with memory", StringComparison.Ordinal) && m.Contains("the VM was paused for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_vm_with_memory_that_is_off_gets_a_normal_snapshot()
    {
        var (vmUuid, _) = _pool.AddVm("halted-vm", DiskSize);
        _pool.SetPowerState(vmUuid, "Halted");
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await WithMemoryAsync(jobId);

        var session = await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct));
        Assert.Equal(SessionState.Succeeded, session.State);
        Assert.Equal(0, _pool.Checkpoints);
        Assert.Null((await LatestRestorePointAsync()).MemoryImageBytes);
        await using var db = await DbAsync();
        Assert.Contains(await db.SessionLog.Where(l => l.SessionId == session.Id).Select(l => l.Message).ToListAsync(Ct),
            m => m.Contains("is Halted: it gets a snapshot without memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disk_marked_nobak_is_captured_by_the_checkpoint_but_not_backed_up_and_its_snapshot_goes()
    {
        var (vmUuid, disks) = _pool.AddVm("nobak-memory-vm", DiskSize, DiskSize / 2);
        _pool.RenameDisk(disks[1], "scratch [NOBAK]");
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await WithMemoryAsync(jobId);

        Assert.Equal(SessionState.Succeeded, (await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var point = await LatestRestorePointAsync();
        Assert.Equal(disks[0], Assert.Single(point.Disks).SourceDiskId);
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Bool("is_a_snapshot") && v.Str("type") != "cbt_metadata");
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Str("type") == "suspend");
    }

    private async Task WithMemoryAsync(Guid jobId)
    {
        await using var db = await DbAsync();
        await db.JobObjects.Where(o => o.JobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(o => o.WithMemory, true), Ct);
    }
}
