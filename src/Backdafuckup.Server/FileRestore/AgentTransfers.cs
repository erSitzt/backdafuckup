using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Server.Agents;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// One file restore into a guest: the planned items, the security descriptors, where each file comes from in the
/// backup, and what the agent reported so far. File content is read from its own parser instance, one file at a time.
/// </summary>
public sealed class AgentTransfer : IDisposable
{
    private readonly object _contentGate = new();
    private readonly IReadOnlyList<GuestDiskSource> _disks;
    private readonly string _volume;
    private GuestImage? _contentImage;
    private int _disposed;

    internal AgentTransfer(Guid agentId, FileTransferInfo info, AgentTransferPlan plan, IReadOnlyList<GuestDiskSource> disks, string volume, IFileRestoreObserver observer, TimeProvider time)
    {
        AgentId = agentId;
        Info = info;
        Plan = plan;
        _disks = disks;
        _volume = volume;
        Observer = observer;
        Time = time;
        Hashes = new string?[plan.Items.Count];
        Written = new bool[plan.Items.Count];
        Verified = new bool[plan.Items.Count];
        LastHeartbeat = time.GetUtcNow();
    }

    public Guid Id => Info.TransferId;

    public Guid AgentId { get; }

    public FileTransferInfo Info { get; }

    internal AgentTransferPlan Plan { get; }

    internal IFileRestoreObserver Observer { get; }

    internal TimeProvider Time { get; }

    /// <summary>SHA-256 of each file as it was sent; compared with what the agent wrote.</summary>
    internal string?[] Hashes { get; }

    /// <summary>Per item: the agent reports the file as written (it exists at its final path, whatever its content).</summary>
    internal bool[] Written { get; }

    /// <summary>Per item: written, and its SHA-256 matches what was sent.</summary>
    internal bool[] Verified { get; }

    public DateTimeOffset LastHeartbeat { get; internal set; }

    public bool CancelRequested { get; internal set; }

    /// <summary>Completed with the agent's final report.</summary>
    internal TaskCompletionSource<FileTransferProgress> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal long ReportedBytes { get; set; }

    internal long RestoredFiles { get; set; }

    internal long RestoredBytes { get; set; }

    internal long Renamed { get; set; }

    internal long Failed { get; set; }

    internal long FailedDirectories { get; set; }

    internal long Incomplete { get; set; }

    internal int Done { get; set; }

    /// <summary>Opens the content of file item <paramref name="index"/> (on the caller's thread, under the content lock).</summary>
    internal T WithContent<T>(int index, Func<Stream, GuestEntry, T> read)
    {
        lock (_contentGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _contentImage ??= GuestImage.Open(_disks, describe: false);
            var source = Plan.Sources[index] ?? throw new KeyNotFoundException($"Item {index} is not a file.");
            var fs = _contentImage.OpenFileSystem(source.Volume ?? _volume);
            var entry = fs.Stat(source.Path);
            using var stream = fs.OpenRead(source.Path);
            return read(stream, entry);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_contentGate)
        {
            _contentImage?.Dispose();
        }
    }
}

/// <summary>What the server sends to the agent: items in order, their sources in the backup, and the descriptors.</summary>
internal sealed class AgentTransferPlan
{
    public List<FileTransferItem> Items { get; } = [];

    /// <summary>For file items, where the content is in the backup; null for folders and folder ends.</summary>
    public List<TransferSource?> Sources { get; } = [];

    /// <summary>How each item's source appears in the log (C:\Users\x\a.txt).</summary>
    public List<string> Descriptions { get; } = [];

    public List<byte[]> Descriptors { get; } = [];

    public long Files { get; set; }

    public long Directories { get; set; }

    public long Bytes { get; set; }

    public long Skipped { get; set; }

    /// <summary>Items that could not even be read from the backup while planning.</summary>
    public long Failed { get; set; }
}

/// <summary>A file in the backup: on the transfer's volume (null) or on the given one (e.g. a database's log on L:).</summary>
internal sealed record TransferSource(string? Volume, GuestPath Path);

/// <summary>The transfers in progress (in memory: after a server restart the agent's next request gets 404 and it stops).</summary>
public sealed class AgentTransfers(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, AgentTransfer> _transfers = new();

    internal TimeProvider Time => time;

    internal void Add(AgentTransfer transfer) => _transfers[transfer.Id] = transfer;

    internal void Remove(AgentTransfer transfer) => _transfers.TryRemove(transfer.Id, out _);

    /// <summary>The transfer if it belongs to this agent (404 otherwise, also for other agents' transfers).</summary>
    public AgentTransfer? Get(Guid transferId, Guid agentId) =>
        _transfers.TryGetValue(transferId, out var transfer) && transfer.AgentId == agentId ? transfer : null;

    public FileTransferItemsPage Page(AgentTransfer transfer, int offset, int limit)
    {
        transfer.LastHeartbeat = time.GetUtcNow();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 2000);
        var items = transfer.Plan.Items.Skip(offset).Take(limit).ToList();
        var page = new FileTransferItemsPage { Items = items, End = offset + items.Count >= transfer.Plan.Items.Count };
        foreach (var index in items.Where(i => i.Security >= 0).Select(i => i.Security).Distinct())
        {
            page.Security[index] = Convert.ToBase64String(transfer.Plan.Descriptors[index]);
        }

        return page;
    }

    /// <summary>Applies an agent report: results to the session log and counters, and the heartbeat.</summary>
    public FileTransferProgressResponse Report(AgentTransfer transfer, FileTransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        transfer.LastHeartbeat = time.GetUtcNow();
        lock (transfer)
        {
            if (progress.Bytes > transfer.ReportedBytes)
            {
                transfer.Observer.Copied(progress.Bytes - transfer.ReportedBytes);
                transfer.ReportedBytes = progress.Bytes;
            }

            transfer.Done = Math.Max(transfer.Done, progress.Done);
            foreach (var result in progress.Results ?? [])
            {
                Apply(transfer, result);
            }

            if (progress.Finished)
            {
                transfer.Finished.TrySetResult(progress);
            }
        }

        return new FileTransferProgressResponse { Cancel = transfer.CancelRequested };
    }

    /// <summary>Streams the content of a file item; the hash is kept for the comparison with the agent's report.</summary>
    public async Task StreamContentAsync(AgentTransfer transfer, int index, HttpResponse response, CancellationToken cancellationToken)
    {
        if (index < 0 || index >= transfer.Plan.Items.Count || transfer.Plan.Sources[index] is null)
        {
            throw new KeyNotFoundException($"Item {index} of the transfer is not a file.");
        }

        transfer.LastHeartbeat = time.GetUtcNow();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ready = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 << 20, resumeWriterThreshold: 2 << 20, useSynchronizationContext: false));
        var producer = FileLevelWorkers.RunDedicated(
            () =>
            {
                try
                {
                    transfer.WithContent(index, (input, entry) =>
                    {
                        ready.TrySetResult(entry.Size);
                        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        using var output = pipe.Writer.AsStream(leaveOpen: true);
                        var buffer = new byte[1 << 20];
                        int n;
                        while ((n = input.Read(buffer)) > 0)
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            sha.AppendData(buffer, 0, n);
                            output.Write(buffer, 0, n);
                        }

                        transfer.Hashes[index] = Convert.ToHexStringLower(sha.GetHashAndReset());
                        return true;
                    });
                    pipe.Writer.Complete();
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                    pipe.Writer.Complete(ex);
                    throw;
                }
            },
            "agent-transfer-content");

        long size;
        try
        {
            size = await ready.Task.ConfigureAwait(false);
        }
        catch
        {
            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await FileRestoreMounts.ObserveAsync(producer).ConfigureAwait(false);
            throw;
        }

        response.ContentType = "application/octet-stream";
        response.ContentLength = size;
        try
        {
            // While the agent downloads, the transfer is alive: one large file can take longer than the silence limit.
            while (true)
            {
                var read = await pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                foreach (var segment in read.Buffer)
                {
                    await response.Body.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                }

                pipe.Reader.AdvanceTo(read.Buffer.End);
                transfer.LastHeartbeat = time.GetUtcNow();
                if (read.IsCompleted)
                {
                    break;
                }
            }

            await pipe.Reader.CompleteAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pipe.Reader.CompleteAsync(ex).ConfigureAwait(false);
            await FileRestoreMounts.ObserveAsync(producer).ConfigureAwait(false);
            if (!response.HasStarted)
            {
                response.ContentLength = null;
                throw;
            }

            // The agent sees a short download and reports the file as failed.
            response.HttpContext.Abort();
        }
    }

    private static void Apply(AgentTransfer transfer, FileTransferResult result)
    {
        if (result.Index < 0 || result.Index >= transfer.Plan.Items.Count)
        {
            return;
        }

        var item = transfer.Plan.Items[result.Index];
        var source = transfer.Plan.Descriptions[result.Index];
        var isFile = item.Kind == FileTransferItemKinds.File;
        var problem = result.Message?.TrimEnd('.') ?? "no reason given";
        switch (result.Status)
        {
            case FileTransferStatus.Ok or FileTransferStatus.Renamed or FileTransferStatus.Incomplete when isFile:
                transfer.Written[result.Index] = true;
                if (transfer.Hashes[result.Index] is { } sent && !string.Equals(sent, result.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    transfer.Failed++;
                    transfer.Observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, source, result.FinalPath ?? item.Path,
                        $"the written file differs from the backup (SHA-256 {result.Sha256 ?? "missing"} instead of {sent}); check the file"));
                    return;
                }

                transfer.Verified[result.Index] = transfer.Hashes[result.Index] is not null;
                transfer.RestoredFiles++;
                transfer.RestoredBytes += item.Size;
                transfer.Observer.FileRestored(transfer.RestoredFiles);
                if (result.Status == FileTransferStatus.Renamed || result.FinalPath is not null)
                {
                    transfer.Renamed++;
                    transfer.Observer.Renamed(source, result.FinalPath ?? item.Path);
                }

                if (result.Status == FileTransferStatus.Incomplete)
                {
                    transfer.Incomplete++;
                    transfer.Observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Incomplete, source, result.FinalPath ?? item.Path, $"restored, but {problem}"));
                }

                break;
            case FileTransferStatus.Incomplete:
                transfer.Incomplete++;
                transfer.Observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Incomplete, source, item.Path, problem));
                break;
            default:
                transfer.Failed++;
                if (item.Kind == FileTransferItemKinds.Directory)
                {
                    transfer.FailedDirectories++;
                }

                transfer.Observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, source, item.Path, problem));
                break;
        }
    }
}
