using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Backdafuckup.Server.Hosting;

/// <summary>
/// Provides the HTTPS certificate for Kestrel. Uses Kestrel:Certificates:Default from configuration if present;
/// otherwise generates a self-signed ECDSA P-256 certificate (SAN = host name, fully qualified name, localhost, local IPs)
/// on first start and stores it in the data directory; it is issued again with the same key before it expires or when the
/// host's name changed, so the agents' pins stay valid.
/// </summary>
public static class ServerCertificate
{
    public const int DefaultHttpsPort = 9555;

    public static void ConfigureServerKestrel(this IWebHostBuilder webHost, IConfiguration configuration, ServerPaths paths)
    {
        // An explicit Kestrel endpoint configuration (Kestrel:Endpoints) always wins.
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            return;
        }

        var port = configuration.GetValue("Server:HttpsPort", DefaultHttpsPort);
        webHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listen => listen.UseHttps(LoadOrCreate(paths.CertificatePath)));
        });
    }

    /// <summary>
    /// The certificate in <paramref name="pfxPath"/>, or a new one. One that expires within 30 days, or that does not name
    /// this host (a data folder taken over from another machine), is issued again with the same key: the agents pin the
    /// SHA-256 of the public key, so they keep trusting the server. Only without a certificate is a new key created.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath) => LoadOrCreate(pfxPath, HostNames(), TimeProvider.System.GetUtcNow());

    internal static X509Certificate2 LoadOrCreate(string pfxPath, (IReadOnlyList<string> Dns, IReadOnlyList<IPAddress> Addresses) names, DateTimeOffset now)
    {
        if (File.Exists(pfxPath))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null, X509KeyStorageFlags.Exportable);
            if (existing.NotAfter > now.UtcDateTime.AddDays(30) && Names(existing) is var covered && names.Dns.All(covered.Contains))
            {
                return existing;
            }

            using (existing)
            using (var stored = existing.GetECDsaPrivateKey())
            {
                if (stored is not null)
                {
                    // A copy of the key in memory: exported and stored like a new one (a key loaded on Windows lives in CNG).
                    using var key = ECDsa.Create(stored.ExportParameters(includePrivateParameters: true));
                    return Save(pfxPath, CreateSelfSigned(key, names, now));
                }
            }
        }

        using var created = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Save(pfxPath, CreateSelfSigned(created, names, now));
    }

    /// <summary>The DNS names a certificate is issued for (its SAN).</summary>
    internal static HashSet<string> Names(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static X509Certificate2 Save(string pfxPath, X509Certificate2 certificate)
    {
        using (certificate)
        {
            var pfx = certificate.Export(X509ContentType.Pkcs12);
            Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
            File.WriteAllBytes(pfxPath, pfx);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return X509CertificateLoader.LoadPkcs12(pfx, password: null);
        }
    }

    /// <summary>This host's names for the certificate: the host name, its fully qualified name, localhost, and its addresses.</summary>
    private static (IReadOnlyList<string> Dns, IReadOnlyList<IPAddress> Addresses) HostNames()
    {
        var hostName = Dns.GetHostName();
        var dns = new List<string> { hostName };
        var addresses = new List<IPAddress>();
        try
        {
            var entry = Dns.GetHostEntry(hostName);
            if (!string.Equals(entry.HostName, hostName, StringComparison.OrdinalIgnoreCase))
            {
                dns.Add(entry.HostName);
            }

            addresses.AddRange(entry.AddressList.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6));
        }
        catch (SocketException)
        {
            // Name resolution is optional for the SAN list.
        }

        return (dns, addresses);
    }

    private static X509Certificate2 CreateSelfSigned(ECDsa key, (IReadOnlyList<string> Dns, IReadOnlyList<IPAddress> Addresses) names, DateTimeOffset now)
    {
        var request = new CertificateRequest($"CN={names.Dns[0]}, O=backdafuckup", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names.Dns.Append("localhost").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            san.AddDnsName(name);
        }

        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }.Concat(names.Addresses).Distinct())
        {
            san.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], // server authentication
            false));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }
}
