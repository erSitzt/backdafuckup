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

/// <summary>Builds the API client of a cluster: bearer token, and the API servers trusted through the cluster CA (or a pinned certificate).</summary>
public static class KubernetesClients
{
    /// <param name="observer">Collects the certificates the client accepted and refused; null when nobody asks.</param>
    /// <param name="transport">Tests only: answers every request instead of the network.</param>
    internal static IKubernetes Create(ConnectionSettings settings, TlsObserver? observer, DelegatingHandler? transport)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pins = new HashSet<string>(settings.Pins.Select(p => p.SpkiSha256), StringComparer.OrdinalIgnoreCase);
        var authorities = CertificateAuthorities(settings);
        var config = new KubernetesClientConfiguration
        {
            Host = BaseUrl(settings),
            AccessToken = settings.Credential.Password,
            UserAgent = "backdafuckup",
            HttpClientTimeout = TimeSpan.FromSeconds(60),

            // The cluster's CA: every API server behind a load balancer has its own certificate, and RKE2 renews them; the
            // client checks the chain against this CA only, also for the WebSockets of exec.
            SslCaCerts = authorities.Count > 0 ? authorities : null,
            FirstMessageHandlerSetup = authorities.Count > 0 ? null : handler =>
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
        // Retries outside the transport: what the API server asks to retry, also from the fake API of tests.
        return transport is null ? new k8s.Kubernetes(config, new RetryHandler()) : new k8s.Kubernetes(config, new RetryHandler(), transport);
    }

    /// <summary>The CA certificates among the pins: a cluster is trusted through the CA that signs its API servers.</summary>
    public static X509Certificate2Collection CertificateAuthorities(ConnectionSettings settings)
    {
        var result = new X509Certificate2Collection();
        foreach (var pin in settings.Pins.Where(p => !string.IsNullOrWhiteSpace(p.Pem)))
        {
            var certificate = X509Certificate2.CreateFromPem(pin.Pem);
            if (IsAuthority(certificate))
            {
                result.Add(certificate);
            }
            else
            {
                certificate.Dispose();
            }
        }

        return result;
    }

    public static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority);

    /// <summary>The API's base URL: https, the address and port, and the optional path prefix (Rancher's proxy).</summary>
    public static string BaseUrl(ConnectionSettings settings)
    {
        var path = settings.Options.TryGetValue(KubernetesConnectionOptions.BasePath, out var p) && !string.IsNullOrWhiteSpace(p) ? "/" + p.Trim().Trim('/') : string.Empty;
        var host = settings.Address.Contains(':', StringComparison.Ordinal) && !settings.Address.StartsWith('[') ? $"[{settings.Address}]" : settings.Address;
        return $"https://{host}:{settings.Port}{path}";
    }
}

/// <summary>
/// Repeats a request the API server asks to repeat: 429 (API priority and fairness, or "storage is (re)initializing"
/// while a resource's cache is rebuilt) and 503, after its Retry-After (1 to 5 s), up to five times. The server has not
/// processed such a request, so repeating a create is safe too.
/// </summary>
internal sealed class RetryHandler(int attempts = 5) : DelegatingHandler
{
    internal static TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(5);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (attempt >= attempts || response.StatusCode is not (System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable))
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
            response.Dispose();
            await Task.Delay(wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > MaxWait ? MaxWait : wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
