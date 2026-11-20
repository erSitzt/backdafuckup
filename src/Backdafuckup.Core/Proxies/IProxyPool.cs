using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Core.Proxies;

/// <summary>A connected backup proxy: who it is and where it runs (the SMBIOS UUID finds its VM in the hypervisor).</summary>
/// <param name="InitiatorName">The proxy's iSCSI initiator name (for the array's host object), if it has one.</param>
/// <param name="SanRefusal">Why the proxy offers no SAN access (its self-check), one reason per line, or null.</param>
/// <param name="Platform">Where the agent runs, <see cref="ProxyPlatforms"/> (null: an agent from before, Linux).</param>
public sealed record ProxyInfo(
    Guid Id, string Name, string? HostName, string? SmbiosUuid, string? BiosSerial, IReadOnlyList<string> Capabilities, string? InitiatorName = null, string? SanRefusal = null,
    string? Platform = null);

/// <summary>What a proxy agent can do, as its hello names it.</summary>
public static class ProxyCapabilities
{
    /// <summary>Serves a disk hot-added to its VM (vSphere HotAdd): only a Linux agent in a VM.</summary>
    public const string HotAdd = "hotadd-v1";

    /// <summary>Serves opened devices as NBD over tunnels.</summary>
    public const string Nbd = "nbd-v1";

    /// <summary>Reads array snapshot LUNs presented to its host object (the SAN self-check passed).</summary>
    public const string San = "san-v1";
}

/// <summary>Where a proxy agent runs: the .NET runtime identifier of its build.</summary>
public static class ProxyPlatforms
{
    public const string Linux = "linux-x64";

    public const string Windows = "win-x64";

    /// <summary>The platform a hello names; an agent from before the field is Linux.</summary>
    public static string Of(string? platform) => string.Equals(platform, Windows, StringComparison.OrdinalIgnoreCase) ? Windows : Linux;

    public static bool IsWindows(string? platform) => Of(platform) == Windows;

    /// <summary>The platform of this process.</summary>
    public static string Current => OperatingSystem.IsWindows() ? Windows : Linux;
}

/// <summary>
/// The backup proxies as a provider uses them: which are connected, and a disk the hypervisor attached to one of them
/// (HotAdd, or a LUN presented to it) opened for reading or writing.
/// </summary>
public interface IProxyPool
{
    IReadOnlyList<ProxyInfo> Connected();

    /// <summary>
    /// Waits until the proxy sees a disk with <paramref name="serial"/> (and <paramref name="sizeBytes"/>), and opens it.
    /// Read-only disks are also set read-only in the proxy's kernel.
    /// </summary>
    Task<IProxyDisk> AttachAsync(Guid proxyId, string serial, long sizeBytes, bool writable, CancellationToken cancellationToken);

    /// <summary>
    /// The proxy logs in to an array's iSCSI target at the portals (once) and rescans, so a snapshot LUN presented to its
    /// host object appears; then <see cref="AttachAsync"/> with the LUN's WWN as serial opens it.
    /// </summary>
    Task LoginIscsiAsync(Guid proxyId, IReadOnlyList<string> portals, string targetIqn, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This proxy pool has no SAN access.");

    /// <summary>
    /// Whether the proxy opens a TCP connection to each iSCSI portal, within seconds, without logging in; null when the
    /// proxy cannot say (not connected, an older agent).
    /// </summary>
    Task<ProxyIscsiReach?> ProbeIscsiAsync(Guid proxyId, IReadOnlyList<string> portals, CancellationToken cancellationToken) =>
        Task.FromResult<ProxyIscsiReach?>(null);

    /// <summary>
    /// The proxy asks its storage for added and removed LUNs, e.g. after snapshots were unpresented (Windows keeps a
    /// vanished disk until the next rescan; Linux has deleted it already).
    /// </summary>
    Task RescanAsync(Guid proxyId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The answer of <see cref="IProxyPool.ProbeIscsiAsync"/>: the portals that answered, the ones that did not (with why),
/// and the proxy's own IPv4 addresses ("192.0.2.91/24 (ens192)").
/// </summary>
public sealed record ProxyIscsiReach(IReadOnlyList<string> Reached, IReadOnlyList<string> Unreached, IReadOnlyList<string> Addresses);

/// <summary>
/// A disk opened on a proxy. Disposing closes it and removes it from the proxy's kernel, which must happen before the
/// hypervisor detaches the disk.
/// </summary>
public interface IProxyDisk : IAsyncDisposable
{
    /// <summary>"proxy p1 (host proxy01) /dev/sdb", for messages.</summary>
    string Description { get; }

    long SizeBytes { get; }

    /// <summary>Reads the disk over <paramref name="connections"/> NBD streams through the proxy's tunnels.</summary>
    Task<IDiskReader> OpenReaderAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken);

    /// <summary>Writes the disk (attached writable) over <paramref name="connections"/> NBD streams through the proxy's tunnels.</summary>
    Task<IDiskBlockWriter> OpenWriterAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken);
}
