using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

public sealed class AdGpoTests : IDisposable
{
    private const string Domain = "DC=bdfu-lab,DC=local";
    private const string Policies = "CN=Policies,CN=System," + Domain;
    private const string A = "{6AC1786C-016F-11D2-945F-00C04FB984F9}";
    private const string B = "{2E7B0A4C-5D3F-4B71-9A11-0F4C2B1A9E01}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryDirectory _backup = new("dc1.bdfu-lab.local:50000", Domain);
    private readonly InMemoryDirectory _live = new("dc1.bdfu-lab.local:389", Domain);
    private readonly Dictionary<string, Guid> _ids = [];
    private readonly List<string> _log = [];
    private readonly List<string> _tracked = [];
    private readonly FakeAdEnvironment _env;
    private readonly AdSchema _schema = new(
    [
        new AdAttributeInfo { Name = "displayName", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "versionNumber", Syntax = "2.5.5.9" },
        new AdAttributeInfo { Name = "flags", Syntax = "2.5.5.9" },
        new AdAttributeInfo { Name = "gPCFileSysPath", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "gPLink", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "description", Syntax = "2.5.5.12" },
    ]);

    public AdGpoTests()
    {
        Directory.CreateDirectory(_root);
        _env = new FakeAdEnvironment(_root, _backup, _live);
        Seed();
    }

    private string PoliciesFolder => Path.Combine(_root, "sysvol", "bdfu-lab.local", "Policies");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_changed_policy_gets_its_files_container_permissions_link_and_a_higher_version()
    {
        var restorer = Restorer();
        var staging = Assert.Single(restorer.Prepare([new AdGpoChoice { Guid = _ids[A] }], _log.Add));
        Assert.Equal(Path.Combine(PoliciesFolder, A + ".bdfu-new"), staging.Folder);
        Assert.Equal("Lab Policy A", staging.DisplayName);
        Assert.Contains(staging.Folder, _tracked);
        Copy(staging.Folder, "registry.pol from the backup", "[General]\r\nVersion=131075\r\ndisplayName=Lab Policy A\r\n");

        var item = Assert.Single(restorer.Run(new AdJobRequest { Gpos = [new AdGpoChoice { Guid = _ids[A] }] }, write: true, _log.Add));

        Assert.Equal(AdRestoreOutcomes.Done, item.Outcome);
        Assert.Equal(AdRestoreActions.Gpo, item.Action);
        var folder = Path.Combine(PoliciesFolder, A);
        Assert.Equal("registry.pol from the backup", File.ReadAllText(Path.Combine(folder, "Machine", "registry.pol")));
        var version = AdGpoRestorer.NextVersion(0x20003, 0x50001);
        Assert.Equal((6 << 16) | 4, version);
        Assert.Contains($"Version={version}", File.ReadAllText(Path.Combine(folder, "GPT.INI")), StringComparison.Ordinal);
        Assert.Equal([A], Directory.GetDirectories(PoliciesFolder).Select(Path.GetFileName));
        Assert.Empty(_tracked);

        var gpc = _live.Find(_ids[A])!;
        Assert.Equal("Lab Policy A", gpc.Text("displayName"));
        Assert.Equal(version.ToString(System.Globalization.CultureInfo.InvariantCulture), gpc.Text("versionNumber"));
        Assert.Equal([1, 2, 3], gpc.Bytes("nTSecurityDescriptor"));
        Assert.Contains($"[LDAP://cn={A},cn=policies,cn=system,{Domain};0]", _live.Find(_ids["Lab"])!.Text("gPLink"), StringComparison.Ordinal);
        Assert.Equal(["files", "version", "modify", "modify", "acl", "link", "files"], item.Steps.Select(s => s.Operation));
        Assert.Contains(_log, l => l.StartsWith($"version {Path.Combine(folder, "GPT.INI")}: Version 327681 → {version} (user 6, computer 4)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gone_policy_is_re_created_with_its_name_and_its_files_and_linked_again()
    {
        var restorer = Restorer();
        var staging = Assert.Single(restorer.Prepare([new AdGpoChoice { Guid = _ids[B] }], _log.Add));
        Copy(staging.Folder, "B settings", "[General]\r\nVersion=65537\r\n");

        // The preview writes nothing and names every step.
        var plan = Assert.Single(restorer.Run(new AdJobRequest { Gpos = [new AdGpoChoice { Guid = _ids[B] }] }, write: false, _log.Add));
        Assert.Equal(AdRestoreOutcomes.Planned, plan.Outcome);
        Assert.Equal(AdObjectStates.Gone, plan.State);
        Assert.Empty(_live.Writes);
        Assert.False(Directory.Exists(Path.Combine(PoliciesFolder, B)));

        var item = Assert.Single(restorer.Run(new AdJobRequest { Gpos = [new AdGpoChoice { Guid = _ids[B] }] }, write: true, _log.Add));
        Assert.Equal(AdRestoreOutcomes.Done, item.Outcome);
        var gpc = _live.Find($"CN={B},{Policies}", false)!;
        Assert.NotEqual(_ids[B], gpc.Guid);
        Assert.NotNull(_live.Find($"CN=Machine,CN={B},{Policies}", false));
        Assert.NotNull(_live.Find($"CN=User,CN={B},{Policies}", false));
        Assert.Equal(((1 + 1) << 16 | (1 + 1)).ToString(System.Globalization.CultureInfo.InvariantCulture), gpc.Text("versionNumber"));
        Assert.Equal("B settings", File.ReadAllText(Path.Combine(PoliciesFolder, B, "Machine", "registry.pol")));
        Assert.Contains(item.Warnings, w => w.StartsWith("The policy container was re-created", StringComparison.Ordinal));
        Assert.DoesNotContain(item.Warnings, w => w.Contains("SID", StringComparison.Ordinal));
        Assert.Contains($"cn={B}", _live.Find(Domain, false)!.Text("gPLink"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("files", item.Steps.Where(s => s.Text.StartsWith("deleted", StringComparison.Ordinal)).Select(s => s.Operation));
    }

    [Fact]
    public void A_failed_restore_puts_the_previous_files_back()
    {
        var restorer = Restorer();
        var staging = Assert.Single(restorer.Prepare([new AdGpoChoice { Guid = _ids[A] }], _log.Add));
        Copy(staging.Folder, "registry.pol from the backup", "[General]\r\nVersion=131075\r\n");
        _live.FailWrite = dn => dn.StartsWith($"CN={A}", StringComparison.OrdinalIgnoreCase);

        var item = Assert.Single(restorer.Run(new AdJobRequest { Gpos = [new AdGpoChoice { Guid = _ids[A] }] }, write: true, _log.Add));

        Assert.Equal(AdRestoreOutcomes.Failed, item.Outcome);
        Assert.Contains("InsufficientAccessRights (50)", item.Reason, StringComparison.Ordinal);
        Assert.Equal("live settings", File.ReadAllText(Path.Combine(PoliciesFolder, A, "Machine", "registry.pol")));
        Assert.Equal("registry.pol from the backup", File.ReadAllText(Path.Combine(staging.Folder, "Machine", "registry.pol")));
        Assert.False(Directory.Exists(Path.Combine(PoliciesFolder, A + ".bdfu-replaced")));
        Assert.Equal([staging.Folder], _tracked);
        Assert.Contains(_log, l => l.Contains("the previous files are back in place", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_the_copied_files_nothing_is_changed()
    {
        var restorer = Restorer();
        restorer.Prepare([new AdGpoChoice { Guid = _ids[A] }], _log.Add);

        var item = Assert.Single(restorer.Run(new AdJobRequest { Gpos = [new AdGpoChoice { Guid = _ids[A] }] }, write: true, _log.Add));

        Assert.Equal(AdRestoreOutcomes.Failed, item.Outcome);
        Assert.Contains("has no GPT.INI: the files of Lab Policy A were not copied", item.Reason, StringComparison.Ordinal);
        Assert.Empty(_live.Writes);
        Assert.Equal("live settings", File.ReadAllText(Path.Combine(PoliciesFolder, A, "Machine", "registry.pol")));
    }

    [Fact]
    public void An_interrupted_restore_is_cleaned_up_at_unmount()
    {
        var mounts = new AdMounts(_env, new AgentLog(Path.Combine(_root, "agent.log")), _root);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(mounts.Prepare(id, 1, _ => { }).Folder, "ntds.dit"), "database");
        mounts.Mount(id, new AdAccount { UserName = "BDFU-LAB\\Administrator", Password = "secret" }, TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(5), _ => { }, TestContext.Current.CancellationToken);
        var staging = Path.Combine(PoliciesFolder, B + ".bdfu-new");
        var replaced = Path.Combine(PoliciesFolder, A + ".bdfu-replaced");
        Directory.CreateDirectory(staging);
        Directory.Move(Path.Combine(PoliciesFolder, A), replaced);
        mounts.TrackPolicyFolder(id, staging, true);
        mounts.TrackPolicyFolder(id, replaced, true);
        Assert.Contains("bdfu-replaced", File.ReadAllText(Path.Combine(_root, "ad-mounts.json")), StringComparison.Ordinal);

        mounts.Unmount(id, "test");

        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(replaced));
        Assert.Equal("live settings", File.ReadAllText(Path.Combine(PoliciesFolder, A, "Machine", "registry.pol")));
    }

    [Theory]
    [InlineData("[General]\r\nVersion=7\r\ndisplayName=Ünïcode\r\n", "[General]\r\nVersion=65538\r\ndisplayName=Ünïcode\r\n")]
    [InlineData("[General]\r\ndisplayName=x\r\n", "[General]\r\nVersion=65538\r\ndisplayName=x\r\n")]
    [InlineData("", "[General]\r\nVersion=65538\r\n")]
    public void The_version_is_written_into_gpt_ini_keeping_the_rest(string before, string after)
    {
        var latin1 = Encoding.GetEncoding(28591);
        Assert.Equal(latin1.GetBytes(after), AdGpoRestorer.WithVersion(latin1.GetBytes(before), 65538));
    }

    private AdGpoRestorer Restorer() => new(_env, _backup, _live, Domain, _schema, recycleBin: false, (folder, add) =>
    {
        _tracked.Remove(folder);
        if (add)
        {
            _tracked.Add(folder);
        }
    });

    private static void Copy(string folder, string registry, string gptIni)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Machine"));
        File.WriteAllText(Path.Combine(folder, "Machine", "registry.pol"), registry);
        File.WriteAllText(Path.Combine(folder, "GPT.INI"), gptIni);
    }

    private void Seed()
    {
        Guid Id(string name) => _ids.TryGetValue(name, out var id) ? id : _ids[name] = Guid.NewGuid();
        object[] Classes(params string[] classes) => ["top", .. classes];
        var linkA = $"[LDAP://cn={A},cn=policies,cn=system,{Domain};0]";
        var linkB = $"[LDAP://cn={B},cn=policies,cn=system,{Domain};0]";
        foreach (var directory in new[] { _backup, _live })
        {
            var live = directory == _live;
            directory.Add(Domain, Id("domain"), ("objectClass", Classes("domain", "domainDNS")), ("gPLink", [live ? string.Empty : linkB]));
            directory.Add("CN=System," + Domain, Id("System"), ("objectClass", Classes("container")));
            directory.Add(Policies, Id("Policies"), ("objectClass", Classes("container")));
            directory.Add("OU=Lab," + Domain, Id("Lab"), ("objectClass", Classes("organizationalUnit")), ("gPLink", [live ? string.Empty : linkA]));
        }

        foreach (var (name, display, directory, version) in new[] { (A, "Lab Policy A", _backup, 0x20003), (A, "Lab Policy A (changed)", _live, 0x50001), (B, "Lab Policy B", _backup, 0x10001) })
        {
            var gpc = directory.Add($"CN={name},{Policies}", Id(name), ("objectClass", Classes("container", "groupPolicyContainer")), ("cn", [name]), ("displayName", [display]),
                ("versionNumber", [version.ToString(System.Globalization.CultureInfo.InvariantCulture)]), ("flags", ["0"]),
                ("gPCFileSysPath", [$@"\\bdfu-lab.local\SysVol\bdfu-lab.local\Policies\{name}"]), ("nTSecurityDescriptor", [directory == _backup ? new byte[] { 1, 2, 3 } : [9, 9]]));
            directory.Add($"CN=User,{gpc.Dn}", Id(name + "/User"), ("objectClass", Classes("container")));
            directory.Add($"CN=Machine,{gpc.Dn}", Id(name + "/Machine"), ("objectClass", Classes("container")));
        }

        // The live files of policy A.
        Copy(Path.Combine(PoliciesFolder, A), "live settings", "[General]\r\nVersion=327681\r\ndisplayName=Lab Policy A (changed)\r\n");
    }
}
