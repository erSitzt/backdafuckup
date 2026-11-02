using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using GuestVolume = Backdafuckup.FileLevel.GuestVolume;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Smb;
using Backdafuckup.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// Restores files and folders of a restore point to a target (session kind FileRestore). It opens its own pin and parser
/// instance, so it keeps running when the file browser it was started from closes.
/// </summary>
public sealed class FileRestoreSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    ChunkCache cache,
    SessionEventWriter events,
    SessionTracker tracker,
    CredentialProtector protector,
    ISmbShareConnector shares,
    ISshConnector ssh,
    ConnectionManager connections,
    AgentService agents,
    AgentRegistry registry,
    AgentTransfers transfers,
    IOptions<AgentOptions> agentOptions,
    IOptions<FileRestoreOptions> options,
    TimeProvider time)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<FileRestoreRequestDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The file restore session has no parameters.");
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point no longer exists.");
        }

        var task = await tracker.AddTaskAsync(session.Id, point.VmNativeId, point.VmName, cancellationToken).ConfigureAwait(false);
        var log = events.CreateLog(session.Id, task.Id, point.VmName);
        await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);
        var progress = new RestoreProgress(this, session.Id, task.Id, log);
        try
        {
            var paths = request.Paths.Select(GuestPath.Parse).ToList();
            var repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
            using var pin = repository.Pin(point.Id);
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                var pointState = await db.RestorePoints.Where(r => r.Id == point.Id).Select(r => (RestorePointState?)r.State).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (pointState != RestorePointState.Available)
                {
                    throw new InvalidOperationException($"The restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} is not available ({pointState?.ToString() ?? "deleted"}).");
                }
            }

            await using var reader = await repository.OpenRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false);
            var disks = await FileRestoreMounts.OpenDisksAsync(reader, cache, cancellationToken).ConfigureAwait(false);
            if (point.Consistency != "application")
            {
                log.Info("The restore point is crash-consistent: the newest changes before the snapshot may be missing in some files.");
            }

            var (result, targetText, targetWarnings) = request.Target.Kind switch
            {
                FileRestoreTargetKindDto.Agent => await RunAgentAsync(request, point, disks, paths, log, progress, cancellationToken).ConfigureAwait(false),
                FileRestoreTargetKindDto.Ssh => await RunSshAsync(request, point, disks, paths, log, progress, session.TriggeredBy, cancellationToken).ConfigureAwait(false),
                _ => await RunShareAsync(request, point, disks, paths, log, progress, cancellationToken).ConfigureAwait(false),
            };
            var done = result;
            var final = progress.Final(done.Files, done.Bytes);
            var summary = $"{done.Files:N0} file(s) in {done.Directories:N0} folder(s) restored, {VmBackupTask.Format(done.Bytes)}, to {targetText}";
            var issues = new List<string>();
            if (done.Failed > 0)
            {
                issues.Add($"{done.Failed:N0} failed");
            }

            if (done.Skipped > 0)
            {
                issues.Add($"{done.Skipped:N0} skipped");
            }

            if (done.Incomplete > 0)
            {
                issues.Add($"{done.Incomplete:N0} without times or attributes");
            }

            if (targetWarnings > 0)
            {
                issues.Add("written with a fallback of the target (see the warning in the log)");
            }

            if (done.Renamed > 0)
            {
                summary += $"; {done.Renamed:N0} as RESTORED- copies next to existing files";
            }

            summary += issues.Count > 0 ? $". Not restored or incomplete: {string.Join(", ", issues)} (see the log)." : ".";
            var state = issues.Count == 0 ? SessionState.Succeeded : done.Files == 0 && done.Failed > 0 ? SessionState.Failed : SessionState.Warning;
            log.Info($"File restore finished in {MetricsText.Duration(final.Seconds)} ({MetricsText.Rate(final.ProcessingRate)}): {summary}");
            await FinishAsync(task.Id, state, state == SessionState.Failed ? summary : null, done.Bytes, final).ConfigureAwait(false);
            return (state, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Warning("Stopped by user. Files restored so far stay on the target.");
            await FinishAsync(task.Id, SessionState.Stopped, "Stopped", 0, progress.Final(null, null)).ConfigureAwait(false);
            return (SessionState.Stopped, "Stopped by user.");
        }
        catch (Exception ex)
        {
            log.Error($"File restore failed: {ex.Message}");
            await FinishAsync(task.Id, SessionState.Failed, ex.Message, 0, progress.Final(null, null)).ConfigureAwait(false);
            return (SessionState.Failed, ex.Message);
        }
    }

    private TimeProvider Clock => time;

    private async Task<(FileRestoreResult Result, string Target, int Warnings)> RunShareAsync(
        FileRestoreRequestDto request, RestorePointEntity point, List<GuestDiskSource> disks, List<GuestPath> paths, IOperationLog log, RestoreProgress progress, CancellationToken cancellationToken)
    {
        var unc = UncPath.Parse(request.Target.SharePath ?? throw new ArgumentException("No share path."));
        var (user, password) = await CredentialAsync(request.Target.CredentialId, cancellationToken).ConfigureAwait(false);
        FileRestoreResult? result = null;
        var warnings = 0;
        await FileLevelWorkers.RunDedicated(
            () =>
            {
                using var image = GuestImage.Open(disks);
                var volume = ReadableVolume(image, request.Volume);
                var fs = image.OpenFileSystem(volume.Id);
                string Describe(GuestPath path) => FileRestoreMounts.Describe(volume, volume.Id, path);
                log.Info($"Restoring {Names(paths)} from {Describe(paths[0].Parent!)} (restore point of {point.CreatedUtc.ToLocalTime():g}) to {unc}. {ModeText(request.Mode)}");
                var share = shares.Connect(unc, user, password, TimeSpan.FromSeconds(30));
                foreach (var note in share.Notes)
                {
                    log.Info(note);
                }

                var notesLogged = share.Notes.Count;
                foreach (var referral in share.Referrals)
                {
                    log.Info($"DFS: {referral}.");
                }

                log.Info($@"Connected to \\{share.Location.Server}\{share.Location.Share} on {share.Endpoint} as {user}.");
                using var target = new ShareRestoreTarget(share);
                target.EnsureFolder();
                try
                {
                    result = FileRestorePipeline.Run(
                        fs,
                        paths,
                        target,
                        new FileRestorePipelineOptions { Mode = request.Mode == FileRestoreModeDto.Keep ? FileRestoreMode.Keep : FileRestoreMode.Overwrite, Describe = Describe },
                        progress,
                        cancellationToken);
                }
                finally
                {
                    foreach (var note in share.Notes.Skip(notesLogged))
                    {
                        warnings++;
                        log.Warning(note);
                    }
                }
            },
            "file-restore").ConfigureAwait(false);
        return (result!, unc.ToString(), warnings);

    }

    /// <summary>
    /// Into a Windows VM through its agent: plans the items here, then the agent fetches and writes them and reports back.
    /// The agent stays reserved (no self-update) until it has finished.
    /// </summary>
    private async Task<(FileRestoreResult Result, string Target, int Warnings)> RunAgentAsync(
        FileRestoreRequestDto request, RestorePointEntity point, List<GuestDiskSource> disks, List<GuestPath> paths, IOperationLog log, RestoreProgress progress, CancellationToken cancellationToken)
    {
        var vmId = request.Target.VmNativeId ?? point.VmNativeId;
        var agent = await agents.FindForVmAsync(vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No backdafuckup agent is installed in the VM {(vmId == point.VmNativeId ? point.VmName : vmId)}. Install it under Guest Agents, or restore to a share.");
        var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
        if (!agents.IsOnline(agent.Id))
        {
            throw new InvalidOperationException($"The agent in {host} is offline (last contact {agent.LastContactUtc.ToLocalTime():g}). Start the VM or the service {AgentProtocol.ServiceName} there.");
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilityRestoreFiles))
        {
            throw new InvalidOperationException(
                $"The agent in {host} (version {agent.AgentVersion}) cannot restore files yet. It updates itself within a few minutes after a server update; try again then.");
        }

        AgentTransferPlan? plan = null;
        string root = string.Empty;
        string targetText = string.Empty;
        await FileLevelWorkers.RunDedicated(
            () =>
            {
                using var image = GuestImage.Open(disks);
                var volume = ReadableVolume(image, request.Volume);
                if (volume.FileSystem is not ("NTFS" or "FAT"))
                {
                    throw new InvalidOperationException($"Volume {FileRestoreMounts.Title(volume)} is {volume.FileSystem}; the agent restores files of Windows volumes. Restore these files to a share.");
                }

                var fs = image.OpenFileSystem(volume.Id);
                string Describe(GuestPath path) => FileRestoreMounts.Describe(volume, volume.Id, path);
                var parent = paths[0].Parent!;
                Func<GuestPath, string> targetOf;
                if (string.IsNullOrWhiteSpace(request.Target.Folder))
                {
                    var mount = volume.MountPoint?.TrimEnd('\\');
                    if (mount is null || !AgentTargetPaths.IsLocalAbsolute(mount + "\\"))
                    {
                        throw new InvalidOperationException($"Volume {FileRestoreMounts.Title(volume)} had no drive letter in the backup, so there is no original location. Choose a folder in the VM.");
                    }

                    root = mount + "\\";
                    targetOf = path => root + string.Join('\\', path.Components);
                    targetText = $"{agent.HostName}, original location {Describe(parent)}";
                }
                else
                {
                    var folder = request.Target.Folder.Trim().TrimEnd('\\');
                    root = folder.Length == 2 ? folder + "\\" : folder;
                    targetOf = path => root.TrimEnd('\\') + "\\" + string.Join('\\', path.Components.Skip(parent.Components.Count));
                    targetText = $"{agent.HostName}, folder {root}";
                }

                log.Info(
                    $"Restoring {Names(paths)} from {Describe(parent)} (restore point of {point.CreatedUtc.ToLocalTime():g}) into {targetText} through the agent ({host}). " +
                    ModeText(request.Mode) + (request.Target.RestorePermissions ? " Owner and permissions come from the backup." : " Permissions are inherited from the target folder."));
                plan = AgentTargetPaths.Plan(fs, paths, targetOf, request.Target.RestorePermissions, Describe, progress, options.Value.MaxAgentTransferItems, cancellationToken);
            },
            "file-restore-plan").ConfigureAwait(false);

        var planned = plan!;
        progress.Planned(planned.Files, planned.Directories, planned.Bytes);
        var info = new FileTransferInfo
        {
            TransferId = Guid.NewGuid(),
            Roots = [root],
            Overwrite = request.Mode != FileRestoreModeDto.Keep,
            RestorePermissions = request.Target.RestorePermissions,
            ItemCount = planned.Items.Count,
            TotalBytes = planned.Bytes,
        };
        using var transfer = new AgentTransfer(agent.Id, info, planned, disks, request.Volume, progress, time);
        using var reservation = registry.Reserve(agent.Id);
        transfers.Add(transfer);
        try
        {
            var accepted = await registry.SendAsync(
                agent.Id,
                new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.RestoreFiles, TransferId = info.TransferId },
                agentOptions.Value.PrepareTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!accepted.Success)
            {
                throw new InvalidOperationException($"The agent in {host} did not start the restore: {accepted.Error}");
            }

            var report = await WaitForAgentAsync(transfer, host, log, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var unprocessed = 0L;
            if (report is null || report.Error is not null || transfer.Done < planned.Items.Count)
            {
                unprocessed = planned.Items.Skip(transfer.Done).Count(i => i.Kind == FileTransferItemKinds.File);
                log.Error($"The agent in {host} stopped after {transfer.Done:N0} of {planned.Items.Count:N0} item(s): {report?.Error ?? "no final report"}. {unprocessed:N0} file(s) were not restored.");
            }

            return (new FileRestoreResult(
                transfer.RestoredFiles,
                Math.Max(0, planned.Directories - transfer.FailedDirectories),
                transfer.RestoredBytes,
                transfer.Renamed,
                transfer.Failed + planned.Failed + unprocessed,
                planned.Skipped,
                transfer.Incomplete), targetText, 0);
        }
        finally
        {
            transfers.Remove(transfer);
        }
    }

    /// <summary>
    /// Into a Linux VM over SSH: uploads the selection into a staging folder (the pipeline), then the restore script
    /// puts it in place as root. Per-item results of the script adjust the counts of the upload.
    /// </summary>
    private async Task<(FileRestoreResult Result, string Target, int Warnings)> RunSshAsync(
        FileRestoreRequestDto request, RestorePointEntity point, List<GuestDiskSource> disks, List<GuestPath> paths, IOperationLog log, RestoreProgress progress, string user, CancellationToken cancellationToken)
    {
        var (userName, secret) = await LinuxLoginAsync(request.Target.CredentialId, cancellationToken).ConfigureAwait(false);
        var login = new SshLogin(userName, secret.Password, secret.PrivateKey, secret.Passphrase, secret.SudoPassword);
        var port = request.Target.Port ?? 22;
        IReadOnlyList<string> addresses = !string.IsNullOrWhiteSpace(request.Target.Address) ? [request.Target.Address.Trim()] : await GuestAddressesAsync(point, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException($"The guest tools of {point.VmName} report no IP address (VM off, or no guest tools). Enter the address of the VM.");
        }

        FileRestoreResult? result = null;
        string targetText = string.Empty;
        await FileLevelWorkers.RunDedicated(
            () =>
            {
                using var image = GuestImage.Open(disks);
                var volume = ReadableVolume(image, request.Volume);
                if (volume.FileSystem is not ("ext" or "XFS"))
                {
                    throw new InvalidOperationException($"Volume {FileRestoreMounts.Title(volume)} is {volume.FileSystem}; SSH restores files of Linux volumes. Restore Windows files through the agent.");
                }

                var fs = image.OpenFileSystem(volume.Id);
                string Describe(GuestPath path) => FileRestoreMounts.Describe(volume, volume.Id, path);
                var parent = paths[0].Parent!;
                string scriptRoot;
                string baseFolder;
                if (string.IsNullOrWhiteSpace(request.Target.Folder))
                {
                    var mount = volume.MountPoint;
                    if (mount is null || !mount.StartsWith('/'))
                    {
                        throw new InvalidOperationException($"Volume {FileRestoreMounts.Title(volume)} had no mount point in the backup's /etc/fstab, so there is no original location. Choose a folder in the VM.");
                    }

                    scriptRoot = mount;
                    baseFolder = mount.TrimEnd('/') + parent;
                    targetText = $"{point.VmName}, original location {Describe(parent)}";
                }
                else
                {
                    scriptRoot = SshTargetPaths.Normalize(request.Target.Folder) ?? throw new ArgumentException($"'{request.Target.Folder}' is not an absolute folder in the VM.");
                    baseFolder = scriptRoot;
                    targetText = $"{point.VmName}, folder {scriptRoot}";
                }

                var errors = new List<string>();
                ISshSession? connected = null;
                foreach (var address in addresses)
                {
                    try
                    {
                        connected = ssh.Connect(address, port, login, new DbHostKeyStore(dbFactory, time, user, note => log.Info(note)), TimeSpan.FromSeconds(30));
                        break;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // The same account fails on every address.
                        throw;
                    }
                    catch (IOException ex)
                    {
                        errors.Add(ex.Message);
                    }
                }

                using var sshSession = connected ?? throw new IOException(string.Join(" ", errors));
                log.Info($"Restoring {Names(paths)} from {Describe(parent)} (restore point of {point.CreatedUtc.ToLocalTime():g}) into {targetText} over SSH. " +
                    ModeText(request.Mode) + (request.Target.RestorePermissions ? " Owner and group come from the backup." : " Owner and group are those of the restore folder."));
                log.Info($"Connected to {sshSession.Endpoint} as {userName}" + (sshSession.IsRoot ? "." : "; the files are put in place as root through sudo."));
                var stage = sshSession.CreateStaging();
                try
                {
                    var target = new SshStagingTarget(sshSession, stage, baseFolder, point.VmName);
                    var checking = new SpaceCheck(progress, bytes =>
                    {
                        if (sshSession.FreeBytes(stage) is { } free && bytes > free)
                        {
                            throw new IOException($"The selection needs {VmBackupTask.Format(bytes)} in /var/tmp on {sshSession.Endpoint}, but only {VmBackupTask.Format(free)} are free.");
                        }
                    });
                    var uploaded = FileRestorePipeline.Run(
                        fs,
                        paths,
                        target,
                        new FileRestorePipelineOptions { Mode = request.Mode == FileRestoreModeDto.Keep ? FileRestoreMode.Keep : FileRestoreMode.Overwrite, Describe = Describe },
                        checking,
                        cancellationToken);
                    log.Info($"{uploaded.Files:N0} file(s) uploaded to {stage}; putting them in place as root.");
                    var applied = target.Apply(request.Mode == FileRestoreModeDto.Keep, request.Target.RestorePermissions, scriptRoot, cancellationToken);
                    foreach (var (entry, reason) in applied.Failed)
                    {
                        progress.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, target.Display(entry), null, reason));
                    }

                    foreach (var (entry, reason) in applied.Partial)
                    {
                        progress.Problem(new FileRestoreProblem(FileRestoreProblemKind.Incomplete, target.Display(entry), null, $"restored, but {reason}"));
                    }

                    foreach (var (entry, newPath) in applied.Renamed)
                    {
                        progress.Renamed(target.Display(entry), newPath);
                    }

                    result = uploaded with
                    {
                        Files = applied.Restored.Count,
                        Bytes = applied.Restored.Sum(e => e.Size),
                        Directories = Math.Max(0, uploaded.Directories - applied.Failed.Count(f => f.Entry.Kind == 'D')),
                        Renamed = applied.Renamed.Count,
                        Failed = uploaded.Failed + applied.Failed.Count,
                        Incomplete = uploaded.Incomplete + applied.Partial.Count,
                    };
                }
                finally
                {
                    sshSession.RemoveStaging(stage);
                }
            },
            "file-restore-ssh").ConfigureAwait(false);
        return (result!, targetText, 0);
    }

    private async Task<(string User, LinuxSecret Secret)> LinuxLoginAsync(Guid? credentialId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.Kind == CredentialKind.Linux, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Linux credential no longer exists. Choose another one under Credentials.");
        return (credential.Username, Api.LinuxCredentialEndpoints.Unprotect(protector, credential));
    }

    private async Task<IReadOnlyList<string>> GuestAddressesAsync(RestorePointEntity point, CancellationToken cancellationToken)
    {
        if (point.ConnectionId is not { } connectionId)
        {
            return [];
        }

        await using var hypervisor = await connections.OpenAsync(connectionId, cancellationToken).ConfigureAwait(false);
        return await hypervisor.GetGuestAddressesAsync(point.VmNativeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forwards everything; checks the free space once the selection is counted.</summary>
    private sealed class SpaceCheck(IFileRestoreObserver inner, Action<long> check) : IFileRestoreObserver
    {
        public void Counting(long files, long bytes) => inner.Counting(files, bytes);

        public void Planned(long files, long directories, long bytes)
        {
            inner.Planned(files, directories, bytes);
            check(bytes);
        }

        public void Copied(long bytes) => inner.Copied(bytes);

        public void FileRestored(long files) => inner.FileRestored(files);

        public void Renamed(string source, string target) => inner.Renamed(source, target);

        public void Problem(FileRestoreProblem problem) => inner.Problem(problem);
    }

    /// <summary>
    /// Waits for the agent's final report. Stop asks the agent to finish the current file; silence for longer than
    /// <see cref="AgentOptions.TransferSilenceLimit"/> fails the restore (VM down, network, agent stopped).
    /// </summary>
    private async Task<FileTransferProgress?> WaitForAgentAsync(AgentTransfer transfer, string host, IOperationLog log, CancellationToken cancellationToken)
    {
        DateTimeOffset? stopDeadline = null;
        while (true)
        {
            var finished = await Task.WhenAny(transfer.Finished.Task, Task.Delay(TimeSpan.FromSeconds(2), time, CancellationToken.None)).ConfigureAwait(false);
            if (finished == transfer.Finished.Task)
            {
                return await transfer.Finished.Task.ConfigureAwait(false);
            }

            var now = time.GetUtcNow();
            if (cancellationToken.IsCancellationRequested && !transfer.CancelRequested)
            {
                transfer.CancelRequested = true;
                stopDeadline = now + TimeSpan.FromMinutes(2);
                log.Info($"Stopping: the agent in {host} finishes the current file and stops.");
            }

            if (stopDeadline is { } deadline && now > deadline)
            {
                return null;
            }

            var silence = now - transfer.LastHeartbeat;
            if (silence > agentOptions.Value.TransferSilenceLimit)
            {
                throw new TimeoutException(
                    $"The agent in {host} has not reported for {silence.TotalMinutes:0} minutes (VM stopped, network down or agent restarted). Files restored so far stay in the VM.");
            }
        }
    }

    private static GuestVolume ReadableVolume(GuestImage image, string volumeId)
    {
        var volume = image.Volumes.FirstOrDefault(v => v.Id == volumeId)
            ?? throw new KeyNotFoundException($"The restore point has no volume '{volumeId}'.");
        return volume.Readable ? volume : throw new InvalidOperationException($"Volume {FileRestoreMounts.Title(volume)} cannot be read: {volume.Reason}.");
    }

    private static string Names(List<GuestPath> paths) =>
        string.Join(", ", paths.Take(5).Select(p => p.Name)) + (paths.Count > 5 ? $" and {paths.Count - 5:N0} more" : string.Empty);

    private static string ModeText(FileRestoreModeDto mode) =>
        mode == FileRestoreModeDto.Overwrite ? "Existing files are replaced." : "Existing files stay; restored ones get the RESTORED- prefix.";

    private async Task<(string User, string Password)> CredentialAsync(Guid? credentialId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.Kind == CredentialKind.Windows, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Windows credential for the share no longer exists. Choose another one under Windows Credentials.");
        return (credential.Username, protector.Unprotect(credential.ProtectedSecret));
    }

    private Task FinishAsync(Guid taskId, SessionState state, string? error, long bytes, TransferMetricsDto metrics) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            t.Metrics = metrics;
            t.TransferredBytes = Math.Max(t.TransferredBytes, bytes);
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                t.Progress = 100;
            }
        }, CancellationToken.None);

    private async Task PushAsync(Guid sessionId, Guid taskId, double percent, long bytes, TransferMetricsDto metrics)
    {
        try
        {
            await tracker.UpdateTaskAsync(taskId, t => { t.Progress = percent; t.TransferredBytes = bytes; t.Metrics = metrics; }).ConfigureAwait(false);
            await tracker.RollUpAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
        {
            // Progress is informational; the final state is written when the restore ends.
        }
    }

    /// <summary>Turns pipeline events into log lines (with limits) and throttled progress updates.</summary>
    private sealed class RestoreProgress(FileRestoreSessionRunner runner, Guid sessionId, Guid taskId, IOperationLog log) : IFileRestoreObserver
    {
        private const int MaxProblemLines = 500;
        private const int MaxRenamedLines = 200;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private readonly RateMeter _rate = new();
        private readonly DateTimeOffset _started = runner.Clock.GetUtcNow();
        private DateTimeOffset _lastPush = DateTimeOffset.MinValue;
        private long _plannedFiles;
        private long _plannedBytes;
        private long _copied;
        private long _files;
        private int _problemLines;
        private int _renamedLines;

        public void Counting(long files, long bytes)
        {
            if (Due())
            {
                log.Info($"Counting the selection: {files:N0} file(s), {VmBackupTask.Format(bytes)} so far…", "count");
            }
        }

        public void Planned(long files, long directories, long bytes)
        {
            _plannedFiles = files;
            _plannedBytes = bytes;
            log.Info($"Selection: {files:N0} file(s) in {directories:N0} folder(s), {VmBackupTask.Format(bytes)}.", "count");
        }

        public void Copied(long bytes)
        {
            _copied += bytes;
            Push(force: false);
        }

        public void FileRestored(long files)
        {
            _files = files;
            Push(force: false);
        }

        public void Renamed(string source, string target)
        {
            if (++_renamedLines <= MaxRenamedLines)
            {
                log.Info($"{source} exists on the target; restored as {target}.");
            }
            else if (_renamedLines == MaxRenamedLines + 1)
            {
                log.Info("Further files restored as RESTORED- copies are not listed.");
            }
        }

        public void Problem(FileRestoreProblem problem)
        {
            if (++_problemLines > MaxProblemLines)
            {
                if (_problemLines == MaxProblemLines + 1)
                {
                    log.Warning("Further problems are not listed one by one; the summary counts them.");
                }

                return;
            }

            var reason = problem.Reason.TrimEnd('.');
            log.Warning(problem.Kind switch
            {
                FileRestoreProblemKind.Skipped => $"{problem.Source} skipped: {reason}.",
                _ when problem.Target is { } target => $"{problem.Source} → {target}: {reason}.",
                _ => $"{problem.Source}: {reason}.",
            });
        }

        /// <summary>The last progress line and metrics; with the counts of the result, failed files no longer count.</summary>
        public TransferMetricsDto Final(long? files, long? bytes)
        {
            _files = files ?? _files;
            _copied = bytes ?? _copied;
            var metrics = Metrics(runner.Clock.GetUtcNow()) with { CurrentBytesPerSecond = 0 };
            if (_plannedFiles > 0 || _files > 0)
            {
                log.Info(ProgressText(), "progress");
            }
            return metrics;
        }

        private void Push(bool force)
        {
            var now = runner.Clock.GetUtcNow();
            if (!force && now - _lastPush < Interval)
            {
                return;
            }

            _lastPush = now;
            _rate.Sample(now, _copied);
            log.Info(ProgressText(), "progress");
            var percent = _plannedBytes > 0 ? Math.Round(Math.Min(100, _copied * 100.0 / _plannedBytes), 1)
                : _plannedFiles > 0 ? Math.Round(Math.Min(100, _files * 100.0 / _plannedFiles), 1) : 0;
            _ = runner.PushAsync(sessionId, taskId, percent, _copied, Metrics(now));
        }

        private bool Due()
        {
            var now = runner.Clock.GetUtcNow();
            if (now - _lastPush < Interval)
            {
                return false;
            }

            _lastPush = now;
            return true;
        }

        private string ProgressText() =>
            $"Restored {_files:N0} of {_plannedFiles:N0} file(s), {VmBackupTask.Format(_copied)} of {VmBackupTask.Format(_plannedBytes)}.";

        private TransferMetricsDto Metrics(DateTimeOffset now) => new()
        {
            TotalBytes = _plannedBytes,
            ProcessedBytes = _copied,
            ReadBytes = _copied,
            StoredBytes = _copied,
            Seconds = Math.Round((now - _started).TotalSeconds, 3),
            CurrentBytesPerSecond = _rate.Current,
        };
    }
}
