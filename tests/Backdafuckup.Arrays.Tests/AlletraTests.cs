using System.Net;
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
        (new AlletraProvider(_ => array.CreateHandler(), TimeSpan.FromSeconds(30), [.. Enumerable.Repeat(TimeSpan.Zero, 6)]),
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
    public async Task Answers_shaped_as_a_b10000_gives_them_are_read()
    {
        // Seen on a B10000 (OS 10.5.51): licences with {"ms", "tz"} expiries, privileges per domain ("all"), and
        // every /api/v3 call refused without a media type (the fake refuses it too, so every test here covers that).
        var array = new FakeAlletra
        {
            Licenses =
            [
                new JsonObject { ["name"] = "All features including SED Encryption", ["expiration"] = new JsonObject { ["ms"] = 2147483647000, ["tz"] = "+0100" } },
                new JsonObject { ["name"] = "Thin Provisioning (102492160 GiB)", ["expiration"] = new JsonObject { ["ms"] = 1863907200000, ["tz"] = "+0100" }, ["sizeOrCount"] = "102492160 GiB" },
            ],
            Privileges = [new JsonObject { ["domain"] = "all", ["domainId"] = 0, ["role"] = "browse", ["roleId"] = 2, ["localizedRole"] = new JsonObject { ["key"] = "USER_ROLE_BROWSE", ["default"] = "browse" } }],
        };
        var (provider, settings) = Connect(array);
        var info = await provider.TestConnectionAsync(settings, Ct);

        Assert.Equal(["All features including SED Encryption", "Thin Provisioning (102492160 GiB)"], info.Licenses.Select(l => l.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1863907200000), info.Licenses[1].ExpiresUtc);
        Assert.Equal(["browse in all domains"], info.Privileges);
        Assert.DoesNotContain(array.Requests, r => r.Contains("refused", StringComparison.Ordinal));
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

    [Fact]
    public async Task A_busy_array_is_asked_again_and_a_lasting_refusal_says_how_long_it_was_tried()
    {
        var array = new FakeAlletra();
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        var refusals = new Dictionary<string, int>(StringComparer.Ordinal);
        (HttpStatusCode, string)? Times(string request, int times, HttpStatusCode status, string message)
        {
            refusals[request] = refusals.GetValueOrDefault(request) + 1;
            return refusals[request] <= times ? (status, message) : null;
        }

        // Too many sessions at the sign-in, then busy twice on the snapshot: both go through.
        array.Refuse = r => r == "POST /api/v3/credentials" ? Times(r, 1, HttpStatusCode.Conflict, "Maximum number of sessions reached, try again later")
            : r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal) ? Times(r, 2, HttpStatusCode.ServiceUnavailable, "System busy") : null;
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var snapshot = Assert.Single(await session.CreateSnapshotsAsync(
            new ArraySnapshotRequest([ds], "bdfu-b0000001", string.Empty, true, TimeSpan.FromHours(1)), new InMemoryArtifactLedger(), NullOperationLog.Instance, Ct));
        Assert.Equal("bdfu-b0000001-01", snapshot.Name);
        Assert.Equal(3, array.Requests.Count(r => r.StartsWith($"POST /api/v3/volumes/{ds.Uid}", StringComparison.Ordinal)));

        // Busy for good: the error says it was tried again. An error that is not "busy" is not tried again.
        array.Refuse = r => r == "GET /api/v3/volumes" ? (HttpStatusCode.ServiceUnavailable, "System busy") : null;
        var busy = await Assert.ThrowsAsync<AlletraException>(() => session.FindVolumesByWwnAsync(["60002ac0000000000000002200019c45"], Ct));
        Assert.Equal("Array array01.corp.example: GET api/v3/volumes answered HTTP 503: System busy (still busy after 6 retries over 0 s)", busy.Message);
        array.Refuse = null;
        var before = array.Requests.Count;
        await Assert.ThrowsAsync<AlletraException>(() => session.CreateSnapshotsAsync(
            new ArraySnapshotRequest([ds], "bdfu-b0000001", string.Empty, true, TimeSpan.FromHours(1)), new InMemoryArtifactLedger(), NullOperationLog.Instance, Ct));
        Assert.Single(array.Requests.Skip(before), r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_for_lack_of_rights_names_the_operation_the_role_it_needs_and_the_roles_the_account_has()
    {
        var array = new FakeAlletra { Privileges = [new JsonObject { ["domain"] = "bdfu-test", ["role"] = "browse", ["roleId"] = 1 }] };
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        var (provider, settings) = Connect(array);
        Assert.Equal(["browse in domain bdfu-test"], (await provider.TestConnectionAsync(settings, Ct)).Privileges);

        array.Refuse = r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal) ? (HttpStatusCode.Conflict, "User does not have the required privilege for this operation") : null;
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();
        var denied = await Assert.ThrowsAsync<AlletraException>(() => session.CreateSnapshotsAsync(
            new ArraySnapshotRequest([ds], "bdfu-0000000a", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct));

        Assert.True(denied.PermissionDenied);
        Assert.Equal("Array array01.corp.example: taking snapshot bdfu-0000000a-01 of vdi-ds1 was refused for lack of rights: the account bdfu-wsapi needs the role edit " +
                     "in domain bdfu-test (or in all domains), and it has browse in domain bdfu-test. The array said HTTP 409: User does not have the required privilege for this operation.",
            denied.Message);
        Assert.Single(array.Requests, r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal)); // not tried again
        Assert.Equal(ArtifactState.Done, Assert.Single(ledger.Items).State);

        // HTTP 403 is a refusal for lack of rights whatever the message; an account with roles in all domains says so.
        array.Privileges = [new JsonObject { ["domain"] = "-", ["role"] = "browse" }, "basic_edit"];
        array.Refuse = r => r.StartsWith("POST /api/v3/volumes/", StringComparison.Ordinal) ? (HttpStatusCode.Forbidden, "Forbidden") : null;
        array.ExpireSessions();
        var forbidden = await Assert.ThrowsAsync<AlletraException>(() => session.CreateSnapshotsAsync(
            new ArraySnapshotRequest([ds], "bdfu-0000000b", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct));
        Assert.Contains("and it has browse in all domains, basic_edit. The array said HTTP 403: Forbidden.", forbidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_is_presented_only_to_a_host_object_in_its_domain_or_in_a_domain_set_with_it()
    {
        var array = new FakeAlletra();
        var ds = Volume(array.AddVolume("vdi-ds1", "60002AC0000000000000002200019C45", 1024));
        array.AddHost("bdfu-proxy-01", "iqn.2004-10.com.ubuntu:01:proxy01");
        var host = array.Hosts.Single();
        var (provider, settings) = Connect(array);
        await using var session = await provider.OpenAsync(settings, Ct);
        var ledger = new InMemoryArtifactLedger();
        var snapshot = Assert.Single(await session.CreateSnapshotsAsync(new ArraySnapshotRequest([ds], "bdfu-d0000001", string.Empty, true, TimeSpan.FromHours(1)), ledger, NullOperationLog.Instance, Ct));

        // Another domain: refused before anything is made, both named.
        host["domain"] = "vdi-hosts";
        var other = await Assert.ThrowsAsync<AlletraException>(() => session.PresentAsync(snapshot, "bdfu-proxy-01", ledger, Ct));
        Assert.Equal("Array array01.corp.example: snapshot bdfu-d0000001-01 is in domain bdfu-test, host object bdfu-proxy-01 in domain vdi-hosts, and the array presents a volume " +
                     "only to a host in the same domain: put host object bdfu-proxy-01 into domain bdfu-test (or a domain set with it), or map the proxy to a host object in bdfu-test. " +
                     "Nothing was presented.", other.Message);
        Assert.DoesNotContain(array.Requests, r => r.StartsWith("POST /api/v3/vluns", StringComparison.Ordinal));
        Assert.DoesNotContain(ledger.Items, a => a.Kind == ArrayArtifactKinds.Presentation);

        // A domain set that holds the snapshot's domain: presented.
        array.DomainSets.Add(new JsonObject { ["uid"] = "ds1", ["name"] = "vdi-hosts", ["members"] = new JsonArray("bdfu-test", "vdi") });
        var presented = await session.PresentAsync(snapshot, "bdfu-proxy-01", ledger, Ct);
        await session.UnpresentAsync(presented, Ct);

        // The host in no domain: tried, and the array's refusal comes with both domains.
        host.Remove("domain");
        var none = await Assert.ThrowsAsync<AlletraException>(() => session.PresentAsync(snapshot, "bdfu-proxy-01", ledger, Ct));
        Assert.EndsWith("answered HTTP 409: Host bdfu-proxy-01 and volume bdfu-d0000001-01 are in different domains " +
                        "(snapshot bdfu-d0000001-01 is in domain bdfu-test, host object bdfu-proxy-01 in no domain)", none.Message, StringComparison.Ordinal);

        // No such host object.
        var missing = await Assert.ThrowsAsync<AlletraException>(() => session.PresentAsync(snapshot, "bdfu-proxy-99", ledger, Ct));
        Assert.Equal("Array array01.corp.example: there is no host object bdfu-proxy-99 (create it with the proxy's initiator name, or map the proxy to an existing one).", missing.Message);
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
