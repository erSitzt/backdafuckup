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

            job.RunId = registry.RunOf(agentId);
            return await job.Result.Task.WaitAsync(timeout, time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _jobs.TryRemove(request.JobId, out _);
        }
    }

    /// <summary>
    /// Starts a job that reports progress before its result (<see cref="SqlJobKinds.SnapshotSuspend"/>): the agent posts
    /// <see cref="SqlSnapshotProgress"/>, then polls the signal, then posts the result. Dispose the handle to forget the job;
    /// an agent still polling then gets 404 and resumes without recording.
    /// </summary>
    public async Task<RunningSqlJob> StartAsync(Guid agentId, SqlJobRequest request, CancellationToken cancellationToken)
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

            job.RunId = registry.RunOf(agentId);
        }
        catch
        {
            _jobs.TryRemove(request.JobId, out _);
            throw;
        }

        return new RunningSqlJob(job, () => _jobs.TryRemove(request.JobId, out _), time);
    }

    /// <summary>The agent reports that the job's first stage is done (e.g. the databases are suspended).</summary>
    public bool Progress(Guid jobId, Guid agentId, SqlSnapshotProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId && job.Progress.TrySetResult(progress);
    }

    /// <summary>The server's current signal for a running job (null if the job is not running or not the agent's).</summary>
    public string? Signal(Guid jobId, Guid agentId) =>
        _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId ? job.Signal : null;

    /// <summary>The request of a running job, for the agent it was sent to (null otherwise).</summary>
    public SqlJobRequest? Get(Guid jobId, Guid agentId) =>
        _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId ? job.Request : null;

    /// <summary>
    /// The agent restarted (a new run id at its poll): the jobs its previous process accepted fail at once with
    /// <see cref="SqlJobLostException"/>, instead of waiting for their timeout.
    /// </summary>
    public void AgentRestarted(Guid agentId)
    {
        var current = registry.RunOf(agentId);
        foreach (var job in _jobs.Values.Where(j => j.AgentId == agentId && j.RunId != Guid.Empty && j.RunId != current))
        {
            job.Result.TrySetException(new SqlJobLostException(job.Request.Kind));
        }
    }

    public bool Complete(Guid jobId, Guid agentId, SqlJobResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return _jobs.TryGetValue(jobId, out var job) && job.AgentId == agentId && job.Result.TrySetResult(result);
    }

    internal sealed record Job(Guid AgentId, SqlJobRequest Request, TaskCompletionSource<SqlJobResult> Result)
    {
        public TaskCompletionSource<SqlSnapshotProgress> Progress { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The agent process that accepted the job (empty until accepted, or for agents before run ids).</summary>
        public Guid RunId { get; set; }

        public volatile string Signal = SqlJobSignals.Wait;
    }
}

/// <summary>The agent restarted (service restart, crash, VM reboot) while it ran the job: the job is gone.</summary>
public sealed class SqlJobLostException(string kind)
    : InvalidOperationException($"the agent restarted (service restart, crash or VM reboot) while it ran the SQL Server job {kind}")
{
    public string Kind { get; } = kind;
}

/// <summary>A two-stage SQL job in progress (see <see cref="SqlJobs.StartAsync"/>).</summary>
public sealed class RunningSqlJob : IDisposable
{
    private readonly SqlJobs.Job _job;
    private readonly Action _forget;
    private readonly TimeProvider _time;

    internal RunningSqlJob(SqlJobs.Job job, Action forget, TimeProvider time)
    {
        _job = job;
        _forget = forget;
        _time = time;
    }

    public Guid JobId => _job.Request.JobId;

    /// <summary>
    /// Waits for the progress report. Returns null with the result instead if the agent finished first (it failed before
    /// it could report progress).
    /// </summary>
    public async Task<(SqlSnapshotProgress? Progress, SqlJobResult? Result)> WaitForProgressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var first = await Task.WhenAny(_job.Progress.Task, _job.Result.Task).WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        return first == _job.Progress.Task ? (await _job.Progress.Task.ConfigureAwait(false), null) : (null, await _job.Result.Task.ConfigureAwait(false));
    }

    /// <summary>Sets the signal the agent polls (one of <see cref="SqlJobSignals"/>).</summary>
    public void SetSignal(string signal) => _job.Signal = signal;

    public Task<SqlJobResult> WaitForResultAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _job.Result.Task.WaitAsync(timeout, _time, cancellationToken);

    public void Dispose() => _forget();
}
