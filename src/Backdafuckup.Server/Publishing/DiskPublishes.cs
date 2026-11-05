using System.Buffers;
using System.Collections.Concurrent;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.FileLevel;
using Backdafuckup.Server.Agents;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Publishing;

/// <summary>
/// One publish: disks of a restore point that an agent serves to its guest over a loopback iSCSI target. The agent
/// fetches the disk content block by block (<see cref="DiskPublishes.CopyBlockAsync"/>) and reports every few seconds
/// (<see cref="DiskPublishes.Report"/>); the answer tells it when to unpublish.
/// </summary>
public sealed class DiskPublish
{
    internal DiskPublish(Guid agentId, PublishInfo info, IReadOnlyList<GuestDiskSource> disks, Action<string> log, TimeProvider time, IReadOnlyDictionary<string, Sql.SqlJobFile>? files = null)
    {
        Files = files;
        AgentId = agentId;
        Info = info;
        Disks = disks;
        Log = log;
        LastHeartbeat = time.GetUtcNow();
    }

    public Guid Id => Info.PublishId;

    public Guid AgentId { get; }

    public PublishInfo Info { get; }

    /// <summary>The source of each disk in <see cref="PublishInfo.Disks"/>, same order.</summary>
    internal IReadOnlyList<GuestDiskSource> Disks { get; }

    /// <summary>Receives the agent's log lines.</summary>
    internal Action<string> Log { get; }

    /// <summary>Backup files the agent downloads for a publish at a point in time (by file id).</summary>
    internal IReadOnlyDictionary<string, Sql.SqlJobFile>? Files { get; }

    public DateTimeOffset LastHeartbeat { get; internal set; }

    public bool StopRequested { get; internal set; }

    /// <summary>The agent restarted while this ran: it removes what it set up from its journal at start, and reports nothing more.</summary>
    public bool AgentRestarted { get; internal set; }

    /// <summary>The agent's last report (state, mount folders, overlay size).</summary>
    public PublishStatus? Status { get; internal set; }

    public long ServedBytes => Interlocked.Read(ref _servedBytes);

    internal long _servedBytes;

    /// <summary>Completed when the volumes are mounted, or with the failed or ended report if that comes first.</summary>
    internal TaskCompletionSource<PublishStatus> Mounted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed with the agent's last report, when everything is removed from the guest again.</summary>
    internal TaskCompletionSource<PublishStatus> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// A publish that did not get mounted: refused, failed (<see cref="LastStatus"/> is the agent's final report), or the
/// agent stopped reporting (<see cref="TimedOut"/>; <see cref="LastStatus"/> is its last report, if any).
/// </summary>
public sealed class PublishFailedException(string message, PublishStatus? lastStatus, bool timedOut, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public PublishStatus? LastStatus { get; } = lastStatus;

    /// <summary>The agent did not end the publish with a report: what it set up may still be there.</summary>
    public bool TimedOut { get; } = timedOut;
}

/// <summary>The publishes that run now. In memory only: after a server restart the agent gets 404 and unpublishes.</summary>
public sealed class DiskPublishes(AgentRegistry registry, IOptions<AgentOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, DiskPublish> _publishes = new();

    internal TimeProvider Time => time;

    /// <summary>The agent restarted (a new run id at its poll): its publishes end at once instead of after the silence limit.</summary>
    public void AgentRestarted(Guid agentId)
    {
        foreach (var publish in _publishes.Values.Where(p => p.AgentId == agentId))
        {
            publish.AgentRestarted = true;
        }
    }

    /// <summary>
    /// Hands the disks to the agent and waits until it has mounted the volumes (or failed). On failure the publish is
    /// removed again; on success the caller ends it with <see cref="UnpublishAsync"/>.
    /// </summary>
    /// <exception cref="PublishFailedException">
    /// The agent refused the publish, could not mount it (the message says why), or did not report the mount within
    /// <paramref name="timeout"/>.
    /// </exception>
    public async Task<DiskPublish> PublishAsync(
        Guid agentId, PublishInfo info, IReadOnlyList<GuestDiskSource> disks, Action<string> log, TimeSpan timeout, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, Sql.SqlJobFile>? files = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(disks);
        if (disks.Count != info.Disks.Count)
        {
            throw new ArgumentException("Every published disk needs its source.", nameof(disks));
        }

        info.PublishId = Guid.NewGuid();
        var publish = new DiskPublish(agentId, info, disks, log, time, files);
        _publishes[publish.Id] = publish;
        var mounted = false;
        try
        {
            var accepted = await registry.SendAsync(
                agentId,
                new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.Publish, PublishId = publish.Id },
                options.Value.PrepareTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!accepted.Success)
            {
                throw new PublishFailedException(accepted.Error ?? "The agent did not accept the publish.", null, timedOut: false);
            }

            PublishStatus status;
            try
            {
                status = await WaitAsync(publish, publish.Mounted.Task, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new PublishFailedException(ex.Message, publish.Status, timedOut: true, ex);
            }

            if (status.State != PublishStates.Mounted)
            {
                throw new PublishFailedException(status.Error ?? "The agent ended the publish before its volumes were mounted.", status, timedOut: false);
            }

            mounted = true;
            return publish;
        }
        finally
        {
            if (!mounted)
            {
                // Whatever the agent set up meanwhile, it removes when its next report is answered with 404.
                publish.StopRequested = true;
                _publishes.TryRemove(publish.Id, out _);
            }
        }
    }

    /// <summary>
    /// Tells the agent to unpublish and waits until it reports that everything is removed (returns false if it does not
    /// within <paramref name="timeout"/>; it still unpublishes when its next report gets 404 or the server stays silent).
    /// </summary>
    public async Task<bool> UnpublishAsync(DiskPublish publish, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        publish.StopRequested = true;
        try
        {
            await WaitAsync(publish, publish.Ended.Task, timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _publishes.TryRemove(publish.Id, out _);
        }
    }

    /// <summary>True once the agent has sent its final report (everything removed, or what was left).</summary>
    public static bool HasEnded(DiskPublish publish) => publish.Ended.Task.IsCompleted;

    /// <summary>A running publish of this agent, or null.</summary>
    public DiskPublish? Get(Guid publishId, Guid agentId) =>
        _publishes.TryGetValue(publishId, out var publish) && publish.AgentId == agentId ? publish : null;

    /// <summary>
    /// Writes block <paramref name="block"/> of disk <paramref name="disk"/> to <paramref name="output"/> (the last block of
    /// a disk may be shorter). The restore point is read synchronously, so on a worker thread.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No such disk or block.</exception>
    public async Task CopyBlockAsync(DiskPublish publish, int disk, long block, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(output);
        if (disk < 0 || disk >= publish.Info.Disks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(disk), $"The publish has no disk {disk}.");
        }

        var size = publish.Info.Disks[disk].SizeBytes;
        var blockSize = publish.Info.BlockSize;
        var offset = block * blockSize;
        if (block < 0 || offset >= size)
        {
            throw new ArgumentOutOfRangeException(nameof(block), $"Disk {disk} has no block {block}.");
        }

        var length = (int)Math.Min(blockSize, size - offset);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await Task.Run(
                () =>
                {
                    using var stream = publish.Disks[disk].Open();
                    stream.Position = offset;
                    stream.ReadExactly(buffer, 0, length);
                },
                cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref publish._servedBytes, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Takes the agent's report; the answer says whether to unpublish.</summary>
    public PublishStatusResponse Report(DiskPublish publish, PublishStatus status)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(status);
        publish.LastHeartbeat = time.GetUtcNow();
        publish.Status = status;
        foreach (var line in status.Log)
        {
            publish.Log(line);
        }

        switch (status.State)
        {
            case PublishStates.Mounted:
                publish.Mounted.TrySetResult(status);
                break;
            case PublishStates.Failed:
            case PublishStates.Ended:
                publish.Mounted.TrySetResult(status);
                publish.Ended.TrySetResult(status);
                break;
        }

        return new PublishStatusResponse { Stop = publish.StopRequested };
    }

    /// <summary>Waits for the agent; fails early when the agent stops reporting (offline, stopped, VM off).</summary>
    private async Task<PublishStatus> WaitAsync(DiskPublish publish, Task<PublishStatus> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            var left = deadline - time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                throw new TimeoutException($"The agent did not report within {timeout.TotalMinutes:0.#} minutes.");
            }

            try
            {
                return await task.WaitAsync(left < TimeSpan.FromSeconds(15) ? left : TimeSpan.FromSeconds(15), time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) when (time.GetUtcNow() < deadline)
            {
                var silent = time.GetUtcNow() - publish.LastHeartbeat;
                if (publish.AgentRestarted)
                {
                    throw new TimeoutException("The agent restarted (service restart, crash or VM reboot).");
                }

                if (silent > PublishSilenceLimit)
                {
                    throw new TimeoutException($"The agent has not reported for {silent.TotalMinutes:0} minutes.");
                }
            }
        }
    }

    /// <summary>How long the server waits for a report before it gives a publish up (the agent's own limit is 2 minutes).</summary>
    public static readonly TimeSpan PublishSilenceLimit = TimeSpan.FromMinutes(3);
}
