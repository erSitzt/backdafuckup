using System.Net;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Core.Tls;

namespace Backdafuckup.Server.Tests;

public sealed class ServerCertificateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bdfu-cert-tests", Guid.NewGuid().ToString("N"));

    private static (IReadOnlyList<string>, IReadOnlyList<IPAddress>) Names(params string[] dns) => (dns, [IPAddress.Parse("192.0.2.10")]);

    [Fact]
    public void A_certificate_of_another_host_or_close_to_expiry_is_issued_again_with_the_same_key()
    {
        var pfx = Path.Combine(_folder, "server-certificate.pfx");
        var now = DateTimeOffset.UtcNow;

        // First start: a new key, named after the host.
        using var first = ServerCertificate.LoadOrCreate(pfx, Names("dev-box"), now);
        var pin = CertificatePinning.SpkiSha256(first);
        Assert.Contains("dev-box", ServerCertificate.Names(first));
        Assert.Contains("localhost", ServerCertificate.Names(first));

        // The same host again: the stored certificate as it is.
        using (var again = ServerCertificate.LoadOrCreate(pfx, Names("dev-box"), now.AddDays(1)))
        {
            Assert.Equal(first.Thumbprint, again.Thumbprint);
        }

        // The data folder taken over by another host: issued again for its names, with the same key (the agents' pin).
        using var moved = ServerCertificate.LoadOrCreate(pfx, Names("BACKUP01", "backup01.example.org"), now.AddDays(2));
        Assert.NotEqual(first.Thumbprint, moved.Thumbprint);
        Assert.Equal(pin, CertificatePinning.SpkiSha256(moved));
        Assert.Equal(new HashSet<string>(["BACKUP01", "backup01.example.org", "localhost"], StringComparer.OrdinalIgnoreCase), ServerCertificate.Names(moved));
        Assert.Equal("CN=BACKUP01, O=backdafuckup", moved.Subject);
        Assert.True(moved.HasPrivateKey);

        // Close to its end: renewed, still with the same key.
        using var renewed = ServerCertificate.LoadOrCreate(pfx, Names("BACKUP01", "backup01.example.org"), now.AddYears(5).AddDays(-10));
        Assert.True(renewed.NotAfter > now.AddYears(9).UtcDateTime);
        Assert.Equal(pin, CertificatePinning.SpkiSha256(renewed));

        // Stored: the next start loads it.
        using var stored = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfx, password: null);
        Assert.Equal(renewed.Thumbprint, stored.Thumbprint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
