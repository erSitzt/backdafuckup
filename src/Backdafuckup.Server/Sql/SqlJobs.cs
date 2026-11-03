using System.Collections.Concurrent;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Server.Agents;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// SQL Server jobs handed to agents. In memory only: a job's request carries the account's password, which therefore
/// never reaches the catalog, a session or a log. After a server restart the agent's next request gets 404.
/// </summary>
public sealed class SqlJobs(AgentRegistry registry, IOptions<AgentOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    /// <summary>Sends the job to the agent and waits up to <paramref name="timeout"/> for its result.</summary>
    public async Task<SqlJobResult> RunAsync(Guid agentId, SqlJobRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.JobId = Guid.NewGuid();
        var job = new Job(agentId, request, new TaskCompletionSource<SqlJobResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _jobs[request.JobId] = job;
        try
        {
            var accepted = await registry.SendAsync(
                agentId,
                new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.SqlJob, JobId = request.JobId },
                options.Value.PrepareTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!accepted.Success)
            {
                throw new InvalidOperationException(accepted.Error ?? "The agent did not accept the SQL job.");
            }

            return await job.Result.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _jobs.TryRemove(request.JobId, out _);
        }
    }

    /// <summary>The request of a running job, for the agent it was sent to (null otherwise).</summary>
    public SqlJobRequest? Get(Guid jobId, Guid agentId) =>
        _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId ? job.Request : null;

    public bool Complete(Guid jobId, Guid agentId, SqlJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId && job.Result.TrySetResult(result);
    }

    private sealed record Job(Guid AgentId, SqlJobRequest Request, TaskCompletionSource<SqlJobResult> Result);
}
