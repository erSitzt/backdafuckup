using System.Net;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Images;

namespace Backdafuckup.Nfs.Tests;

/// <summary>
/// The server against an independent client: the libnfs utilities in Docker mount the export through our portmapper and
/// MOUNT, copy files in and out with READ and WRITE, and list the directory with READDIRPLUS.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LibnfsInteropTests
{
    private const ulong ExportId = 0x0000_00ff_0000_0001;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Libnfs_mounts_writes_lists_and_reads_back()
    {
        var fileSystem = new MemoryFileSystem(ExportId);
        var exports = new NfsExportTable();
        exports.Add(new NfsExport("/bdfu/interop", ExportId, fileSystem, AllowedClients: null));
        var portmapper = new PortmapperProgram();
        var calls = new System.Collections.Concurrent.ConcurrentBag<string>();
        var dispatcher = new RpcDispatcher([portmapper, new MountProgram(exports), new Nfs3Program(exports, new NfsServerLimits(), c => calls.Add(c.Procedure))]);
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

        var big = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 123);
        var url = $"nfs://host.testcontainers.internal/bdfu/interop";
        var query = $"?version=3&nfsport={port}&mountport={port}";
        var script = string.Join(" && ",
            "printf 'hello from libnfs' > /tmp/small.txt",
            $"nfs-cp /tmp/small.txt '{url}/small.txt{query}'",
            $"nfs-cp /data/big.bin '{url}/big.bin{query}'",
            $"echo LS; nfs-ls '{url}{query}'",
            $"echo CAT; nfs-cat '{url}/small.txt{query}'; echo",
            $"nfs-cp '{url}/big.bin{query}' /tmp/back.bin",
            "echo SHA $(sha256sum /tmp/back.bin | cut -c1-64)",
            "echo DONE");
        await using var container = new ContainerBuilder(image)
            .WithResourceMapping(big, "/data/big.bin")
            .WithEntrypoint("sh", "-c")
            .WithCommand(script)
            .WithWaitStrategy(Wait.ForUnixContainer())
            .Build();
        await container.StartAsync(Ct);
        var exit = await container.GetExitCodeAsync(Ct);
        var (stdout, stderr) = await container.GetLogsAsync(ct: Ct);
        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");

        Assert.Contains("DONE", stdout, StringComparison.Ordinal);
        Assert.Contains("small.txt", stdout, StringComparison.Ordinal);
        Assert.Contains("big.bin", stdout, StringComparison.Ordinal);
        Assert.Contains("hello from libnfs", stdout, StringComparison.Ordinal);
        Assert.Contains($"SHA {Convert.ToHexStringLower(SHA256.HashData(big))}", stdout, StringComparison.Ordinal);

        // What libnfs asked for, and what ended up in the file system.
        var (handle, _) = await fileSystem.LookupAsync(new NfsCaller(null, new IPEndPoint(IPAddress.Loopback, 0)), fileSystem.RootHandle, "big.bin", Ct);
        var read = await fileSystem.ReadAsync(new NfsCaller(null, new IPEndPoint(IPAddress.Loopback, 0)), handle, 0, big.Length + 1, Ct);
        Assert.Equal(big, read.Data.ToArray());
        Assert.Contains("WRITE", calls);
        Assert.Contains("READ", calls);
        Assert.Contains("CREATE", calls);
    }
}
