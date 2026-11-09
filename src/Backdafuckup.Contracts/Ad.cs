namespace Backdafuckup.Contracts;

/// <summary>A domain controller that can open a restore point's directory (its agent runs dsamain).</summary>
/// <param name="Usable">Online, with the capability, and of the restore point's domain.</param>
/// <param name="Reason">Why it cannot be used.</param>
/// <param name="BackupSource">The DC the restore point is from: it can always open its own backup, also on a VM with a VM generation id.</param>
public sealed record AdTargetDto(Guid AgentId, string HostName, string? Address, string? Domain, bool Online, bool Usable, string? Reason, bool BackupSource = false);

/// <summary>Opens a restore point's directory on a live DC of the same domain, as a Windows account of the domain.</summary>
public sealed record OpenAdExploreDto(Guid RestorePointId, Guid AgentId, Guid CredentialId);

/// <summary>States of an AD explore session.</summary>
public static class AdExploreStates
{
    /// <summary>The backup's directory is being copied to the DC and mounted (see <see cref="AdExploreDto.Step"/>).</summary>
    public const string Opening = "Opening";

    public const string Ready = "Ready";

    public const string Failed = "Failed";

    public const string Closed = "Closed";
}

public sealed record AdExploreDto(
    Guid Id,
    Guid SessionId,
    Guid RestorePointId,
    string VmName,
    DateTimeOffset RestorePointCreatedUtc,
    string? Domain,
    string TargetHost,
    string Account,
    string State,
    string? Step,
    long TransferredBytes,
    long TotalBytes,
    string? Error,
    AdMountDto? Mount,
    DateTimeOffset OpenedUtc,
    int IdleMinutes);

/// <summary>The mounted backup and the live directory's settings.</summary>
public sealed record AdMountDto(string? DomainDn, string LiveHost, bool RecycleBin, int? TombstoneLifetimeDays, int? DeletedObjectLifetimeDays, string? Recovery);

/// <summary>States of an object of the backup in the live directory: unchanged, changed, deleted, recycled, gone.</summary>
public sealed record AdEntryDto(
    Guid Guid,
    string Dn,
    string? LiveDn,
    string Name,
    string ObjectClass,
    string State,
    string? SamAccountName,
    string? DisplayName,
    string? Description,
    bool Container,
    string? ChangedInBackup,
    string? ChangedLive);

/// <param name="Classes">user, group, computer, contact, organizationalUnit, groupPolicyContainer; empty: all.</param>
/// <param name="States">Only these states; empty: all.</param>
/// <param name="Cookie">The previous page's cookie; null: the first page.</param>
public sealed record AdSearchDto(string? BaseDn = null, string? Text = null, List<string>? Classes = null, List<string>? States = null, bool Subtree = true, int PageSize = 200, string? Cookie = null);

/// <param name="Cookie">For the next page; null: the last page.</param>
/// <param name="Scanned">Objects of the backup this page looked at (a state filter may have dropped some).</param>
public sealed record AdSearchPageDto(List<AdEntryDto> Entries, string? Cookie, int Scanned);

public sealed record AdValueDto(string Text, Guid? Guid);

public sealed record AdAttributeDto(string Name, string Syntax, List<AdValueDto> Backup, List<AdValueDto> Live, bool Changed, bool Restorable, string? Note, bool Linked);

public sealed record AdObjectDto(
    Guid Guid,
    string Dn,
    string? LiveDn,
    string ObjectClass,
    string State,
    string? Sid,
    string? ParentDn,
    string? LiveParentDn,
    List<AdAttributeDto> Attributes,
    List<AdValueDto> MemberOfBackup,
    List<AdValueDto> MemberOfLive);

/// <summary>An object to restore: null attributes means every restorable one that differs from the backup.</summary>
public sealed record AdRestoreObjectDto(Guid Guid, List<string>? Attributes = null, bool MoveBack = false, bool Memberships = true);

/// <param name="Recreate">Objects that are gone are created anew (new objectGUID and SID).</param>
/// <param name="TargetParentDn">Where objects go whose container is not in the live directory; null: they are skipped.</param>
/// <param name="NewPassword">For accounts that come back without a password; null: they stay disabled (change at next logon). Never stored.</param>
/// <param name="ConfirmDomain">Restore only: the live domain's DNS name, typed by the user as confirmation.</param>
/// <param name="Gpos">Group policies: their files (GPT) and container (GPC), versions raised, links optional.</param>
/// <param name="PasswordEntered">Preview: a password will be entered (the preview shows those steps without it).</param>
public sealed record AdRestoreRequestDto(
    List<AdRestoreObjectDto> Objects, bool Recreate = false, string? TargetParentDn = null, string? NewPassword = null, string? ConfirmDomain = null,
    List<AdGpoRestoreDto>? Gpos = null, bool PasswordEntered = false);

/// <param name="Guid">The objectGUID of the groupPolicyContainer in the backup.</param>
/// <param name="Links">Add back the links (gPLink of the domain and OUs) the backup had.</param>
public sealed record AdGpoRestoreDto(Guid Guid, bool Links = true);

public sealed record AdRestoreStepDto(string Operation, string Target, string Text, bool Done);

/// <param name="Action">reanimate, recreate, modify, none.</param>
/// <param name="Outcome">planned, done, skipped, failed.</param>
public sealed record AdRestoreItemDto(
    Guid Guid, string Dn, string ObjectClass, string State, string Action, string Outcome, string? Reason, string? LiveDn, Guid? NewGuid,
    List<AdRestoreStepDto> Steps, List<string> Warnings);

/// <summary>What a restore would do (the preview), and where: the live DC and domain the confirmation names.</summary>
public sealed record AdRestorePlanDto(string LiveHost, string? LiveDomain, List<AdRestoreItemDto> Items);
