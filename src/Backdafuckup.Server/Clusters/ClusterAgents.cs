using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Tls;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Clusters;

/// <summary>
/// The agents of clusters the server reaches through them (<see cref="ConnectionAccess.Agent"/>, <see cref="ClusterTunnel"/>).
/// Each such cluster gets a port on the loopback address of this server: the Kubernetes client connects there as to
/// the API server, and every TCP connection it opens is carried by a WebSocket of the agent to the API server. The
/// ServiceAccount token and CA the agent sends are kept in memory only.
/// </summary>
public sealed class ClusterAgents : IAsyncDisposable
{
    private readonly IDbContextFactory<AppDbContext> dbFactory;
    private readonly CredentialProtector protector;
    private readonly IServiceProvider services;
    private readonly TimeProvider time;
    private readonly ILogger<ClusterAgents> logger;

    public ClusterAgents(
        IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, IServiceProvider services, TimeProvider time, ILogger<ClusterAgents> logger, System.Diagnostics.Metrics.IMeterFactory meters)
    {
        this.dbFactory = dbFactory;
        this.protector = protector;
        this.services = services;
        this.time = time;
        this.logger = logger;
        meters.Create(Telemetry.BackupMetrics.MeterName).CreateObservableGauge(
            "bdfu_k8s_agent_connected",
            () => _endpoints.Values.Select(e => new System.Diagnostics.Metrics.Measurement<int>(e.Session is null ? 0 : 1, new KeyValuePair<string, object?>("cluster", e.Name))),
            description: "1 while the agent of a cluster reached through it is connected to this server, 0 once it is gone (clusters whose agent connected since the server started).");
    }

    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long an agent update waits for sessions of clusters to end before it looks again.</summary>
    internal TimeSpan UpdateRetry { get; set; } = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly ConcurrentDictionary<Guid, Endpoint> _endpoints = new();
    private readonly CancellationTokenSource _stopping = new();

    internal ILogger Logger => logger;

    /// <summary>The agent's state for the console; null before it ever connected to this server instance.</summary>
    public ConnectionAgentDto? Describe(Guid connectionId) =>
        _endpoints.TryGetValue(connectionId, out var endpoint)
            ? endpoint.Session is { } session
                ? new ConnectionAgentDto(true, session.Version, session.ConnectedUtc, session.LastContactUtc, session.Pod, session.Node, session.ApiServer)
                : new ConnectionAgentDto(false, endpoint.LastVersion, null, endpoint.LastContactUtc, null, null, null)
            : null;

    /// <summary>How the client reaches the cluster now: the local port of its tunnel, the agent's token and CA.</summary>
    /// <exception cref="InvalidOperationException">The agent is not connected.</exception>
    public ConnectionSettings Settings(HypervisorConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!_endpoints.TryGetValue(connection.Id, out var endpoint) || endpoint.Session is not { } session)
        {
            var since = _endpoints.TryGetValue(connection.Id, out var known) && known.LastContactUtc is { } last ? $" since {last.ToLocalTime():g}" : " yet";
            throw new InvalidOperationException(
                $"The agent of cluster {connection.DisplayName} is not connected to this server{since}. It runs as the Deployment backdafuckup-agent in backdafuckup-system " +
                "(see the cluster's setup manifest) and needs to reach this server's address (Guest agents → Settings).");
        }

        return new ConnectionSettings(
            connection.Id,
            connection.Kind,
            IPAddress.Loopback.ToString(),
            endpoint.Port,
            new NetworkCredential("agent", session.ApiToken),
            TlsTrustMode.Pinned,
            session.Authorities,
            Engine.ConnectionManager.Options(connection, new Dictionary<string, string> { [KubernetesConnectionOptions.TlsName] = session.TlsName, [KubernetesConnectionOptions.ClusterName] = connection.DisplayName }));
    }

    /// <summary>The connection whose connect token the request carries, or null.</summary>
    internal async Task<HypervisorConnection?> AuthenticateAsync(Guid connectionId, string? authorization, CancellationToken cancellationToken)
    {
        const string Scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = await db.Connections.AsNoTracking().Include(c => c.Credential)
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.Kind == HypervisorKind.Kubernetes && c.Access == ConnectionAccess.Agent, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return null;
        }

        var expected = Encoding.UTF8.GetBytes(protector.Unprotect(connection.Credential.ProtectedSecret));
        var presented = Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(expected, presented) ? connection : null;
    }

    /// <summary>The agent's control connection, until it closes: hello, renewed tokens, streams it could not open.</summary>
    internal async Task RunControlAsync(HypervisorConnection connection, WebSocket socket, string remote, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        ClusterAgentMessage? hello;
        using (var first = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
        {
            first.CancelAfter(HelloTimeout);
            hello = await ReceiveAsync(socket, first.Token).ConfigureAwait(false);
        }

        if (hello is not { Type: ClusterAgentMessageTypes.Hello, Token: { Length: > 0 } token, CaPem: { Length: > 0 } caPem })
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "a hello with the ServiceAccount's token and CA comes first").ConfigureAwait(false);
            return;
        }

        X509Certificate2Collection authorities;
        try
        {
            authorities = [];
            authorities.ImportFromPem(caPem);
        }
        catch (CryptographicException ex)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, $"the cluster CA cannot be read: {ex.Message}").ConfigureAwait(false);
            return;
        }

        var now = time.GetUtcNow();
        var session = new AgentSession(socket, Json)
        {
            ApiToken = token,
            Authorities = [.. authorities.Select(c => CertificatePinning.ToPin("cluster CA", c))],
            TlsName = string.IsNullOrWhiteSpace(hello.TlsName) ? ClusterTunnel.ApiServerName : hello.TlsName.Trim(),
            Version = hello.Version,
            Pod = hello.Pod,
            Node = hello.Node,
            ApiServer = hello.ApiHost is null ? null : $"{hello.ApiHost}:{hello.ApiPort ?? 443}",
            ConnectedUtc = now,
            LastContactUtc = now,
        };
        foreach (var certificate in authorities)
        {
            certificate.Dispose();
        }

        var endpoint = _endpoints.GetOrAdd(connection.Id, id => new Endpoint(this, id, _stopping.Token));
        endpoint.Name = connection.DisplayName;
        if (endpoint.Attach(session) is { } previous)
        {
            await CloseAsync(previous.Socket, WebSocketCloseStatus.NormalClosure, "replaced by a new connection of the agent").ConfigureAwait(false);
        }

        logger.LogInformation(
            "The agent of cluster {Cluster} connected from {Remote}: pod {Pod} on node {Node}, version {Version}, API server {ApiServer}; tunnel port {Port}.",
            connection.DisplayName, remote, session.Pod, session.Node, session.Version, session.ApiServer, endpoint.Port);
        _ = RefreshAsync(connection.Id, connection.DisplayName);
        _ = KeepCurrentAsync(connection, endpoint, session);
        try
        {
            while (await ReceiveAsync(socket, linked.Token).ConfigureAwait(false) is { } message)
            {
                session.LastContactUtc = time.GetUtcNow();
                switch (message.Type)
                {
                    case ClusterAgentMessageTypes.Token when message.Token is { Length: > 0 } renewed:
                        session.ApiToken = renewed;
                        break;
                    case ClusterAgentMessageTypes.Failed when message.Stream is { } stream:
                        endpoint.Fail(stream, message.Error ?? "the agent could not open the stream");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or JsonException)
        {
            if (!linked.IsCancellationRequested)
            {
                logger.LogWarning("The control connection of the agent of cluster {Cluster} ({Remote}) broke off: {Error}", connection.DisplayName, remote, ex.Message);
            }
        }
        finally
        {
            if (endpoint.Detach(session))
            {
                logger.LogWarning("The agent of cluster {Cluster} ({Remote}) disconnected.", connection.DisplayName, remote);
                await MarkDisconnectedAsync(connection.Id, connection.DisplayName).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One stream the agent opened: joined with the client's TCP connection that asked for it.</summary>
    internal async Task RunTunnelAsync(Guid connectionId, Guid stream, WebSocket socket, CancellationToken cancellationToken)
    {
        if (!_endpoints.TryGetValue(connectionId, out var endpoint) || endpoint.Join(stream) is not { } client)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "no such stream (timed out?)").ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            await ClusterTunnel.PumpAsync(client.GetStream(), socket, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var endpoint in _endpoints.Values)
        {
            endpoint.Dispose();
        }

        _stopping.Dispose();
    }

    /// <summary>Reads the cluster (platform and inventory) once its agent is there.</summary>
    private async Task RefreshAsync(Guid connectionId, string name)
    {
        try
        {
            var manager = services.GetRequiredService<ConnectionManager>();
            await using var db = await dbFactory.CreateDbContextAsync(_stopping.Token).ConfigureAwait(false);
            if (await Api.InfrastructureEndpoints.TryRefreshInventoryAsync(connectionId, db, manager, time, _stopping.Token).ConfigureAwait(false) is { } error)
            {
                logger.LogWarning("Cluster {Cluster} through its agent: {Error}", name, error);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Cluster {Cluster} could not be read through its agent: {Error}", name, ex.Message);
        }
    }

    /// <summary>
    /// Brings the agent's Deployment to this server's mover image (the agent then restarts with it and connects again).
    /// Not while sessions of clusters run: the tunnel is down for the restart. Waits for them as long as the agent stays connected.
    /// </summary>
    private async Task KeepCurrentAsync(HypervisorConnection connection, Endpoint endpoint, AgentSession session)
    {
        try
        {
            while (endpoint.Session == session)
            {
                if (!await ClusterSessionsRunAsync().ConfigureAwait(false))
                {
                    var mover = await services.GetRequiredService<Movers.MoverRegistry>().EnsurePushedAsync(null, _stopping.Token).ConfigureAwait(false);
                    await using var cluster = await services.GetRequiredService<ConnectionManager>().OpenAsync(connection.Id, _stopping.Token).ConfigureAwait(false);
                    if (cluster is IKubernetesAgentUpdater updater && await updater.UpdateAgentImageAsync(mover.Image, _stopping.Token).ConfigureAwait(false) is { } previous)
                    {
                        logger.LogInformation("The agent of cluster {Cluster} ran {Previous}; its Deployment now runs {Image}, and the agent reconnects with it.", connection.DisplayName, previous, mover.Image);
                    }

                    if (cluster is IKubernetesAgentUpdater nodes && await nodes.UpdateNodeAgentImageAsync(mover.Image, _stopping.Token).ConfigureAwait(false) is { } before)
                    {
                        logger.LogInformation("The node agent of cluster {Cluster} ran {Previous}; its DaemonSet now runs {Image}.", connection.DisplayName, before, mover.Image);
                    }

                    return;
                }

                logger.LogInformation("The agent of cluster {Cluster} is checked for a newer image once no session of a cluster runs.", connection.DisplayName);
                await Task.Delay(UpdateRetry, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            logger.LogWarning("The agent of cluster {Cluster} could not be brought to this server's version: {Error}", connection.DisplayName, ex.Message);
        }
    }

    private async Task<bool> ClusterSessionsRunAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync(_stopping.Token).ConfigureAwait(false);
        return await db.Sessions.AnyAsync(
            s => s.ObjectKind == RestorePointObjectKinds.KubernetesNamespace && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping),
            _stopping.Token).ConfigureAwait(false);
    }

    private async Task MarkDisconnectedAsync(Guid connectionId, string name)
    {
        try
        {
            var at = time.GetUtcNow();
            await using var db = await dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            await db.Connections.Where(c => c.Id == connectionId).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, ConnectionStatus.Error)
                .SetProperty(c => c.LastError, $"The agent of cluster {name} disconnected at {at.ToLocalTime():g}.")).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException or ObjectDisposedException)
        {
            // Status only.
        }
    }

    private static async Task<ClusterAgentMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > 1024 * 1024)
            {
                throw new IOException("A control message of the agent is larger than 1 MiB.");
            }

            if (result.EndOfMessage)
            {
                return JsonSerializer.Deserialize<ClusterAgentMessage>(message.ToArray(), Json);
            }
        }
    }

    internal static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseAsync(status, reason, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    /// <summary>The agent's current control connection and what it sent.</summary>
    private sealed class AgentSession(WebSocket socket, JsonSerializerOptions json)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public WebSocket Socket => socket;

        public required string ApiToken { get; set; }

        public required IReadOnlyList<PinnedCertificate> Authorities { get; init; }

        public required string TlsName { get; init; }

        public string? Version { get; init; }

        public string? Pod { get; init; }

        public string? Node { get; init; }

        public string? ApiServer { get; init; }

        public DateTimeOffset ConnectedUtc { get; init; }

        public DateTimeOffset LastContactUtc { get; set; }

        public async Task SendAsync(ClusterAgentMessage message, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, json);
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
    }

    /// <summary>A cluster's local port, its agent session, and the client connections waiting for their stream.</summary>
    private sealed class Endpoint : IDisposable
    {
        private readonly ClusterAgents _owner;
        private readonly Guid _connectionId;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<Guid, (TcpClient Client, TaskCompletionSource Joined)> _pending = new();
        private readonly CancellationToken _stopping;
        private AgentSession? _session;

        public Endpoint(ClusterAgents owner, Guid connectionId, CancellationToken stopping)
        {
            _owner = owner;
            _connectionId = connectionId;
            _stopping = stopping;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        /// <summary>The cluster's name (for metrics).</summary>
        public string Name { get; set; } = string.Empty;

        public AgentSession? Session => Volatile.Read(ref _session);

        public DateTimeOffset? LastContactUtc { get; private set; }

        public string? LastVersion { get; private set; }

        /// <summary>Makes <paramref name="session"/> the current one; returns the one it replaces.</summary>
        public AgentSession? Attach(AgentSession session)
        {
            LastVersion = session.Version;
            return Interlocked.Exchange(ref _session, session);
        }

        /// <summary>Ends <paramref name="session"/> if it is still the current one.</summary>
        public bool Detach(AgentSession session)
        {
            LastContactUtc = session.LastContactUtc;
            return Interlocked.CompareExchange(ref _session, null, session) == session;
        }

        /// <summary>The client connection waiting for <paramref name="stream"/>, taken from the waiting ones.</summary>
        public TcpClient? Join(Guid stream)
        {
            if (!_pending.TryRemove(stream, out var waiting))
            {
                return null;
            }

            waiting.Joined.TrySetResult();
            return waiting.Client;
        }

        public void Fail(Guid stream, string error)
        {
            if (_pending.TryRemove(stream, out var waiting))
            {
                _owner.Logger.LogWarning("Stream {Stream} to the API server through the agent failed: {Error}", stream, error);
                waiting.Joined.TrySetResult();
                waiting.Client.Dispose();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            foreach (var waiting in _pending.Values)
            {
                waiting.Client.Dispose();
            }
        }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = OpenAsync(client);
            }
        }

        /// <summary>Asks the agent for a stream for a client connection, and closes the connection when none comes.</summary>
        private async Task OpenAsync(TcpClient client)
        {
            if (Session is not { } session)
            {
                client.Dispose();
                return;
            }

            var stream = Guid.NewGuid();
            var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[stream] = (client, joined);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                timeout.CancelAfter(OpenTimeout);
                await session.SendAsync(new ClusterAgentMessage { Type = ClusterAgentMessageTypes.Open, Stream = stream }, timeout.Token).ConfigureAwait(false);
                await joined.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                if (_pending.TryRemove(stream, out _))
                {
                    _owner.Logger.LogWarning("The agent of connection {Connection} did not open a stream to the API server within {Timeout} s.", _connectionId, OpenTimeout.TotalSeconds);
                    client.Dispose();
                }
            }
        }
    }
}
