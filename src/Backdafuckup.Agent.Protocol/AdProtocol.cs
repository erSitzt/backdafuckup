using System;
using System.Collections.Generic;

namespace Backdafuckup.Agent.Protocol;

/// <summary>What an Active Directory job does (<see cref="AdJobRequest.Kind"/>).</summary>
public static class AdJobKinds
{
    /// <summary>Creates the work folder for a backup's directory (ACL: SYSTEM and Administrators) and checks its free space.</summary>
    public const string Prepare = "prepare";

    /// <summary>Recovers the transferred ntds.dit in the work folder and serves it read-only with dsamain on a free port.</summary>
    public const string Mount = "mount";

    /// <summary>Stops dsamain and deletes the work folder (it holds every password hash of the domain).</summary>
    public const string Unmount = "unmount";

    /// <summary>The containers and OUs one level below <see cref="AdJobRequest.BaseDn"/>, with their state.</summary>
    public const string Tree = "tree";

    /// <summary>Objects of the backup that match the search, one page, with their state in the live directory.</summary>
    public const string Search = "search";

    /// <summary>One object: all attributes in the backup and live, their differences, and the memberships.</summary>
    public const string Object = "object";
}

/// <summary>An object of the backup compared with the live directory (matched by objectGUID).</summary>
public static class AdObjectStates
{
    /// <summary>Live, and not changed since the backup (its whenChanged is the same).</summary>
    public const string Unchanged = "unchanged";

    /// <summary>Live, and changed since the backup.</summary>
    public const string Changed = "changed";

    /// <summary>
    /// In Deleted Objects: with the Recycle Bin with all its attributes and links; without it a tombstone that kept only
    /// some attributes.
    /// </summary>
    public const string Deleted = "deleted";

    /// <summary>Recycled (Recycle Bin): a tombstone after the deleted-object lifetime; it can still be reanimated.</summary>
    public const string Recycled = "recycled";

    /// <summary>Not in the live directory at all (removed after the tombstone lifetime): only a re-creation brings it back.</summary>
    public const string Gone = "gone";
}

/// <summary>A Windows account for an Active Directory job. Sent only inside a job request, never stored by the agent.</summary>
public sealed class AdAccount
{
    /// <summary><c>DOMAIN\user</c> or <c>user@domain</c>.</summary>
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>What an Active Directory job asks of the agent (<c>GET api/agent/ad-jobs/{id}</c>).</summary>
public sealed class AdJobRequest
{
    public Guid JobId { get; set; }

    /// <summary>One of <see cref="AdJobKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The explore session; names the work folder (<c>%ProgramData%\backdafuckup\ad\{MountId}</c>) and the dsamain instance.</summary>
    public Guid MountId { get; set; }

    /// <summary>The account for LDAP to the backup (dsamain) and to the live directory. Not needed by prepare and unmount.</summary>
    public AdAccount? Account { get; set; }

    /// <summary>Prepare: the size of the files that will be transferred (ntds.dit and logs).</summary>
    public long RequiredBytes { get; set; }

    /// <summary>Mount: the agent unmounts by itself when no job used the mount for this long (the server's idle timeout plus a margin).</summary>
    public int IdleMinutes { get; set; } = 75;

    /// <summary>Tree and search: where to look; null: the domain.</summary>
    public string? BaseDn { get; set; }

    /// <summary>Search: part of the name, sAMAccountName, display name or UPN; null or empty: everything.</summary>
    public string? Text { get; set; }

    /// <summary>Search: <c>user</c>, <c>group</c>, <c>computer</c>, <c>contact</c>, <c>organizationalUnit</c>, <c>groupPolicyContainer</c>; empty: all.</summary>
    public List<string> Classes { get; set; } = new List<string>();

    /// <summary>Search: only objects in these <see cref="AdObjectStates"/>; empty: all.</summary>
    public List<string> States { get; set; } = new List<string>();

    /// <summary>Search: the whole subtree below <see cref="BaseDn"/> (default) or one level.</summary>
    public bool Subtree { get; set; } = true;

    public int PageSize { get; set; } = 200;

    /// <summary>Search: the paging cookie of the previous page (base64); null: the first page.</summary>
    public string? Cookie { get; set; }

    /// <summary>Object: the objectGUID.</summary>
    public Guid ObjectGuid { get; set; }
}

/// <summary>The outcome of an Active Directory job (<c>POST api/agent/ad-jobs/{id}/result</c>).</summary>
public sealed class AdJobResult
{
    public Guid JobId { get; set; }

    public bool Success { get; set; }

    /// <summary>What failed, naming the DC (host, port), the DN and the LDAP result where it applies.</summary>
    public string? Error { get; set; }

    /// <summary>Lines for the session log (never a password).</summary>
    public List<string> Log { get; set; } = new List<string>();

    /// <summary>The session is not mounted on this DC (unmounted after its idle time, or the agent restarted).</summary>
    public bool NotMounted { get; set; }

    public AdPrepareInfo? Prepare { get; set; }

    public AdMountInfo? Mount { get; set; }

    /// <summary>Tree and search: the entries of this page.</summary>
    public List<AdEntry> Entries { get; set; } = new List<AdEntry>();

    /// <summary>Search: the cookie for the next page; null: this was the last page.</summary>
    public string? Cookie { get; set; }

    /// <summary>Search: how many objects of the backup this page looked at (the state filter may have dropped some).</summary>
    public int Scanned { get; set; }

    public AdObjectView? Object { get; set; }
}

public sealed class AdPrepareInfo
{
    /// <summary>The work folder, empty, with its ACL set.</summary>
    public string Folder { get; set; } = string.Empty;

    public long FreeBytes { get; set; }
}

/// <summary>The backup's directory, served by dsamain.</summary>
public sealed class AdMountInfo
{
    public int Port { get; set; }

    public int ProcessId { get; set; }

    /// <summary>The DC this agent runs on (for log lines and the confirmation).</summary>
    public string LiveHost { get; set; } = string.Empty;

    /// <summary>The domain's DN in the backup and live.</summary>
    public string? BackupDomainDn { get; set; }

    public string? LiveDomainDn { get; set; }

    /// <summary>The live directory: Recycle Bin enabled, tombstone and deleted-object lifetimes (days).</summary>
    public bool RecycleBin { get; set; }

    public int? TombstoneLifetimeDays { get; set; }

    public int? DeletedObjectLifetimeDays { get; set; }

    /// <summary>What the ESE recovery of the copied database reported.</summary>
    public string? Recovery { get; set; }
}

/// <summary>An object in a tree or search result.</summary>
public sealed class AdEntry
{
    public Guid Guid { get; set; }

    /// <summary>Its DN in the backup.</summary>
    public string Dn { get; set; } = string.Empty;

    /// <summary>Its DN in the live directory (in Deleted Objects for deleted objects); null if gone.</summary>
    public string? LiveDn { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The most specific object class, e.g. <c>user</c>, <c>group</c>, <c>organizationalUnit</c>.</summary>
    public string ObjectClass { get; set; } = string.Empty;

    /// <summary>One of <see cref="AdObjectStates"/>.</summary>
    public string State { get; set; } = AdObjectStates.Unchanged;

    public string? SamAccountName { get; set; }

    public string? Description { get; set; }

    /// <summary>Can hold other objects (OU, container, domain).</summary>
    public bool Container { get; set; }

    /// <summary>whenChanged in the backup and live (generalized time, UTC).</summary>
    public string? ChangedInBackup { get; set; }

    public string? ChangedLive { get; set; }
}

/// <summary>One object of the backup next to its live state.</summary>
public sealed class AdObjectView
{
    public Guid Guid { get; set; }

    public string Dn { get; set; } = string.Empty;

    public string? LiveDn { get; set; }

    public string ObjectClass { get; set; } = string.Empty;

    /// <summary>One of <see cref="AdObjectStates"/>.</summary>
    public string State { get; set; } = AdObjectStates.Unchanged;

    /// <summary>objectSid in string form (S-1-5-21-…), if the object has one.</summary>
    public string? Sid { get; set; }

    /// <summary>The DN of the parent in the backup, and whether it exists live (by its objectGUID; a restore needs it).</summary>
    public string? ParentDn { get; set; }

    public string? LiveParentDn { get; set; }

    /// <summary>The attributes of both sides, ordered by name.</summary>
    public List<AdAttribute> Attributes { get; set; } = new List<AdAttribute>();

    /// <summary>The groups the object is a direct member of (back link <c>memberOf</c>, same domain).</summary>
    public List<AdValue> MemberOfBackup { get; set; } = new List<AdValue>();

    public List<AdValue> MemberOfLive { get; set; } = new List<AdValue>();
}

/// <summary>An attribute of an object in the backup and live.</summary>
public sealed class AdAttribute
{
    public string Name { get; set; } = string.Empty;

    /// <summary><c>string</c>, <c>dn</c>, <c>int</c>, <c>bool</c>, <c>time</c>, <c>guid</c>, <c>sid</c>, <c>binary</c>.</summary>
    public string Syntax { get; set; } = "string";

    public List<AdValue> Backup { get; set; } = new List<AdValue>();

    public List<AdValue> Live { get; set; } = new List<AdValue>();

    /// <summary>The values differ (DN values by the GUID of the object they point to).</summary>
    public bool Changed { get; set; }

    /// <summary>A restore can write it back (not system-only, constructed, a back link, a secret or an identity).</summary>
    public bool Restorable { get; set; }

    /// <summary>Why it cannot be restored, or how (e.g. "back link: restored through the group's member").</summary>
    public string? Note { get; set; }

    /// <summary>A linked attribute (its values point to other objects).</summary>
    public bool Linked { get; set; }
}

/// <summary>One value as text; DN values also carry the objectGUID of the object they point to.</summary>
public sealed class AdValue
{
    public string Text { get; set; } = string.Empty;

    public Guid? Guid { get; set; }
}
