using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Backdafuckup.Testing.Nbd;

/// <summary>Behaviour switches of <see cref="InProcessNbdServer"/>.</summary>
public sealed record NbdTestServerOptions
{
    public string ExportName { get; init; } = "export";

    public bool SupportGo { get; init; } = true;

    public bool AdvertiseNoZeroes { get; init; } = true;

    public bool RequireTls { get; init; }

    public bool ReadOnly { get; init; } = true;

    /// <summary>Maximum block size announced via NBD_INFO_BLOCK_SIZE (0 = do not announce).</summary>
    public uint MaxBlockSize { get; init; }

    /// <summary>Random reply delay up to this many milliseconds (produces out-of-order replies).</summary>
    public int MaxReplyDelayMs { get; init; }

    /// <summary>Reads touching this offset fail with EIO (5).</summary>
    public long? FailReadAtOffset { get; init; }
}

/// <summary>
/// Minimal NBD server for tests (fixed newstyle, STARTTLS, GO/EXPORT_NAME, simple replies, out-of-order replies).
/// Implemented from the public-domain protocol specification.
/// </summary>
public sealed class InProcessNbdServer : IAsyncDisposable
{
    private const ulong NbdMagic = 0x4e42444d41474943;
    private const ulong IHaveOpt = 0x49484156454F5054;
    private const ulong OptionReplyMagic = 0x0003e889045565a9;

    private readonly TcpListener _listener;
    private readonly byte[] _data;
    private readonly NbdTestServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> _connections = new();
    private readonly Task _acceptLoop;
    private int _connectionIds;
    private int _totalConnections;

    public InProcessNbdServer(byte[] data, NbdTestServerOptions? options = null)
    {
        _data = data;
        _options = options ?? new NbdTestServerOptions();
        Certificate = CreateCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public X509Certificate2 Certificate { get; }

    public byte[] Data => _data;

    public int TotalConnections => Volatile.Read(ref _totalConnections);

    public int ActiveConnections => _connections.Count;

    /// <summary>Hard-closes all open connections (simulates a network failure).</summary>
    public void DropAllConnections()
    {
        foreach (var (id, client) in _connections)
        {
            try
            {
                client.Client?.Close(0);
            }
            catch (ObjectDisposedException)
            {
                // The connection is closing concurrently.
            }

            _connections.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropAllConnections();
        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
        Certificate.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=nbd-test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var id = Interlocked.Increment(ref _connectionIds);
            Interlocked.Increment(ref _totalConnections);
            _connections[id] = client;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(client);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or EndOfStreamException or System.Security.Authentication.AuthenticationException)
                {
                }
                finally
                {
                    _connections.TryRemove(id, out _);
                    client.Dispose();
                }
            });
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var ct = _stop.Token;
        Stream stream = client.GetStream();
        var greeting = new byte[18];
        BinaryPrimitives.WriteUInt64BigEndian(greeting, NbdMagic);
        BinaryPrimitives.WriteUInt64BigEndian(greeting.AsSpan(8), IHaveOpt);
        BinaryPrimitives.WriteUInt16BigEndian(greeting.AsSpan(16), (ushort)(1 | (_options.AdvertiseNoZeroes ? 2 : 0)));
        await stream.WriteAsync(greeting, ct);

        var clientFlagsBuffer = new byte[4];
        await stream.ReadExactlyAsync(clientFlagsBuffer, ct);
        var noZeroes = (BinaryPrimitives.ReadUInt32BigEndian(clientFlagsBuffer) & 2) != 0;
        var tls = false;

        while (true)
        {
            var header = new byte[16];
            await stream.ReadExactlyAsync(header, ct);
            if (BinaryPrimitives.ReadUInt64BigEndian(header) != IHaveOpt)
            {
                return;
            }

            var option = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
            var data = new byte[length];
            await stream.ReadExactlyAsync(data, ct);

            switch (option)
            {
                case 5: // STARTTLS
                    await ReplyAsync(stream, option, 1, [], ct);
                    var ssl = new SslStream(stream, false);
                    await ssl.AuthenticateAsServerAsync(Certificate, false, false);
                    stream = ssl;
                    tls = true;
                    break;

                case 7 when !_options.SupportGo:
                    await ReplyAsync(stream, option, (1u << 31) | 1, [], ct);
                    break;

                case 7: // GO
                    if (_options.RequireTls && !tls)
                    {
                        await ReplyAsync(stream, option, (1u << 31) | 5, Encoding.UTF8.GetBytes("TLS required"), ct);
                        break;
                    }

                    var nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    var name = Encoding.UTF8.GetString(data, 4, nameLength);
                    if (name != _options.ExportName)
                    {
                        await ReplyAsync(stream, option, (1u << 31) | 6, Encoding.UTF8.GetBytes("unknown export"), ct);
                        break;
                    }

                    if (_options.MaxBlockSize > 0)
                    {
                        var block = new byte[14];
                        BinaryPrimitives.WriteUInt16BigEndian(block, 3);
                        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(2), 1);
                        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(6), 4096);
                        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(10), _options.MaxBlockSize);
                        await ReplyAsync(stream, option, 3, block, ct);
                    }

                    var info = new byte[12];
                    BinaryPrimitives.WriteUInt16BigEndian(info, 0);
                    BinaryPrimitives.WriteUInt64BigEndian(info.AsSpan(2), (ulong)_data.Length);
                    BinaryPrimitives.WriteUInt16BigEndian(info.AsSpan(10), TransmissionFlags());
                    await ReplyAsync(stream, option, 3, info, ct);
                    await ReplyAsync(stream, option, 1, [], ct);
                    await TransmissionAsync(stream, ct);
                    return;

                case 1: // EXPORT_NAME
                    if (_options.RequireTls && !tls)
                    {
                        return;
                    }

                    var export = new byte[noZeroes ? 10 : 134];
                    BinaryPrimitives.WriteUInt64BigEndian(export, (ulong)_data.Length);
                    BinaryPrimitives.WriteUInt16BigEndian(export.AsSpan(8), TransmissionFlags());
                    await stream.WriteAsync(export, ct);
                    await TransmissionAsync(stream, ct);
                    return;

                default:
                    await ReplyAsync(stream, option, (1u << 31) | 1, [], ct);
                    break;
            }
        }
    }

    private ushort TransmissionFlags() => (ushort)(1 | 4 | (1 << 8) | (_options.ReadOnly ? 2 : 0));

    private static async Task ReplyAsync(Stream stream, uint option, uint type, byte[] data, CancellationToken ct)
    {
        var buffer = new byte[20 + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, OptionReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), option);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), type);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(16), (uint)data.Length);
        data.CopyTo(buffer, 20);
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    private async Task TransmissionAsync(Stream stream, CancellationToken ct)
    {
        var sendLock = new SemaphoreSlim(1, 1);
        var inFlight = new List<Task>();
        var header = new byte[28];
        while (true)
        {
            await stream.ReadExactlyAsync(header, ct);
            var type = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
            var cookie = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(16));
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24));

            switch (type)
            {
                case 2: // DISC
                    await Task.WhenAll(inFlight);
                    return;

                case 1: // WRITE
                    var payload = new byte[length];
                    await stream.ReadExactlyAsync(payload, ct);
                    uint writeError = _options.ReadOnly ? 1u : 0u;
                    if (writeError == 0)
                    {
                        payload.CopyTo(_data, offset);
                    }

                    inFlight.Add(SendReplyAsync(stream, sendLock, cookie, writeError, null, ct));
                    break;

                case 0: // READ
                    var fail = _options.FailReadAtOffset is { } bad && bad >= offset && bad < offset + length;
                    var tooBig = _options.MaxBlockSize > 0 && length > _options.MaxBlockSize;
                    var outOfRange = offset + length > _data.Length;
                    uint readError = fail ? 5u : tooBig || outOfRange ? 22u : 0u;
                    var bytes = readError == 0 ? _data.AsMemory((int)offset, length) : (ReadOnlyMemory<byte>?)null;
                    inFlight.Add(SendReplyAsync(stream, sendLock, cookie, readError, bytes, ct));
                    break;

                default: // FLUSH and anything else: success
                    inFlight.Add(SendReplyAsync(stream, sendLock, cookie, 0, null, ct));
                    break;
            }

            inFlight.RemoveAll(t => t.IsCompleted);
        }
    }

    private async Task SendReplyAsync(Stream stream, SemaphoreSlim sendLock, ulong cookie, uint error, ReadOnlyMemory<byte>? payload, CancellationToken ct)
    {
        if (_options.MaxReplyDelayMs > 0)
        {
            await Task.Delay(Random.Shared.Next(_options.MaxReplyDelayMs + 1), ct);
        }

        var reply = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(reply, 0x67446698);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4), error);
        BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(8), cookie);
        await sendLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(reply, ct);
            if (payload is { } data)
            {
                await stream.WriteAsync(data, ct);
            }

            await stream.FlushAsync(ct);
        }
        finally
        {
            sendLock.Release();
        }
    }
}
