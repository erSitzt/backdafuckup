using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Agents;

/// <summary>How a VM is processed in the guest for one backup.</summary>
/// <param name="Quiescer">Freezes the guest around the snapshot; null if the backup will be crash-consistent.</param>
/// <param name="Reason">Why there is no quiescer (agent missing, offline, installation failed).</param>
/// <param name="Warning">Guest processing works, but not as the job asks (e.g. VSS because the SQL Server account is missing): the session warns.</param>
public sealed record GuestPreparation(AgentQuiescer? Quiescer, string? Reason, bool Warning = false);

/// <summary>Installs the agent into a VM (push installation); implemented by the agent deployer.</summary>
public interface IAgentInstaller
{
    /// <summary>Installs or repairs the agent in the VM; throws with a description naming address and port on failure.</summary>
    Task InstallAsync(Guid connectionId, string vmNativeId, string vmName, Guid credentialId, IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>
/// Decides for a backup with guest processing: use the online agent of the VM, install it first (if the job has Windows
/// credentials), or fall back to a crash-consistent backup with the reason.
/// </summary>
public sealed class GuestProcessingService(
    AgentService agents,
    AgentRegistry registry,
    IOptions<AgentOptions> options,
    IAgentInstaller? installer = null,
    Sql.SqlJobs? sqlJobs = null,
    IDbContextFactory<AppDbContext>? dbFactory = null,
    CredentialProtector? protector = null)
{
    /// <param name="backupName">The backup's name in SQL Server's msdb (SQL Server snapshot), e.g. with the restore point.</param>
    public async Task<GuestPreparation> PrepareAsync(BackupJob job, BackupJobObject vm, IOperationLog log, CancellationToken cancellationToken, string? backupName = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(log);
        var agent = await agents.FindForVmAsync(vm.NativeId, cancellationToken).ConfigureAwait(false);
        SqlSnapshotSettings? sql = null;
        var warning = false;
        if (job.GuestProcessingMode == GuestProcessingMode.SqlSnapshot)
        {
            try
            {
                sql = await SqlSettingsAsync(job, backupName ?? $"backdafuckup {job.Name}", cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // VSS needs no account: better than a crash-consistent backup.
                log.Warning($"Guest processing: SQL Server snapshot: {ex.Message} VSS is used for VM {vm.CachedName}.");
                warning = true;
            }
        }

        if (agent is not null && agents.IsOnline(agent.Id))
        {
            return Use(agent, sql, warning);
        }

        var missing = agent is null
            ? $"no backdafuckup agent is installed in VM {vm.CachedName}"
            : $"the agent on {agent.HostName} ({agent.RemoteAddress ?? "address unknown"}) has not contacted the server since {agent.LastContactUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        if (job.GuestCredentialId is not { } credentialId || installer is null)
        {
            return new GuestPreparation(null, $"{Capitalize(missing)}. Install it under Guest agents, or give the job Windows credentials for an automatic installation.");
        }

        log.Info($"Guest processing: {missing}; installing the agent with the job's Windows credentials.");
        try
        {
            await installer.InstallAsync(job.ConnectionId, vm.NativeId, vm.CachedName, credentialId, log, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new GuestPreparation(null, $"{Capitalize(missing)}, and the automatic installation failed: {ex.Message}");
        }

        agent = await agents.WaitForVmAsync(vm.NativeId, options.Value.InstallWaitTimeout, cancellationToken).ConfigureAwait(false);
        return agent is null
            ? new GuestPreparation(null, $"The agent was installed in VM {vm.CachedName}, but did not report to the server within {options.Value.InstallWaitTimeout.TotalMinutes:0} minutes (check the address under Guest agents → Settings and port 9555 from the guest).")
            : Use(agent, sql, warning);
    }

    private GuestPreparation Use(AgentEntity agent, SqlSnapshotSettings? sql, bool warning) =>
        new(new AgentQuiescer(registry, options.Value, agent.Id, $"agent on {agent.HostName} ({agent.RemoteAddress ?? agent.IpAddresses ?? "address unknown"})", sql), null, warning);

    /// <summary>The job's SQL account (decrypted only into this in-memory setting, which reaches the agent in the SQL job).</summary>
    private async Task<SqlSnapshotSettings> SqlSettingsAsync(BackupJob job, string backupName, CancellationToken cancellationToken)
    {
        if (sqlJobs is null || dbFactory is null || protector is null)
        {
            throw new InvalidOperationException("this server cannot run SQL Server jobs.");
        }

        if (job.SqlCredentialId is not { } credentialId)
        {
            throw new InvalidOperationException("the job has no SQL Server account. Choose one under Advanced → Guest processing.");
        }

        var account = await Api.SqlEndpoints.AccountAsync(credentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("the job's SQL Server account no longer exists.");
        return new SqlSnapshotSettings(sqlJobs, account, backupName, job.SqlSnapshotCopyOnly);
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
