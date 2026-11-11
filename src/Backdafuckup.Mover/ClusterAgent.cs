using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;

namespace Backdafuckup.Mover;

/// <summary>
/// The settings of the cluster agent (<c>BDFU_MOVER_MODE=agent</c>), from the environment of its Deployment:
/// <list type="bullet">
/// <item><c>BDFU_AGENT_SERVER_URL</c>: the backup server, e.g. <c>https://backup01:9555/</c>.</item>
/// <item><c>BDFU_AGENT_CONNECTION</c>: the cluster's connection id on the server.</item>
/// <item><c>BDFU_AGENT_TOKEN</c>: the cluster's connect token (from a Secret).</item>
/// <item><c>BDFU_MOVER_SERVER_PINS</c>: SHA-256 SPKI pins of the server certificate; empty: the system's CA store.</item>
/// <item><c>KUBERNETES_SERVICE_HOST</c>, <c>KUBERNETES_SERVICE_PORT</c>: the API server (set by Kubernetes in every pod).</item>
/// <item><c>POD_NAME</c>, <c>NODE_NAME</c>: for the server's messages (downward API).</item>
/// </list>
/// The ServiceAccount's token and CA are read from <see cref="AccountDirectory"/>; the token again whenever the kubelet renews it.
/// </summary>
internal sealed record ClusterAgentOptions(
    Uri ServerUrl, Guid ConnectionId, string Token, IReadOnlyList<string> ServerPins, string ApiHost, int ApiPort, string AccountDirectory, string? Pod, string? Node)
{
    public const string DefaultAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    public static ClusterAgentOptions FromEnvironment(Func<string, string?> get)
    {
        string Required(string name) =>
            get(name) is { Length: > 0 } value ? value : throw new ArgumentException($"The environment variable {name} is not set.");

        var url = Required("BDFU_AGENT_SERVER_URL");
        if (!Uri.TryCreate(url.EndsWith('/') ? url : url + "/", UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException($"BDFU_AGENT_SERVER_URL is not an http(s) URL: {url}");
        }

        var connection = Guid.TryParse(Required("BDFU_AGENT_CONNECTION"), out var id) ? id : throw new ArgumentException($"BDFU_AGENT_CONNECTION is not a connection id: {get("BDFU_AGENT_CONNECTION")}");
        var port = get("KUBERNETES_SERVICE_PORT") is { Length: > 0 } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and < 65536 ? parsed : throw new ArgumentException($"KUBERNETES_SERVICE_PORT is '{text}'.")
            : 443;
        var pins = (get("BDFU_MOVER_SERVER_PINS") ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ClusterAgentOptions(
            server, connection, Required("BDFU_AGENT_TOKEN"), pins, Required("KUBERNETES_SERVICE_HOST"), port,
            get("BDFU_AGENT_ACCOUNT_DIRECTORY") is { Length: > 0 } directory ? directory : DefaultAccountDirectory, get("POD_NAME"), get("NODE_NAME"));
    }
}

/// <summary>
/// The cluster agent: keeps the control WebSocket to the server open (reconnecting after a pause that grows to 30 s)
/// and, for each stream the server asks for, joins a TCP connection to the API server with a WebSocket of its own
/// (<see cref="ClusterTunnel"/>). It never reads what passes: TLS runs from the server to the API server.
/// </summary>
internal sealed class ClusterAgent(
    ClusterAgentOptions options, Func<Uri, CancellationToken, Task<WebSocket>> connectWebSocket, Func<CancellationToken, Task<Stream>> connectApi, TextWriter log)
{
    private static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _send = new(1, 1);

    public static ClusterAgent Create(ClusterAgentOptions options, TextWriter log) => new(
        options,
        async (uri, ct) =>
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + options.Token);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            if (options.ServerPins.Count > 0)
            {
                socket.Options.RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(TlsTrustMode.Pinned, options.ServerPins);
            }

            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
        async ct =>
        {
            var client = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(options.ApiHost, options.ApiPort, timeout.Token).ConfigureAwait(false);
                return new OwningStream(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        },
        log);

    /// <summary>How often the ServiceAccount token file is checked for a renewed token.</summary>
    internal TimeSpan TokenCheck { get; init; } = TimeSpan.FromSeconds(30);

    private Uri Control => WebSocketUri(ClusterTunnel.ControlPath(options.ConnectionId));

    /// <summary>Runs until stopped; connection errors are logged and retried.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pause = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await SessionAsync(() => connected = true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.WriteLine($"{(connected ? "The connection to" : "Cannot connect to")} the backup server {options.ServerUrl.Authority} ({Control}): {Describe(ex)}. Again in {pause.TotalSeconds:0} s.");
            }

            pause = connected ? TimeSpan.FromSeconds(1) : pause;
            try
            {
                await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pause = TimeSpan.FromSeconds(Math.Min(MaxPause.TotalSeconds, pause.TotalSeconds * 2));
        }
    }

    private async Task SessionAsync(Action connected, CancellationToken cancellationToken)
    {
        using var control = await connectWebSocket(Control, cancellationToken).ConfigureAwait(false);
        connected();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = ReadAccount("token");
        await SendAsync(control, new ClusterAgentMessage
        {
            Type = ClusterAgentMessageTypes.Hello,
            Version = Version,
            ApiHost = options.ApiHost,
            ApiPort = options.ApiPort,
            TlsName = ClusterTunnel.ApiServerName,
            CaPem = ReadAccount("ca.crt"),
            Token = token,
            Pod = options.Pod,
            Node = options.Node,
        }, session.Token).ConfigureAwait(false);
        log.WriteLine($"Connected to the backup server {options.ServerUrl.Authority}; the API server is {options.ApiHost}:{options.ApiPort}.");

        var renew = RenewTokenAsync(control, token, session.Token);
        try
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await control.ReceiveAsync(buffer.AsMemory(), session.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException($"closed by the server ({control.CloseStatus} {control.CloseStatusDescription})");
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var received = JsonSerializer.Deserialize(message.ToArray(), AgentJson.Default.ClusterAgentMessage);
                message.SetLength(0);
                if (received is { Type: ClusterAgentMessageTypes.Open, Stream: { } stream })
                {
                    _ = TunnelAsync(control, stream, cancellationToken);
                }
            }
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await renew.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped with the session.
            }
        }
    }

    /// <summary>Joins one TCP connection to the API server with a WebSocket of its own to the server.</summary>
    private async Task TunnelAsync(WebSocket control, Guid stream, CancellationToken cancellationToken)
    {
        Stream api;
        try
        {
            api = await connectApi(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            var error = $"the agent cannot reach the API server {options.ApiHost}:{options.ApiPort}: {Describe(ex)}";
            log.WriteLine($"Stream {stream}: {error}");
            try
            {
                await SendAsync(control, new ClusterAgentMessage { Type = ClusterAgentMessageTypes.Failed, Stream = stream, Error = error }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception send) when (send is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The control connection is gone too; the server times the stream out.
            }

            return;
        }

        try
        {
            using var socket = await connectWebSocket(WebSocketUri(ClusterTunnel.TunnelPath(options.ConnectionId, stream)), cancellationToken).ConfigureAwait(false);
            await ClusterTunnel.PumpAsync(api, socket, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or HttpRequestException)
        {
            await api.DisposeAsync().ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                log.WriteLine($"Stream {stream}: {Describe(ex)}");
            }
        }
    }

    /// <summary>Sends the ServiceAccount token again whenever the kubelet has renewed it (projected tokens live an hour).</summary>
    private async Task RenewTokenAsync(WebSocket control, string sent, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TokenCheck, cancellationToken).ConfigureAwait(false);
            string token;
            try
            {
                token = ReadAccount("token");
            }
            catch (IOException ex)
            {
                log.WriteLine(ex.Message);
                continue;
            }

            if (token != sent)
            {
                await SendAsync(control, new ClusterAgentMessage { Type = ClusterAgentMessageTypes.Token, Token = token }, cancellationToken).ConfigureAwait(false);
                sent = token;
            }
        }
    }

    private async Task SendAsync(WebSocket socket, ClusterAgentMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, AgentJson.Default.ClusterAgentMessage);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private string ReadAccount(string file)
    {
        var path = Path.Combine(options.AccountDirectory, file);
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The ServiceAccount's {file} cannot be read from {path} (automountServiceAccountToken off?): {ex.Message}", ex);
        }
    }

    private Uri WebSocketUri(string path)
    {
        var builder = new UriBuilder(new Uri(options.ServerUrl, path)) { Scheme = options.ServerUrl.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    private static string Version =>
        typeof(ClusterAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => $"{ex.Message} ({inner.Message})",
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            $"the server certificate is not trusted (pinned keys or the system's CA store; with a certificate of your own CA set Agent:ServerPins on the server): {ex.Message}",
        _ => ex.Message,
    };

    /// <summary>A TCP connection's stream that closes its client with it.</summary>
    private sealed class OwningStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClusterAgentMessage))]
internal sealed partial class AgentJson : JsonSerializerContext;
