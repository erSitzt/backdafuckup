using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Tape.Devices;

namespace Backdafuckup.Server.Tape;

/// <summary>The hosts tape devices can be attached to: this server, and every tape agent.</summary>
public sealed class TapeHosts(ITapeHost local, TapeAgents agents)
{
    public async Task<IReadOnlyList<TapeHostDto>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<TapeHostDto> { new(TapeHostIds.Local, local.Name, true) };
        foreach (var agent in await agents.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TapeHostDto(TapeHostIds.Agent(agent.Id), $"tape agent {agent.Name}", agent.Online));
        }

        return result;
    }

    /// <summary>The host; an agent that is not connected fails with a message that says since when and from where it was last seen.</summary>
    public async Task<ITapeHost> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (id == TapeHostIds.Local)
        {
            return local;
        }

        if (!Guid.TryParse(id, out var agentId) || await agents.FindAsync(agentId, cancellationToken).ConfigureAwait(false) is not { } agent)
        {
            throw new TapeRequestException($"There is no tape host '{id}': it is this server ('{TapeHostIds.Local}') or a tape agent's id.", StatusCodes.Status404NotFound);
        }

        return agents.Connection(agentId) ?? throw new IOException(
            $"The tape agent {agent.Name} is not connected" +
            (agent.LastSeenUtc is { } seen ? $" (last seen {seen.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC from {agent.LastAddress ?? "an unknown address"})" : " (it has never connected)") +
            $". Is the service backdafuckup-tape-agent running on {agent.HostName ?? "its host"}, and does it reach this server?");
    }

    /// <summary>Host names for messages and the console, by host id.</summary>
    public async Task<IReadOnlyDictionary<string, string>> NamesAsync(CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.Id, h => h.Name, StringComparer.Ordinal);
}

/// <summary>A request that cannot be done as asked; the message says why, for the console.</summary>
public sealed class TapeRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
