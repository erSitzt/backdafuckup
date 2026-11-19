using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Backdafuckup.Nfs.Tests;

/// <summary>The NFSv3 server library (instant recovery spec, task 1): XDR, RPC, portmapper, MOUNT and NFS3 over real sockets.</summary>
public sealed class NfsProtocolTests
{
    private const ulong ExportId = 0x1122334455667788;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RpcContext Local = new(new IPEndPoint(IPAddress.Loopback, 900), "tcp");

    // ---- XDR and record marking ----

    [Fact]
    public void Xdr_pads_opaque_data_and_refuses_lengths_beyond_their_limit()
    {
        var writer = new XdrWriter().WriteString("abcde").WriteUInt64(0x0102030405060708).WriteBool(true);
        Assert.Equal(Convert.FromHexString("00000005616263646500000001020304050607080000000" + "1"), writer.ToArray());
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(("abcde", 0x0102030405060708UL, true), (reader.ReadString(16), reader.ReadUInt64(), reader.ReadBool()));
        Assert.Contains("exceeds its limit of 4", Assert.Throws<XdrException>(() => new XdrReader(writer.ToArray()).ReadString(4)).Message, StringComparison.Ordinal);
        Assert.Contains("ends after 3 bytes", Assert.Throws<XdrException>(() => new XdrReader(new byte[3]).ReadUInt32()).Message, StringComparison.Ordinal);
        Assert.Throws<XdrException>(() => new XdrReader(Convert.FromHexString("00000002")).ReadBool());
    }

    [Fact]
    public async Task A_record_in_fragments_is_joined_and_one_beyond_the_limit_is_refused()
    {
        var stream = new MemoryStream(Convert.FromHexString("00000002" + "AABB" + "80000003" + "CCDDEE"));
        Assert.Equal(Convert.FromHexString("AABBCCDDEE"), await RpcRecordMarking.ReadRecordAsync(stream, 100, Ct));
        Assert.Null(await RpcRecordMarking.ReadRecordAsync(stream, 100, Ct));

        var large = new MemoryStream(Convert.FromHexString("80001000"));
        await Assert.ThrowsAsync<RpcRecordTooLargeException>(async () => await RpcRecordMarking.ReadRecordAsync(large, 1024, Ct));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await RpcRecordMarking.ReadRecordAsync(new MemoryStream(Convert.FromHexString("80000004AA")), 100, Ct));
    }

    // ---- RPC ----

    [Fact]
    public async Task A_portmapper_getport_call_encoded_by_hand_gets_the_exact_reply()
    {
        var portmapper = new PortmapperProgram();
        portmapper.Register(Nfs3Program.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, 2049);
        var dispatcher = new RpcDispatcher([portmapper]);

        // xid 0x0a0b0c0d, CALL, rpcvers 2, prog 100000, vers 2, proc 3 (GETPORT), AUTH_NONE, verifier AUTH_NONE, mapping {100003, 3, TCP, 0}
        var call = Convert.FromHexString(
            "0A0B0C0D" + "00000000" + "00000002" + "000186A0" + "00000002" + "00000003" + "00000000" + "00000000" + "00000000" + "00000000" +
            "000186A3" + "00000003" + "00000006" + "00000000");
        var reply = await dispatcher.DispatchAsync(call, Local, Ct);

        // xid, REPLY, MSG_ACCEPTED, verifier AUTH_NONE (empty), SUCCESS, port 2049
        Assert.Equal(Convert.FromHexString("0A0B0C0D" + "00000001" + "00000000" + "00000000" + "00000000" + "00000000" + "00000801"), reply);
    }

    [Fact]
    public async Task Rpc_errors_follow_the_specification()
    {
        var dispatcher = new RpcDispatcher([new PortmapperProgram()]);
        static byte[] Call(uint rpcVersion, uint program, uint version, uint procedure, uint flavor = 0, byte[]? args = null)
        {
            var w = new XdrWriter().WriteUInt32(7).WriteUInt32(0).WriteUInt32(rpcVersion).WriteUInt32(program).WriteUInt32(version).WriteUInt32(procedure)
                .WriteUInt32(flavor).WriteOpaque([]).WriteUInt32(0).WriteOpaque([]);
            return [.. w.ToArray(), .. args ?? []];
        }

        async Task<string> Reply(byte[] call) => Convert.ToHexString((await dispatcher.DispatchAsync(call, Local, Ct))!);

        Assert.Equal("00000007" + "00000001" + "00000001" + "00000000" + "00000002" + "00000002", await Reply(Call(3, 100000, 2, 0))); // RPC_MISMATCH 2..2
        Assert.EndsWith("00000001", await Reply(Call(2, 100003, 3, 0)), StringComparison.Ordinal); // PROG_UNAVAIL
        Assert.EndsWith("00000002" + "00000002" + "00000002", await Reply(Call(2, 100000, 4, 0)), StringComparison.Ordinal); // PROG_MISMATCH 2..2
        Assert.EndsWith("00000003", await Reply(Call(2, 100000, 2, 5)), StringComparison.Ordinal); // PROC_UNAVAIL (CALLIT)
        Assert.EndsWith("00000004", await Reply(Call(2, 100000, 2, 3, args: [0, 0, 1])), StringComparison.Ordinal); // GARBAGE_ARGS
        Assert.Equal("00000007" + "00000001" + "00000001" + "00000001" + "00000001", await Reply(Call(2, 100000, 2, 0, flavor: 3))); // AUTH_ERROR BADCRED (AUTH_DH)
        Assert.Null(await dispatcher.DispatchAsync(Convert.FromHexString("0000000700000001"), Local, Ct)); // a reply, not a call
    }

    // ---- MOUNT and NFS over TCP ----

    [Fact]
    public async Task Mount_gives_allowed_hosts_the_root_handle_and_refuses_others()
    {
        await using var server = await Server.StartAsync(allowed: new HashSet<IPAddress> { IPAddress.Loopback });
        await using var client = await TestClient.ConnectAsync(server.Endpoint);

        var mounted = await client.CallAsync(MountProgram.ProgramNumber, 3, 1, w => w.WriteString("/bdfu/test"));
        Assert.Equal(0u, mounted.ReadUInt32());
        Assert.Equal(server.FileSystem.RootHandle.ToArray(), mounted.ReadOpaque(64).ToArray());
        Assert.Equal((1u, RpcConstants.AuthSys), (mounted.ReadUInt32(), mounted.ReadUInt32()));
        Assert.Equal((uint)NfsStatus.NoEnt, (await client.CallAsync(MountProgram.ProgramNumber, 3, 1, w => w.WriteString("/bdfu/other"))).ReadUInt32());

        var exported = await client.CallAsync(MountProgram.ProgramNumber, 3, 5, _ => { });
        Assert.Equal((true, "/bdfu/test", true, "127.0.0.1", false, false),
            (exported.ReadBool(), exported.ReadString(1024), exported.ReadBool(), exported.ReadString(255), exported.ReadBool(), exported.ReadBool()));

        // Another host (the allowlist names only 10.26.0.21 now): refused, and reported.
        await using var strict = await Server.StartAsync(allowed: new HashSet<IPAddress> { IPAddress.Parse("10.26.0.21") });
        await using var other = await TestClient.ConnectAsync(strict.Endpoint);
        Assert.Equal((uint)NfsStatus.Access, (await other.CallAsync(MountProgram.ProgramNumber, 3, 1, w => w.WriteString("/bdfu/test"))).ReadUInt32());
        Assert.Equal([("127.0.0.1", "/bdfu/test")], strict.Refused);
        var getattr = await other.CallAsync(Nfs3Program.ProgramNumber, 3, 1, w => w.WriteOpaque(strict.FileSystem.RootHandle.Span));
        Assert.Equal((uint)NfsStatus.Access, getattr.ReadUInt32());
    }

    [Fact]
    public async Task Files_are_created_written_read_renamed_and_removed()
    {
        await using var server = await Server.StartAsync();
        await using var client = await TestClient.ConnectAsync(server.Endpoint);
        var root = server.FileSystem.RootHandle.ToArray();

        // CREATE GUARDED, then again: EXIST.
        var created = await client.Nfs(8, w => Dir(w, root, "disk0-flat.vmdk").WriteUInt32(1).Sattr());
        Assert.Equal(0u, created.ReadUInt32());
        Assert.True(created.ReadBool());
        var file = created.ReadOpaque(64).ToArray();
        Assert.Equal((uint)NfsStatus.Exist, (await client.Nfs(8, w => Dir(w, root, "disk0-flat.vmdk").WriteUInt32(1).Sattr())).ReadUInt32());

        // WRITE FILE_SYNC at an offset, READ back with eof, the verifier and size.
        var data = Encoding.ASCII.GetBytes("hello from ESXi");
        var written = await client.Nfs(7, w => w.WriteOpaque(file).WriteUInt64(4096).WriteUInt32((uint)data.Length).WriteUInt32(2).WriteOpaque(data));
        Assert.Equal(0u, written.ReadUInt32());
        SkipWcc(written);
        Assert.Equal(((uint)data.Length, 2u, 0x0102030405060708UL), (written.ReadUInt32(), written.ReadUInt32(), written.ReadUInt64()));
        var read = await client.Nfs(6, w => w.WriteOpaque(file).WriteUInt64(4096).WriteUInt32(1 << 20));
        Assert.Equal(0u, read.ReadUInt32());
        Assert.Equal(4096UL + (ulong)data.Length, SkipPostOp(read)!.Value.Size);
        Assert.Equal(((uint)data.Length, true), (read.ReadUInt32(), read.ReadBool()));
        Assert.Equal(data, read.ReadOpaque(1 << 20).ToArray());

        // LOOKUP finds it; COMMIT reaches the file system.
        var lookup = await client.Nfs(3, w => Dir(w, root, "disk0-flat.vmdk"));
        Assert.Equal(0u, lookup.ReadUInt32());
        Assert.Equal(file, lookup.ReadOpaque(64).ToArray());
        Assert.Equal(0u, (await client.Nfs(21, w => w.WriteOpaque(file).WriteUInt64(0).WriteUInt32(0))).ReadUInt32());
        Assert.Equal(1, server.FileSystem.Commits);

        // RENAME, REMOVE, then the handle is stale; MKDIR and RMDIR.
        Assert.Equal(0u, (await client.Nfs(14, w => Dir(Dir(w, root, "disk0-flat.vmdk"), root, "renamed.vmdk"))).ReadUInt32());
        Assert.Equal((uint)NfsStatus.NoEnt, (await client.Nfs(3, w => Dir(w, root, "disk0-flat.vmdk"))).ReadUInt32());
        Assert.Equal(0u, (await client.Nfs(12, w => Dir(w, root, "renamed.vmdk"))).ReadUInt32());
        Assert.Equal((uint)NfsStatus.Stale, (await client.Nfs(1, w => w.WriteOpaque(file))).ReadUInt32());
        Assert.Equal(0u, (await client.Nfs(9, w => Dir(w, root, "vm1").Sattr())).ReadUInt32());
        Assert.Equal(0u, (await client.Nfs(13, w => Dir(w, root, "vm1"))).ReadUInt32());

        // What is not supported or not allowed.
        Assert.Equal((uint)NfsStatus.NotSupported, (await client.Nfs(15, w => w.WriteOpaque(root))).ReadUInt32());
        Assert.Equal((uint)NfsStatus.NameTooLong, (await client.Nfs(3, w => Dir(w, root, new string('a', 256)))).ReadUInt32());
        Assert.Equal((uint)NfsStatus.BadHandle, (await client.Nfs(1, w => w.WriteOpaque([9, 9, 9]))).ReadUInt32());
        server.Exports.Remove(ExportId);
        Assert.Equal((uint)NfsStatus.Stale, (await client.Nfs(1, w => w.WriteOpaque(root))).ReadUInt32());
    }

    [Fact]
    public async Task Readdirplus_pages_by_the_clients_limits_and_continues_at_the_cookie()
    {
        await using var server = await Server.StartAsync();
        await using var client = await TestClient.ConnectAsync(server.Endpoint);
        var root = server.FileSystem.RootHandle.ToArray();
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(0u, (await client.Nfs(8, w => Dir(w, root, $"file-{i:00}.log").WriteUInt32(0).Sattr())).ReadUInt32());
        }

        var names = new List<string>();
        ulong cookie = 0, verifier = 0;
        var pages = 0;
        while (true)
        {
            var c = cookie;
            var v = verifier;
            var reply = await client.Nfs(17, w => w.WriteOpaque(root).WriteUInt64(c).WriteUInt64(v).WriteUInt32(512).WriteUInt32(4096));
            Assert.Equal(0u, reply.ReadUInt32());
            SkipPostOp(reply);
            verifier = reply.ReadUInt64();
            pages++;
            while (reply.ReadBool())
            {
                reply.ReadUInt64();
                names.Add(reply.ReadString(255));
                cookie = reply.ReadUInt64();
                SkipPostOp(reply);
                if (reply.ReadBool())
                {
                    reply.ReadOpaque(64);
                }
            }

            if (reply.ReadBool())
            {
                break;
            }
        }

        Assert.True(pages > 2, $"{pages} page(s)");
        Assert.Equal([".", "..", .. Enumerable.Range(0, 40).Select(i => $"file-{i:00}.log")], names);
        Assert.Equal((uint)NfsStatus.TooSmall, (await client.Nfs(17, w => w.WriteOpaque(root).WriteUInt64(0).WriteUInt64(0).WriteUInt32(8).WriteUInt32(64))).ReadUInt32());
    }

    [Fact]
    public async Task Calls_on_one_connection_run_concurrently_and_replies_come_back_out_of_order()
    {
        await using var server = await Server.StartAsync();
        await using var client = await TestClient.ConnectAsync(server.Endpoint);
        var root = server.FileSystem.RootHandle.ToArray();
        var created = await client.Nfs(8, w => Dir(w, root, "slow").WriteUInt32(0).Sattr());
        created.ReadUInt32();
        created.ReadBool();
        var file = created.ReadOpaque(64).ToArray();

        server.FileSystem.HoldReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = client.Nfs(6, w => w.WriteOpaque(file).WriteUInt64(0).WriteUInt32(10));
        var fast = await client.Nfs(1, w => w.WriteOpaque(root)); // answered while the READ waits
        Assert.Equal(0u, fast.ReadUInt32());
        Assert.False(slow.IsCompleted);
        server.FileSystem.HoldReads.SetResult();
        Assert.Equal(0u, (await slow).ReadUInt32());
    }

    [Fact]
    public async Task A_record_beyond_the_limit_closes_the_connection_and_udp_answers_the_portmapper()
    {
        await using var server = await Server.StartAsync();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(server.Endpoint, Ct);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Convert.FromHexString("80400000"), Ct); // 4 MiB announced
            Assert.Equal(0, await stream.ReadAsync(new byte[16], Ct));
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var call = new XdrWriter().WriteUInt32(42).WriteUInt32(0).WriteUInt32(2).WriteUInt32(PortmapperProgram.ProgramNumber).WriteUInt32(2).WriteUInt32(3)
            .WriteUInt32(0).WriteOpaque([]).WriteUInt32(0).WriteOpaque([])
            .WriteUInt32(MountProgram.ProgramNumber).WriteUInt32(3).WriteUInt32(PortmapperProgram.ProtocolUdp).WriteUInt32(0).ToArray();
        await udp.SendAsync(call, server.Endpoint, Ct);
        var answer = await udp.ReceiveAsync(Ct);
        var reader = new XdrReader(answer.Buffer);
        Assert.Equal((42u, 1u, 0u, 0u, 0u, 0u), (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
        Assert.Equal((uint)server.Endpoint.Port, reader.ReadUInt32());
    }

    // ---- helpers ----

    private static XdrWriter Dir(XdrWriter w, byte[] directory, string name) => w.WriteOpaque(directory).WriteString(name);

    private static NfsWccAttributes? SkipPostOp(XdrReader r)
    {
        if (!r.ReadBool())
        {
            return null;
        }

        var type = r.ReadUInt32();
        r.ReadUInt32();
        r.ReadUInt32();
        r.ReadUInt32();
        r.ReadUInt32();
        var size = r.ReadUInt64();
        r.ReadUInt64();
        r.ReadUInt64();
        r.ReadUInt64();
        r.ReadUInt64();
        var atime = new NfsTime(r.ReadUInt32(), r.ReadUInt32());
        var mtime = new NfsTime(r.ReadUInt32(), r.ReadUInt32());
        var ctime = new NfsTime(r.ReadUInt32(), r.ReadUInt32());
        Assert.True(type is 1 or 2);
        return new NfsWccAttributes(size, mtime, ctime);
    }

    private static void SkipWcc(XdrReader r)
    {
        if (r.ReadBool())
        {
            r.ReadUInt64();
            r.ReadUInt64();
            r.ReadUInt64();
        }

        SkipPostOp(r);
    }

    /// <summary>Portmapper, MOUNT and NFS on one loopback port, with one in-memory export at /bdfu/test.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private RpcListener _listener = null!;

        public MemoryFileSystem FileSystem { get; } = new(ExportId);

        public NfsExportTable Exports { get; } = new();

        public List<(string Host, string Path)> Refused { get; } = [];

        public IPEndPoint Endpoint => _listener.Endpoint;

        public static Task<Server> StartAsync(IReadOnlySet<IPAddress>? allowed = null)
        {
            var server = new Server();
            server.Exports.Refused = (address, path) => server.Refused.Add((address.ToString(), path));
            server.Exports.Add(new NfsExport("/bdfu/test", ExportId, server.FileSystem, allowed));
            var portmapper = new PortmapperProgram();
            var dispatcher = new RpcDispatcher([portmapper, new MountProgram(server.Exports), new Nfs3Program(server.Exports, new NfsServerLimits())]);
            server._listener = new RpcListener(new IPEndPoint(IPAddress.Loopback, 0), dispatcher, new RpcServerOptions(), udp: true);
            foreach (var (program, version) in new[] { (MountProgram.ProgramNumber, 3u), (Nfs3Program.ProgramNumber, 3u) })
            {
                portmapper.Register(program, version, PortmapperProgram.ProtocolTcp, server.Endpoint.Port);
                portmapper.Register(program, version, PortmapperProgram.ProtocolUdp, server.Endpoint.Port);
            }

            server._listener.Start();
            return Task.FromResult(server);
        }

        public ValueTask DisposeAsync() => _listener.DisposeAsync();
    }

    /// <summary>A minimal RPC client over TCP: AUTH_SYS as root, calls pipelined, replies matched by xid.</summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
        private readonly Task _reader;
        private int _xid;

        private TestClient(TcpClient tcp)
        {
            _tcp = tcp;
            _stream = tcp.GetStream();
            _reader = Task.Run(ReadAsync);
        }

        public static async Task<TestClient> ConnectAsync(IPEndPoint endpoint)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(endpoint, Ct);
            return new TestClient(tcp);
        }

        public Task<XdrReader> Nfs(uint procedure, Action<XdrWriter> args) => CallAsync(Nfs3Program.ProgramNumber, 3, procedure, args);

        public async Task<XdrReader> CallAsync(uint program, uint version, uint procedure, Action<XdrWriter> args)
        {
            var xid = (uint)Interlocked.Increment(ref _xid);
            var credential = new XdrWriter().WriteUInt32(0).WriteString("esx01").WriteUInt32(0).WriteUInt32(0).WriteUInt32(0);
            var call = new XdrWriter().WriteUInt32(xid).WriteUInt32(0).WriteUInt32(2).WriteUInt32(program).WriteUInt32(version).WriteUInt32(procedure)
                .WriteUInt32(RpcConstants.AuthSys).WriteOpaque(credential.Written.Span).WriteUInt32(0).WriteOpaque([]);
            args(call);
            var reply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[xid] = reply;
            await _write.WaitAsync(Ct);
            try
            {
                await RpcRecordMarking.WriteRecordAsync(_stream, call.Written, Ct);
            }
            finally
            {
                _write.Release();
            }

            var reader = new XdrReader(await reply.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            Assert.Equal((xid, 1u, 0u, 0u, 0u, 0u), (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32()));
            return reader;
        }

        private async Task ReadAsync()
        {
            while (await RpcRecordMarking.ReadRecordAsync(_stream, 4 << 20, CancellationToken.None) is { } record)
            {
                if (_pending.TryRemove(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record), out var waiting))
                {
                    waiting.SetResult(record);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            try
            {
                await _reader;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            _write.Dispose();
        }
    }
}

internal static class SattrExtensions
{
    /// <summary>An empty sattr3 (nothing set).</summary>
    public static XdrWriter Sattr(this XdrWriter w) => w.WriteBool(false).WriteBool(false).WriteBool(false).WriteBool(false).WriteUInt32(0).WriteUInt32(0);
}
