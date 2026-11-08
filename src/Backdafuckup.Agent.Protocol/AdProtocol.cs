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

    /// <summary>What a restore of <see cref="AdJobRequest.Objects"/> would write, step by step; writes nothing.</summary>
    public const string Plan = "plan";

    /// <summary>Restores <see cref="AdJobRequest.Objects"/> into the live directory (reanimate, re-create, write attributes).</summary>
    public const string Restore = "restore";

    /// <summary>
    /// Creates an empty staging folder <c>Policies\{GPO}.bdfu-new</c> in this DC's SYSVOL for each of
    /// <see cref="AdJobRequest.Gpos"/>; the server copies the backup's GPT into it, the restore swaps it in.
    /// </summary>
    public const string GpoPrepare = "gpo-prepare";
}

/// <summary>A group policy to restore: its container (GPC) by objectGUID in the backup, its files (GPT) from the backup's SYSVOL.</summary>
public sealed class AdGpoChoice
{
    /// <summary>The objectGUID of the groupPolicyContainer in the backup.</summary>
    public Guid Guid { get; set; }

    /// <summary>The links (gPLink of the domain and the OUs) that the backup had and the live directory lacks are added back.</summary>
    public bool Links { get; set; } = true;
}

/// <summary>Where the server copies a GPO's files to.</summary>
public sealed class AdGpoStaging
{
    public Guid Guid { get; set; }

    /// <summary>The GPO's name in SYSVOL and AD, e.g. <c>{31B2F340-016D-11D2-945F-00C04FB984F9}</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>The empty staging folder on the DC.</summary>
    public string Folder { get; set; } = string.Empty;
}

/// <summary>What a restore does with an object (<see cref="AdRestoreItem.Action"/>).</summary>
public static class AdRestoreActions
{
    /// <summary>Brings a deleted or recycled object back from Deleted Objects (same objectGUID and SID).</summary>
    public const string Reanimate = "reanimate";

    /// <summary>Creates the object anew from the backup (new objectGUID and SID).</summary>
    public const string Recreate = "recreate";

    /// <summary>Writes attributes of a live object back (and moves or renames it, if chosen).</summary>
    public const string Modify = "modify";

    /// <summary>A group policy: its files (GPT) swapped in, its container (GPC) restored, versions raised, links added.</summary>
    public const string Gpo = "gpo";

    /// <summary>Nothing to restore.</summary>
    public const string None = "none";
}

/// <summary>The outcome of an object in a restore (<see cref="AdRestoreItem.Outcome"/>).</summary>
public static class AdRestoreOutcomes
{
    public const string Planned = "planned";

    public const string Done = "done";

    public const string Skipped = "skipped";

    public const string Failed = "failed";
}

/// <summary>An object chosen for a restore.</summary>
public sealed class AdRestoreObject
{
    public Guid Guid { get; set; }

    /// <summary>The attributes to write back; null: every restorable attribute that differs from the backup.</summary>
    public List<string>? Attributes { get; set; }

    /// <summary>A live object that was moved or renamed goes back into the backup's container and name.</summary>
    public bool MoveBack { get; set; }

    /// <summary>The object is added back to the groups it was a direct member of in the backup.</summary>
    public bool Memberships { get; set; } = true;
}

/// <summary>An object of a restore: what was planned or done, step by step.</summary>
public sealed class AdRestoreItem
{
    public Guid Guid { get; set; }

    /// <summary>Its DN in the backup.</summary>
    public string Dn { get; set; } = string.Empty;

    public string ObjectClass { get; set; } = string.Empty;

    /// <summary>Its state before the restore (<see cref="AdObjectStates"/>).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>One of <see cref="AdRestoreActions"/>.</summary>
    public string Action { get; set; } = AdRestoreActions.None;

    /// <summary>One of <see cref="AdRestoreOutcomes"/>.</summary>
    public string Outcome { get; set; } = AdRestoreOutcomes.Planned;

    /// <summary>Why it was skipped or failed (with the DC, the DN and the LDAP result).</summary>
    public string? Reason { get; set; }

    /// <summary>Its DN in the live directory after the restore.</summary>
    public string? LiveDn { get; set; }

    /// <summary>A re-created object's new objectGUID.</summary>
    public Guid? NewGuid { get; set; }

    public List<AdRestoreStep> Steps { get; set; } = new List<AdRestoreStep>();

    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>One write of a restore.</summary>
public sealed class AdRestoreStep
{
    /// <summary><c>reanimate</c>, <c>add</c>, <c>modify</c>, <c>move</c>, <c>member</c>, <c>password</c>, <c>account</c>.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>The DN the operation writes.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>What it writes, e.g. <c>description: "Marketing" → "Sales"</c> (never a password).</summary>
    public string Text { get; set; } = string.Empty;

    public bool Done { get; set; }
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

    /// <summary>Plan and restore: the objects, restored parents first.</summary>
    public List<AdRestoreObject> Objects { get; set; } = new List<AdRestoreObject>();

    /// <summary>Plan and restore: objects that are gone are created anew (new objectGUID and SID).</summary>
    public bool Recreate { get; set; }

    /// <summary>
    /// Plan and restore: where objects go whose container is not in the live directory (and not restored with them);
    /// null: they are skipped.
    /// </summary>
    public string? TargetParentDn { get; set; }

    /// <summary>
    /// Restore: the password for user accounts that come back without one (tombstone or re-created). Null: they stay
    /// disabled and must change the password at the next logon. Never logged or stored.
    /// </summary>
    public string? NewPassword { get; set; }

    /// <summary>Plan: a password will be entered for accounts without one (the preview shows those steps without it).</summary>
    public bool PasswordEntered { get; set; }

    /// <summary>Gpo-prepare, plan and restore: the group policies.</summary>
    public List<AdGpoChoice> Gpos { get; set; } = new List<AdGpoChoice>();
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

    /// <summary>Plan and restore: the objects with their steps and outcomes.</summary>
    public List<AdRestoreItem> Restore { get; set; } = new List<AdRestoreItem>();

    /// <summary>Gpo-prepare: the staging folders.</summary>
    public List<AdGpoStaging> GpoStaging { get; set; } = new List<AdGpoStaging>();
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

    /// <summary>displayName in the backup (e.g. a group policy's name).</summary>
    public string? DisplayName { get; set; }

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
