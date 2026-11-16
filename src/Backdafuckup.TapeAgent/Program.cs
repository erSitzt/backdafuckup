using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.Tape.Agent;
using Backdafuckup.Tape.Devices;

namespace Backdafuckup.TapeAgent;

/// <summary>
/// The tape agent of backdafuckup. Its settings come from the environment (the systemd unit reads them from
/// /etc/backdafuckup-tape-agent/agent.env, which the console shows once when the agent is added):
/// BDFU_SERVER_URL, BDFU_TAPE_AGENT_ID, BDFU_TAPE_AGENT_TOKEN and BDFU_SERVER_PINS (SHA-256 of the server's public key;
/// the agent trusts exactly that key). <c>backdafuckup-tape-agent devices</c> lists what it would report.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var host = new LocalTapeHost($"the tape agent on {Environment.MachineName}");
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

        if (args is ["devices", ..])
        {
            var devices = await host.DiscoverAsync(stop.Token).ConfigureAwait(false);
            foreach (var d in devices)
            {
                Console.WriteLine($"{d.Path}  {(d.PeripheralType == 8 ? "changer" : "drive  ")}  {d.Vendor} {d.Model}  serial {d.Serial ?? "(open in another program)"}  {d.Connection}");
            }

            Console.WriteLine(devices.Count == 0 ? "No tape drives or changers found (does this user belong to the group tape?)." : $"{devices.Count} device(s).");
            return 0;
        }

        Console.WriteLine($"backdafuckup tape agent {version}");
        Options options;
        try
        {
            options = Options.FromEnvironment();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var control = new Uri(options.Server, TapeAgentProtocol.ControlPath(options.AgentId));
        if (control.Scheme == Uri.UriSchemeHttps)
        {
            control = new UriBuilder(control) { Scheme = "wss" }.Uri;
        }
        else if (control.Scheme == Uri.UriSchemeHttp)
        {
            control = new UriBuilder(control) { Scheme = "ws" }.Uri;
        }

        var client = new TapeAgentClient(
            host,
            async ct =>
            {
                var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(60);
                if (options.Pins.Count > 0)
                {
                    socket.Options.RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, options.Pins);
                }

                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await socket.ConnectAsync(control, timeout.Token).ConfigureAwait(false);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            $"{options.Server.Authority} ({control.AbsolutePath})",
            version,
            message => Console.WriteLine(message));
        await client.RunAsync(stop.Token).ConfigureAwait(false);
        Console.WriteLine("Stopped.");
        return 0;
    }

    private sealed record Options(Uri Server, Guid AgentId, string Token, IReadOnlyList<string> Pins)
    {
        public static Options FromEnvironment()
        {
            static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value.Trim()
                : throw new ArgumentException($"{name} is not set. Copy the agent.env the console showed when the agent was added to /etc/backdafuckup-tape-agent/agent.env.");

            var url = Required("BDFU_SERVER_URL");
            if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http"))
            {
                throw new ArgumentException($"BDFU_SERVER_URL is not an https:// address: {url}");
            }

            var id = Required("BDFU_TAPE_AGENT_ID");
            if (!Guid.TryParse(id, out var agentId))
            {
                throw new ArgumentException($"BDFU_TAPE_AGENT_ID is not an agent id: {id}");
            }

            var pins = (Environment.GetEnvironmentVariable("BDFU_SERVER_PINS") ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (server.Scheme == "https" && pins.Length == 0)
            {
                throw new ArgumentException("BDFU_SERVER_PINS is empty: the agent trusts only the server's key, so it needs its SHA-256 (the console's agent.env has it).");
            }

            return new Options(server, agentId, Required("BDFU_TAPE_AGENT_TOKEN"), pins);
        }
    }
}
