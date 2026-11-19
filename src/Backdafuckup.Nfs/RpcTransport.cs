using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Backdafuckup.Nfs;

/// <summary>A record is larger than the server takes; the connection is closed.</summary>
public sealed class RpcRecordTooLargeException(string message) : IOException(message);

/// <summary>RPC over TCP: record marking (RFC 5531, section 11), fragments of up to 2^31-1 bytes, the last one flagged.</summary>
public static class RpcRecordMarking
{
    private const uint LastFragment = 0x8000_0000;

    /// <summary>The next record, or null when the stream ends cleanly between records.</summary>
    public static async ValueTask<byte[]?> ReadRecordAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var record = new MemoryStream();
        while (true)
        {
            var read = await ReadAtLeastAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (read == 0 && record.Length == 0)
            {
                return null;
            }

            if (read < 4)
            {
                throw new EndOfStreamException("The connection ended inside a record marker.");
            }

            var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
            var length = marker & ~LastFragment;
            if (record.Length + length > (uint)maxBytes)
            {
                throw new RpcRecordTooLargeException($"A record of more than {maxBytes} bytes ({record.Length + length} so far).");
            }

            var fragment = new byte[length];
            if (await ReadAtLeastAsync(stream, fragment, cancellationToken).ConfigureAwait(false) < length)
            {
                throw new EndOfStreamException("The connection ended inside a record.");
            }

            record.Write(fragment);
            if ((marker & LastFragment) != 0)
            {
                return record.ToArray();
            }
        }
    }

    /// <summary>Writes one record as a single fragment.</summary>
    public static async ValueTask WriteRecordAsync(Stream stream, ReadOnlyMemory<byte> record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[4 + record.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, LastFragment | (uint)record.Length);
        record.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
}

/// <summary>How the RPC transports behave.</summary>
public sealed record RpcServerOptions
{
    /// <summary>Largest call taken (an NFS WRITE of 1 MiB plus its header fits).</summary>
    public int MaxRecordBytes { get; init; } = (2 * 1024 * 1024) + 4096;

    /// <summary>Calls of one TCP connection processed at the same time; replies may leave out of order (each names its xid).</summary>
    public int MaxInFlight { get; init; } = 128;
}

/// <summary>Serves RPC calls on TCP connections and UDP datagrams through a <see cref="RpcDispatcher"/>.</summary>
public static class RpcTransport
{
    /// <summary>Serves one TCP connection until the client closes it; calls run concurrently, replies are written one at a time.</summary>
    public static async Task ServeStreamAsync(Stream stream, RpcDispatcher dispatcher, RpcContext context, RpcServerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(options);
        using var slots = new SemaphoreSlim(options.MaxInFlight);
        using var writeLock = new SemaphoreSlim(1, 1);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task>();
        try
        {
            while (true)
            {
                var record = await RpcRecordMarking.ReadRecordAsync(stream, options.MaxRecordBytes, connection.Token).ConfigureAwait(false);
                if (record is null)
                {
                    break;
                }

                await slots.WaitAsync(connection.Token).ConfigureAwait(false);
                running.RemoveAll(t => t.IsCompleted);
                running.Add(Task.Run(async () =>
                {
                    try
                    {
                        var reply = await dispatcher.DispatchAsync(record, context, connection.Token).ConfigureAwait(false);
                        if (reply is not null)
                        {
                            await writeLock.WaitAsync(connection.Token).ConfigureAwait(false);
                            try
                            {
                                await RpcRecordMarking.WriteRecordAsync(stream, reply, connection.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                writeLock.Release();
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                    {
                        await connection.CancelAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The connection went away while calls ran.
            }
        }
    }

    /// <summary>Serves datagrams on a bound UDP socket (one call per datagram, the reply to its sender).</summary>
    public static async Task ServeDatagramsAsync(Socket socket, RpcDispatcher dispatcher, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(dispatcher);
        var buffer = new byte[65536];
        var any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // ICMP port unreachable of an earlier reply, and the like.
                continue;
            }

            var remote = (IPEndPoint)received.RemoteEndPoint;
            var reply = await dispatcher.DispatchAsync(buffer.AsMemory(0, received.ReceivedBytes).ToArray(), new RpcContext(remote, "udp"), cancellationToken).ConfigureAwait(false);
            if (reply is not null)
            {
                try
                {
                    await socket.SendToAsync(reply, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // The client is gone; it retransmits if it still cares.
                }
            }
        }
    }
}

/// <summary>
/// One TCP listener (and optionally a UDP socket) on an address and port, serving a dispatcher. Reports every connection
/// with its remote address, so that a failed mount can say which hosts reached the server and which did not.
/// </summary>
public sealed class RpcListener : IAsyncDisposable
{
    private readonly RpcDispatcher _dispatcher;
    private readonly RpcServerOptions _options;
    private readonly Action<IPEndPoint>? _connected;
    private readonly TcpListener _tcp;
    private readonly Socket? _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = [];
    private int _connections;

    /// <summary>Binds now (so a port conflict fails here, naming the address and port); serving starts with <see cref="Start"/>.</summary>
    public RpcListener(IPEndPoint endpoint, RpcDispatcher dispatcher, RpcServerOptions options, bool udp, Action<IPEndPoint>? connected = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connected = connected;
        _tcp = new TcpListener(endpoint);
        try
        {
            _tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _tcp.Start(backlog: 64);
            if (udp)
            {
                _udp = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _udp.Bind(new IPEndPoint(endpoint.Address, ((IPEndPoint)_tcp.LocalEndpoint).Port));
            }
        }
        catch (SocketException ex)
        {
            _tcp.Stop();
            _udp?.Dispose();
            throw new IOException($"Cannot listen on {endpoint} ({(udp ? "TCP and UDP" : "TCP")}): {ex.Message} (error {ex.SocketErrorCode}).", ex);
        }

        Endpoint = (IPEndPoint)_tcp.LocalEndpoint;
    }

    /// <summary>The bound endpoint (with the actual port when 0 was asked for).</summary>
    public IPEndPoint Endpoint { get; }

    public int Connections => Volatile.Read(ref _connections);

    public void Start()
    {
        _loops.Add(Task.Run(() => AcceptAsync(_stop.Token)));
        if (_udp is not null)
        {
            _loops.Add(Task.Run(() => RpcTransport.ServeDatagramsAsync(_udp, _dispatcher, _stop.Token)));
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    client.NoDelay = true;
                    var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                    _connected?.Invoke(remote);
                    Interlocked.Increment(ref _connections);
                    try
                    {
                        await RpcTransport.ServeStreamAsync(client.GetStream(), _dispatcher, new RpcContext(remote, "tcp"), _options, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        // The client went away, or sent a record beyond the limit.
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _connections);
                    }
                }
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _tcp.Stop();
        _udp?.Dispose();
        try
        {
            await Task.WhenAll(_loops).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }

        _stop.Dispose();
    }
}
