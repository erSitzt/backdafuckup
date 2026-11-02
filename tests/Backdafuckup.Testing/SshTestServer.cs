using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Backdafuckup.Testing;

/// <summary>
/// A Debian guest with OpenSSH and sudo for the restore-over-SSH tests. Accounts: <c>root</c> (password login),
/// <c>alice</c> (sudo with password), <c>bob</c> (sudo without password), <c>tess</c> (sudo with requiretty) and
/// <c>nosudo</c>; each account's password is its name + "pw".
/// </summary>
public sealed class SshTestServer : IAsyncDisposable
{
    private const string Dockerfile = """
        FROM debian:trixie-slim
        RUN apt-get update && apt-get install -y --no-install-recommends openssh-server sudo && rm -rf /var/lib/apt/lists/* && mkdir -p /run/sshd \
         && echo 'root:rootpw' | chpasswd \
         && useradd -m -s /bin/sh -u 1001 alice && echo 'alice:alicepw' | chpasswd && echo 'alice ALL=(ALL) ALL' > /etc/sudoers.d/alice \
         && useradd -m -s /bin/sh -u 1002 bob && echo 'bob:bobpw' | chpasswd && echo 'bob ALL=(ALL) NOPASSWD: ALL' > /etc/sudoers.d/bob \
         && useradd -m -s /bin/sh -u 1003 tess && echo 'tess:tesspw' | chpasswd && printf 'Defaults:tess requiretty\ntess ALL=(ALL) ALL\n' > /etc/sudoers.d/tess \
         && useradd -m -s /bin/sh -u 1004 nosudo && echo 'nosudo:nosudopw' | chpasswd \
         && chmod 440 /etc/sudoers.d/* \
         && sed -i 's/^#\?PermitRootLogin.*/PermitRootLogin yes/; s/^#\?PasswordAuthentication.*/PasswordAuthentication yes/' /etc/ssh/sshd_config
        EXPOSE 22
        CMD ["/usr/sbin/sshd", "-D", "-e"]
        """;

    private readonly IContainer _container;

    private SshTestServer(IContainer container)
    {
        _container = container;
    }

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(22);

    public static async Task<SshTestServer> StartAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bdfu-ssh-image");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile"), Dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal), cancellationToken);
        IFutureDockerImage image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(directory)
            .WithName("backdafuckup/ssh-test:1")
            .WithCleanUp(false)
            .Build();
        await image.CreateAsync(cancellationToken);
        var container = new ContainerBuilder(image)
            .WithPortBinding(22, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22))
            .Build();
        await container.StartAsync(cancellationToken);
        return new SshTestServer(container);
    }

    /// <summary>Runs a command in the guest as root; throws if it fails.</summary>
    public async Task<string> ExecAsync(CancellationToken cancellationToken, params string[] command)
    {
        var result = await _container.ExecAsync(command, cancellationToken);
        return result.ExitCode == 0
            ? result.Stdout
            : throw new InvalidOperationException($"{string.Join(' ', command)} failed ({result.ExitCode}): {result.Stderr}");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
