namespace Backdafuckup.Contracts;

/// <summary>A domain controller that can open a restore point's directory (its agent runs dsamain).</summary>
/// <param name="Usable">Online, with the capability, and of the restore point's domain.</param>
/// <param name="Reason">Why it cannot be used.</param>
public sealed record AdTargetDto(Guid AgentId, string HostName, string? Address, string? Domain, bool Online, bool Usable, string? Reason);

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
