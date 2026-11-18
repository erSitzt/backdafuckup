using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Backdafuckup.Nbd;

namespace Backdafuckup.Nbd.Tests;

/// <summary>Our NBD server (the proxy agent's side of a disk tunnel) with our NBD client over a stream.</summary>
public sealed class NbdServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An export in memory; reads of <see cref="FailAt"/> fail with an I/O error.</summary>
    private sealed class MemoryExport(byte[] data, bool readOnly = false) : INbdExport
    {
        public byte[] Data { get; } = data;

        public long Size => Data.LongLength;

        public bool ReadOnly { get; } = readOnly;

        public long? FailAt { get; set; }

        public int Flushes;

        public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            // Small random delays make replies come back out of order.
            await Task.Delay(Random.Shared.Next(0, 3), cancellationToken);
            if (FailAt is { } fail && fail >= offset && fail < offset + destination.Length)
            {
                throw new IOException("medium error");
            }

            Data.AsMemory((int)offset, destination.Length).CopyTo(destination);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            data.CopyTo(Data.AsMemory((int)offset));
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Flushes);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteZeroesAsync(long offset, long length, CancellationToken cancellationToken)
        {
            Data.AsSpan((int)offset, (int)length).Clear();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A loopback listener serving <paramref name="export"/> on every connection.</summary>
    private static (int Port, Task Accepting, CancellationTokenSource Stop) Serve(INbdExport export, NbdServerOptions? options = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            using (listener)
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                await NbdServer.ServeAsync(client.GetStream(), export, options ?? new NbdServerOptions { ExportName = "disk" }, stop.Token);
                            }
                            catch (Exception ex) when (ex is IOException or OperationCanceledException or NbdException)
                            {
                                // The client went away.
                            }
                        }
                    });
                }
            }
        });
        return (((IPEndPoint)listener.LocalEndpoint).Port, accepting, stop);
    }

    private static async Task<NbdClient> ConnectAsync(int port, string name = "disk")
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
        return await NbdClient.ConnectAsync(tcp.GetStream(), new NbdConnectOptions { Host = "proxy p1 /dev/sdb", ExportName = name, TlsMode = NbdTlsMode.Disabled }, Ct);
    }

    [Fact]
    public async Task Reads_writes_zeroes_and_flushes_go_through_and_replies_may_come_out_of_order()
    {
        var export = new MemoryExport(RandomNumberGenerator.GetBytes(8 << 20));
        var original = export.Data.ToArray();
        var (port, accepting, stop) = Serve(export);
        await using (var client = await ConnectAsync(port))
        {
            Assert.Equal((8L << 20, false, true, true), (client.Size, client.IsReadOnly, client.CanFlush, client.CanWriteZeroes));
            Assert.True(client.NegotiatedWithGo);

            // 64 reads of 128 KiB at once.
            var reads = Enumerable.Range(0, 64).Select(async i =>
            {
                var buffer = new byte[128 << 10];
                await client.ReadAsync(i * (128L << 10), buffer, Ct);
                return buffer.AsSpan().SequenceEqual(original.AsSpan(i * (128 << 10), 128 << 10));
            });
            Assert.All(await Task.WhenAll(reads), Assert.True);

            var data = RandomNumberGenerator.GetBytes(300_000);
            await client.WriteAsync(1_000_000, data, Ct);
            await client.WriteZeroesAsync(5L << 20, 2L << 20, Ct);
            await client.FlushAsync(Ct);
            Assert.True(export.Data.AsSpan(1_000_000, data.Length).SequenceEqual(data));
            Assert.True(export.Data.AsSpan(5 << 20, 2 << 20).IndexOfAnyExcept((byte)0) < 0);
            Assert.Equal(1, export.Flushes);
        }

        await stop.CancelAsync();
        await accepting;
    }

    [Fact]
    public async Task A_failed_read_is_an_io_error_for_that_request_only_and_a_read_only_export_refuses_writes()
    {
        var export = new MemoryExport(RandomNumberGenerator.GetBytes(1 << 20), readOnly: true) { FailAt = 600_000 };
        var (port, accepting, stop) = Serve(export);
        await using (var client = await ConnectAsync(port))
        {
            Assert.True(client.IsReadOnly);
            var ex = await Assert.ThrowsAsync<NbdCommandException>(async () => await client.ReadAsync(512 << 10, new byte[256 << 10], Ct));
            Assert.Equal(NbdProtocol.ErrIo, ex.Error);

            // The connection goes on.
            var buffer = new byte[4096];
            await client.ReadAsync(0, buffer, Ct);
            Assert.True(buffer.AsSpan().SequenceEqual(export.Data.AsSpan(0, 4096)));
        }

        // A client that writes anyway gets EPERM (raw request on the wire).
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, Ct);
            var stream = tcp.GetStream();
            await HandshakeExportNameAsync(stream, "disk");
            var request = new byte[NbdProtocol.RequestHeaderSize + 4];
            BinaryPrimitives.WriteUInt32BigEndian(request, NbdProtocol.RequestMagic);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), NbdProtocol.CmdWrite);
            BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(8), 77);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(24), 4);
            await stream.WriteAsync(request, Ct);
            var reply = new byte[NbdProtocol.SimpleReplyHeaderSize];
            await stream.ReadExactlyAsync(reply, Ct);
            Assert.Equal((NbdProtocol.SimpleReplyMagic, NbdProtocol.ErrPerm, 77UL),
                (BinaryPrimitives.ReadUInt32BigEndian(reply), BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)), BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8))));
        }

        await stop.CancelAsync();
        await accepting;
    }

    [Fact]
    public async Task An_unknown_export_name_is_refused_and_several_connections_read_one_export()
    {
        var export = new MemoryExport(RandomNumberGenerator.GetBytes(4 << 20));
        var (port, accepting, stop) = Serve(export);

        var refused = await Assert.ThrowsAsync<NbdOptionException>(() => ConnectAsync(port, "other"));
        Assert.Equal(NbdProtocol.RepErrUnknown, refused.ReplyType);

        await using (var pool = await NbdConnectionPool.CreateAsync(_ => ConnectAsync(port), connections: 4, maxInFlightPerConnection: 4, Ct))
        {
            var copy = new byte[export.Data.Length];
            await Task.WhenAll(Enumerable.Range(0, 32).Select(i => pool.ReadAsync(i * (128L << 10), copy.AsMemory(i * (128 << 10), 128 << 10), Ct).AsTask()));
            Assert.True(copy.AsSpan().SequenceEqual(export.Data));
        }

        await stop.CancelAsync();
        await accepting;
    }

    /// <summary>Fixed newstyle with NBD_OPT_EXPORT_NAME (the old way), as a minimal client would do it.</summary>
    private static async Task HandshakeExportNameAsync(NetworkStream stream, string name)
    {
        var hello = new byte[18];
        await stream.ReadExactlyAsync(hello, Ct);
        Assert.Equal((NbdProtocol.NbdMagic, NbdProtocol.IHaveOpt), (BinaryPrimitives.ReadUInt64BigEndian(hello), BinaryPrimitives.ReadUInt64BigEndian(hello.AsSpan(8))));
        var flags = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(flags, NbdProtocol.ClientFlagFixedNewstyle | NbdProtocol.ClientFlagNoZeroes);
        await stream.WriteAsync(flags, Ct);
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var option = new byte[16 + nameBytes.Length];
        BinaryPrimitives.WriteUInt64BigEndian(option, NbdProtocol.IHaveOpt);
        BinaryPrimitives.WriteUInt32BigEndian(option.AsSpan(8), NbdProtocol.OptExportName);
        BinaryPrimitives.WriteUInt32BigEndian(option.AsSpan(12), (uint)nameBytes.Length);
        nameBytes.CopyTo(option, 16);
        await stream.WriteAsync(option, Ct);
        var answer = new byte[10];
        await stream.ReadExactlyAsync(answer, Ct);
    }
}
