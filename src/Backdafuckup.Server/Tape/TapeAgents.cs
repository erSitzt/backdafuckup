using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Tape.Agent;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Tape;

/// <summary>
/// Tape agents: created in the console with a connect token (shown once, kept protected), connected through their
/// control WebSocket. A connected agent is a tape host like this server; a newer connection of the same agent replaces
/// the older one.
/// </summary>
public sealed class TapeAgents
{
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CredentialProtector _protector;
    private readonly Agents.AgentSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<TapeAgents> _logger;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<Guid, string> _seen = new();

    public TapeAgents(
        IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, Agents.AgentSettingsStore settings, TimeProvider time, ILogger<TapeAgents> logger, IMeterFactory meters)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _settings = settings;
        _time = time;
        _logger = logger;
        meters.Create(Telemetry.BackupMetrics.MeterName).CreateObservableGauge(
            "bdfu_tape_agent_connected",
            () => _seen.Select(a => new Measurement<int>(_sessions.ContainsKey(a.Key) ? 1 : 0, new KeyValuePair<string, object?>("agent", a.Value))),
            description: "1 while a tape agent is connected to this server, 0 once it is gone (agents that connected since the server started).");
    }

    public TapeAgentConnection? Connection(Guid agentId) => _sessions.TryGetValue(agentId, out var session) ? session.Connection : null;

    public async Task<TapeAgentEntity?> FindAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TapeAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TapeAgentDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agents = await db.TapeAgents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().Select(l => new { l.Host, l.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return agents.Select(a => ToDto(a, libraries.Where(l => l.Host == TapeHostIds.Agent(a.Id)).Select(l => l.Name).Order(StringComparer.Ordinal).ToList())).ToList();
    }

    public async Task<TapeAgentSecretDto> CreateAsync(string name, CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        var url = await ServerUrlAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.TapeAgents.AnyAsync(a => a.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A tape agent named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var token = NewToken();
        var agent = new TapeAgentEntity { Name = name, ProtectedToken = _protector.Protect(token), CreatedUtc = _time.GetUtcNow() };
        db.TapeAgents.Add(agent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Tape agent {Agent} ({Id}) created.", name, agent.Id);
        return new TapeAgentSecretDto(ToDto(agent, []), EnvironmentFile(agent, url, token));
    }

    /// <summary>A new connect token; the agent is disconnected until its settings file has the new one.</summary>
    public async Task<TapeAgentSecretDto> RenewAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var url = await ServerUrlAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.TapeAgents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape agent {agentId} does not exist.", StatusCodes.Status404NotFound);
        var token = NewToken();
        agent.ProtectedToken = _protector.Protect(token);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Disconnect(agentId);
        _logger.LogInformation("Connect token of tape agent {Agent} renewed; it is disconnected until it uses the new one.", agent.Name);
        return new TapeAgentSecretDto(ToDto(agent, []), EnvironmentFile(agent, url, token));
    }

    public async Task DeleteAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var agent = await db.TapeAgents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape agent {agentId} does not exist.", StatusCodes.Status404NotFound);
        var host = TapeHostIds.Agent(agentId);
        var libraries = await db.TapeLibraries.Where(l => l.Host == host).Select(l => l.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (libraries.Count > 0)
        {
            throw new TapeRequestException($"The tape agent {agent.Name} has libraries ({string.Join(", ", libraries)}); remove them first.", StatusCodes.Status409Conflict);
        }

        db.TapeAgents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Disconnect(agentId);
        _seen.TryRemove(agentId, out _);
    }

    /// <summary>The agent whose connect token the request carries, or null.</summary>
    internal async Task<TapeAgentEntity?> AuthenticateAsync(Guid agentId, string? authorization, CancellationToken cancellationToken)
    {
        const string Scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var agent = await FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null)
        {
            return null;
        }

        var expected = Encoding.UTF8.GetBytes(_protector.Unprotect(agent.ProtectedToken));
        var presented = Encoding.UTF8.GetBytes(authorization[Scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(expected, presented) ? agent : null;
    }

    /// <summary>The agent's control connection, until it closes.</summary>
    internal async Task RunControlAsync(TapeAgentEntity agent, WebSocket socket, string remote, CancellationToken cancellationToken)
    {
        (TapeAgentMessage Header, byte[] Data)? hello;
        try
        {
            using var first = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            first.CancelAfter(HelloTimeout);
            hello = await TapeAgentProtocol.ReceiveAsync(socket, first.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Tape agent {Agent} from {Remote} sent no valid hello: {Error}", agent.Name, remote, ex.Message);
            return;
        }

        if (hello is not { Header: { Type: TapeAgentMessageTypes.Hello } header } || header.Protocol != TapeAgentProtocol.Version)
        {
            var reason = hello is { Header.Type: TapeAgentMessageTypes.Hello } ? $"protocol {hello.Value.Header.Protocol} is not {TapeAgentProtocol.Version}: update the tape agent" : "a hello comes first";
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning("Tape agent {Agent} from {Remote} refused: {Reason}.", agent.Name, remote, reason);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new Session(new TapeAgentConnection(socket, $"tape agent {agent.Name}", header), stop);
        if (_sessions.TryGetValue(agent.Id, out var older))
        {
            await older.Stop.CancelAsync().ConfigureAwait(false);
        }

        _sessions[agent.Id] = session;
        _seen[agent.Id] = agent.Name;
        await RecordAsync(agent.Id, header, remote).ConfigureAwait(false);
        _logger.LogInformation(
            "Tape agent {Agent} connected from {Remote} (host {Host}, version {Version}) with {Devices} device(s).",
            agent.Name, remote, header.HostName, header.AgentVersion, header.Devices?.Count ?? 0);
        try
        {
            await session.Connection.RunAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<Guid, Session>(agent.Id, session));
            await RecordAsync(agent.Id, null, null).ConfigureAwait(false);
            _logger.LogInformation("Tape agent {Agent} ({Remote}) disconnected.", agent.Name, remote);
        }
    }

    private void Disconnect(Guid agentId)
    {
        if (_sessions.TryGetValue(agentId, out var session))
        {
            try
            {
                session.Stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended meanwhile.
            }
        }
    }

    private async Task RecordAsync(Guid agentId, TapeAgentMessage? hello, string? remote)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            if (hello is null)
            {
                await db.TapeAgents.Where(a => a.Id == agentId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenUtc, now), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await db.TapeAgents.Where(a => a.Id == agentId).ExecuteUpdateAsync(
                s => s.SetProperty(a => a.LastSeenUtc, now).SetProperty(a => a.HostName, hello.HostName).SetProperty(a => a.AgentVersion, hello.AgentVersion).SetProperty(a => a.LastAddress, remote),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            _logger.LogWarning(ex, "Recording the contact of tape agent {Agent} failed.", agentId);
        }
    }

    private async Task<string> ServerUrlAsync(CancellationToken cancellationToken) =>
        await _settings.GetServerUrlAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } url
            ? url.TrimEnd('/') + "/"
            : throw new TapeRequestException("Set the address under which agents reach this server first (Guest Agents → Settings → server address).", StatusCodes.Status409Conflict);

    private string EnvironmentFile(TapeAgentEntity agent, string url, string token) =>
        $"""
        # Tape agent "{agent.Name}" of the backup server {url}
        # Save as /etc/backdafuckup-tape-agent/agent.env (owner root, mode 600), then: systemctl restart backdafuckup-tape-agent
        BDFU_SERVER_URL={url}
        BDFU_TAPE_AGENT_ID={agent.Id:D}
        BDFU_TAPE_AGENT_TOKEN={token}
        BDFU_SERVER_PINS={string.Join(",", _settings.ServerPins())}

        """;

    private static string NewToken() => "bdfuta_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private TapeAgentDto ToDto(TapeAgentEntity agent, IReadOnlyList<string> libraries)
    {
        var connection = Connection(agent.Id);
        return new TapeAgentDto(
            agent.Id, agent.Name, connection is not null, connection?.HostName ?? agent.HostName, connection?.AgentVersion ?? agent.AgentVersion,
            connection is not null ? _time.GetUtcNow() : agent.LastSeenUtc, agent.LastAddress, connection?.Devices.Count ?? 0, libraries, agent.CreatedUtc);
    }

    private sealed record Session(TapeAgentConnection Connection, CancellationTokenSource Stop);
}
