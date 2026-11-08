using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Restores objects of a backup's directory into the live one over LDAP, or plans it without writing. Per object:
/// <list type="bullet">
/// <item>deleted or recycled: reanimated from Deleted Objects (same objectGUID and SID), then the attributes the
/// tombstone lost are written back;</item>
/// <item>gone: created anew from the backup (new objectGUID and SID; permissions and references elsewhere do not
/// follow);</item>
/// <item>changed: the chosen attributes are written back, and it is moved back if chosen.</item>
/// </list>
/// Two passes: first the objects and their plain attributes (parents first), then links, memberships and accounts, so a
/// link can point to an object restored in the same run. Nothing live is deleted. Passwords are never logged.
/// </summary>
public sealed class AdRestorer
{
    private const int AccountDisable = 0x2;
    private static readonly string[] All = { "*", "parentGUID" };

    private readonly IAdDirectory _backup;
    private readonly IAdDirectory _live;
    private readonly string _domainDn;
    private readonly AdSchema _schema;
    private readonly bool _recycleBin;
    private readonly Dictionary<Guid, string> _restored = new Dictionary<Guid, string>();
    private readonly Dictionary<Guid, string?> _liveDns = new Dictionary<Guid, string?>();

    public AdRestorer(IAdDirectory backup, IAdDirectory live, string domainDn, AdSchema schema, bool recycleBin)
    {
        _backup = backup;
        _live = live;
        _domainDn = domainDn;
        _schema = schema;
        _recycleBin = recycleBin;
    }

    /// <param name="write">False: only plan (the steps come back with <see cref="AdRestoreStep.Done"/> false).</param>
    public List<AdRestoreItem> Run(AdJobRequest request, bool write, Action<string> log)
    {
        var work = new List<Work>();
        var items = new List<AdRestoreItem>();
        foreach (var choice in request.Objects.GroupBy(o => o.Guid).Select(g => g.First()))
        {
            var backup = Read(_backup, choice.Guid, false);
            if (backup is null)
            {
                items.Add(new AdRestoreItem { Guid = choice.Guid, Outcome = AdRestoreOutcomes.Skipped, Reason = $"The backup's directory ({_backup.Endpoint}) has no object with objectGUID {choice.Guid}." });
                continue;
            }

            var live = Read(_live, choice.Guid, true);
            var item = new AdRestoreItem { Guid = choice.Guid, Dn = backup.Dn, ObjectClass = backup.ObjectClass, State = AdCompare.State(backup, live, _recycleBin), LiveDn = live?.Dn };
            items.Add(item);
            work.Add(new Work(choice, backup, live, item));
        }

        // Parents first: a reanimated or re-created OU must exist before its children come back into it.
        foreach (var w in work.OrderBy(w => Depth(w.Backup.Dn)))
        {
            Guard(w.Item, () => First(w, request, write, log));
        }

        foreach (var w in work.Where(w => w.Item.Outcome == AdRestoreOutcomes.Planned && w.Item.Action != AdRestoreActions.None))
        {
            Second(w, request, write, log);
        }

        foreach (var item in items.Where(i => i.Outcome == AdRestoreOutcomes.Planned))
        {
            if (item.Steps.Count == 0)
            {
                // E.g. moved back to where it is, or only attributes chosen that do not differ.
                item.Action = AdRestoreActions.None;
                continue;
            }

            item.Outcome = write ? AdRestoreOutcomes.Done : AdRestoreOutcomes.Planned;
        }

        foreach (var item in items.Where(i => i.Action == AdRestoreActions.None && i.Outcome != AdRestoreOutcomes.Failed && i.Outcome != AdRestoreOutcomes.Skipped))
        {
            item.Outcome = AdRestoreOutcomes.Skipped;
            item.Reason ??= "Nothing to restore: it is as in the backup.";
        }

        return items;
    }

    /// <summary>The password value of unicodePwd: the password in quotes, UTF-16LE ([MS-ADTS] 3.1.1.3.1.5.1).</summary>
    public static byte[] PasswordValue(string password) => Encoding.Unicode.GetBytes("\"" + password + "\"");

    private static int Depth(string dn)
    {
        var depth = 0;
        for (var parent = AdText.Parent(dn); parent is not null; parent = AdText.Parent(parent))
        {
            depth++;
        }

        return depth;
    }

    private static string Rdn(string dn) => AdText.Parent(dn) is { } parent ? dn.Substring(0, dn.Length - parent.Length - 1) : dn;

    private static bool IsUser(AdRecord record)
    {
        var classes = record.Values("objectClass").OfType<string>().ToList();
        return classes.Contains("user", StringComparer.OrdinalIgnoreCase) && !classes.Contains("computer", StringComparer.OrdinalIgnoreCase);
    }

    private static string Show(IReadOnlyList<object> values)
    {
        if (values.Count == 0)
        {
            return "(none)";
        }

        if (values.Count > 5)
        {
            return $"{values.Count} values";
        }

        return string.Join(", ", values.Select(v => v switch
        {
            string text when text.StartsWith("<GUID=", StringComparison.OrdinalIgnoreCase) => "\"" + AdText.ParseExtendedDn(text).Dn + "\"",
            string text => "\"" + (text.Length > 80 ? text.Substring(0, 80) + "…" : text) + "\"",
            byte[] bytes => $"{bytes.Length} bytes",
            _ => "?",
        }));
    }

    private void First(Work w, AdJobRequest request, bool write, Action<string> log)
    {
        var item = w.Item;
        var backup = w.Backup;
        switch (item.State)
        {
            case AdObjectStates.Unchanged when !w.Choice.MoveBack:
                item.Action = AdRestoreActions.None;
                return;
            case AdObjectStates.Unchanged:
            case AdObjectStates.Changed:
                item.Action = AdRestoreActions.Modify;
                w.Current = w.Live;
                item.LiveDn = w.Live!.Dn;
                if (w.Choice.MoveBack)
                {
                    MoveBack(w, request, write, log);
                }

                WriteAttributes(w, linked: false, write, log);
                _restored[item.Guid] = item.LiveDn!;
                return;
            case AdObjectStates.Deleted:
            case AdObjectStates.Recycled:
            {
                item.Action = AdRestoreActions.Reanimate;
                var target = TargetDn(w, request) ?? throw new Skip(Unplaced(w));
                Step(item, "reanimate", w.Live!.Dn, $"back from Deleted Objects to {target}" +
                    (_recycleBin && item.State == AdObjectStates.Deleted ? " with all its attributes and links (Recycle Bin)" : string.Empty), write, log,
                    () => _live.Modify(w.Live.Dn, new[]
                    {
                        new AdModification(AdModificationKind.Delete, "isDeleted"),
                        new AdModification(AdModificationKind.Replace, "distinguishedName", target),
                    }, showDeleted: true));
                item.LiveDn = target;
                _restored[item.Guid] = target;
                _liveDns[item.Guid] = target;
                w.Current = write ? Read(_live, item.Guid, false) ?? w.Live : w.Live;
                WriteAttributes(w, linked: false, write, log);
                return;
            }

            case AdObjectStates.Gone when !request.Recreate:
                throw new Skip($"Not in the live directory any more (removed after the tombstone lifetime); choose \"re-create\" to create it anew with a new SID.");
            case AdObjectStates.Gone:
            {
                item.Action = AdRestoreActions.Recreate;
                var target = TargetDn(w, request) ?? throw new Skip(Unplaced(w));
                var attributes = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["objectClass"] = backup.Values("objectClass").ToList(),
                };
                foreach (var (name, values) in Restorable(backup, linked: false))
                {
                    attributes[name] = values;
                }

                if (backup.Text("userAccountControl") is { } uac && int.TryParse(uac, out var flags))
                {
                    // Created disabled: an account cannot be enabled without a password; the second pass decides.
                    attributes["userAccountControl"] = new List<object> { (flags | AccountDisable).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                }

                item.Warnings.Add("Re-created with a new objectGUID and SID: permissions (ACLs), references from other domains and SID-based assignments do not follow.");
                if (backup.Values("objectClass").OfType<string>().Contains("computer", StringComparer.OrdinalIgnoreCase))
                {
                    item.Warnings.Add("A re-created computer account has no trust with its computer: the computer must join the domain again.");
                }

                Step(item, "add", target, $"new {backup.ObjectClass} with {attributes.Count} attribute(s): {string.Join(", ", attributes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}", write, log,
                    () => _live.Add(target, attributes));
                item.LiveDn = target;
                _restored[item.Guid] = target;
                w.Current = write ? FindByDn(target) : null;
                item.NewGuid = w.Current?.Guid;
                if (item.NewGuid is { } newGuid)
                {
                    _liveDns[newGuid] = target;
                }

                return;
            }

            default:
                item.Action = AdRestoreActions.None;
                return;
        }
    }

    private void Second(Work w, AdJobRequest request, bool write, Action<string> log)
    {
        var item = w.Item;
        try
        {
            if (item.Action == AdRestoreActions.Recreate)
            {
                // The links of a new object: what the backup had, for the targets that exist now.
                var links = Restorable(w.Backup, linked: true).ToList();
                if (links.Count > 0)
                {
                    Step(item, "modify", item.LiveDn!, string.Join("; ", links.Select(l => $"{l.Name}: {Show(l.Values)}")), write, log,
                        () => _live.Modify(item.LiveDn!, links.Select(l => new AdModification(AdModificationKind.Replace, l.Name, l.Values.ToArray())).ToList()));
                }
            }
            else
            {
                WriteAttributes(w, linked: true, write, log);
            }

            if (w.Choice.Memberships)
            {
                AddMemberships(w, write, log);
            }

            // The primary group needs the membership first; a new account starts with the default one.
            if (item.Action == AdRestoreActions.Recreate && w.Backup.Text("primaryGroupID") is { } primary && primary != "513" && primary != "515"
                && Writable("primaryGroupID", _schema.Find("primaryGroupID"), linked: false))
            {
                Step(item, "modify", item.LiveDn!, $"primaryGroupID: \"{primary}\"", write, log,
                    () => _live.Modify(item.LiveDn!, new[] { new AdModification(AdModificationKind.Replace, "primaryGroupID", primary) }));
            }

            var reanimatedWithPassword = item.Action == AdRestoreActions.Reanimate && _recycleBin && item.State == AdObjectStates.Deleted;
            if (IsUser(w.Backup) && (item.Action == AdRestoreActions.Recreate || (item.Action == AdRestoreActions.Reanimate && !reanimatedWithPassword)))
            {
                Account(w, request, write, log);
            }
        }
        catch (AdDirectoryException ex)
        {
            item.Warnings.Add(ex.Message);
            log($"{item.Dn}: {ex.Message}");
        }
    }

    /// <summary>A user that came back without a password: set the new one and enable as in the backup, or keep it disabled.</summary>
    private void Account(Work w, AdJobRequest request, bool write, Action<string> log)
    {
        var item = w.Item;
        var flags = int.TryParse(w.Backup.Text("userAccountControl"), out var uac) ? uac : 0x200;
        // The preview knows only that a password was entered; the restore gets the password itself.
        if (!string.IsNullOrEmpty(request.NewPassword) || (!write && request.PasswordEntered))
        {
            Step(item, "password", item.LiveDn!, "the password entered for the restore", write, log,
                () => _live.Modify(item.LiveDn!, new[] { new AdModification(AdModificationKind.Replace, "unicodePwd", PasswordValue(request.NewPassword!)) }));
            Step(item, "account", item.LiveDn!, $"userAccountControl {flags} ({((flags & AccountDisable) != 0 ? "disabled" : "enabled")}, as in the backup)", write, log,
                () => _live.Modify(item.LiveDn!, new[] { new AdModification(AdModificationKind.Replace, "userAccountControl", flags.ToString(System.Globalization.CultureInfo.InvariantCulture)) }));
            return;
        }

        var disabled = flags | AccountDisable;
        Step(item, "account", item.LiveDn!, $"disabled (userAccountControl {disabled}), must change the password at the next logon: no password was entered", write, log,
            () => _live.Modify(item.LiveDn!, new[]
            {
                new AdModification(AdModificationKind.Replace, "userAccountControl", disabled.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new AdModification(AdModificationKind.Replace, "pwdLastSet", "0"),
            }));
        item.Warnings.Add("The account is disabled until a password is set (it must be changed at the next logon).");
    }

    /// <summary>Adds the object to the groups it was a direct member of in the backup and is not now.</summary>
    private void AddMemberships(Work w, bool write, Action<string> log)
    {
        var item = w.Item;
        if (item.Action == AdRestoreActions.Reanimate && _recycleBin && item.State == AdObjectStates.Deleted)
        {
            // The Recycle Bin keeps the links; they come back with the object.
            return;
        }

        var current = item.NewGuid ?? item.Guid;
        foreach (var value in w.Backup.Values("memberOf").OfType<string>())
        {
            var (groupGuid, groupDn) = AdText.ParseExtendedDn(value);
            var liveGroup = groupGuid is { } g ? LiveDn(g) : null;
            if (liveGroup is null)
            {
                item.Warnings.Add($"Not added back to {groupDn}: the group is not in the live directory.");
                continue;
            }

            var group = Read(_live, groupGuid!.Value, false);
            if (group is not null && group.Values("member").OfType<string>().Any(m => AdText.ParseExtendedDn(m).Guid == current))
            {
                continue;
            }

            Step(item, "member", liveGroup, $"member: add \"{item.LiveDn}\"", write, log, () =>
            {
                try
                {
                    _live.Modify(liveGroup, new[] { new AdModification(AdModificationKind.Add, "member", item.LiveDn!) });
                }
                catch (AdDirectoryException ex) when (ex.ResultCode == 20)
                {
                    // attributeOrValueExists: it is a member already.
                }
            });
        }
    }

    private void MoveBack(Work w, AdJobRequest request, bool write, Action<string> log)
    {
        var item = w.Item;
        var rdn = Rdn(w.Backup.Dn);
        var parent = ParentDn(w.Backup) ?? request.TargetParentDn;
        if (parent is null)
        {
            item.Warnings.Add($"Not moved back: its container {AdText.Parent(w.Backup.Dn)} is not in the live directory.");
            return;
        }

        var target = rdn + "," + parent;
        if (string.Equals(target, item.LiveDn, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var source = item.LiveDn!;
        Step(item, "move", source, $"to {target}", write, log, () => _live.Move(source, rdn, parent));
        item.LiveDn = target;
        _liveDns[item.Guid] = target;
    }

    /// <summary>Writes the attributes that differ from the backup (plain ones, or links with their targets mapped by GUID).</summary>
    private void WriteAttributes(Work w, bool linked, bool write, Action<string> log)
    {
        var item = w.Item;
        var current = w.Current;
        var changes = new List<AdModification>();
        var texts = new List<string>();
        var selected = w.Choice.Attributes is { } chosen ? new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase) : null;
        var backup = Group(w.Backup);
        var live = current is null ? new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase) : Group(current);
        var accountLater = IsUser(w.Backup) && item.Action == AdRestoreActions.Reanimate && !(_recycleBin && item.State == AdObjectStates.Deleted);
        foreach (var name in backup.Keys.Union(live.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var info = _schema.Find(name);
            if (!Writable(name, info, linked) || (selected is not null && !selected.Contains(name)))
            {
                continue;
            }

            if (accountLater && string.Equals(name, "userAccountControl", StringComparison.OrdinalIgnoreCase))
            {
                // Without a password the account cannot be enabled: the account step decides.
                continue;
            }

            backup.TryGetValue(name, out var wanted);
            live.TryGetValue(name, out var now);
            wanted ??= new List<object>();
            now ??= new List<object>();
            if (Same(wanted, now))
            {
                continue;
            }

            if (linked)
            {
                var (adds, removes, missing) = LinkChanges(wanted, now);
                foreach (var target in missing)
                {
                    item.Warnings.Add($"{name}: {target} is not in the live directory; that value is not restored.");
                }

                if (adds.Count > 0)
                {
                    changes.Add(new AdModification(AdModificationKind.Add, name, adds.ToArray()));
                }

                if (removes.Count > 0)
                {
                    changes.Add(new AdModification(AdModificationKind.Delete, name, removes.ToArray()));
                }

                if (adds.Count + removes.Count > 0)
                {
                    texts.Add($"{name}: {(adds.Count > 0 ? $"add {Show(adds)}" : string.Empty)}{(adds.Count > 0 && removes.Count > 0 ? ", " : string.Empty)}{(removes.Count > 0 ? $"remove {Show(removes)}" : string.Empty)}");
                }

                continue;
            }

            changes.Add(wanted.Count == 0
                ? new AdModification(AdModificationKind.Delete, name)
                : new AdModification(AdModificationKind.Replace, name, wanted.ToArray()));
            texts.Add($"{name}: {Show(now)} → {Show(wanted)}");
        }

        if (changes.Count == 0)
        {
            return;
        }

        var dn = item.LiveDn!;
        Step(item, "modify", dn, string.Join("; ", texts), write, log, () => _live.Modify(dn, changes));
    }

    /// <summary>
    /// Links: the backup's targets that exist live (by GUID, or re-created in this run) and are missing, and live targets
    /// the backup did not have.
    /// </summary>
    private (List<object> Adds, List<object> Removes, List<string> Missing) LinkChanges(List<object> wanted, List<object> now)
    {
        var current = now.OfType<string>().Select(v => AdText.ParseExtendedDn(v)).ToList();
        var targets = new List<(Guid Guid, string Dn)>();
        var missing = new List<string>();
        foreach (var (guid, dn) in wanted.OfType<string>().Select(v => AdText.ParseExtendedDn(v)))
        {
            if (guid is { } g && LiveDn(g) is { } live)
            {
                targets.Add((g, live));
            }
            else
            {
                missing.Add(dn);
            }
        }

        bool Matches((Guid? Guid, string Dn) value, (Guid Guid, string Dn) target) =>
            value.Guid == target.Guid || string.Equals(value.Dn, target.Dn, StringComparison.OrdinalIgnoreCase);

        var adds = targets.Where(t => !current.Any(c => Matches(c, t))).Select(t => (object)t.Dn).ToList();
        var removes = current.Where(c => !targets.Any(t => Matches(c, t))).Select(c => (object)c.Dn).ToList();
        return (adds, removes, missing);
    }

    /// <summary>The backup's attributes a restore writes into a new object (plain ones or links, with the links' targets mapped).</summary>
    private IEnumerable<(string Name, List<object> Values)> Restorable(AdRecord backup, bool linked)
    {
        foreach (var pair in Group(backup))
        {
            var name = pair.Key;
            var values = pair.Value;
            if (!Writable(name, _schema.Find(name), linked) || values.Count == 0
                || string.Equals(name, "userAccountControl", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "primaryGroupID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!linked)
            {
                yield return (name, values);
                continue;
            }

            var mapped = values.OfType<string>().Select(v => AdText.ParseExtendedDn(v)).Select(v => v.Guid is { } g ? LiveDn(g) : null).Where(v => v is not null).Cast<object>().ToList();
            if (mapped.Count > 0)
            {
                yield return (name, mapped);
            }
        }
    }

    private static bool Writable(string name, AdAttributeInfo? info, bool linked)
    {
        if (string.Equals(name, "objectClass", StringComparison.OrdinalIgnoreCase) || AdCompare.Maintained.Contains(name))
        {
            return false;
        }

        var (restorable, _) = AdCompare.Restorable(name, info);
        return restorable && (info!.DisplaySyntax == "dn") == linked;
    }

    private static bool Same(List<object> a, List<object> b)
    {
        static string Key(object v) => v switch
        {
            string t when t.StartsWith("<GUID=", StringComparison.OrdinalIgnoreCase) => "g:" + AdText.ParseExtendedDn(t).Guid,
            string t => "s:" + t,
            byte[] bytes => "b:" + BitConverter.ToString(bytes),
            _ => string.Empty,
        };

        return a.Select(Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(b.Select(Key).OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static Dictionary<string, List<object>> Group(AdRecord record)
    {
        var grouped = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in record.Attributes)
        {
            var name = pair.Key.Split(';')[0];
            if (!grouped.TryGetValue(name, out var list))
            {
                grouped[name] = list = new List<object>();
            }

            list.AddRange(pair.Value);
        }

        return grouped;
    }

    /// <summary>Where a reanimated or re-created object goes: its old container (found by GUID), else the chosen one.</summary>
    private string? TargetDn(Work w, AdJobRequest request) =>
        (ParentDn(w.Backup) ?? request.TargetParentDn) is { } parent ? Rdn(w.Backup.Dn) + "," + parent : null;

    private string Unplaced(Work w) =>
        $"Its container {AdText.Parent(w.Backup.Dn)} is not in the live directory: restore it too, or choose a container for such objects.";

    /// <summary>The live DN of the backup object's parent (by parentGUID; restored in this run counts).</summary>
    private string? ParentDn(AdRecord backup) =>
        backup.Bytes("parentGUID") is { Length: 16 } bytes ? LiveDn(new Guid(bytes)) : null;

    /// <summary>The live DN of an object that is not deleted (or restored in this run), by its backup objectGUID.</summary>
    private string? LiveDn(Guid guid)
    {
        if (_restored.TryGetValue(guid, out var restored))
        {
            return restored;
        }

        if (!_liveDns.TryGetValue(guid, out var dn))
        {
            var record = Read(_live, guid, false);
            dn = record is null || record.Flag("isDeleted") ? null : record.Dn;
            _liveDns[guid] = dn;
        }

        return dn;
    }

    private AdRecord? Read(IAdDirectory directory, Guid guid, bool showDeleted) =>
        directory.Search(_domainDn, AdText.GuidFilter(guid), AdScope.Subtree, All, 1, null, showDeleted, extendedDn: true, fullValues: true).Records.FirstOrDefault();

    private AdRecord? FindByDn(string dn)
    {
        try
        {
            return _live.Search(dn, "(objectClass=*)", AdScope.Base, All, 1, null, extendedDn: true).Records.FirstOrDefault();
        }
        catch (AdDirectoryException ex) when (ex.ResultCode == 32)
        {
            return null;
        }
    }

    private static void Step(AdRestoreItem item, string operation, string target, string text, bool write, Action<string> log, Action action)
    {
        var step = new AdRestoreStep { Operation = operation, Target = target, Text = text };
        item.Steps.Add(step);
        if (!write)
        {
            return;
        }

        action();
        step.Done = true;
        log($"{operation} {target}: {text}");
    }

    private static void Guard(AdRestoreItem item, Action action)
    {
        try
        {
            action();
        }
        catch (Skip skip)
        {
            item.Outcome = AdRestoreOutcomes.Skipped;
            item.Reason = skip.Message;
        }
        catch (AdDirectoryException ex)
        {
            item.Outcome = AdRestoreOutcomes.Failed;
            item.Reason = ex.Message;
        }
    }

    private sealed class Work
    {
        public Work(AdRestoreObject choice, AdRecord backup, AdRecord? live, AdRestoreItem item)
        {
            Choice = choice;
            Backup = backup;
            Live = live;
            Item = item;
        }

        public AdRestoreObject Choice { get; }

        public AdRecord Backup { get; }

        public AdRecord? Live { get; }

        public AdRestoreItem Item { get; }

        /// <summary>The live object after the first pass (reanimated or re-created), for the attribute comparison.</summary>
        public AdRecord? Current { get; set; }
    }

    private sealed class Skip : Exception
    {
        public Skip(string message)
            : base(message)
        {
        }
    }
}
