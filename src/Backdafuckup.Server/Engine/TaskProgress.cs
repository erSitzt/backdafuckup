using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// A session task whose progress comes from bytes done of a total (reading from tape, writing to tape, moving a chain):
/// written at most every 2 s and in order, so that the final state is never overwritten by a late update.
/// </summary>
internal sealed class TaskProgress(SessionTracker tracker, Guid sessionId, SessionTaskEntity task, TimeProvider time)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private Task _writes = Task.CompletedTask;
    private long _lastWrite;

    public bool Finished { get; private set; }

    /// <summary>Adds the task to the session and marks it running.</summary>
    public static async Task<TaskProgress> BeginAsync(SessionTracker tracker, Guid sessionId, string objectNativeId, string objectName, TimeProvider time, CancellationToken cancellationToken)
    {
        var progress = new TaskProgress(tracker, sessionId, await tracker.AddTaskAsync(sessionId, objectNativeId, objectName, cancellationToken).ConfigureAwait(false), time);
        await progress.StartAsync().ConfigureAwait(false);
        return progress;
    }

    public Task StartAsync() =>
        tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None);

    public void Report(long done, long total)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (Finished || (done < total && System.Diagnostics.Stopwatch.GetElapsedTime(_lastWrite, now) < Interval))
            {
                return;
            }

            _lastWrite = now;
            var percent = total <= 0 ? 100 : Math.Round(done * 100.0 / total, 1);
            _writes = _writes.ContinueWith(_ => WriteAsync(t => { t.Progress = percent; t.ReadBytes = done; }), TaskScheduler.Default).Unwrap();
        }
    }

    public async Task FinishAsync(SessionState state, string? error)
    {
        Task pending;
        lock (_gate)
        {
            Finished = true;
            pending = _writes;
        }

        await pending.ConfigureAwait(false);
        await WriteAsync(t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            if (state == SessionState.Succeeded)
            {
                t.Progress = 100;
            }
        }).ConfigureAwait(false);
    }

    private async Task WriteAsync(Action<SessionTaskEntity> update)
    {
        try
        {
            await tracker.UpdateTaskAsync(task.Id, update, CancellationToken.None).ConfigureAwait(false);
            await tracker.RollUpAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // Progress is informational; the session's own end state is written by the queue.
        }
    }
}
