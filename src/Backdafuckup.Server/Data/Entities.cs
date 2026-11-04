using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
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

    /// <summary>Hypervisor credentials belong to a pool connection; Windows credentials are managed on their own.</summary>
    public CredentialKind Kind { get; set; } = CredentialKind.Hypervisor;

    public required string Username { get; set; }

    /// <summary>Password protected with ASP.NET Data Protection. Never returned by the API.</summary>
    public required string ProtectedSecret { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}

public enum CredentialKind
{
    Hypervisor,

    /// <summary>Windows administrator for the push installation of the guest agent (DOMAIN\user or .\user).</summary>
    Windows,

    /// <summary>Linux account for file restores over SSH; the secret is a protected <see cref="LinuxSecret"/> (JSON).</summary>
    Linux,

    /// <summary>SQL Server login for SQL restores; the secret is the protected password.</summary>
    SqlServer,
}

/// <summary>What a Linux credential holds (stored as protected JSON): password or private key, and the sudo password.</summary>
public sealed record LinuxSecret(string? Password, string? PrivateKey, string? Passphrase, string? SudoPassword);

/// <summary>An SSH host key trusted on first use (one per host and port); a different key later is refused.</summary>
public sealed class SshHostKeyEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The address as it was connected to (IP or name).</summary>
    public required string Host { get; set; }

    public int Port { get; set; }

    /// <summary>e.g. ssh-ed25519.</summary>
    public required string KeyType { get; set; }

    /// <summary>SHA-256 of the key, base64 without padding (as OpenSSH shows it after "SHA256:").</summary>
    public required string Fingerprint { get; set; }

    public DateTimeOffset TrustedUtc { get; set; }

    /// <summary>The session that met the host first.</summary>
    public string? TrustedBy { get; set; }
}

/// <summary>A backdafuckup agent in a Windows guest (enrolled with the enrollment secret, then known by its key).</summary>
public sealed class AgentEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>SHA-256 (hex) of the agent's key; the key itself is only known to the agent.</summary>
    public required string KeyHash { get; set; }

    public required string HostName { get; set; }

    public string? DnsName { get; set; }

    public string? BiosSerial { get; set; }

    public string? SmbiosUuid { get; set; }

    /// <summary>The VM this agent runs in (matched by BIOS serial / SMBIOS UUID); null while unknown.</summary>
    public string? VmNativeId { get; set; }

    public string? OsName { get; set; }

    public string? OsVersion { get; set; }

    /// <summary>"net10" or "net48".</summary>
    public string Build { get; set; } = string.Empty;

    public string AgentVersion { get; set; } = string.Empty;

    public string FilesHash { get; set; } = string.Empty;

    public string? VssProvider { get; set; }

    /// <summary>The guest's own addresses, comma separated.</summary>
    public string? IpAddresses { get; set; }

    /// <summary>The address the server saw the last request from.</summary>
    public string? RemoteAddress { get; set; }

    public DateTimeOffset EnrolledUtc { get; set; }

    public DateTimeOffset LastContactUtc { get; set; }
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

    /// <summary>Disks of one VM read at the same time. Each disk is its own stream (NBD connections or one HTTP export).</summary>
    public int MaxParallelDisks { get; set; } = 2;

    /// <summary>How disks are read: NBD with HTTP-export fallback (default), NBD only, or HTTP export only.</summary>
    public DiskTransportMode TransportMode { get; set; } = DiskTransportMode.Automatic;

    public int RetryCount { get; set; } = 2;

    public int RetryDelayMinutes { get; set; } = 5;

    /// <summary>Application-consistent backups: freeze the guest through the backdafuckup agent (VSS) for the snapshot.</summary>
    public bool GuestProcessing { get; set; }

    public GuestFailurePolicy GuestFailurePolicy { get; set; } = GuestFailurePolicy.CrashConsistentWithWarning;

    /// <summary>Windows credentials to install the agent automatically where it is missing (optional).</summary>
    public Guid? GuestCredentialId { get; set; }

    /// <summary>How the guest is made consistent for the snapshot: VSS (default) or SQL Server's T-SQL snapshot backup.</summary>
    public GuestProcessingMode GuestProcessingMode { get; set; } = GuestProcessingMode.Vss;

    /// <summary>
    /// The account the agent uses for <see cref="GuestProcessingMode.SqlSnapshot"/>: a Windows account or a SQL Server
    /// login with the right to suspend and back up the databases (sysadmin).
    /// </summary>
    public Guid? SqlCredentialId { get; set; }

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

    /// <summary>"crash" or "application".</summary>
    public string Consistency { get; set; } = "crash";

    public string? ConsistencyReason { get; set; }

    /// <summary>SQL Server databases in the guest metadata; null until known (restore points from before it was recorded).</summary>
    public int? SqlDatabaseCount { get; set; }

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
    AgentDeployment,

    /// <summary>An open file browser (file-level restore); not queued, its log is the audit trail.</summary>
    FileBrowse,

    /// <summary>Restore of single files into a VM or onto a share.</summary>
    FileRestore,

    /// <summary>A SQL Server database attached from a restore point (published) until it is unpublished.</summary>
    SqlPublish,

    /// <summary>A SQL Server database restored from a restore point (its files written into a VM and attached).</summary>
    SqlRestore,
}

public enum GuestProcessingMode
{
    /// <summary>VSS with our provider holding the writes during the VM snapshot (Windows allows at most 10 s).</summary>
    Vss,

    /// <summary>
    /// SQL Server 2022 and later: the agent suspends the writes of the SQL Server databases (no time limit), VSS only
    /// gathers the metadata. The databases are application-consistent, the rest of the guest crash-consistent. A VM where
    /// no database can be suspended uses <see cref="Vss"/>.
    /// </summary>
    SqlSnapshot,
}

public enum GuestFailurePolicy
{
    /// <summary>Back up the VM crash-consistently and end with a warning.</summary>
    CrashConsistentWithWarning,

    /// <summary>Fail the VM (retried like other failures).</summary>
    FailVm,
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

    /// <summary>Transfer statistics (rates, data reduction, bottleneck), rolled up from the tasks.</summary>
    public TransferMetricsDto? Metrics { get; set; }

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

    /// <summary>Transfer statistics of this object, including per-disk details.</summary>
    public TransferMetricsDto? Metrics { get; set; }
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
