using System;
using System.Collections.Generic;

namespace Backdafuckup.Agent.Protocol;

/// <summary>
/// The agent API: the agent in the Windows guest only ever connects to the backup server (HTTPS, long poll); the
/// server never connects to the agent. Pattern taken from the erSitzt/inventory agent.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><c>POST api/agent/enroll</c> with the enrollment secret from <c>agent.json</c> returns the agent's own key
/// (256 bit, shown once; the server keeps only its SHA-256).</item>
/// <item>Every further request carries <see cref="KeyHeader"/>. <c>POST api/agent/poll</c> is held open for up to
/// <see cref="PollHoldSeconds"/> and answers with the next command (200) or nothing (204).</item>
/// <item>The agent answers each command with <c>POST api/agent/result</c> and polls again at once, so a follow-up
/// command (thaw after freeze) reaches it without delay.</item>
/// </list>
/// </remarks>
public static class AgentProtocol
{
    public const string KeyHeader = "X-Agent-Key";
    public const string EnrollPath = "api/agent/enroll";
    public const string PollPath = "api/agent/poll";
    public const string ResultPath = "api/agent/result";

    /// <summary>Files of the agent package for the self-update: <c>api/agent/files/{build}/{name}</c>.</summary>
    public const string FilesPath = "api/agent/files";

    public const int PollHoldSeconds = 50;

    /// <summary>
    /// File-level restore into the guest: <c>GET {id}</c> (what to do), <c>GET {id}/items?offset=&amp;limit=</c>,
    /// <c>GET {id}/items/{index}/content</c>, <c>POST {id}/progress</c>.
    /// </summary>
    public const string TransfersPath = "api/agent/transfers";

    /// <summary>The agent restores files (<see cref="AgentCommandKinds.RestoreFiles"/>); older agents are asked to update first.</summary>
    public const string CapabilityRestoreFiles = "restore-files-v1";

    /// <summary>
    /// The agent understands <see cref="FileTransferInfo.IfExists"/> (also "fail": an existing file fails the item) and
    /// reports its work slot in <see cref="AgentPollRequest.Busy"/>.
    /// </summary>
    public const string CapabilityRestoreFilesV2 = "restore-files-v2";

    /// <summary>The agent runs SQL Server jobs (<see cref="AgentCommandKinds.SqlJob"/>).</summary>
    public const string CapabilitySqlRestore = "sql-restore-v1";

    /// <summary>SQL Server jobs: <c>GET {id}</c> (the request, with the account), <c>POST {id}/result</c>.</summary>
    public const string SqlJobsPath = "api/agent/sql-jobs";

    /// <summary>The agent publishes restore point disks into its guest (<see cref="AgentCommandKinds.Publish"/>).</summary>
    public const string CapabilityPublish = "publish-v1";

    /// <summary>
    /// Publishing: <c>GET {id}</c> (what to publish), <c>GET {id}/disks/{n}/blocks/{b}</c> (disk content, one block of
    /// <see cref="PublishInfo.BlockSize"/> bytes), <c>POST {id}/status</c> (heartbeat; the answer says whether to stop).
    /// </summary>
    public const string PublishesPath = "api/agent/publishes";

    public const string ServiceName = "BackdafuckupAgent";
    public const string ServiceDisplayName = "backdafuckup Agent";
    public const string ExecutableName = "backdafuckup-agent.exe";
    public const string VssDllName = "bdfu-vss.dll";
    public const string ConfigFile = "agent.json";
    public const string KeyFile = "agent.key";
    public const string LogFile = "agent.log";

    /// <summary>Exit code after a self-update: the service recovery restarts the agent with the new files.</summary>
    public const int RestartExitCode = 3;

    /// <summary>The agent builds; the push installation prefers <see cref="BuildNet10"/>.</summary>
    public const string BuildNet10 = "net10";
    public const string BuildNet48 = "net48";
}

/// <summary><c>agent.json</c> next to the agent: written by the push installation or the setup package.</summary>
public sealed class AgentConfig
{
    /// <summary>For example <c>https://backup.example.local:9555/</c>.</summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>SHA-256 of the server certificate's public key (SPKI), hex. Empty: Windows certificate validation.</summary>
    public List<string> ServerPins { get; set; } = new List<string>();

    /// <summary>Only needed until the agent has its own key.</summary>
    public string? EnrollmentSecret { get; set; }
}

public sealed class AgentEnrollRequest
{
    public string EnrollmentSecret { get; set; } = string.Empty;

    public AgentHostInfo Host { get; set; } = new AgentHostInfo();
}

public sealed class AgentEnrollResponse
{
    public Guid AgentId { get; set; }

    public string AgentKey { get; set; } = string.Empty;
}

/// <summary>What the agent reports about itself with every poll.</summary>
public sealed class AgentHostInfo
{
    public string HostName { get; set; } = string.Empty;

    public string? DnsName { get; set; }

    /// <summary>BIOS serial number. On XCP-ng / XenServer HVM guests this is the VM UUID.</summary>
    public string? BiosSerial { get; set; }

    /// <summary>SMBIOS system UUID as Windows shows it (the first three groups may be byte-swapped).</summary>
    public string? SmbiosUuid { get; set; }

    public string? OsName { get; set; }

    public string? OsVersion { get; set; }

    public List<string> IpAddresses { get; set; } = new List<string>();

    /// <summary><see cref="AgentProtocol.BuildNet10"/> or <see cref="AgentProtocol.BuildNet48"/>.</summary>
    public string Build { get; set; } = string.Empty;

    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>SHA-256 over the installed agent files; differs from the server's package after a server update.</summary>
    public string FilesHash { get; set; } = string.Empty;

    /// <summary>"registered", "missing" or an error text.</summary>
    public string VssProvider { get; set; } = string.Empty;

    /// <summary>What this agent can do beyond VSS, e.g. <see cref="AgentProtocol.CapabilityRestoreFiles"/>.</summary>
    public List<string> Capabilities { get; set; } = new List<string>();
}

public sealed class AgentPollRequest
{
    public AgentHostInfo Host { get; set; } = new AgentHostInfo();

    /// <summary>
    /// What occupies the agent's work slot (a file restore, later SQL jobs), or null when it is idle. The server offers no
    /// self-update while it is set, also after a server restart, and queues further work for this agent.
    /// </summary>
    public string? Busy { get; set; }
}

/// <summary>Answer to a poll: a command, or an update offer.</summary>
public sealed class AgentPollResponse
{
    public AgentCommand? Command { get; set; }

    public AgentUpdateOffer? Update { get; set; }
}

public static class AgentCommandKinds
{
    /// <summary>Report the host info again (also used as a connectivity check).</summary>
    public const string GetInfo = "GetInfo";

    /// <summary>Gather the VSS writer metadata (volumes, writers, SQL databases) for the coming backup.</summary>
    public const string PrepareBackup = "PrepareBackup";

    /// <summary>Freeze I/O through VSS; answered once our provider holds in CommitSnapshots.</summary>
    public const string Freeze = "Freeze";

    /// <summary>Release the freeze; answered with the writer results.</summary>
    public const string Thaw = "Thaw";

    /// <summary>Abort a prepared or frozen backup (no thaw result needed).</summary>
    public const string Abort = "Abort";

    /// <summary>
    /// Start restoring the files of transfer <see cref="AgentCommand.TransferId"/>. Answered at once; the agent then fetches
    /// the items and reports through <see cref="AgentProtocol.TransfersPath"/>.
    /// </summary>
    public const string RestoreFiles = "RestoreFiles";

    /// <summary>
    /// Run SQL Server job <see cref="AgentCommand.JobId"/>. Answered at once; the agent then fetches the request and posts
    /// the result through <see cref="AgentProtocol.SqlJobsPath"/>.
    /// </summary>
    public const string SqlJob = "SqlJob";

    /// <summary>
    /// Publish <see cref="AgentCommand.PublishId"/>: serve the restore point's disks to the guest's iSCSI initiator over
    /// 127.0.0.1, bring them online and mount their volumes into folders. Answered at once; the agent reports through
    /// <see cref="AgentProtocol.PublishesPath"/> and unpublishes when the server says stop or goes silent.
    /// </summary>
    public const string Publish = "Publish";
}

public sealed class AgentCommand
{
    public Guid Id { get; set; }

    /// <summary>One of <see cref="AgentCommandKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Groups prepare, freeze and thaw of one VM backup.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Freeze: how long the provider holds before it gives up by itself.</summary>
    public int TimeoutMs { get; set; }

    /// <summary>VSS_BT_COPY: leaves the backup history of other products untouched (default).</summary>
    public bool CopyOnly { get; set; } = true;

    /// <summary>RestoreFiles: the transfer to fetch.</summary>
    public Guid TransferId { get; set; }

    /// <summary>SqlJob: the job to fetch.</summary>
    public Guid JobId { get; set; }

    /// <summary>Publish: the publish to fetch.</summary>
    public Guid PublishId { get; set; }
}

public sealed class AgentCommandResult
{
    public Guid CommandId { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    /// <summary>HRESULT or Win32 error of a failed VSS call, if any.</summary>
    public int ErrorCode { get; set; }

    public AgentHostInfo? Info { get; set; }

    public GuestBackupInfo? Prepare { get; set; }

    public FreezeReport? Freeze { get; set; }

    public ThawReport? Thaw { get; set; }
}

/// <summary>What the guest contains, from the VSS writer metadata: stored with the restore point.</summary>
public sealed class GuestBackupInfo
{
    public List<GuestVolume> Volumes { get; set; } = new List<GuestVolume>();

    public List<GuestWriter> Writers { get; set; } = new List<GuestWriter>();

    public List<GuestSqlDatabase> SqlDatabases { get; set; } = new List<GuestSqlDatabase>();

    /// <summary>
    /// The SQL Server writer's metadata document (<c>IVssExamineWriterMetadata::SaveAsXML</c>), kept with the restore point
    /// for a later restore through the writer. Null if the guest has no SQL Server writer or the agent is older.
    /// </summary>
    public string? SqlWriterMetadataXml { get; set; }
}

public sealed class GuestVolume
{
    /// <summary>Volume GUID path, e.g. <c>\\?\Volume{…}\</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public List<string> MountPoints { get; set; } = new List<string>();

    public string? FileSystem { get; set; }

    public string? Label { get; set; }

    public long SizeBytes { get; set; }
}

public sealed class GuestWriter
{
    public string Name { get; set; } = string.Empty;

    public string WriterId { get; set; } = string.Empty;

    public string? InstanceName { get; set; }

    /// <summary>VSS_WRITER_STATE as text, e.g. "Stable", "FailedAtFreeze".</summary>
    public string State { get; set; } = string.Empty;

    public string? Failure { get; set; }

    public List<string> Components { get; set; } = new List<string>();
}

public sealed class GuestSqlDatabase
{
    /// <summary>SQL Server instance, e.g. <c>MSSQLSERVER</c> or <c>SQLEXPRESS</c>.</summary>
    public string Instance { get; set; } = string.Empty;

    /// <summary>The server part of the writer's logical path (the computer name), if the agent reports it.</summary>
    public string? Server { get; set; }

    public string Database { get; set; } = string.Empty;

    /// <summary>Data and log files with their paths in the guest (all kinds, as before).</summary>
    public List<string> Files { get; set; } = new List<string>();

    /// <summary>Data files (.mdf/.ndf, FILESTREAM containers) as the SQL writer reports them. Empty for older agents.</summary>
    public List<GuestSqlFile> DataFiles { get; set; } = new List<GuestSqlFile>();

    /// <summary>Log files (.ldf). Empty for older agents.</summary>
    public List<GuestSqlFile> LogFiles { get; set; } = new List<GuestSqlFile>();

    /// <summary>Other files of the component (e.g. full-text catalogs).</summary>
    public List<GuestSqlFile> OtherFiles { get; set; } = new List<GuestSqlFile>();

    /// <summary>True if the component was part of the VSS backup (selectable and added); null for older agents.</summary>
    public bool? InBackup { get; set; }

    /// <summary>VSS_COMPONENT_FLAGS of the component.</summary>
    public int ComponentFlags { get; set; }
}

/// <summary>One file description of a VSS component: folder, file spec (may contain wildcards) and the recursive flag.</summary>
public sealed class GuestSqlFile
{
    /// <summary>Folder, e.g. <c>D:\SQL\</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>File name or wildcard spec, e.g. <c>shop.mdf</c> or <c>*</c>.</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>True for folders that are included with their subfolders, e.g. FILESTREAM containers.</summary>
    public bool Recursive { get; set; }

    /// <summary>The full path of a single file, or null for a wildcard or recursive spec (a method, so it is not serialized).</summary>
    public string? FullPath() => Recursive || Spec.IndexOfAny(new[] { '*', '?' }) >= 0
        ? null
        : (Path.EndsWith("\\", StringComparison.Ordinal) ? Path : Path + "\\") + Spec;
}

public sealed class FreezeReport
{
    /// <summary>From the start of the VSS snapshot set until our provider held the writes.</summary>
    public long FreezeMs { get; set; }
}

public sealed class ThawReport
{
    /// <summary>How long writes were held (freeze until thaw).</summary>
    public long HeldMs { get; set; }

    /// <summary>True if VSS completed the snapshot set and no writer failed: the snapshot is application-consistent.</summary>
    public bool Consistent { get; set; }

    /// <summary>HRESULT of the snapshot set (0 = S_OK), e.g. VSS_E_HOLD_WRITES_TIMEOUT after more than 10 s.</summary>
    public int VssResult { get; set; }

    public List<GuestWriter> Writers { get; set; } = new List<GuestWriter>();

    /// <summary>
    /// The Backup Components Document after BackupComplete (<c>IVssBackupComponents::SaveAsXML</c>), with the writers'
    /// per-component backup metadata. Kept for a later restore through the SQL writer; null for older agents.
    /// </summary>
    public string? BackupComponentsXml { get; set; }
}

/// <summary>The server ships different agent files than installed: download them and restart.</summary>
public sealed class AgentUpdateOffer
{
    public string Build { get; set; } = string.Empty;

    public string FilesHash { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public List<AgentFile> Files { get; set; } = new List<AgentFile>();
}

public sealed class AgentFile
{
    public string Name { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>SHA-256, hex.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>What a file restore transfer asks of the agent (<c>GET api/agent/transfers/{id}</c>).</summary>
public sealed class FileTransferInfo
{
    public Guid TransferId { get; set; }

    /// <summary>Folders the transfer writes below, e.g. <c>C:\</c> or <c>D:\Restore</c>. The agent refuses anything else.</summary>
    public List<string> Roots { get; set; } = new List<string>();

    /// <summary>True: replace existing files. False: keep them and write <see cref="KeepPrefix"/>&lt;name&gt;.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// One of <see cref="FileTransferIfExists"/>; null for servers that only know <see cref="Overwrite"/>. Agents without
    /// <see cref="AgentProtocol.CapabilityRestoreFilesV2"/> ignore it, so the server sends "fail" only to agents that have it.
    /// </summary>
    public string? IfExists { get; set; }

    public string KeepPrefix { get; set; } = "RESTORED-";

    /// <summary>Apply the stored security descriptors (owner, group, DACL).</summary>
    public bool RestorePermissions { get; set; }

    public int ItemCount { get; set; }

    public long TotalBytes { get; set; }
}

/// <summary>What happens to a file that exists at the target.</summary>
public static class FileTransferIfExists
{
    public const string Replace = "replace";

    /// <summary>Keep it and write the restored file as <see cref="FileTransferInfo.KeepPrefix"/>&lt;name&gt;.</summary>
    public const string Keep = "keep";

    /// <summary>Keep it and fail the item (e.g. database files, which must keep their names).</summary>
    public const string Fail = "fail";
}

public static class FileTransferItemKinds
{
    public const string Directory = "dir";

    public const string File = "file";

    /// <summary>After the content of a folder: set its times, attributes and permissions now.</summary>
    public const string EndDirectory = "end";
}

/// <summary>One step of a transfer, in order: a folder comes before its content and its end after it.</summary>
public sealed class FileTransferItem
{
    public int Index { get; set; }

    /// <summary>One of <see cref="FileTransferItemKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Absolute path in the guest, e.g. <c>C:\Users\x\report.docx</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime? CreatedUtc { get; set; }

    public DateTime? ModifiedUtc { get; set; }

    public DateTime? AccessedUtc { get; set; }

    /// <summary>FileAttributes to set: read-only, hidden, system, archive (0: none).</summary>
    public int Attributes { get; set; }

    /// <summary>Key into <see cref="FileTransferItemsPage.Security"/>, or -1.</summary>
    public int Security { get; set; } = -1;
}

public sealed class FileTransferItemsPage
{
    public List<FileTransferItem> Items { get; set; } = new List<FileTransferItem>();

    /// <summary>Self-relative security descriptors (base64) used by the items of this page.</summary>
    public Dictionary<int, string> Security { get; set; } = new Dictionary<int, string>();

    /// <summary>True when no items follow this page.</summary>
    public bool End { get; set; }
}

public static class FileTransferStatus
{
    public const string Ok = "ok";

    /// <summary>Written under another name (keep mode): see <see cref="FileTransferResult.FinalPath"/>.</summary>
    public const string Renamed = "renamed";

    public const string Failed = "failed";

    /// <summary>Written, but times, attributes or permissions could not be set.</summary>
    public const string Incomplete = "incomplete";
}

public sealed class FileTransferResult
{
    public int Index { get; set; }

    /// <summary>One of <see cref="FileTransferStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public string? FinalPath { get; set; }

    /// <summary>Files: SHA-256 (hex) of what was written; the server compares it with what it sent.</summary>
    public string? Sha256 { get; set; }
}

/// <summary>Results since the last report; also the heartbeat of the transfer (<c>POST api/agent/transfers/{id}/progress</c>).</summary>
public sealed class FileTransferProgress
{
    /// <summary>Items processed so far (the next index).</summary>
    public int Done { get; set; }

    /// <summary>Bytes of file content written so far.</summary>
    public long Bytes { get; set; }

    public List<FileTransferResult> Results { get; set; } = new List<FileTransferResult>();

    public bool Finished { get; set; }

    /// <summary>Why the agent gave up on the whole transfer, if it did.</summary>
    public string? Error { get; set; }
}

public sealed class FileTransferProgressResponse
{
    /// <summary>The restore was stopped on the server: finish the current file and report Finished.</summary>
    public bool Cancel { get; set; }
}

public static class SqlJobKinds
{
    /// <summary>The instances (registry, service), the volumes, and with an account the databases and the account's rights.</summary>
    public const string Inventory = "inventory";
}

public static class SqlAccountKinds
{
    /// <summary>A Windows account: the agent logs on with it and connects with Windows authentication.</summary>
    public const string Windows = "windows";

    /// <summary>A SQL Server login.</summary>
    public const string Sql = "sql";
}

/// <summary>How the agent connects to SQL Server. Sent only inside a job request, never stored by the agent.</summary>
public sealed class SqlAccount
{
    /// <summary>One of <see cref="SqlAccountKinds"/>.</summary>
    public string Kind { get; set; } = SqlAccountKinds.Sql;

    /// <summary><c>DOMAIN\user</c>, <c>user@domain</c> or <c>.\user</c> for Windows accounts; the login name for SQL logins.</summary>
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}

/// <summary>What a SQL job asks of the agent (<c>GET api/agent/sql-jobs/{id}</c>).</summary>
public sealed class SqlJobRequest
{
    public Guid JobId { get; set; }

    /// <summary>One of <see cref="SqlJobKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The instance, e.g. <c>SQLEXPRESS</c> or <c>MSSQLSERVER</c>; null: all instances (inventory).</summary>
    public string? Instance { get; set; }

    public SqlAccount? Account { get; set; }
}

public sealed class SqlJobResult
{
    public Guid JobId { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public SqlInventory? Inventory { get; set; }
}

public sealed class SqlInventory
{
    public List<SqlInstanceInfo> Instances { get; set; } = new List<SqlInstanceInfo>();

    /// <summary>Local fixed volumes with their free space.</summary>
    public List<SqlVolumeInfo> Volumes { get; set; } = new List<SqlVolumeInfo>();
}

/// <summary>A local SQL Server instance: what the registry and the service manager say, and with an account what SQL Server says.</summary>
public sealed class SqlInstanceInfo
{
    /// <summary>MSSQLSERVER for the default instance, otherwise the instance name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>E.g. <c>MSSQL16.SQLEXPRESS</c>.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Setup version from the registry, e.g. <c>16.0.1000.6</c>.</summary>
    public string? SetupVersion { get; set; }

    public string? SetupEdition { get; set; }

    public string? ServiceName { get; set; }

    /// <summary>"Running", "Stopped", … or null if unknown.</summary>
    public string? ServiceState { get; set; }

    public string? ServiceAccount { get; set; }

    public string? DefaultDataFolder { get; set; }

    public string? DefaultLogFolder { get; set; }

    public string? BackupFolder { get; set; }

    /// <summary>True if the job's account could connect; then the fields below are filled.</summary>
    public bool Connected { get; set; }

    /// <summary>Why the connection failed (wrong password, no login, service stopped, …).</summary>
    public string? ConnectError { get; set; }

    /// <summary>SERVERPROPERTY('ProductVersion'), e.g. <c>16.0.4305.5</c>.</summary>
    public string? ProductVersion { get; set; }

    public string? Edition { get; set; }

    /// <summary>SERVERPROPERTY('EngineEdition'): 4 = Express.</summary>
    public int? EngineEdition { get; set; }

    /// <summary>The login SQL Server sees (SUSER_SNAME()).</summary>
    public string? LoginName { get; set; }

    public bool? IsSysadmin { get; set; }

    public bool? IsDbCreator { get; set; }

    public List<SqlDatabaseInfo> Databases { get; set; } = new List<SqlDatabaseInfo>();
}

public sealed class SqlDatabaseInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>sys.databases.state_desc, e.g. ONLINE, OFFLINE, RESTORING.</summary>
    public string State { get; set; } = string.Empty;

    public string? RecoveryModel { get; set; }

    public bool ReadOnly { get; set; }

    public List<SqlDatabaseFileInfo> Files { get; set; } = new List<SqlDatabaseFileInfo>();
}

public sealed class SqlDatabaseFileInfo
{
    public string LogicalName { get; set; } = string.Empty;

    public string PhysicalName { get; set; } = string.Empty;

    /// <summary>sys.master_files.type_desc: ROWS, LOG, FILESTREAM, FULLTEXT.</summary>
    public string Type { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

public sealed class SqlVolumeInfo
{
    /// <summary>E.g. <c>D:\</c>.</summary>
    public string Root { get; set; } = string.Empty;

    public string? Label { get; set; }

    public long FreeBytes { get; set; }

    public long TotalBytes { get; set; }
}

/// <summary>What to publish (<c>GET api/agent/publishes/{id}</c>).</summary>
public sealed class PublishInfo
{
    public Guid PublishId { get; set; }

    /// <summary>For the agent's log, e.g. "SQL01, restore point of 2026-11-03 10:11".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Size of the blocks the agent fetches (default 1 MiB).</summary>
    public int BlockSize { get; set; } = 1 << 20;

    /// <summary>Writes go to an overlay file in the guest; beyond this size they fail.</summary>
    public long MaxOverlayBytes { get; set; }

    public List<PublishDisk> Disks { get; set; } = new List<PublishDisk>();

    /// <summary>The partitions to mount into folders.</summary>
    public List<PublishVolume> Volumes { get; set; } = new List<PublishVolume>();
}

public sealed class PublishDisk
{
    /// <summary>Position in <see cref="PublishInfo.Disks"/>; also the disk's index in the block URLs and its LUN.</summary>
    public int Index { get; set; }

    public string Key { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

public sealed class PublishVolume
{
    /// <summary>Folder name for the mount, e.g. "D" (the volume's drive letter in the backup) or "volume2".</summary>
    public string Name { get; set; } = string.Empty;

    public int DiskIndex { get; set; }

    /// <summary>Byte offset of the partition on its disk.</summary>
    public long StartOffset { get; set; }
}

public static class PublishStates
{
    public const string Starting = "starting";

    /// <summary>The volumes are mounted (<see cref="PublishStatus.Mounts"/>).</summary>
    public const string Mounted = "mounted";

    public const string Failed = "failed";

    /// <summary>Unpublished: everything is removed again.</summary>
    public const string Ended = "ended";
}

/// <summary>The agent's report about a publish, also its heartbeat (<c>POST api/agent/publishes/{id}/status</c>).</summary>
public sealed class PublishStatus
{
    public string State { get; set; } = PublishStates.Starting;

    public List<PublishMount> Mounts { get; set; } = new List<PublishMount>();

    /// <summary>Bytes written into the overlay so far.</summary>
    public long OverlayBytes { get; set; }

    /// <summary>Bytes fetched from the server so far.</summary>
    public long ReadBytes { get; set; }

    public string? Error { get; set; }

    /// <summary>Lines for the session log (what was done, in order), since the last report.</summary>
    public List<string> Log { get; set; } = new List<string>();
}

public sealed class PublishMount
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the volume is mounted in the guest, e.g. <c>C:\ProgramData\backdafuckup Agent\publish\1a2b\D</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

public sealed class PublishStatusResponse
{
    /// <summary>True: unpublish now.</summary>
    public bool Stop { get; set; }
}
