using System.Collections.Concurrent;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Server.Agents;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Ad;

/// <summary>
/// Active Directory jobs handed to agents on domain controllers. In memory only: a request carries the account's
/// password, which therefore never reaches the catalog, a session or a log. After a server restart the agent's next
/// request gets 404.
/// </summary>
public sealed class AdJobs(AgentRegistry registry, IOptions<AgentOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    /// <summary>Sends the job to the agent and waits up to <paramref name="timeout"/> for its result.</summary>
    public async Task<AdJobResult> RunAsync(Guid agentId, AdJobRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.JobId = Guid.NewGuid();
        var job = new Job(agentId, request, new TaskCompletionSource<AdJobResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _jobs[request.JobId] = job;
        try
        {
            var accepted = await registry.SendAsync(
                agentId,
                new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.AdJob, JobId = request.JobId },
                options.Value.PrepareTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!accepted.Success)
            {
                throw new InvalidOperationException(accepted.Error ?? "The agent did not accept the Active Directory job.");
            }

            job.RunId = registry.RunOf(agentId);
            return await job.Result.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"The agent did not finish the Active Directory job {request.Kind} within {timeout.TotalMinutes:0.#} minutes.", ex);
        }
        finally
        {
            _jobs.TryRemove(request.JobId, out _);
        }
    }

    /// <summary>The request of a running job, for the agent it was sent to (null otherwise).</summary>
    public AdJobRequest? Get(Guid jobId, Guid agentId) =>
        _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId ? job.Request : null;

    public bool Complete(Guid jobId, Guid agentId, AdJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId && job.Result.TrySetResult(result);
    }

    /// <summary>The agent restarted: the jobs its previous process accepted are gone, and so are its mounts.</summary>
    public void AgentRestarted(Guid agentId)
    {
        var current = registry.RunOf(agentId);
        foreach (var job in _jobs.Values.Where(j => j.AgentId == agentId && j.RunId != Guid.Empty && j.RunId != current))
        {
            job.Result.TrySetResult(new AdJobResult
            {
                JobId = job.Request.JobId,
                Error = $"the agent restarted (service restart, crash or VM reboot) while it ran the Active Directory job {job.Request.Kind}",
                NotMounted = true,
            });
        }
    }

    private sealed record Job(Guid AgentId, AdJobRequest Request, TaskCompletionSource<AdJobResult> Result)
    {
        public Guid RunId { get; set; }
    }
}
