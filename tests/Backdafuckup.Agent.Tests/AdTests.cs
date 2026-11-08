using System.Net;
using System.Text;
using System.Text.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

public sealed class AdTests : IDisposable
{
    private const string Domain = "DC=bdfu-lab,DC=local";
    private static readonly AdAccount Admin = new() { UserName = @"BDFU-LAB\Administrator", Password = "secret" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryDirectory _backup = new("dc1.bdfu-lab.local:50000", Domain);
    private readonly InMemoryDirectory _live = new("dc1.bdfu-lab.local:389", Domain);
    private readonly Dictionary<string, Guid> _ids = [];

    public AdTests()
    {
        Directory.CreateDirectory(_root);
        Seed();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_search_gives_each_backup_object_its_live_state()
    {
        var queries = new AdQueries(_backup, _live, Domain);

        var (entries, cookie, scanned) = queries.Search(new AdJobRequest { Classes = ["user"], PageSize = 100 });

        Assert.Null(cookie);
        Assert.Equal(5, scanned);
        var states = entries.ToDictionary(e => e.Name, e => e.State);
        Assert.Equal(AdObjectStates.Unchanged, states["lab-user01"]);  // its OU was renamed: not a change of the user
        Assert.Equal(AdObjectStates.Changed, states["lab-user02"]);
        Assert.Equal(AdObjectStates.Deleted, states["lab-user03"]);
        Assert.Equal(AdObjectStates.Recycled, states["lab-user04"]);
        Assert.Equal(AdObjectStates.Gone, states["lab-user05"]);
        var deleted = entries.Single(e => e.Name == "lab-user03");
        Assert.Contains("CN=Deleted Objects", deleted.LiveDn, StringComparison.Ordinal);
        Assert.Equal("lab-user03", deleted.SamAccountName);
        Assert.Null(entries.Single(e => e.Name == "lab-user05").LiveDn);

        // Only what was deleted since the backup or is gone, and a search by name.
        (entries, _, _) = queries.Search(new AdJobRequest { States = [AdObjectStates.Deleted, AdObjectStates.Recycled, AdObjectStates.Gone] });
        Assert.Equal(["lab-user03", "lab-user04", "lab-user05"], entries.Select(e => e.Name).Order());
        (entries, _, _) = queries.Search(new AdJobRequest { Text = "user02" });
        Assert.Equal("lab-user02", Assert.Single(entries).Name);
    }

    [Fact]
    public void Without_the_recycle_bin_a_recycled_object_is_an_ordinary_tombstone()
    {
        // AD marks a deleted object as recycled at once when the Recycle Bin is off (seen on Windows Server 2025).
        var queries = new AdQueries(_backup, _live, Domain, recycleBin: false);

        var (entries, _, _) = queries.Search(new AdJobRequest { Classes = ["user"] });

        Assert.Equal(AdObjectStates.Deleted, entries.Single(e => e.Name == "lab-user04").State);
        Assert.Equal(AdObjectStates.Deleted, entries.Single(e => e.Name == "lab-user03").State);
    }

    [Fact]
    public void A_search_is_paged_and_looks_up_the_live_objects_in_batches()
    {
        var queries = new AdQueries(_backup, _live, Domain);

        var first = queries.Search(new AdJobRequest { Classes = ["user"], PageSize = 2 });
        var second = queries.Search(new AdJobRequest { Classes = ["user"], PageSize = 2, Cookie = Convert.ToBase64String(first.Cookie!) });
        var third = queries.Search(new AdJobRequest { Classes = ["user"], PageSize = 2, Cookie = Convert.ToBase64String(second.Cookie!) });

        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, second.Entries.Count);
        Assert.Single(third.Entries);
        Assert.Null(third.Cookie);
        Assert.Equal(5, first.Entries.Concat(second.Entries).Concat(third.Entries).Select(e => e.Guid).Distinct().Count());
        Assert.All(_live.Filters.Where(f => f.Contains("objectGUID", StringComparison.Ordinal)), f => Assert.StartsWith("(|(objectGUID=", f, StringComparison.Ordinal));
    }

    [Fact]
    public void The_tree_lists_the_containers_below_a_dn_with_their_state()
    {
        var queries = new AdQueries(_backup, _live, Domain);

        var top = queries.Tree(null);
        var lab = Assert.Single(top, e => e.Name == "Lab");
        Assert.True(lab.Container);
        Assert.Equal("OU=Lab2," + Domain, lab.LiveDn);
        Assert.Equal(AdObjectStates.Changed, lab.State); // renamed
        Assert.Equal(["Groups", "Users"], queries.Tree("OU=Lab," + Domain).Select(e => e.Name));
    }

    [Fact]
    public void The_object_view_compares_every_attribute_and_says_what_a_restore_can_write()
    {
        var queries = new AdQueries(_backup, _live, Domain);
        var schema = new AdSchema(
        [
            new AdAttributeInfo { Name = "description", Syntax = "2.5.5.12" },
            new AdAttributeInfo { Name = "manager", Syntax = "2.5.5.1", LinkId = 42 },
            new AdAttributeInfo { Name = "memberOf", Syntax = "2.5.5.1", LinkId = 3, SystemOnly = true },
            new AdAttributeInfo { Name = "objectSid", Syntax = "2.5.5.17", SystemOnly = true },
            new AdAttributeInfo { Name = "whenChanged", Syntax = "2.5.5.11", SystemOnly = true },
            new AdAttributeInfo { Name = "pwdLastSet", Syntax = "2.5.5.16" },
            new AdAttributeInfo { Name = "sAMAccountName", Syntax = "2.5.5.12" },
            new AdAttributeInfo { Name = "userAccountControl", Syntax = "2.5.5.9" },
        ]);

        var changed = queries.Object(_ids["lab-user02"], schema);

        Assert.Equal(AdObjectStates.Changed, changed.State);
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-1102", changed.Sid);
        var description = changed.Attributes.Single(a => a.Name == "description");
        Assert.True(description.Changed);
        Assert.True(description.Restorable);
        Assert.Equal("Sales", description.Backup.Single().Text);
        Assert.Equal("Marketing", description.Live.Single().Text);

        // A link points to its object by GUID: the manager's OU was renamed, which is no change of the attribute.
        var manager = changed.Attributes.Single(a => a.Name == "manager");
        Assert.False(manager.Changed);
        Assert.True(manager.Restorable && manager.Linked);
        Assert.Equal(_ids["lab-user01"], manager.Backup.Single().Guid);
        Assert.Equal("CN=lab-user01,OU=Users,OU=Lab," + Domain, manager.Backup.Single().Text);
        Assert.Equal("CN=lab-user01,OU=Users,OU=Lab2," + Domain, manager.Live.Single().Text);

        Assert.False(changed.Attributes.Single(a => a.Name == "objectSid").Restorable);
        var whenChanged = changed.Attributes.Single(a => a.Name == "whenChanged");
        Assert.False(whenChanged.Changed);
        Assert.Equal("maintained by the domain controllers", whenChanged.Note);
        Assert.Contains("password", changed.Attributes.Single(a => a.Name == "pwdLastSet").Note, StringComparison.Ordinal);
        Assert.Contains("UTC", changed.Attributes.Single(a => a.Name == "pwdLastSet").Backup.Single().Text, StringComparison.Ordinal);
        Assert.DoesNotContain(changed.Attributes, a => a.Name == "memberOf");
        Assert.Equal(_ids["LabGroupA"], changed.MemberOfBackup.Single().Guid);
        Assert.Empty(changed.MemberOfLive);

        // The parent is found live by its GUID although the OU above it was renamed.
        Assert.Equal("OU=Users,OU=Lab," + Domain, changed.ParentDn);
        Assert.Equal("OU=Users,OU=Lab2," + Domain, changed.LiveParentDn);

        // A tombstone: the live side has only what the tombstone kept.
        var deleted = queries.Object(_ids["lab-user03"], schema);
        Assert.Equal(AdObjectStates.Deleted, deleted.State);
        Assert.Empty(deleted.Attributes.Single(a => a.Name == "description").Live);
        Assert.Equal("OU=Users,OU=Lab2," + Domain, deleted.LiveParentDn);

        var gone = queries.Object(_ids["lab-user05"], schema);
        Assert.Equal(AdObjectStates.Gone, gone.State);
        Assert.Null(gone.LiveDn);
        Assert.All(gone.Attributes, a => Assert.False(a.Changed));

        var missing = Assert.Throws<AdDirectoryException>(() => queries.Object(Guid.NewGuid(), schema));
        Assert.Contains("dc1.bdfu-lab.local:50000", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_text_is_escaped_and_classes_are_combined()
    {
        Assert.Equal("(objectClass=*)", AdQueries.Filter(null, []));
        Assert.Equal(
            "(&(|(name=*a\\2a\\28b\\29\\5c*)(sAMAccountName=*a\\2a\\28b\\29\\5c*)(displayName=*a\\2a\\28b\\29\\5c*)(userPrincipalName=*a\\2a\\28b\\29\\5c*))(|(&(objectCategory=person)(objectClass=user))(objectClass=group)))",
            AdQueries.Filter(" a*(b)\\ ", ["user", "group", "unknown"]));
    }

    [Theory]
    [InlineData("<GUID=6f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d>;<SID=010500000000000515000000>;CN=x,DC=a", "6f1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", "CN=x,DC=a")]
    [InlineData("CN=plain,DC=a", null, "CN=plain,DC=a")]
    public void Extended_dns_are_split_into_guid_and_dn(string value, string? guid, string dn)
    {
        var parsed = AdText.ParseExtendedDn(value);
        Assert.Equal(guid is null ? null : Guid.Parse(guid), parsed.Guid);
        Assert.Equal(dn, parsed.Dn);
    }

    [Fact]
    public void Names_sids_and_times_are_shown_readably()
    {
        Assert.Equal("lab-user03", AdText.RdnValue("CN=lab-user03\\0ADEL:0f4c2b1a-0000-0000-0000-000000000000,CN=Deleted Objects," + Domain));
        Assert.Equal("Smith, John", AdText.RdnValue("CN=Smith\\, John,OU=Users," + Domain));
        Assert.Equal("OU=Users," + Domain, AdText.Parent("CN=Smith\\, John,OU=Users," + Domain));
        Assert.Equal("S-1-5-32-544", AdText.Sid([1, 2, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0, 0x20, 2, 0, 0]));
        Assert.Null(AdText.Sid([1, 2, 0]));
        Assert.Equal("0 (never)", AdText.FileTime("0"));
        Assert.Equal("9223372036854775807 (never expires)", AdText.FileTime("9223372036854775807"));
        Assert.Equal("133000000000000000 (2022-06-18 04:26:40 UTC)", AdText.FileTime("133000000000000000"));
    }

    [Fact]
    public void A_mount_prepares_a_folder_serves_the_backup_and_deletes_everything_at_unmount()
    {
        var env = new FakeAdEnvironment(_root, _backup, _live);
        var log = new AgentLog(Path.Combine(_root, "agent.log"));
        var mounts = new AdMounts(env, log, _root);
        var id = Guid.NewGuid();
        var lines = new List<string>();

        var prepared = mounts.Prepare(id, 2L << 30, lines.Add);
        Assert.Equal(Path.Combine(env.WorkRoot, id.ToString("D")), prepared.Folder);
        Assert.True(Directory.Exists(prepared.Folder));
        Assert.True(File.Exists(Path.Combine(_root, "ad-mounts.json")));

        // Without the transferred database the mount stops before dsamain.
        var missing = Assert.Throws<InvalidOperationException>(() => mounts.Mount(id, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), lines.Add, Ct));
        Assert.Contains("ntds.dit is missing on dc1.bdfu-lab.local", missing.Message, StringComparison.Ordinal);
        Assert.Empty(env.Started);

        File.WriteAllText(Path.Combine(prepared.Folder, "ntds.dit"), "database");
        var info = mounts.Mount(id, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), lines.Add, Ct);
        Assert.Equal(50000, info.Port);
        Assert.Equal(Domain, info.BackupDomainDn);
        Assert.Equal(Domain, info.LiveDomainDn);
        Assert.Equal("dc1.bdfu-lab.local", info.LiveHost);
        Assert.Equal($"{Path.Combine(prepared.Folder, "ntds.dit")}|{prepared.Folder}|50000", Assert.Single(env.Started));
        Assert.NotNull(mounts.Active);
        Assert.Contains(lines, l => l.Contains("answers on dc1.bdfu-lab.local:50000", StringComparison.Ordinal));
        Assert.Equal(info.Port, mounts.Use(id).Info.Port);

        mounts.Unmount(id, "the explore session ended");
        Assert.Equal([4242], env.Stopped);
        Assert.False(Directory.Exists(prepared.Folder));
        Assert.False(File.Exists(Path.Combine(_root, "ad-mounts.json")));
        Assert.Null(mounts.Active);
        Assert.Throws<AdNotMountedException>(() => mounts.Use(id));
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_root, "agent.log")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_mount_fails_clearly_and_keeps_nothing_running()
    {
        var env = new FakeAdEnvironment(_root, _backup, _live) { Free = 1L << 30 };
        var mounts = new AdMounts(env, new AgentLog(Path.Combine(_root, "agent.log")), _root);
        var id = Guid.NewGuid();

        // Not enough space: the folder is gone again.
        var space = Assert.Throws<InvalidOperationException>(() => mounts.Prepare(id, 2L << 30, _ => { }));
        Assert.Contains("Not enough free space for the backup's directory on dc1.bdfu-lab.local", space.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(env.WorkRoot, id.ToString("D"))));

        // dsamain ends at once: its output is in the error, together with the recovery's.
        env.Free = 100L << 30;
        env.ServerFails = "Database version mismatch.";
        env.Recovery = new AdToolResult { ExitCode = -1, Output = "Operation terminated with error -1216." };
        var folder = mounts.Prepare(id, 1L << 30, _ => { }).Folder;
        File.WriteAllText(Path.Combine(folder, "ntds.dit"), "database");
        var failed = Assert.Throws<InvalidOperationException>(() => mounts.Mount(id, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct));
        Assert.Contains("dsamain on dc1.bdfu-lab.local (port 50000) ended with 1 before it served the backup: Database version mismatch.", failed.Message, StringComparison.Ordinal);
        Assert.Contains("error -1216", failed.Message, StringComparison.Ordinal);

        // A wrong password is named as such.
        env.ServerFails = null;
        mounts.Unmount(id, "test");
        folder = mounts.Prepare(id, 1L << 30, _ => { }).Folder;
        File.WriteAllText(Path.Combine(folder, "ntds.dit"), "database");
        var logon = Assert.Throws<InvalidOperationException>(() => mounts.Mount(id, new AdAccount { UserName = "BDFU-LAB\\x", Password = "wrong" }, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct));
        Assert.Contains("The account BDFU-LAB\\x cannot log on to the backup's directory on dc1.bdfu-lab.local:50000", logon.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_backup_of_another_domain_is_refused()
    {
        var other = new InMemoryDirectory("dc1.bdfu-lab.local:50000", "DC=other,DC=local");
        var env = new FakeAdEnvironment(_root, other, _live);
        var mounts = new AdMounts(env, new AgentLog(Path.Combine(_root, "agent.log")), _root);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(mounts.Prepare(id, 1, _ => { }).Folder, "ntds.dit"), "database");

        var refused = Assert.Throws<InvalidOperationException>(() => mounts.Mount(id, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct));

        Assert.Contains("The backup is of domain DC=other,DC=local, but dc1.bdfu-lab.local is a domain controller of DC=bdfu-lab,DC=local", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void After_an_agent_restart_and_after_the_idle_time_nothing_is_left()
    {
        var env = new FakeAdEnvironment(_root, _backup, _live);
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var log = new AgentLog(Path.Combine(_root, "agent.log"));
        var mounts = new AdMounts(env, log, _root, () => now);
        var first = Guid.NewGuid();
        var folder = mounts.Prepare(first, 1, _ => { }).Folder;
        File.WriteAllText(Path.Combine(folder, "ntds.dit"), "database");
        var info = mounts.Mount(first, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct);
        var stray = Path.Combine(env.WorkRoot, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(stray);

        // The agent restarts: the new instance stops the old dsamain and deletes both folders.
        var restarted = new AdMounts(env, log, _root, () => now);
        restarted.SweepAtStart();
        Assert.Contains(info.ProcessId, env.Stopped);
        Assert.False(Directory.Exists(folder));
        Assert.False(Directory.Exists(stray));
        Assert.False(File.Exists(Path.Combine(_root, "ad-mounts.json")));

        // A session that nobody uses goes away after its idle time; one in use stays.
        var idle = Guid.NewGuid();
        var used = Guid.NewGuid();
        foreach (var id in new[] { idle, used })
        {
            File.WriteAllText(Path.Combine(restarted.Prepare(id, 1, _ => { }).Folder, "ntds.dit"), "database");
            restarted.Mount(id, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct);
        }

        now = now.AddMinutes(45);
        restarted.Use(used);
        now = now.AddMinutes(30);
        restarted.SweepIdle();
        Assert.Throws<AdNotMountedException>(() => restarted.Use(idle));
        Assert.Equal(Domain, restarted.Use(used).Info.BackupDomainDn);
        Assert.Contains("unused for 60 minutes", File.ReadAllText(Path.Combine(_root, "agent.log")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ad_job_fetches_its_request_and_posts_the_result_without_the_password()
    {
        var env = new FakeAdEnvironment(_root, _backup, _live);
        var log = new AgentLog(Path.Combine(_root, "agent.log"));
        var mounts = new AdMounts(env, log, _root);
        var mountId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(mounts.Prepare(mountId, 1, _ => { }).Folder, "ntds.dit"), "database");
        mounts.Mount(mountId, Admin, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, Ct);
        var server = new JobServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://backup.test/") };

        var jobId = Guid.NewGuid();
        server.Requests[jobId] = new AdJobRequest { JobId = jobId, Kind = AdJobKinds.Search, MountId = mountId, Account = Admin, States = [AdObjectStates.Gone] };
        await new AdJobRunner(http, jobId, mounts, log).RunAsync(Ct);

        var result = server.Results[jobId];
        Assert.True(result.Success, result.Error);
        Assert.Equal("lab-user05", Assert.Single(result.Entries).Name);
        Assert.True(result.Scanned >= 5);

        // A query of a session that is not mounted fails with the reason.
        var other = Guid.NewGuid();
        server.Requests[other] = new AdJobRequest { JobId = other, Kind = AdJobKinds.Tree, MountId = Guid.NewGuid(), Account = Admin };
        await new AdJobRunner(http, other, mounts, log).RunAsync(Ct);
        Assert.False(server.Results[other].Success);
        Assert.Contains("is not mounted on dc1.bdfu-lab.local", server.Results[other].Error, StringComparison.Ordinal);

        // Unmount through a job.
        var unmount = Guid.NewGuid();
        server.Requests[unmount] = new AdJobRequest { JobId = unmount, Kind = AdJobKinds.Unmount, MountId = mountId };
        await new AdJobRunner(http, unmount, mounts, log).RunAsync(Ct);
        Assert.True(server.Results[unmount].Success);
        Assert.Null(mounts.Active);
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_root, "agent.log")), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", string.Join("\n", server.Results.Values.SelectMany(r => r.Log)), StringComparison.Ordinal);
    }

    private void Seed()
    {
        Guid Id(string name) => _ids[name] = Guid.NewGuid();
        object[] Classes(params string[] classes) => ["top", .. classes];
        byte[] Sid(uint rid) => [1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0, .. BitConverter.GetBytes(1004336348u), .. BitConverter.GetBytes(1177238915u), .. BitConverter.GetBytes(682003330u), .. BitConverter.GetBytes(rid)];

        foreach (var directory in new[] { _backup, _live })
        {
            var live = directory == _live;
            var lab = live ? "OU=Lab2," + Domain : "OU=Lab," + Domain;
            directory.Add(Domain, _ids.TryGetValue("domain", out var d) ? d : Id("domain"), ("objectClass", Classes("domain", "domainDNS")));
            directory.Add(lab, _ids.TryGetValue("Lab", out var l) ? l : Id("Lab"), ("objectClass", Classes("organizationalUnit")), ("whenChanged", [live ? "20261107100000.0Z" : "20261101100000.0Z"]));
            directory.Add("OU=Users," + lab, _ids.TryGetValue("Users", out var u) ? u : Id("Users"), ("objectClass", Classes("organizationalUnit")));
            directory.Add("OU=Groups," + lab, _ids.TryGetValue("Groups", out var g) ? g : Id("Groups"), ("objectClass", Classes("organizationalUnit")));
            if (live)
            {
                directory.Add("CN=Deleted Objects," + Domain, _ids.TryGetValue("Deleted Objects", out var x) ? x : Id("Deleted Objects"), ("objectClass", Classes("container")));
            }
        }

        AdRecord User(InMemoryDirectory directory, string name, uint rid, string description, string? dn = null, params (string, object[])[] more)
        {
            var guid = _ids.TryGetValue(name, out var existing) ? existing : Id(name);
            var lab = directory == _live ? "OU=Lab2," : "OU=Lab,";
            var attributes = new List<(string, object[])>
            {
                ("objectClass", Classes("person", "organizationalPerson", "user")),
                ("objectCategory", ["person"]),
                ("sAMAccountName", [name]),
                ("description", [description]),
                ("objectSid", [Sid(rid)]),
                ("pwdLastSet", ["133000000000000000"]),
                ("userAccountControl", ["512"]),
                ("whenChanged", ["20261101100000.0Z"]),
            };
            attributes.AddRange(more);
            return directory.Add(dn ?? $"CN={name},OU=Users,{lab}{Domain}", guid, [.. attributes]);
        }

        var groupBackup = _backup.Add("CN=LabGroupA,OU=Groups,OU=Lab," + Domain, Id("LabGroupA"), ("objectClass", Classes("group")), ("sAMAccountName", ["LabGroupA"]));
        var user01 = User(_backup, "lab-user01", 1101, "Boss");
        User(_backup, "lab-user02", 1102, "Sales", null, ("manager", [InMemoryDirectory.Link(user01)]), ("memberOf", [InMemoryDirectory.Link(groupBackup)]));
        groupBackup.Attributes["member"] = [InMemoryDirectory.Link(_backup.Records.Single(r => r.Dn.StartsWith("CN=lab-user02", StringComparison.Ordinal)))];
        User(_backup, "lab-user03", 1103, "To be deleted");
        User(_backup, "lab-user04", 1104, "To be recycled");
        User(_backup, "lab-user05", 1105, "Gone for good");

        var liveUser01 = User(_live, "lab-user01", 1101, "Boss", null, ("whenChanged", ["20261107100000.0Z"]));
        _live.Add("CN=LabGroupA,OU=Groups,OU=Lab2," + Domain, _ids["LabGroupA"], ("objectClass", Classes("group")), ("sAMAccountName", ["LabGroupA"]));
        User(_live, "lab-user02", 1102, "Marketing", null, ("manager", [InMemoryDirectory.Link(liveUser01)]), ("whenChanged", ["20261107110000.0Z"]));

        // lab-user03: a tombstone (no Recycle Bin) kept only some attributes; lab-user04 was recycled.
        _live.Add($"CN=lab-user03\\0ADEL:{_ids["lab-user03"]:D},CN=Deleted Objects," + Domain, _ids["lab-user03"],
            ("objectClass", Classes("person", "organizationalPerson", "user")), ("isDeleted", ["TRUE"]), ("sAMAccountName", ["lab-user03"]),
            ("objectSid", [Sid(1103)]), ("lastKnownParent", [$"<GUID={_ids["Users"]:D}>;OU=Users,OU=Lab2," + Domain]));
        _live.Add($"CN=lab-user04\\0ADEL:{_ids["lab-user04"]:D},CN=Deleted Objects," + Domain, _ids["lab-user04"],
            ("objectClass", Classes("person", "organizationalPerson", "user")), ("isDeleted", ["TRUE"]), ("isRecycled", ["TRUE"]));
    }

    private sealed class JobServer : HttpMessageHandler
    {
        public Dictionary<Guid, AdJobRequest> Requests { get; } = [];

        public Dictionary<Guid, AdJobResult> Results { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Assert.StartsWith(AgentProtocol.AdJobsPath + "/", path, StringComparison.Ordinal);
            var parts = path[(AgentProtocol.AdJobsPath.Length + 1)..].Split('/');
            var id = Guid.Parse(parts[0]);
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Requests[id], AgentJson.Options), Encoding.UTF8, "application/json") };
            }

            Assert.Equal("result", parts[1]);
            Results[id] = JsonSerializer.Deserialize<AdJobResult>(await request.Content!.ReadAsStringAsync(cancellationToken), AgentJson.Options)!;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
