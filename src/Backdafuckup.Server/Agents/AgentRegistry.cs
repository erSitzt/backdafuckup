using System.Collections.Concurrent;
using System.Threading.Channels;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Server.Agents;

/// <summary>Timeouts of the agent API (configuration section "Agent").</summary>
public sealed class AgentOptions
{
    /// <summary>How long a poll is held open when there is no command.</summary>
    public TimeSpan PollHold { get; set; } = TimeSpan.FromSeconds(AgentProtocol.PollHoldSeconds);

    /// <summary>An agent counts as online while its last contact is younger than this (a poll is renewed every ≤ 50 s).</summary>
    public TimeSpan OnlineWindow { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>Gathering the VSS writer metadata can take a while on servers with many databases.</summary>
    public TimeSpan PrepareTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Until our VSS provider holds the writes (writers flush first; VSS allows them 60 s).</summary>
    public TimeSpan FreezeTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>The provider releases the writes by itself after this time if no thaw arrives (VSS gives up after 10 s anyway).</summary>
    public TimeSpan ProviderHoldLimit { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ThawTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A file restore into a VM fails when the agent neither downloads nor reports for this long.</summary>
    public TimeSpan TransferSilenceLimit { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Push installation: how long a started agent service must keep running to count as working.</summary>
    public TimeSpan ServiceStableTime { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Push installation: interval of the service status queries.</summary>
    public TimeSpan ServicePollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>After a push installation: how long to wait for the new agent to report.</summary>
    public TimeSpan InstallWaitTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Folder with the agent builds (<c>net10</c>, <c>net48</c>); default: <c>agent</c> next to the server. A relative
    /// path is relative to the server's program folder (e.g. <c>bin/Debug/net10.0</c> in development).
    /// </summary>
    public string? PackageDirectory { get; set; }
}

/// <summary>
/// Hands commands to agents through their long poll and matches the results to the waiting caller. Lives in memory:
/// after a server restart the agents simply poll again.
/// </summary>
public sealed class AgentRegistry(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, AgentChannel> _agents = new();
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();

    /// <summary>The agent has an open poll or contacted the server within <paramref name="window"/>.</summary>
    public bool IsOnline(Guid agentId, TimeSpan window) =>
        _agents.TryGetValue(agentId, out var channel) && (channel.OpenPolls > 0 || time.GetUtcNow() - channel.LastContact < window);

    public DateTimeOffset? LastContact(Guid agentId) => _agents.TryGetValue(agentId, out var channel) ? channel.LastContact : null;

    public void Touch(Guid agentId) => Channel(agentId).LastContact = time.GetUtcNow();

    /// <summary>What the agent reported it can do with its last poll (kept in memory; agents poll every ≤ 50 s).</summary>
    public void SetCapabilities(Guid agentId, IReadOnlyCollection<string>? capabilities) =>
        Channel(agentId).Capabilities = capabilities is null ? [] : [.. capabilities];

    public bool HasCapability(Guid agentId, string capability) =>
        _agents.TryGetValue(agentId, out var channel) && channel.Capabilities.Contains(capability, StringComparer.Ordinal);

    /// <summary>
    /// Marks the agent busy for an operation (prepare … thaw): no self-update is offered meanwhile, because a restart
    /// between freeze and thaw would break the freeze.
    /// </summary>
    public IDisposable Reserve(Guid agentId)
    {
        var channel = Channel(agentId);
        Interlocked.Increment(ref channel.Operations);
        return new Reservation(channel);
    }

    /// <summary>True while an operation runs or commands wait for the agent: updates wait.</summary>
    public bool IsBusy(Guid agentId) =>
        _agents.TryGetValue(agentId, out var channel) && (Volatile.Read(ref channel.Operations) > 0 || channel.Commands.Reader.Count > 0);

    /// <summary>Waits up to <paramref name="hold"/> for the next command of the agent (the long poll).</summary>
    public async Task<AgentCommand?> NextCommandAsync(Guid agentId, TimeSpan hold, CancellationToken cancellationToken)
    {
        var channel = Channel(agentId);
        Interlocked.Increment(ref channel.OpenPollCount);
        channel.LastContact = time.GetUtcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(hold);
            while (await channel.Commands.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                if (channel.Commands.Reader.TryRead(out var command) && _pending.ContainsKey(command.Id))
                {
                    return command;
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref channel.OpenPollCount);
            channel.LastContact = time.GetUtcNow();
        }
    }

    /// <summary>Sends a command and waits for the agent's result. Throws <see cref="TimeoutException"/> if none arrives.</summary>
    public async Task<AgentCommandResult> SendAsync(Guid agentId, AgentCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = new Pending(agentId, new TaskCompletionSource<AgentCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending[command.Id] = pending;
        try
        {
            await Channel(agentId).Commands.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            return await pending.Result.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(command.Id, out _);
        }
    }

    /// <summary>A result posted by an agent; ignored if nobody waits for it (any more) or it came from another agent.</summary>
    public bool Complete(Guid agentId, AgentCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Touch(agentId);
        return _pending.TryGetValue(result.CommandId, out var pending) && pending.AgentId == agentId && pending.Result.TrySetResult(result);
    }

    private AgentChannel Channel(Guid agentId) => _agents.GetOrAdd(agentId, _ => new AgentChannel());

    private sealed record Pending(Guid AgentId, TaskCompletionSource<AgentCommandResult> Result);

    private sealed class Reservation(AgentChannel channel) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref channel.Operations);
            }
        }
    }

    private sealed class AgentChannel
    {
        public int OpenPollCount;

        public int Operations;

        public Channel<AgentCommand> Commands { get; } = System.Threading.Channels.Channel.CreateUnbounded<AgentCommand>();

        public DateTimeOffset LastContact { get; set; }

        public string[] Capabilities { get; set; } = [];

        public int OpenPolls => Volatile.Read(ref OpenPollCount);
    }
}
