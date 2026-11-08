using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Restores group policies: the files (GPT) that the server copied from the restore point into a staging folder in
/// SYSVOL are swapped in (the current ones are set aside until the restore is done, and come back if it fails), the
/// container (GPC with its User and Machine containers) is restored like other objects, the version is raised in both
/// (each half to max(live, backup) + 1, so that clients apply the policy again and DFS-R replicates it), the DACL comes
/// from the backup, and the links the backup had are added back.
/// </summary>
public sealed class AdGpoRestorer
{
    private const string Staging = ".bdfu-new";
    private const string Replaced = ".bdfu-replaced";
    private static readonly Regex GpoName = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");
    private static readonly Regex VersionLine = new Regex(@"(?im)^(\s*Version\s*=\s*)\d+");
    private static readonly Regex General = new Regex(@"(?im)^([ \t]*\[General\][ \t]*)(\r?)$");
    private static readonly string[] All = { "*", "parentGUID" };

    private readonly IAdEnvironment _env;
    private readonly IAdDirectory _backup;
    private readonly IAdDirectory _live;
    private readonly string _domainDn;
    private readonly AdSchema _schema;
    private readonly bool _recycleBin;
    private readonly Action<string, bool> _track;

    /// <param name="track">Notes (true) or forgets (false) a staging or set-aside folder in the mount's journal.</param>
    public AdGpoRestorer(IAdEnvironment env, IAdDirectory backup, IAdDirectory live, string domainDn, AdSchema schema, bool recycleBin, Action<string, bool> track)
    {
        _env = env;
        _backup = backup;
        _live = live;
        _domainDn = domainDn;
        _schema = schema;
        _recycleBin = recycleBin;
        _track = track;
    }

    /// <summary>This DC's <c>SYSVOL\{domain}\Policies</c>.</summary>
    public string Policies => _env.Join(_env.Join(_env.SysvolRoot ?? throw new InvalidOperationException(
        $"The SYSVOL folder of {_env.HostName} is unknown (Netlogon\\Parameters\\SysVol): group policies cannot be restored there."), AdText.DnsName(_domainDn)), "Policies");

    /// <summary>The new version: user (high 16 bits) and computer (low 16 bits) each max(live, backup) + 1.</summary>
    public static int NextVersion(int backup, int live) =>
        ((Math.Max(backup >> 16, live >> 16) + 1) << 16) | (Math.Max(backup & 0xFFFF, live & 0xFFFF) + 1);

    /// <summary>GPT.INI with its Version line set (bytes kept as they are, ANSI or ASCII).</summary>
    public static byte[] WithVersion(byte[] content, int version)
    {
        var latin1 = Encoding.GetEncoding(28591);
        var text = latin1.GetString(content);
        var value = version.ToString(CultureInfo.InvariantCulture);
        text = VersionLine.IsMatch(text)
            ? VersionLine.Replace(text, m => m.Groups[1].Value + value, 1)
            : General.IsMatch(text)
                ? General.Replace(text, m => m.Groups[1].Value + "\r\nVersion=" + value + m.Groups[2].Value, 1)
                : "[General]\r\nVersion=" + value + "\r\n" + text;
        return latin1.GetBytes(text);
    }

    /// <summary>Creates an empty staging folder in SYSVOL for each policy (a leftover one is deleted first).</summary>
    public List<AdGpoStaging> Prepare(IEnumerable<AdGpoChoice> gpos, Action<string> log)
    {
        var result = new List<AdGpoStaging>();
        foreach (var choice in gpos)
        {
            var gpc = Read(_backup, choice.Guid, false) ?? throw new InvalidOperationException($"The backup's directory ({_backup.Endpoint}) has no group policy with objectGUID {choice.Guid}.");
            var name = Name(gpc);
            var staging = _env.Join(Policies, name + Staging);
            if (_env.FolderExists(staging))
            {
                _env.DeletePolicyFolder(staging);
            }

            _env.CreatePolicyFolder(staging);
            _track(staging, true);
            log($"Staging folder {staging} created for the files of {gpc.Text("displayName") ?? name}.");
            result.Add(new AdGpoStaging { Guid = choice.Guid, Name = name, DisplayName = gpc.Text("displayName"), Folder = staging });
        }

        return result;
    }

    public List<AdRestoreItem> Run(AdJobRequest request, bool write, Action<string> log)
    {
        var items = new List<AdRestoreItem>();
        foreach (var choice in request.Gpos.GroupBy(g => g.Guid).Select(g => g.First()))
        {
            var item = new AdRestoreItem { Guid = choice.Guid, Action = AdRestoreActions.Gpo, ObjectClass = "groupPolicyContainer" };
            items.Add(item);
            try
            {
                RestoreOne(choice, item, request, write, log);
                item.Outcome = write ? AdRestoreOutcomes.Done : AdRestoreOutcomes.Planned;
            }
            catch (Exception ex) when (ex is AdDirectoryException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                item.Outcome = AdRestoreOutcomes.Failed;
                item.Reason = ex.Message;
                log($"{item.Dn}: {ex.Message}");
            }
        }

        return items;
    }

    private void RestoreOne(AdGpoChoice choice, AdRestoreItem item, AdJobRequest request, bool write, Action<string> log)
    {
        var gpc = Read(_backup, choice.Guid, false) ?? throw new InvalidOperationException($"The backup's directory ({_backup.Endpoint}) has no group policy with objectGUID {choice.Guid}.");
        var name = Name(gpc);
        item.Dn = gpc.Dn;
        var liveGpc = Read(_live, choice.Guid, true);
        item.State = AdCompare.State(gpc, liveGpc);
        if (liveGpc is null && FindByName(name) is { } other)
        {
            throw new InvalidOperationException(
                $"A group policy named {name} exists at {other.Dn} with another objectGUID (re-created after the backup); restore its settings with the policy management tools.");
        }

        var backupVersion = Int(gpc.Text("versionNumber"));
        var liveVersion = liveGpc is not null && !liveGpc.Flag("isDeleted") ? Int(liveGpc.Text("versionNumber")) : 0;
        var version = NextVersion(backupVersion, liveVersion);
        var display = gpc.Text("displayName") ?? name;
        var folder = _env.Join(Policies, name);
        var staging = folder + Staging;
        var replaced = folder + Replaced;

        // 1. The files: swapped in from the staging folder; the current ones are set aside until the end.
        if (write)
        {
            if (!_env.FileExists(_env.Join(staging, "GPT.INI")))
            {
                throw new InvalidOperationException($"{staging} on {_env.HostName} has no GPT.INI: the files of {display} were not copied from the restore point.");
            }

            if (_env.FolderExists(replaced))
            {
                throw new InvalidOperationException($"{replaced} on {_env.HostName} is left from an earlier restore of {display}: check it, delete it, and restore again.");
            }
        }

        var swapped = false;
        var existed = _env.FolderExists(folder);
        try
        {
            Step(item, "files", folder, $"the files of \"{display}\" from the restore point (the current ones kept as {name}{Replaced} until the restore is done)", write, log, () =>
            {
                if (existed)
                {
                    _track(replaced, true);
                    _env.MovePolicyFolder(folder, replaced);
                }

                _env.MovePolicyFolder(staging, folder);
                _track(staging, false);
                swapped = true;
            });
            var gptIni = _env.Join(folder, "GPT.INI");
            Step(item, "version", gptIni, $"Version {liveVersion} → {version} (user {version >> 16}, computer {version & 0xFFFF})", write, log,
                () => _env.WriteFile(gptIni, WithVersion(_env.ReadFile(gptIni), version)));

            // 2. The container and its User and Machine containers, like other objects (a gone policy keeps its name).
            var children = _backup.Search(gpc.Dn, "(objectClass=*)", AdScope.OneLevel, All, 100, null).Records.Where(r => r.Guid is not null).ToList();
            var objects = new[] { gpc }.Concat(children).Select(r => new AdRestoreObject { Guid = r.Guid!.Value, Memberships = false }).ToList();
            var restored = new AdRestorer(_backup, _live, _domainDn, _schema, _recycleBin).Run(new AdJobRequest { Objects = objects, Recreate = true }, write, log);
            foreach (var sub in restored)
            {
                item.Steps.AddRange(sub.Steps);
                item.Warnings.AddRange(sub.Warnings.Where(w => !w.StartsWith("Re-created with a new objectGUID", StringComparison.Ordinal)));
                if (sub.Outcome == AdRestoreOutcomes.Failed)
                {
                    throw new InvalidOperationException($"{sub.Dn}: {sub.Reason}");
                }
            }

            var gpcDn = restored.First(r => r.Guid == choice.Guid).LiveDn ?? liveGpc?.Dn ?? gpc.Dn;
            item.LiveDn = gpcDn;
            if (restored.Any(r => r.Action == AdRestoreActions.Recreate))
            {
                item.Warnings.Add("The policy container was re-created: it has a new objectGUID, its name and links are the same.");
            }

            Step(item, "modify", gpcDn, $"versionNumber: {liveVersion} → {version}", write, log,
                () => _live.Modify(gpcDn, new[] { new AdModification(AdModificationKind.Replace, "versionNumber", version.ToString(CultureInfo.InvariantCulture)) }));

            // 3. Who may read and apply it: the DACL of the backup.
            var descriptor = _backup.ReadSecurityDescriptor(gpc.Dn);
            var current = write ? _live.ReadSecurityDescriptor(gpcDn) : liveGpc is not null && !liveGpc.Flag("isDeleted") ? _live.ReadSecurityDescriptor(liveGpc.Dn) : null;
            if (descriptor is not null && (current is null || !descriptor.SequenceEqual(current)))
            {
                Step(item, "acl", gpcDn, "permissions (DACL) from the backup", write, log, () => _live.WriteSecurityDescriptor(gpcDn, descriptor));
            }

            // 4. The links of the backup that are missing live.
            if (choice.Links)
            {
                AddLinks(item, name, write, log);
            }
        }
        catch when (write && swapped)
        {
            // Back as it was: the restored files go to the staging folder (deleted at unmount), the previous ones back.
            _env.MovePolicyFolder(folder, staging);
            _track(staging, true);
            if (_env.FolderExists(replaced))
            {
                _env.MovePolicyFolder(replaced, folder);
                _track(replaced, false);
            }

            log($"{folder}: the previous files are back in place, as the restore of {display} failed.");
            throw;
        }

        // 5. Done: the previous files go.
        if (existed)
        {
            Step(item, "files", replaced, "deleted (the previous files)", write, log, () =>
            {
                _env.DeletePolicyFolder(replaced);
                _track(replaced, false);
            });
        }
    }

    private void AddLinks(AdRestoreItem item, string name, bool write, Action<string> log)
    {
        var linked = _backup.Search(_domainDn, "(gPLink=*" + AdText.Escape(name) + "*)", AdScope.Subtree, All, 500, null).Records;
        foreach (var container in linked)
        {
            var segment = Segment(container.Text("gPLink") ?? string.Empty, name);
            if (segment is null || container.Guid is not { } guid)
            {
                continue;
            }

            var live = Read(_live, guid, false);
            if (live is null || live.Flag("isDeleted"))
            {
                item.Warnings.Add($"Not linked to {container.Dn} again: it is not in the live directory.");
                continue;
            }

            var links = live.Text("gPLink") ?? string.Empty;
            if (links.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                continue;
            }

            Step(item, "link", live.Dn, $"gPLink: add {segment}", write, log,
                () => _live.Modify(live.Dn, new[] { new AdModification(AdModificationKind.Replace, "gPLink", links + segment) }));
        }
    }

    /// <summary>The <c>[LDAP://cn={GUID},cn=policies,…;options]</c> part of a gPLink value that names the policy.</summary>
    private static string? Segment(string gpLink, string name)
    {
        var start = 0;
        while ((start = gpLink.IndexOf('[', start)) >= 0)
        {
            var end = gpLink.IndexOf(']', start);
            if (end < 0)
            {
                return null;
            }

            var part = gpLink.Substring(start, end - start + 1);
            if (part.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return part;
            }

            start = end + 1;
        }

        return null;
    }

    private static string Name(AdRecord gpc)
    {
        var name = gpc.Text("cn") ?? AdText.RdnValue(gpc.Dn);
        return GpoName.IsMatch(name) ? name : throw new InvalidOperationException($"{gpc.Dn} is not a group policy container (its name {name} is not a GUID).");
    }

    private AdRecord? FindByName(string name) =>
        _live.Search(_domainDn, "(&(objectClass=groupPolicyContainer)(cn=" + AdText.Escape(name) + "))", AdScope.Subtree, All, 1, null).Records.FirstOrDefault();

    private AdRecord? Read(IAdDirectory directory, Guid guid, bool showDeleted) =>
        directory.Search(_domainDn, AdText.GuidFilter(guid), AdScope.Subtree, All, 1, null, showDeleted, extendedDn: true).Records.FirstOrDefault();

    private static int Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

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
}
