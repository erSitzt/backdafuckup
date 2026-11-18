using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Core.Proxies;

/// <summary>A connected backup proxy: who it is and where it runs (the SMBIOS UUID finds its VM in the hypervisor).</summary>
public sealed record ProxyInfo(Guid Id, string Name, string? HostName, string? SmbiosUuid, string? BiosSerial, IReadOnlyList<string> Capabilities);

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
}

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
