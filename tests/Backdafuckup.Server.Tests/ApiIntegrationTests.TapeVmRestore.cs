using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Tests;

/// <summary>A VM restored directly from tape, through a cache that holds only its chunks.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_vm_is_restored_directly_from_tape_through_a_cache_that_is_deleted_afterwards()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.AddCartridge("BDF000L9", 64L << 20);
        var image = RandomNumberGenerator.GetBytes(6 << 20);
        var point = await BackUpImageAsync(client, "app02", image);
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null)]), Ct));
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Weekly", library.Id), Ct));
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("To tape", pool.Id, [point.JobId!.Value]), Ct));
        var written = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct))).SessionId);
        Assert.True(written.Session.State == "Succeeded", written.Session.Summary);
        var copy = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);
        var marked = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={point.JobId}", Ct))!);
        Assert.Equal(["Weekly"], marked.OnTape!);

        // The restore wizard sees the copy as a restore point: its disks, its pool, what the cache takes.
        var shown = (await client.GetFromJsonAsync<RestorePointDto>($"/api/tape/copies/{copy.Id}/restore-point", Ct))!;
        Assert.Equal((point.VmName, point.ConnectionId, string.Join(",", point.Disks.Select(d => d.Key))), (shown.VmName, shown.ConnectionId, string.Join(",", shown.Disks.Select(d => d.Key))));
        Assert.Equal(copy.StoredBytes, shown.NewStoredBytes);

        // Restored as a new VM: the disk byte for byte, the drive freed before the VM goes to the pool, the cache gone.
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/copies/{copy.Id}/restore-vm", new RestoreRequestDto
        {
            RestorePointId = copy.Id,
            Mode = RestoreModeDto.AlternateLocation,
            VmName = "app02-from-tape",
        }, Ct));
        var done = await WaitForSessionAsync(client, started.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => l.Message).ToList();
        Assert.True(done.Session.State == "Succeeded", done.Session.Summary + "\n" + string.Join("\n", log));
        Assert.Equal("Restored as 'app02-from-tape'.", done.Session.Summary);
        Assert.Contains(log, m => m.Contains("into the cache; Drive 1 is free again.", StringComparison.Ordinal));
        var vm = _pool.Records("VM").Single(v => v.Str("name_label") == "app02-from-tape");
        var disk = _pool.Records("VBD").Where(b => _pool.Uuid("VM", b.Str("VM")) == vm.Str("uuid")).Select(b => b.Str("VDI"))
            .Select(r => _pool.Records("VDI").Single(v => v.Str("uuid") == _pool.Uuid("VDI", r)))
            .Single(v => v.Long("virtual_size") == image.LongLength);
        Assert.Equal(image, _pool.DiskContent(disk.Str("uuid")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "data", "tape", "cache")));
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points", Ct))!, r => r.VmName == "app02" && r.Id != point.Id);

        // A target pool that does not exist is refused.
        var unknown = await client.PostAsJsonAsync($"/api/tape/copies/{copy.Id}/restore-vm", new RestoreRequestDto { RestorePointId = copy.Id, TargetConnectionId = Guid.NewGuid() }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
