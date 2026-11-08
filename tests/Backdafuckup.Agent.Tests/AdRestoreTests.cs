using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

public sealed class AdRestoreTests
{
    private const string Domain = "DC=bdfu-lab,DC=local";
    private const string Users = "OU=Users,OU=Lab," + Domain;
    private const string Password = "N3w-Passw0rd!";

    private readonly InMemoryDirectory _backup = new("dc1.bdfu-lab.local:50000", Domain);
    private readonly InMemoryDirectory _live = new("dc1.bdfu-lab.local:389", Domain);
    private readonly Dictionary<string, Guid> _ids = [];
    private readonly List<string> _log = [];
    private readonly AdSchema _schema = new(
    [
        new AdAttributeInfo { Name = "description", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "title", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "sAMAccountName", Syntax = "2.5.5.12" },
        new AdAttributeInfo { Name = "userAccountControl", Syntax = "2.5.5.9" },
        new AdAttributeInfo { Name = "manager", Syntax = "2.5.5.1", LinkId = 42 },
        new AdAttributeInfo { Name = "member", Syntax = "2.5.5.1", LinkId = 2 },
        new AdAttributeInfo { Name = "memberOf", Syntax = "2.5.5.1", LinkId = 3, SystemOnly = true },
        new AdAttributeInfo { Name = "objectSid", Syntax = "2.5.5.17", SystemOnly = true },
        new AdAttributeInfo { Name = "whenChanged", Syntax = "2.5.5.11", SystemOnly = true },
        new AdAttributeInfo { Name = "msDS-KeyCredentialLink", Syntax = "2.5.5.7" },
    ]);

    public AdRestoreTests() => Seed(recycleBin: false);

    [Fact]
    public void A_plan_lists_every_step_and_writes_nothing()
    {
        var items = Restorer(false).Run(Request(false, Choice("lab-user02", moveBack: true), Choice("lab-user03"), Choice("lab-user04")), write: false, _log.Add);

        Assert.Empty(_live.Writes);
        Assert.Empty(_log);
        var changed = Item(items, "lab-user02");
        Assert.Equal(AdRestoreActions.Modify, changed.Action);
        Assert.Equal(AdRestoreOutcomes.Planned, changed.Outcome);
        Assert.Equal(["move", "modify", "modify"], changed.Steps.Select(s => s.Operation));
        Assert.Equal($"to CN=lab-user02,{Users}", changed.Steps[0].Text);
        Assert.Equal("description: \"Marketing\" → \"Sales\"; title: \"Lead\" → \"Sales rep\"", changed.Steps[1].Text);
        Assert.Equal($"manager: add \"CN=lab-user01,{Users}\"", changed.Steps[2].Text);
        Assert.All(changed.Steps, s => Assert.False(s.Done));

        var tombstone = Item(items, "lab-user03");
        Assert.Equal(AdRestoreActions.Reanimate, tombstone.Action);
        Assert.Equal(["reanimate", "modify", "member", "account"], tombstone.Steps.Select(s => s.Operation));
        Assert.Equal($"back from Deleted Objects to CN=lab-user03,{Users}", tombstone.Steps[0].Text);
        Assert.Contains("description: (none) → \"To be deleted\"", tombstone.Steps[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("userAccountControl", tombstone.Steps[1].Text, StringComparison.Ordinal);
        Assert.Equal($"CN=LabGroupA,OU=Groups,OU=Lab,{Domain}", tombstone.Steps[2].Target);
        Assert.Contains("disabled (userAccountControl 514), must change the password at the next logon", tombstone.Steps[3].Text, StringComparison.Ordinal);

        // Gone, and re-creation not chosen.
        var gone = Item(items, "lab-user04");
        Assert.Equal(AdRestoreOutcomes.Skipped, gone.Outcome);
        Assert.Contains("choose \"re-create\"", gone.Reason, StringComparison.Ordinal);
        Assert.Empty(gone.Steps);
    }

    [Fact]
    public void The_preview_shows_the_password_steps_when_a_password_will_be_entered()
    {
        var request = Request(false, Choice("lab-user03"));
        request.PasswordEntered = true;

        var item = Item(Restorer(false).Run(request, write: false, _log.Add), "lab-user03");

        Assert.Equal(["reanimate", "modify", "member", "password", "account"], item.Steps.Select(s => s.Operation));
        Assert.Equal("userAccountControl 512 (enabled, as in the backup)", item.Steps[^1].Text);
        Assert.Empty(item.Warnings);
        Assert.Empty(_live.Writes);
    }

    [Fact]
    public void A_tombstone_comes_back_with_its_attributes_membership_and_a_new_password_and_a_changed_object_is_put_back()
    {
        var items = Restorer(false).Run(Request(false, Password, Choice("lab-user03"), Choice("lab-user02", moveBack: true)), write: true, _log.Add);

        var tombstone = Item(items, "lab-user03");
        Assert.Equal(AdRestoreOutcomes.Done, tombstone.Outcome);
        Assert.Equal($"CN=lab-user03,{Users}", tombstone.LiveDn);
        var user03 = _live.Find(_ids["lab-user03"])!;
        Assert.Equal($"CN=lab-user03,{Users}", user03.Dn);
        Assert.False(user03.Flag("isDeleted"));
        Assert.Equal("To be deleted", user03.Text("description"));
        Assert.Equal(AdRestorer.PasswordValue(Password), user03.Bytes("unicodePwd"));
        Assert.Equal("512", user03.Text("userAccountControl"));
        Assert.Contains(_live.Find(_ids["LabGroupA"])!.Values("member").OfType<string>(), m => AdText.ParseExtendedDn(m).Guid == _ids["lab-user03"]);
        Assert.All(tombstone.Steps, s => Assert.True(s.Done));

        var changed = Item(items, "lab-user02");
        var user02 = _live.Find(_ids["lab-user02"])!;
        Assert.Equal($"CN=lab-user02,{Users}", user02.Dn);
        Assert.Equal("Sales", user02.Text("description"));
        Assert.Equal(_ids["lab-user01"], AdText.ParseExtendedDn(user02.Text("manager")!).Guid);
        Assert.Equal(AdRestoreOutcomes.Done, changed.Outcome);

        // Every write is in the log with its DN; the password never.
        Assert.Contains(_log, l => l.StartsWith($"reanimate CN=lab-user03\\0ADEL:{_ids["lab-user03"]:D},CN=Deleted Objects,{Domain}", StringComparison.Ordinal));
        Assert.Contains(_log, l => l == $"password CN=lab-user03,{Users}: the password entered for the restore");
        Assert.DoesNotContain(_log, l => l.Contains(Password, StringComparison.Ordinal));
        Assert.DoesNotContain(_live.Writes, w => w.StartsWith("delete", StringComparison.Ordinal));
    }

    [Fact]
    public void With_the_recycle_bin_a_deleted_object_comes_back_whole_and_keeps_its_password()
    {
        Seed(recycleBin: true);

        var item = Item(Restorer(true).Run(Request(false, Choice("lab-user03")), write: true, _log.Add), "lab-user03");

        Assert.Equal(AdRestoreOutcomes.Done, item.Outcome);
        Assert.Equal(["reanimate"], item.Steps.Select(s => s.Operation));
        Assert.Contains("with all its attributes and links (Recycle Bin)", item.Steps[0].Text, StringComparison.Ordinal);
        Assert.Equal("To be deleted", _live.Find(_ids["lab-user03"])!.Text("description"));
        Assert.Null(_live.Find(_ids["lab-user03"])!.Bytes("unicodePwd"));
    }

    [Fact]
    public void Gone_objects_are_re_created_parents_first_disabled_without_a_password()
    {
        var items = Restorer(false).Run(Request(true, Choice("lab-user05"), Choice("Gone"), Choice("lab-user04")), write: true, _log.Add);

        var ou = Item(items, "Gone");
        Assert.Equal(AdRestoreActions.Recreate, ou.Action);
        Assert.Equal(AdRestoreOutcomes.Done, ou.Outcome);
        Assert.NotNull(ou.NewGuid);
        Assert.NotEqual(_ids["Gone"], ou.NewGuid);
        Assert.Contains(ou.Warnings, w => w.Contains("new objectGUID and SID", StringComparison.Ordinal));

        var inOu = _live.Find($"CN=lab-user05,OU=Gone,OU=Lab,{Domain}", false)!;
        Assert.Equal("514", inOu.Text("userAccountControl"));
        Assert.Equal("0", inOu.Text("pwdLastSet"));
        Assert.Equal("lab-user05", inOu.Text("sAMAccountName"));
        Assert.Null(inOu.Values("objectSid").FirstOrDefault());
        Assert.Contains(Item(items, "lab-user05").Warnings, w => w.Contains("disabled until a password is set", StringComparison.Ordinal));

        // The links come in the second pass: the manager and the group membership.
        var user04 = _live.Find($"CN=lab-user04,{Users}", false)!;
        Assert.Equal(_ids["lab-user01"], AdText.ParseExtendedDn(user04.Text("manager")!).Guid);
        Assert.Contains(_live.Find(_ids["LabGroupA"])!.Values("member").OfType<string>(), m => AdText.ParseExtendedDn(m).Guid == user04.Guid);
        Assert.Equal(new[] { $"add OU=Gone,OU=Lab,{Domain}", $"add CN=lab-user04,{Users}", $"add CN=lab-user05,OU=Gone,OU=Lab,{Domain}" }.Order(StringComparer.Ordinal),
            _live.Writes.Where(w => w.StartsWith("add", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.True(_live.Writes.IndexOf($"add OU=Gone,OU=Lab,{Domain}") < _live.Writes.IndexOf($"add CN=lab-user05,OU=Gone,OU=Lab,{Domain}"));
    }

    [Fact]
    public void An_object_whose_container_is_gone_is_skipped_or_put_into_the_chosen_one()
    {
        var skipped = Item(Restorer(false).Run(Request(true, Choice("lab-user05")), write: true, _log.Add), "lab-user05");
        Assert.Equal(AdRestoreOutcomes.Skipped, skipped.Outcome);
        Assert.Contains($"Its container OU=Gone,OU=Lab,{Domain} is not in the live directory", skipped.Reason, StringComparison.Ordinal);
        Assert.Empty(_live.Writes);

        var placed = Item(Restorer(false).Run(Request(true, null, Users, Choice("lab-user05")), write: true, _log.Add), "lab-user05");
        Assert.Equal(AdRestoreOutcomes.Done, placed.Outcome);
        Assert.Equal($"CN=lab-user05,{Users}", placed.LiveDn);
    }

    [Fact]
    public void A_refused_reanimation_fails_with_the_ldap_result_and_the_others_go_on()
    {
        Seed(recycleBin: true);
        _live.RefuseRecycledReanimation = true;

        var items = Restorer(true).Run(Request(false, Choice("lab-user04"), Choice("lab-user02")), write: true, _log.Add);

        var recycled = Item(items, "lab-user04");
        Assert.Equal(AdObjectStates.Recycled, recycled.State);
        Assert.Equal(AdRestoreOutcomes.Failed, recycled.Outcome);
        Assert.Contains("on dc1.bdfu-lab.local:389 failed: UnwillingToPerform (53)", recycled.Reason, StringComparison.Ordinal);
        Assert.Equal(AdRestoreOutcomes.Done, Item(items, "lab-user02").Outcome);
    }

    [Fact]
    public void Only_the_chosen_attributes_are_written_and_an_unchanged_object_is_skipped()
    {
        var items = Restorer(false).Run(Request(false, Choice("lab-user02", attributes: ["title"]), Choice("lab-user01")), write: true, _log.Add);

        var changed = Item(items, "lab-user02");
        Assert.Equal("title: \"Lead\" → \"Sales rep\"", Assert.Single(changed.Steps).Text);
        Assert.Equal("Marketing", _live.Find(_ids["lab-user02"])!.Text("description"));
        var unchanged = Item(items, "lab-user01");
        Assert.Equal(AdRestoreOutcomes.Skipped, unchanged.Outcome);
        Assert.Equal("Nothing to restore: it is as in the backup.", unchanged.Reason);

        // Moved back where it already is, and only an attribute that does not differ: nothing to do.
        var again = Item(Restorer(false).Run(Request(false, Choice("lab-user02", moveBack: true, attributes: ["title"])), write: true, _log.Add), "lab-user02");
        Assert.Equal(AdRestoreOutcomes.Done, again.Outcome);
        var nothing = Item(Restorer(false).Run(Request(false, Choice("lab-user02", moveBack: true, attributes: ["title"])), write: true, _log.Add), "lab-user02");
        Assert.Equal(AdRestoreOutcomes.Skipped, nothing.Outcome);
        Assert.Empty(nothing.Steps);
    }

    private static AdRestoreItem Item(List<AdRestoreItem> items, string name) => items.Single(i => AdText.RdnValue(i.Dn) == name);

    private AdRestorer Restorer(bool recycleBin) => new(_backup, _live, Domain, _schema, recycleBin);

    private AdRestoreObject Choice(string name, bool moveBack = false, List<string>? attributes = null) =>
        new() { Guid = _ids[name], MoveBack = moveBack, Attributes = attributes };

    private static AdJobRequest Request(bool recreate, params AdRestoreObject[] objects) => Request(recreate, null, null, objects);

    private static AdJobRequest Request(bool recreate, string? password, params AdRestoreObject[] objects) => Request(recreate, password, null, objects);

    private static AdJobRequest Request(bool recreate, string? password, string? targetParent, params AdRestoreObject[] objects) =>
        new() { Kind = AdJobKinds.Restore, Objects = [.. objects], Recreate = recreate, NewPassword = password, TargetParentDn = targetParent };

    private void Seed(bool recycleBin)
    {
        _backup.Records.Clear();
        _live.Records.Clear();
        Guid Id(string name) => _ids.TryGetValue(name, out var id) ? id : _ids[name] = Guid.NewGuid();
        object[] Classes(params string[] classes) => ["top", .. classes];
        var user = Classes("person", "organizationalPerson", "user");

        foreach (var directory in new[] { _backup, _live })
        {
            directory.Add(Domain, Id("domain"), ("objectClass", Classes("domain", "domainDNS")));
            directory.Add("OU=Lab," + Domain, Id("Lab"), ("objectClass", Classes("organizationalUnit")));
            directory.Add(Users, Id("Users"), ("objectClass", Classes("organizationalUnit")));
            directory.Add("OU=Groups,OU=Lab," + Domain, Id("Groups"), ("objectClass", Classes("organizationalUnit")));
        }

        _backup.Add("OU=Gone,OU=Lab," + Domain, Id("Gone"), ("objectClass", Classes("organizationalUnit")), ("description", ["An OU that was removed"]));
        _live.Add("CN=Deleted Objects," + Domain, Id("Deleted Objects"), ("objectClass", Classes("container")));

        AdRecord Person(InMemoryDirectory d, string dn, string name, string description, params (string, object[])[] more) =>
            d.Add(dn, Id(name), [("objectClass", user), ("sAMAccountName", [name]), ("description", [description]), ("userAccountControl", ["512"]),
                ("objectSid", [new byte[] { 1, 1, 0, 0, 0, 0, 0, 5, 7, 0, 0, 0 }]), ("whenChanged", ["20261101100000.0Z"]), .. more]);

        var user01 = Person(_backup, $"CN=lab-user01,{Users}", "lab-user01", "Boss");
        var liveUser01 = Person(_live, $"CN=lab-user01,{Users}", "lab-user01", "Boss");
        var groupBackup = _backup.Add($"CN=LabGroupA,OU=Groups,OU=Lab,{Domain}", Id("LabGroupA"), ("objectClass", Classes("group")), ("sAMAccountName", ["LabGroupA"]));
        var groupLive = _live.Add($"CN=LabGroupA,OU=Groups,OU=Lab,{Domain}", Id("LabGroupA"), ("objectClass", Classes("group")), ("sAMAccountName", ["LabGroupA"]));
        object[] memberOf = [InMemoryDirectory.Link(groupBackup)];

        var user02 = Person(_backup, $"CN=lab-user02,{Users}", "lab-user02", "Sales", ("title", ["Sales rep"]), ("manager", [InMemoryDirectory.Link(user01)]), ("memberOf", memberOf));
        var user03 = Person(_backup, $"CN=lab-user03,{Users}", "lab-user03", "To be deleted", ("memberOf", memberOf));
        var user04 = Person(_backup, $"CN=lab-user04,{Users}", "lab-user04", "Gone for good", ("manager", [InMemoryDirectory.Link(user01)]), ("memberOf", memberOf));
        Person(_backup, $"CN=lab-user05,OU=Gone,OU=Lab,{Domain}", "lab-user05", "In a removed OU");
        groupBackup.Attributes["member"] = [InMemoryDirectory.Link(user02), InMemoryDirectory.Link(user03), InMemoryDirectory.Link(user04)];

        // Live: lab-user02 changed and moved out of OU=Users; lab-user03 deleted; lab-user04 deleted too (recycled with the Recycle Bin) or gone.
        var liveUser02 = Person(_live, $"CN=lab-user02,OU=Lab,{Domain}", "lab-user02", "Marketing", ("title", ["Lead"]), ("memberOf", [InMemoryDirectory.Link(groupLive)]));
        groupLive.Attributes["member"] = [InMemoryDirectory.Link(liveUser02)];
        var deleted = $"CN=lab-user03\\0ADEL:{_ids["lab-user03"]:D},CN=Deleted Objects,{Domain}";
        if (recycleBin)
        {
            Person(_live, deleted, "lab-user03", "To be deleted", ("isDeleted", ["TRUE"]));
            _live.Add($"CN=lab-user04\\0ADEL:{_ids["lab-user04"]:D},CN=Deleted Objects,{Domain}", _ids["lab-user04"], ("objectClass", user), ("isDeleted", ["TRUE"]), ("isRecycled", ["TRUE"]));
        }
        else
        {
            // A tombstone keeps sAMAccountName and objectSid, not the description or the memberships.
            _live.Add(deleted, _ids["lab-user03"], ("objectClass", user), ("isDeleted", ["TRUE"]), ("sAMAccountName", ["lab-user03"]),
                ("objectSid", [new byte[] { 1, 1, 0, 0, 0, 0, 0, 5, 7, 0, 0, 0 }]), ("userAccountControl", ["512"]));
        }

        _ = liveUser01;
    }
}
