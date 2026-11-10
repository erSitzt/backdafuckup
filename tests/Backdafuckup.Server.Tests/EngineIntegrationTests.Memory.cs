using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
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

    [Fact]
    public async Task A_vm_with_memory_resumes_at_its_original_location_where_it_was()
    {
        var (vmUuid, _) = _pool.AddVm("resume-vm", DiskSize, DiskSize / 2);
        var point = await BackUpWithMemoryAsync(vmUuid);
        var memory = await MemoryImageAsync(point);
        await using (var reader = await (await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct)).OpenRestorePointAsync(point.Id, Ct))
        {
            // The checkpoint's metadata names its own disk snapshots: the restore point keeps them per disk.
            Assert.Equal(point.Disks.Select(d => d.DiskKey).Order(), reader.Manifest.MemoryDiskIds!.Keys.Order());
        }

        var restore = await RestoreAsync(point, new RestoreRequestDto
        {
            RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, ReplaceExisting = true, PowerOn = true, ResumeWithMemory = true,
        });

        Assert.Equal(SessionState.Succeeded, restore.State);
        var resumed = _pool.Records("VM").Single(v => v.Str("name_label") == "resume-vm");
        Assert.Equal(("Running", false), (resumed.Str("power_state"), resumed.Bool("is_a_template")));
        Assert.Equal(memory, Assert.Single(_pool.ResumedImages));
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Str("type") == "suspend");
        var log = await LogAsync(restore.Id);
        Assert.Contains(log, m => m.Contains("suspended, with its memory as at the backup", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("Resuming the restored VM: it continues where it was at the snapshot with memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resumed_copy_comes_without_network_connections_and_waits_suspended()
    {
        var (vmUuid, _) = _pool.AddVm("copy-vm", DiskSize);
        var point = await BackUpWithMemoryAsync(vmUuid);
        var memory = await MemoryImageAsync(point);
        var targetSr = _pool.Uuid("SR", _pool.SecondSrRef);

        var restore = await RestoreAsync(point, new RestoreRequestDto
        {
            RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "copy-vm-resumed",
            DiskStorage = point.Disks.ToDictionary(d => d.DiskKey, _ => targetSr), ResumeWithMemory = true,
        });

        Assert.Equal(SessionState.Succeeded, restore.State);
        var copy = _pool.Records("VM").Single(v => v.Str("name_label") == "copy-vm-resumed");
        Assert.Equal(("Suspended", false), (copy.Str("power_state"), copy.Bool("is_a_template")));
        Assert.DoesNotContain(_pool.Records("VIF"), v => v.Str("VM") == RefOf("VM", copy.Str("uuid")));
        Assert.Contains(_pool.Records("VIF"), v => v.Str("VM") == RefOf("VM", vmUuid));

        // The RAM image is the copy's suspend image, next to its disks.
        var suspend = _pool.Records("VDI").Single(v => v.Str("type") == "suspend");
        Assert.Equal(_pool.SecondSrRef, suspend.Str("SR"));
        Assert.Equal(memory, _pool.DiskContent(suspend.Str("uuid")));
        Assert.Empty(_pool.ResumedImages);
        var log = await LogAsync(restore.Id);
        Assert.Contains(log, m => m.Contains("removed: the copy resumes without network connections", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("The restored VM stays suspended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resume_is_refused_without_memory_and_in_another_pool()
    {
        var (vmUuid, _) = _pool.AddVm("plain-vm", DiskSize);
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        var queue = Services.GetRequiredService<SessionQueue>();
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var plain = await LatestRestorePointAsync();
        await WithMemoryAsync(jobId);
        Assert.Equal(SessionState.Succeeded, (await RunAsync(queue.EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var withMemory = await LatestRestorePointAsync();
        var vms = _pool.Records("VM").Count;
        var vdis = _pool.Records("VDI").Count;

        var noMemory = await RestoreAsync(plain, new RestoreRequestDto { RestorePointId = plain.Id, VmName = "plain-copy", ResumeWithMemory = true });
        Assert.Equal(SessionState.Failed, noMemory.State);
        Assert.Contains(await LogAsync(noMemory.Id), m => m.Contains("has no memory image: it can only be booted from its disks", StringComparison.Ordinal));

        Guid otherPool;
        await using (var db = await DbAsync())
        {
            var source = await db.Connections.Include(c => c.Credential).FirstAsync(c => c.Id == withMemory.ConnectionId, Ct);
            var other = new HypervisorConnection { DisplayName = "other-pool", Address = source.Address, CredentialId = source.CredentialId };
            db.Connections.Add(other);
            await db.SaveChangesAsync(Ct);
            otherPool = other.Id;
        }

        var elsewhere = await RestoreAsync(withMemory, new RestoreRequestDto
        {
            RestorePointId = withMemory.Id, VmName = "memory-copy", TargetConnectionId = otherPool, ResumeWithMemory = true,
        });
        Assert.Equal(SessionState.Failed, elsewhere.State);
        Assert.Contains(await LogAsync(elsewhere.Id), m => m.Contains("resumes only in the pool it was backed up in", StringComparison.Ordinal) && m.Contains("'other-pool'", StringComparison.Ordinal));
        Assert.Equal((vms, vdis), (_pool.Records("VM").Count, _pool.Records("VDI").Count));
    }

    [Fact]
    public async Task A_restore_point_with_memory_still_boots_cold_from_its_disks()
    {
        var (vmUuid, _) = _pool.AddVm("cold-vm", DiskSize);
        var point = await BackUpWithMemoryAsync(vmUuid);

        var restore = await RestoreAsync(point, new RestoreRequestDto { RestorePointId = point.Id, VmName = "cold-vm-restored", PowerOn = true });

        Assert.Equal(SessionState.Succeeded, restore.State);
        var restored = _pool.Records("VM").Single(v => v.Str("name_label") == "cold-vm-restored");
        Assert.Equal("Running", restored.Str("power_state"));
        Assert.Contains(_pool.Records("VIF"), v => v.Str("VM") == RefOf("VM", restored.Str("uuid")));
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Str("type") == "suspend");
        Assert.Empty(_pool.ResumedImages);
    }

    [Fact]
    public async Task A_resume_that_xapi_cannot_set_up_is_rolled_back_completely()
    {
        var (vmUuid, _) = _pool.AddVm("fragile-vm", DiskSize);
        var point = await BackUpWithMemoryAsync(vmUuid);
        var vms = _pool.Records("VM").Count;
        var vdis = _pool.Records("VDI").Count;

        // The import gives a halted VM: it cannot resume.
        _pool.ImportIgnoresSuspendImage = true;
        var halted = await RestoreAsync(point, new RestoreRequestDto { RestorePointId = point.Id, VmName = "fragile-copy", ResumeWithMemory = true });
        Assert.Equal(SessionState.Failed, halted.State);
        Assert.Contains(await LogAsync(halted.Id), m => m.Contains("Halted with suspend image none", StringComparison.Ordinal) && m.Contains("so it cannot resume", StringComparison.Ordinal));
        Assert.Equal((vms, vdis), (_pool.Records("VM").Count, _pool.Records("VDI").Count));

        // The pool refuses to take the NICs of the suspended copy: a copy never resumes with them, and the suspended VM goes too.
        _pool.ImportIgnoresSuspendImage = false;
        _pool.RefuseNicChangesWhenSuspended = true;
        var withNics = await RestoreAsync(point, new RestoreRequestDto { RestorePointId = point.Id, VmName = "fragile-copy", ResumeWithMemory = true });
        Assert.Equal(SessionState.Failed, withNics.State);
        Assert.Contains(await LogAsync(withNics.Id), m => m.Contains("could not be removed (VM_BAD_POWER_STATE); a copy resumes only without network connections", StringComparison.Ordinal));
        Assert.Equal((vms, vdis), (_pool.Records("VM").Count, _pool.Records("VDI").Count));
        Assert.DoesNotContain(_pool.Records("VDI"), v => v.Str("type") == "suspend");
    }

    private async Task<RestorePointEntity> BackUpWithMemoryAsync(string vmUuid)
    {
        var (jobId, _) = await SeedAsync(vmUuid, retention: 3);
        await WithMemoryAsync(jobId);
        Assert.Equal(SessionState.Succeeded, (await RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueBackupAsync(jobId, SessionRunMode.Incremental, "test", Ct))).State);
        var point = await LatestRestorePointAsync();
        Assert.NotNull(point.MemoryImageBytes);
        return point;
    }

    /// <summary>The RAM image as the restore point holds it.</summary>
    private async Task<byte[]> MemoryImageAsync(RestorePointEntity point)
    {
        var repository = await Services.GetRequiredService<RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using var reader = await repository.OpenRestorePointAsync(point.Id, Ct);
        var image = await RestorePointDiskImage.OpenAsync(reader, SnapshotDisk.MemoryKey, Ct);
        var content = new byte[_pool.MemoryImageSize];
        await image.ReadAsync(0, content, Ct);
        return content;
    }

    private Task<SessionEntity> RestoreAsync(RestorePointEntity point, RestoreRequestDto request) =>
        RunAsync(Services.GetRequiredService<SessionQueue>().EnqueueRestoreAsync(request, point.VmName, "test", Ct));

    private async Task<List<string>> LogAsync(Guid sessionId)
    {
        await using var db = await DbAsync();
        return await db.SessionLog.Where(l => l.SessionId == sessionId).Select(l => l.Message).ToListAsync(Ct);
    }

    private async Task WithMemoryAsync(Guid jobId)
    {
        await using var db = await DbAsync();
        await db.JobObjects.Where(o => o.JobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(o => o.WithMemory, true), Ct);
    }
}
