using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// A container registry in memory with the push half of the distribution API and Docker token authentication (like
/// Nexus): <c>/v2/</c> answers 401 with a Bearer challenge, <c>/token</c> hands out tokens for Basic credentials.
/// </summary>
internal sealed class FakeRegistry(string userName, string password, bool basicOnly = false) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, byte[]> _uploads = new();
    private int _tokens;

    public ConcurrentDictionary<string, byte[]> Blobs { get; } = new();

    public ConcurrentDictionary<string, (string MediaType, byte[] Content)> Manifests { get; } = new();

    public List<string> Requests { get; } = [];

    public List<string> Scopes { get; } = [];

    /// <summary>The User-Agent of each request.</summary>
    public List<string> UserAgents { get; } = [];

    /// <summary>Like an HAProxy in front of the registry that lets only Docker clients through: other User-Agents get 403 and an HTML page.</summary>
    public string? RequiredUserAgentPrefix { get; set; }

    /// <summary>Tokens issued before this number are expired (to test a new token mid-push).</summary>
    public int ValidFromToken { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var userAgent = request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(' ', agents) : string.Empty;
        lock (Requests)
        {
            Requests.Add($"{request.Method} {path}");
            UserAgents.Add(userAgent);
        }

        if (RequiredUserAgentPrefix is { } prefix && !userAgent.StartsWith(prefix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><body><h1>403 Forbidden</h1>Request forbidden by administrative rules.</body></html>", Encoding.UTF8, "text/html"),
            };
        }

        if (path == "/token")
        {
            if (!BasicOk(request))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            lock (Scopes)
            {
                Scopes.Add($"{query["service"]} {query["scope"]}");
            }

            return Json($$"""{"token":"token-{{Interlocked.Increment(ref _tokens)}}"}""");
        }

        if (!Authorized(request))
        {
            var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            challenge.Headers.WwwAuthenticate.ParseAdd(basicOnly ? "Basic realm=\"Registry\"" : "Bearer realm=\"https://registry.test:8443/token\",service=\"registry.test:8443\"");
            return challenge;
        }

        if (path == "/v2/")
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var parts = path["/v2/".Length..].Split('/');
        if (request.Method == HttpMethod.Head && parts[^2] == "blobs")
        {
            return new HttpResponseMessage(Blobs.ContainsKey(parts[^1]) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/blobs/uploads/", StringComparison.Ordinal))
        {
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = [];
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
            accepted.Headers.Location = new Uri($"{path}{id}?_state=abc", UriKind.Relative);
            return accepted;
        }

        if (request.Method == HttpMethod.Put && parts.Length > 2 && parts[^2] == "uploads" && _uploads.TryRemove(parts[^1], out _))
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
            if (query["_state"] != "abc" || query["digest"] != digest)
            {
                return Error(HttpStatusCode.BadRequest, "DIGEST_INVALID", $"digest {query["digest"]} does not match {digest}");
            }

            Blobs[digest] = content;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Put && parts[^2] == "manifests")
        {
            var content = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
            Manifests[$"{string.Join('/', parts[..^2])}:{parts[^1]}"] = (request.Content.Headers.ContentType!.MediaType!, content);
            var created = new HttpResponseMessage(HttpStatusCode.Created);
            created.Headers.Add("Docker-Content-Digest", digest);
            return created;
        }

        return Error(HttpStatusCode.NotFound, "NAME_UNKNOWN", $"no route for {request.Method} {path}");
    }

    /// <summary>An image archive as the .NET SDK writes it: manifest.json, the config, and gzip-compressed layers.</summary>
    public static byte[] ImageArchive(string tag, params string[] layerFiles)
    {
        using var archive = new MemoryStream();
        using (var tar = new TarWriter(archive, TarEntryFormat.Pax, leaveOpen: true))
        {
            var layers = new List<string>();
            foreach (var file in layerFiles)
            {
                using var layerTar = new MemoryStream();
                using (var inner = new TarWriter(layerTar, leaveOpen: true))
                {
                    inner.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"app/{file}") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(file)) });
                }

                using var gzip = new MemoryStream();
                using (var compress = new GZipStream(gzip, CompressionLevel.Fastest, leaveOpen: true))
                {
                    compress.Write(layerTar.ToArray());
                }

                var name = $"{Convert.ToHexStringLower(SHA256.HashData(gzip.ToArray()))}/layer.tar";
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(gzip.ToArray()) });
                layers.Add(name);
            }

            var config = Encoding.UTF8.GetBytes($$"""{"architecture":"amd64","os":"linux","config":{"Entrypoint":["/app/backdafuckup-mover"],"User":"root"},"tag":"{{tag}}"}""");
            var configName = $"{Convert.ToHexStringLower(SHA256.HashData(config))}.json";
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, configName) { DataStream = new MemoryStream(config) });
            var manifest = $$"""[{"Config":"{{configName}}","RepoTags":["backdafuckup/mover:{{tag}}"],"Layers":[{{string.Join(',', layers.Select(l => $"\"{l}\""))}}]}]""";
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "manifest.json") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest)) });
        }

        return archive.ToArray();
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        new(status) { Content = new StringContent($$"""{"errors":[{"code":"{{code}}","message":"{{message}}"}]}""", Encoding.UTF8, "application/json") };

    private bool BasicOk(HttpRequestMessage request) =>
        request.Headers.Authorization is { Scheme: "Basic", Parameter: { } p } && Encoding.UTF8.GetString(Convert.FromBase64String(p)) == $"{userName}:{password}";

    private bool Authorized(HttpRequestMessage request) => basicOnly
        ? BasicOk(request)
        : request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } token } && token.StartsWith("token-", StringComparison.Ordinal)
            && int.Parse(token["token-".Length..], System.Globalization.CultureInfo.InvariantCulture) >= ValidFromToken;
}
