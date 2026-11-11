using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using k8s;

namespace Backdafuckup.Kubernetes;

/// <summary>Connection options of a Kubernetes cluster, in <see cref="ConnectionSettings.Options"/>.</summary>
public static class KubernetesConnectionOptions
{
    /// <summary>
    /// Path in front of the API, e.g. <c>/k8s/clusters/c-m-abc12</c> for a cluster reached through Rancher's proxy;
    /// empty for the API server itself.
    /// </summary>
    public const string BasePath = "kubernetes.basePath";
}

/// <summary>The certificates a client accepted and refused, keyed by address: refused ones are offered for trust, accepted ones pinned.</summary>
internal sealed class TlsObserver
{
    public ConcurrentDictionary<string, X509Certificate2> Accepted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, X509Certificate2> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Builds the API client of a cluster: bearer token, and the API server's certificate pinned (trust on first use).</summary>
internal static class KubernetesClients
{
    /// <param name="observer">Collects the certificates the client accepted and refused; null when nobody asks.</param>
    /// <param name="transport">Tests only: answers every request instead of the network.</param>
    public static IKubernetes Create(ConnectionSettings settings, TlsObserver? observer, DelegatingHandler? transport)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pins = new HashSet<string>(settings.Pins.Select(p => p.SpkiSha256), StringComparer.OrdinalIgnoreCase);
        var config = new KubernetesClientConfiguration
        {
            Host = BaseUrl(settings),
            AccessToken = settings.Credential.Password,
            UserAgent = "backdafuckup",
            HttpClientTimeout = TimeSpan.FromSeconds(60),
            FirstMessageHandlerSetup = handler =>
            {
                if (handler is SocketsHttpHandler sockets)
                {
                    sockets.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate is null)
                        {
                            return false;
                        }

                        var trusted = settings.TrustMode == TlsTrustMode.SystemCa
                            ? errors == SslPolicyErrors.None
                            : pins.Contains(CertificatePinning.SpkiSha256(certificate));
                        (trusted ? observer?.Accepted : observer?.Rejected)?.TryAdd(settings.Address, new X509Certificate2(certificate));

                        return trusted;
                    };
                }
            },
        };
        return transport is null ? new k8s.Kubernetes(config) : new k8s.Kubernetes(config, transport);
    }

    /// <summary>The API's base URL: https, the address and port, and the optional path prefix (Rancher's proxy).</summary>
    public static string BaseUrl(ConnectionSettings settings)
    {
        var path = settings.Options.TryGetValue(KubernetesConnectionOptions.BasePath, out var p) && !string.IsNullOrWhiteSpace(p) ? "/" + p.Trim().Trim('/') : string.Empty;
        var host = settings.Address.Contains(':', StringComparison.Ordinal) && !settings.Address.StartsWith('[') ? $"[{settings.Address}]" : settings.Address;
        return $"https://{host}:{settings.Port}{path}";
    }
}
