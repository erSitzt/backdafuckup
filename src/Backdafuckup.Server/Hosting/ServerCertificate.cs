using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Backdafuckup.Server.Hosting;

/// <summary>
/// Provides the HTTPS certificate for Kestrel. Uses Kestrel:Certificates:Default from configuration if present;
/// otherwise generates a self-signed ECDSA P-256 certificate (SAN = host name + local IPs) on first start and
/// stores it in the data directory.
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

    public static X509Certificate2 LoadOrCreate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
            if (existing.NotAfter > DateTime.UtcNow.AddDays(30))
            {
                return existing;
            }
        }

        var created = CreateSelfSigned();
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, created.Export(X509ContentType.Pkcs12));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(pfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), password: null);
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        var hostName = Dns.GetHostName();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}, O=backdafuckup", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        try
        {
            foreach (var address in Dns.GetHostAddresses(hostName))
            {
                if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                {
                    san.AddIpAddress(address);
                }
            }
        }
        catch (SocketException)
        {
            // Name resolution is optional for the SAN list.
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], // server authentication
            false));

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }
}
