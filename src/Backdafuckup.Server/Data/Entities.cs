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

/// <summary>
/// An API token of a user: scripts send it as <c>Authorization: Bearer bdfu_…</c> and act as that user. Only the
/// SHA-256 of the token is stored; the token itself is shown once when it is created.
/// </summary>
public sealed class ApiTokenEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>The token's first characters (<c>bdfu_</c> and 8 more), to tell tokens apart in the list.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the whole token, lowercase hex.</summary>
    public required string Hash { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>After this the token is refused; null: it does not expire.</summary>
    public DateTimeOffset? ExpiresUtc { get; set; }

    public DateTimeOffset? LastUsedUtc { get; set; }

    /// <summary>The address of the last request with the token.</summary>
    public string? LastUsedFrom { get; set; }

    /// <summary>What the token may do: <c>full</c> (as its user) or <c>metrics</c> (only <c>/metrics</c>).</summary>
    public string Scope { get; set; } = Backdafuckup.Contracts.ApiTokenScopes.Full;
}

public enum ConnectionStatus
{
    Unknown,
    Connected,
    Error,
}

/// <summary>How the server reaches a connection's API.</summary>
public enum ConnectionAccess
{
    /// <summary>The server connects to the address itself.</summary>
    Direct,

    /// <summary>Kubernetes: through the tunnel of the cluster's agent, which dials out to the server.</summary>
    Agent,
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

    /// <summary>Kubernetes: the platforms recognised at the last scan or backup run, with their evidence.</summary>
    public List<Backdafuckup.Core.Hypervisor.DetectedPlatform>? DetectedPlatforms { get; set; }

    /// <summary>Kubernetes: the platforms set by hand (catalog ids), used instead of the detected ones; null: detected.</summary>
    public List<string>? PlatformOverride { get; set; }

    /// <summary>Kubernetes: where the cluster's usage numbers come from (a Prometheus API); null: none.</summary>
    public Backdafuckup.Kubernetes.KubernetesUsageConfig? UsageSource { get; set; }

    /// <summary>The usage source's password or bearer token, protected with ASP.NET Data Protection; never returned by the API.</summary>
    public string? UsageSecret { get; set; }

    public Guid CredentialId { get; set; }

    public Credential Credential { get; set; } = null!;

    public TlsTrustMode TrustMode { get; set; } = TlsTrustMode.Pinned;

    public bool AllowInsecureNbd { get; set; }

    /// <summary>
    /// <see cref="ConnectionAccess.Agent"/>: the address and token come from the cluster's agent, and the credential's
    /// secret is the cluster's connect token (in the setup manifest), which the agent authenticates with.
    /// </summary>
    public ConnectionAccess Access { get; set; }

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

    /// <summary><c>Win32_ComputerSystem.DomainRole</c> (4 and 5: domain controller); null from older agents.</summary>
    public int? DomainRole { get; set; }

    /// <summary>The computer's DNS domain (or workgroup).</summary>
    public string? Domain { get; set; }

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

/// <summary>The SQL Server backups of a job (see <c>SqlBackupSettingsDto</c>).</summary>
public sealed class SqlBackupSettings
{
    public bool Enabled { get; set; }

    public int LogIntervalMinutes { get; set; } = 60;

    /// <summary>Native full backups of user databases; default weekly on Sunday at 20:00.</summary>
    public JobSchedule FullSchedule { get; set; } = new() { Kind = ScheduleKind.Weekly, DaysOfWeek = 1 << (int)DayOfWeek.Sunday, TimeOfDay = new TimeOnly(20, 0) };

    /// <summary>Full backups of master, msdb and model; default daily at 20:00.</summary>
    public JobSchedule SystemSchedule { get; set; } = new() { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(20, 0) };

    /// <summary>Native fulls also of databases whose base is the SQL Server snapshot (2022+ in SqlSnapshot mode).</summary>
    public bool FullForAllDatabases { get; set; }

    /// <summary>A database without a usable chain (new, after SIMPLE, after a break) gets a native full at once.</summary>
    public bool FullOnChainBreak { get; set; } = true;

    public List<string> ExcludedDatabases { get; set; } = [];

    /// <summary>Folder in the guest for the backup files until they are sent; null: the instance's backup folder.</summary>
    public string? StagingFolder { get; set; }
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

    /// <summary>
    /// Kubernetes: back up every pod volume that is not a PVC (emptyDir and the like), not only those a pod names in
    /// <c>backup.velero.io/backup-volumes</c>. Needs the node agent in the cluster.
    /// </summary>
    public bool KubernetesPodVolumes { get; set; }

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

    /// <summary>
    /// <see cref="GuestProcessingMode.SqlSnapshot"/>: record copy-only backups (outside the backup chain) instead of
    /// regular full backups (the default for new jobs). A regular full backup starts or continues the log chain and is the
    /// base for log and differential backups, also those of other tools. VSS backups are always copy-only.
    /// </summary>
    public bool SqlSnapshotCopyOnly { get; set; }

    /// <summary>SQL Server backups the agent runs in the job's VMs (log chain, native and system fulls).</summary>
    public SqlBackupSettings SqlBackup { get; set; } = new();

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

    /// <summary>
    /// Snapshot with memory (Xen: <c>VM.checkpoint</c>): the VM is paused while its RAM is written and all its disks are
    /// captured, so the disks match and a restore can resume it. Only for a running VM; no guest processing then.
    /// </summary>
    public bool WithMemory { get; set; }
}

/// <summary>
/// A VM that replaced another at a restore to the original location. It got a new VM id because the original still held
/// the old one. A later restore to the original location from a restore point of the old VM follows these to the VM in
/// use now, instead of replacing the old, renamed one again.
/// </summary>
public sealed class VmReplacementEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ConnectionId { get; set; }

    public required string ReplacedVmId { get; set; }

    public required string VmId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>The restore session that replaced it.</summary>
    public Guid SessionId { get; set; }
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

    /// <summary>
    /// The consistency is worse than the job's guest processing promises (see <c>RestorePointManifest.ConsistencyWarning</c>):
    /// the restore point shows it as a warning. Not for the note of a SQL Server snapshot that recorded every database.
    /// </summary>
    public bool ConsistencyWarning { get; set; }

    /// <summary><see cref="Contracts.RestorePointObjectKinds"/>: a VM, or a Kubernetes namespace (its objects and volumes).</summary>
    public string ObjectKind { get; set; } = Contracts.RestorePointObjectKinds.Vm;

    /// <summary>SQL Server databases in the guest metadata; null until known (restore points from before it was recorded).</summary>
    public int? SqlDatabaseCount { get; set; }

    /// <summary>The user databases of <see cref="SqlDatabaseCount"/> (not master, model, msdb, tempdb); null until known (older restore points are checked at start).</summary>
    public int? SqlUserDatabaseCount { get; set; }

    /// <summary>Of <see cref="SqlUserDatabaseCount"/>, the application-consistent ones; valid when that is known.</summary>
    public int? SqlConsistentCount { get; set; }

    /// <summary>Size of the RAM image of a snapshot with memory (a restore can resume the VM); null without memory.</summary>
    public long? MemoryImageBytes { get; set; }

    /// <summary>
    /// The VM was a domain controller; null until known (older restore points are checked at start). Restore points with memory
    /// have no guest details: see <see cref="Engine.DomainControllerEvidence"/>.
    /// </summary>
    public bool? DomainController { get; set; }

    /// <summary>The domain controller's DNS domain ("" if unknown).</summary>
    public string? AdDomain { get; set; }

    /// <summary>Whether the snapshot fulls of its SQL Server snapshot are in the SQL catalog; null until checked (older restore points).</summary>
    public bool? SqlBackupSetsRecorded { get; set; }

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

    /// <summary><see cref="RestorePointDiskKinds.Disk"/>, or <see cref="RestorePointDiskKinds.FileTree"/> for a volume read file by file.</summary>
    public string Kind { get; set; } = RestorePointDiskKinds.Disk;

    /// <summary>A file tree's files.</summary>
    public long? FileCount { get; set; }

    /// <summary>A file tree's entries that were not read.</summary>
    public long? IssueCount { get; set; }
}

public static class RestorePointDiskKinds
{
    public const string Disk = "disk";

    public const string FileTree = "files";
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

    /// <summary>A SQL Server database of a restore point exported as a .bak (published, backed up, unpublished).</summary>
    SqlExport,

    /// <summary>
    /// The SQL Server backups of a job on one day (log, native and system fulls of all its VMs): not queued; every run of
    /// the day reopens it, and its state is the worst of the day.
    /// </summary>
    SqlBackup,

    /// <summary>
    /// A restore point's AD database opened on a live domain controller (dsamain) for browsing and comparing; not queued,
    /// its log is the audit trail.
    /// </summary>
    AdExplore,

    /// <summary>Objects of an AD explore session restored into the live directory (one task per object).</summary>
    AdRestore,

    /// <summary>A tape library action: load, unload, import, export, a full inventory.</summary>
    TapeMaintenance,
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

    /// <summary>
    /// What the session works on, for the console's icons: <see cref="Contracts.RestorePointObjectKinds"/> for backups,
    /// restores and file restores (a VM or a Kubernetes namespace); null for sessions of other kinds (SQL Server, Active
    /// Directory, maintenance).
    /// </summary>
    public string? ObjectKind { get; set; }

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
