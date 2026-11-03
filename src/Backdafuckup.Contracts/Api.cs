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

public sealed record ProbeRequestDto(string Address, int Port = 443);

public sealed record CreateConnectionDto
{
    public required string DisplayName { get; init; }

    public required string Address { get; init; }

    public int Port { get; init; } = 443;

    public required string UserName { get; init; }

    public required string Password { get; init; }

    /// <summary>Certificates the user confirmed in the trust dialog (SPKI SHA-256 per host address).</summary>
    public List<CertificateDto> TrustedCertificates { get; init; } = [];

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

public sealed record JobObjectDto(string NativeId, string Name);

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
    Guid? GuestCredentialId);

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
    int? SqlDatabaseCount = null);

public sealed record VerifyResultDto(bool Ok, long CheckedChunks, IReadOnlyList<string> Problems);

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

    public DateTimeOffset EnrolledUtc { get; init; }

    public DateTimeOffset LastContactUtc { get; init; }
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

public static class GuestFailurePolicies
{
    public const string CrashConsistentWithWarning = "CrashConsistentWithWarning";
    public const string FailVm = "FailVm";
}

/// <summary>Result of "Test VSS": prepare, freeze and thaw in the guest without a VM snapshot.</summary>
public sealed record AgentTestResultDto(bool Ok, string Message, IReadOnlyList<string> Log);
