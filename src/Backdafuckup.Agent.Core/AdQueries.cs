using System;
using System.Collections.Generic;
using System.Linq;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Read-only queries over a backup's directory (dsamain) and the live one: the backup decides what is listed, the live
/// directory (with deleted objects) gives each object its state. Searches are paged, so the agent never holds the whole
/// directory.
/// </summary>
public sealed class AdQueries
{
    private const int LookupBatch = 50;
    /// <summary>All attributes, and the constructed parentGUID: a move shows as a changed parent, not as a changed DN.</summary>
    private static readonly string[] All = { "*", "parentGUID" };

    private readonly IAdDirectory _backup;
    private readonly IAdDirectory _live;
    private readonly string _domainDn;

    public AdQueries(IAdDirectory backup, IAdDirectory live, string domainDn)
    {
        _backup = backup;
        _live = live;
        _domainDn = domainDn;
    }

    /// <summary>The containers and OUs one level below <paramref name="baseDn"/> (null: the domain).</summary>
    public List<AdEntry> Tree(string? baseDn)
    {
        const string filter = "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=builtinDomain)(objectClass=domainDNS)(objectClass=lostAndFound))";
        var records = new List<AdRecord>();
        byte[]? cookie = null;
        do
        {
            var page = _backup.Search(BaseDn(baseDn), filter, AdScope.OneLevel, All, 500, cookie, extendedDn: true);
            records.AddRange(page.Records);
            cookie = page.Cookie;
        }
        while (cookie is { Length: > 0 } && records.Count < 5000);

        return Classify(records).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>One page of objects in the backup that match; with a state filter, a page may hold fewer than its size.</summary>
    public (List<AdEntry> Entries, byte[]? Cookie, int Scanned) Search(AdJobRequest request)
    {
        var page = _backup.Search(BaseDn(request.BaseDn), Filter(request.Text, request.Classes), request.Subtree ? AdScope.Subtree : AdScope.OneLevel, All,
            Math.Max(1, Math.Min(request.PageSize, 1000)), string.IsNullOrEmpty(request.Cookie) ? null : Convert.FromBase64String(request.Cookie), extendedDn: true);
        var entries = Classify(page.Records);
        if (request.States.Count > 0)
        {
            entries = entries.Where(e => request.States.Contains(e.State, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return (entries, page.Cookie is { Length: > 0 } ? page.Cookie : null, page.Records.Count);
    }

    /// <summary>One object of the backup by objectGUID, next to its live state; throws if the backup does not have it.</summary>
    public AdObjectView Object(Guid guid, AdSchema schema)
    {
        var backup = _backup.Search(_domainDn, AdText.GuidFilter(guid), AdScope.Subtree, All, 1, null, extendedDn: true, fullValues: true).Records.FirstOrDefault()
            ?? throw new AdDirectoryException($"The backup's directory ({_backup.Endpoint}) has no object with objectGUID {guid} below {_domainDn}.", 32);
        var live = _live.Search(_domainDn, AdText.GuidFilter(guid), AdScope.Subtree, All, 1, null, showDeleted: true, extendedDn: true, fullValues: true).Records.FirstOrDefault();
        var view = AdCompare.View(backup, live, schema);

        // The parent must exist live for a restore into the old place: found by its objectGUID (it may have moved).
        if (view.ParentDn is { } parentDn)
        {
            var parent = _backup.Search(parentDn, "(objectClass=*)", AdScope.Base, new[] { "objectGUID" }, 1, null).Records.FirstOrDefault();
            if (parent?.Guid is { } parentGuid)
            {
                var liveParent = _live.Search(_domainDn, AdText.GuidFilter(parentGuid), AdScope.Subtree, new[] { "objectGUID", "isDeleted" }, 1, null, showDeleted: true).Records.FirstOrDefault();
                view.LiveParentDn = liveParent is null || liveParent.Flag("isDeleted") ? null : liveParent.Dn;
            }
        }

        return view;
    }

    /// <summary>
    /// Searches by name, sAMAccountName, display name or UPN and by class. The text is escaped (RFC 4515), so it matches
    /// literally, with a wildcard on both sides.
    /// </summary>
    public static string Filter(string? text, IReadOnlyCollection<string> classes)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var value = "*" + AdText.Escape(text!.Trim()) + "*";
            parts.Add($"(|(name={value})(sAMAccountName={value})(displayName={value})(userPrincipalName={value}))");
        }

        var classFilters = classes.Select(ClassFilter).Where(f => f is not null).ToList();
        if (classFilters.Count == 1)
        {
            parts.Add(classFilters[0]!);
        }
        else if (classFilters.Count > 1)
        {
            parts.Add("(|" + string.Concat(classFilters) + ")");
        }

        return parts.Count switch
        {
            0 => "(objectClass=*)",
            1 => parts[0],
            _ => "(&" + string.Concat(parts) + ")",
        };
    }

    private static string? ClassFilter(string objectClass) => objectClass switch
    {
        "user" => "(&(objectCategory=person)(objectClass=user))",
        "group" => "(objectClass=group)",
        "computer" => "(objectClass=computer)",
        "contact" => "(objectClass=contact)",
        "organizationalUnit" => "(objectClass=organizationalUnit)",
        "groupPolicyContainer" => "(objectClass=groupPolicyContainer)",
        _ => null,
    };

    private string BaseDn(string? baseDn) => string.IsNullOrEmpty(baseDn) ? _domainDn : baseDn!;

    /// <summary>Looks up the live counterparts by objectGUID (deleted ones too), a batch at a time.</summary>
    private List<AdEntry> Classify(List<AdRecord> records)
    {
        var live = new Dictionary<Guid, AdRecord>();
        foreach (var batch in records.Where(r => r.Guid is not null).Select((r, i) => (r, i)).GroupBy(x => x.i / LookupBatch, x => x.r))
        {
            var filter = "(|" + string.Concat(batch.Select(r => AdText.GuidFilter(r.Guid!.Value))) + ")";
            byte[]? cookie = null;
            do
            {
                var page = _live.Search(_domainDn, filter, AdScope.Subtree, All, LookupBatch, cookie, showDeleted: true, extendedDn: true);
                foreach (var record in page.Records)
                {
                    if (record.Guid is { } guid)
                    {
                        live[guid] = record;
                    }
                }

                cookie = page.Cookie;
            }
            while (cookie is { Length: > 0 });
        }

        return records.Where(r => r.Guid is not null).Select(r =>
        {
            live.TryGetValue(r.Guid!.Value, out var current);
            return new AdEntry
            {
                Guid = r.Guid!.Value,
                Dn = r.Dn,
                LiveDn = current?.Dn,
                Name = AdText.RdnValue(r.Dn),
                ObjectClass = r.ObjectClass,
                State = AdCompare.State(r, current),
                SamAccountName = r.Text("sAMAccountName"),
                Description = r.Text("description"),
                Container = r.Values("objectClass").OfType<string>().Any(c => c is "organizationalUnit" or "container" or "builtinDomain" or "domainDNS" or "lostAndFound"),
                ChangedInBackup = r.Text("whenChanged"),
                ChangedLive = current?.Text("whenChanged"),
            };
        }).ToList();
    }
}
