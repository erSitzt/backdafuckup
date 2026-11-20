using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Arrays;
using Backdafuckup.Server.Data;
using Backdafuckup.Testing.Arrays;
using Backdafuckup.Testing.Vim;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Storage systems (spec 3, task 3): adding an array, mapping its volumes to datastores and SRs by WWN, leftovers and cleanup.</summary>
public sealed partial class ApiIntegrationTests
{
    private const string SrWwn = "60002AC0000000000000002300019C45";
    private const string DatastoreWwn = "60002AC0000000000000002200019C45";

    private FakeAlletra? _alletra;

    private FakeAlletra Alletra => _alletra ??= new FakeAlletra();

    private FakeAlletra? _alletra2;

    /// <summary>The second array of a replicated pair (address array02.corp.example).</summary>
    private FakeAlletra Alletra2 => _alletra2 ??= new FakeAlletra();

    [Fact]
    public async Task A_storage_system_maps_its_volumes_to_srs_and_datastores_by_wwn_and_cleans_up_only_its_own_objects()
    {
        var client = await SignedInClientAsync();
        var array = Alletra;
        // The licence list as a B10000 gives it: no "Virtual Copy", but all features (no warning).
        array.Licenses = [new JsonObject { ["name"] = "All features including SED Encryption", ["expiration"] = new JsonObject { ["ms"] = 2147483647000, ["tz"] = "+0100" } }];
        var srVolume = array.AddVolume("xcp-sr1", SrWwn, 512 * 1024);
        var dsVolume = array.AddVolume("vdi-ds1", DatastoreWwn, 1024 * 1024);
        array.AddVolume("unrelated", "60002AC0000000000000002400019C45", 1024);
        var (_, srUuid) = _pool.AddLunSr("LUN SR", "3" + SrWwn.ToLowerInvariant());
        var lab = Vsphere;
        lab.Vcenter.SetVmfs(lab.Datastore, 6, "naa." + DatastoreWwn.ToLowerInvariant());
        await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "VSphere", DisplayName = "Lab vCenter", Address = "vcenter.corp.example", UserName = lab.Vcenter.UserName, Password = lab.Vcenter.Password,
        }, Ct));

        // Wrong password: refused, naming the array.
        var refused = await client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto { Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = "wrong" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Storage system array01.corp.example:443: Array array01.corp.example: sign-in as bdfu-wsapi refused", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var system = await Read<StorageSystemDto>(client.PostAsJsonAsync("/api/storage-systems", new CreateStorageSystemDto
        {
            Name = "Alletra", Address = "array01.corp.example", UserName = array.User, Password = array.Password,
        }, Ct));
        Assert.Equal(("Connected", "HPE Alletra Storage MP B10000", "CZ0000TEST", 2, 0), (system.Status, system.Facts!.Model, system.Facts.Serial, system.Volumes, system.VolumesEnabled));
        Assert.Null(system.Facts.LicenseWarning);

        var volumes = (await client.GetFromJsonAsync<List<StorageVolumeDto>>($"/api/storage-systems/{system.Id}/volumes", Ct))!;
        Assert.Equal([("vdi-ds1", "ds-shared-01", "Vmfs6", "Lab vCenter"), ("xcp-sr1", "LUN SR", "Lvhd", "Lab pool")],
            volumes.Select(v => (v.VolumeName, v.StorageName!, v.Layout, v.ConnectionName!)));
        Assert.Equal(srUuid, volumes.Single(v => v.VolumeName == "xcp-sr1").StorageNativeId);
        Assert.All(volumes, v => Assert.False(v.Enabled));
        var sr = volumes.Single(v => v.VolumeName == "xcp-sr1");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{sr.Id}", new SetStorageVolumeDto(true), Ct)).StatusCode);

        // The datastore now spans two LUNs: refused, and it cannot be turned on.
        lab.Vcenter.SetVmfs(lab.Datastore, 6, "naa." + DatastoreWwn.ToLowerInvariant(), "naa.60002ac0000000000000002500019c45");
        volumes = (await Read<List<StorageVolumeDto>>(client.PostAsync($"/api/storage-systems/{system.Id}/volumes/refresh", null, Ct)))!;
        var ds = volumes.Single(v => v.VolumeName == "vdi-ds1");
        Assert.Equal("Datastore ds-shared-01 spans 2 LUNs; a backup from array snapshots needs a datastore on one LUN.", ds.Refusal);
        Assert.True(volumes.Single(v => v.VolumeName == "xcp-sr1").Enabled);
        var off = await client.PutAsJsonAsync($"/api/storage-systems/{system.Id}/volumes/{ds.Id}", new SetStorageVolumeDto(true), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        Assert.Contains("Volume vdi-ds1 cannot be used: Datastore ds-shared-01 spans 2 LUNs", await off.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A session took a snapshot and presented it, then died: the ledger names both, the cleanup removes the presentation, then the snapshot.
        var service = _factory!.Services.GetRequiredService<StorageSystems>();
        var connectionId = (await client.GetFromJsonAsync<List<ConnectionDto>>("/api/connections", Ct))!.Single(c => c.DisplayName == "Lab pool").Id;
        var ledger = new DbArtifactLedger(_factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(), connectionId, null, null, TimeProvider.System, system.Id);
        ArraySnapshot snapshot;
        await using (var session = await service.OpenAsync(system.Id, Ct))
        {
            snapshot = Assert.Single(await session.CreateSnapshotsAsync(
                new ArraySnapshotRequest([AlletraVolume(srVolume)], "bdfu-0badf00d", "session=test", true, TimeSpan.FromHours(6)), ledger, NullOperationLog.Instance, Ct));
            array.AddHost("bdfu-proxy-01", "iqn.2004-10.com.ubuntu:01:proxy01");
            await session.PresentAsync(snapshot, "bdfu-proxy-01", ledger, Ct);
        }

        Assert.StartsWith($"backdafuckup server={await service.ServerIdAsync(Ct)} ", array.Volumes[snapshot.Uid]["comment"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(await client.GetFromJsonAsync<List<StorageLeftoverDto>>($"/api/storage-systems/{system.Id}/leftovers", Ct) ?? [], l => Assert.True(l.Recorded));
        var busy = await client.DeleteAsync($"/api/storage-systems/{system.Id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Contains("still has 2 snapshot(s) or presentation(s) of this server to clean up", await busy.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            await db.Artifacts.Where(a => a.StorageSystemId == system.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdatedUtc, DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        }

        await _factory.Services.GetRequiredService<Backdafuckup.Server.Engine.StartupRecovery>().RetryPendingArtifactsAsync(
            _factory.Services.GetRequiredService<Backdafuckup.Server.Engine.RunningSessions>(), Ct);
        Assert.Empty(array.Vluns);
        Assert.False(array.Volumes.ContainsKey(snapshot.Uid));
        Assert.True(array.Volumes.ContainsKey(srVolume["uid"]!.GetValue<string>()));
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            Assert.All(await db.Artifacts.Where(a => a.StorageSystemId == system.Id).ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
        }

        // Orphans: ours without a ledger row are removed once older than two hours; a young one, an admin's and another server's stay.
        string Tag(DateTimeOffset created) => $"backdafuckup server={service.ServerIdAsync(Ct).Result} source=x created={created:yyyy-MM-ddTHH:mm:ssZ}";
        var old = array.AddVolume("bdfu-0rphan01-01", "60002AC0000000000000003100019C45", 1024);
        old["type"] = "VVTYPE_SNAPSHOT";
        old["copyOfID"] = srVolume["id"]!.GetValue<long>();
        old["comment"] = Tag(DateTimeOffset.UtcNow.AddHours(-3));
        var young = array.AddVolume("bdfu-young001-01", "60002AC0000000000000003200019C45", 1024);
        young["type"] = "VVTYPE_SNAPSHOT";
        young["copyOfID"] = srVolume["id"]!.GetValue<long>();
        young["comment"] = Tag(DateTimeOffset.UtcNow.AddMinutes(-5));
        var foreign = array.AddVolume("bdfu-other001-01", "60002AC0000000000000003300019C45", 1024);
        foreign["type"] = "VVTYPE_SNAPSHOT";
        foreign["copyOfID"] = srVolume["id"]!.GetValue<long>();
        foreign["comment"] = "backdafuckup server=another-server created=2026-01-01T00:00:00Z";
        await service.ScanOrphansAsync(Ct);
        Assert.False(array.Volumes.ContainsKey(old["uid"]!.GetValue<string>()));
        Assert.True(array.Volumes.ContainsKey(young["uid"]!.GetValue<string>()));
        Assert.True(array.Volumes.ContainsKey(foreign["uid"]!.GetValue<string>()));

        // Nothing of ours open any more: the storage system can be removed (the young orphan is the array's expiry's).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/storage-systems/{system.Id}", Ct)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<StorageSystemDto>>("/api/storage-systems", Ct))!);
    }

    private static ArrayVolume AlletraVolume(System.Text.Json.Nodes.JsonObject volume) => new(
        volume["uid"]!.GetValue<string>(), volume["id"]!.GetValue<long>(), volume["name"]!.GetValue<string>(), volume["wwn"]!.GetValue<string>(),
        volume["sizeMiB"]!.GetValue<long>() << 20, ArrayVolumeTypes.Base, null, null, false, null);
}
