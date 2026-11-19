using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Backdafuckup.Nfs;

/// <summary>An RPC call that the server refused (not accepted, or accepted with an error status).</summary>
public sealed class RpcCallException(string message) : IOException(message);

/// <summary>
/// A minimal RPC client over one stream (TCP or a Unix socket): one call at a time, AUTH_NONE, record marking. Enough for
/// talking to the system's portmapper; the NFS clients that matter are the hypervisors' own.
/// </summary>
public sealed class RpcClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly string _peer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _xid = (uint)Random.Shared.Next();

    private RpcClient(Stream stream, string peer)
    {
        _stream = stream;
        _peer = peer;
    }

    public static async Task<RpcClient> ConnectUnixAsync(string socketPath, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException($"Cannot connect to the Unix socket {socketPath}: {ex.Message} (error {ex.SocketErrorCode}).", ex);
        }

        return new RpcClient(new NetworkStream(socket, ownsSocket: true), socketPath);
    }

    public static async Task<RpcClient> ConnectTcpAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient(endpoint.AddressFamily);
        try
        {
            await tcp.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new IOException($"Cannot connect to {endpoint} (TCP): {ex.Message} (error {ex.SocketErrorCode}).", ex);
        }

        tcp.NoDelay = true;
        return new RpcClient(new OwningStream(tcp), endpoint.ToString());
    }

    /// <summary>Calls <paramref name="procedure"/> and returns a reader positioned at the results.</summary>
    public async Task<XdrReader> CallAsync(uint program, uint version, uint procedure, Action<XdrWriter>? arguments, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var xid = unchecked(++_xid);
            var call = new XdrWriter()
                .WriteUInt32(xid).WriteUInt32(RpcConstants.Call).WriteUInt32(RpcConstants.Version)
                .WriteUInt32(program).WriteUInt32(version).WriteUInt32(procedure)
                .WriteUInt32(RpcConstants.AuthNone).WriteOpaque([])
                .WriteUInt32(RpcConstants.AuthNone).WriteOpaque([]);
            arguments?.Invoke(call);
            await RpcRecordMarking.WriteRecordAsync(_stream, call.Written, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var record = await RpcRecordMarking.ReadRecordAsync(_stream, 1024 * 1024, cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException($"{_peer} closed the connection before it answered (program {program} version {version} procedure {procedure}).");
                if (record.Length < 4 || BinaryPrimitives.ReadUInt32BigEndian(record) != xid)
                {
                    continue;
                }

                return ParseReply(record, program, version, procedure);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private XdrReader ParseReply(byte[] record, uint program, uint version, uint procedure)
    {
        var reader = new XdrReader(record);
        reader.ReadUInt32();
        if (reader.ReadUInt32() != RpcConstants.Reply)
        {
            throw new RpcCallException($"{_peer} sent a call instead of a reply.");
        }

        var what = $"program {program} version {version} procedure {procedure} at {_peer}";
        if (reader.ReadUInt32() != RpcConstants.MsgAccepted)
        {
            throw new RpcCallException($"{_peer} denied the call ({what}).");
        }

        reader.ReadUInt32();
        reader.ReadOpaque(RpcConstants.MaxAuthBytes);
        var status = reader.ReadUInt32();
        return status == 0 ? reader : throw new RpcCallException($"{_peer} did not accept the call ({what}): {(RpcAcceptStatus)status}.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed class OwningStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _inner = tcp.GetStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
