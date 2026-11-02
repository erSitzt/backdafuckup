using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Xen.Tls;

/// <summary>Public-key pinning (SHA-256 of the SubjectPublicKeyInfo) for XAPI and NBD TLS connections.</summary>
public static class CertificatePinning
{
    public static string SpkiSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public static string SpkiSha256(X509Certificate certificate) =>
        certificate is X509Certificate2 c2 ? SpkiSha256(c2) : SpkiSha256(new X509Certificate2(certificate));

    public static string SpkiSha256FromPem(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return SpkiSha256(certificate);
    }

    public static PinnedCertificate ToPin(string hostAddress, X509Certificate2 certificate) =>
        new(hostAddress, SpkiSha256(certificate), certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), certificate.ExportCertificatePem());

    public static CertificateProbe ToProbe(string hostAddress, X509Certificate2 certificate, bool validAgainstSystemCa) => new(
        hostAddress,
        certificate.Subject,
        certificate.Issuer,
        certificate.NotBefore.ToUniversalTime(),
        certificate.NotAfter.ToUniversalTime(),
        SpkiSha256(certificate),
        certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(),
        certificate.ExportCertificatePem(),
        validAgainstSystemCa);

    /// <summary>Validation callback that accepts exactly the given SPKI hashes (or the system CA chain in SystemCa mode).</summary>
    public static RemoteCertificateValidationCallback CreateValidator(TlsTrustMode mode, IEnumerable<string> spkiSha256)
    {
        var pins = new HashSet<string>(spkiSha256, StringComparer.OrdinalIgnoreCase);
        return (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            return mode == TlsTrustMode.SystemCa
                ? errors == SslPolicyErrors.None
                : pins.Contains(SpkiSha256(certificate));
        };
    }

    /// <summary>Connects with TLS without trusting anything and returns the presented certificate.</summary>
    public static async Task<(X509Certificate2 Certificate, bool ValidAgainstSystemCa)> ProbeAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        X509Certificate2? presented = null;
        var systemValid = false;
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, errors) =>
        {
            if (certificate is not null)
            {
                presented = new X509Certificate2(certificate);
                systemValid = errors == SslPolicyErrors.None;
            }

            return true;
        });
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck },
            timeout.Token).ConfigureAwait(false);
        return (presented ?? throw new IOException($"{host}:{port} presented no TLS certificate."), systemValid);
    }
}
