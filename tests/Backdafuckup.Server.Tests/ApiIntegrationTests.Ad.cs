using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Agent.Tests;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

public sealed partial class ApiIntegrationTests
{
    private const string AdDomain = "DC=bdfu-lab,DC=local";

    [Fact]
    public async Task An_ad_explore_copies_the_directory_to_the_dc_mounts_it_compares_and_removes_it_again()
    {
        var client = await SignedInClientAsync();
        var dit = Pattern(3 << 20, 0x5a);
        var log1 = Pattern(1 << 20, 0x11);
        var image = WindowsTestDisk.Create(
        [
            new WindowsTestDisk.Volume('C', 24, new Dictionary<string, byte[]>
            {
                ["Windows/NTDS/ntds.dit"] = dit,
                ["Windows/NTDS/edb.chk"] = Pattern(8192, 0x22),
                ["Windows/NTDS/edb.log"] = log1,
                ["Windows/NTDS/edb00001.log"] = Pattern(1 << 20, 0x33),
                ["Windows/NTDS/temp.edb"] = Pattern(65536, 0x44),
                ["Windows/NTDS/edbres00001.jrs"] = Pattern(65536, 0x55),
            }),
        ]);
        var sandbox = Path.Combine(_root, "dc1-sandbox");
        var (backup, live) = AdDirectories();
        var env = new FakeAdEnvironment(sandbox, backup, live, windowsSandbox: true);
        (Backdafuckup.Server.Data.AgentEntity Agent, CancellationTokenSource Stop, Task Running)? dc = null;
        var point = await BackUpImageAsync(client, "dc1", image, async vm => dc = await StartAgentAsync(vm, new SandboxRestoreFileSystem(sandbox), ad: env));
        Assert.True(point.DomainController);
        Assert.Equal("bdfu-lab.local", point.AdDomain);
        Assert.Equal("application", point.Consistency);

        // The DC is a target; a Windows account of the domain opens the explore session.
        var target = Assert.Single((await client.GetFromJsonAsync<List<AdTargetDto>>($"/api/ad/restore-points/{point.Id}/targets", Ct))!);
        Assert.True(target.Usable, target.Reason);
        Assert.Equal("bdfu-lab.local", target.Domain);
        Assert.True(target.BackupSource);
        var account = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"BDFU-LAB\Administrator", "secret", null), Ct));
        var opened = await Read<AdExploreDto>(client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, target.AgentId, account.Id), Ct));
        var explore = await WaitForExploreAsync(client, opened.Id);
        Assert.True(explore.State == AdExploreStates.Ready, explore.Error);
        Assert.Equal(AdDomain, explore.Mount!.DomainDn);
        Assert.Equal("dc1.bdfu-lab.local", explore.Mount.LiveHost);
        Assert.Equal(explore.TotalBytes, explore.TransferredBytes);

        // Opening again returns the same session.
        Assert.Equal(opened.Id, (await Read<AdExploreDto>(client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, target.AgentId, account.Id), Ct))).Id);

        // The database and its logs are in the protected work folder (not temp.edb or the reserve logs), as in the backup.
        var folder = Path.Combine(sandbox, "C", "ProgramData", "backdafuckup", "ad", explore.Id.ToString("D"));
        Assert.Equal(["edb.chk", "edb.log", "edb00001.log", "ntds.dit"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(dit, await File.ReadAllBytesAsync(Path.Combine(folder, "ntds.dit"), Ct));
        Assert.Equal(log1, await File.ReadAllBytesAsync(Path.Combine(folder, "edb.log"), Ct));
        Assert.Equal(@$"C:\ProgramData\backdafuckup\ad\{explore.Id:D}\ntds.dit|C:\ProgramData\backdafuckup\ad\{explore.Id:D}|50000", Assert.Single(env.Started));

        // Tree, search and one object, compared with the live directory.
        var tree = (await client.GetFromJsonAsync<List<AdEntryDto>>($"/api/ad/explores/{explore.Id}/tree", Ct))!;
        Assert.Equal("Lab", Assert.Single(tree).Name);
        var gone = await Read<AdSearchPageDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/search", new AdSearchDto(States: [AdObjectStates.Deleted, AdObjectStates.Gone]), Ct));
        Assert.Equal(["lab-user02", "lab-user03"], gone.Entries.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Null(gone.Cookie);
        var deleted = gone.Entries.Single(e => e.Name == "lab-user02");
        Assert.Equal(AdObjectStates.Deleted, deleted.State);
        var view = await client.GetFromJsonAsync<AdObjectDto>($"/api/ad/explores/{explore.Id}/objects/{deleted.Guid}", Ct);
        Assert.Equal(AdObjectStates.Deleted, view!.State);
        Assert.Equal("OU=Lab," + AdDomain, view.LiveParentDn);
        Assert.Contains(view.Attributes, a => a.Name == "description" && a.Backup.Single().Text == "Sales" && a.Live.Count == 0);
        var missing = await client.GetAsync($"/api/ad/explores/{explore.Id}/objects/{Guid.NewGuid()}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);

        // The preview writes nothing; the restore needs the live domain as confirmation; the password is never logged.
        var gonePoint = gone.Entries.Single(e => e.Name == "lab-user03");
        var objects = new List<AdRestoreObjectDto> { new(deleted.Guid), new(gonePoint.Guid) };
        var plan = await Read<AdRestorePlanDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/restore/plan", new AdRestoreRequestDto(objects, Recreate: true), Ct));
        Assert.Equal("bdfu-lab.local", plan.LiveDomain);
        Assert.Equal(["reanimate", "recreate"], plan.Items.OrderBy(i => i.Dn, StringComparer.Ordinal).Select(i => i.Action));
        Assert.All(plan.Items, i => Assert.Equal("planned", i.Outcome));
        Assert.Empty(live.Writes);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/restore", new AdRestoreRequestDto(objects, Recreate: true, NewPassword: "N3w-Passw0rd!", ConfirmDomain: "other.local"), Ct)).StatusCode);
        Assert.Empty(live.Writes);
        var restore = await Read<StartedDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/restore",
            new AdRestoreRequestDto(objects, Recreate: true, NewPassword: "N3w-Passw0rd!", ConfirmDomain: "BDFU-LAB.local"), Ct));
        var restored = await WaitForSessionAsync(client, restore.SessionId);
        Assert.Equal("Warning", restored.Session.State); // the re-created account has a new SID
        Assert.Equal("AdRestore", restored.Session.Kind);
        Assert.Equal(2, restored.Tasks.Count);
        Assert.Contains("2 of 2 object(s) restored into DC1 (1 with warnings)", restored.Session.Summary, StringComparison.Ordinal);
        Assert.False(live.Find(deleted.Guid)!.Flag("isDeleted"));
        Assert.Equal("CN=lab-user02,OU=Lab," + AdDomain, live.Find(deleted.Guid)!.Dn);
        Assert.Equal("Sales", live.Find(deleted.Guid)!.Text("description"));
        Assert.Equal(Backdafuckup.Agent.Core.AdRestorer.PasswordValue("N3w-Passw0rd!"), live.Find("CN=lab-user03,OU=Lab," + AdDomain, false)!.Bytes("unicodePwd"));
        var restoreLog = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{restore.SessionId}/log", Ct))!.Select(l => l.Message).ToList();
        Assert.Contains(restoreLog, m => m.Contains(@"written as BDFU-LAB\Administrator", StringComparison.Ordinal));
        Assert.Contains(restoreLog, m => m.StartsWith($"CN=lab-user02,OU=Lab,{AdDomain} (user, was deleted): reanimated as CN=lab-user02,OU=Lab,{AdDomain}", StringComparison.Ordinal));
        Assert.Contains(restoreLog, m => m.Contains("re-created as CN=lab-user03,OU=Lab," + AdDomain, StringComparison.Ordinal) && m.Contains("new objectGUID", StringComparison.Ordinal));
        Assert.Contains(restoreLog, m => m.StartsWith("reanimate CN=lab-user02", StringComparison.Ordinal));
        Assert.DoesNotContain(restoreLog, m => m.Contains("N3w-Passw0rd!", StringComparison.Ordinal));
        Assert.DoesNotContain("N3w-Passw0rd!", restored.Session.Summary ?? string.Empty, StringComparison.Ordinal);

        // Closing removes the copy from the DC and ends the session.
        await Ok(client.DeleteAsync($"/api/ad/explores/{explore.Id}", Ct));
        Assert.False(Directory.Exists(folder));
        Assert.Equal([4242], env.Stopped);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/ad/explores/{explore.Id}", Ct)).StatusCode);
        var session = await WaitForSessionAsync(client, explore.SessionId);
        Assert.Equal("Succeeded", session.Session.State);
        Assert.Equal("AdExplore", session.Session.Kind);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{explore.SessionId}/log", Ct))!.Select(l => l.Message).ToList();
        Assert.Contains(log, m => m.StartsWith(@"Opened by admin: the Active Directory database of dc1 (bdfu-lab.local)", StringComparison.Ordinal) && m.Contains(@"as BDFU-LAB\Administrator", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains(@"Directory database C:\Windows\NTDS\ntds.dit, logs in C:\Windows\NTDS (from the guest metadata)", StringComparison.Ordinal));
        Assert.Contains(log, m => m.StartsWith("Copied 4 file(s)", StringComparison.Ordinal) && m.Contains("each checked with SHA-256", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("answers on dc1.bdfu-lab.local:50000", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains($"admin viewed CN=lab-user02,OU=Lab,{AdDomain} (user, deleted", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("The backup's directory was removed from DC1", StringComparison.Ordinal));
        Assert.DoesNotContain(log, m => m.Contains("secret", StringComparison.Ordinal));
        var agentLog = await File.ReadAllTextAsync(Path.Combine(Directory.GetDirectories(_root, "agent-*").Single(), AgentProtocol.LogFile), Ct);
        Assert.DoesNotContain("secret", agentLog, StringComparison.Ordinal);
        Assert.DoesNotContain("N3w-Passw0rd!", agentLog, StringComparison.Ordinal);
        await using (var db = await _factory!.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct))
        {
            Assert.DoesNotContain("N3w-Passw0rd!", (await db.Sessions.FindAsync([restore.SessionId], Ct))!.Parameters, StringComparison.Ordinal);
        }

        await dc!.Value.Stop.CancelAsync();
        await dc.Value.Running;
    }

    [Fact]
    public async Task An_ad_explore_takes_the_ifm_copy_of_the_restore_point_and_the_dc_deletes_it_after_the_snapshot()
    {
        var client = await SignedInClientAsync();
        var ifmDit = Pattern(2 << 20, 0x77);
        var image = WindowsTestDisk.Create(
        [
            new WindowsTestDisk.Volume('C', 16, new Dictionary<string, byte[]>
            {
                ["Windows/NTDS/ntds.dit"] = Pattern(1 << 20, 0x5a),
                ["Windows/NTDS/edb.log"] = Pattern(1 << 20, 0x11),
                ["ProgramData/backdafuckup/ad-ifm/Active Directory/ntds.dit"] = ifmDit,
                ["ProgramData/backdafuckup/ad-ifm/registry/SYSTEM"] = Pattern(65536, 0x33),
            }),
        ]);
        var sandbox = Path.Combine(_root, "dc5-sandbox");
        var (backup, live) = AdDirectories();
        var env = new FakeAdEnvironment(sandbox, backup, live, windowsSandbox: true);
        (Backdafuckup.Server.Data.AgentEntity Agent, CancellationTokenSource Stop, Task Running)? dc = null;
        var point = await BackUpImageAsync(client, "dc5", image, async vm => dc = await StartAgentAsync(vm, new SandboxRestoreFileSystem(sandbox), ad: env, ifm: true));
        var vss = _lastVss!;
        for (var i = 0; i < 50 && !vss.Calls.Contains("cleanup"); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Contains("cleanup", vss.Calls);
        Assert.True(vss.Calls.IndexOf("cleanup") > vss.Calls.IndexOf("freeze"));
        var account = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"BDFU-LAB\Administrator", "secret", null), Ct));

        var opened = await Read<AdExploreDto>(client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, dc!.Value.Agent.Id, account.Id), Ct));
        var explore = await WaitForExploreAsync(client, opened.Id);

        Assert.True(explore.State == AdExploreStates.Ready, explore.Error);
        var folder = Path.Combine(sandbox, "C", "ProgramData", "backdafuckup", "ad", explore.Id.ToString("D"));
        Assert.Equal(["ntds.dit"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(ifmDit, await File.ReadAllBytesAsync(Path.Combine(folder, "ntds.dit"), Ct));

        // The agent restarts: it removes the copy at its start, and the session ends at once (not at the next query).
        await dc.Value.Stop.CancelAsync();
        await dc.Value.Running;
        dc = await StartAgentAsync(point.VmNativeId, new SandboxRestoreFileSystem(sandbox), ad: env, directory: Directory.GetDirectories(_root, "agent-*").Single());
        var ended = await WaitForSessionAsync(client, explore.SessionId);
        Assert.Equal("Failed", ended.Session.State);
        Assert.False(Directory.Exists(folder));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/ad/explores/{explore.Id}", Ct)).StatusCode);
        var restarted = "restarted (service restart, crash or VM reboot) and removed the backup's directory at its start";
        var log = new List<string>();
        for (var i = 0; i < 50 && !log.Any(m => m.Contains(restarted, StringComparison.Ordinal)); i++)
        {
            // The log is flushed when the session has been closed in the background.
            await Task.Delay(100, Ct);
            log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{explore.SessionId}/log", Ct))!.Select(l => l.Message).ToList();
        }

        Assert.True(log.Any(m => m.StartsWith("The agent on DC1 (10.0.0.10) restarted", StringComparison.Ordinal) && m.Contains(restarted, StringComparison.Ordinal)), string.Join("\n", log));
        Assert.Contains(log, m => m.Contains(@"Directory database C:\ProgramData\backdafuckup\ad-ifm\Active Directory\ntds.dit: the consistent copy made with ntdsutil IFM", StringComparison.Ordinal));
        Assert.DoesNotContain(log, m => m.Contains("crash-consistent", StringComparison.Ordinal));
        await dc.Value.Stop.CancelAsync();
        await dc.Value.Running;
    }

    [Fact]
    public async Task An_ad_explore_is_refused_for_another_domain_and_a_failed_open_leaves_nothing_on_the_dc()
    {
        var client = await SignedInClientAsync();
        var image = WindowsTestDisk.Create([new WindowsTestDisk.Volume('C', 16, new Dictionary<string, byte[]> { ["Windows/NTDS/ntds.dit"] = Pattern(1 << 20, 0x5a) })]);
        var sandbox = Path.Combine(_root, "dc2-sandbox");
        var (backup, live) = AdDirectories();
        var env = new FakeAdEnvironment(sandbox, backup, live, windowsSandbox: true) { ServerFails = "Database version mismatch." };
        (Backdafuckup.Server.Data.AgentEntity Agent, CancellationTokenSource Stop, Task Running)? dc = null;
        var point = await BackUpImageAsync(client, "dc2", image, async vm => dc = await StartAgentAsync(vm, new SandboxRestoreFileSystem(sandbox), ad: env));
        var account = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"BDFU-LAB\Administrator", "secret", null), Ct));

        // dsamain cannot open the database: the explore fails with its output, and the work folder is gone again.
        var opened = await Read<AdExploreDto>(client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, dc!.Value.Agent.Id, account.Id), Ct));
        var failed = await WaitForExploreAsync(client, opened.Id);
        Assert.Equal(AdExploreStates.Failed, failed.State);
        Assert.Contains("ended with 1 before it served the backup: Database version mismatch.", failed.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(sandbox, "C", "ProgramData", "backdafuckup", "ad", opened.Id.ToString("D"))));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/ad/explores/{opened.Id}/search", new AdSearchDto(), Ct)).StatusCode);
        await Ok(client.DeleteAsync($"/api/ad/explores/{opened.Id}", Ct));
        Assert.Equal("Failed", (await WaitForSessionAsync(client, opened.SessionId)).Session.State);

        // A restore point of another domain is refused for this DC.
        await using (var db = await _factory!.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var entity = await db.RestorePoints.FindAsync([point.Id], Ct);
            entity!.AdDomain = "other.local";
            await db.SaveChangesAsync(Ct);
        }

        var target = Assert.Single((await client.GetFromJsonAsync<List<AdTargetDto>>($"/api/ad/restore-points/{point.Id}/targets", Ct))!);
        Assert.False(target.Usable);
        Assert.Contains("it is a domain controller of bdfu-lab.local, the restore point is of other.local", target.Reason, StringComparison.Ordinal);
        var refused = await client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, dc.Value.Agent.Id, account.Id), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await dc.Value.Stop.CancelAsync();
        await dc.Value.Running;
    }

    [Fact]
    public async Task An_ad_restore_brings_a_group_policy_back_with_its_files_container_version_and_link()
    {
        const string gpo = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";
        var client = await SignedInClientAsync();
        var image = WindowsTestDisk.Create(
        [
            new WindowsTestDisk.Volume('C', 16, new Dictionary<string, byte[]>
            {
                ["Windows/NTDS/ntds.dit"] = Pattern(1 << 20, 0x5a),
                [$"Windows/SYSVOL/domain/Policies/{gpo}/GPT.INI"] = "[General]\r\nVersion=131075\r\ndisplayName=Lab Policy A\r\n"u8.ToArray(),
                [$"Windows/SYSVOL/domain/Policies/{gpo}/Machine/registry.pol"] = "backup settings"u8.ToArray(),
                [$"Windows/SYSVOL/domain/Policies/{gpo}/User/Scripts/logon.cmd"] = "echo backup"u8.ToArray(),
            }),
        ]);
        var sandbox = Path.Combine(_root, "dc3-sandbox");
        var live = Path.Combine(sandbox, "C", "Windows", "SYSVOL", "sysvol", "bdfu-lab.local", "Policies", gpo);
        Directory.CreateDirectory(Path.Combine(live, "Machine"));
        await File.WriteAllTextAsync(Path.Combine(live, "GPT.INI"), "[General]\r\nVersion=327681\r\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(live, "Machine", "registry.pol"), "live settings", Ct);
        var (backup, liveDirectory) = GpoDirectories(gpo);
        var env = new FakeAdEnvironment(sandbox, backup, liveDirectory, windowsSandbox: true);
        var fs = new SandboxRestoreFileSystem(sandbox) { RecordPermissions = true };
        (Backdafuckup.Server.Data.AgentEntity Agent, CancellationTokenSource Stop, Task Running)? dc = null;
        var point = await BackUpImageAsync(client, "dc3", image, async vm => dc = await StartAgentAsync(vm, fs, ad: env));
        var account = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"BDFU-LAB\Administrator", "secret", null), Ct));
        var opened = await Read<AdExploreDto>(client.PostAsJsonAsync("/api/ad/explores", new OpenAdExploreDto(point.Id, dc!.Value.Agent.Id, account.Id), Ct));
        var explore = await WaitForExploreAsync(client, opened.Id);
        Assert.True(explore.State == AdExploreStates.Ready, explore.Error);

        var found = await Read<AdSearchPageDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/search", new AdSearchDto(Classes: ["groupPolicyContainer"]), Ct));
        var policy = Assert.Single(found.Entries);
        Assert.Equal(AdObjectStates.Changed, policy.State);
        var request = new AdRestoreRequestDto([], Gpos: [new AdGpoRestoreDto(policy.Guid)], ConfirmDomain: "bdfu-lab.local");
        var plan = await Read<AdRestorePlanDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/restore/plan", request, Ct));
        var planned = Assert.Single(plan.Items);
        Assert.Equal("gpo", planned.Action);
        Assert.Contains(planned.Steps, s => s.Operation == "link" && s.Target == "OU=Lab," + AdDomain);
        Assert.Equal("live settings", await File.ReadAllTextAsync(Path.Combine(live, "Machine", "registry.pol"), Ct));

        var restore = await Read<StartedDto>(client.PostAsJsonAsync($"/api/ad/explores/{explore.Id}/restore", request, Ct));
        var session = await WaitForSessionAsync(client, restore.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{restore.SessionId}/log", Ct))!.Select(l => l.Message).ToList();
        Assert.True(session.Session.State == "Succeeded", string.Join("\n", log));

        // The files of the backup with a higher version; nothing of the staging or the previous files is left.
        Assert.Equal("backup settings", await File.ReadAllTextAsync(Path.Combine(live, "Machine", "registry.pol"), Ct));
        Assert.Equal("echo backup", await File.ReadAllTextAsync(Path.Combine(live, "User", "Scripts", "logon.cmd"), Ct));
        var version = (6 << 16) | 4;
        Assert.Contains($"Version={version}", await File.ReadAllTextAsync(Path.Combine(live, "GPT.INI"), Ct), StringComparison.Ordinal);
        Assert.Equal([gpo], Directory.GetDirectories(Path.GetDirectoryName(live)!).Select(Path.GetFileName));
        Assert.Contains(fs.Secured, p => p.EndsWith(@"\Machine\registry.pol", StringComparison.Ordinal));

        var container = liveDirectory.Find(policy.Guid)!;
        Assert.Equal("Lab Policy A", container.Text("displayName"));
        Assert.Equal(version.ToString(System.Globalization.CultureInfo.InvariantCulture), container.Text("versionNumber"));
        Assert.Contains(gpo, liveDirectory.Find("OU=Lab," + AdDomain, false)!.Text("gPLink"), StringComparison.Ordinal);
        Assert.Contains(log, m => m.StartsWith($"Copied the files of the group policy Lab Policy A (3 file(s)", StringComparison.Ordinal) && m.Contains("with their permissions", StringComparison.Ordinal));
        Assert.Contains(log, m => m.Contains("group policy restored (files, container, version, links)", StringComparison.Ordinal));

        await Ok(client.DeleteAsync($"/api/ad/explores/{explore.Id}", Ct));
        await dc.Value.Stop.CancelAsync();
        await dc.Value.Running;
    }

    /// <summary>A policy and its link: changed live (display name, version) and its link on OU=Lab removed.</summary>
    private static (InMemoryDirectory Backup, InMemoryDirectory Live) GpoDirectories(string gpo)
    {
        var backup = new InMemoryDirectory("dc1.bdfu-lab.local:50000", AdDomain);
        var live = new InMemoryDirectory("dc1.bdfu-lab.local:389", AdDomain);
        var domain = Guid.NewGuid();
        var lab = Guid.NewGuid();
        foreach (var directory in new[] { backup, live })
        {
            directory.Add(AdDomain, domain, ("objectClass", ["top", "domain", "domainDNS"]));
            directory.Add("OU=Lab," + AdDomain, lab, ("objectClass", ["top", "organizationalUnit"]));
        }

        AddGpo(backup, live, gpo, "Lab Policy A");
        return (backup, live);
    }

    /// <summary>Adds a policy (changed live, its link on OU=Lab only in the backup) and the schema attributes it needs.</summary>
    private static void AddGpo(InMemoryDirectory backup, InMemoryDirectory live, string gpo, string name)
    {
        var policies = "CN=Policies,CN=System," + AdDomain;
        var ids = new Dictionary<string, Guid>();
        Guid Id(string key) => ids.TryGetValue(key, out var id) ? id : ids[key] = Guid.NewGuid();
        foreach (var directory in new[] { backup, live })
        {
            var isLive = directory == live;
            if (directory.Find("CN=System," + AdDomain, false) is null)
            {
                directory.Add("CN=System," + AdDomain, Id("System"), ("objectClass", ["top", "container"]));
                directory.Add(policies, Id("Policies"), ("objectClass", ["top", "container"]));
            }

            directory.Find("OU=Lab," + AdDomain, false)!.Attributes["gPLink"] = [isLive ? string.Empty : $"[LDAP://cn={gpo},cn=policies,cn=system,{AdDomain};0]"];
            var gpc = directory.Add($"CN={gpo},{policies}", Id(gpo), ("objectClass", ["top", "container", "groupPolicyContainer"]), ("cn", [gpo]),
                ("displayName", [isLive ? name + " (changed)" : name]), ("versionNumber", [isLive ? "327681" : "131075"]));
            directory.Add($"CN=User,{gpc.Dn}", Id("User"), ("objectClass", ["top", "container"]));
            directory.Add($"CN=Machine,{gpc.Dn}", Id("Machine"), ("objectClass", ["top", "container"]));
        }

        foreach (var (attribute, syntax) in new[] { ("displayName", "2.5.5.12"), ("versionNumber", "2.5.5.9"), ("gPLink", "2.5.5.12"), ("objectClass", "2.5.5.2") })
        {
            if (live.Find($"CN={attribute},{live.SchemaDn}", false) is null)
            {
                live.Add($"CN={attribute},{live.SchemaDn}", Guid.NewGuid(), ("objectClass", ["top", "attributeSchema"]), ("lDAPDisplayName", [attribute]), ("attributeSyntax", [syntax]));
            }
        }
    }

    /// <summary>
    /// Not a test: with BDFU_UI_FIXTURE=&lt;folder&gt; it leaves a repository with a domain controller's restore point
    /// (ntds.dit, logs and a group policy in SYSVOL) for checking the AD pages by hand, with <see cref="Ui_dc_agent_for_a_running_server"/>.
    /// </summary>
    [Fact]
    public async Task Ui_fixture_for_active_directory()
    {
        var keep = Environment.GetEnvironmentVariable("BDFU_UI_FIXTURE");
        Assert.SkipUnless(keep is { Length: > 0 }, "Only builds a repository for checking the console (BDFU_UI_FIXTURE=<folder>).");
        var client = await SignedInClientAsync();
        var sandbox = Path.Combine(_root, "ui-dc");
        var (backup, live) = AdDirectories();
        var point = await BackUpImageAsync(client, "dc1", UiDcImage(), async vm => await StartAgentAsync(vm, new SandboxRestoreFileSystem(sandbox), ad: new FakeAdEnvironment(sandbox, backup, live, windowsSandbox: true)));
        Assert.True(point.DomainController);
        CopyDirectory(Path.Combine(_root, "repo"), keep!);
    }

    /// <summary>
    /// Not a test: with BDFU_UI_SERVER=http://localhost:9556/ and BDFU_UI_SECRET it runs the real agent as a domain controller of
    /// bdfu-lab.local (in-memory directories, files in a sandbox) against that server until BDFU_UI_STOP (a file) exists.
    /// </summary>
    [Fact]
    public async Task Ui_dc_agent_for_a_running_server()
    {
        var server = Environment.GetEnvironmentVariable("BDFU_UI_SERVER");
        Assert.SkipUnless(server is { Length: > 0 }, "Only runs a domain controller's agent for checking the console (BDFU_UI_SERVER=<url>).");
        var stopFile = Environment.GetEnvironmentVariable("BDFU_UI_STOP") ?? Path.Combine(_root, "stop");
        // The enrollment secret from the console (Guest agents → Settings); the sign-in cookie is HTTPS-only.
        var secret = Environment.GetEnvironmentVariable("BDFU_UI_SECRET");
        Assert.False(string.IsNullOrEmpty(secret), "Set BDFU_UI_SECRET to the server's enrollment secret.");

        var directory = Path.Combine(_root, "ui-agent");
        Directory.CreateDirectory(directory);
        var paths = new AgentPaths(directory);
        await File.WriteAllTextAsync(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(new AgentConfig { ServerUrl = server!, EnrollmentSecret = secret }, Backdafuckup.Agent.Core.AgentJson.Options), Ct);
        var sandbox = Path.Combine(_root, "ui-dc");
        var live = Path.Combine(sandbox, "C", "Windows", "SYSVOL", "sysvol", "bdfu-lab.local", "Policies", UiGpo);
        Directory.CreateDirectory(Path.Combine(live, "Machine"));
        await File.WriteAllTextAsync(Path.Combine(live, "GPT.INI"), "[General]\r\nVersion=327681\r\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(live, "Machine", "registry.pol"), "live settings", Ct);
        var (backupDirectory, liveDirectory) = AdDirectories();
        AddGpo(backupDirectory, liveDirectory, UiGpo, "Lab Policy A");
        for (var i = 10; i < 40; i++)
        {
            var guid = Guid.NewGuid();
            foreach (var d in new[] { backupDirectory, liveDirectory })
            {
                d.Add($"CN=lab-user{i},OU=Lab,{AdDomain}", guid, ("objectClass", ["top", "person", "organizationalPerson", "user"]), ("objectCategory", ["person"]),
                    ("sAMAccountName", [$"lab-user{i}"]), ("description", [i % 7 == 0 && d == liveDirectory ? "Changed after the backup" : $"Lab user {i}"]));
            }
        }

        var env = new FakeAdEnvironment(sandbox, backupDirectory, liveDirectory, windowsSandbox: true);
        var runner = new AgentRunner(paths, new SimulatedHost(Environment.GetEnvironmentVariable("BDFU_UI_VM") ?? Guid.NewGuid().ToString(), true), new SimulatedVss(null, true),
            new AgentLog(paths.LogFile), _ => new HttpClientHandler(), TimeSpan.FromSeconds(2), new SandboxRestoreFileSystem(sandbox) { RecordPermissions = true }, ad: env);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = Task.Run(() => runner.RunAsync(stop.Token), Ct);
        var until = DateTime.UtcNow.AddHours(2);
        while (!File.Exists(stopFile) && DateTime.UtcNow < until && !running.IsCompleted)
        {
            await Task.Delay(1000, Ct);
        }

        await stop.CancelAsync();
        await running;
    }

    private const string UiGpo = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";

    private static byte[] UiDcImage() => WindowsTestDisk.Create(
    [
        new WindowsTestDisk.Volume('C', 24, new Dictionary<string, byte[]>
        {
            ["Windows/NTDS/ntds.dit"] = Pattern(3 << 20, 0x5a),
            ["Windows/NTDS/edb.chk"] = Pattern(8192, 0x22),
            ["Windows/NTDS/edb.log"] = Pattern(1 << 20, 0x11),
            [$"Windows/SYSVOL/domain/Policies/{UiGpo}/GPT.INI"] = "[General]\r\nVersion=131075\r\ndisplayName=Lab Policy A\r\n"u8.ToArray(),
            [$"Windows/SYSVOL/domain/Policies/{UiGpo}/Machine/registry.pol"] = "backup settings"u8.ToArray(),
        }),
    ]);

    private static async Task<AdExploreDto> WaitForExploreAsync(HttpClient client, Guid id)
    {
        for (var i = 0; i < 300; i++)
        {
            var explore = (await client.GetFromJsonAsync<AdExploreDto>($"/api/ad/explores/{id}", Ct))!;
            if (explore.State != AdExploreStates.Opening)
            {
                return explore;
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException("The explore session did not open within 30 s.");
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + (i * 31));
        }

        return bytes;
    }

    /// <summary>The backup's directory and the live one: lab-user01 unchanged, lab-user02 deleted (tombstone), lab-user03 gone.</summary>
    private static (InMemoryDirectory Backup, InMemoryDirectory Live) AdDirectories()
    {
        var backup = new InMemoryDirectory("dc1.bdfu-lab.local:50000", AdDomain);
        var live = new InMemoryDirectory("dc1.bdfu-lab.local:389", AdDomain);
        var ids = new Dictionary<string, Guid> { ["domain"] = Guid.NewGuid(), ["Lab"] = Guid.NewGuid(), ["deleted"] = Guid.NewGuid() };
        foreach (var name in new[] { "lab-user01", "lab-user02", "lab-user03" })
        {
            ids[name] = Guid.NewGuid();
        }

        foreach (var directory in new[] { backup, live })
        {
            directory.Add(AdDomain, ids["domain"], ("objectClass", ["top", "domain", "domainDNS"]));
            directory.Add("OU=Lab," + AdDomain, ids["Lab"], ("objectClass", ["top", "organizationalUnit"]));
        }

        live.Add("CN=Deleted Objects," + AdDomain, ids["deleted"], ("objectClass", ["top", "container"]));
        object[] user = ["top", "person", "organizationalPerson", "user"];
        backup.Add("CN=lab-user01,OU=Lab," + AdDomain, ids["lab-user01"], ("objectClass", user), ("objectCategory", ["person"]), ("description", ["Boss"]));
        backup.Add("CN=lab-user02,OU=Lab," + AdDomain, ids["lab-user02"], ("objectClass", user), ("objectCategory", ["person"]), ("description", ["Sales"]));
        backup.Add("CN=lab-user03,OU=Lab," + AdDomain, ids["lab-user03"], ("objectClass", user), ("objectCategory", ["person"]), ("description", ["Gone"]));
        live.Add("CN=lab-user01,OU=Lab," + AdDomain, ids["lab-user01"], ("objectClass", user), ("objectCategory", ["person"]), ("description", ["Boss"]));
        live.Add($"CN=lab-user02\\0ADEL:{ids["lab-user02"]:D},CN=Deleted Objects," + AdDomain, ids["lab-user02"], ("objectClass", user), ("isDeleted", ["TRUE"]));

        // The schema of the live DC (for the object view).
        foreach (var (name, syntax) in new[] { ("description", "2.5.5.12"), ("objectClass", "2.5.5.2"), ("objectCategory", "2.5.5.1"), ("name", "2.5.5.12") })
        {
            live.Add($"CN={name},{live.SchemaDn}", Guid.NewGuid(), ("objectClass", ["top", "attributeSchema"]), ("lDAPDisplayName", [name]), ("attributeSyntax", [syntax]));
        }

        return (backup, live);
    }
}
