namespace Backdafuckup.Contracts;

public static class ApiHeaders
{
    /// <summary>Required on every state-changing API request (CSRF protection; cannot be set cross-site without CORS).</summary>
    public const string Csrf = "X-BDFU-Request";
}

// ---- Auth / setup ----
public sealed record SetupStatusDto(bool SetupRequired);

public sealed record SetupRequestDto(string SetupToken, string UserName, string Password);

public sealed record LoginRequestDto(string UserName, string Password);

public sealed record CurrentUserDto(string UserName, IReadOnlyList<string> Roles);

/// <summary>What an API token may do.</summary>
public static class ApiTokenScopes
{
    /// <summary>Everything its user may do.</summary>
    public const string Full = "full";

    /// <summary>Read the Prometheus metrics at <c>/metrics</c>, nothing else (the scrape token).</summary>
    public const string Metrics = "metrics";
}

/// <summary>An API token as listed (never the token itself). <see cref="Scope"/>: one of <see cref="ApiTokenScopes"/>.</summary>
public sealed record ApiTokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedUtc, DateTimeOffset? ExpiresUtc, DateTimeOffset? LastUsedUtc, string? LastUsedFrom, string Scope = ApiTokenScopes.Full);

/// <summary>A new API token: a name, the days until it expires (null: it does not expire), and its scope (null: full).</summary>
public sealed record CreateApiTokenDto(string Name, int? ExpiresInDays, string? Scope = null);

/// <summary>The created token: <see cref="Token"/> is shown this once and stored only as a hash.</summary>
public sealed record CreatedApiTokenDto(ApiTokenDto Info, string Token);

/// <summary>The running server: product version (e.g. 0.3.0), the Git commit it was built from, and when.</summary>
public sealed record AboutDto(string Version, string? Commit, DateTimeOffset? BuildUtc);

// ---- Connections (pools) ----
public sealed record CertificateDto(
    string HostAddress,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string SpkiSha256,
    string Sha256Thumbprint,
    bool ValidAgainstSystemCa);

/// <param name="Kind">"XenServer" (default) or "Kubernetes".</param>
public sealed record ProbeRequestDto(string Address, int Port = 443, string Kind = "XenServer");

public sealed record CreateConnectionDto
{
    /// <summary>"XenServer" (default): a pool, with a user name and password. "Kubernetes": a cluster, with a ServiceAccount token as the password.</summary>
    public string Kind { get; init; } = "XenServer";

    public required string DisplayName { get; init; }

    public required string Address { get; init; }

    public int Port { get; init; } = 443;

    public required string UserName { get; init; }

    public required string Password { get; init; }

    /// <summary>Certificates the user confirmed in the trust dialog (SPKI SHA-256 per host address).</summary>
    public List<CertificateDto> TrustedCertificates { get; init; } = [];

    /// <summary>
    /// Kubernetes: the cluster's CA certificate (PEM, the ca.crt of the token secret). The API servers are trusted when
    /// they present a certificate it signed.
    /// </summary>
    public string? CaCertificatePem { get; init; }

    public bool AllowInsecureNbd { get; init; }
}

/// <summary>Returned with HTTP 409 when hosts present certificates the user has not trusted yet.</summary>
public sealed record UntrustedCertificatesDto(string Message, IReadOnlyList<CertificateDto> Certificates);

public sealed record ConnectionDto(
    Guid Id,
    string Kind,
    string DisplayName,
    string Address,
    int Port,
    string? MasterAddress,
    string? PlatformName,
    string? ProductName,
    string? ProductVersion,
    string Status,
    DateTimeOffset? LastConnectedUtc,
    string? LastError,
    string UserName,
    bool AllowInsecureNbd,
    IReadOnlyList<ConnectionHostDto> Hosts);

public sealed record ConnectionHostDto(string Address, string SpkiSha256, DateTimeOffset TrustedUtc);

public sealed record InventoryItemDto(string Kind, string NativeId, string Name, string? ParentNativeId, IReadOnlyDictionary<string, string> Properties);

// ---- Repositories ----
public sealed record CreateRepositoryDto
{
    public required string Name { get; init; }

    public required string Path { get; init; }

    public int ChunkSizeKiB { get; init; } = 1024;

    public int CompressionLevel { get; init; } = 3;

    public int MaxConcurrentTasks { get; init; } = 4;
}

public sealed record RepositoryDto(
    Guid Id,
    string Name,
    string Kind,
    string Path,
    Guid RepositoryUuid,
    int ChunkSize,
    int CompressionLevel,
    int MaxConcurrentTasks,
    string State,
    long? TotalBytes,
    long? FreeBytes,
    int RestorePoints,
    long LogicalBytes,
    string? LastError);

// ---- Jobs ----
public sealed record ScheduleDto(string Kind, string TimeOfDay, IReadOnlyList<DayOfWeek> Days, string TimeZoneId);

/// <summary>A VM of a job. <see cref="WithMemory"/>: back it up with a snapshot with memory (it can be resumed on restore).</summary>
public sealed record JobObjectDto(string NativeId, string Name, bool WithMemory = false);

public sealed record JobDto(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    Guid ConnectionId,
    string ConnectionName,
    Guid RepositoryId,
    string RepositoryName,
    int RetentionPoints,
    ScheduleDto Schedule,
    IReadOnlyList<JobObjectDto> Objects,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset? LastRunUtc,
    string? LastResult,
    bool IsRunning,
    int MaxParallelVms,
    int NbdConnectionsPerDisk,
    int RetryCount,
    string TransportMode,
    int MaxParallelDisks,
    bool GuestProcessing,
    string GuestFailurePolicy,
    Guid? GuestCredentialId,
    string GuestProcessingMode = GuestProcessingModes.Vss,
    Guid? SqlCredentialId = null,
    bool SqlSnapshotCopyOnly = false,
    SqlBackupSettingsDto? SqlBackup = null);

/// <summary>
/// SQL Server backups the agent runs in the job's VMs (the log chain): transaction log backups every
/// <see cref="LogIntervalMinutes"/>, native full backups (the chain base without the SQL Server snapshot, or for all
/// databases) and full backups of the system databases on their own schedules.
/// </summary>
public sealed record SqlBackupSettingsDto(
    bool Enabled = false,
    int LogIntervalMinutes = 60,
    ScheduleDto? FullSchedule = null,
    ScheduleDto? SystemSchedule = null,
    bool FullForAllDatabases = false,
    bool FullOnChainBreak = true,
    IReadOnlyList<string>? ExcludedDatabases = null,
    string? StagingFolder = null)
{
    /// <summary>The intervals offered as presets; longer ones (60 to 1440 minutes) can be chosen freely.</summary>
    public static readonly IReadOnlyList<int> PresetIntervals = [5, 15, 30, 60];

    public static bool IsValidInterval(int minutes) => minutes is 5 or 15 or 30 || minutes is >= 60 and <= 1440;
}

public sealed record SaveJobDto
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public required Guid ConnectionId { get; init; }

    public required Guid RepositoryId { get; init; }

    public int RetentionPoints { get; init; } = 14;

    public required ScheduleDto Schedule { get; init; }

    public required List<JobObjectDto> Objects { get; init; }

    public int MaxParallelVms { get; init; } = 2;

    public int NbdConnectionsPerDisk { get; init; } = 4;

    /// <summary>Disks of one VM read at the same time (each is its own stream).</summary>
    public int MaxParallelDisks { get; init; } = 2;

    /// <summary>Application-consistent backups through the guest agent (VSS).</summary>
    public bool GuestProcessing { get; init; }

    /// <summary><see cref="GuestFailurePolicies"/>: crash-consistent with a warning (default) or fail the VM.</summary>
    public string GuestFailurePolicy { get; init; } = GuestFailurePolicies.CrashConsistentWithWarning;

    /// <summary>Windows credentials to install the agent where it is missing (optional).</summary>
    public Guid? GuestCredentialId { get; init; }

    /// <summary><see cref="GuestProcessingModes"/>: VSS (default) or the SQL Server snapshot.</summary>
    public string GuestProcessingMode { get; init; } = GuestProcessingModes.Vss;

    /// <summary>SQL Server snapshot: the Windows account or SQL Server login the agent uses (sysadmin).</summary>
    public Guid? SqlCredentialId { get; init; }

    /// <summary>SQL Server snapshot: copy-only backups instead of regular full backups (default: regular full backups).</summary>
    public bool SqlSnapshotCopyOnly { get; init; }

    /// <summary>SQL Server backups in the VMs (log chain); null: off.</summary>
    public SqlBackupSettingsDto? SqlBackup { get; init; }

    public int RetryCount { get; init; } = 2;

    /// <summary>"Automatic" (NBD, otherwise HTTP export), "NbdOnly" or "HttpExportOnly".</summary>
    public string TransportMode { get; init; } = TransportModes.Automatic;
}

public static class TransportModes
{
    public const string Automatic = "Automatic";
    public const string NbdOnly = "NbdOnly";
    public const string HttpExportOnly = "HttpExportOnly";
}

public sealed record RunJobDto(bool ActiveFull = false);

// ---- Sessions ----
public sealed record SessionDetailDto(SessionSummaryDto Session, IReadOnlyList<SessionTaskDto> Tasks);

public sealed record StartedDto(Guid SessionId);

// ---- Restore points ----
public sealed record RestorePointDiskDto(string Key, string Name, long CapacityBytes, string? StorageId, string? StorageName, string Mode, string? FullReason, long ReadBytes, long NewStoredBytes);

public sealed record RestorePointNicDto(string Device, string Mac, string NetworkId, string NetworkName);

/// <param name="SqlUserDatabaseCount">The user databases of <c>SqlDatabaseCount</c> (not master, model, msdb); null if not known yet.</param>
/// <param name="SqlConsistentCount">Of <c>SqlUserDatabaseCount</c>, the application-consistent databases.</param>
/// <param name="ConsistencyWarning">The consistency is worse than the job promised; not for a SQL Server snapshot's own note.</param>
/// <param name="DomainControllerEvidence">
/// Only from <c>GET /api/restore-points/{id}</c>, for a restore point with memory, which has no guest details: why the VM is
/// known to be a domain controller anyway (its agent, or another restore point of it); null if nothing shows it.
/// </param>
public sealed record RestorePointDto(
    Guid Id,
    Guid RepositoryId,
    Guid? JobId,
    string JobName,
    Guid? ConnectionId,
    string VmNativeId,
    string VmName,
    DateTimeOffset CreatedUtc,
    string Kind,
    string State,
    long LogicalBytes,
    long NewStoredBytes,
    IReadOnlyList<RestorePointDiskDto> Disks,
    IReadOnlyList<RestorePointNicDto> Nics,
    int VCpus,
    long MemoryBytes,
    string? Firmware,
    string Consistency = "crash",
    string? ConsistencyReason = null,
    int? SqlDatabaseCount = null,
    bool DomainController = false,
    string? AdDomain = null,
    bool HasVmGenerationId = true,
    long? MemoryImageBytes = null,
    string? DomainControllerEvidence = null,
    int? SqlConsistentCount = null,
    bool ConsistencyWarning = false,
    int? SqlUserDatabaseCount = null);

public sealed record VerifyResultDto(bool Ok, long CheckedChunks, IReadOnlyList<string> Problems);

// ---- Backups overview (one row per VM, GET /api/restore-points/vms) ----

/// <summary>The state of a VM in a job (<see cref="BackupVmJobDto.State"/>) and of the whole VM (<see cref="BackupVmDto.State"/>).</summary>
public static class BackupVmStates
{
    /// <summary>A restore point of the job's most recent scheduled run is there.</summary>
    public const string Ok = "ok";

    /// <summary>The VM missed the most recent scheduled run that started at least 12 hours ago.</summary>
    public const string Stale = "stale";

    /// <summary>The VM is in the job but has no restore point of it.</summary>
    public const string NoPoint = "nopoint";

    /// <summary>The job has no schedule: it never marks a VM as stale.</summary>
    public const string Manual = "manual";

    /// <summary>The job is disabled.</summary>
    public const string Disabled = "disabled";

    /// <summary>Restore points without a job, or of a deleted job.</summary>
    public const string NoJob = "nojob";
}

/// <summary>The newest restore point of a VM (in a job, or overall), without disks and NICs.</summary>
public sealed record BackupVmPointDto(
    Guid Id,
    Guid? JobId,
    DateTimeOffset CreatedUtc,
    string Kind,
    string Consistency,
    string? ConsistencyReason,
    long LogicalBytes,
    int SqlDatabaseCount,
    bool DomainController,
    string? AdDomain,
    long? MemoryImageBytes = null,
    int? SqlConsistentCount = null,
    bool ConsistencyWarning = false,
    int? SqlUserDatabaseCount = null);

/// <summary>
/// A VM in one job: its restore points there and its state. <see cref="JobId"/> is null (and <see cref="JobExists"/>
/// false) for restore points without a job: their job was deleted, and <see cref="JobName"/> is the name stored with the
/// newest of them.
/// </summary>
public sealed record BackupVmJobDto(
    Guid? JobId,
    string JobName,
    bool JobExists,
    bool Enabled,
    ScheduleDto? Schedule,
    int PointCount,
    long StoredBytes,
    IReadOnlyList<Guid> RepositoryIds,
    BackupVmPointDto? Latest,
    string State,
    DateTimeOffset? MissedRunUtc);

/// <summary>One VM with its jobs; <see cref="State"/> is <c>stale</c> if one of its jobs is, else <c>nopoint</c> without any restore point, else <c>ok</c>.</summary>
public sealed record BackupVmDto(
    string VmNativeId,
    string VmName,
    Guid? ConnectionId,
    IReadOnlyList<BackupVmJobDto> Jobs,
    int PointCount,
    long StoredBytes,
    BackupVmPointDto? Latest,
    string State);

// ---- Dashboard ----
public sealed record DashboardDto(
    int JobsSucceeded24h,
    int JobsWarning24h,
    int JobsFailed24h,
    int ProtectedVms,
    int UnprotectedVms,
    IReadOnlyList<SessionSummaryDto> RunningSessions,
    IReadOnlyList<SessionSummaryDto> RecentSessions,
    IReadOnlyList<RepositoryDto> Repositories);

/// <summary>A backdafuckup agent in a Windows guest.</summary>
public sealed record AgentDto
{
    public Guid Id { get; init; }

    public required string HostName { get; init; }

    public string? DnsName { get; init; }

    public string? VmNativeId { get; init; }

    public string? VmName { get; init; }

    public string? OsName { get; init; }

    public string? OsVersion { get; init; }

    /// <summary>"net10" or "net48".</summary>
    public required string Build { get; init; }

    public required string Version { get; init; }

    public string? VssProvider { get; init; }

    public string? IpAddresses { get; init; }

    public string? RemoteAddress { get; init; }

    public bool Online { get; init; }

    /// <summary>False while the agent still runs files older than the server's build (it updates itself).</summary>
    public bool UpToDate { get; init; }

    /// <summary>The agent can restore files into its guest (reported with its last poll).</summary>
    public bool CanRestoreFiles { get; init; }

    /// <summary>The agent runs SQL Server jobs (inventory, restore, publish).</summary>
    public bool CanRunSql { get; init; }

    /// <summary>The agent can publish a SQL Server database from a restore point into its guest.</summary>
    public bool CanPublishSql { get; init; }

    /// <summary>The agent can restore a SQL Server database from a restore point into its guest (write the files and attach them).</summary>
    public bool CanRestoreSql { get; init; }

    public DateTimeOffset EnrolledUtc { get; init; }

    public DateTimeOffset LastContactUtc { get; init; }

    /// <summary>The agent runs on a domain controller (domain role 4 or 5).</summary>
    public bool DomainController { get; init; }

    /// <summary>The computer's DNS domain.</summary>
    public string? Domain { get; init; }
}

public sealed record AgentBuildDto(string Name, string Version, long SizeBytes, string FilesHash);

public sealed record AgentSettingsDto
{
    /// <summary>The address under which the guests reach the backup server.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>Lets new agents enroll; part of the setup package and the push installation.</summary>
    public required string EnrollmentSecret { get; init; }

    /// <summary>SPKI SHA-256 of the server certificate the agents pin (empty: Windows certificate validation).</summary>
    public IReadOnlyList<string> ServerPins { get; init; } = [];

    public IReadOnlyList<AgentBuildDto> Builds { get; init; } = [];

    public required string PackageDirectory { get; init; }
}

public sealed record AgentSettingsUpdateDto(string? ServerUrl);

/// <summary>Push installation of the guest agent into VMs of a pool, with Windows administrator credentials.</summary>
public sealed record InstallAgentsDto(Guid ConnectionId, IReadOnlyList<JobObjectDto> Vms, Guid CredentialId);

/// <summary>Windows administrator credentials (for the push installation of the agent); the password is never returned.</summary>
public sealed record WindowsCredentialDto(Guid Id, string UserName, string? Description, DateTimeOffset CreatedUtc);

public sealed record SaveWindowsCredentialDto(string UserName, string Password, string? Description);

/// <summary>A Linux account for file restores over SSH (secrets are never returned).</summary>
public sealed record LinuxCredentialDto(Guid Id, string UserName, string? Description, bool UsesPrivateKey, bool HasSudoPassword, DateTimeOffset CreatedUtc);

/// <param name="PrivateKey">OpenSSH or PEM private key; password or key is required.</param>
/// <param name="SudoPassword">For accounts other than root whose sudo asks for a password.</param>
public sealed record SaveLinuxCredentialDto(string UserName, string? Password, string? PrivateKey, string? Passphrase, string? SudoPassword, string? Description);

/// <summary>An SSH host key trusted on first use.</summary>
public sealed record SshHostKeyDto(Guid Id, string Host, int Port, string KeyType, string Fingerprint, DateTimeOffset TrustedUtc, string? TrustedBy);

public static class GuestProcessingModes
{
    /// <summary>VSS: every VSS-aware application; the VM snapshot must finish within 10 s.</summary>
    public const string Vss = "Vss";

    /// <summary>
    /// SQL Server 2022+: the databases' writes are suspended for the snapshot, without a time limit. VMs where no database
    /// can be suspended (no SQL Server, an older one, no rights) use VSS.
    /// </summary>
    public const string SqlSnapshot = "SqlSnapshot";
}

public static class GuestFailurePolicies
{
    public const string CrashConsistentWithWarning = "CrashConsistentWithWarning";
    public const string FailVm = "FailVm";
}

/// <summary>Result of "Test VSS": prepare, freeze and thaw in the guest without a VM snapshot.</summary>
public sealed record AgentTestResultDto(bool Ok, string Message, IReadOnlyList<string> Log);
