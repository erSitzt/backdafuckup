using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// How much data a cluster's pods hold outside their PVCs, and how much its PVCs hold: what a backup names as not backed up
/// (spec 2026-11-12-1952-kubernetes-file-level-volumes). Keys are (namespace, pod, volume or container) and (namespace, PVC).
/// </summary>
/// <param name="Source">Where the numbers came from, for messages ("Prometheus https://…").</param>
/// <param name="Problems">Queries that failed, with the reason; the numbers of the others are still good.</param>
public sealed record KubernetesUsage(
    string Source,
    IReadOnlyDictionary<(string Namespace, string Pod, string Volume), long> PodVolumes,
    IReadOnlyDictionary<(string Namespace, string Pod, string Container), long> ContainerLayers,
    IReadOnlyDictionary<(string Namespace, string Claim), long> Claims,
    IReadOnlyDictionary<(string Namespace, string Pod, string Container), double> ContainerWrites,
    IReadOnlyList<string> Problems);

/// <summary>Reads a cluster's usage numbers.</summary>
public interface IKubernetesUsageSource
{
    /// <summary>For messages: "Prometheus https://prometheus.example:9090".</summary>
    string Name { get; }

    /// <summary>The numbers of one namespace, or of all (null).</summary>
    Task<KubernetesUsage> ReadAsync(string? ns, CancellationToken cancellationToken);
}

/// <summary>The usage source configured for a cluster connection, if any (the server keeps the configuration and its secret).</summary>
public interface IKubernetesUsageSources
{
    Task<IKubernetesUsageSource?> ForAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>
/// A Prometheus (or a compatible API: Thanos, Mimir, VictoriaMetrics) that has the cluster's storage metrics. The default
/// queries read the ephemeral-storage-exporter (emptyDirs, writable layers), the kubelet's <c>kubelet_volume_stats</c> (PVCs)
/// and cAdvisor's <c>container_fs_writes_bytes_total</c> (write rates); each can be replaced.
/// </summary>
/// <param name="Url">The API's base URL (the part before <c>/api/v1/query</c>).</param>
/// <param name="Username">With a secret: basic authentication; without: the secret is a bearer token.</param>
/// <param name="CaPem">Trust certificates issued by this CA (PEM) instead of the system's; null: the system's.</param>
/// <param name="PinnedSpkiSha256">Trust exactly this certificate (its public key's SHA-256, base64); null: by CA.</param>
/// <param name="Filter">Label matchers every default query gets, for a Prometheus of several clusters: <c>cluster="prod"</c>.</param>
public sealed record KubernetesUsageConfig(
    string Url,
    string? Username = null,
    string? CaPem = null,
    string? PinnedSpkiSha256 = null,
    string? Filter = null,
    string? PodVolumesQuery = null,
    string? ContainerLayersQuery = null,
    string? ClaimsQuery = null,
    string? ContainerWritesQuery = null)
{
    /// <summary>emptyDirs (and other volumes the kubelet measures) per pod: bytes.</summary>
    public const string DefaultPodVolumesQuery =
        "max by (pod_namespace, pod_name, volume_name) (ephemeral_storage_container_volume_usage{pod_namespace=~\"$namespace\"$filter})";

    /// <summary>Each container's writable layer: bytes.</summary>
    public const string DefaultContainerLayersQuery =
        "max by (pod_namespace, pod_name, container) (ephemeral_storage_container_rootfs_used_bytes{pod_namespace=~\"$namespace\"$filter})";

    /// <summary>Each PVC's data: bytes.</summary>
    public const string DefaultClaimsQuery =
        "max by (namespace, persistentvolumeclaim) (kubelet_volume_stats_used_bytes{namespace=~\"$namespace\"$filter})";

    /// <summary>How fast each container writes to local disks over the last hour: bytes per second.</summary>
    public const string DefaultContainerWritesQuery =
        "sum by (namespace, pod, container) (rate(container_fs_writes_bytes_total{namespace=~\"$namespace\",container!=\"\",container!=\"POD\"$filter}[1h]))";

    /// <summary>A query with <c>$namespace</c> (a regular expression: the namespace, or every one) and <c>$filter</c> filled in.</summary>
    public string Expand(string query, string? ns) =>
        query.Replace("$namespace", ns is null ? ".+" : System.Text.RegularExpressions.Regex.Escape(ns).Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("$filter", string.IsNullOrWhiteSpace(Filter) ? string.Empty : "," + Filter.Trim().Trim(','), StringComparison.Ordinal);

    /// <summary>Checks what a user entered; the message names what is wrong.</summary>
    public static string? Problem(KubernetesUsageConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(url.Query))
        {
            return $"\"{config.Url}\" is not an http(s) URL without a query (the address of the Prometheus API, e.g. https://prometheus.example:9090).";
        }

        if (config.CaPem is { Length: > 0 } pem)
        {
            try
            {
                using var ca = X509Certificate2.CreateFromPem(pem);
            }
            catch (CryptographicException ex)
            {
                return $"The CA certificate is not a PEM certificate: {ex.Message}";
            }
        }

        if (config.Filter is { Length: > 0 } filter && (filter.Contains('{', StringComparison.Ordinal) || filter.Contains('}', StringComparison.Ordinal)))
        {
            return $"The label filter \"{filter}\" is a list of label matchers without braces, e.g. cluster=\"prod\".";
        }

        return null;
    }
}

/// <summary>What a test of a usage source found: series per query, problems, and a certificate to trust when TLS failed.</summary>
public sealed record KubernetesUsageTest(int PodVolumes, int ContainerLayers, int Claims, int ContainerWrites, IReadOnlyList<string> Problems, CertificateProbe? Untrusted);

/// <summary>Reads usage numbers from a Prometheus API.</summary>
public sealed class PrometheusUsageSource : IKubernetesUsageSource, IDisposable
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);
    private readonly KubernetesUsageConfig _config;
    private readonly HttpClient _http;
    private readonly Uri _query;
    private X509Certificate2? _rejected;

    /// <param name="transport">Tests only: answers the requests instead of the network.</param>
    public PrometheusUsageSource(KubernetesUsageConfig config, string? secret, HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        var baseUrl = config.Url.TrimEnd('/');
        _query = new Uri(baseUrl + "/api/v1/query");
        Name = $"Prometheus {baseUrl}";
        HttpMessageHandler handler = transport ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Validate },
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = QueryTimeout };
        if (!string.IsNullOrEmpty(secret))
        {
            _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(config.Username)
                ? new AuthenticationHeaderValue("Bearer", secret)
                : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{secret}")));
        }
    }

    public string Name { get; }

    public async Task<KubernetesUsage> ReadAsync(string? ns, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var volumes = new Dictionary<(string, string, string), long>();
        var layers = new Dictionary<(string, string, string), long>();
        var claims = new Dictionary<(string, string), long>();
        var writes = new Dictionary<(string, string, string), double>();
        foreach (var series in await QueryAsync("pod volumes", _config.PodVolumesQuery ?? KubernetesUsageConfig.DefaultPodVolumesQuery, ns, problems, cancellationToken).ConfigureAwait(false))
        {
            if (series.Label(NamespaceLabels) is { } n && series.Label(PodLabels) is { } p && series.Label(VolumeLabels) is { } v)
            {
                volumes[(n, p, v)] = (long)series.Value;
            }
        }

        foreach (var series in await QueryAsync("writable layers", _config.ContainerLayersQuery ?? KubernetesUsageConfig.DefaultContainerLayersQuery, ns, problems, cancellationToken).ConfigureAwait(false))
        {
            if (series.Label(NamespaceLabels) is { } n && series.Label(PodLabels) is { } p && series.Label(ContainerLabels) is { } c)
            {
                layers[(n, p, c)] = (long)series.Value;
            }
        }

        foreach (var series in await QueryAsync("PVCs", _config.ClaimsQuery ?? KubernetesUsageConfig.DefaultClaimsQuery, ns, problems, cancellationToken).ConfigureAwait(false))
        {
            if (series.Label(NamespaceLabels) is { } n && series.Label(ClaimLabels) is { } c)
            {
                claims[(n, c)] = (long)series.Value;
            }
        }

        foreach (var series in await QueryAsync("write rates", _config.ContainerWritesQuery ?? KubernetesUsageConfig.DefaultContainerWritesQuery, ns, problems, cancellationToken).ConfigureAwait(false))
        {
            if (series.Label(NamespaceLabels) is { } n && series.Label(PodLabels) is { } p && series.Label(ContainerLabels) is { } c)
            {
                writes[(n, p, c)] = series.Value;
            }
        }

        return new KubernetesUsage(Name, volumes, layers, claims, writes, problems);
    }

    /// <summary>Runs every query for the whole cluster: how many series each found, what failed, and an untrusted certificate.</summary>
    public async Task<KubernetesUsageTest> TestAsync(CancellationToken cancellationToken)
    {
        var usage = await ReadAsync(null, cancellationToken).ConfigureAwait(false);
        var untrusted = _rejected is { } rejected ? CertificatePinning.ToProbe(_query.Host, rejected, false) : null;
        return new KubernetesUsageTest(usage.PodVolumes.Count, usage.ContainerLayers.Count, usage.Claims.Count, usage.ContainerWrites.Count, usage.Problems, untrusted);
    }

    public void Dispose()
    {
        _http.Dispose();
        _rejected?.Dispose();
    }

    // The label each exporter uses, the pod's own first (a ServiceMonitor may set "namespace" to the exporter's).
    private static readonly string[] NamespaceLabels = ["pod_namespace", "namespace"];
    private static readonly string[] PodLabels = ["pod_name", "pod"];
    private static readonly string[] VolumeLabels = ["volume_name", "volume"];
    private static readonly string[] ContainerLabels = ["container", "container_name"];
    private static readonly string[] ClaimLabels = ["persistentvolumeclaim", "claim"];

    private bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        bool trusted;
        if (_config.PinnedSpkiSha256 is { Length: > 0 } pin)
        {
            trusted = string.Equals(CertificatePinning.SpkiSha256(certificate), pin, StringComparison.OrdinalIgnoreCase);
        }
        else if (_config.CaPem is { Length: > 0 } pem)
        {
            trusted = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && ChainsTo(certificate, pem);
        }
        else
        {
            trusted = errors == SslPolicyErrors.None;
        }

        if (!trusted)
        {
            Interlocked.Exchange(ref _rejected, new X509Certificate2(certificate))?.Dispose();
        }

        return trusted;
    }

    private static bool ChainsTo(X509Certificate certificate, string caPem)
    {
        using var ca = X509Certificate2.CreateFromPem(caPem);
        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private async Task<IReadOnlyList<Series>> QueryAsync(string what, string query, string? ns, List<string> problems, CancellationToken cancellationToken)
    {
        var expanded = _config.Expand(query, ns);
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("query", expanded)]);
        try
        {
            using var response = await _http.PostAsync(_query, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonElement root;
            try
            {
                root = JsonDocument.Parse(body).RootElement;
            }
            catch (JsonException)
            {
                problems.Add($"{Name} answered the query for {what} with {(int)response.StatusCode} {response.StatusCode} and no Prometheus result: {Short(body)}");
                return [];
            }

            if (!response.IsSuccessStatusCode || root.TryGetProperty("status", out var status) && status.GetString() != "success")
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : Short(body);
                problems.Add($"{Name} refused the query for {what} ({(int)response.StatusCode} {response.StatusCode}): {error}. Query: {expanded}");
                return [];
            }

            var data = root.GetProperty("data");
            if (data.GetProperty("resultType").GetString() != "vector")
            {
                problems.Add($"The query for {what} gives a {data.GetProperty("resultType").GetString()}, not an instant vector: {expanded}");
                return [];
            }

            var result = new List<Series>();
            foreach (var item in data.GetProperty("result").EnumerateArray())
            {
                var labels = item.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
                var value = item.GetProperty("value")[1].GetString();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number >= 0)
                {
                    result.Add(new Series(labels, number));
                }
            }

            return result;
        }
        catch (HttpRequestException ex) when (_rejected is not null)
        {
            problems.Add($"The TLS certificate of {Name} is not trusted ({_rejected.Subject}, issued by {_rejected.Issuer}): {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            problems.Add($"{Name} cannot be reached for the query for {what}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            problems.Add($"{Name} did not answer the query for {what} within {QueryTimeout.TotalSeconds:0} s.");
        }

        return [];
    }

    private static string Short(string text) => text.Length > 300 ? text[..300] + "…" : text;

    private sealed record Series(Dictionary<string, string> Labels, double Value)
    {
        public string? Label(string[] names) => names.Select(n => Labels.GetValueOrDefault(n)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }
}
