using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Nbd;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Proxies;

/// <summary>A request about proxies that cannot be done; the message says why.</summary>
public sealed class ProxyRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The backup proxies: their records (install token and key only as hashes), their live control connections, the
/// tunnels the server asks them to open, and the disks the providers attach through them (<see cref="IProxyPool"/>).
/// </summary>
public sealed class ProxyAgents(
    IDbContextFactory<AppDbContext> dbFactory, Agents.AgentSettingsStore settings, TimeProvider time, ILogger<ProxyAgents> logger, System.Diagnostics.Metrics.IMeterFactory meters) : IProxyPool
{
    private readonly System.Diagnostics.Metrics.Meter _meter = meters.Create(Telemetry.BackupMetrics.MeterName);
    private readonly ConcurrentDictionary<Guid, string> _seen = new();
    private int _gauges;

    private static readonly TimeSpan InstallTokenLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TunnelTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, ProxyConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, PendingTunnel> _tunnels = new();

    /// <summary>How long a proxy waits for an attached disk to appear (tests shorten it).</summary>
    internal TimeSpan DeviceWait { get; set; } = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<ProxyDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return [.. (await db.Proxies.AsNoTracking().OrderBy(p => p.Name).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDto)];
    }

    /// <summary>A new proxy with a one-time install token (24 hours).</summary>
    public async Task<ProxyInstallDto> CreateAsync(string name, CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new ProxyRequestException("The name must have 1 to 100 characters.");
        }

        var url = await ServerUrlAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Proxies.AnyAsync(p => p.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new ProxyRequestException($"A proxy named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var token = NewSecret("bdfupi_");
        var proxy = new ProxyEntity
        {
            Name = name,
            CreatedUtc = time.GetUtcNow(),
            InstallTokenHash = Hash(token),
            InstallTokenExpiresUtc = time.GetUtcNow() + InstallTokenLifetime,
        };
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Proxy {Proxy} ({Id}) created; its install token is valid until {Expiry:u}.", name, proxy.Id, proxy.InstallTokenExpiresUtc);
        return new ProxyInstallDto(ToDto(proxy), token, Command(proxy, url, token));
    }

    /// <summary>A new install token: the proxy enrolls again (e.g. after its machine was rebuilt); its old key stops working.</summary>
    public async Task<ProxyInstallDto> RenewInstallAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        var url = await ServerUrlAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false)
            ?? throw new ProxyRequestException($"Proxy {proxyId} does not exist.", StatusCodes.Status404NotFound);
        var token = NewSecret("bdfupi_");
        proxy.InstallTokenHash = Hash(token);
        proxy.InstallTokenExpiresUtc = time.GetUtcNow() + InstallTokenLifetime;
        proxy.KeyHash = null;
        proxy.EnrolledUtc = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Disconnect(proxyId);
        return new ProxyInstallDto(ToDto(proxy), token, Command(proxy, url, token));
    }

    public async Task DeleteAsync(Guid proxyId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false)
            ?? throw new ProxyRequestException($"Proxy {proxyId} does not exist.", StatusCodes.Status404NotFound);
        if (_connections.TryGetValue(proxyId, out var connection) && connection.OpenHandles > 0)
        {
            throw new ProxyRequestException($"Proxy {proxy.Name} has {connection.OpenHandles} disk(s) open for a running session; remove it after the session.", StatusCodes.Status409Conflict);
        }

        db.Proxies.Remove(proxy);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Disconnect(proxyId);
    }

    /// <summary>The agent's enrollment: a valid, unexpired install token gives it its own key (shown only now).</summary>
    public async Task<EnrollProxyResponseDto> EnrollAsync(EnrollProxyRequestDto request, string remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var proxy = await db.Proxies.FirstOrDefaultAsync(p => p.Id == request.ProxyId, cancellationToken).ConfigureAwait(false);
        if (proxy?.InstallTokenHash is not { } expected || !CryptographicOperations.FixedTimeEquals(expected, Hash(request.Token ?? string.Empty)))
        {
            logger.LogWarning("Proxy enrollment from {Remote} for {Proxy} refused: unknown proxy or wrong install token.", remote, request.ProxyId);
            throw new ProxyRequestException("Unknown proxy or wrong install token (create a new one under Infrastructure → Proxies).", StatusCodes.Status401Unauthorized);
        }

        if (proxy.InstallTokenExpiresUtc is { } expiry && expiry < time.GetUtcNow())
        {
            throw new ProxyRequestException($"The install token of proxy {proxy.Name} expired at {expiry:u}; renew it under Infrastructure → Proxies.", StatusCodes.Status401Unauthorized);
        }

        var key = NewSecret("bdfupk_");
        proxy.KeyHash = Hash(key);
        proxy.InstallTokenHash = null;
        proxy.InstallTokenExpiresUtc = null;
        proxy.EnrolledUtc = time.GetUtcNow();
        proxy.HostName = request.HostName ?? proxy.HostName;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Proxy {Proxy} enrolled from {Remote} (host {Host}).", proxy.Name, remote, request.HostName);
        return new EnrollProxyResponseDto(key);
    }

    /// <summary>The proxy whose key the request carries, or null.</summary>
    internal async Task<ProxyEntity?> AuthenticateAsync(Guid proxyId, string? authorization, CancellationToken cancellationToken)
    {
        if (Bearer(authorization) is not { } presented)
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var proxy = await db.Proxies.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proxyId, cancellationToken).ConfigureAwait(false);
        return proxy?.KeyHash is { } expected && CryptographicOperations.FixedTimeEquals(expected, Hash(presented)) ? proxy : null;
    }

    /// <summary>The proxy's control connection, until it closes.</summary>
    internal async Task RunControlAsync(ProxyEntity proxy, WebSocket socket, string remote, CancellationToken cancellationToken)
    {
        ProxyAgentMessage? hello;
        try
        {
            using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            first.CancelAfter(HelloTimeout);
            hello = await ProxyAgentProtocol.ReceiveAsync(socket, first.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Proxy {Proxy} from {Remote} sent no valid hello: {Error}", proxy.Name, remote, ex.Message);
            return;
        }

        if (hello is not { Type: ProxyMessageTypes.Hello } || hello.ProtocolVersion != ProxyAgentProtocol.Version)
        {
            var reason = hello is { Type: ProxyMessageTypes.Hello }
                ? $"protocol {hello.ProtocolVersion} is not {ProxyAgentProtocol.Version}: update the proxy agent and the server to the same version"
                : "a hello comes first";
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning("Proxy {Proxy} from {Remote} refused: {Reason}.", proxy.Name, remote, reason);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new ProxyConnection(proxy.Id, proxy.Name, socket, hello, stop);
        if (_connections.TryGetValue(proxy.Id, out var older))
        {
            await older.Stop.CancelAsync().ConfigureAwait(false);
        }

        _connections[proxy.Id] = connection;
        _seen[proxy.Id] = proxy.Name;
        RegisterGauges();
        await RecordAsync(proxy.Id, hello, remote).ConfigureAwait(false);
        logger.LogInformation(
            "Proxy {Proxy} connected from {Remote} (host {Host}, version {Version}) with {Devices} device(s).",
            proxy.Name, remote, hello.HostName, hello.AgentVersion, hello.Devices?.Count ?? 0);
        try
        {
            await connection.RunAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _connections.TryRemove(new KeyValuePair<Guid, ProxyConnection>(proxy.Id, connection));
            await RecordAsync(proxy.Id, null, null).ConfigureAwait(false);
            logger.LogInformation("Proxy {Proxy} ({Remote}) disconnected.", proxy.Name, remote);
        }
    }

    /// <summary>The proxies' gauges, once a proxy connected (the series are the proxies that connected since the server started).</summary>
    private void RegisterGauges()
    {
        if (Interlocked.Exchange(ref _gauges, 1) != 0)
        {
            return;
        }

        _meter.CreateObservableGauge(
            "bdfu_proxy_connected",
            () => _seen.Select(p => new System.Diagnostics.Metrics.Measurement<int>(_connections.ContainsKey(p.Key) ? 1 : 0, new KeyValuePair<string, object?>("proxy", p.Value))),
            description: "1 while a backup proxy is connected to this server, 0 once it is gone (proxies that connected since the server started).");
        _meter.CreateObservableGauge(
            "bdfu_proxy_open_disks",
            () => _seen.Select(p => new System.Diagnostics.Metrics.Measurement<int>(
                _connections.TryGetValue(p.Key, out var connection) ? connection.OpenHandles : 0, new KeyValuePair<string, object?>("proxy", p.Value))),
            description: "Disks the server has open on a backup proxy now (hot-added disks being read or written).");
    }

    /// <summary>A tunnel the proxy opened for a stream the server asked for; false when the stream or token is unknown.</summary>
    internal bool TryClaimTunnel(Guid proxyId, Guid streamId, string? authorization, out PendingTunnel tunnel)
    {
        tunnel = null!;
        if (!_tunnels.TryGetValue(streamId, out var pending) || pending.ProxyId != proxyId || Bearer(authorization) is not { } token
            || !CryptographicOperations.FixedTimeEquals(pending.TokenHash, Hash(token)) || !_tunnels.TryRemove(streamId, out _))
        {
            return false;
        }

        tunnel = pending;
        return true;
    }

    public IReadOnlyList<ProxyInfo> Connected() =>
        [.. _connections.Values.Select(c => new ProxyInfo(c.ProxyId, c.Name, c.Hello.HostName, c.Hello.SmbiosUuid, c.Hello.BiosSerial, c.Hello.Capabilities ?? []))];

    public async Task<IProxyDisk> AttachAsync(Guid proxyId, string serial, long sizeBytes, bool writable, CancellationToken cancellationToken)
    {
        var connection = _connections.GetValueOrDefault(proxyId)
            ?? throw new InvalidOperationException($"Proxy {proxyId} is not connected.");
        var device = (await connection.CommandAsync(
            new ProxyAgentMessage { Type = ProxyMessageTypes.WaitDevice, Serial = serial, SizeBytes = sizeBytes, TimeoutMs = (int)DeviceWait.TotalMilliseconds },
            DeviceWait + TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false)).Device ?? throw new InvalidDataException($"{connection.Describe()} answered wait-device without a device.");
        var handle = Guid.NewGuid().ToString("N");
        await connection.CommandAsync(new ProxyAgentMessage { Type = ProxyMessageTypes.Open, Handle = handle, Path = device.Path, Writable = writable }, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);
        Interlocked.Increment(ref connection.OpenHandleCount);
        return new ProxyDisk(this, connection, handle, device);
    }

    /// <summary>Asks the proxy to open a tunnel for the handle and returns its stream once the proxy connected.</summary>
    private async Task<Stream> OpenTunnelAsync(ProxyConnection connection, string handle, CancellationToken cancellationToken)
    {
        var streamId = Guid.NewGuid();
        var token = NewSecret("bdfupt_");
        var pending = new PendingTunnel(connection.ProxyId, Hash(token));
        _tunnels[streamId] = pending;
        try
        {
            await connection.CommandAsync(new ProxyAgentMessage { Type = ProxyMessageTypes.Tunnel, Handle = handle, StreamId = streamId, Token = token }, TunnelTimeout, cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TunnelTimeout);
            try
            {
                return await pending.Socket.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{connection.Describe()} did not open its tunnel within {TunnelTimeout.TotalSeconds:0} s (can it reach this server's address?).");
            }
        }
        finally
        {
            _tunnels.TryRemove(streamId, out _);
        }
    }

    private void Disconnect(Guid proxyId)
    {
        if (_connections.TryGetValue(proxyId, out var connection))
        {
            try
            {
                connection.Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }
    }

    private async Task RecordAsync(Guid proxyId, ProxyAgentMessage? hello, string? remote)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var now = time.GetUtcNow();
            if (hello is null)
            {
                await db.Proxies.Where(p => p.Id == proxyId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LastSeenUtc, now), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await db.Proxies.Where(p => p.Id == proxyId).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.LastSeenUtc, now).SetProperty(p => p.HostName, hello.HostName).SetProperty(p => p.AgentVersion, hello.AgentVersion)
                    .SetProperty(p => p.LastAddress, remote).SetProperty(p => p.SmbiosUuid, hello.SmbiosUuid).SetProperty(p => p.BiosSerial, hello.BiosSerial),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            logger.LogWarning(ex, "Recording the contact of proxy {Proxy} failed.", proxyId);
        }
    }

    private ProxyDto ToDto(ProxyEntity proxy)
    {
        var connection = _connections.GetValueOrDefault(proxy.Id);
        var hello = connection?.Hello;
        return new ProxyDto(
            proxy.Id, proxy.Name, connection is not null, proxy.KeyHash is not null, hello?.HostName ?? proxy.HostName, hello?.AgentVersion ?? proxy.AgentVersion,
            hello?.SmbiosUuid ?? proxy.SmbiosUuid, hello?.Addresses ?? [], hello?.Capabilities ?? [], hello?.Devices?.Count ?? 0,
            connection is not null ? time.GetUtcNow() : proxy.LastSeenUtc, proxy.LastAddress, proxy.CreatedUtc, proxy.InstallTokenExpiresUtc);
    }

    private string Command(ProxyEntity proxy, string url, string token) =>
        $"backdafuckup-proxy enroll --server {url} --proxy {proxy.Id:D} --token {token} --pins {string.Join(",", settings.ServerPins())}";

    private async Task<string> ServerUrlAsync(CancellationToken cancellationToken) =>
        await settings.GetServerUrlAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } url
            ? url.TrimEnd('/') + "/"
            : throw new ProxyRequestException("Set the address under which agents reach this server first (Guest Agents → Settings → server address).", StatusCodes.Status409Conflict);

    private static string? Bearer(string? authorization)
    {
        const string Scheme = "Bearer ";
        return authorization is not null && authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? authorization[Scheme.Length..].Trim() : null;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string NewSecret(string prefix) => prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A tunnel the server asked for: the proxy proves itself with the token; the endpoint hands over its socket.</summary>
    internal sealed class PendingTunnel(Guid proxyId, byte[] tokenHash)
    {
        public Guid ProxyId { get; } = proxyId;

        public byte[] TokenHash { get; } = tokenHash;

        public TaskCompletionSource<Stream> Socket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A disk opened on a proxy; disposing releases it and removes it from the proxy's kernel.</summary>
    private sealed class ProxyDisk(ProxyAgents owner, ProxyConnection connection, string handle, ProxyDevice device) : IProxyDisk
    {
        private int _released;

        public string Description => $"{connection.Describe()} {device.Path}";

        public long SizeBytes => device.SizeBytes;

        public async Task<IDiskReader> OpenReaderAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken)
        {
            var pool = await NbdConnectionPool.CreateAsync(
                async ct => await NbdClient.ConnectAsync(
                    await owner.OpenTunnelAsync(connection, handle, ct).ConfigureAwait(false),
                    new NbdConnectOptions { Host = Description, ExportName = handle, TlsMode = NbdTlsMode.Disabled, MaxInFlight = maxInFlightPerConnection, ConnectTimeout = TimeSpan.FromSeconds(30) },
                    ct).ConfigureAwait(false),
                connections, maxInFlightPerConnection, cancellationToken).ConfigureAwait(false);
            return new ProxyNbdReader(pool, "hotadd", connections * maxInFlightPerConnection);
        }

        public async Task<IDiskBlockWriter> OpenWriterAsync(int connections, int maxInFlightPerConnection, CancellationToken cancellationToken)
        {
            var pool = await NbdConnectionPool.CreateAsync(
                async ct => await NbdClient.ConnectAsync(
                    await owner.OpenTunnelAsync(connection, handle, ct).ConfigureAwait(false),
                    new NbdConnectOptions { Host = Description, ExportName = handle, TlsMode = NbdTlsMode.Disabled, MaxInFlight = maxInFlightPerConnection, ConnectTimeout = TimeSpan.FromSeconds(30) },
                    ct).ConfigureAwait(false),
                connections, maxInFlightPerConnection, cancellationToken).ConfigureAwait(false);
            return new ProxyNbdWriter(pool, connections * maxInFlightPerConnection);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            try
            {
                await connection.CommandAsync(new ProxyAgentMessage { Type = ProxyMessageTypes.Release, Handle = handle, DeleteDevice = true }, TimeSpan.FromSeconds(60), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref connection.OpenHandleCount);
            }
        }
    }
}

/// <summary>One live control connection: commands go out with an id, replies come back with it.</summary>
internal sealed class ProxyConnection(Guid proxyId, string name, WebSocket socket, ProxyAgentMessage hello, CancellationTokenSource stop)
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ProxyAgentMessage>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private long _nextId;
    internal int OpenHandleCount;

    public Guid ProxyId { get; } = proxyId;

    public string Name { get; } = name;

    public ProxyAgentMessage Hello { get; } = hello;

    public CancellationTokenSource Stop { get; } = stop;

    public int OpenHandles => Volatile.Read(ref OpenHandleCount);

    public string Describe() => $"proxy {Name} (host {Hello.HostName})";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await ProxyAgentProtocol.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false) is { } message)
            {
                if (message.Type == ProxyMessageTypes.Reply && _pending.TryRemove(message.Id, out var waiting))
                {
                    waiting.TrySetResult(message);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            // The connection ended; the commands still waiting fail below.
        }
        finally
        {
            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetException(new IOException($"{Describe()} disconnected while a command was running."));
            }

            _pending.Clear();
        }
    }

    /// <summary>Sends a command and waits for its reply; an error reply throws with the proxy's message.</summary>
    public async Task<ProxyAgentMessage> CommandAsync(ProxyAgentMessage command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<ProxyAgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await ProxyAgentProtocol.SendAsync(socket, _send, command with { Id = id }, cancellationToken).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            ProxyAgentMessage answer;
            try
            {
                answer = await reply.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Describe()} did not answer {command.Type} within {timeout.TotalSeconds:0} s.");
            }

            return answer.Error is { } error ? throw new IOException($"{Describe()}: {command.Type} failed: {error}") : answer;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }
}
