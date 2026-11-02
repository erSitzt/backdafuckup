using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Microsoft.AspNetCore.Identity;

namespace Backdafuckup.Server.Data;

public sealed class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }
}

public enum ConnectionStatus
{
    Unknown,
    Connected,
    Error,
}

public sealed class HypervisorConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public HypervisorKind Kind { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Address as entered by the user.</summary>
    public required string Address { get; set; }

    public int Port { get; set; } = 443;

    public string? CurrentMasterAddress { get; set; }

    public string? PlatformId { get; set; }

    public string? PlatformName { get; set; }

    public string? ProductName { get; set; }

    public string? ProductVersion { get; set; }

    public Guid CredentialId { get; set; }

    public Credential Credential { get; set; } = null!;

    public TlsTrustMode TrustMode { get; set; } = TlsTrustMode.Pinned;

    public bool AllowInsecureNbd { get; set; }

    public ConnectionStatus Status { get; set; }

    public DateTimeOffset? LastConnectedUtc { get; set; }

    public string? LastError { get; set; }

    public List<ConnectionHost> Hosts { get; set; } = [];

    public uint Version { get; set; }
}

/// <summary>A trusted host certificate (public-key pin) of a connection.</summary>
public sealed class ConnectionHost
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConnectionId { get; set; }

    public required string Address { get; set; }

    public required string SpkiSha256 { get; set; }

    public required string Sha256Thumbprint { get; set; }

    public required string CertificatePem { get; set; }

    public DateTimeOffset TrustedUtc { get; set; }

    public string? TrustedBy { get; set; }
}

public sealed class Credential
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Username { get; set; }

    /// <summary>Password protected with ASP.NET Data Protection. Never returned by the API.</summary>
    public required string ProtectedSecret { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class InventoryEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConnectionId { get; set; }

    public InventoryKind Kind { get; set; }

    public required string NativeId { get; set; }

    public required string Name { get; set; }

    public string? ParentNativeId { get; set; }

    public Dictionary<string, string> Properties { get; set; } = [];

    public DateTimeOffset RefreshedUtc { get; set; }
}

public enum RepositoryKind
{
    LocalDirectory,
}

public enum RepositoryState
{
    Online,
    Offline,
    Error,
}

public sealed class BackupRepositoryEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public RepositoryKind Kind { get; set; }

    public required string Path { get; set; }

    /// <summary>Id from repo.json.</summary>
    public Guid RepositoryUuid { get; set; }

    public int ChunkSize { get; set; }

    public int CompressionLevel { get; set; }

    public int MaxConcurrentTasks { get; set; } = 4;

    public RepositoryState State { get; set; }

    public long? TotalBytes { get; set; }

    public long? FreeBytes { get; set; }

    public string? LastError { get; set; }

    public uint Version { get; set; }
}

public enum ScheduleKind
{
    None,
    Daily,
    Weekly,
}

/// <summary>Daily or weekly schedule in a time zone (IANA id).</summary>
public sealed class JobSchedule
{
    public ScheduleKind Kind { get; set; }

    public TimeOnly TimeOfDay { get; set; } = new(22, 0);

    /// <summary>Bit mask of <see cref="DayOfWeek"/> (bit n = day n). Weekly only.</summary>
    public int DaysOfWeek { get; set; }

    public string TimeZoneId { get; set; } = "UTC";
}

public enum SessionResult
{
    Success,
    Warning,
    Failed,
    Stopped,
}

public sealed class BackupJob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public Guid ConnectionId { get; set; }

    public HypervisorConnection Connection { get; set; } = null!;

    public Guid RepositoryId { get; set; }

    public BackupRepositoryEntity Repository { get; set; } = null!;

    public int RetentionPoints { get; set; } = 14;

    public JobSchedule Schedule { get; set; } = new();

    public int MaxParallelVms { get; set; } = 2;

    public int NbdConnectionsPerDisk { get; set; } = 4;

    public int RetryCount { get; set; } = 2;

    public int RetryDelayMinutes { get; set; } = 5;

    public DateTimeOffset? NextRunUtc { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }

    public SessionResult? LastResult { get; set; }

    public List<BackupJobObject> Objects { get; set; } = [];

    public uint Version { get; set; }
}

public sealed class BackupJobObject
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid JobId { get; set; }

    public required string NativeId { get; set; }

    public required string CachedName { get; set; }

    public int Order { get; set; }
}

public enum RestorePointState
{
    Creating,
    Available,
    Deleting,
    Missing,
}

public sealed class RestorePointEntity
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }

    public Guid? JobId { get; set; }

    public required string JobName { get; set; }

    public Guid? SessionId { get; set; }

    public Guid? ConnectionId { get; set; }

    public required string PlatformId { get; set; }

    public required string VmNativeId { get; set; }

    public required string VmName { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public RestorePointKind Kind { get; set; }

    public RestorePointState State { get; set; }

    public long LogicalBytes { get; set; }

    public long NewStoredBytes { get; set; }

    public string? ManifestPath { get; set; }

    public VmDescriptor? Vm { get; set; }

    public List<RestorePointDisk> Disks { get; set; } = [];
}

public sealed class RestorePointDisk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid RestorePointId { get; set; }

    public required string DiskKey { get; set; }

    public required string SourceDiskId { get; set; }

    public required string Name { get; set; }

    public long CapacityBytes { get; set; }

    public string? StorageId { get; set; }

    public string? StorageName { get; set; }

    public DiskBackupMode Mode { get; set; }

    public string? FullReason { get; set; }

    public string? ChangeTokenKind { get; set; }

    public string? ChangeTokenValue { get; set; }

    public long ReadBytes { get; set; }

    public long NewStoredBytes { get; set; }
}

public enum SessionKind
{
    Backup,
    Restore,
    RepositoryMaintenance,
}

public enum SessionRunMode
{
    Incremental,
    ActiveFull,
}

public enum SessionState
{
    Queued,
    Running,
    Stopping,
    Succeeded,
    Warning,
    Failed,
    Stopped,
}

public sealed class SessionEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public SessionKind Kind { get; set; }

    public Guid? JobId { get; set; }

    public Guid? RepositoryId { get; set; }

    public required string Name { get; set; }

    public required string TriggeredBy { get; set; }

    public SessionRunMode RunMode { get; set; }

    public SessionState State { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    public double Progress { get; set; }

    public long ProcessedBytes { get; set; }

    public long ReadBytes { get; set; }

    public long TransferredBytes { get; set; }

    public string? ServerInstanceId { get; set; }

    public string? Summary { get; set; }

    /// <summary>Kind-specific parameters (e.g. the restore request) as JSON.</summary>
    public string? Parameters { get; set; }

    public List<SessionTaskEntity> Tasks { get; set; } = [];
}

public sealed class SessionTaskEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid SessionId { get; set; }

    public required string ObjectNativeId { get; set; }

    public required string ObjectName { get; set; }

    public SessionState State { get; set; }

    public int Attempt { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public DateTimeOffset? EndedUtc { get; set; }

    public double Progress { get; set; }

    public long ReadBytes { get; set; }

    public long TransferredBytes { get; set; }

    public Guid? RestorePointId { get; set; }

    public string? Error { get; set; }
}

public sealed class SessionLogEntry
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public Guid? TaskId { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public LogSeverity Severity { get; set; }

    public required string Message { get; set; }

    /// <summary>Entries with the same key in a session replace each other (progress lines).</summary>
    public string? Key { get; set; }
}

public sealed class ProviderArtifactEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConnectionId { get; set; }

    public required string Kind { get; set; }

    public required string NativeId { get; set; }

    public string? NativeRef { get; set; }

    public Guid? SessionId { get; set; }

    public Guid? RestorePointId { get; set; }

    public ArtifactState State { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public string? Data { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class AppSetting
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
