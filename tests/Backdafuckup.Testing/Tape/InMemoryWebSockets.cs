using System.IO.Pipelines;
using System.Net.WebSockets;

namespace Backdafuckup.Testing.Tape;

/// <summary>Two connected WebSockets in memory: a server and a client end, for protocol tests without a network.</summary>
public static class InMemoryWebSockets
{
    public static (WebSocket Server, WebSocket Client) Create()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader, toClient.Writer), new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        var client = WebSocket.CreateFromStream(new DuplexStream(toClient.Reader, toServer.Writer), new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
        return (server, client);
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _in = reader.AsStream();
        private readonly Stream _out = writer.AsStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _in.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _in.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _out.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _out.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush() => _out.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _out.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _in.Dispose();
                _out.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
