namespace Backdafuckup.Contracts;

/// <summary>A backup proxy (Linux agent that reads and writes disks for vSphere HotAdd and SAN backups).</summary>
/// <param name="Enrolled">The agent exchanged its install token for its key.</param>
/// <param name="InstallTokenExpiresUtc">Until when the install command works (null once enrolled).</param>
public sealed record ProxyDto(
    Guid Id,
    string Name,
    bool Online,
    bool Enrolled,
    string? HostName,
    string? Version,
    string? SmbiosUuid,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Capabilities,
    int Devices,
    DateTimeOffset? LastSeenUtc,
    string? LastAddress,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? InstallTokenExpiresUtc);

public sealed record CreateProxyDto(string Name);

/// <summary>The proxy agent the server offers for download (built by scripts/build-proxy.sh); empty if it has none.</summary>
public sealed record ProxyPackageDto(string? Version, IReadOnlyList<ProxyPackageFileDto> Files);

public sealed record ProxyPackageFileDto(string Name, long SizeBytes);

/// <summary>A new proxy, or a renewed install token: the token is shown only here.</summary>
/// <param name="Command">What to run on the proxy machine (as root) with the agent installed.</param>
public sealed record ProxyInstallDto(ProxyDto Proxy, string InstallToken, string Command);

/// <summary>The agent's enrollment: its one-time install token for its own key.</summary>
public sealed record EnrollProxyRequestDto(Guid ProxyId, string Token, string? HostName);

public sealed record EnrollProxyResponseDto(string AgentKey);
