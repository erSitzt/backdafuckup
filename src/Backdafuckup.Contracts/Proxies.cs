namespace Backdafuckup.Contracts;

/// <summary>
/// A backup proxy: the Linux agent (vSphere HotAdd in a VM, SAN reads) or the Windows agent on a physical server (SAN reads).
/// <c>Platform</c> is "linux-x64" or "win-x64" (null: it never connected).
/// </summary>
/// <param name="Enrolled">The agent exchanged its install token for its key.</param>
/// <param name="InstallTokenExpiresUtc">Until when the install command works (null once enrolled).</param>
/// <param name="InitiatorName">The proxy machine's iSCSI initiator name: its array host object must list it.</param>
/// <param name="SanRefusal">Why the proxy offers no SAN access (its self-check), or null.</param>
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
    DateTimeOffset? InstallTokenExpiresUtc,
    string? InitiatorName = null,
    string? SanRefusal = null,
    string? Platform = null);

public sealed record CreateProxyDto(string Name);

/// <summary>The proxy agent the server offers for download (built by scripts/build-proxy.sh); empty if it has none.</summary>
public sealed record ProxyPackageDto(string? Version, IReadOnlyList<ProxyPackageFileDto> Files);

public sealed record ProxyPackageFileDto(string Name, long SizeBytes);

/// <summary>A new proxy, or a renewed install token: the token is shown only here.</summary>
/// <param name="Command">What to run on the proxy machine (as root) with the agent installed.</param>
/// <param name="Command">The enroll command, for installing by hand.</param>
/// <param name="InstallCommand">One line to paste on a Linux proxy as root (<c>curl … | sudo bash</c>); null without an agent package.</param>
/// <param name="InstallScript">The same installer as a script file; null without an agent package.</param>
/// <param name="WindowsInstallCommand">One line to paste in a PowerShell started as administrator on a Windows server; null without the Windows agent.</param>
/// <param name="WindowsInstallScript">The same as a .ps1 file; null without the Windows agent.</param>
/// <param name="WindowsCommand">The install command for the downloaded <c>backdafuckup-proxy.exe</c>, by hand.</param>
public sealed record ProxyInstallDto(
    ProxyDto Proxy, string InstallToken, string Command, string? InstallCommand = null, string? InstallScript = null, string? WindowsInstallCommand = null,
    string? WindowsInstallScript = null, string? WindowsCommand = null);

/// <summary>The agent's enrollment: its one-time install token for its own key.</summary>
public sealed record EnrollProxyRequestDto(Guid ProxyId, string Token, string? HostName);

public sealed record EnrollProxyResponseDto(string AgentKey);
