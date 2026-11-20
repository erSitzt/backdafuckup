using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Core.Tls;
using Backdafuckup.Proxy;
using Backdafuckup.Proxy.Windows;

namespace Backdafuckup.ProxyAgent;

/// <summary>
/// The backup proxy agent of backdafuckup, for Linux and Windows.
/// <list type="bullet">
/// <item><c>backdafuckup-proxy enroll --server URL --proxy ID --token TOKEN --pins SPKI</c>: exchanges the console's
/// one-time install token for the proxy's own key and writes agent.json (Linux: /etc/backdafuckup-proxy, mode 600;
/// Windows: %ProgramData%\backdafuckup-proxy, SYSTEM and Administrators only).</item>
/// <item><c>backdafuckup-proxy run [--console]</c> (the systemd or Windows service): connects to the server and carries
/// out its commands.</item>
/// <item><c>backdafuckup-proxy install --server URL --proxy ID --token TOKEN --pins SPKI</c> (Windows, as administrator):
/// copies itself to %ProgramFiles%\backdafuckup Proxy, enrolls, and installs and starts the service.</item>
/// <item><c>backdafuckup-proxy uninstall</c> (Windows): stops and removes the service, its key, log and program.</item>
/// <item><c>backdafuckup-proxy devices</c>: lists the disks it would report.</item>
/// <item><c>backdafuckup-proxy prepare-san [--dry-run]</c>: makes the settings SAN access needs (as root or
/// administrator), see <see cref="SanPreparation"/> and <see cref="WindowsSanPreparation"/>.</item>
/// </list>
/// The agent trusts exactly the server's key (its SHA-256 pin).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var configPath = Option(args, "--config") ?? Environment.GetEnvironmentVariable("BDFU_PROXY_CONFIG") ?? AgentPaths.Config;

        // Started by the Windows service manager: the service runs the same loop and stops it.
        if (OperatingSystem.IsWindows() && args.FirstOrDefault() is "run" or null && !Environment.UserInteractive && !args.Contains("--console"))
        {
            WindowsAgent.RunService(token => RunAsync(configPath, version, code =>
            {
                if (OperatingSystem.IsWindows())
                {
                    WindowsAgent.StopForUpdate(code);
                }
            }, token));
            return 0;
        }

        using var stop = new CancellationTokenSource();
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        try
        {
            switch (args.FirstOrDefault())
            {
                case "devices":
                    foreach (var device in await Devices().ListAsync(stop.Token).ConfigureAwait(false))
                    {
                        Console.WriteLine($"{device.Path}  {device.SizeBytes} bytes  serial {device.Serial ?? "-"}  {device.Model}");
                    }

                    return 0;
                case "enroll":
                    return await EnrollAsync(EnrollArguments(args), configPath, stop.Token).ConfigureAwait(false);
                case "prepare-san":
                    return OperatingSystem.IsWindows()
                        ? await new WindowsSanPreparation(PowerShell.Through(), Console.WriteLine).RunAsync(args.Contains("--dry-run"), stop.Token).ConfigureAwait(false)
                        : await new SanPreparation(Console.WriteLine).RunAsync(args.Contains("--dry-run"), stop.Token).ConfigureAwait(false);
                case "install" when OperatingSystem.IsWindows():
                    var install = EnrollArguments(args);
                    return await WindowsAgent.InstallAsync(token => EnrollAsync(install, AgentPaths.Config, token), stop.Token).ConfigureAwait(false);
                case "uninstall" when OperatingSystem.IsWindows():
                    return await WindowsAgent.UninstallAsync(stop.Token).ConfigureAwait(false);
                case "run" or null:
                    return await RunAsync(configPath, version, code => Environment.Exit(code), stop.Token).ConfigureAwait(false);
                default:
                    Console.Error.WriteLine("Usage: backdafuckup-proxy enroll --server URL --proxy ID --token TOKEN --pins SPKI | run [--console] | devices | prepare-san [--dry-run] " +
                                            "| install --server URL --proxy ID --token TOKEN --pins SPKI (Windows) | uninstall (Windows) [--config FILE]");
                    return 2;
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    /// <summary>The arguments of enroll and install.</summary>
    private sealed record Enrollment(Uri Server, Guid ProxyId, string Token, string[] Pins);

    private static Enrollment EnrollArguments(string[] args)
    {
        var server = ServerUri(Option(args, "--server") ?? throw new ArgumentException("--server is missing (the backup server's address, e.g. https://backup01.corp.example:9555/)."));
        var proxyId = Guid.TryParse(Option(args, "--proxy"), out var id) ? id : throw new ArgumentException("--proxy is missing or no proxy id.");
        var token = Option(args, "--token") ?? throw new ArgumentException("--token is missing (the install token the console showed).");
        var pins = (Option(args, "--pins") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (server.Scheme == "https" && pins.Length == 0)
        {
            throw new ArgumentException("--pins is missing: the agent trusts only the server's key, so it needs its SHA-256 (the console's command has it).");
        }

        return new Enrollment(server, proxyId, token, pins);
    }

    private static async Task<int> EnrollAsync(Enrollment enrollment, string configPath, CancellationToken cancellationToken)
    {
        var (server, proxyId, token, pins) = enrollment;

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
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // On Windows the folder admits only SYSTEM and Administrators (the install protects it).
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var file = new FileStream(configPath, options))
        {
            await JsonSerializer.SerializeAsync(file, config, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(OperatingSystem.IsWindows()
            ? $"Enrolled as proxy {proxyId} of {server}; settings in {configPath}."
            : $"Enrolled as proxy {proxyId} of {server}; settings in {configPath}. Start the service: systemctl enable --now backdafuckup-proxy");
        return 0;
    }

    /// <summary>The agent's loop; <paramref name="restart"/> ends the process for the service manager to start the updated program.</summary>
    private static async Task<int> RunAsync(string configPath, string version, Action<int> restart, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            throw new ArgumentException($"{configPath} does not exist: enroll the proxy first (backdafuckup-proxy enroll …, the command the console shows).");
        }

        var config = JsonSerializer.Deserialize<AgentConfig>(await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false))
            ?? throw new ArgumentException($"{configPath} is empty.");
        var server = ServerUri(config.ServerUrl);
        AgentLog.Write($"backdafuckup proxy {version} ({ProxyPlatforms.Current}), server {server}, proxy {config.ProxyId}");
        var program = Environment.ProcessPath;
        var hash = ProxySelfUpdate.RunningHash();
        if (hash is not null && program is not null)
        {
            ProxySelfUpdate.RemoveReplaced(program);
        }

        var prepare = config.PrepareSan is not false && Environment.GetEnvironmentVariable("BDFU_PROXY_PREPARE_SAN") != "0" && program is not null;
        Func<ProxyAgentMessage, Task>? update = hash is null || program is null ? null : offer => UpdateAsync(server, config, program, offer, restart);
        ProxyAgentClient client;
        if (OperatingSystem.IsWindows())
        {
            // A physical Windows server with SAN access: no HotAdd; the server's existing iSCSI sessions; disks by WWN, read-only.
            var powershell = PowerShell.Through();
            var (uuid, serial) = await WindowsHostFacts.ReadAsync(powershell, cancellationToken).ConfigureAwait(false);
            client = new ProxyAgentClient(
                new ProxyAgentSettings(server, config.ProxyId, config.AgentKey),
                new ProxyHostInfo(Environment.MachineName, uuid, serial, Addresses(), version, hash, ProxyPlatforms.Windows, HotAdd: false),
                new WindowsBlockDevices(new Win32Disks(), powershell),
                (uri, bearer, ct) => ConnectAsync(uri, bearer, config.ServerPins, ct),
                AgentLog.Write,
                new WindowsIscsiInitiator(powershell),
                ct => WindowsSanCheck.CheckAsync(powershell, ct),
                update,
                prepare ? ct => new WindowsSanPreparation(powershell, line => AgentLog.Write($"prepare-san: {line}")).RunAsync(dryRun: false, ct) : null);
        }
        else
        {
            client = new ProxyAgentClient(
                new ProxyAgentSettings(server, config.ProxyId, config.AgentKey),
                new ProxyHostInfo(Environment.MachineName, ReadDmi("product_uuid"), ReadDmi("product_serial"), Addresses(), version, hash, ProxyPlatforms.Linux),
                new LinuxBlockDevices(),
                (uri, bearer, ct) => ConnectAsync(uri, bearer, config.ServerPins, ct),
                AgentLog.Write,
                new IscsiadmInitiator(),
                ct => SanSelfCheck.CheckAsync(null, ct),
                update,
                prepare ? ct => PrepareSanAsync(program!, ct) : null);
        }

        await client.RunAsync(cancellationToken).ConfigureAwait(false);
        AgentLog.Write("Stopped.");
        return 0;
    }

    /// <summary>The disks of this platform.</summary>
    private static IBlockDevices Devices() =>
        OperatingSystem.IsWindows() ? new WindowsBlockDevices(new Win32Disks(), PowerShell.Through()) : new LinuxBlockDevices();

    /// <summary>
    /// Downloads the server's program file (with the agent key, the server's certificate pinned), swaps it in and ends
    /// the process: systemd (exit 0, Restart=always) or the Windows service recovery (exit code 3) starts the new one.
    /// </summary>
    private static async Task UpdateAsync(Uri server, AgentConfig config, string program, ProxyAgentMessage offer, Action<int> restart)
    {
        try
        {
            var handler = new SocketsHttpHandler();
            if (config.ServerPins.Count > 0)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, config.ServerPins);
            }

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentKey);
            await ProxySelfUpdate.InstallAsync(http, new Uri(server, offer.Path!), program, offer.SizeBytes!.Value, offer.Sha256!, CancellationToken.None).ConfigureAwait(false);
            AgentLog.Write($"Agent updated to version {offer.AgentVersion} ({program}); restarting.");
            restart(OperatingSystem.IsWindows() ? WindowsAgent.RestartExitCode : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            AgentLog.Write($"Agent update to version {offer.AgentVersion} from {server} failed: {ex.Message}. The agent keeps running; the update is offered again at the next connection.");
        }
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

    /// <summary>agent.json (<see cref="AgentPaths.Config"/>).</summary>
    /// <param name="PrepareSan">False: the agent leaves the machine's settings alone (an admin runs prepare-san, or sets them).</param>
    private sealed record AgentConfig(string ServerUrl, Guid ProxyId, string AgentKey, IReadOnlyList<string> ServerPins, bool? PrepareSan = null);

    /// <summary>
    /// Runs prepare-san as a transient systemd unit: the service's own sandbox (ProtectSystem=strict) keeps /etc/lvm
    /// read-only, a transient unit started by systemd does not. Its output goes into the agent's log. Without
    /// systemd-run, prepare-san runs in the agent and leaves what it cannot write to the admin.
    /// </summary>
    private static async Task PrepareSanAsync(string program, CancellationToken cancellationToken)
    {
        var (code, output) = await Commands.RunAsync(
            "systemd-run", ["--wait", "--pipe", "--collect", "--quiet", "--unit", "backdafuckup-proxy-prepare-san", program, "prepare-san"], cancellationToken).ConfigureAwait(false);
        if (code == Commands.NotFound)
        {
            await new SanPreparation(line => AgentLog.Write($"prepare-san: {line.Trim()}")).RunAsync(dryRun: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AgentLog.Write($"prepare-san: {line}");
        }
    }
}
