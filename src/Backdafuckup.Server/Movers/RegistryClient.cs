using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backdafuckup.Server.Movers;

/// <summary>Creates the HTTP handler for registries (system CA validation); tests replace it.</summary>
public sealed class RegistryTransport(Func<HttpMessageHandler> create)
{
    public RegistryTransport()
        : this(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30) })
    {
    }

    public HttpMessageHandler Create() => create();
}

/// <summary>
/// Pushes an image to a container registry over the distribution API (OCI distribution spec): blobs the registry lacks,
/// then the manifest. Authentication as the registry asks for it: none, Basic, or a Bearer token from its token service
/// (Docker token authentication, as Nexus, Harbor and the CNCF distribution registry use it).
/// </summary>
public sealed partial class RegistryClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _registry;
    private readonly string? _userName;
    private readonly string? _password;
    private AuthenticationHeaderValue? _authorization;

    /// <param name="userAgent">Sent with every request, the token service's included (proxies in front of registries may check it).</param>
    public RegistryClient(HttpMessageHandler handler, string registry, string? userName, string? password, string userAgent)
    {
        _registry = registry;
        _userName = userName;
        _password = password;
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri($"https://{registry}/"), Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>Pushes the blobs and the manifest under <paramref name="tag"/>; returns the manifest digest. <paramref name="log"/> gets a line per step.</summary>
    public async Task<string> PushAsync(string repository, IReadOnlyList<RegistryBlob> blobs, byte[] manifest, string manifestMediaType, string tag, Action<string>? log, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(repository, cancellationToken).ConfigureAwait(false);
        foreach (var blob in blobs)
        {
            using (var head = await SendAsync(() => new HttpRequestMessage(HttpMethod.Head, $"v2/{repository}/blobs/{blob.Digest}"), repository, cancellationToken).ConfigureAwait(false))
            {
                if (head.StatusCode == HttpStatusCode.OK)
                {
                    log?.Invoke($"{blob.Digest[..19]}… ({blob.Size} bytes) is already in {repository}.");
                    continue;
                }

                if (head.StatusCode != HttpStatusCode.NotFound)
                {
                    throw await ErrorAsync(head, $"checking the blob {blob.Digest} in {repository}", cancellationToken).ConfigureAwait(false);
                }
            }

            Uri location;
            using (var start = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"v2/{repository}/blobs/uploads/"), repository, cancellationToken).ConfigureAwait(false))
            {
                if (start.StatusCode != HttpStatusCode.Accepted || start.Headers.Location is null)
                {
                    throw await ErrorAsync(start, $"starting the upload of {blob.Digest} to {repository}", cancellationToken).ConfigureAwait(false);
                }

                location = start.Headers.Location.IsAbsoluteUri ? start.Headers.Location : new Uri(_http.BaseAddress!, start.Headers.Location);
            }

            var target = new UriBuilder(location);
            target.Query = (target.Query.Length > 1 ? target.Query[1..] + "&" : string.Empty) + "digest=" + Uri.EscapeDataString(blob.Digest);
            using (var upload = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Put, target.Uri) { Content = Content(blob.Content, "application/octet-stream") },
                repository,
                cancellationToken).ConfigureAwait(false))
            {
                if (upload.StatusCode != HttpStatusCode.Created)
                {
                    throw await ErrorAsync(upload, $"uploading {blob.Digest} ({blob.Size} bytes) to {repository}", cancellationToken).ConfigureAwait(false);
                }
            }

            log?.Invoke($"{blob.Digest[..19]}… ({blob.Size} bytes) uploaded.");
        }

        var digest = RegistryBlob.DigestOf(manifest);
        using var put = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"v2/{repository}/manifests/{tag}") { Content = Content(manifest, manifestMediaType) },
            repository,
            cancellationToken).ConfigureAwait(false);
        if (put.StatusCode != HttpStatusCode.Created)
        {
            throw await ErrorAsync(put, $"storing the manifest {repository}:{tag}", cancellationToken).ConfigureAwait(false);
        }

        if (put.Headers.TryGetValues("Docker-Content-Digest", out var stored) && stored.FirstOrDefault() is { Length: > 0 } registryDigest && registryDigest != digest)
        {
            throw new RegistryException($"The registry {_registry} stored the manifest {repository}:{tag} as {registryDigest}, expected {digest}.");
        }

        log?.Invoke($"{repository}:{tag} ({digest[..19]}…) stored.");
        return digest;
    }

    public void Dispose() => _http.Dispose();

    private static ByteArrayContent Content(byte[] content, string mediaType)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return body;
    }

    /// <summary>Sends with the current authorization; a 401 (an expired token) authenticates again once.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, string repository, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(create(), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        await AuthenticateAsync(repository, cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(create(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.Authorization = _authorization;
            try
            {
                return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                throw new RegistryException($"The certificate of the registry {_registry} is not trusted by the backup server (its system CA store): {ex.Message}", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new RegistryException($"The registry {_registry} cannot be reached from the backup server: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Asks <c>/v2/</c> how to authenticate and does so for pushing to <paramref name="repository"/>.</summary>
    private async Task AuthenticateAsync(string repository, CancellationToken cancellationToken)
    {
        _authorization = null;
        using var probe = await SendOnceAsync(new HttpRequestMessage(HttpMethod.Get, "v2/"), cancellationToken).ConfigureAwait(false);
        if (probe.IsSuccessStatusCode)
        {
            return;
        }

        if (probe.StatusCode != HttpStatusCode.Unauthorized)
        {
            throw await ErrorAsync(probe, "checking its API (/v2/)", cancellationToken).ConfigureAwait(false);
        }

        if (_userName is not { Length: > 0 } || _password is null)
        {
            throw new RegistryException($"The registry {_registry} needs credentials for pushing to {repository}; none are set.");
        }

        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_userName}:{_password}")));
        var challenge = probe.Headers.WwwAuthenticate.FirstOrDefault();
        if (challenge is null || challenge.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            _authorization = basic;
            return;
        }

        if (!challenge.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new RegistryException($"The registry {_registry} asks for {challenge.Scheme} authentication; Basic and Bearer are supported.");
        }

        var parameters = ChallengeParameter().Matches(challenge.Parameter ?? string.Empty)
            .ToDictionary(m => m.Groups["key"].Value.ToLowerInvariant(), m => m.Groups["value"].Value, StringComparer.Ordinal);
        if (!parameters.TryGetValue("realm", out var realm) || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUrl))
        {
            throw new RegistryException($"The registry {_registry} asks for a Bearer token without naming its token service (realm).");
        }

        var query = new StringBuilder();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Append("service=").Append(Uri.EscapeDataString(service)).Append('&');
        }

        query.Append("scope=").Append(Uri.EscapeDataString($"repository:{repository}:pull,push"));
        var tokenUrl = new UriBuilder(realmUrl) { Query = query.ToString() }.Uri;
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
        tokenRequest.Headers.Authorization = basic;
        HttpResponseMessage tokenResponse;
        try
        {
            tokenResponse = await _http.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RegistryException($"The token service {tokenUrl.GetLeftPart(UriPartial.Path)} of the registry {_registry} cannot be reached: {ex.Message}", ex);
        }

        using (tokenResponse)
        {
            if (!tokenResponse.IsSuccessStatusCode)
            {
                throw await ErrorAsync(tokenResponse, $"getting a token for {repository} from {tokenUrl.GetLeftPart(UriPartial.Path)} (user {_userName})", cancellationToken).ConfigureAwait(false);
            }

            using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var token = (json.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null)
                ?? (json.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null)
                ?? throw new RegistryException($"The token service of the registry {_registry} answered without a token.");
            _authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private async Task<RegistryException> ErrorAsync(HttpResponseMessage response, string step, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                detail = string.Join("; ", errors.EnumerateArray().Select(e =>
                    $"{(e.TryGetProperty("code", out var c) ? c.GetString() : null)}: {(e.TryGetProperty("message", out var m) ? m.GetString() : null)}"));
            }
        }
        catch (JsonException)
        {
            // Not the registry's error document.
        }

        detail = detail.Length > 300 ? detail[..300] + "…" : detail;
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " The user name or password is wrong.",
            HttpStatusCode.Forbidden => $" The user {_userName} may not push to this repository.",
            HttpStatusCode.NotFound => " Is the repository path right, and is this the registry's API address (for Nexus: the Docker connector port or subdomain of the hosted repository)?",
            _ => string.Empty,
        };
        return new RegistryException($"The registry {_registry} refused {step}: {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? $" ({detail})" : string.Empty)}.{hint}");
    }

    [GeneratedRegex(@"(?<key>[A-Za-z]+)=""(?<value>[^""]*)""")]
    private static partial Regex ChallengeParameter();
}

public sealed class RegistryException : Exception
{
    public RegistryException(string message)
        : base(message)
    {
    }

    public RegistryException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
