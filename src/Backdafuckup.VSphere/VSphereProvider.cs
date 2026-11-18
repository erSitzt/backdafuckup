using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.VSphere.Vim;

namespace Backdafuckup.VSphere;

/// <summary>Settings of the vSphere provider (configuration section "VSphere").</summary>
public sealed class VSphereOptions
{
    /// <summary>How long one vim25 request may take.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// VMware vSphere through vCenter's public vim25 SOAP API (our own client, no VMware SDK). Only vCenter is supported: a
/// standalone ESXi host is refused, because backups need the snapshot, CBT and HotAdd orchestration vCenter provides.
/// </summary>
public sealed class VSphereProvider : IHypervisorProvider
{
    public const int DefaultPort = 443;

    private readonly Func<ConnectionSettings, HttpMessageHandler>? _handlerFactory;

    public VSphereProvider(VSphereOptions options)
        : this(options, null)
    {
    }

    /// <summary>Allows replacing the HTTP handler (tests use the fake vCenter).</summary>
    public VSphereProvider(VSphereOptions options, Func<ConnectionSettings, HttpMessageHandler>? handlerFactory)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _handlerFactory = handlerFactory;
    }

    public HypervisorKind Kind => HypervisorKind.VSphere;

    public VSphereOptions Options { get; }

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
        VimSession session;
        try
        {
            session = await ConnectAsync(settings, rejected, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthenticationException or IOException or TimeoutException)
        {
            if (!rejected.IsEmpty)
            {
                var probes = rejected.Select(kv => CertificatePinning.ToProbe(kv.Key, kv.Value, false)).ToList();
                return new ConnectResult(false, "The TLS certificate of the vCenter is not trusted yet.", null, probes, []);
            }

            return new ConnectResult(false, $"Cannot reach vCenter {settings.Address}:{settings.Port}: {ex.Message}", null, [], []);
        }
        catch (VimFaultException ex)
        {
            return new ConnectResult(false, ex.Message, null, [], []);
        }
        catch (InvalidDataException ex)
        {
            return new ConnectResult(false, $"vCenter {settings.Address}:{settings.Port} answered something that is not the vim25 API: {ex.Message}", null, [], []);
        }

        await using (session.ConfigureAwait(false))
        {
            if (Refusal(session, settings) is { } refusal)
            {
                return new ConnectResult(false, refusal, null, [], []);
            }

            var pins = await VSphereInventory.GetHostPinsAsync(session, cancellationToken).ConfigureAwait(false);
            return new ConnectResult(true, null, Platform(session, settings), [], pins);
        }
    }

    public async Task<IHypervisorSession> OpenAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var session = await ConnectAsync(settings, null, cancellationToken).ConfigureAwait(false);
        if (Refusal(session, settings) is { } refusal)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new NotSupportedException(refusal);
        }

        return new VSphereHypervisorSession(this, settings, session, Platform(session, settings));
    }

    internal async Task<VimSession> ConnectAsync(ConnectionSettings settings, ConcurrentDictionary<string, X509Certificate2>? rejected, CancellationToken cancellationToken)
    {
        VimTransport transport;
        if (_handlerFactory is not null)
        {
            transport = new VimTransport(new Uri($"https://{settings.Address}:{settings.Port}/"), _handlerFactory(settings), Options.RequestTimeout);
        }
        else
        {
            var validator = CertificatePinning.CreateValidator(settings.TrustMode, settings.Pins.Select(p => p.SpkiSha256));
            RemoteCertificateValidationCallback callback = (sender, certificate, chain, errors) =>
            {
                var ok = validator(sender, certificate, chain, errors);
                if (!ok && certificate is not null && rejected is not null)
                {
                    rejected[settings.Address] = new X509Certificate2(certificate);
                }

                return ok;
            };
            transport = new VimTransport(settings.Address, settings.Port, callback, Options.RequestTimeout);
        }

        return await VimSession.ConnectAsync(transport, settings.Credential.UserName, settings.Credential.Password, cancellationToken).ConfigureAwait(false);
    }

    private static string? Refusal(VimSession session, ConnectionSettings settings) =>
        session.Content.About.ApiType == "VirtualCenter"
            ? null
            : $"{settings.Address} is a standalone ESXi host ({session.Content.About.FullName}), not a vCenter. Add the vCenter that manages it: " +
              "backups need vCenter for snapshots, change tracking and HotAdd.";

    private static PlatformInfo Platform(VimSession session, ConnectionSettings settings)
    {
        var about = session.Content.About;
        return new PlatformInfo(
            about.InstanceUuid ?? settings.Address,
            settings.Address,
            settings.Address,
            "VMware vCenter Server",
            string.IsNullOrEmpty(about.Build) ? about.Version ?? about.ApiVersion : $"{about.Version} build {about.Build}");
    }
}
