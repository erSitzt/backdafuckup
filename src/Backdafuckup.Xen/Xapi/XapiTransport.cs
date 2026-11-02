using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backdafuckup.Xen.Xapi;

/// <summary>An error returned by XAPI: an error code (e.g. HOST_IS_SLAVE) and its parameters.</summary>
public sealed class XapiException(string code, IReadOnlyList<string> parameters, string method)
    : Exception($"XAPI {method} failed: {code}" + (parameters.Count > 0 ? $" [{string.Join(", ", parameters)}]" : string.Empty))
{
    public string Code { get; } = code;

    public IReadOnlyList<string> Parameters { get; } = parameters;

    public string Method { get; } = method;
}

/// <summary>
/// XAPI JSON-RPC 2.0 transport (<c>POST https://host/jsonrpc</c>) plus the HTTP transfer handlers.
/// One instance talks to one host; TLS validation is supplied by the caller (certificate pinning).
/// </summary>
public sealed class XapiTransport : IDisposable
{
    private readonly HttpClient _http;
    private long _nextId;

    public XapiTransport(string host, int port, RemoteCertificateValidationCallback certificateValidation, TimeSpan? timeout = null)
        : this(new Uri($"https://{FormatHost(host)}:{port}/"), CreateHandler(certificateValidation), timeout)
    {
        Host = host;
    }

    /// <summary>For tests: arbitrary base address and handler.</summary>
    public XapiTransport(Uri baseAddress, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        BaseAddress = baseAddress;
        Host = baseAddress.Host;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        RequestTimeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public Uri BaseAddress { get; }

    public string Host { get; }

    public TimeSpan RequestTimeout { get; }

    public async Task<JsonNode?> CallAsync(string method, IReadOnlyList<JsonNode?> parameters, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = new JsonArray(parameters.Select(p => p?.DeepClone()).ToArray()),
            ["id"] = Interlocked.Increment(ref _nextId),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("jsonrpc", content, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"XAPI {method}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        var node = JsonNode.Parse(body) ?? throw new JsonException($"XAPI {method}: empty response.");
        if (node["error"] is JsonObject error)
        {
            throw ParseError(method, error);
        }

        // XML-RPC style responses wrapped in JSON (Status/Value) are tolerated as well.
        if (node["result"] is JsonObject legacy && legacy["Status"] is { } status)
        {
            if (status.GetValue<string>() == "Success")
            {
                return legacy["Value"];
            }

            var description = legacy["ErrorDescription"] as JsonArray ?? [];
            var code = description.Count > 0 ? description[0]?.ToString() ?? "UNKNOWN" : "UNKNOWN";
            throw new XapiException(code, description.Skip(1).Select(d => d?.ToString() ?? string.Empty).ToList(), method);
        }

        return node["result"];
    }

    /// <summary>Sends a request to an XAPI HTTP handler (export_metadata, import_raw_vdi ...) without following redirects.</summary>
    public async Task<HttpResponseMessage> SendHandlerAsync(HttpMethod method, string pathAndQuery, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, pathAndQuery) { Content = content };
        if (content is not null && content.Headers.ContentType is null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    private static XapiException ParseError(string method, JsonObject error)
    {
        // JSON-RPC 2.0 errors from XAPI: { code, message = <API error code>, data = [params] }.
        var code = error["message"]?.ToString() ?? "UNKNOWN";
        var parameters = error["data"] switch
        {
            JsonArray array => array.Select(d => d?.ToString() ?? string.Empty).ToList(),
            JsonValue value => [value.ToString()],
            _ => [],
        };

        // Some versions put the code into data[0] and a generic message into message.
        if (!LooksLikeErrorCode(code) && parameters.Count > 0 && LooksLikeErrorCode(parameters[0]))
        {
            code = parameters[0];
            parameters = parameters.Skip(1).ToList();
        }

        return new XapiException(code, parameters, method);
    }

    private static bool LooksLikeErrorCode(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    private static SocketsHttpHandler CreateHandler(RemoteCertificateValidationCallback validation) => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.None,
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = validation,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
        },
    };

    private static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;
}
