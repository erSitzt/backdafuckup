using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Arrays;
using Backdafuckup.Server.Data;
using Backdafuckup.Testing.Lvhd;
using Backdafuckup.Xen.Xapi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Backup from storage snapshots (spec 3, task 4): the array path of one VM, its batch, its fallbacks and cleanup.</summary>
public sealed partial class ApiIntegrationTests
{
    private const long LvhdCapacity = 4L * 2 * 1024 * 1024;
    private const int LvhdBlock = 2 * 1024 * 1024;
    private const string ProxyIqn = "iqn.2004-10.com.ubuntu:01:proxy01";

    private FakeSanProxies? _sanProxies;

    /// <summary>The proxy pool of the server's array path in these tests (one proxy with SAN access).</summary>
    private FakeSanProxies SanProxies => _sanProxies ??= new FakeSanProxies(Alletra);

    [Fact]
    public async Task A_vm_on_an_lvhd_volume_is_read_from_an_array_snapshot_and_its_vm_snapshot_released_before()
    {
        var (service, array, pool, srUuid, connectionId, systemId) = await ArraySetupAsync();
        var (leaf, baseVdi, image, expected) = LvhdImage(srUuid);
        pool.Luns[SrWwn.ToLowerInvariant()] = image;
        var backup = new ArrayFakeSession(srUuid, leaf, baseVdi, image);

        var log = new RecordingLog();
        await using (var lease = await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), log, Ct))
        {
            Assert.NotNull(lease);
            Assert.True(backup.Released);
            Assert.Equal("array-lvhd", lease.Transport);
            var disk = backup.Disks[0];
            Assert.Equal(backup.Changes, lease.GetChangedExtents(disk));
            await using var reader = (IDiskReader)await lease.OpenReaderAsync(disk, new DiskReaderOptions(1, 1, DiskTransportMode.Automatic), Ct);
            var all = new byte[LvhdCapacity];
            await reader.ReadAsync(0, all, Ct);
            Assert.Equal(expected, all);
            Assert.Equal(("lvhd", 1), (lease.Info.Method, lease.Info.Snapshots.Count));

            // Snapshot read-only, presented to the proxy's host only, logged in at the array's portal.
            var snapshot = Assert.Single(array.Volumes.Values, v => v["copyOfID"] is not null);
            Assert.StartsWith("bdfu-", snapshot["name"]!.GetValue<string>(), StringComparison.Ordinal);
            // One presentation: a template and an entry per iSCSI port of the array, all to proxy01.
            Assert.Single(array.Vluns.Values, v => v["portPos"] is null);
            Assert.All(array.Vluns.Values, v => Assert.Equal("proxy01", v["hostName"]!.GetValue<string>()));
            Assert.Equal([("10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001")], pool.Logins);
            Assert.Contains(log.Entries, e => e.Message.Contains("are read from array snapshot bdfu-", StringComparison.Ordinal));
        }

        // The last VM gave it back: presentation and snapshot are gone, the ledger rows closed, the device released.
        Assert.Empty(array.Vluns);
        Assert.DoesNotContain(array.Volumes.Values, v => v["copyOfID"] is not null);
        Assert.True(pool.Released);
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
        var rows = await db.Artifacts.Where(a => a.StorageSystemId == systemId).ToListAsync(Ct);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(ArtifactState.Done, r.State));
    }

    [Fact]
    public async Task A_chain_that_differs_on_the_lun_keeps_the_vm_snapshot_and_removes_the_array_snapshot()
    {
        var (service, array, pool, srUuid, connectionId, _) = await ArraySetupAsync();
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        pool.Luns[SrWwn.ToLowerInvariant()] = image;

        // XAPI reports another size for the base copy than its logical volume has (inflated since?).
        var backup = new ArrayFakeSession(srUuid, leaf, baseVdi with { PhysicalUtilisation = baseVdi.PhysicalUtilisation * 2 }, image);
        var log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), log, Ct));
        Assert.False(backup.Released);
        Assert.Contains(log.Entries, e => e.Severity == LogSeverity.Warning && e.Message.Contains("but XAPI reported", StringComparison.Ordinal) &&
                                          e.Message.Contains("They are read from the VM snapshot", StringComparison.Ordinal));
        await WaitUntilAsync(() => array.Vluns.Count == 0 && !array.Volumes.Values.Any(v => v["copyOfID"] is not null));
    }

    [Fact]
    public async Task Without_a_proxy_whose_initiator_the_array_knows_the_vm_is_read_normally()
    {
        var (service, array, pool, srUuid, connectionId, _) = await ArraySetupAsync();
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        pool.Initiator = "iqn.2004-10.com.ubuntu:01:unknown";
        var backup = new ArrayFakeSession(srUuid, leaf, baseVdi, image);
        var log = new RecordingLog();

        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), log, Ct));
        Assert.False(backup.Released);
        Assert.Contains(log.Entries, e => e.Message.Contains("No host object on storage system Alletra (array01.corp.example) lists the iSCSI initiator of a proxy with SAN access: proxy01 iqn.2004-10.com.ubuntu:01:unknown", StringComparison.Ordinal));
        await WaitUntilAsync(() => !array.Volumes.Values.Any(v => v["copyOfID"] is not null));
    }

    [Fact]
    public async Task A_volume_not_turned_on_or_without_experimental_readers_is_not_used()
    {
        var (service, _, _, srUuid, connectionId, systemId) = await ArraySetupAsync(experimental: false);
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        var log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("turn on 'Experimental readers' for storage system Alletra", StringComparison.Ordinal));

        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            await db.StorageVolumes.Where(v => v.StorageSystemId == systemId).ExecuteUpdateAsync(u => u.SetProperty(v => v.Enabled, false), Ct);
        }

        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("volume xcp-sr1 (storage LUN SR) is not turned on for storage snapshots", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_proxys_host_object_is_shown_with_its_rest_call_and_cli_line_and_created_only_when_asked()
    {
        // A turned-on volume in domain bdfu-test, and a host object for another initiator.
        var (_, array, pool, _, _, systemId) = await ArraySetupAsync();
        var client = await SignedInAgainAsync();
        pool.Initiator = "iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5";
        var system = new { Id = systemId };

        var access = await client.GetFromJsonAsync<StorageProxyAccessDto>($"/api/storage-systems/{system.Id}/proxy-access", Ct);
        var proxy = Assert.Single(access!.Proxies);
        Assert.Equal(("proxy01", true, "iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5", (string?)null), (proxy.ProxyName, proxy.Connected, proxy.InitiatorName, proxy.HostName));
        Assert.Equal(["10.26.101.10:3260"], access.Portals);
        Assert.Null(access.CreateRefusal);

        // The plan reads the array and changes nothing.
        var plan = await Read<StorageHostPlanDto>(client.PostAsJsonAsync($"/api/storage-systems/{system.Id}/proxy-access/plan", new StorageHostRequestDto(proxy.ProxyId), Ct));
        Assert.Equal(("bdfu-proxy01", true, (string?)null), (plan.Name, plan.CanRun, plan.Refusal));
        var change = Assert.Single(plan.Changes);
        // The fake's volumes are in domain bdfu-test, so the host object goes there too.
        Assert.Equal("bdfu-test", plan.Domain);
        Assert.Equal("createhost -iscsi -persona 2 -domain bdfu-test -os Linux -comment \"backdafuckup proxy proxy01\" bdfu-proxy01 iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5", change.Cli);
        Assert.Contains("No host object on Alletra lists the initiator iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5 yet.", plan.Checks);
        Assert.DoesNotContain(array.Requests, r => r.StartsWith("POST /api/v3/hosts", StringComparison.Ordinal));

        var created = await Read<StorageProxyAccessDto>(client.PostAsJsonAsync($"/api/storage-systems/{system.Id}/proxy-access/hosts",
            new StorageHostRequestDto(proxy.ProxyId, plan.Name, plan.Domain), Ct));

        Assert.Equal("bdfu-proxy01", Assert.Single(created.Proxies).HostName);
        Assert.Single(array.Requests, r => r.StartsWith("POST /api/v3/hosts", StringComparison.Ordinal));
        Assert.Equal("bdfu-test", array.Hosts.Single(h => h["name"]!.GetValue<string>() == "bdfu-proxy01")["domain"]!.GetValue<string>());
        // Asked again, there is nothing to do.
        var again = await client.PostAsJsonAsync($"/api/storage-systems/{system.Id}/proxy-access/hosts", new StorageHostRequestDto(proxy.ProxyId, "bdfu-proxy01b", "-"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("Host object bdfu-proxy01 lists the initiator iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5 already: nothing to do.", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browse_account_is_shown_the_cli_line_but_cannot_create_the_host_object()
    {
        var client = await SignedInClientAsync();
        var array = Alletra;
        array.Privileges = [new JsonObject { ["domain"] = "all", ["domainId"] = 0, ["role"] = "browse" }];
        array.AddVolume("xcp-sr1", SrWwn, 64);
        var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
        }, Ct));

        var plan = await Read<StorageHostPlanDto>(client.PostAsJsonAsync($"/api/storage-systems/{system.Id}/proxy-access/plan", new StorageHostRequestDto(SanProxies.ProxyId), Ct));

        Assert.False(plan.CanRun);
        Assert.Null(plan.Refusal);
        Assert.StartsWith("createhost -iscsi -persona 2 ", Assert.Single(plan.Changes).Cli, StringComparison.Ordinal);
        Assert.Equal($"The account {array.User} may not change the array (browse in all domains). Run the CLI line as an array administrator, or give the account the role edit.", plan.RunRefusal);
        var create = await client.PostAsJsonAsync($"/api/storage-systems/{system.Id}/proxy-access/hosts", new StorageHostRequestDto(SanProxies.ProxyId, plan.Name, "-"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        Assert.DoesNotContain(array.Requests, r => r.StartsWith("POST /api/v3/hosts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_account_that_may_only_look_is_not_used_for_snapshots()
    {
        Alletra.Privileges = [new JsonObject { ["domain"] = "all", ["domainId"] = 0, ["role"] = "browse" }];
        var (service, array, _, srUuid, connectionId, _) = await ArraySetupAsync();
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        var log = new RecordingLog();

        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("the account of storage system Alletra may not take snapshots (browse in all domains)", StringComparison.Ordinal));
        Assert.DoesNotContain(array.Requests, r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_replicated_volume_is_snapshotted_only_where_it_is_primary()
    {
        var (service, array, _, srUuid, connectionId, systemId) = await ArraySetupAsync();
        var client = await SignedInAgainAsync();
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        var volume = array.Volumes.Values.Single(v => v["name"]!.GetValue<string>() == "xcp-sr1");
        bool Snapshotted(Testing.Arrays.FakeAlletra a) => a.Requests.Any(r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal));

        // Only the secondary side is known here: refused, nothing taken.
        volume["rcopyStatus"] = "secondary";
        var mapped = await Read<List<StorageVolumeDto>>(client.PostAsync($"/api/storage-systems/{systemId}/volumes/refresh", null, Ct));
        Assert.Equal("secondary", Assert.Single(mapped).ReplicationRole);
        var log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("volume xcp-sr1 (storage LUN SR) is the secondary copy of a replicated volume", StringComparison.Ordinal));
        Assert.False(Snapshotted(array));

        // Mapped as primary, but the group switched sides since (a failover): nothing taken, the reason logged.
        volume["rcopyStatus"] = "primary";
        await client.PostAsync($"/api/storage-systems/{systemId}/volumes/refresh", null, Ct);
        volume["rcopyStatus"] = "secondary";
        log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("Volume xcp-sr1 (WWN", StringComparison.Ordinal)
                                          && e.Message.Contains("is now the secondary copy on storage system Alletra (array01.corp.example)", StringComparison.Ordinal));
        Assert.False(Snapshotted(array));

        // Both arrays of the pair added, the same WWN on each: only the one where it is primary is snapshotted.
        var second = Alletra2;
        second.AddVolume("xcp-sr1", SrWwn, 64)["rcopyStatus"] = "primary";
        second.AddHost("proxy01", ProxyIqn);
        second.AddIscsiPort("0:2:1", "10.26.102.10", "iqn.2000-05.com.3pardata:20210002ac000002");
        var other = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra 2", Address = "array02.corp.example", UserName = second.User, Password = second.Password,
        }, Ct));
        var otherVolume = Assert.Single((await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{other.Id}/volumes", Ct))!);
        Assert.Equal(("xcp-sr1", "LUN SR", "primary"), (otherVolume.VolumeName, otherVolume.StorageName, otherVolume.ReplicationRole));
        await client.PutAsJsonAsync($"/api/storage-systems/{other.Id}/volumes/{otherVolume.Id}", new SetStorageVolumeDto(true), Ct);
        await client.PutAsJsonAsync($"/api/storage-systems/{other.Id}/options", other.Options with { ExperimentalReaders = true, BatchWindowSeconds = 0 }, Ct);
        await client.PostAsync($"/api/storage-systems/{systemId}/volumes/refresh", null, Ct);

        await service.OnVmSnapshotCreatedAsync(Context(connectionId, new ArrayFakeSession(srUuid, leaf, baseVdi, image)), new RecordingLog(), Ct);
        Assert.True(Snapshotted(second));
        Assert.False(Snapshotted(array));
        await WaitUntilAsync(() => !second.Volumes.Values.Any(v => v["copyOfID"] is not null) && second.Vluns.Count == 0);
    }

    /// <summary>Keeps every line written to it.</summary>
    private sealed class RecordingLog : IOperationLog
    {
        private readonly Lock _lock = new();
        private readonly List<(LogSeverity Severity, string Message)> _entries = [];

        public IReadOnlyList<(LogSeverity Severity, string Message)> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public void Write(LogSeverity severity, string message, string? key = null)
        {
            lock (_lock)
            {
                _entries.Add((severity, message));
            }
        }
    }

    [Fact]
    public async Task A_job_reads_a_vm_on_a_lun_sr_from_array_snapshots_in_full_and_then_incrementally()
    {
        var client = await SignedInClientAsync();
        var array = Alletra;
        array.AddVolume("xcp-sr1", SrWwn, 256);
        array.AddHost("proxy01", ProxyIqn);
        array.AddIscsiPort("0:2:1", "10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001");
        var (srRef, srUuid) = _pool.AddLunSr("LUN SR", "3" + SrWwn.ToLowerInvariant());
        var (vmUuid, disks) = _pool.AddVm("app01", 16L * 1024 * 1024);
        _pool.MoveVdi(disks[0], srRef);
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
        }, Ct));
        var volume = (await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{system.Id}/volumes", Ct))!.Single(v => v.VolumeName == "xcp-sr1");
        await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{volume.Id}", new SetStorageVolumeDto(true), Ct);
        var refused = await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/options", system.Options with { BatchWindowSeconds = 3600 }, Ct);
        Assert.Contains("The batch window must be between 0 and 600 (is 3600).", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        system = await Read<StorageSystemDto>(client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/options", system.Options with { ExperimentalReaders = true, BatchWindowSeconds = 0 }, Ct));
        Assert.True(system.Options.ExperimentalReaders);

        // The SR's LUN as the array snapshot holds it: the snapshot VDI (a deflated, empty leaf) on its chain of base copies, the
        // oldest a dynamic VHD with the disk's data, each newer one a differencing VHD with what changed since.
        byte[]? captured = null;
        SanProxies.LunBuilder = wwn =>
        {
            var vdis = _pool.Records("VDI");
            var snapshot = vdis.Last(v => v.Str("SR") == srRef && v["is_a_snapshot"]!.GetValue<bool>() && v.Str("type") == "user");
            var chain = new List<JsonObject>();
            for (var parent = ((JsonObject)snapshot["sm_config"]!)["vhd-parent"]?.GetValue<string>(); parent is not null;)
            {
                var member = vdis.Single(v => v.Str("uuid") == parent);
                chain.Add(member);
                parent = (member["sm_config"] as JsonObject)?["vhd-parent"]?.GetValue<string>();
            }

            captured = _pool.VdiData(chain[0].Str("uuid"));
            var size = captured.LongLength;
            var volumes = new List<PvBuilder.Volume> { new("MGT", Extents: 1) };
            var ids = chain.Select(_ => Guid.NewGuid()).ToList();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
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
            return PvBuilder.Build($"VG_XenStorage-{srUuid}", volumes);
        };

        var created = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "From the array",
            ConnectionId = connection.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vmUuid, "app01")],
            RetryCount = 0,
            FromStorageSnapshots = true,
        }, Ct));
        Assert.True((await client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", Ct))!.FromStorageSnapshots);

        async Task<(string Transport, List<LogEntryDto> Log)> RunAsync()
        {
            var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{created.Id}/run", new RunJobDto(), Ct));
            var session = await WaitForSessionAsync(client, started.SessionId);
            var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
            Assert.True(session.Session.State == "Succeeded", string.Join("\n", log.Select(l => l.Message)));
            return (Assert.Single(Assert.IsType<TransferMetricsDto>(session.Tasks[0].Metrics).Disks!).Transport!, log);
        }

        async Task AssertStoredAsync()
        {
            var point = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={created.Id}", Ct))!.OrderBy(p => p.CreatedUtc).Last();
            var repo = await _factory!.Services.GetRequiredService<Backdafuckup.Server.Engine.RepositoryManager>().GetAsync(point.RepositoryId, Ct);
            await using var reader = await repo.OpenRestorePointAsync(point.Id, Ct);
            Assert.Equal("lvhd", reader.Manifest.ArraySnapshot!.Method);
            var disk = Assert.Single(reader.Manifest.Disks);
            var index = await reader.ReadDiskIndexAsync(disk.Key, Ct);
            for (var c = 0; c < index.Entries.Length; c++)
            {
                var chunk = captured.AsSpan(c * index.ChunkSize, (int)Math.Min(index.ChunkSize, captured!.Length - ((long)c * index.ChunkSize)));
                Assert.Equal(chunk.IndexOfAnyExcept((byte)0) < 0 ? Core.Repository.ChunkId.Zero : Core.Repository.ChunkId.Compute(chunk), index.Entries[c]);
            }
        }

        // Full: read from the array snapshot; the VM snapshot went before the disk was read and only its CBT metadata is left.
        var (transport, log) = await RunAsync();
        Assert.True(transport == "array-lvhd", string.Join("\n", log.Select(l => l.Message)));
        Assert.Contains(log, l => l.Message.Contains("are read from array snapshot bdfu-", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Message.Contains("released before its disks are read", StringComparison.Ordinal));
        // Verify mode (5 runs by default) compared a sample with NBD reads of the XAPI snapshot VDI before the release.
        Assert.Contains(log, l => l.Message.Contains("Verify mode (LVHD (XCP-ng LVM storage) reader on Alletra, backup 1 of 5)", StringComparison.Ordinal));
        await AssertStoredAsync();
        Assert.DoesNotContain(_pool.Records("VM"), v => v["is_a_snapshot"]!.GetValue<bool>());
        Assert.Contains(_pool.Records("VDI"), v => v.Str("type") == "cbt_metadata");

        // Incremental: only the changed block, and the unchanged regions checked against the previous restore point.
        _pool.WriteDisk(disks[0], 3L * 1024 * 1024, Enumerable.Repeat((byte)0x5a, 64 * 1024).ToArray());
        (transport, log) = await RunAsync();
        Assert.True(transport == "array-lvhd", string.Join("\n", log.Select(l => l.Message)));
        Assert.Contains(log, l => l.Message.Contains("unchanged region(s) read from the array snapshot match the previous restore point", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Message.Contains("backup 2 of 5)", StringComparison.Ordinal));
        await AssertStoredAsync();
        Assert.Equal(2, SanProxies.Attached);
        Assert.Equal(2, (await ReaderStateAsync(system.Id)).VerifiedRuns);

        // Nothing is left on the array, and every ledger row of it is closed.
        Assert.Empty(array.Vluns);
        Assert.DoesNotContain(array.Volumes.Values, v => v["copyOfID"] is not null);
        await using var check = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
        Assert.All(await check.Artifacts.Where(a => a.StorageSystemId == system.Id).ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
    }

    private static VmSnapshotContext Context(Guid connectionId, ArrayFakeSession backup) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), connectionId, null!, backup, new Dictionary<string, ChangeToken?> { ["0"] = new ChangeToken("xen-cbt", "previous") }, MaxPeers: 1);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        Assert.True(condition());
    }

    /// <summary>A storage system with the SR's volume turned on, a host object for the proxy, an iSCSI port, and the service with a fake SAN proxy.</summary>
    private async Task<(ArraySnapshotService Service, Testing.Arrays.FakeAlletra Array, FakeSanProxies Pool, string SrUuid, Guid ConnectionId, Guid SystemId)> ArraySetupAsync(
        bool experimental = true, int probation = 0, int gap = 0)
    {
        var client = await SignedInClientAsync();
        var array = Alletra;
        array.AddVolume("xcp-sr1", SrWwn, 64);
        array.AddHost("proxy01", ProxyIqn);
        array.AddIscsiPort("0:2:1", "10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001");
        var (_, srUuid) = _pool.AddLunSr("LUN SR", "3" + SrWwn.ToLowerInvariant());
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
        }, Ct));
        var volume = (await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{system.Id}/volumes", Ct))!.Single(v => v.VolumeName == "xcp-sr1");
        await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{volume.Id}", new SetStorageVolumeDto(true), Ct);
        var services = _factory!.Services;
        var dbFactory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync(Ct))
        {
            var entity = await db.StorageSystems.SingleAsync(s => s.Id == system.Id, Ct);
            entity.Options = new StorageSystemOptions { ExperimentalReaders = experimental, BatchWindowSeconds = 0, ProbationRuns = probation, MinSecondsAfterVmSnapshot = gap };
            await db.SaveChangesAsync(Ct);
        }

        return (services.GetRequiredService<ArraySnapshotService>(), array, SanProxies, srUuid, connection.Id, system.Id);
    }

    private static readonly Guid ArrayLeafVhd = Guid.Parse("0bbbbbbb-0000-4000-8000-0000000000aa");
    private static readonly Guid ArrayBaseVhd = Guid.Parse("0aaaaaaa-0000-4000-8000-0000000000aa");

    /// <summary>An SR's physical volume with a snapshot VDI (an empty-ish leaf) on its base copy, and what the disk reads as.</summary>
    private static (ArrayChainMember Leaf, ArrayChainMember Base, byte[] Image, byte[] Expected) LvhdImage(string srUuid)
    {
        var random = new Random(7);
        byte[] Sector()
        {
            var bytes = new byte[512];
            random.NextBytes(bytes);
            return bytes;
        }

        var baseSectors = new Dictionary<long, byte[]> { [0] = Sector(), [9] = Sector(), [4096] = Sector() };
        var leafSectors = new Dictionary<long, byte[]> { [9] = Sector(), [12000] = Sector() };
        var expected = new byte[LvhdCapacity];
        foreach (var (s, data) in baseSectors.Concat(leafSectors))
        {
            data.CopyTo(expected, s * 512);
        }

        var baseVhd = VhdBuilder.Build(LvhdCapacity, LvhdBlock, ArrayBaseVhd, null, baseSectors);
        var leafVhd = VhdBuilder.Build(LvhdCapacity, LvhdBlock, ArrayLeafVhd, ArrayBaseVhd, leafSectors);
        const string leafUuid = "5e1a7c0d-0000-4000-8000-000000000001";
        const string baseUuid = "5e1a7c0d-0000-4000-8000-000000000002";
        var image = PvBuilder.Build($"VG_XenStorage-{srUuid}",
        [
            new PvBuilder.Volume("MGT", Extents: 1),
            new PvBuilder.Volume($"VHD-{baseUuid}", baseVhd, Extents: Extents(baseVhd.Length)),
            new PvBuilder.Volume($"VHD-{leafUuid}", leafVhd, Extents: Extents(leafVhd.Length)),
        ]);
        return (new ArrayChainMember(leafUuid, LvhdCapacity, Extents(leafVhd.Length) * PvBuilder.ExtentBytes),
            new ArrayChainMember(baseUuid, LvhdCapacity, Extents(baseVhd.Length) * PvBuilder.ExtentBytes), image, expected);
    }

    private static long Extents(long bytes) => Math.Max(1, (bytes + PvBuilder.ExtentBytes - 1) / PvBuilder.ExtentBytes);

    /// <summary>A VM snapshot on the LUN SR whose disk is the leaf → base chain; records the early release.</summary>
    private sealed class ArrayFakeSession(string srUuid, ArrayChainMember leaf, ArrayChainMember baseCopy, byte[] image) : IVmBackupSession, IArraySnapshotFacts
    {
        public byte[] Image { get; } = image;

        public bool Released { get; private set; }

        public ChangedExtents Changes { get; } = new(false, null, [new Core.Extents.DiskExtent(0, LvhdBlock)]);

        public VmDescriptor Vm { get; } = new() { VmId = "vm-1", Name = "app01" };

        public IReadOnlyList<SnapshotDisk> Disks { get; } =
            [new SnapshotDisk("0", "vdi-source", leaf.Id, "app01 disk", LvhdCapacity, true, srUuid, "LUN SR", new Dictionary<string, string>())];

        public VmMetadataBlob Metadata { get; } = new("none", []);

        public string SnapshotId => "snapshot-1";

        public GuestFreezeResult? GuestFreeze => null;

        public bool CanReleaseEarly => true;

        public Task<IReadOnlyDictionary<string, ArrayDiskFacts>> GetArrayDisksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, ArrayDiskFacts>>(new Dictionary<string, ArrayDiskFacts> { ["0"] = new(srUuid, StorageLayout.Lvhd, [leaf, baseCopy]) });

        public Task ReleaseSnapshotAsync(IOperationLog log, CancellationToken cancellationToken)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken) =>
            Task.FromResult(since is null ? ChangedExtents.Full("no previous backup") : Changes);

        /// <summary>What the disk reads as through the platform's own transport (verify mode); null: no such reader.</summary>
        public byte[]? Normal { get; init; }

        public int ReadersOpened { get; private set; }

        public Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken)
        {
            Assert.False(Released, "The VM snapshot is read after it was released.");
            ReadersOpened++;
            return Normal is null ? throw new NotSupportedException("No reader in this test.") : Task.FromResult<IDiskSource>(new SnapshotReader(Normal));
        }

        public ChangeToken? GetNewChangeToken(SnapshotDisk disk) => new("xen-cbt", disk.SnapshotDiskId);

        public Task CommitAsync(IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The VM snapshot's disk read through NBD.</summary>
    private sealed class SnapshotReader(byte[] data) : IDiskReader
    {
        public long Capacity => data.Length;

        public string TransportName => "nbd";

        public int MaxRequestBytes => 256 * 1024;

        public int MaxConcurrentRequests => 4;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            data.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<StorageReaderState> ReaderStateAsync(Guid systemId)
    {
        await using var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
        return ArrayReaderStates.Find(await db.StorageSystems.AsNoTracking().SingleAsync(s => s.Id == systemId, Ct), ArrayReaderStates.Lvhd);
    }

    [Fact]
    public async Task Verify_mode_compares_a_sample_with_the_vm_snapshot_before_releasing_it_for_the_first_backups()
    {
        var (service, array, _, srUuid, connectionId, systemId) = await ArraySetupAsync(probation: 2);
        var (leaf, baseVdi, image, expected) = LvhdImage(srUuid);
        SanProxies.Luns[SrWwn.ToLowerInvariant()] = image;

        for (var run = 1; run <= 3; run++)
        {
            var backup = new ArrayFakeSession(srUuid, leaf, baseVdi, image) { Normal = expected };
            var log = new RecordingLog();
            await using (var lease = await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), log, Ct))
            {
                Assert.NotNull(lease);
                Assert.True(backup.Released);
                var verification = lease.GetVerification(backup.Disks[0]);
                if (run <= 2)
                {
                    Assert.Equal(1, backup.ReadersOpened);
                    Assert.NotNull(verification);
                    Assert.Equal("verify", verification.Method);
                    Assert.True(verification is { Passed: true, Regions: > 1 }, $"{verification}");
                    Assert.Contains(log.Entries, e => e.Message.Contains($"Verify mode (LVHD (XCP-ng LVM storage) reader on Alletra, backup {run} of 2)", StringComparison.Ordinal));
                }
                else
                {
                    Assert.Equal(0, backup.ReadersOpened);
                    Assert.Null(verification);
                }
            }

            Assert.Empty(array.Vluns);
        }

        Assert.Equal(2, (await ReaderStateAsync(systemId)).VerifiedRuns);
    }

    [Fact]
    public async Task A_verify_mismatch_keeps_the_vm_snapshot_and_switches_the_reader_off_until_it_is_turned_back_on()
    {
        var (service, array, _, srUuid, connectionId, systemId) = await ArraySetupAsync(probation: 5);
        var (leaf, baseVdi, image, expected) = LvhdImage(srUuid);
        SanProxies.Luns[SrWwn.ToLowerInvariant()] = image;
        var different = (byte[])expected.Clone();
        different[100_000] ^= 0x5a;

        var backup = new ArrayFakeSession(srUuid, leaf, baseVdi, image) { Normal = different };
        var log = new RecordingLog();
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), log, Ct));
        Assert.False(backup.Released);
        var error = Assert.Single(log.Entries, e => e.Severity == LogSeverity.Error).Message;
        Assert.Contains("read differently from array snapshot bdfu-", error, StringComparison.Ordinal);
        Assert.Contains("first at byte 100000", error, StringComparison.Ordinal);
        Assert.Contains("reader is switched off for Alletra", error, StringComparison.Ordinal);
        Assert.Empty(array.Vluns);
        Assert.DoesNotContain(array.Volumes.Values, v => v["copyOfID"] is not null);
        var state = await ReaderStateAsync(systemId);
        Assert.True(state.Tripped);
        Assert.Contains("disk app01 disk of app01", state.TripReason, StringComparison.Ordinal);

        // Switched off: the next VM is not even planned onto the array.
        var next = new ArrayFakeSession(srUuid, leaf, baseVdi, image) { Normal = expected };
        Assert.Null(await service.OnVmSnapshotCreatedAsync(Context(connectionId, next), log, Ct));
        Assert.Contains(log.Entries, e => e.Message.Contains("reader is switched off for storage system Alletra since", StringComparison.Ordinal));
        Assert.Equal(0, next.ReadersOpened);

        // The console shows it, and turns it back on; verify mode starts again.
        var client = _factory!.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add(ApiHeaders.Csrf, "1");
        await Ok(client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", "correct horse battery"), Ct));
        var shown = (await client.GetFromJsonAsync<List<StorageSystemDto>>("/api/storage-systems", Ct))!.Single(s => s.Id == systemId);
        var reader = Assert.Single(shown.Readers!, r => r.Reader == "lvhd");
        Assert.NotNull(reader.TrippedUtc);
        var reset = await Read<StorageReaderDto>(client.PostAsync($"/api/storage-systems/{systemId}/readers/lvhd/reset", null, Ct));
        Assert.Equal((null, 0, 5), (reset.TrippedUtc, reset.VerifiedRuns, reset.ProbationRuns));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsync($"/api/storage-systems/{systemId}/readers/ntfs/reset", null, Ct)).StatusCode);

        await using (var lease = await service.OnVmSnapshotCreatedAsync(Context(connectionId, next), log, Ct))
        {
            Assert.NotNull(lease);
            Assert.True(lease.GetVerification(next.Disks[0])!.Passed);
        }
    }

    [Fact]
    public async Task A_mismatch_found_after_the_release_switches_the_reader_off()
    {
        var (service, _, _, srUuid, connectionId, systemId) = await ArraySetupAsync();
        var (leaf, baseVdi, image, _) = LvhdImage(srUuid);
        SanProxies.Luns[SrWwn.ToLowerInvariant()] = image;
        var backup = new ArrayFakeSession(srUuid, leaf, baseVdi, image);
        await using (var lease = await service.OnVmSnapshotCreatedAsync(Context(connectionId, backup), new RecordingLog(), Ct))
        {
            Assert.NotNull(lease);
            Assert.Equal("LVHD (XCP-ng LVM storage)", lease.Reader);
            await lease.ReportMismatchAsync(backup.Disks[0], "3 of 32 unchanged regions differ", Ct);
        }

        var state = await ReaderStateAsync(systemId);
        Assert.True(state.Tripped);
        Assert.Equal("disk app01 disk of app01: 3 of 32 unchanged regions differ", state.TripReason);
    }

    /// <summary>
    /// One proxy with SAN access. A LUN it is asked for by WWN is the image of the volume the array's snapshot with that WWN
    /// is a copy of (the snapshot holds what the volume held).
    /// </summary>
    private sealed class FakeSanProxies(Testing.Arrays.FakeAlletra array) : IProxyPool
    {
        public Guid ProxyId { get; } = Guid.CreateVersion7();

        public string Initiator { get; set; } = ProxyIqn;

        /// <summary>Volume WWN (lower case) → its content.</summary>
        public Dictionary<string, byte[]> Luns { get; } = new(StringComparer.Ordinal);

        /// <summary>Builds a volume's content when its snapshot is opened (from the pool's state at that moment), by volume WWN.</summary>
        public Func<string, byte[]?>? LunBuilder { get; set; }

        public int Attached { get; private set; }

        public List<(string Portal, string Target)> Logins { get; } = [];

        public bool Released { get; private set; }

        public IReadOnlyList<ProxyInfo> Connected() =>
            [new ProxyInfo(ProxyId, "proxy01", "proxy01", null, null, [ProxyAgentProtocol.CapabilityHotAdd, ProxyAgentProtocol.CapabilitySan], Initiator)];

        public Task LoginIscsiAsync(Guid proxyId, IReadOnlyList<string> portals, string targetIqn, CancellationToken cancellationToken)
        {
            Logins.AddRange(portals.Select(p => (p, targetIqn)));
            return Task.CompletedTask;
        }

        public Task<IProxyDisk> AttachAsync(Guid proxyId, string serial, long sizeBytes, bool writable, CancellationToken cancellationToken)
        {
            Assert.False(writable);
            var snapshot = array.Volumes.Values.Single(v => Wwns.Normalize(v["wwn"]!.GetValue<string>()) == serial);
            var source = array.Volumes.Values.Single(v => v["id"]!.GetValue<long>() == snapshot["copyOfID"]!.GetValue<long>());
            var volumeWwn = Wwns.Normalize(source["wwn"]!.GetValue<string>());
            var content = LunBuilder?.Invoke(volumeWwn) ?? Luns.GetValueOrDefault(volumeWwn) ?? new byte[sizeBytes];
            Attached++;
            var lun = new byte[sizeBytes];
            content.CopyTo(lun, 0);
            return Task.FromResult<IProxyDisk>(new Disk(this, lun, serial));
        }

        private sealed class Disk(FakeSanProxies owner, byte[] lun, string serial) : IProxyDisk
        {
            public string Description => $"proxy proxy01 /dev/disk/by-id/wwn-0x{serial}";

            public long SizeBytes => lun.Length;

            public Task<IDiskReader> OpenReaderAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken) =>
                Task.FromResult<IDiskReader>(new MemoryReader(lun));

            public Task<IDiskBlockWriter> OpenWriterAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken) => throw new NotSupportedException();

            public ValueTask DisposeAsync()
            {
                owner.Released = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class MemoryReader(byte[] data) : IDiskReader
        {
            public long Capacity => data.Length;

            public string TransportName => "nbd";

            public int MaxRequestBytes => 1024 * 1024;

            public int MaxConcurrentRequests => 4;

            public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
            {
                data.AsMemory((int)offset, destination.Length).CopyTo(destination);
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
