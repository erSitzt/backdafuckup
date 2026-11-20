using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Backdafuckup.Testing.Lvhd;
using Backdafuckup.Xen.Xapi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// A job's VMs grouped by the array volumes they lie on: the VMs of a volume share one array snapshot however few VMs the
/// job reads at once; a limit splits them; a VM that drops out does not keep its group waiting.
/// </summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task The_vms_of_a_volume_share_one_array_snapshot_even_when_one_vm_is_read_at_a_time()
    {
        var client = await SignedInClientAsync();
        var array = Alletra;
        array.AddVolume("xcp-sr1", SrWwn, 256);
        array.AddHost("proxy01", ProxyIqn);
        array.AddIscsiPort("0:2:1", "10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001");
        var (srRef, srUuid) = _pool.AddLunSr("LUN SR", "3" + SrWwn.ToLowerInvariant());
        var vms = new List<(string Uuid, string Name)>();
        foreach (var name in new[] { "app01", "app02", "app03" })
        {
            var (uuid, disks) = _pool.AddVm(name, 8L * 1024 * 1024);
            _pool.MoveVdi(disks[0], srRef);
            vms.Add((uuid, name));
        }

        var (webUuid, _) = _pool.AddVm("web01", 8L * 1024 * 1024); // on the pool's local storage
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
        }, Ct));
        var volume = (await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{system.Id}/volumes", Ct))!.Single(v => v.VolumeName == "xcp-sr1");
        await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{volume.Id}", new SetStorageVolumeDto(true), Ct);
        await Read<StorageSystemDto>(client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/options",
            system.Options with { ExperimentalReaders = true, BatchWindowSeconds = 60, ProbationRuns = 0, MinSecondsAfterVmSnapshot = 0 }, Ct));
        SanProxies.LunBuilder = _ => LunOfSr(srRef, srUuid);

        var job = new SaveJobDto
        {
            Name = "Grouped",
            ConnectionId = connection.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [.. vms.Select(v => new JobObjectDto(v.Uuid, v.Name)), new JobObjectDto(webUuid, "web01")],
            MaxParallelVms = 1,
            RetryCount = 0,
            FromStorageSnapshots = true,
        };
        var created = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", job, Ct));

        async Task<(SessionDetailDto Session, List<LogEntryDto> Log)> RunAsync()
        {
            var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{created.Id}/run", new RunJobDto(), Ct));
            var session = await WaitForSessionAsync(client, started.SessionId);
            var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
            Assert.True(session.Session.State == "Succeeded", string.Join("\n", log.Select(l => l.Message)));
            return (session, log);
        }

        int Snapshots() => array.Requests.Count(r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal) && r.Contains("CREATE_SNAPSHOT_VOLUME", StringComparison.Ordinal));
        string Transport(SessionDetailDto session, string vm) => Assert.Single(session.Tasks.Single(t => t.ObjectName == vm).Metrics!.Disks!).Transport!;

        // One group of three, one array snapshot for all of them, though the job reads one VM at a time; web01 as before.
        var (first, log) = await RunAsync();
        var messages = string.Join("\n", log.Select(l => $"{l.TimestampUtc:HH:mm:ss.fff} {l.Message}"));
        Assert.True(Snapshots() == 1, messages);
        Assert.Contains("Group 1: Alletra xcp-sr1: 3 VM(s) (app01, app02, app03).", messages, StringComparison.Ordinal);
        Assert.Contains("web01: read from its VM snapshot as usual (storage ", messages, StringComparison.Ordinal);
        Assert.Contains("taken in", messages, StringComparison.Ordinal);
        Assert.Contains(" for 3 VM(s) (app01, app02, app03; the oldest VM snapshot had waited ", messages, StringComparison.Ordinal);
        Assert.All(vms, v => Assert.Equal("array-lvhd", Transport(first, v.Name)));
        Assert.NotEqual("array-lvhd", Transport(first, "web01"));
        Assert.DoesNotContain(_pool.Records("VM"), v => v["is_a_snapshot"]!.GetValue<bool>());

        // A limit of two: the volume's VMs in two groups, two array snapshots.
        await client.PutAsJsonAsync($"/api/jobs/{created.Id}", job with { MaxVmsPerArraySnapshot = 2 }, Ct);
        var (second, log2) = await RunAsync();
        messages = string.Join("\n", log2.Select(l => l.Message));
        Assert.Equal(3, Snapshots());
        Assert.Contains("Group 1: Alletra xcp-sr1 (part 1 of 2): 2 VM(s) (app01, app02).", messages, StringComparison.Ordinal);
        Assert.Contains("Group 2: Alletra xcp-sr1 (part 2 of 2): 1 VM(s) (app03).", messages, StringComparison.Ordinal);
        Assert.All(vms, v => Assert.Equal("array-lvhd", Transport(second, v.Name)));

        // The volume turned off: no group, and the log says why.
        await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{volume.Id}", new SetStorageVolumeDto(false), Ct);
        var (_, log3) = await RunAsync();
        Assert.Contains(log3, l => l.Message == "app01: read from its VM snapshot as usual (the volume of storage LUN SR (xcp-sr1) is not turned on).");
        Assert.Equal(3, Snapshots());
        await WaitUntilAsync(() => array.Vluns.Count == 0 && !array.Volumes.Values.Any(v => v["copyOfID"] is not null));
    }

    [Fact]
    public async Task A_group_does_not_wait_for_a_vm_that_dropped_out()
    {
        var (service, _, pool, srUuid, connectionId, systemId) = await ArraySetupAsync();
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var entity = await db.StorageSystems.SingleAsync(s => s.Id == systemId, Ct);
            entity.Options = new StorageSystemOptions { ExperimentalReaders = true, BatchWindowSeconds = 600, ProbationRuns = 0, MinSecondsAfterVmSnapshot = 0 };
            await db.SaveChangesAsync(Ct);
        }

        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        pool.Luns[SrWwn.ToLowerInvariant()] = image;
        var (run, group) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var context = new VmSnapshotContext(run, Guid.CreateVersion7(), connectionId, null!, new ArrayFakeSession(srUuid, leaf, baseVdi, image),
            new Dictionary<string, ChangeToken?> { ["0"] = new ChangeToken("xen-cbt", "previous") }, GroupId: group, GroupSize: 3);

        // A group of three: one VM failed before its snapshot (withdrawn before the batch opened), one comes, one drops out later.
        service.Withdraw(run, 1, group);
        var first = service.OnVmSnapshotCreatedAsync(context, new RecordingLog(), Ct);
        await Task.Delay(1000, Ct);
        Assert.False(first.IsCompleted);
        service.Withdraw(run, 1, group);

        await using var lease = await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task Other_vms_of_the_run_that_come_first_do_not_close_a_group_before_its_last_vm_joined()
    {
        var (service, array, pool, srUuid, connectionId, systemId) = await ArraySetupAsync();
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var entity = await db.StorageSystems.SingleAsync(s => s.Id == systemId, Ct);
            entity.Options = new StorageSystemOptions { ExperimentalReaders = true, BatchWindowSeconds = 600, ProbationRuns = 0, MinSecondsAfterVmSnapshot = 0 };
            await db.SaveChangesAsync(Ct);
        }

        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        pool.Luns[SrWwn.ToLowerInvariant()] = image;
        var (run, group) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        VmSnapshotContext InRun(ArrayFakeSession backup, Guid? groupId) =>
            new(run, Guid.CreateVersion7(), connectionId, null!, backup, new Dictionary<string, ChangeToken?> { ["0"] = new ChangeToken("xen-cbt", "previous") },
                MaxPeers: 1, RunVms: 4, GroupId: groupId, GroupSize: groupId is null ? 0 : 2);

        // A run of four: a group of two on the array, two VMs elsewhere. The group's first VM, then both others come.
        var first = service.OnVmSnapshotCreatedAsync(InRun(new ArrayFakeSession(srUuid, leaf, baseVdi, image), group), new RecordingLog(), Ct);
        await Task.Delay(500, Ct);
        Assert.Null(await service.OnVmSnapshotCreatedAsync(InRun(new ArrayFakeSession(Guid.NewGuid().ToString(), leaf, baseVdi, image), null), new RecordingLog(), Ct));
        Assert.Null(await service.OnVmSnapshotCreatedAsync(InRun(new ArrayFakeSession(Guid.NewGuid().ToString(), leaf, baseVdi, image), null), new RecordingLog(), Ct));
        await Task.Delay(500, Ct);
        Assert.False(first.IsCompleted); // still waiting for the group's second VM

        // The second VM of the group comes: one array snapshot for both.
        await using var second = await service.OnVmSnapshotCreatedAsync(InRun(new ArrayFakeSession(srUuid, leaf, baseVdi, image), group), new RecordingLog(), Ct);
        await using var one = await first.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.NotNull(one);
        Assert.NotNull(second);
        Assert.Single(array.Requests, r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal) && r.Contains("CREATE_SNAPSHOT_VOLUME", StringComparison.Ordinal));
    }

    /// <summary>
    /// The LUN of an SR as an array snapshot holds it: the volume group with the chain of every VM snapshot on the SR (the
    /// snapshot VDI, a deflated empty leaf, on its base copies; the oldest a dynamic VHD with the data, each newer one a
    /// differencing VHD with what changed since).
    /// </summary>
    private byte[] LunOfSr(string srRef, string srUuid)
    {
        var vdis = _pool.Records("VDI");
        var volumes = new List<PvBuilder.Volume> { new("MGT", Extents: 1) };
        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in vdis.Where(v => v.Str("SR") == srRef && v["is_a_snapshot"]!.GetValue<bool>() && v.Str("type") == "user"))
        {
            var chain = new List<JsonObject>();
            for (var parent = ((JsonObject)snapshot["sm_config"]!)["vhd-parent"]?.GetValue<string>(); parent is not null;)
            {
                var member = vdis.Single(v => v.Str("uuid") == parent);
                chain.Add(member);
                parent = (member["sm_config"] as JsonObject)?["vhd-parent"]?.GetValue<string>();
            }

            var size = _pool.VdiData(chain[0].Str("uuid")).LongLength;
            var ids = chain.Select(c => DeterministicId(c.Str("uuid"))).ToList();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                if (!added.Add(chain[i].Str("uuid")))
                {
                    continue;
                }

                var data = _pool.VdiData(chain[i].Str("uuid"));
                var below = i == chain.Count - 1 ? new byte[size] : _pool.VdiData(chain[i + 1].Str("uuid"));
                var sectors = new Dictionary<long, byte[]>();
                for (long sector = 0; sector < size / 512; sector++)
                {
                    var bytes = data.AsSpan((int)(sector * 512), 512);
                    if (!bytes.SequenceEqual(below.AsSpan((int)(sector * 512), 512)))
                    {
                        sectors[sector] = bytes.ToArray();
                    }
                }

                var vhd = VhdBuilder.Build(size, LvhdBlock, ids[i], i == chain.Count - 1 ? null : ids[i + 1], sectors);
                volumes.Add(new PvBuilder.Volume($"VHD-{chain[i].Str("uuid")}", vhd, Extents: (size + Testing.Xapi.FakeXenPool.LvhdOverhead) / PvBuilder.ExtentBytes));
            }

            var leafVhd = VhdBuilder.Build(size, LvhdBlock, Guid.NewGuid(), ids[0], new Dictionary<long, byte[]>());
            volumes.Add(new PvBuilder.Volume($"VHD-{snapshot.Str("uuid")}", leafVhd, Extents: Testing.Xapi.FakeXenPool.LvhdOverhead / PvBuilder.ExtentBytes));
        }

        return PvBuilder.Build($"VG_XenStorage-{srUuid}", volumes);

        static Guid DeterministicId(string uuid) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uuid)));
    }
}
