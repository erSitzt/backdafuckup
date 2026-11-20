namespace Backdafuckup.Contracts;

/// <summary>A storage array for backups from storage snapshots.</summary>
/// <param name="Volumes">Volumes on which a datastore or SR of a connection lies.</param>
/// <param name="Readers">The experimental readers on this system: verify runs, format version, switched off after a mismatch.</param>
public sealed record StorageSystemDto(
    Guid Id, string Kind, string Name, string Address, int Port, string UserName, string Status, string? LastError, DateTimeOffset? LastCheckedUtc,
    StorageSystemFactsDto? Facts, StorageSystemOptionsDto Options, IReadOnlyList<string> PinnedSpki, int Volumes, int VolumesEnabled,
    IReadOnlyList<StorageReaderDto>? Readers = null);

/// <summary>
/// An experimental reader on a storage system. While <see cref="VerifiedRuns"/> is below <see cref="ProbationRuns"/>, backups
/// run in verify mode. <see cref="TrippedUtc"/> set: switched off after a mismatch until turned back on.
/// </summary>
public sealed record StorageReaderDto(
    string Reader, string Name, int VerifiedRuns, int ProbationRuns, string? FormatVersion, DateTimeOffset? TrippedUtc, string? TripReason);

/// <param name="LicenseWarning">Set when no snapshot feature is licensed or it has expired (a warning, nothing is blocked).</param>
public sealed record StorageSystemFactsDto(
    string Name, string Model, string Serial, string SystemWwn, string OsVersion, string ApiVersion, IReadOnlyList<StorageLicenseDto> Licenses,
    IReadOnlyList<string> Privileges, int? SessionTimeoutSeconds, string? LicenseWarning);

public sealed record StorageLicenseDto(string Name, DateTimeOffset? ExpiresUtc);

/// <param name="MinSecondsAfterVmSnapshot">The array snapshot waits at least this long after the batch's last VM snapshot.</param>
public sealed record StorageSystemOptionsDto(int ExpiryHours, int MaxVmsPerSnapshot, int BatchWindowSeconds, bool ExperimentalReaders, int ProbationRuns, int MinSecondsAfterVmSnapshot = 2);

public sealed record CreateStorageSystemDto
{
    /// <summary>"AlletraMp".</summary>
    public string Kind { get; init; } = "AlletraMp";

    public required string Name { get; init; }

    public required string Address { get; init; }

    public int Port { get; init; } = 443;

    public required string UserName { get; init; }

    public required string Password { get; init; }

    /// <summary>The certificates the administrator accepted after the probe (their public-key pins are trusted).</summary>
    public List<CertificateDto> TrustedCertificates { get; init; } = [];
}

/// <summary>A volume of the array with the datastore or SR of a connection on it.</summary>
/// <param name="ReplicationRole">"primary" or "secondary" for a volume replicated to another array (Peer Persistence), else null.</param>
public sealed record StorageVolumeDto(
    Guid Id, string VolumeUid, string VolumeName, string Wwn, long SizeBytes, Guid? ConnectionId, string? ConnectionName, string? StorageNativeId, string? StorageName,
    string Layout, bool Enabled, string? Refusal, DateTimeOffset UpdatedUtc, string? ReplicationRole = null);

public sealed record SetStorageVolumeDto(bool Enabled);

/// <summary>An object of this server on the array: a snapshot or a presentation, recorded in the ledger or left over.</summary>
public sealed record StorageLeftoverDto(string Kind, string NativeId, string Name, string? Detail, bool Recorded);

/// <summary>
/// How the backup proxies reach a storage system over iSCSI: per proxy, the host object that lists its initiator (the
/// array presents snapshots only to host objects). <see cref="Portals"/>: the array's iSCSI addresses the proxies log in
/// to. <see cref="Domain"/>: the domain a new host object goes into (that of the turned-on volumes; null: none).
/// <see cref="CreateRefusal"/>: why the backup server's account cannot create host objects (null: it can).
/// </summary>
public sealed record StorageProxyAccessDto(
    IReadOnlyList<StorageProxyHostDto> Proxies, IReadOnlyList<string> Portals, string? Domain, string? DomainNote, string? CreateRefusal, bool Replicated, string? Error = null);

/// <summary>A proxy and the host object listing its initiator on the array (<see cref="HostName"/> null: none).</summary>
public sealed record StorageProxyHostDto(
    Guid ProxyId, string ProxyName, bool Connected, string? InitiatorName, string? SanProblem, string? HostName, string? HostPersona, string? HostDomain);

/// <summary>A host object for a proxy, as an administrator chose it in the dialog (null: the proposed name or domain).</summary>
public sealed record StorageHostRequestDto(Guid ProxyId, string? Name = null, string? Domain = null);

/// <summary>
/// What creating a host object for a proxy does, before anything is done: the checks made on the array, and each change
/// as the API call the backup server sends and as the array's CLI line. <see cref="Refusal"/>: why it cannot be done
/// (nothing to do, a name taken, no initiator); <see cref="CanRun"/> also needs an account that may change the array.
/// </summary>
public sealed record StorageHostPlanDto(
    string ProxyName, string? InitiatorName, string Name, string? Domain, IReadOnlyList<string> Checks, IReadOnlyList<StorageArrayChangeDto> Changes, string? Refusal,
    bool CanRun, string? RunRefusal);

/// <summary>One change on the array: the API call as sent, and the CLI line that does the same (and the one that undoes it).</summary>
public sealed record StorageArrayChangeDto(string Title, string Method, string Path, string Body, string Cli, string UndoCli);

