using System.Diagnostics;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// Restores a whole volume as files (a Kubernetes volume into a StorageClass without snapshots): the file system of the
/// disk's image in the restore point (ext or XFS) is read on a file-level worker, and every item below its root — without
/// <c>lost+found</c>, which the new volume has of its own — goes as a stream to the mover of the new volume, the root's own
/// owner, mode and times last. What the mover could not put in place is logged; the restore goes on.
/// </summary>
public static class VolumeFileSender
{
    private const int MaxIssueLines = 50;

    public static async Task<FileRestoreResult> SendAsync(
        IRestorePointReader reader, ChunkCache cache, string diskKey, IFileLevelDiskWriter writer, IOperationLog log, IProgress<long>? processed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        var disks = (await FileRestoreMounts.OpenDisksAsync(reader, cache, cancellationToken).ConfigureAwait(false)).Where(d => d.Key == diskKey).ToList();
        if (disks.Count == 0)
        {
            throw new InvalidDataException($"The restore point has no image of the volume {diskKey}.");
        }

        // The file system is checked before the mover is started.
        await FileLevelWorkers.RunDedicated(() => _ = Volume(disks, diskKey, writer), "volume-files-check").ConfigureAwait(false);
        var channel = await writer.OpenFilesAsync(cancellationToken).ConfigureAwait(false);
        await channel.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);
        log.Info($"The mover for {writer.Description} is connected; sending the files.");

        FileRestoreResult? sent = null;
        MoverFilesTarget? target = null;
        var observer = new Observer(writer.Capacity, processed);
        try
        {
            await FileLevelWorkers.RunDedicated(
                () =>
                {
                    using var image = GuestImage.Open(disks);
                    var volume = Volume(image, diskKey, writer);
                    var fs = image.OpenFileSystem(volume.Id);
                    var entries = fs.List(GuestPath.Root)
                        .Where(e => !(e.Name == "lost+found" && e.Kind == GuestEntryKind.Directory))
                        .Select(e => GuestPath.Root.Append(e.Name))
                        .ToList();
                    target = new MoverFilesTarget(channel, [], writer.Description);
                    sent = entries.Count == 0
                        ? new FileRestoreResult(0, 0, 0, 0, 0, 0, 0)
                        : FileRestorePipeline.Run(
                            fs,
                            entries,
                            target,
                            new FileRestorePipelineOptions { Mode = FileRestoreMode.Overwrite, Describe = path => $"{writer.Description}:/{string.Join('/', path.Components)}" },
                            observer,
                            cancellationToken);
                    var root = fs.Stat(GuestPath.Root);
                    target.BaseMeta(new RestoredItemMeta(null, root.ModifiedUtc, root.AccessedUtc, null, root.Mode, root.UserId, root.GroupId));
                    target.Finish();
                },
                "volume-files").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !channel.IsUsable)
        {
            // The stream broke because the mover did (its pod failed, the connection dropped): its error is the cause.
            await channel.Completion.ConfigureAwait(false);
            throw;
        }

        log.Info($"{sent!.Files:N0} file(s) and {sent.Directories:N0} folder(s) sent to {writer.Description}; waiting for the mover to put the last ones in place.");
        var report = await writer.FinishFilesAsync(cancellationToken).ConfigureAwait(false);
        var lines = 0;
        foreach (var issue in report.Issues)
        {
            var entry = issue.Index >= 0 ? target!.Entry(issue.Index) : null;
            var where = entry is not null ? target!.Display(entry) : writer.Description;
            if (lines++ < MaxIssueLines)
            {
                log.Warning(issue.Kind switch
                {
                    MoverFileIssueKinds.Failed => $"{where} could not be restored: {issue.Text}",
                    _ => $"{where}: restored, but {issue.Text}",
                });
            }
        }

        if (lines > MaxIssueLines)
        {
            log.Warning($"{writer.Description}: {lines - MaxIssueLines:N0} more item(s) with problems (see the lines above for the kind).");
        }

        foreach (var problem in observer.Problems.Take(MaxIssueLines))
        {
            log.Warning($"{problem.Source}: {problem.Reason}");
        }

        log.Info($"{writer.Description}: {report.Files:N0} file(s) put in place ({FileRestoreMounts.Fmt(report.Bytes)}); {report.Failed + sent.Failed:N0} failed, {sent.Skipped:N0} skipped.");
        return sent with { Files = report.Files, Bytes = report.Bytes, Failed = sent.Failed + report.Failed, Incomplete = sent.Incomplete + report.Partial };
    }

    private static GuestVolume Volume(List<GuestDiskSource> disks, string diskKey, IFileLevelDiskWriter writer)
    {
        using var image = GuestImage.Open(disks);
        return Volume(image, diskKey, writer);
    }

    /// <summary>The volume's file system: the PVC's image holds it without a partition table (ext or XFS).</summary>
    private static GuestVolume Volume(GuestImage image, string diskKey, IFileLevelDiskWriter writer)
    {
        var volume = image.Volumes.FirstOrDefault(v => v.DiskKey == diskKey && v.Readable)
            ?? throw new InvalidOperationException(
                $"The image of {writer.Description} holds no file system the server can read ({string.Join("; ", image.Volumes.Select(v => $"{FileRestoreMounts.Title(v)}: {v.FileSystem ?? v.Reason}"))}); " +
                "it can go into a StorageClass with snapshots only (as a block volume).");
        return volume.FileSystem is "ext" or "XFS"
            ? volume
            : throw new InvalidOperationException($"The image of {writer.Description} is {volume.FileSystem}; volumes go in as files only from ext or XFS. Map it to a StorageClass with snapshots.");
    }

    /// <summary>Progress as a share of the volume's size (the planned bytes are the whole), and the pipeline's problems.</summary>
    private sealed class Observer(long capacity, IProgress<long>? processed) : IFileRestoreObserver
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private long _planned = 1;
        private long _copied;
        private long _reported = Stopwatch.GetTimestamp();

        public List<FileRestoreProblem> Problems { get; } = [];

        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes) => _planned = Math.Max(1, bytes);

        public void Copied(long bytes)
        {
            _copied += bytes;
            if (processed is not null && Stopwatch.GetElapsedTime(_reported) >= Interval)
            {
                _reported = Stopwatch.GetTimestamp();
                processed.Report(Math.Min(capacity, (long)(capacity * ((double)_copied / _planned))));
            }
        }

        public void FileRestored(long files)
        {
        }

        public void Renamed(string source, string target)
        {
        }

        public void Problem(FileRestoreProblem problem)
        {
            if (Problems.Count < MaxIssueLines)
            {
                Problems.Add(problem);
            }
        }
    }
}
