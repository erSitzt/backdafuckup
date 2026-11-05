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

    /// <summary>
    /// SQL Server jobs: <c>GET {id}</c> (the request, with the account), <c>POST {id}/result</c>; for
    /// <see cref="SqlJobKinds.SnapshotSuspend"/> also <c>POST {id}/progress</c> (suspended) and <c>GET {id}/signal</c>.
    /// </summary>
    public const string SqlJobsPath = "api/agent/sql-jobs";

    /// <summary>Header of a SQL backup file upload: the <see cref="SqlBackupUpload"/> as base64 JSON.</summary>
    public const string BackupUploadHeader = "X-Bdfu-Backup";

    /// <summary>The agent publishes restore point disks into its guest (<see cref="AgentCommandKinds.Publish"/>).</summary>
    public const string CapabilityPublish = "publish-v1";

    /// <summary>
    /// The agent attaches a SQL Server database from a publish (<see cref="PublishInfo.Sql"/>) and removes it again before
    /// it unpublishes; it runs <see cref="SqlJobKinds.DropPublished"/>.
    /// </summary>
    public const string CapabilitySqlPublish = "sql-publish-v1";

    /// <summary>
    /// The agent attaches restored database files (<see cref="SqlJobKinds.AttachFiles"/>) and deletes the files of a
    /// failed restore (<see cref="SqlJobKinds.DeleteFiles"/>).
    /// </summary>
    public const string CapabilitySqlAttachFiles = "sql-attach-files-v1";

    /// <summary>
    /// The agent suspends SQL Server databases for the VM snapshot (<see cref="SqlJobKinds.SnapshotSuspend"/>): the T-SQL
    /// snapshot backup of SQL Server 2022 and later, which has no time limit, instead of VSS.
    /// </summary>
    public const string CapabilitySqlSnapshot = "sql-snapshot-v1";

    /// <summary>The agent overwrites a database with restored files (<see cref="SqlJobKinds.ReplaceFiles"/>).</summary>
    public const string CapabilitySqlReplaceFiles = "sql-replace-files-v1";

    /// <summary>
    /// The agent reports how it recovered an overwrite that its restart interrupted and brings the database back online
    /// (<see cref="SqlJobKinds.ReplaceRecovery"/>).
    /// </summary>
    public const string CapabilitySqlReplaceRecovery = "sql-replace-recovery-v1";

    /// <summary>
    /// The agent backs up a database to a <c>.bak</c> file and verifies it (<see cref="SqlJobKinds.BackupDatabase"/>), and
    /// uploads it on request (<c>PUT {SqlJobsPath}/{id}/file?name=</c>).
    /// </summary>
    public const string CapabilitySqlBackup = "sql-backup-v1";

    /// <summary>
    /// The agent runs the SQL Server backups of the log chain (<see cref="SqlJobKinds.LogBackup"/>,
    /// <see cref="SqlJobKinds.FullBackup"/>): backup files are queued durably in the agent's folder, uploaded to
    /// <c>PUT {SqlJobsPath}/{id}/backups/{fileId}</c> and deleted after <c>POST {SqlJobsPath}/{id}/backups/commit</c>.
    /// </summary>
    public const string CapabilitySqlBackupChain = "sql-backup-chain-v1";

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

    /// <summary>
    /// Random for each start of the agent process. A new value tells the server that the agent restarted (service
    /// restart, crash, VM reboot): the SQL jobs the previous process accepted are gone. Empty from agents before this field.
    /// </summary>
    public Guid RunId { get; set; }
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

    /// <summary>
    /// Removes a database that a publish attached and could not remove itself (agent or VM restarted while published):
    /// <see cref="SqlJobRequest.Database"/> is dropped only if all its files lie in <see cref="SqlJobRequest.PublishFolder"/>.
    /// </summary>
    public const string DropPublished = "drop-published";

    /// <summary>
    /// Attaches <see cref="SqlJobRequest.Files"/> (restored before, primary data file first) as
    /// <see cref="SqlJobRequest.Database"/>, after giving SQL Server access to them; if the attach fails, the files are
    /// deleted again (they were restored for it).
    /// </summary>
    public const string AttachFiles = "attach-files";

    /// <summary>Deletes <see cref="SqlJobRequest.Files"/>: the files a failed or stopped restore had written.</summary>
    public const string DeleteFiles = "delete-files";

    /// <summary>
    /// T-SQL snapshot backup around the VM snapshot (SQL Server 2022 and later): suspends the writes of every user database
    /// on its own connection (<c>MODE = COPY_ONLY</c>), reports <see cref="SqlSnapshotProgress"/>, waits for
    /// <see cref="SqlJobSignals"/>, then records the snapshot in <c>msdb</c> with <c>BACKUP DATABASE … WITH METADATA_ONLY</c>
    /// (which resumes the writes) and returns the <c>.bkm</c> files in <see cref="SqlJobResult.Snapshot"/>. Closing the
    /// connections resumes SQL Server too: on abort, after <see cref="SqlJobRequest.HoldLimitMs"/>, or if the agent dies.
    /// </summary>
    public const string SnapshotSuspend = "snapshot-suspend";

    /// <summary>
    /// Overwrites <see cref="SqlJobRequest.Database"/> with restored files: <see cref="SqlJobRequest.Files"/> are the
    /// database's files (exactly the ones it has now); the restored copies lie beside them as <c>*.bdfu-new</c>. The
    /// database goes offline, its files are moved aside (<c>*.bdfu-replaced</c>), the restored ones put in place, and it
    /// goes online again; on failure its own files go back. <see cref="SqlJobRequest.KeepReplaced"/> keeps the old files.
    /// </summary>
    public const string ReplaceFiles = "replace-files";

    /// <summary>
    /// Backs up <see cref="SqlJobRequest.Database"/> to <see cref="SqlJobRequest.FileName"/> in
    /// <see cref="SqlJobRequest.Folder"/> (default: the instance's backup folder; an existing file is never replaced, the
    /// name gets <c>_2</c>, <c>_3</c>, …): <c>BACKUP DATABASE … WITH COPY_ONLY, CHECKSUM</c> (and COMPRESSION except on
    /// Express), then <c>RESTORE VERIFYONLY … WITH CHECKSUM</c>. With <see cref="SqlJobRequest.Upload"/> the agent then
    /// sends the file to the server. The result names the file and its size.
    /// </summary>
    public const string BackupDatabase = "backup-database";

    /// <summary>
    /// After the agent restarted during a <see cref="ReplaceFiles"/> job of <see cref="SqlJobRequest.Database"/>: reports
    /// how its recovery at start ended (<see cref="SqlJobResult.Outcome"/>, one of <see cref="SqlReplaceOutcomes"/>) and,
    /// if the database got its own files back and was taken offline by the overwrite, brings it online again.
    /// </summary>
    public const string ReplaceRecovery = "replace-recovery";

    /// <summary>
    /// Transaction log backups of every user database with a log chain on <see cref="SqlJobRequest.Instance"/> (all running
    /// instances if null); a database in FULL or BULK_LOGGED recovery without a chain gets a native full backup instead if
    /// <see cref="SqlJobRequest.FullWithoutChain"/>. Backup files left from earlier runs are uploaded first. The result
    /// lists the databases, what was backed up and the backup sets of other tools since
    /// <see cref="SqlJobRequest.ForeignSinceUtc"/>.
    /// </summary>
    public const string LogBackup = "log-backup";

    /// <summary>
    /// Native full backups (<c>BACKUP DATABASE … WITH CHECKSUM</c>, verified): of <see cref="SqlJobRequest.Databases"/>,
    /// or with <see cref="SqlJobRequest.SystemDatabases"/> of <c>master</c>, <c>msdb</c> and <c>model</c>.
    /// </summary>
    public const string FullBackup = "full-backup";
}

/// <summary>How an overwrite that an agent restart interrupted ended (<see cref="SqlJobKinds.ReplaceRecovery"/>).</summary>
public static class SqlReplaceOutcomes
{
    /// <summary>The database had come online with the restored files: the overwrite stands.</summary>
    public const string Completed = "completed";

    /// <summary>The database has its own files again (and is online as before); the restored copies are <c>*.bdfu-new</c>.</summary>
    public const string RolledBack = "rolled-back";

    /// <summary>The overwrite had not changed anything yet (no journal): the database is as before.</summary>
    public const string Untouched = "untouched";
}

/// <summary>File names of <see cref="SqlJobKinds.ReplaceFiles"/>, shared by server and agent.</summary>
public static class SqlReplaceNames
{
    /// <summary>A restored file beside the database's own, until it is swapped in.</summary>
    public const string NewSuffix = ".bdfu-new";

    /// <summary>The database's previous file after the swap (deleted unless kept).</summary>
    public const string ReplacedSuffix = ".bdfu-replaced";
}

/// <summary>What the server tells a running <see cref="SqlJobKinds.SnapshotSuspend"/> job (<c>GET {id}/signal</c>).</summary>
public static class SqlJobSignals
{
    /// <summary>Keep the databases suspended: the VM snapshot is not done yet.</summary>
    public const string Wait = "wait";

    /// <summary>The VM snapshot is taken: record it and resume.</summary>
    public const string SnapshotTaken = "snapshot-taken";

    /// <summary>No snapshot (it failed or the backup stopped): resume without recording anything.</summary>
    public const string Abort = "abort";
}

public sealed class SqlJobSignal
{
    /// <summary>One of <see cref="SqlJobSignals"/>.</summary>
    public string Signal { get; set; } = SqlJobSignals.Wait;
}

/// <summary>The suspend of a <see cref="SqlJobKinds.SnapshotSuspend"/> job is in place (<c>POST {id}/progress</c>).</summary>
public sealed class SqlSnapshotProgress
{
    public List<SqlSnapshotInstance> Instances { get; set; } = new List<SqlSnapshotInstance>();
}

/// <summary>One SQL Server instance in a <see cref="SqlJobKinds.SnapshotSuspend"/> job.</summary>
public sealed class SqlSnapshotInstance
{
    /// <summary>Instance name, e.g. <c>SQLEXPRESS</c> or <c>MSSQLSERVER</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string? ProductVersion { get; set; }

    /// <summary>Why the instance was left out or failed as a whole (stopped, too old, no connection, no rights).</summary>
    public string? Error { get; set; }

    public List<SqlSnapshotDatabase> Databases { get; set; } = new List<SqlSnapshotDatabase>();

    /// <summary>Result only: the snapshot backup metadata (<c>.bkm</c>) of the recorded databases.</summary>
    public byte[]? Metadata { get; set; }

    /// <summary>Result only: where SQL Server wrote the <c>.bkm</c> (the agent deletes it after reading).</summary>
    public string? MetadataPath { get; set; }
}

public sealed class SqlSnapshotDatabase
{
    public string Name { get; set; } = string.Empty;

    /// <summary>One of <see cref="SqlSnapshotStates"/>.</summary>
    public string State { get; set; } = SqlSnapshotStates.Skipped;

    /// <summary>Why the database was skipped or failed.</summary>
    public string? Reason { get; set; }

    /// <summary>FULL, BULK_LOGGED or SIMPLE (null: an agent before this field).</summary>
    public string? RecoveryModel { get; set; }

    /// <summary>
    /// Before the backup: the database had a log chain (FULL or BULK_LOGGED with a regular full backup). Without one SQL
    /// Server frees its log at checkpoints as in SIMPLE recovery, and BACKUP LOG is refused.
    /// </summary>
    public bool HadLogChain { get; set; }

    /// <summary>Minutes since the last log backup in msdb (from any tool); null if there is none.</summary>
    public int? LogBackupAgeMinutes { get; set; }

    /// <summary>Result: as recorded in msdb (null: an agent before this field, which records copy-only).</summary>
    public bool? CopyOnly { get; set; }

    /// <summary>Result: this regular full backup started the database's log chain.</summary>
    public bool StartedLogChain { get; set; }

    /// <summary>Result: the backup set's position in the instance's <c>.bkm</c> (<c>RESTORE … WITH FILE = n</c>).</summary>
    public int? Position { get; set; }

    /// <summary>Result: the backup set's LSNs from msdb (numeric(25) as text), for restore planning with log backups.</summary>
    public string? FirstLsn { get; set; }

    public string? LastLsn { get; set; }

    public string? CheckpointLsn { get; set; }

    public string? DatabaseBackupLsn { get; set; }

    /// <summary>Result: the whole backup set as msdb recorded it (null: an agent before this field).</summary>
    public SqlBackupSetFacts? BackupSet { get; set; }
}

/// <summary>
/// A backup set from <c>msdb.dbo.backupset</c>: what the server's catalog and restore planner need. Numbers and GUIDs
/// travel as text (numeric(25) LSNs do not fit a long), times as SQL Server's local time with its UTC offset.
/// </summary>
public sealed class SqlBackupSetFacts
{
    public string BackupSetUuid { get; set; } = string.Empty;

    /// <summary><c>D</c> (full), <c>I</c> (differential) or <c>L</c> (log).</summary>
    public string Type { get; set; } = string.Empty;

    public int Position { get; set; } = 1;

    public bool IsCopyOnly { get; set; }

    public bool IsSnapshot { get; set; }

    public string FirstLsn { get; set; } = string.Empty;

    public string LastLsn { get; set; } = string.Empty;

    public string CheckpointLsn { get; set; } = string.Empty;

    public string? DatabaseBackupLsn { get; set; }

    public string FamilyGuid { get; set; } = string.Empty;

    public string DatabaseGuid { get; set; } = string.Empty;

    public string? FirstRecoveryForkGuid { get; set; }

    public string? LastRecoveryForkGuid { get; set; }

    public string? ForkPointLsn { get; set; }

    /// <summary>Start in SQL Server's local time (ISO 8601 without offset).</summary>
    public string Start { get; set; } = string.Empty;

    /// <summary>Finish in SQL Server's local time (ISO 8601 without offset).</summary>
    public string Finish { get; set; } = string.Empty;

    /// <summary>The server's offset from UTC in 15-minute steps (<c>backupset.time_zone</c>); null when unknown (127).</summary>
    public int? TimeZone { get; set; }

    public string? RecoveryModel { get; set; }

    public long BackupSize { get; set; }

    public long? CompressedBackupSize { get; set; }

    /// <summary>NAME of the backup set (ours: <c>backdafuckup …</c>).</summary>
    public string? Name { get; set; }

    /// <summary>The device the backup was written to (<c>backupmediafamily.physical_device_name</c>), where known.</summary>
    public string? Device { get; set; }

    /// <summary>Full backups by the agent: the database's files when it was backed up (where a restore puts them).</summary>
    public List<SqlBackupFileFacts>? Files { get; set; }
}

/// <summary>A file of a backed-up database (<c>sys.master_files</c>).</summary>
public sealed class SqlBackupFileFacts
{
    public int FileId { get; set; }

    public string LogicalName { get; set; } = string.Empty;

    public string PhysicalName { get; set; } = string.Empty;

    /// <summary><c>ROWS</c>, <c>LOG</c>, <c>FILESTREAM</c> or <c>FULLTEXT</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

public static class SqlSnapshotStates
{
    /// <summary>Progress: the database's writes are suspended.</summary>
    public const string Suspended = "suspended";

    /// <summary>Result: recorded with <c>BACKUP … METADATA_ONLY</c> (in <c>msdb</c> and the <c>.bkm</c>).</summary>
    public const string Recorded = "recorded";

    /// <summary>Not suspended (offline, read-only, a database snapshot); no writes could go into the VM snapshot from SQL Server.</summary>
    public const string Skipped = "skipped";

    /// <summary>Suspending or recording failed, or the suspend ended before the VM snapshot was taken.</summary>
    public const string Failed = "failed";
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

    /// <summary>DropPublished: the database to remove.</summary>
    public string? Database { get; set; }

    /// <summary>DropPublished: the publish's folder in the guest (<see cref="PublishStatus.Folder"/>).</summary>
    public string? PublishFolder { get; set; }

    /// <summary>AttachFiles, DeleteFiles: the database's files in the guest, the primary data file first.</summary>
    public List<string>? Files { get; set; }

    /// <summary>
    /// DeleteFiles: per entry of <see cref="Files"/> the SHA-256 (hex) the restored file has, or null. A file with a
    /// hash is deleted only if its content matches, otherwise it is kept and named: used for files the agent may have
    /// finished without reporting it before it restarted.
    /// </summary>
    public List<string?>? ExpectedHashes { get; set; }

    /// <summary>
    /// SnapshotSuspend: the longest time the databases stay suspended. After it the agent closes its connections, which
    /// resumes SQL Server, and the databases are reported as failed.
    /// </summary>
    public int HoldLimitMs { get; set; }

    /// <summary>
    /// SnapshotSuspend: the backup's name in <c>msdb</c> and in the <c>.bkm</c> (e.g. "backdafuckup restore point …").
    /// BackupDatabase: the backup set's name in the .bak (the original database's name, also when it is published under another).
    /// </summary>
    public string? BackupName { get; set; }

    /// <summary>
    /// SnapshotSuspend: false records regular full backups (they start or continue the log chain and become the base for
    /// differential backups); true or null (a server before this setting) copy-only ones (<c>MODE = COPY_ONLY</c>).
    /// </summary>
    public bool? CopyOnly { get; set; }

    /// <summary>ReplaceFiles: keep the database's previous files as <c>*.bdfu-replaced</c> instead of deleting them.</summary>
    public bool KeepReplaced { get; set; }

    /// <summary>BackupDatabase: the folder in the guest; null: the instance's backup folder.</summary>
    public string? Folder { get; set; }

    /// <summary>BackupDatabase: the file name (e.g. <c>shop_20261104_2158.bak</c>).</summary>
    public string? FileName { get; set; }

    /// <summary>BackupDatabase: the description in the backup (e.g. where it came from).</summary>
    public string? Description { get; set; }

    /// <summary>BackupDatabase: send the file to the server after the verify.</summary>
    public bool Upload { get; set; }

    /// <summary>BackupDatabase with upload: where the server files it (the export session); set by the server.</summary>
    public Guid? UploadId { get; set; }

    /// <summary>LogBackup/FullBackup: the backup job (written into the backup sets' DESCRIPTION).</summary>
    public Guid? BackupJobId { get; set; }

    /// <summary>LogBackup/FullBackup: the session (written into the backup sets' DESCRIPTION).</summary>
    public Guid? SessionId { get; set; }

    /// <summary>FullBackup: the user databases to back up.</summary>
    public List<string>? Databases { get; set; }

    /// <summary>FullBackup: back up <c>master</c>, <c>msdb</c> and <c>model</c>.</summary>
    public bool SystemDatabases { get; set; }

    /// <summary>LogBackup: user databases to leave out.</summary>
    public List<string>? ExcludedDatabases { get; set; }

    /// <summary>LogBackup: a database in FULL or BULK_LOGGED recovery without a log chain gets a native full backup.</summary>
    public bool FullWithoutChain { get; set; }

    /// <summary>LogBackup: report backup sets of other tools that finished since then (UTC).</summary>
    public DateTime? ForeignSinceUtc { get; set; }

    /// <summary>LogBackup/FullBackup/SnapshotSuspend: how long to wait for another backup of the instance in the agent (ms).</summary>
    public int LockWaitMs { get; set; }
}

public sealed class SqlJobResult
{
    public Guid JobId { get; set; }

    public bool Success { get; set; }

    public string? Error { get; set; }

    public SqlInventory? Inventory { get; set; }

    /// <summary>SnapshotSuspend: per instance the recorded, skipped and failed databases and the <c>.bkm</c>.</summary>
    public List<SqlSnapshotInstance>? Snapshot { get; set; }

    /// <summary>SnapshotSuspend: how long the first database's writes were suspended until the last one resumed.</summary>
    public long SuspendedMs { get; set; }

    /// <summary>BackupDatabase: the backup file in the guest and its size.</summary>
    public string? BackupPath { get; set; }

    public long BackupBytes { get; set; }

    /// <summary>BackupDatabase: the upload reached the server completely.</summary>
    public bool Uploaded { get; set; }

    /// <summary>ReplaceRecovery: one of <see cref="SqlReplaceOutcomes"/>.</summary>
    public string? Outcome { get; set; }

    /// <summary>LogBackup/FullBackup: the databases of the instances and what happened to each.</summary>
    public List<SqlBackupDatabaseOutcome>? BackupDatabases { get; set; }

    /// <summary>LogBackup: backup sets of other tools since <see cref="SqlJobRequest.ForeignSinceUtc"/>.</summary>
    public List<SqlForeignBackup>? ForeignBackups { get; set; }

    /// <summary>LogBackup/FullBackup: backup files committed on the server in this job (uploaded now or earlier).</summary>
    public int CommittedFiles { get; set; }

    /// <summary>LogBackup/FullBackup: backup files still waiting in the agent's queue (upload failed, retried next run).</summary>
    public int QueuedFiles { get; set; }

    /// <summary>What the job did, for the session log (e.g. "removed database [shop_published]").</summary>
    public List<string> Log { get; set; } = new List<string>();
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

    /// <summary>
    /// A database to attach from the mounted volumes once they are mounted (agents with
    /// <see cref="AgentProtocol.CapabilitySqlPublish"/>). Carries the account, so the agent never stores it.
    /// </summary>
    public PublishSqlDatabase? Sql { get; set; }
}

/// <summary>Attach a database from the published volumes; it is taken offline and dropped before the volumes go.</summary>
public sealed class PublishSqlDatabase
{
    /// <summary>The local instance, e.g. <c>SQLEXPRESS</c> or <c>MSSQLSERVER</c>.</summary>
    public string Instance { get; set; } = string.Empty;

    public SqlAccount Account { get; set; } = new SqlAccount();

    /// <summary>The name to attach the database as; it must not exist on the instance.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>All files of the database, the primary data file first.</summary>
    public List<PublishSqlFile> Files { get; set; } = new List<PublishSqlFile>();
}

public sealed class PublishSqlFile
{
    /// <summary><see cref="PublishVolume.Name"/> of the volume that holds the file.</summary>
    public string Volume { get; set; } = string.Empty;

    /// <summary>Path within the volume, e.g. <c>SQL\shop.mdf</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>"data" or "log".</summary>
    public string Kind { get; set; } = "data";
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

    /// <summary>The publish's folder in the guest; the mount folders are below it.</summary>
    public string? Folder { get; set; }

    public List<PublishMount> Mounts { get; set; } = new List<PublishMount>();

    /// <summary>The database attached from the publish (<see cref="PublishInfo.Sql"/>), while it is attached.</summary>
    public string? Database { get; set; }

    /// <summary>
    /// Set in the final report when the attached database could not be removed (SQL Server not reachable, …): the
    /// server removes it with <see cref="SqlJobKinds.DropPublished"/>.
    /// </summary>
    public string? DatabaseLeft { get; set; }

    /// <summary>Final report: everything was removed from the guest (otherwise the journal keeps it for another try).</summary>
    public bool CleanedUp { get; set; }

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

/// <summary>What a backup job did with one database.</summary>
public sealed class SqlBackupDatabaseOutcome
{
    public string Instance { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public string? RecoveryModel { get; set; }

    /// <summary>The database has a log chain (<c>last_log_backup_lsn</c> is set).</summary>
    public bool HasLogChain { get; set; }

    public string? FamilyGuid { get; set; }

    public string? DatabaseGuid { get; set; }

    public string? RecoveryForkGuid { get; set; }

    /// <summary>One of <see cref="SqlBackupActions"/>.</summary>
    public string Action { get; set; } = SqlBackupActions.Skipped;

    /// <summary>Why it was skipped or failed.</summary>
    public string? Reason { get; set; }

    /// <summary>The backup file in the guest (when backed up).</summary>
    public string? Path { get; set; }

    public long Bytes { get; set; }
}

public static class SqlBackupActions
{
    public const string Log = "log";
    public const string Full = "full";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>A backup set another tool wrote (or one whose NAME is not ours), as seen in msdb.</summary>
public sealed class SqlForeignBackup
{
    public string Instance { get; set; } = string.Empty;

    public string Database { get; set; } = string.Empty;

    public SqlBackupSetFacts BackupSet { get; set; } = new SqlBackupSetFacts();
}

/// <summary>Sent with each backup file upload (header <c>X-Bdfu-Backup</c>, base64 JSON).</summary>
public sealed class SqlBackupUpload
{
    /// <summary>The queue entry's id (stable across retries).</summary>
    public string FileId { get; set; } = string.Empty;

    public string Instance { get; set; } = string.Empty;

    public string? ProductVersion { get; set; }

    public string Database { get; set; } = string.Empty;

    /// <summary>One of <see cref="SqlBackupActions.Log"/>, <see cref="SqlBackupActions.Full"/>, or <c>system</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long Length { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public SqlBackupSetFacts BackupSet { get; set; } = new SqlBackupSetFacts();

    /// <summary>The backup job and session the file was written for (may be an earlier run's).</summary>
    public Guid? BackupJobId { get; set; }

    public Guid? SessionId { get; set; }
}

/// <summary>The server's answer to an upload or a commit.</summary>
public sealed class SqlBackupUploadReply
{
    /// <summary>Upload: the backup set is already in the catalog (an earlier upload committed it); the agent may delete it.</summary>
    public bool Known { get; set; }

    /// <summary>Commit: the file ids now stored for good (the agent deletes these files).</summary>
    public List<string> Committed { get; set; } = new List<string>();
}
