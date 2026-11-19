using System.Net;
using System.Security.Cryptography;
using Backdafuckup.Nfs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Backdafuckup.InstantRecovery.Tests;

/// <summary>
/// Conformance with the Linux kernel's NFSv3 client (spec 2, task 3): a privileged container mounts the export (TCP, hard,
/// no locking, like ESXi), works on it with shell tools and fio with verification, and survives a server restart.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KernelClientTests : IDisposable
{
    private const ulong ExportId = 0x4b43;
    private const long DiskSize = 64L * 1024 * 1024;
    private readonly string _root = Directory.CreateTempSubdirectory("bdfu-ir-kernel-").FullName;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _disk = RandomNumberGenerator.GetBytes((int)DiskSize);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Base(byte[] data) : IBaseImage
    {
        public long Size => data.Length;

        public long AllocatedBytes => data.Length;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            data.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The file system and an NFS server for it on a loopback port (the same port again after a restart).</summary>
    private sealed class Served : IAsyncDisposable
    {
        private Served(InstantRecoveryFileSystem fs, RpcListener listener)
        {
            FileSystem = fs;
            Listener = listener;
        }

        public InstantRecoveryFileSystem FileSystem { get; }

        public RpcListener Listener { get; }

        public static Served Start(string root, byte[] key, byte[] disk, int port)
        {
            var fs = InstantRecoveryFileSystem.Open(root, ExportId, key, _ => new Base(disk), new InstantRecoveryFsOptions { FreeSpaceReserveBytes = 0 });
            fs.CreateBaseFile("vm01/disk0-flat.vmdk", "rp/scsi0:0", disk.Length);
            var exports = new NfsExportTable();
            exports.Add(new NfsExport("/bdfu/ir", ExportId, fs, AllowedClients: null));
            var portmapper = new PortmapperProgram();
            var dispatcher = new RpcDispatcher([portmapper, new MountProgram(exports), new Nfs3Program(exports, new NfsServerLimits())], replies: new RpcReplyCache(TimeSpan.FromMinutes(2)));
            var listener = new RpcListener(new IPEndPoint(IPAddress.Loopback, port), dispatcher, new RpcServerOptions(), udp: false);
            portmapper.Register(MountProgram.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, listener.Endpoint.Port);
            portmapper.Register(Nfs3Program.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, listener.Endpoint.Port);
            listener.Start();
            return new Served(fs, listener);
        }

        public async ValueTask DisposeAsync()
        {
            await Listener.DisposeAsync();
            FileSystem.Dispose();
        }
    }

    private static async Task<IFutureDockerImage> ImageAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "kernelnfs"))
            .WithName("backdafuckup/kernel-nfs-test:2")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(Ct);
        return image;
    }

    private static string Mount(int port) =>
        $"mkdir -p /mnt/ir && mount -t nfs -o vers=3,proto=tcp,port={port},mountport={port},mountproto=tcp,nolock,hard,timeo=20,retrans=5,rsize=1048576,wsize=1048576 " +
        "host.testcontainers.internal:/bdfu/ir /mnt/ir";

    private static async Task<(long Exit, string Out)> RunToEndAsync(IContainer container)
    {
        var exit = await container.GetExitCodeAsync(Ct);
        var (stdout, stderr) = await container.GetLogsAsync(ct: Ct);
        return (exit, stdout + "\n" + stderr);
    }

    [Fact]
    public async Task The_kernel_client_reads_the_disk_works_with_files_and_fio_verifies_its_writes()
    {
        await using var served = Served.Start(_root, _key, _disk, 0);
        var port = served.Listener.Endpoint.Port;
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)port, Ct);
        var script = string.Join(" && ",
            Mount(port),
            "echo SHA $(sha256sum /mnt/ir/vm01/disk0-flat.vmdk | cut -c1-64)",
            "mkdir /mnt/ir/t && echo hello > /mnt/ir/t/a && mv /mnt/ir/t/a /mnt/ir/t/b && test \"$(cat /mnt/ir/t/b)\" = hello && ln -s b /mnt/ir/t/c 2>/dev/null; rm /mnt/ir/t/b && rmdir /mnt/ir/t",
            "for i in $(seq 1 200); do echo $i > /mnt/ir/vm01/f$i; done && test $(ls /mnt/ir/vm01 | wc -l) = 201 && rm /mnt/ir/vm01/f*",
            "dd if=/dev/zero of=/mnt/ir/vm01/vm01.vswp bs=1M count=8 status=none && truncate -s 1M /mnt/ir/vm01/vm01.vswp && test $(stat -c %s /mnt/ir/vm01/vm01.vswp) = 1048576",
            "fio --name=verify --filename=/mnt/ir/vm01/disk0-flat.vmdk --rw=randwrite --bs=4k --size=32m --offset=16m --verify=crc32c --do_verify=1 " +
            "--direct=1 --ioengine=libaio --iodepth=16 --output-format=terse --minimal > /tmp/fio.txt && echo FIO-OK",
            "fio --name=seq --filename=/mnt/ir/vm01/seq.bin --rw=write --bs=1m --size=48m --verify=md5 --do_verify=1 --end_fsync=1 --minimal > /dev/null && echo FIO-SEQ-OK",
            "sync && umount /mnt/ir && echo DONE");
        await using var container = new ContainerBuilder(await ImageAsync()).WithPrivileged(true).WithEntrypoint("sh", "-c").WithCommand(script)
            .WithWaitStrategy(Wait.ForUnixContainer()).Build();
        await container.StartAsync(Ct);
        var (exit, output) = await RunToEndAsync(container);
        Assert.True(exit == 0, $"exit {exit}\n{output}");
        Assert.Contains($"SHA {Convert.ToHexStringLower(SHA256.HashData(_disk))}", output, StringComparison.Ordinal);
        Assert.Contains("FIO-OK", output, StringComparison.Ordinal);
        Assert.Contains("FIO-SEQ-OK", output, StringComparison.Ordinal);
        Assert.Contains("DONE", output, StringComparison.Ordinal);

        // Outside the region fio wrote, the disk is still the restore point's.
        var handle = (await served.FileSystem.LookupAsync(new NfsCaller(null, new IPEndPoint(IPAddress.Loopback, 0)),
            (await served.FileSystem.LookupAsync(new NfsCaller(null, new IPEndPoint(IPAddress.Loopback, 0)), served.FileSystem.RootHandle, "vm01", Ct)).Handle, "disk0-flat.vmdk", Ct)).Handle;
        var head = await served.FileSystem.ReadAsync(new NfsCaller(null, new IPEndPoint(IPAddress.Loopback, 0)), handle, 0, 1 << 20, Ct);
        Assert.True(head.Data.Span.SequenceEqual(_disk.AsSpan(0, 1 << 20)));
    }

    [Fact]
    public async Task A_server_restart_in_the_middle_of_fio_causes_no_stale_handles_and_no_lost_writes()
    {
        var served = Served.Start(_root, _key, _disk, 0);
        var port = served.Listener.Endpoint.Port;
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)port, Ct);

        // fio writes at a limited rate (about 16 s), so the restart falls into the writes; then it verifies everything.
        var script = string.Join(" && ",
            Mount(port),
            "echo FIO-START",
            "fio --name=restart --filename=/mnt/ir/vm01/disk0-flat.vmdk --rw=randwrite --bs=64k --size=48m --rate=3m --verify=crc32c --do_verify=1 " +
            "--ioengine=psync --minimal > /tmp/fio.txt 2>&1 || (cat /tmp/fio.txt; dmesg 2>/dev/null | tail -5; false)",
            "echo FIO-OK",
            "sync && umount /mnt/ir && echo DONE");
        await using var container = new ContainerBuilder(await ImageAsync()).WithPrivileged(true).WithEntrypoint("sh", "-c").WithCommand(script)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("FIO-START")).Build();
        await container.StartAsync(Ct);

        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        var verifier = served.FileSystem.WriteVerifier;
        await served.DisposeAsync(); // the server goes away with unstable writes in flight
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        served = Served.Start(_root, _key, _disk, port);
        await using var restarted = served;
        Assert.NotEqual(verifier, restarted.FileSystem.WriteVerifier);

        var (exit, output) = await RunToEndAsync(container);
        Assert.True(exit == 0, $"exit {exit}\n{output}");
        Assert.Contains("FIO-OK", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DONE", output, StringComparison.Ordinal);
    }
}
