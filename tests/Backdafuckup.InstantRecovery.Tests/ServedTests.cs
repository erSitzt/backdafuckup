using System.Net;
using System.Security.Cryptography;
using Backdafuckup.Nfs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Backdafuckup.InstantRecovery.Tests;

/// <summary>The instant recovery file system behind the NFS server, used by an independent client (libnfs in Docker).</summary>
[Trait("Category", "Integration")]
public sealed class ServedTests : IDisposable
{
    private const ulong ExportId = 0x42;
    private readonly string _root = Directory.CreateTempSubdirectory("bdfu-ir-served-").FullName;

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

    [Fact]
    public async Task Libnfs_reads_the_restore_point_disk_and_writes_beside_it()
    {
        var disk = RandomNumberGenerator.GetBytes(12 * 1024 * 1024 + 512);
        using var fs = InstantRecoveryFileSystem.Open(_root, ExportId, RandomNumberGenerator.GetBytes(32), _ => new Base(disk), new InstantRecoveryFsOptions { FreeSpaceReserveBytes = 0 });
        fs.CreateBaseFile("vm01/disk0-flat.vmdk", "rp/scsi0:0", disk.Length);
        fs.CreateFile("vm01/disk0.vmdk", "# Disk DescriptorFile\nversion=1\ncreateType=\"vmfs\"\n"u8);

        var exports = new NfsExportTable();
        exports.Add(new NfsExport("/bdfu/ir-0042", ExportId, fs, AllowedClients: null));
        var portmapper = new PortmapperProgram();
        var dispatcher = new RpcDispatcher([portmapper, new MountProgram(exports), new Nfs3Program(exports, new NfsServerLimits())], replies: new RpcReplyCache(TimeSpan.FromMinutes(2)));
        await using var listener = new RpcListener(new IPEndPoint(IPAddress.Loopback, 0), dispatcher, new RpcServerOptions(), udp: false);
        var port = listener.Endpoint.Port;
        portmapper.Register(MountProgram.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, port);
        portmapper.Register(Nfs3Program.ProgramNumber, 3, PortmapperProgram.ProtocolTcp, port);
        listener.Start();
        await TestcontainersSettings.ExposeHostPortsAsync((ushort)port, Ct);

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "libnfs"))
            .WithName("backdafuckup/libnfs-test:1")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(Ct);
        var url = "nfs://host.testcontainers.internal/bdfu/ir-0042";
        var query = $"?version=3&nfsport={port}&mountport={port}";
        var script = string.Join(" && ",
            $"nfs-cp '{url}/vm01/disk0-flat.vmdk{query}' /tmp/flat.bin",
            "echo SHA $(sha256sum /tmp/flat.bin | cut -c1-64)",
            "printf 'vmx' > /tmp/vm01.vmx",
            $"nfs-cp /tmp/vm01.vmx '{url}/vm01/vm01.vmx{query}'",
            $"echo LS; nfs-ls '{url}/vm01{query}'",
            "echo DONE");
        await using var container = new ContainerBuilder(image).WithEntrypoint("sh", "-c").WithCommand(script).WithWaitStrategy(Wait.ForUnixContainer()).Build();
        await container.StartAsync(Ct);
        var exit = await container.GetExitCodeAsync(Ct);
        var (stdout, stderr) = await container.GetLogsAsync(ct: Ct);
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains($"SHA {Convert.ToHexStringLower(SHA256.HashData(disk))}", stdout, StringComparison.Ordinal);
        Assert.Contains("vm01.vmx", stdout, StringComparison.Ordinal);
        Assert.Contains("disk0.vmdk", stdout, StringComparison.Ordinal);
        Assert.Equal(0, fs.OverlayBytes - 3 - "# Disk DescriptorFile\nversion=1\ncreateType=\"vmfs\"\n".Length); // nothing of the disk was copied into the overlay
    }
}
