using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Backdafuckup.Nbd;

/// <summary>
/// One NBD client connection: fixed newstyle negotiation (optionally upgraded with STARTTLS), NBD_OPT_GO with
/// fallback to NBD_OPT_EXPORT_NAME, simple replies, pipelined requests. Thread-safe.
/// </summary>
public sealed class NbdClient : IAsyncDisposable
{
    private const int StateWaiting = 0;
    private const int StateReceiving = 1;
    private const int StateAbandoned = 2;
    private const int MaxOptionReplyLength = 16 * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _inFlightLimit;
    private readonly ConcurrentDictionary<ulong, PendingRequest> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _receiveLoop;
    private long _nextCookie;
    private int _inFlight;
    private int _disposed;
    private volatile Exception? _fault;

    private NbdClient(TcpClient tcp, Stream stream, NegotiationResult negotiated, int maxInFlight)
    {
        _tcp = tcp;
        _stream = stream;
        Size = negotiated.Size;
        TransmissionFlags = negotiated.TransmissionFlags;
        MinimumBlockSize = negotiated.MinimumBlockSize;
        PreferredBlockSize = negotiated.PreferredBlockSize;
        MaxPayload = negotiated.MaximumBlockSize > 0
            ? (int)Math.Min(negotiated.MaximumBlockSize, NbdProtocol.DefaultMaxPayload)
            : NbdProtocol.DefaultMaxPayload;
        UsesTls = negotiated.UsesTls;
        NegotiatedWithGo = negotiated.UsedGo;
        _inFlightLimit = new SemaphoreSlim(Math.Max(1, maxInFlight));
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Export size in bytes.</summary>
    public long Size { get; }

    public ushort TransmissionFlags { get; }

    public int MinimumBlockSize { get; }

    public int PreferredBlockSize { get; }

    /// <summary>Largest payload of a single request (reads larger than this are split).</summary>
    public int MaxPayload { get; }

    public bool UsesTls { get; }

    /// <summary>Negotiated TLS version (None without TLS).</summary>
    public System.Security.Authentication.SslProtocols TlsProtocol => _stream is SslStream ssl ? ssl.SslProtocol : System.Security.Authentication.SslProtocols.None;

    /// <summary>True if NBD_OPT_GO was used, false if the server needed the NBD_OPT_EXPORT_NAME fallback.</summary>
    public bool NegotiatedWithGo { get; }

    public bool IsReadOnly => (TransmissionFlags & NbdProtocol.TransReadOnly) != 0;

    public bool CanMultiConn => (TransmissionFlags & NbdProtocol.TransCanMultiConn) != 0;

    public bool CanFlush => (TransmissionFlags & NbdProtocol.TransSendFlush) != 0;

    public bool IsFaulted => _fault is not null || Volatile.Read(ref _disposed) != 0;

    public int InFlight => Volatile.Read(ref _inFlight);

    public static async Task<NbdClient> ConnectAsync(NbdConnectOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TlsMode == NbdTlsMode.Required && options.ServerCertificateValidation is null)
        {
            throw new ArgumentException("TLS requires a server certificate validation callback.", nameof(options));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            var (stream, negotiated) = await NegotiateAsync(tcp.GetStream(), options, timeout.Token).ConfigureAwait(false);
            return new NbdClient(tcp, stream, negotiated, options.MaxInFlight);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var connected = tcp.Connected;
            tcp.Dispose();
            throw new TimeoutException(connected
                ? $"NBD connection to {options.Host}:{options.Port}: the server accepted the TCP connection but did not complete the NBD/TLS handshake within {options.ConnectTimeout.TotalSeconds:0} s."
                : $"NBD connection to {options.Host}:{options.Port}: no answer to the TCP connection within {options.ConnectTimeout.TotalSeconds:0} s. The connection attempts are dropped, not refused: either a firewall between the backup server and the host (or the host's iptables) blocks TCP {options.Port}, or the host cannot answer because it has no route back to the backup server (its interface in this network has no gateway, and the backup server is not in that subnet).");
        }
        catch (EndOfStreamException ex)
        {
            tcp.Dispose();
            throw new NbdException($"NBD server {options.Host}:{options.Port} closed the connection during negotiation.", ex);
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new NbdException($"Cannot connect to NBD server {options.Host}:{options.Port}: {ex.Message} ({ex.SocketErrorCode}).", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="offset"/>.</summary>
    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        CheckRange(offset, destination.Length);
        var position = 0;
        while (position < destination.Length)
        {
            var length = Math.Min(MaxPayload, destination.Length - position);
            await ExecuteAsync(NbdProtocol.CmdRead, offset + position, length, destination.Slice(position, length), default, cancellationToken)
                .ConfigureAwait(false);
            position += length;
        }
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("The NBD export is read-only.");
        }

        CheckRange(offset, data.Length);
        var position = 0;
        while (position < data.Length)
        {
            var length = Math.Min(MaxPayload, data.Length - position);
            await ExecuteAsync(NbdProtocol.CmdWrite, offset + position, length, default, data.Slice(position, length), cancellationToken)
                .ConfigureAwait(false);
            position += length;
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (CanFlush)
        {
            await ExecuteAsync(NbdProtocol.CmdFlush, 0, 0, default, default, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_fault is null)
        {
            try
            {
                // NBD_CMD_DISC has no reply. Best effort.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _sendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    var cookie = (ulong)Interlocked.Increment(ref _nextCookie);
                    await SendRequestAsync(NbdProtocol.CmdDisc, cookie, 0, 0, default).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The connection is going away anyway.
            }
        }

        Fault(new ObjectDisposedException(nameof(NbdClient)));
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _tcp.Dispose();
        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or NbdException)
        {
            // Expected when the stream is closed under the receive loop.
        }

        // _shutdown is intentionally not disposed: concurrent callers may still observe or cancel it.
    }

    private void CheckRange(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range {offset}+{length} is outside the export (size {Size}).");
        }
    }

    private async ValueTask ExecuteAsync(ushort command, long offset, int length, Memory<byte> readDestination, ReadOnlyMemory<byte> writeData, CancellationToken cancellationToken)
    {
        ThrowIfFaulted();
        await _inFlightLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlight);
        try
        {
            var request = new PendingRequest(command, offset, length, readDestination);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfFaulted();
                var cookie = (ulong)Interlocked.Increment(ref _nextCookie);
                _pending[cookie] = request;
                try
                {
                    // Never pass the caller's token here: a half-written request would corrupt the stream.
                    await SendRequestAsync(command, cookie, offset, length, writeData).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _pending.TryRemove(cookie, out _);
                    Fault(ex);
                    throw new NbdException("Sending the NBD request failed.", ex);
                }
            }
            finally
            {
                _sendLock.Release();
            }

            await using (cancellationToken.Register(static state => ((PendingRequest)state!).TryAbandon(), request).ConfigureAwait(false))
            {
                await request.Completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _inFlightLimit.Release();
        }
    }

    private async ValueTask SendRequestAsync(ushort command, ulong cookie, long offset, int length, ReadOnlyMemory<byte> data)
    {
        var header = ArrayPool<byte>.Shared.Rent(NbdProtocol.RequestHeaderSize);
        try
        {
            var span = header.AsSpan(0, NbdProtocol.RequestHeaderSize);
            BinaryPrimitives.WriteUInt32BigEndian(span, NbdProtocol.RequestMagic);
            BinaryPrimitives.WriteUInt16BigEndian(span[4..], 0);
            BinaryPrimitives.WriteUInt16BigEndian(span[6..], command);
            BinaryPrimitives.WriteUInt64BigEndian(span[8..], cookie);
            BinaryPrimitives.WriteUInt64BigEndian(span[16..], (ulong)offset);
            BinaryPrimitives.WriteUInt32BigEndian(span[24..], (uint)length);
            await _stream.WriteAsync(header.AsMemory(0, NbdProtocol.RequestHeaderSize), _shutdown.Token).ConfigureAwait(false);
            if (!data.IsEmpty)
            {
                await _stream.WriteAsync(data, _shutdown.Token).ConfigureAwait(false);
            }

            await _stream.FlushAsync(_shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[NbdProtocol.SimpleReplyHeaderSize];
        byte[]? scratch = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _shutdown.Token).ConfigureAwait(false);
                var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (magic == NbdProtocol.StructuredReplyMagic)
                {
                    throw new NbdException("Server sent a structured reply although structured replies were not negotiated.");
                }

                if (magic != NbdProtocol.SimpleReplyMagic)
                {
                    throw new NbdException($"Invalid NBD reply magic 0x{magic:x8}.");
                }

                var error = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
                var cookie = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (!_pending.TryRemove(cookie, out var request))
                {
                    throw new NbdException($"NBD reply for unknown cookie {cookie}.");
                }

                if (error != 0)
                {
                    request.Complete(new NbdCommandException(request.Command, error, request.Offset, request.Length));
                    continue;
                }

                if (request.Command != NbdProtocol.CmdRead)
                {
                    request.Complete(null);
                    continue;
                }

                if (request.TryBeginReceive())
                {
                    await _stream.ReadExactlyAsync(request.Destination, _shutdown.Token).ConfigureAwait(false);
                    request.Complete(null);
                }
                else
                {
                    // The caller gave up on this read; drain the payload without touching its buffer.
                    scratch ??= new byte[256 * 1024];
                    var remaining = request.Length;
                    while (remaining > 0)
                    {
                        var n = Math.Min(remaining, scratch.Length);
                        await _stream.ReadExactlyAsync(scratch.AsMemory(0, n), _shutdown.Token).ConfigureAwait(false);
                        remaining -= n;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void Fault(Exception ex)
    {
        if (_fault is null)
        {
            _fault = ex;
        }

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already shut down.
        }

        foreach (var cookie in _pending.Keys)
        {
            if (_pending.TryRemove(cookie, out var request))
            {
                request.Complete(new NbdException("The NBD connection failed.", ex));
            }
        }
    }

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            // A connection pool may close a connection while other readers still hold it; they retry elsewhere.
            throw new NbdException("The NBD connection was closed.");
        }

        if (_fault is { } fault)
        {
            throw new NbdException("The NBD connection is no longer usable.", fault);
        }
    }

    private static async Task<(Stream Stream, NegotiationResult Result)> NegotiateAsync(Stream stream, NbdConnectOptions options, CancellationToken cancellationToken)
    {
        var greeting = new byte[18];
        await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt64BigEndian(greeting) != NbdProtocol.NbdMagic)
        {
            throw new NbdException("Not an NBD server (missing NBDMAGIC).");
        }

        var second = BinaryPrimitives.ReadUInt64BigEndian(greeting.AsSpan(8));
        if (second == NbdProtocol.OldStyleMagic)
        {
            throw new NbdException("The NBD server uses old-style negotiation, which is not supported.");
        }

        if (second != NbdProtocol.IHaveOpt)
        {
            throw new NbdException("Unexpected NBD handshake magic.");
        }

        var handshakeFlags = BinaryPrimitives.ReadUInt16BigEndian(greeting.AsSpan(16));
        if ((handshakeFlags & NbdProtocol.FlagFixedNewstyle) == 0)
        {
            throw new NbdException("The NBD server does not support fixed newstyle negotiation.");
        }

        var noZeroes = (handshakeFlags & NbdProtocol.FlagNoZeroes) != 0;
        var clientFlags = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            clientFlags,
            NbdProtocol.ClientFlagFixedNewstyle | (noZeroes ? NbdProtocol.ClientFlagNoZeroes : 0));
        await stream.WriteAsync(clientFlags, cancellationToken).ConfigureAwait(false);

        var usesTls = false;
        if (options.TlsMode == NbdTlsMode.Required)
        {
            await SendOptionAsync(stream, NbdProtocol.OptStartTls, [], cancellationToken).ConfigureAwait(false);
            var reply = await ReadOptionReplyAsync(stream, NbdProtocol.OptStartTls, cancellationToken).ConfigureAwait(false);
            if (reply.Type != NbdProtocol.RepAck)
            {
                throw new NbdOptionException(NbdProtocol.OptStartTls, reply.Type, reply.Message);
            }

            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, options.ServerCertificateValidation);
            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = options.TlsTargetHost ?? options.Host,
                    EnabledSslProtocols = options.TlsProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                },
                cancellationToken).ConfigureAwait(false);
            stream = ssl;
            usesTls = true;
        }

        var name = Encoding.UTF8.GetBytes(options.ExportName);
        var go = new byte[4 + name.Length + 2 + 2];
        BinaryPrimitives.WriteUInt32BigEndian(go, (uint)name.Length);
        name.CopyTo(go, 4);
        BinaryPrimitives.WriteUInt16BigEndian(go.AsSpan(4 + name.Length), 1);
        BinaryPrimitives.WriteUInt16BigEndian(go.AsSpan(6 + name.Length), NbdProtocol.InfoBlockSize);
        await SendOptionAsync(stream, NbdProtocol.OptGo, go, cancellationToken).ConfigureAwait(false);

        long size = -1;
        ushort transmissionFlags = 0;
        int minBlock = 1, preferredBlock = 4096;
        long maxBlock = 0;
        while (true)
        {
            var reply = await ReadOptionReplyAsync(stream, NbdProtocol.OptGo, cancellationToken).ConfigureAwait(false);
            if (reply.Type == NbdProtocol.RepInfo)
            {
                var data = reply.Data;
                if (data.Length < 2)
                {
                    throw new NbdException("Malformed NBD_REP_INFO.");
                }

                var infoType = BinaryPrimitives.ReadUInt16BigEndian(data);
                if (infoType == NbdProtocol.InfoExport && data.Length >= 12)
                {
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(2));
                    transmissionFlags = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10));
                }
                else if (infoType == NbdProtocol.InfoBlockSize && data.Length >= 14)
                {
                    minBlock = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(2));
                    preferredBlock = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(6));
                    maxBlock = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10));
                }

                continue;
            }

            if (reply.Type == NbdProtocol.RepAck)
            {
                if (size < 0)
                {
                    throw new NbdException("NBD server acknowledged NBD_OPT_GO without sending export information.");
                }

                return (stream, new NegotiationResult(size, transmissionFlags, minBlock, preferredBlock, maxBlock, usesTls, UsedGo: true));
            }

            if (reply.Type == NbdProtocol.RepErrUnsup)
            {
                break; // fall back to NBD_OPT_EXPORT_NAME
            }

            if ((reply.Type & NbdProtocol.RepFlagError) != 0)
            {
                throw new NbdOptionException(NbdProtocol.OptGo, reply.Type, reply.Message);
            }

            // Unknown non-error reply types must be ignored.
        }

        await SendOptionAsync(stream, NbdProtocol.OptExportName, name, cancellationToken).ConfigureAwait(false);
        var export = new byte[noZeroes ? 10 : 134];
        await stream.ReadExactlyAsync(export, cancellationToken).ConfigureAwait(false);
        size = (long)BinaryPrimitives.ReadUInt64BigEndian(export);
        transmissionFlags = BinaryPrimitives.ReadUInt16BigEndian(export.AsSpan(8));
        return (stream, new NegotiationResult(size, transmissionFlags, 1, 4096, 0, usesTls, UsedGo: false));
    }

    private static async Task SendOptionAsync(Stream stream, uint option, byte[] data, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 + data.Length];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, NbdProtocol.IHaveOpt);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), option);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), (uint)data.Length);
        data.CopyTo(buffer, 16);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OptionReply> ReadOptionReplyAsync(Stream stream, uint expectedOption, CancellationToken cancellationToken)
    {
        var header = new byte[20];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt64BigEndian(header) != NbdProtocol.OptionReplyMagic)
        {
            throw new NbdException("Invalid NBD option reply magic.");
        }

        var option = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        if (option != expectedOption)
        {
            throw new NbdException($"NBD option reply for option {option}, expected {expectedOption}.");
        }

        if (length > MaxOptionReplyLength)
        {
            throw new NbdException($"NBD option reply too large ({length} bytes).");
        }

        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return new OptionReply(type, data);
    }

    private sealed record NegotiationResult(long Size, ushort TransmissionFlags, int MinimumBlockSize, int PreferredBlockSize, long MaximumBlockSize, bool UsesTls, bool UsedGo);

    private sealed record OptionReply(uint Type, byte[] Data)
    {
        public string? Message => (Type & NbdProtocol.RepFlagError) != 0 && Data.Length > 0 ? Encoding.UTF8.GetString(Data) : null;
    }

    private sealed class PendingRequest(ushort command, long offset, int length, Memory<byte> destination)
    {
        private int _state = StateWaiting;

        public ushort Command { get; } = command;

        public long Offset { get; } = offset;

        public int Length { get; } = length;

        public Memory<byte> Destination { get; } = destination;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Called by the receive loop before writing into <see cref="Destination"/>.</summary>
        public bool TryBeginReceive() => Interlocked.CompareExchange(ref _state, StateReceiving, StateWaiting) == StateWaiting;

        /// <summary>Called on caller cancellation; only succeeds while nothing has been written to the buffer.</summary>
        public void TryAbandon()
        {
            if (Interlocked.CompareExchange(ref _state, StateAbandoned, StateWaiting) == StateWaiting)
            {
                Completion.TrySetCanceled();
            }
        }

        public void Complete(Exception? error)
        {
            if (error is null)
            {
                Completion.TrySetResult();
            }
            else
            {
                Completion.TrySetException(error);
            }
        }
    }
}
