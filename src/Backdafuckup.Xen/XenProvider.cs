using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Xen.Tls;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

/// <summary>XCP-ng / XenServer integration via XAPI (JSON-RPC), XAPI HTTP handlers and NBD.</summary>
public sealed class XenProvider : IHypervisorProvider
{
    public const int DefaultPort = 443;

    private readonly Func<ConnectionSettings, string, XapiTransport>? _transportFactory;

    public XenProvider(XenOptions options)
        : this(options, null)
    {
    }

    /// <summary>Allows replacing the XAPI transport (tests use an in-memory XAPI).</summary>
    public XenProvider(XenOptions options, Func<ConnectionSettings, string, XapiTransport>? transportFactory)
    {
        Options = options;
        _transportFactory = transportFactory;
    }

    public HypervisorKind Kind => HypervisorKind.XenServer;

    public XenOptions Options { get; }

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
        var rejected = new ConcurrentDictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        XapiSession session;
        try
        {
            session = await XapiSession.LoginAsync(CreateLoginOptions(settings, rejected), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException)
        {
            if (!rejected.IsEmpty)
            {
                var probes = rejected.Select(kv => CertificatePinning.ToProbe(kv.Key, kv.Value, false)).ToList();
                return new ConnectResult(false, "The TLS certificate of the host is not trusted yet.", null, probes, []);
            }

            return new ConnectResult(false, $"Cannot reach {settings.Address}: {ex.Message}", null, [], []);
        }
        catch (XapiException ex)
        {
            var message = ex.Code == "SESSION_AUTHENTICATION_FAILED" ? "Authentication failed (wrong user name or password)." : ex.Message;
            return new ConnectResult(false, message, null, [], []);
        }

        await using (session.ConfigureAwait(false))
        {
            var platform = await XenInventory.GetPlatformAsync(session, cancellationToken).ConfigureAwait(false);
            var pins = await GetHostPinsAsync(session, cancellationToken).ConfigureAwait(false);
            return new ConnectResult(true, null, platform, [], pins);
        }
    }

    public async Task<IHypervisorSession> OpenAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var session = await XapiSession.LoginAsync(CreateLoginOptions(settings, null), cancellationToken).ConfigureAwait(false);
        try
        {
            var platform = await XenInventory.GetPlatformAsync(session, cancellationToken).ConfigureAwait(false);
            return new XenHypervisorSession(this, settings, session, platform);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal XapiLoginOptions CreateLoginOptions(ConnectionSettings settings, ConcurrentDictionary<string, X509Certificate2>? rejected) => new()
    {
        Address = settings.Address,
        Username = settings.Credential.UserName,
        Password = settings.Credential.Password,
        FallbackAddresses = settings.Pins.Select(p => p.HostAddress).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        TransportFactory = host => CreateTransport(settings, host, rejected),
        RetryAttempts = Options.XapiRetryAttempts,
        RetryDelay = Options.XapiRetryDelay,
    };

    internal XapiTransport CreateTransport(ConnectionSettings settings, string host, ConcurrentDictionary<string, X509Certificate2>? rejected)
    {
        if (_transportFactory is not null)
        {
            return _transportFactory(settings, host);
        }

        var validator = CertificatePinning.CreateValidator(settings.TrustMode, settings.Pins.Select(p => p.SpkiSha256));
        RemoteCertificateValidationCallback callback = (sender, certificate, chain, errors) =>
        {
            var ok = validator(sender, certificate, chain, errors);
            if (!ok && certificate is not null && rejected is not null)
            {
                rejected[host] = new X509Certificate2(certificate);
            }

            return ok;
        };
        return new XapiTransport(host, settings.Port, callback);
    }

    /// <summary>Pins of all pool hosts, obtained over the authenticated master connection.</summary>
    private static async Task<List<PinnedCertificate>> GetHostPinsAsync(XapiSession session, CancellationToken cancellationToken)
    {
        var pins = new List<PinnedCertificate>();
        foreach (var (hostRef, host) in await session.GetAllRecordsAsync("host", cancellationToken).ConfigureAwait(false))
        {
            var address = host.Str("address");
            string pem;
            try
            {
                pem = await session.CallStringAsync("host.get_server_certificate", cancellationToken, hostRef).ConfigureAwait(false);
            }
            catch (XapiException)
            {
                continue;
            }

            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                pins.Add(CertificatePinning.ToPin(address, certificate));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Unparsable certificate: the host is pinned on first direct contact instead.
            }
        }

        return pins;
    }
}

/// <summary>An open connection to one pool.</summary>
internal sealed partial class XenHypervisorSession(XenProvider provider, ConnectionSettings settings, XapiSession session, PlatformInfo platform) : IHypervisorSession
{
    public PlatformInfo Platform { get; } = platform;

    internal XenProvider Provider { get; } = provider;

    internal ConnectionSettings Settings { get; } = settings;

    internal XapiSession Xapi { get; } = session;

    internal bool AllowInsecureNbd =>
        Settings.Options.TryGetValue(XenConnectionOptions.AllowInsecureNbd, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken) => XenInventory.GetAsync(Xapi, Platform, cancellationToken);

    /// <summary>Opens a dedicated XAPI session (each VM task gets its own; NBD export names are bound to it).</summary>
    internal Task<XapiSession> OpenDedicatedSessionAsync(CancellationToken cancellationToken) =>
        XapiSession.LoginAsync(Provider.CreateLoginOptions(Settings, null) with { Address = Xapi.MasterAddress }, cancellationToken);

    internal XapiTransport CreateTransport(string host) => Provider.CreateTransport(Settings, host, null);

    public ValueTask DisposeAsync() => Xapi.DisposeAsync();

    internal static JsonObject Map(params (string Key, string Value)[] entries)
    {
        var map = new JsonObject();
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }
}
