namespace Backdafuckup.Server.Data;

/// <summary>
/// A backup proxy. Its secrets are kept only as SHA-256 hashes: the one-time install token (valid until
/// <see cref="InstallTokenExpiresUtc"/>) and, after enrollment, the agent's key.
/// </summary>
public sealed class ProxyEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public byte[]? InstallTokenHash { get; set; }

    public DateTimeOffset? InstallTokenExpiresUtc { get; set; }

    public byte[]? KeyHash { get; set; }

    public DateTimeOffset? EnrolledUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>Where it connected from last (address:port).</summary>
    public string? LastAddress { get; set; }

    public string? HostName { get; set; }

    public string? AgentVersion { get; set; }

    /// <summary>Where the agent runs (<see cref="Core.Proxies.ProxyPlatforms"/>), from its last hello; null before the first.</summary>
    public string? Platform { get; set; }

    /// <summary>The SMBIOS UUID of the machine (finds the proxy's VM in vCenter).</summary>
    public string? SmbiosUuid { get; set; }

    public string? BiosSerial { get; set; }
}
