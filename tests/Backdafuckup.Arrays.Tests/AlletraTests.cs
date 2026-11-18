using System.Text.Json.Nodes;
using Backdafuckup.Arrays.Alletra;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Testing.Arrays;

namespace Backdafuckup.Arrays.Tests;

/// <summary>The Alletra WSAPI v3 client against the fake array: sessions, asynchronous calls, snapshots, presentations, proofs.</summary>
public sealed class AlletraTests
{
    private const string ServerId = "0f6c8a2e-1b3d-4e5f-8a9b-0c1d2e3f4a5b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (AlletraProvider Provider, ArrayConnectionSettings Settings) Connect(FakeAlletra array) =>
        (new AlletraProvider(_ => array.CreateHandler(), TimeSpan.FromSeconds(30)),
         new ArrayConnectionSettings("array01.corp.example", 443, array.User, array.Password, TlsTrustMode.Pinned, [], ServerId));

    [Theory]
    [InlineData(FakeListShape.MembersKeyed)]
    [InlineData(FakeListShape.MembersArray)]
    [InlineData(FakeListShape.PlainArray)]
    public async Task The_connection_test_reads_the_array_and_volumes_are_found_by_wwn_in_any_notation(FakeListShape shape)
    {
        var array = new FakeAlletra { ListShape = shape };
        array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024 * 1024);
        array.AddVolume("xcp-sr1", "60002AC0000000000000002300019C45", 512 * 1024);
        var (provider, settings) = Connect(array);

        var info = await provider.TestConnectionAsync(settings, Ct);
        Assert.Equal(("alletra-test", "HPE Alletra Storage MP B10000", "CZ0000TEST", "10.4.2", TimeSpan.FromMinutes(30)), (info.Name, info.Model, info.Serial, info.OsVersion, info.SessionTimeout));
        Assert.Equal(["Virtual Copy", "Remote Copy"], info.Licenses.Select(l => l.Name));
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 0, 0, 0, TimeSpan.Zero), info.Licenses[1].ExpiresUtc);
        Assert.Equal(["edit"], info.Privileges);

        await using var session = await provider.OpenAsync(settings, Ct);
        var found = await session.FindVolumesByWwnAsync(["naa.60002ac0000000000000002300019c45", "3" + "60002AC0000000000000002200019C45"], Ct);
        Assert.Equal(["vdi-ds1", "xcp-sr1"], found.Select(v => v.Name).Order(StringComparer.Ordinal));
        Assert.Equal(512L << 30, found.Single(v => v.Name == "xcp-sr1").SizeBytes);
    }

    [Fact]
    public async Task A_wrong_password_names_the_array_and_the_account()
    {
        var array = new FakeAlletra();
        var (provider, settings) = Connect(array);
        var ex = await Assert.ThrowsAsync<AlletraException>(() => provider.TestConnectionAsync(settings with { Password = "wrong" }, Ct));
        Assert.Contains("Array array01.corp.example: sign-in as bdfu-wsapi refused", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshots_are_named_commented_and_expire_and_are_recorded_before_they_are_taken(bool asyncCalls)
    {
        var array = new FakeAlletra { AsyncCalls = asyncCalls };
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        var sr = Volume(array.AddVolume("xcp-sr1", "60002AC0000000000000002300019C45", 1024));
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();
        var log = new Lines();

        var snapshots = await session.CreateSnapshotsAsync(
            new ArraySnapshotRequest([ds, sr], "bdfu-1a2b3c4d", "session=1a2b3c4d", ReadOnly: true, TimeSpan.FromHours(6)), ledger, log, Ct);

        Assert.Equal(["bdfu-1a2b3c4d-01", "bdfu-1a2b3c4d-02"], snapshots.Select(s => s.Name));
        var created = array.Volumes[snapshots[0].Uid];
        Assert.Equal(("VVTYPE_SNAPSHOT", ds.Id, true, 6L * 3600), (created["type"]!.GetValue<string>(), created["copyOfID"]!.GetValue<long>(), created["readOnly"]!.GetValue<bool>(),
            created["expireSecs"]!.GetValue<long>()));
        Assert.StartsWith($"backdafuckup server={ServerId} source={ds.Uid} created=", created["comment"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(array.Requests, r => r.Contains("retainSecs", StringComparison.Ordinal));
        Assert.Equal([(ArrayArtifactKinds.Snapshot, snapshots[0].Uid, "bdfu-1a2b3c4d-01", ArtifactState.Active), (ArrayArtifactKinds.Snapshot, snapshots[1].Uid, "bdfu-1a2b3c4d-02", ArtifactState.Active)],
            ledger.Items.Select(a => (a.Kind, a.NativeId, a.NativeRef!, a.State)));
        Assert.Contains(log.Messages, m => m.StartsWith("Array snapshot bdfu-1a2b3c4d-01 of vdi-ds1 on alletra-test (array01.corp.example) in ", StringComparison.Ordinal));

        // A long name base is cut so the name fits the array's 31 characters.
        var longer = await session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-a-very-long-session-name-here", string.Empty, true, TimeSpan.FromHours(1)), ledger, log, Ct);
        Assert.Equal(31, longer[0].Name.Length);
        Assert.EndsWith("-01", longer[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_session_signs_in_again_once_and_a_failed_task_or_conflict_names_the_arrays_message()
    {
        var array = new FakeAlletra { AsyncCalls = true };
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();

        array.ExpireSessions();
        Assert.Single(await session.FindVolumesByWwnAsync(["60002ac0000000000000002200019c45"], Ct));
        Assert.Equal(2, array.Logins);

        array.FailNextTask = "Snapshot limit of the volume reached";
        var failed = await Assert.ThrowsAsync<AlletraException>(() => session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-00000001", string.Empty, true, TimeSpan.FromHours(1)),
            ledger, NullOperationLog.Instance, Ct));
        Assert.Contains("Array array01.corp.example: POST api/v3/volumes/", failed.Message, StringComparison.Ordinal);
        Assert.Contains("failed: Snapshot limit of the volume reached (STATE_FAILED)", failed.Message, StringComparison.Ordinal);
        // The fake made the snapshot before its task failed: recorded for the cleanup, which deletes it.
        var left = Assert.Single(ledger.Items);
        Assert.Equal((ArtifactState.CleanupPending, array.Volumes.Values.Single(v => v["name"]!.GetValue<string>() == "bdfu-00000001-01")["uid"]!.GetValue<string>()), (left.State, left.NativeId));

        await session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-00000002", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct);
        var conflict = await Assert.ThrowsAsync<AlletraException>(() => session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-00000002", string.Empty, true, TimeSpan.FromHours(1)),
            ledger, NullOperationLog.Instance, Ct));
        Assert.Contains("answered HTTP 409: Volume bdfu-00000002-01 already exists", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_is_presented_to_one_host_extended_and_deleted_only_with_every_proof()
    {
        var array = new FakeAlletra();
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        array.AddHost("esx01", "iqn.1998-01.com.vmware:esx01-12345678");
        array.AddHost("bdfu-proxy-01", "iqn.2004-10.com.ubuntu:01:proxy01");
        array.AddIscsiPort("0:2:1", "10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001");
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();
        var snapshot = Assert.Single(await session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-cafe0001", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct));

        Assert.Equal([("esx01", "iqn.1998-01.com.vmware:esx01-12345678"), ("bdfu-proxy-01", "iqn.2004-10.com.ubuntu:01:proxy01")],
            (await session.ListHostsAsync(Ct)).Select(h => (h.Name, Assert.Single(h.IscsiNames))));
        Assert.Equal(("10.26.101.10", "iqn.2000-05.com.3pardata:20210002ac000001"), Assert.Single(await session.GetIscsiPortalsAsync(Ct)) is var portal ? (portal.Address, portal.TargetIqn) : default);

        await Assert.ThrowsAsync<ArgumentException>(() => session.PresentAsync(snapshot, "set:esx-cluster", ledger, Ct));
        var presented = await session.PresentAsync(snapshot, "bdfu-proxy-01", ledger, Ct);
        Assert.Equal((snapshot.Wwn, "bdfu-proxy-01", 1), (presented.Wwn, presented.HostName, presented.Lun));
        Assert.Equal(ArtifactState.Active, ledger.Items.Single(a => a.Kind == ArrayArtifactKinds.Presentation).State);

        await session.ExtendExpiryAsync(snapshot, TimeSpan.FromHours(2), Ct);
        Assert.Equal(7200, array.Volumes[snapshot.Uid]["expireSecs"]!.GetValue<long>());
        Assert.Contains(array.Requests, r => r.StartsWith($"PATCH /api/v3/volumes/{snapshot.Uid} {{\"expireSecs\":7200}}", StringComparison.Ordinal));

        // Still presented: not deleted. Not ours (another server, a base volume, another source): left alone.
        var presentedError = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteSnapshotAsync(snapshot, Ct));
        Assert.Contains("is still presented to bdfu-proxy-01", presentedError.Message, StringComparison.Ordinal);
        await session.UnpresentAsync(presented, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteSnapshotAsync(snapshot with { SourceId = 4711 }, Ct));
        var foreignBase = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteSnapshotAsync(snapshot with { Uid = ds.Uid }, Ct));
        Assert.Contains("its name vdi-ds1 is not one of ours", foreignBase.Message, StringComparison.Ordinal);
        array.Volumes[snapshot.Uid]["comment"] = "backdafuckup server=another-server";
        var otherServer = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeleteSnapshotAsync(snapshot, Ct));
        Assert.Contains("its comment does not name this server", otherServer.Message, StringComparison.Ordinal);
        Assert.True(array.Volumes.ContainsKey(snapshot.Uid));
        array.Volumes[snapshot.Uid]["comment"] = $"backdafuckup server={ServerId} source={ds.Uid}";

        await session.DeleteSnapshotAsync(snapshot, Ct);
        Assert.False(array.Volumes.ContainsKey(snapshot.Uid));
        Assert.True(array.Volumes.ContainsKey(ds.Uid));
        await session.DeleteSnapshotAsync(snapshot, Ct); // gone already: fine
    }

    [Fact]
    public async Task The_orphan_scan_finds_only_our_snapshots_and_their_presentations_and_cleans_them_presentation_first()
    {
        var array = new FakeAlletra();
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        array.AddHost("esx01", "iqn.1998-01.com.vmware:esx01-12345678");
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();
        var snapshot = Assert.Single(await session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-dead0001", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct));
        await session.PresentAsync(snapshot, "esx01", ledger, Ct);
        // Not ours: an admin's snapshot with a similar name, and one of another backup server.
        var foreign = array.AddVolume("bdfu-admin-copy", "60002AC0000000000000002400019C45", 1024);
        foreign["type"] = "VVTYPE_SNAPSHOT";
        foreign["copyOfID"] = ds.Id;
        foreign["comment"] = "backdafuckup server=another-server";

        var found = await session.FindTaggedAsync(Ct);
        Assert.Equal([ArrayArtifactKinds.Presentation, ArrayArtifactKinds.Snapshot], found.Select(a => a.Kind));
        Assert.All(found, a => Assert.Equal(snapshot.Name, a.NativeRef));

        var log = new Lines();
        foreach (var artifact in found)
        {
            await session.CleanupArtifactAsync(artifact, log, Ct);
        }

        Assert.Empty(array.Vluns);
        Assert.False(array.Volumes.ContainsKey(snapshot.Uid));
        Assert.True(array.Volumes.ContainsKey(foreign["uid"]!.GetValue<string>()));
        Assert.Contains(log.Messages, m => m.StartsWith("Removed the presentation of bdfu-dead0001-01 to esx01", StringComparison.Ordinal));
        Assert.Contains(log.Messages, m => m.StartsWith("Deleted the array snapshot bdfu-dead0001-01", StringComparison.Ordinal));
    }

    private static ArrayVolume Volume(JsonObject volume) => AlletraSession.Volume(volume);

    private sealed class Lines : IOperationLog
    {
        public List<string> Messages { get; } = [];

        public void Write(LogSeverity severity, string message, string? key = null)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
        }
    }
}
