using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Agents;

/// <summary>Enrollment, authentication and bookkeeping of the agents (catalog table <c>agents</c>).</summary>
public sealed class AgentService(
    IDbContextFactory<AppDbContext> dbFactory,
    AgentSettingsStore settings,
    AgentRegistry registry,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<AgentService> logger)
{
    public AgentOptions Options => options.Value;

    public bool IsOnline(Guid agentId) => registry.IsOnline(agentId, options.Value.OnlineWindow);

    /// <summary>Exchanges the enrollment secret for a new agent key. Returns null if the secret is wrong.</summary>
    public async Task<AgentEnrollResponse?> EnrollAsync(AgentEnrollRequest request, string? remoteAddress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = await settings.GetEnrollmentSecretAsync(cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.EnrollmentSecret ?? string.Empty), Encoding.UTF8.GetBytes(expected)))
        {
            logger.LogWarning("Agent enrollment from {Address} ({Host}) rejected: wrong enrollment secret.", remoteAddress, request.Host?.HostName);
            return null;
        }

        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = new AgentEntity { KeyHash = Hash(key), HostName = request.Host?.HostName ?? "unknown", EnrolledUtc = now, LastContactUtc = now };
        await ApplyHostInfoAsync(db, agent, request.Host ?? new AgentHostInfo(), remoteAddress, cancellationToken).ConfigureAwait(false);

        // A re-installed agent replaces its earlier entry for the same VM or host instead of piling up stale rows.
        var stale = await db.Agents.Where(a => (agent.VmNativeId != null && a.VmNativeId == agent.VmNativeId)
            || (agent.VmNativeId == null && a.VmNativeId == null && a.HostName == agent.HostName)).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.Agents.RemoveRange(stale);
        db.Agents.Add(agent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        registry.Touch(agent.Id);
        logger.LogInformation("Agent {AgentId} enrolled: host {Host} ({Address}), VM {Vm}, build {Build} {Version}.", agent.Id, agent.HostName, remoteAddress, agent.VmNativeId ?? "not matched", agent.Build, agent.AgentVersion);
        return new AgentEnrollResponse { AgentId = agent.Id, AgentKey = key };
    }

    /// <summary>The agent that owns this key, or null.</summary>
    public async Task<Guid?> AuthenticateAsync(string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
        {
            return null;
        }

        var hash = Hash(key.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Agents.Where(a => a.KeyHash == hash).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a poll: host info, address and last contact.</summary>
    public async Task UpdateAsync(Guid agentId, AgentHostInfo host, string? remoteAddress, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return;
        }

        agent.LastContactUtc = time.GetUtcNow();
        await ApplyHostInfoAsync(db, agent, host, remoteAddress, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        registry.Touch(agentId);
    }

    /// <summary>The agent running in this VM, if one is enrolled.</summary>
    public async Task<AgentEntity?> FindForVmAsync(string vmNativeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Agents.AsNoTracking().Where(a => a.VmNativeId == vmNativeId).OrderByDescending(a => a.LastContactUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until an agent of this VM is online (after a push installation).</summary>
    public async Task<AgentEntity?> WaitForVmAsync(string vmNativeId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            var agent = await FindForVmAsync(vmNativeId, cancellationToken).ConfigureAwait(false);
            if (agent is not null && IsOnline(agent.Id))
            {
                return agent;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static async Task ApplyHostInfoAsync(AppDbContext db, AgentEntity agent, AgentHostInfo host, string? remoteAddress, CancellationToken cancellationToken)
    {
        agent.HostName = string.IsNullOrWhiteSpace(host.HostName) ? agent.HostName : host.HostName;
        agent.DnsName = host.DnsName;
        agent.BiosSerial = host.BiosSerial;
        agent.SmbiosUuid = host.SmbiosUuid;
        agent.OsName = host.OsName;
        agent.OsVersion = host.OsVersion;
        agent.Build = host.Build ?? string.Empty;
        agent.AgentVersion = host.AgentVersion ?? string.Empty;
        agent.FilesHash = host.FilesHash ?? string.Empty;
        agent.VssProvider = host.VssProvider;
        agent.IpAddresses = host.IpAddresses is { Count: > 0 } ips ? string.Join(", ", ips) : null;
        agent.RemoteAddress = remoteAddress;

        var candidates = VmIdCandidates(host);
        if (candidates.Count > 0)
        {
            // Prefer a VM the inventory knows; before the first inventory refresh, the BIOS serial number (on XCP-ng
            // exactly the VM UUID) is taken as it is.
            agent.VmNativeId = await db.Inventory.AsNoTracking()
                .Where(i => i.Kind == InventoryKind.Vm && candidates.Contains(i.NativeId))
                .Select(i => i.NativeId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? (Guid.TryParse(host.BiosSerial?.Trim(), out var serial) ? serial.ToString("D", CultureInfo.InvariantCulture) : agent.VmNativeId);
        }
    }

    /// <summary>
    /// VM UUIDs the agent may run in: the BIOS serial number (XCP-ng puts the VM UUID there) and the SMBIOS UUID, both as
    /// shown and with the first three groups byte-swapped (Windows reads them little-endian).
    /// </summary>
    public static IReadOnlyList<string> VmIdCandidates(AgentHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var result = new List<string>();
        foreach (var value in new[] { host.BiosSerial, host.SmbiosUuid })
        {
            if (Guid.TryParse(value?.Trim(), out var guid))
            {
                result.Add(guid.ToString("D", CultureInfo.InvariantCulture));
                var bytes = guid.ToByteArray();
                var swapped = new Guid(bytes, bigEndian: true);
                result.Add(swapped.ToString("D", CultureInfo.InvariantCulture));
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }
}
