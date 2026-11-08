using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>Compares an object of the backup with the live one (matched by objectGUID), attribute by attribute.</summary>
public static class AdCompare
{
    /// <summary>Attributes the DCs maintain by themselves (logons, USNs, replication data), or that the state already shows.</summary>
    internal static readonly HashSet<string> Maintained = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "whenChanged", "uSNChanged", "uSNCreated", "dSCorePropagationData", "lastLogon", "lastLogonTimestamp", "logonCount",
        "badPwdCount", "badPasswordTime", "lastLogoff", "replPropertyMetaData", "replUpToDateVector", "isDeleted", "isRecycled",
        "lastKnownParent", "msDS-LastKnownRDN", "objectGUID", "instanceType", "msDS-RevealedDSAs", "msDS-FailedInteractiveLogonCount",
        "msDS-FailedInteractiveLogonCountAtLastSuccessfulLogon", "msDS-LastSuccessfulInteractiveLogonTime", "msDS-LastFailedInteractiveLogonTime",
    };

    /// <summary>Never written by a restore: the object's identity, its name and place, and what only the system sets.</summary>
    private static readonly Dictionary<string, string> NotRestored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["objectGUID"] = "identity",
        ["objectSid"] = "identity",
        ["sIDHistory"] = "identity (not restored)",
        ["distinguishedName"] = "name and place: restored by renaming or moving the object back",
        ["parentGUID"] = "place: restored by moving the object back into this container",
        ["name"] = "name and place: restored by renaming the object",
        ["cn"] = "name and place: restored by renaming the object",
        ["ou"] = "name and place: restored by renaming the object",
        ["objectClass"] = "set when the object is created",
        ["objectCategory"] = "set when the object is created",
        ["whenCreated"] = "maintained by the domain controllers",
        ["sAMAccountType"] = "maintained by the domain controllers",
        ["pwdLastSet"] = "password: a restore sets a new one or requires a change at the next logon",
        ["unicodePwd"] = "password",
        ["isCriticalSystemObject"] = "system object flag",
        ["systemFlags"] = "system object flag",
        ["memberOf"] = "back link: restored through the groups' member attribute",
        ["msDS-KeyVersionNumber"] = "maintained by the domain controllers",
    };

    /// <summary>Integer attributes that hold a FILETIME.</summary>
    private static readonly HashSet<string> FileTimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "pwdLastSet", "accountExpires", "lastLogon", "lastLogonTimestamp", "badPasswordTime", "lastLogoff", "lockoutTime",
    };

    /// <summary>The state of a backup object given its live counterpart (null: none with this objectGUID, even deleted).</summary>
    public static string State(AdRecord backup, AdRecord? live) =>
        live is null ? AdObjectStates.Gone
        : live.Flag("isRecycled") ? AdObjectStates.Recycled
        : live.Flag("isDeleted") ? AdObjectStates.Deleted
        : Differs(backup, live) ? AdObjectStates.Changed
        : AdObjectStates.Unchanged;

    /// <summary>Whether any attribute that is not maintained by the DCs differs (DN values by the GUID they point to).</summary>
    public static bool Differs(AdRecord backup, AdRecord live) => ChangedAttributes(backup, live).Any();

    public static IEnumerable<string> ChangedAttributes(AdRecord backup, AdRecord live)
    {
        var a = Group(backup);
        var b = Group(live);
        // The DN follows from the name and the parent (parentGUID): renaming a parent OU changes no object below it.
        foreach (var name in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase)
                     .Where(n => !Maintained.Contains(n) && !string.Equals(n, "distinguishedName", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            a.TryGetValue(name, out var left);
            b.TryGetValue(name, out var right);
            if (!SameValues(left, right))
            {
                yield return name;
            }
        }
    }

    /// <summary>The whole object view: every attribute of both sides with syntax, difference and whether a restore can write it.</summary>
    public static AdObjectView View(AdRecord backup, AdRecord? live, AdSchema schema)
    {
        var view = new AdObjectView
        {
            Guid = backup.Guid ?? Guid.Empty,
            Dn = backup.Dn,
            LiveDn = live?.Dn,
            ObjectClass = backup.ObjectClass,
            State = State(backup, live),
            Sid = backup.Bytes("objectSid") is { } sid ? AdText.Sid(sid) : null,
            ParentDn = AdText.Parent(backup.Dn),
        };
        var left = Group(backup);
        var right = live is null ? new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase) : Group(live);
        view.MemberOfBackup = Values(left, "memberOf", "dn", "memberOf");
        view.MemberOfLive = Values(right, "memberOf", "dn", "memberOf");
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(name, "memberOf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = schema.Find(name);
            var syntax = name.Equals("objectGUID", StringComparison.OrdinalIgnoreCase) || name.Equals("parentGUID", StringComparison.OrdinalIgnoreCase)
                ? "guid"
                : info?.DisplaySyntax ?? "string";
            var derived = name.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase);
            left.TryGetValue(name, out var a);
            right.TryGetValue(name, out var b);
            var (restorable, note) = Restorable(name, info);
            var maintained = Maintained.Contains(name);
            view.Attributes.Add(new AdAttribute
            {
                Name = name,
                Syntax = syntax,
                Backup = Values(left, name, syntax, name),
                Live = Values(right, name, syntax, name),
                Changed = !maintained && !derived && live is not null && !SameValues(a, b),
                Restorable = restorable && !maintained,
                Note = maintained ? "maintained by the domain controllers" : note,
                Linked = info?.LinkId > 0,
            });
        }

        return view;
    }

    /// <summary>Whether a restore may write the attribute, and if not, why.</summary>
    public static (bool Restorable, string? Note) Restorable(string name, AdAttributeInfo? info)
    {
        if (NotRestored.TryGetValue(name, out var reason))
        {
            return (false, reason);
        }

        if (info is null)
        {
            return (false, "not in the schema of the live directory");
        }

        if (info.BackLink)
        {
            return (false, "back link: restored through the attribute that links to it");
        }

        if (info.Constructed)
        {
            return (false, "constructed by the domain controller");
        }

        if (info.SystemOnly)
        {
            return (false, "system only");
        }

        return (true, info.ForwardLink ? "link: the values point to objects by their objectGUID" : null);
    }

    /// <summary>The attributes by name without range suffix (<c>member;range=0-1499</c> → <c>member</c>).</summary>
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

    private static bool SameValues(List<object>? a, List<object>? b)
    {
        var left = (a ?? new List<object>()).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var right = (b ?? new List<object>()).Select(Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    /// <summary>The value's identity for comparing: DN values by GUID, binary values by their bytes.</summary>
    private static string Key(object value) => value switch
    {
        string text when text.StartsWith("<GUID=", StringComparison.OrdinalIgnoreCase) && AdText.ParseExtendedDn(text) is { Guid: { } guid } => "guid:" + guid.ToString("D"),
        string text => "s:" + text,
        byte[] bytes => "b:" + BitConverter.ToString(bytes),
        _ => string.Empty,
    };

    private static List<AdValue> Values(Dictionary<string, List<object>> attributes, string name, string syntax, string attribute)
    {
        if (!attributes.TryGetValue(name, out var values))
        {
            return new List<AdValue>();
        }

        return values.Select(v => Format(attribute, syntax, v)).OrderBy(v => v.Text, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AdValue Format(string attribute, string syntax, object value)
    {
        if (value is string text)
        {
            if (text.StartsWith("<GUID=", StringComparison.OrdinalIgnoreCase))
            {
                var (guid, dn) = AdText.ParseExtendedDn(text);
                return new AdValue { Text = dn, Guid = guid };
            }

            return new AdValue { Text = syntax == "int" && FileTimes.Contains(attribute) ? AdText.FileTime(text) : text };
        }

        var bytes = (byte[])value;
        return syntax switch
        {
            "guid" when bytes.Length == 16 => new AdValue { Text = new Guid(bytes).ToString("D") },
            "sid" => new AdValue { Text = AdText.Sid(bytes) ?? Hex(bytes) },
            "binary" => new AdValue { Text = Binary(bytes) },
            _ => new AdValue { Text = Encoding.UTF8.GetString(bytes) },
        };
    }

    private static string Binary(byte[] bytes)
    {
        if (bytes.Length <= 32)
        {
            return Hex(bytes);
        }

        using (var sha = SHA256.Create())
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} bytes, SHA-256 {1}…", bytes.Length, Hex(sha.ComputeHash(bytes)).Substring(0, 16));
        }
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
}
