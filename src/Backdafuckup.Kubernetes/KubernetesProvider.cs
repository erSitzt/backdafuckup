using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// A Kubernetes cluster as a connection: its namespaces and volumes are the inventory. The server reaches the API with a
/// ServiceAccount token; the API server's certificate is pinned on first use, as for pools.
/// </summary>
/// <param name="transport">Tests only: answers the API requests of a connection instead of the network (null: the network).</param>
/// <param name="channels">The server's mover channels (volume data); null: objects only.</param>
/// <param name="images">The mover image in a registry the clusters can pull from.</param>
/// <param name="timings">Tests only: shorter waits.</param>
/// <param name="executor">Tests only: runs hooks instead of the cluster's exec.</param>
public sealed class KubernetesProvider(
    Func<ConnectionSettings, DelegatingHandler?>? transport = null, IMoverChannels? channels = null, IMoverImageSource? images = null, KubernetesTimings? timings = null,
    IPodExecutor? executor = null)
    : IHypervisorProvider
{
    /// <summary>Tests only: one transport for every connection.</summary>
    public KubernetesProvider(
        Func<DelegatingHandler?> transport, IMoverChannels? channels = null, IMoverImageSource? images = null, KubernetesTimings? timings = null, IPodExecutor? executor = null)
        : this(_ => transport(), channels, images, timings, executor)
    {
    }

    public HypervisorKind Kind => HypervisorKind.Kubernetes;

    public async Task<CertificateProbe> ProbeCertificateAsync(string address, int port, CancellationToken cancellationToken)
    {
        var (certificate, valid) = await CertificatePinning.ProbeAsync(address, port, cancellationToken).ConfigureAwait(false);
        using (certificate)
        {
            return CertificatePinning.ToProbe(address, certificate, valid);
        }
    }

    public async Task<ConnectResult> TestConnectionAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var tls = new TlsObserver();
        var url = KubernetesClients.BaseUrl(settings);
        using var client = KubernetesClients.Create(settings, tls, transport?.Invoke(settings));
        PlatformInfo platform;
        try
        {
            platform = await KubernetesInventory.GetPlatformAsync(client, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException)
        {
            if (KubernetesClients.CertificateAuthorities(settings) is { Count: > 0 } authorities && ex is HttpRequestException or AuthenticationException)
            {
                var ca = authorities[0];
                return new ConnectResult(false, $"The API server {url} did not present a certificate signed by the cluster CA given ({ca.Subject}), " +
                    $"or not for the address {settings.Address}: {ex.Message}", null, [], []);
            }

            if (!tls.Rejected.IsEmpty)
            {
                return new ConnectResult(false, $"The TLS certificate of the API server {url} is not trusted yet.", null,
                    tls.Rejected.Select(kv => CertificatePinning.ToProbe(kv.Key, kv.Value, false)).ToList(), []);
            }

            return new ConnectResult(false, $"Cannot reach the Kubernetes API {url}: {ex.Message}", null, [], []);
        }
        catch (HttpOperationException ex)
        {
            return new ConnectResult(false, Describe(ex, url), null, [], []);
        }

        // Pin the certificate the validated requests were answered with (with its PEM); without TLS (tests) keep what was given.
        var pins = tls.Accepted.Select(kv => CertificatePinning.ToPin(kv.Key, kv.Value)).ToList();
        foreach (var certificate in tls.Accepted.Values)
        {
            certificate.Dispose();
        }

        return new ConnectResult(true, null, platform, [], pins.Count > 0 ? pins : settings.Pins);
    }

    public Task<IHypervisorSession> OpenAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        IHypervisorSession session = new KubernetesSession(KubernetesClients.Create(settings, null, transport?.Invoke(settings)), settings, channels, images, timings, executor);
        return Task.FromResult(session);
    }

    /// <summary>An API error with the cluster and what the token may lack.</summary>
    internal static string Describe(HttpOperationException ex, string url) => ex.Response?.StatusCode switch
    {
        HttpStatusCode.Unauthorized => $"The Kubernetes API {url} refused the token (401 Unauthorized): it is wrong, expired, or its ServiceAccount was deleted.",
        HttpStatusCode.Forbidden => $"The Kubernetes API {url} refused a request (403 Forbidden): the ServiceAccount lacks a permission. {ex.Response.Content}",
        { } code => $"The Kubernetes API {url} answered {(int)code} {code}: {ex.Response?.Content}",
        null => $"The Kubernetes API {url} failed: {ex.Message}",
    };
}
