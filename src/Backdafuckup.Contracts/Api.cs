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
    int RetryCount);

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

    public int RetryCount { get; init; } = 2;
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
    string? Firmware);

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
