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
}

public sealed class AgentPollRequest
{
    public AgentHostInfo Host { get; set; } = new AgentHostInfo();
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

    public string Database { get; set; } = string.Empty;

    /// <summary>Data and log files with their paths in the guest.</summary>
    public List<string> Files { get; set; } = new List<string>();
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
