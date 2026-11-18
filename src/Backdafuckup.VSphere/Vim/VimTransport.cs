using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Xml.Linq;

namespace Backdafuckup.VSphere.Vim;

/// <summary>
/// vim25 SOAP over HTTPS to one vCenter (<c>POST https://vcenter/sdk</c>). Keeps the session cookie itself, so any
/// <see cref="HttpMessageHandler"/> works (the tests' fake vCenter too); TLS validation comes from the caller (pinning).
/// </summary>
public sealed class VimTransport : IDisposable
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string SessionCookie = "vmware_soap_session";
    private readonly HttpClient _http;
    private string? _cookie;

    public VimTransport(string host, int port, RemoteCertificateValidationCallback certificateValidation, TimeSpan? timeout = null)
        : this(new Uri($"https://{FormatHost(host)}:{port}/"), CreateHandler(certificateValidation), timeout)
    {
    }

    /// <summary>For tests: any base address and handler.</summary>
    public VimTransport(Uri baseAddress, HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        BaseAddress = baseAddress;
        Endpoint = baseAddress.IsDefaultPort ? baseAddress.Host : $"{baseAddress.Host}:{baseAddress.Port}";
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        RequestTimeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public Uri BaseAddress { get; }

    /// <summary>"vcenter.corp.example" or "host:port", for messages.</summary>
    public string Endpoint { get; }

    public TimeSpan RequestTimeout { get; }

    /// <summary>The vim25 version the requests name (SOAPAction <c>urn:vim25/&lt;version&gt;</c>); set by <see cref="NegotiateVersionAsync"/>.</summary>
    public string Version { get; private set; } = "7.0.3.0";

    /// <summary>Whether a session cookie is held (after a login).</summary>
    public bool HasSession => _cookie is not null;

    /// <summary>Reads the newest vim25 version the server speaks from <c>/sdk/vimServiceVersions.xml</c>.</summary>
    public async Task<string> NegotiateVersionAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.GetAsync("sdk/vimServiceVersions.xml", timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"vCenter {Endpoint}: /sdk/vimServiceVersions.xml answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}; is this a vCenter or ESXi host?", null, response.StatusCode);
        }

        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        var version = document.Descendants("namespace").Where(n => (string?)n.Element("name") == "urn:vim25").Select(n => (string?)n.Element("version")).FirstOrDefault()
            ?? throw new InvalidDataException($"vCenter {Endpoint}: /sdk/vimServiceVersions.xml names no urn:vim25 version.");
        Version = version.Trim();
        return Version;
    }

    /// <summary>Sends one request body (e.g. <c>&lt;RetrieveServiceContent&gt;</c>) and returns the response element.</summary>
    public async Task<XElement> InvokeAsync(XElement request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var method = request.Name.LocalName;
        var envelope = new XElement(
            Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", Soap.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xsi", VimXml.Xsi.NamespaceName),
            new XAttribute("xmlns", VimXml.Ns.NamespaceName),
            new XElement(Soap + "Body", request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, "sdk")
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
        };
        message.Headers.TryAddWithoutValidation("SOAPAction", $"urn:vim25/{Version}");
        if (_cookie is { } cookie)
        {
            message.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"vCenter {Endpoint}: {method} got no answer within {RequestTimeout.TotalSeconds:0} s.");
        }

        using (response)
        {
            KeepCookie(response.Headers);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            XElement body;
            try
            {
                body = XDocument.Parse(text).Root?.Element(Soap + "Body")
                    ?? throw new InvalidDataException("no SOAP body");
            }
            catch (System.Xml.XmlException ex)
            {
                throw new InvalidDataException(
                    $"vCenter {Endpoint}: {method} answered HTTP {(int)response.StatusCode} without a SOAP envelope ({ex.Message}).", ex);
            }

            if (body.Element(Soap + "Fault") is { } fault)
            {
                throw ParseFault(fault, method);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"vCenter {Endpoint}: {method} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}.", null, response.StatusCode);
            }

            return body.Elements().FirstOrDefault()
                ?? throw new InvalidDataException($"vCenter {Endpoint}: {method} answered an empty SOAP body.");
        }
    }

    /// <summary>Forgets the session cookie (after a logout, or before a new login).</summary>
    public void ForgetSession() => _cookie = null;

    public void Dispose() => _http.Dispose();

    private VimFaultException ParseFault(XElement fault, string method)
    {
        var message = (string?)fault.Element("faultstring") ?? "SOAP fault";
        var detail = fault.Element("detail")?.Elements().FirstOrDefault();
        var type = detail?.Attribute(VimXml.Xsi + "type")?.Value is { } typed
            ? typed[(typed.IndexOf(':', StringComparison.Ordinal) + 1)..]
            : detail?.Name.LocalName is { } name && name.EndsWith("Fault", StringComparison.Ordinal) ? name[..^"Fault".Length] : detail?.Name.LocalName ?? string.Empty;
        return new VimFaultException(type, message.Trim(), method, Endpoint, detail);
    }

    private void KeepCookie(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0].Trim();
            if (pair.StartsWith(SessionCookie + "=", StringComparison.Ordinal))
            {
                _cookie = pair;
            }
        }
    }

    private static SocketsHttpHandler CreateHandler(RemoteCertificateValidationCallback validation) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
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

    private static string FormatHost(string host) => host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
}
