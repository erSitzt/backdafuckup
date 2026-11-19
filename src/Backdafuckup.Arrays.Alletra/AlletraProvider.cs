using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Tls;

namespace Backdafuckup.Arrays.Alletra;

/// <summary>HPE Alletra Storage MP (and other arrays with WSAPI v3) through its public REST API.</summary>
/// <param name="retryDelays">Pauses between tries while the array is busy (null: about 90 s in all; tests pass zeros).</param>
public sealed class AlletraProvider(
    Func<ArrayConnectionSettings, HttpMessageHandler>? handlerFactory = null, TimeSpan? taskTimeout = null, IReadOnlyList<TimeSpan>? retryDelays = null) : IStorageArrayProvider
{
    public const string ProviderKind = "AlletraMp";

    public string Kind => ProviderKind;

    public async Task<ArrayInfo> TestConnectionAsync(ArrayConnectionSettings settings, CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(settings, cancellationToken).ConfigureAwait(false);
        return session.Info;
    }

    public async Task<IStorageArraySession> OpenAsync(ArrayConnectionSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var client = new AlletraClient(Handler(settings), settings.Address, settings.Port, settings.UserName, settings.Password, taskTimeout, retryDelays);
        try
        {
            await client.LoginAsync(cancellationToken).ConfigureAwait(false);
            var info = await InfoAsync(client, cancellationToken).ConfigureAwait(false);
            return new AlletraSession(client, settings, info);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private HttpMessageHandler Handler(ArrayConnectionSettings settings)
    {
        if (handlerFactory is not null)
        {
            return handlerFactory(settings);
        }

        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(settings.TrustMode, settings.PinnedSpkiSha256),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            },
        };
    }

    /// <summary>The array's facts from <c>/server/info</c> and <c>/api/v3/systems</c> (name, model, serial, WWN, OS, licences).</summary>
    private static async Task<ArrayInfo> InfoAsync(AlletraClient client, CancellationToken cancellationToken)
    {
        var server = await client.GetAsync("server/info", cancellationToken).ConfigureAwait(false);
        var system = AlletraClient.Members(await client.GetAsync("api/v3/systems", cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        var licenses = new List<ArrayLicense>();
        foreach (var feature in system?["licenses"]?["consolidatedFeatures"] as JsonArray ?? [])
        {
            var name = feature is JsonValue plain && plain.TryGetValue<string>(out var text) ? text
                : AlletraClient.Text(feature?["name"]) ?? AlletraClient.Text(feature?["displayName"]) ?? AlletraClient.Text(feature?["feature"]);
            if (name is null)
            {
                continue;
            }

            var expires = AlletraClient.Text(feature?["expiryDate"]) ?? AlletraClient.Text(feature?["expiration"]) ?? AlletraClient.Text(feature?["expires"]);
            licenses.Add(new ArrayLicense(name, DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null));
        }

        var timeout = server?["timeout"] is JsonValue t && t.TryGetValue<int>(out var seconds) ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        return new ArrayInfo(
            AlletraClient.Text(server?["systemName"]) ?? AlletraClient.Text(system?["name"]) ?? client.Endpoint,
            AlletraClient.Text(server?["systemModel"]) ?? AlletraClient.Text(server?["hwmodel"]) ?? "HPE Alletra",
            AlletraClient.Text(server?["systemSerial"]) ?? string.Empty,
            AlletraClient.Text(server?["systemWwn"]) ?? AlletraClient.Text(system?["systemWWN"]) ?? string.Empty,
            AlletraClient.Text(system?["version"]?["display"]) ?? AlletraClient.Text(server?["osVersion"]?["display"]) ?? AlletraClient.Text(server?["osVersion"]) ?? string.Empty,
            "WSAPI v3",
            licenses,
            client.Privileges,
            timeout);
    }
}
