using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Backdafuckup.Testing.Nfs;

/// <summary>
/// A real rpcbind (Debian's) in Docker whose /run is a directory of the test machine, so the test reaches rpcbind's Unix
/// socket the way the server does on Linux. rpcbind runs as root in the container; the files it leaves are removed
/// from inside the container on disposal.
/// </summary>
public sealed class RpcbindContainer : IAsyncDisposable
{
    private const string Image = "backdafuckup/rpcbind-test:2";
    private const string Dockerfile = """
        FROM debian:bookworm-slim
        RUN apt-get update && apt-get install -y --no-install-recommends rpcbind netbase && rm -rf /var/lib/apt/lists/*
        CMD ["rpcbind", "-f", "-w"]
        """;

    private readonly IContainer _container;
    private readonly string _directory;

    private RpcbindContainer(IContainer container, string directory)
    {
        _container = container;
        _directory = directory;
    }

    /// <summary>rpcbind's socket on the test machine.</summary>
    public string SocketPath => Path.Combine(_directory, "rpcbind.sock");

    public static async Task<RpcbindContainer> StartAsync(CancellationToken cancellationToken)
    {
        var build = Directory.CreateTempSubdirectory("bdfu-rpcbind-image-");
        await File.WriteAllTextAsync(Path.Combine(build.FullName, "Dockerfile"), Dockerfile, cancellationToken);
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(build.FullName)
            .WithName(Image)
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(cancellationToken);
        build.Delete(recursive: true);

        // A Unix socket's path must stay below 108 bytes.
        var directory = Path.Combine(Path.GetTempPath().Length < 40 ? Path.GetTempPath() : "/tmp", $"bdfu-rpcb-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(directory);
        var container = new ContainerBuilder(Image)
            .WithBindMount(directory, "/run")
            .WithCleanUp(true)
            .Build();
        await container.StartAsync(cancellationToken);
        var result = new RpcbindContainer(container, directory);
        for (var i = 0; i < 100 && !File.Exists(result.SocketPath); i++)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (!File.Exists(result.SocketPath))
        {
            var (stdout, stderr) = await container.GetLogsAsync(ct: cancellationToken);
            throw new InvalidOperationException($"rpcbind did not create {result.SocketPath}: {stdout} {stderr}");
        }

        return result;
    }

    /// <summary>The mappings as a remote client sees them: rpcinfo -p asks portmapper version 2 over TCP, as hypervisors do.</summary>
    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(["rpcinfo", "-p", "127.0.0.1"], cancellationToken);
        return result.Stdout + result.Stderr;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _container.ExecAsync(["sh", "-c", "rm -rf /run/* /run/.[!.]*"]);
        }
        catch (Exception)
        {
            // Best effort; the container goes anyway.
        }

        await _container.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
