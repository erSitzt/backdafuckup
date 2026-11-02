using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Backdafuckup.Nbd.Tests;

/// <summary>
/// Interoperability tests against nbdkit (the reference NBD server, BSD licensed) running in Docker.
/// The pattern plugin serves data where every 8-byte aligned word contains its own offset (big-endian).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NbdkitInteropTests : IAsyncLifetime
{
    private const long ExportSize = 256L * 1024 * 1024;

    private IFutureDockerImage? _image;
    private IContainer? _plain;
    private IContainer? _tls;
    private X509Certificate2? _certificate;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(Path.Combine(AppContext.BaseDirectory, "nbdkit"))
            .WithName("backdafuckup/nbdkit-test:1")
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync(Ct);

        _plain = new ContainerBuilder(_image)
            .WithPortBinding(10809, true)
            .WithCommand("pattern", $"size={ExportSize}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10809))
            .Build();

        _certificate = CreateCertificate();
        _tls = new ContainerBuilder(_image)
            .WithPortBinding(10809, true)
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certificate.ExportCertificatePem()), "/certs/server-cert.pem")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certificate.ExportCertificatePem()), "/certs/ca-cert.pem")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem()), "/certs/server-key.pem")
            .WithCommand("--tls=require", "--tls-certificates=/certs", "pattern", $"size={ExportSize}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(10809))
            .Build();

        await Task.WhenAll(_plain.StartAsync(Ct), _tls.StartAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (_plain is not null)
        {
            await _plain.DisposeAsync();
        }

        if (_tls is not null)
        {
            await _tls.DisposeAsync();
        }

        _certificate?.Dispose();
    }

    [Fact]
    public async Task Reads_pattern_data_without_tls()
    {
        await using var client = await NbdClient.ConnectAsync(Options(_plain!, NbdTlsMode.Disabled), Ct);

        Assert.Equal(ExportSize, client.Size);
        Assert.True(client.NegotiatedWithGo);
        await AssertPatternAsync(client, 0, 4096);
        await AssertPatternAsync(client, 12_345_677, 1024 * 1024 + 3);
        await AssertPatternAsync(client, ExportSize - 100, 100);
    }

    [Fact]
    public async Task Reads_pattern_data_over_tls_with_pinned_certificate()
    {
        await using var client = await NbdClient.ConnectAsync(Options(_tls!, NbdTlsMode.Required), Ct);

        Assert.True(client.UsesTls);
        var reads = Enumerable.Range(0, 32).Select(i => AssertPatternAsync(client, i * 7_000_003L, 256 * 1024));
        await Task.WhenAll(reads);
    }

    [Fact]
    public async Task Tls_server_rejects_plain_clients()
    {
        var ex = await Assert.ThrowsAsync<NbdOptionException>(async () =>
            await NbdClient.ConnectAsync(Options(_tls!, NbdTlsMode.Disabled), Ct));
        Assert.Equal(NbdProtocol.RepErrTlsReqd, ex.ReplyType);
    }

    [Fact]
    public async Task Pool_reads_whole_export_region_in_parallel()
    {
        await using var pool = await NbdConnectionPool.CreateAsync(ct => NbdClient.ConnectAsync(Options(_tls!, NbdTlsMode.Required), ct), 4, 4, Ct);
        var tasks = Enumerable.Range(0, 64).Select(async i =>
        {
            var offset = i * 1024L * 1024;
            var buffer = new byte[1024 * 1024];
            await pool.ReadAsync(offset, buffer, Ct);
            AssertPattern(offset, buffer);
        });
        await Task.WhenAll(tasks);
    }

    private NbdConnectOptions Options(IContainer container, NbdTlsMode tls) => new()
    {
        Host = container.Hostname,
        Port = container.GetMappedPublicPort(10809),
        ExportName = string.Empty,
        TlsMode = tls,
        TlsTargetHost = "localhost",
        ServerCertificateValidation = (_, cert, _, _) => cert is not null && cert.GetPublicKey().AsSpan().SequenceEqual(_certificate!.GetPublicKey()),
    };

    private static async Task AssertPatternAsync(NbdClient client, long offset, int length)
    {
        var buffer = new byte[length];
        await client.ReadAsync(offset, buffer, Ct);
        AssertPattern(offset, buffer);
    }

    private static void AssertPattern(long offset, byte[] buffer)
    {
        Span<byte> word = stackalloc byte[8];
        for (var i = 0; i < buffer.Length; i++)
        {
            var position = offset + i;
            BinaryPrimitives.WriteUInt64BigEndian(word, (ulong)(position & ~7L));
            if (buffer[i] != word[(int)(position & 7)])
            {
                Assert.Fail($"Pattern mismatch at offset {position}.");
            }
        }
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }
}
