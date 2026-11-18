using Backdafuckup.Core.DataMover;
using Backdafuckup.Nbd;

namespace Backdafuckup.Proxy;

/// <summary>A disk on a proxy, read over NBD streams through the proxy's WebSocket tunnels (transport "hotadd" or "san").</summary>
public sealed class ProxyNbdReader(NbdConnectionPool pool, string transportName, int maxConcurrentRequests) : IDiskReader
{
    public long Capacity => pool.Size;

    public string TransportName { get; } = transportName;

    public int MaxRequestBytes => pool.MaxPayload;

    public int MaxConcurrentRequests => Math.Max(1, maxConcurrentRequests);

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) => pool.ReadAsync(offset, destination, cancellationToken);

    public ValueTask DisposeAsync() => pool.DisposeAsync();
}
