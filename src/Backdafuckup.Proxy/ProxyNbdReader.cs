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

/// <summary>A disk on a proxy, written over NBD streams through the proxy's WebSocket tunnels (a restore's new disk).</summary>
public sealed class ProxyNbdWriter(NbdConnectionPool pool, int maxConcurrentRequests) : IDiskBlockWriter
{
    public long Capacity => pool.Size;

    public int MaxRequestBytes => pool.MaxPayload;

    public int MaxConcurrentRequests => Math.Max(1, maxConcurrentRequests);

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => pool.WriteAsync(offset, data, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken) => pool.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => pool.DisposeAsync();
}
