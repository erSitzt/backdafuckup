using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.Proxy;

namespace Backdafuckup.ProxyAgent;

/// <summary>
/// The backup proxy agent of backdafuckup.
/// <list type="bullet">
/// <item><c>backdafuckup-proxy enroll --server URL --proxy ID --token TOKEN --pins SPKI</c>: exchanges the console's
/// one-time install token for the proxy's own key and writes /etc/backdafuckup-proxy/agent.json (mode 600).</item>
/// <item><c>backdafuckup-proxy run</c> (the systemd service): connects to the server and carries out its commands.</item>
/// <item><c>backdafuckup-proxy devices</c>: lists the block devices it would report.</item>
/// </list>
/// The agent trusts exactly the server's key (its SHA-256 pin).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class Program
{
    private const string DefaultConfig = "/etc/backdafuckup-proxy/agent.json";

    public static async Task<int> Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var configPath = Option(args, "--config") ?? Environment.GetEnvironmentVariable("BDFU_PROXY_CONFIG") ?? DefaultConfig;
        try
        {
            switch (args.FirstOrDefault())
            {
                case "devices":
                    foreach (var device in await new LinuxBlockDevices().ListAsync(stop.Token).ConfigureAwait(false))
                    {
                        Console.WriteLine($"{device.Path}  {device.SizeBytes} bytes  serial {device.Serial ?? "-"}  {device.Model}");
                    }

                    return 0;
                case "enroll":
                    return await EnrollAsync(args, configPath, stop.Token).ConfigureAwait(false);
                case "run" or null:
                    return await RunAsync(configPath, version, stop.Token).ConfigureAwait(false);
                default:
                    Console.Error.WriteLine("Usage: backdafuckup-proxy enroll --server URL --proxy ID --token TOKEN --pins SPKI | run | devices [--config FILE]");
                    return 2;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<int> EnrollAsync(string[] args, string configPath, CancellationToken cancellationToken)
    {
        var server = ServerUri(Option(args, "--server") ?? throw new ArgumentException("--server is missing (the backup server's address, e.g. https://backup01.corp.example:9555/)."));
        var proxyId = Guid.TryParse(Option(args, "--proxy"), out var id) ? id : throw new ArgumentException("--proxy is missing or no proxy id.");
        var token = Option(args, "--token") ?? throw new ArgumentException("--token is missing (the install token the console showed).");
        var pins = (Option(args, "--pins") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (server.Scheme == "https" && pins.Length == 0)
        {
            throw new ArgumentException("--pins is missing: the agent trusts only the server's key, so it needs its SHA-256 (the console's command has it).");
        }

        using var handler = new SocketsHttpHandler();
        if (pins.Length > 0)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, pins);
        }

        using var http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(60) };
        using var response = await http.PostAsJsonAsync(ProxyAgentProtocol.EnrollPath, new EnrollProxyRequestDto(proxyId, token, Environment.MachineName), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"{server} refused the enrollment: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
            return 1;
        }

        var enrolled = await response.Content.ReadFromJsonAsync<EnrollProxyResponseDto>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server answered the enrollment without a key.");
        var config = new AgentConfig(server.ToString(), proxyId, enrolled.AgentKey, pins);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        await using (var file = new FileStream(configPath, options))
        {
            await JsonSerializer.SerializeAsync(file, config, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Enrolled as proxy {proxyId} of {server}; settings in {configPath}. Start the service: systemctl enable --now backdafuckup-proxy");
        return 0;
    }

    private static async Task<int> RunAsync(string configPath, string version, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            throw new ArgumentException($"{configPath} does not exist: enroll the proxy first (backdafuckup-proxy enroll …, the command the console shows).");
        }

        var config = JsonSerializer.Deserialize<AgentConfig>(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false))
            ?? throw new ArgumentException($"{configPath} is empty.");
        var server = ServerUri(config.ServerUrl);
        Console.WriteLine($"backdafuckup proxy {version}, server {server}, proxy {config.ProxyId}");
        var client = new ProxyAgentClient(
            new ProxyAgentSettings(server, config.ProxyId, config.AgentKey),
            new ProxyHostInfo(Environment.MachineName, ReadDmi("product_uuid"), ReadDmi("product_serial"), Addresses(), version, null),
            new LinuxBlockDevices(),
            (uri, bearer, ct) => ConnectAsync(uri, bearer, config.ServerPins, ct),
            message => Console.WriteLine(message),
            new IscsiadmInitiator(),
            ct => SanSelfCheck.CheckAsync(null, ct));
        await client.RunAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static async Task<WebSocket> ConnectAsync(Uri uri, string bearer, IReadOnlyList<string> pins, CancellationToken cancellationToken)
    {
        var target = new UriBuilder(uri) { Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(60);
        if (pins.Count > 0)
        {
            socket.Options.RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, pins);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(target, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Uri ServerUri(string url) =>
        Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var server) && server.Scheme is "https" or "http"
            ? server
            : throw new ArgumentException($"The server address is not an https:// address: {url}");

    /// <summary>The machine's SMBIOS fields (root only), which name the proxy's VM in the hypervisor.</summary>
    private static string? ReadDmi(string name)
    {
        try
        {
            return File.ReadAllText(Path.Combine("/sys/class/dmi/id", name)).Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> Addresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork || (a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal))
            .Select(a => a.Address.ToString())];

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>/etc/backdafuckup-proxy/agent.json.</summary>
    private sealed record AgentConfig(string ServerUrl, Guid ProxyId, string AgentKey, IReadOnlyList<string> ServerPins);
}
