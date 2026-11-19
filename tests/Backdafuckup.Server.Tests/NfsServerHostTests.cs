using System.Net;
using System.Net.Sockets;
using Backdafuckup.Core.Repository;
using Backdafuckup.InstantRecovery;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Server.InstantRecovery;
using Backdafuckup.Testing.Nfs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tests;

/// <summary>The NFS server of instant recovery as the backup server hosts it: off by default, bind errors that say why.</summary>
public sealed class NfsServerHostTests : IDisposable
{
    private const ulong ExportId = 0x0000_0042_0000_0001;
    private readonly DirectoryInfo _data = Directory.CreateTempSubdirectory("bdfu-nfshost-");
    private readonly IDataProtectionProvider _keys = new EphemeralDataProtectionProvider();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _data.Delete(recursive: true);

    [Fact]
    public async Task Off_by_default_opens_no_port_and_creates_nothing()
    {
        await using var host = Host(new InstantRecoveryOptions());
        await host.StartAsync(Ct);

        Assert.False(host.Status.Enabled);
        Assert.False(host.Status.Listening);
        Assert.Contains("InstantRecovery:Enabled", host.Status.Error, StringComparison.Ordinal);
        Assert.False((await host.EnsureListeningAsync(Ct)).Listening);
        Assert.False(Directory.Exists(host.OverlayDirectory));
    }

    [Fact]
    public async Task Serves_portmapper_mount_and_nfs_and_tells_which_ports_a_client_reached()
    {
        await using var host = Host(Loopback());
        await host.StartAsync(Ct);
        var status = host.Status;
        Assert.True(status.Listening, status.Error);
        Assert.Equal("own", status.Portmapper);
        Assert.All(new[] { status.PortmapperPort, status.MountPort, status.NfsPort }, p => Assert.NotEqual(0, p));
        using var fs = AddExport(host, "/bdfu/ir-test", [IPAddress.Loopback]);

        // The portmapper answers where MOUNT and NFS are.
        await using (var portmapper = await RpcClient.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, status.PortmapperPort), Ct))
        {
            Assert.Equal((uint)status.MountPort, await GetPortAsync(portmapper, MountProgram.ProgramNumber, PortmapperProgram.ProtocolTcp));
            Assert.Equal((uint)status.MountPort, await GetPortAsync(portmapper, MountProgram.ProgramNumber, PortmapperProgram.ProtocolUdp));
            Assert.Equal((uint)status.NfsPort, await GetPortAsync(portmapper, Nfs3Program.ProgramNumber, PortmapperProgram.ProtocolTcp));
        }

        await using (var mount = await RpcClient.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, status.MountPort), Ct))
        {
            var reply = await mount.CallAsync(MountProgram.ProgramNumber, 3, 1, w => w.WriteString("/bdfu/ir-test"), Ct);
            Assert.Equal(0u, reply.ReadUInt32());
            Assert.True(reply.ReadOpaque(64).Length > 0);
        }

        var before = host.Clients.Describe(IPAddress.Loopback, host.ExpectedPorts);
        Assert.Contains($"reached {status.PortmapperPort}/tcp (portmapper), {status.MountPort}/tcp (MOUNT)", before, StringComparison.Ordinal);
        Assert.Contains($"but never {status.NfsPort}/tcp (NFS)", before, StringComparison.Ordinal);

        await using (var nfs = await RpcClient.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, status.NfsPort), Ct))
        {
            await nfs.CallAsync(Nfs3Program.ProgramNumber, 3, 0, null, Ct);
        }

        Assert.DoesNotContain("never", host.Clients.Describe(IPAddress.Loopback, host.ExpectedPorts), StringComparison.Ordinal);
        Assert.Contains("has not reached this server on any of", host.Clients.Describe(IPAddress.Parse("192.0.2.7"), host.ExpectedPorts), StringComparison.Ordinal);

        await host.StopAsync(Ct);
        Assert.False(host.Status.Listening);
        await Assert.ThrowsAnyAsync<IOException>(() => RpcClient.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, status.NfsPort), Ct));
    }

    [Fact]
    public async Task Stop_and_dispose_in_either_order_and_twice()
    {
        var host = Host(Loopback());
        await host.StartAsync(Ct);
        Assert.True(host.Status.Listening, host.Status.Error);
        await host.DisposeAsync();
        await host.StopAsync(Ct);
        await host.DisposeAsync();
        Assert.False(host.Status.Listening);

        var other = Host(Loopback());
        await other.StartAsync(Ct);
        await other.StopAsync(Ct);
        await other.DisposeAsync();
        await other.StopAsync(Ct);
    }

    [Fact]
    public async Task A_client_outside_the_allowlist_is_refused_and_named()
    {
        await using var host = Host(Loopback());
        await host.StartAsync(Ct);
        using var fs = AddExport(host, "/bdfu/ir-other", [IPAddress.Parse("192.0.2.11")]);

        await using var mount = await RpcClient.ConnectTcpAsync(new IPEndPoint(IPAddress.Loopback, host.Status.MountPort), Ct);
        var reply = await mount.CallAsync(MountProgram.ProgramNumber, 3, 1, w => w.WriteString("/bdfu/ir-other"), Ct);
        Assert.Equal((uint)NfsStatus.Access, reply.ReadUInt32());
        Assert.Contains("it was refused /bdfu/ir-other (not in the export's allowlist)", host.Clients.Describe(IPAddress.Loopback, host.ExpectedPorts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_in_use_names_the_program_that_holds_it_and_the_next_try_succeeds()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var options = Loopback();
        options.NfsPort = port;
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.False(host.Status.Listening);
        var error = host.Status.Error!;
        Assert.Contains($"127.0.0.1:{port}/tcp (NFS)", error, StringComparison.Ordinal);
        Assert.Contains("in use by", error, StringComparison.Ordinal);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
        {
            Assert.Contains($"(pid {Environment.ProcessId})", error, StringComparison.Ordinal);
        }

        blocker.Stop();
        var status = await host.EnsureListeningAsync(Ct);
        Assert.True(status.Listening, status.Error);
        Assert.Equal(port, status.NfsPort);
    }

    [Fact]
    public async Task Port_111_without_the_capability_says_how_to_grant_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && !CanBindBelow1024(), "Needs Linux without the right to bind ports below 1024.");
        var options = Loopback();
        options.PortmapperMode = PortmapperMode.Own;
        options.PortmapperPort = 111;
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.False(host.Status.Listening);
        Assert.Contains("127.0.0.1:111/tcp (portmapper)", host.Status.Error, StringComparison.Ordinal);
        Assert.Contains("CAP_NET_BIND_SERVICE", host.Status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listen_address_this_server_does_not_have_is_named()
    {
        var options = Loopback();
        options.ListenAddress = "192.0.2.250";
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.False(host.Status.Listening);
        Assert.Contains("192.0.2.250 is not an address of this server", host.Status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_overlay_directory_that_cannot_be_created_is_named_and_the_server_keeps_running()
    {
        var file = Path.Combine(_data.FullName, "a-file");
        await File.WriteAllTextAsync(file, "not a directory", Ct);
        var options = Loopback();
        options.OverlayDirectory = Path.Combine(file, "overlays");
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.False(host.Status.Listening);
        Assert.Contains($"Cannot create the overlay directory {options.OverlayDirectory}", host.Status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_handle_key_survives_a_restart_and_is_stored_protected()
    {
        byte[] first;
        await using (var host = Host(Loopback()))
        {
            first = host.HandleKey;
        }

        await using var again = Host(Loopback());
        Assert.Equal(first, again.HandleKey);
        Assert.Equal(32, first.Length);
        var stored = await File.ReadAllTextAsync(Path.Combine(again.OverlayDirectory, "handle-key.protected"), Ct);
        Assert.DoesNotContain(Convert.ToBase64String(first), stored, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Registers_with_the_system_rpcbind_and_removes_it_on_stop()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "rpcbind exists on Linux only.");
        await using var rpcbind = await RpcbindContainer.StartAsync(Ct);
        var options = Loopback();
        options.PortmapperMode = PortmapperMode.System;
        options.RpcbindSocket = rpcbind.SocketPath;
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.True(host.Status.Listening, host.Status.Error);
        Assert.Equal("rpcbind", host.Status.Portmapper);
        var system = new SystemPortmapper(rpcbind.SocketPath);
        Assert.Equal(host.Status.NfsPort, await system.GetPortAsync(Nfs3Program.ProgramNumber, 3, ProtocolType.Tcp, AddressFamily.InterNetwork, Ct));
        Assert.Equal(host.Status.MountPort, await system.GetPortAsync(MountProgram.ProgramNumber, 3, ProtocolType.Tcp, AddressFamily.InterNetwork, Ct));
        Assert.Equal(host.Status.MountPort, await system.GetPortAsync(MountProgram.ProgramNumber, 3, ProtocolType.Udp, AddressFamily.InterNetwork, Ct));
        var listed = await rpcbind.ListAsync(Ct);
        Assert.True(listed.Contains($"{100003,10}{3,5}{"tcp",6}{host.Status.NfsPort,7}", StringComparison.Ordinal), listed);
        Assert.True(listed.Contains($"{100005,10}{3,5}{"udp",6}{host.Status.MountPort,7}", StringComparison.Ordinal), listed);

        await host.StopAsync(Ct);
        Assert.Null(await system.GetPortAsync(Nfs3Program.ProgramNumber, 3, ProtocolType.Tcp, AddressFamily.InterNetwork, Ct));
        Assert.Null(await system.GetPortAsync(MountProgram.ProgramNumber, 3, ProtocolType.Tcp, AddressFamily.InterNetwork, Ct));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Another_nfs_server_registered_with_rpcbind_is_named()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "rpcbind exists on Linux only.");
        await using var rpcbind = await RpcbindContainer.StartAsync(Ct);
        var system = new SystemPortmapper(rpcbind.SocketPath);
        Assert.True(await system.SetAsync(Nfs3Program.ProgramNumber, 3, ProtocolType.Tcp, new IPEndPoint(IPAddress.Any, 2049), Ct));
        var options = Loopback();
        options.PortmapperMode = PortmapperMode.System;
        options.RpcbindSocket = rpcbind.SocketPath;
        await using var host = Host(options);

        await host.StartAsync(Ct);
        Assert.False(host.Status.Listening);
        Assert.Contains("maps NFS v3/tcp to port 2049 already (registered by ", host.Status.Error, StringComparison.Ordinal);
        Assert.Contains("another NFS server runs on this machine", host.Status.Error, StringComparison.Ordinal);
        Assert.Equal(2049, await system.GetPortAsync(Nfs3Program.ProgramNumber, 3, ProtocolType.Tcp, AddressFamily.InterNetwork, Ct));
    }

    [Fact]
    public void Universal_addresses_round_trip()
    {
        Assert.Equal("0.0.0.0.8.1", SystemPortmapper.UniversalAddress(new IPEndPoint(IPAddress.Any, 2049)));
        Assert.Equal("10.0.0.5.78.80", SystemPortmapper.UniversalAddress(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 20048)));
        Assert.True(SystemPortmapper.TryParsePort("::1.0.111", out var port));
        Assert.Equal(111, port);
        Assert.False(SystemPortmapper.TryParsePort("", out _));
        Assert.False(SystemPortmapper.TryParsePort("garbage", out _));
    }

    private NfsServerHost Host(InstantRecoveryOptions options) =>
        new(Options.Create(options), new ServerPaths(_data.FullName, _data.FullName, Path.Combine(_data.FullName, "logs")), new CredentialProtector(_keys),
            NullLogger<NfsServerHost>.Instance, TimeProvider.System);

    private static InstantRecoveryOptions Loopback() => new()
    {
        Enabled = true,
        ListenAddress = "127.0.0.1",
        PortmapperMode = PortmapperMode.Own,
        PortmapperPort = 0,
        NfsPort = 0,
        MountPort = 0,
        FreeSpaceReserveBytes = 0,
    };

    private InstantRecoveryFileSystem AddExport(NfsServerHost host, string path, IPAddress[] allowed)
    {
        var directory = Path.Combine(host.OverlayDirectory, Guid.NewGuid().ToString("N"));
        var fs = InstantRecoveryFileSystem.Open(directory, ExportId, host.HandleKey, _ => throw new InvalidOperationException("No base images in this test."),
            new InstantRecoveryFsOptions { FreeSpaceReserveBytes = 0 });
        host.AddExport(new NfsExport(path, ExportId, fs, new HashSet<IPAddress>(allowed)));
        return fs;
    }

    private static async Task<uint> GetPortAsync(RpcClient portmapper, uint program, uint protocol)
    {
        var reply = await portmapper.CallAsync(PortmapperProgram.ProgramNumber, 2, 3, w => w.WriteUInt32(program).WriteUInt32(3).WriteUInt32(protocol).WriteUInt32(0), Ct);
        return reply.ReadUInt32();
    }

    private static bool CanBindBelow1024()
    {
        try
        {
            var start = int.Parse(File.ReadAllText("/proc/sys/net/ipv4/ip_unprivileged_port_start").Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (start <= 111)
            {
                return true;
            }

            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return Environment.UserName == "root";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or SocketException)
        {
            return false;
        }
    }
}
