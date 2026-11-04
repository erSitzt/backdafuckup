using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.FileRestore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Restores a SQL Server database of a restore point as a new database (session kind SqlRestore): checks the target
/// instance with the chosen account, writes the files through the agent (named after the new database, never over an
/// existing file, each checked with SHA-256), then the agent gives SQL Server access to them and attaches them. Files
/// of a failed or stopped restore are deleted again; the backup's database is never touched.
/// <para>
/// With <see cref="SqlRestoreRequestDto.Overwrite"/> it overwrites the original database instead: the files are written
/// beside the database's own as <c>*.bdfu-new</c>, then the agent swaps them in while the database is offline and brings
/// it online (<see cref="SqlJobKinds.ReplaceFiles"/>); if that fails, the database gets its own files back.
/// </para>
/// </summary>
public sealed class SqlRestoreSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    ChunkCache cache,
    SessionEventWriter events,
    SessionTracker tracker,
    CredentialProtector protector,
    AgentService agents,
    AgentRegistry registry,
    AgentTransfers transfers,
    SqlJobs jobs,
    IOptions<AgentOptions> agentOptions,
    IOptions<SqlRestoreOptions> options,
    TimeProvider time)
{
    /// <summary>Kept free on a target volume beside the restored files.</summary>
    private const long FreeSpaceReserve = 512L << 20;

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<SqlRestoreRequestDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The restore session has no parameters.");
        RestorePointEntity point;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point no longer exists.");
        }

        var source = $"{request.Instance}\\{request.Database}";
        var task = await tracker.AddTaskAsync(session.Id, point.VmNativeId, source, cancellationToken).ConfigureAwait(false);
        var log = events.CreateLog(session.Id, task.Id, point.VmName);
        await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);
        try
        {
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
            var sources = await FileRestoreMounts.OpenDisksAsync(reader, cache, cancellationToken).ConfigureAwait(false);
            var stored = await StoredGuestMetadata.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            SqlMountDto? described = null;
            SqlDatabaseDto? found = null;
            await FileLevelWorkers.RunDedicated(
                () =>
                {
                    using var image = GuestImage.Open(sources);
                    described = SqlCatalog.Describe(image, stored);
                    found = SqlCatalog.Restorable(described, request.Instance, request.Database, point.VmName, point.CreatedUtc, "restored");
                },
                "sql-restore-describe").ConfigureAwait(false);
            var database = found!;

            // The target: an agent that writes files and attaches them, the account, and what the instance says.
            var agent = await SqlEndpoints.SqlAgentAsync(request.TargetVmNativeId, agents, registry, cancellationToken).ConfigureAwait(false);
            var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlAttachFiles) || !registry.HasCapability(agent.Id, AgentProtocol.CapabilityRestoreFilesV2))
            {
                throw new InvalidOperationException($"The agent in {host} (version {agent.AgentVersion}) cannot restore SQL Server databases yet. It updates itself within a few minutes after a server update; try again then.");
            }

            var account = await SqlEndpoints.AccountAsync(request.CredentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Choose the account that attaches the database.");
            var inventory = await jobs.RunAsync(
                agent.Id, new SqlJobRequest { Kind = SqlJobKinds.Inventory, Instance = request.TargetInstance, Account = account }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (!inventory.Success || inventory.Inventory is null)
            {
                throw new InvalidOperationException($"The agent in {host} could not check SQL Server: {inventory.Error ?? "no answer"}");
            }

            var instance = inventory.Inventory.Instances.FirstOrDefault(i => string.Equals(i.Name, request.TargetInstance, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{host} has no SQL Server instance {request.TargetInstance}.");
            var target = $"{host}, instance {instance.Name}";
            if (request.Overwrite && !registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlReplaceFiles))
            {
                throw new InvalidOperationException($"The agent in {host} (version {agent.AgentVersion}) cannot overwrite SQL Server databases yet. It updates itself within a few minutes after a server update; try again then.");
            }

            // Primary data file first (it names the database's files), then the other data files, then the logs.
            var files = database.Files.OrderByDescending(f => f.Primary).ThenBy(f => f.Kind == "log" ? 1 : 0).ToList();
            IReadOnlyList<string> targets;
            try
            {
                // Overwrite: the files go back where they are (the server checks that the database has exactly these).
                targets = request.Overwrite
                    ? [.. files.Select(f => f.Path)]
                    : SqlRestoreFiles.TargetPaths(
                        request.DatabaseName, [.. files.Select(f => (f.Path, f.Kind))], request.FileLocation, request.DataFolder, request.LogFolder, instance.DefaultDataFolder, instance.DefaultLogFolder);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{ex.Message} ({target})", ex);
            }

            Check(described!, database, request, instance, inventory.Inventory.Volumes, files, targets, account, target, log);

            // What the agent writes: the target paths, or for an overwrite the copies beside the database's own files.
            IReadOnlyList<string> written = request.Overwrite ? [.. targets.Select(t => t + SqlReplaceNames.NewSuffix)] : targets;
            log.Info(request.Overwrite
                ? $"Overwriting SQL Server database {SqlCatalog.QuoteName(request.DatabaseName)} on {target} with {source} ({files.Count} file(s), {VmBackupTask.Format(database.TotalBytes)}, " +
                  $"{database.Consistency}) from the restore point of {point.CreatedUtc.ToLocalTime():g}, as {account.UserName}. The files are written beside the database's own " +
                  $"(*{SqlReplaceNames.NewSuffix}); then it goes offline (open transactions are rolled back), gets the restored files and goes online again. Owner, users and " +
                  $"settings stay. The previous files are {(request.KeepReplacedFiles ? $"kept as *{SqlReplaceNames.ReplacedSuffix}" : "deleted once it is online")}: {string.Join(", ", targets)}."
                : $"Restoring SQL Server database {source} ({files.Count} file(s), {VmBackupTask.Format(database.TotalBytes)}, {database.Consistency}) from the restore point of " +
                  $"{point.CreatedUtc.ToLocalTime():g} to {target} as {SqlCatalog.QuoteName(request.DatabaseName)}, attached as {account.UserName}: " +
                  string.Join(", ", files.Select((f, i) => $"{f.Path} → {targets[i]}")) + ". Existing files are never replaced.");
            if (database.Consistency != "application")
            {
                log.Info("The database is crash-consistent in this restore point; SQL Server recovers it when it is attached, like after a power failure.");
            }

            AgentTransferPlan? plan = null;
            await FileLevelWorkers.RunDedicated(
                () =>
                {
                    using var image = GuestImage.Open(sources, describe: false);
                    plan = AgentFilePlan.Plan([.. files.Select((f, i) => (f.Volume!, image.OpenFileSystem(f.Volume!), GuestPath.Parse(f.GuestPath!), written[i], f.Path))]);
                },
                "sql-restore-plan").ConfigureAwait(false);

            var info = new FileTransferInfo
            {
                TransferId = Guid.NewGuid(),
                Roots = [.. written.Select(t => t[..t.LastIndexOf('\\')]).Distinct(StringComparer.OrdinalIgnoreCase).Select(r => r.Length == 2 ? r + "\\" : r)],
                Overwrite = false,
                IfExists = FileTransferIfExists.Fail,
                RestorePermissions = false,
                ItemCount = plan!.Items.Count,
                TotalBytes = plan.Bytes,
            };
            var progress = new Progress(tracker, task.Id, plan.Bytes, log, time);
            using var reservation = await FileRestoreSessionRunner.ReserveAgentAsync(
                registry, agent.Id, host, log, agentOptions.Value.WorkWaitLimit, time, cancellationToken).ConfigureAwait(false);
            using var transfer = new AgentTransfer(agent.Id, info, plan, sources, files[0].Volume!, progress, time);
            FileTransferProgress? report;
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
                    throw new InvalidOperationException($"The agent in {host} did not start writing the files: {accepted.Error}");
                }

                report = await WaitForAgentAsync(transfer, host, log, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                transfers.Remove(transfer);
            }

            var writtenSoFar = written.Where((_, i) => transfer.Written[i]).ToList();
            var verified = transfer.Verified.All(v => v);
            if (cancellationToken.IsCancellationRequested || !verified)
            {
                var removed = await DeleteAsync(agent, account, writtenSoFar, log).ConfigureAwait(false);
                var outcome = removed ? "the files written so far were deleted again" : "not all files written so far could be deleted (see the log)";
                if (cancellationToken.IsCancellationRequested)
                {
                    log.Warning($"Stopped before the database was attached; {outcome}.");
                    await FinishTaskAsync(task.Id, SessionState.Stopped, "Stopped").ConfigureAwait(false);
                    return (SessionState.Stopped, $"Stopped before the database was attached; {outcome}.");
                }

                var missing = written.Where((_, i) => !transfer.Verified[i]).ToList();
                throw new InvalidOperationException(
                    $"Writing the files to {target} failed ({report?.Error ?? $"{missing.Count} of {targets.Count} file(s) not written or not as in the backup: {string.Join(", ", missing)}; see the log"}); {outcome}.");
            }

            if (request.Overwrite)
            {
                return await OverwriteAsync(request, agent, account, instance, target, source, point, targets, written, plan.Bytes, task.Id, log).ConfigureAwait(false);
            }

            log.Info($"All {targets.Count} file(s) written and checked (SHA-256, {VmBackupTask.Format(plan.Bytes)}); attaching them. This cannot be stopped halfway.");
            var attach = await jobs.RunAsync(
                agent.Id,
                new SqlJobRequest { Kind = SqlJobKinds.AttachFiles, Instance = instance.Name, Account = account, Database = request.DatabaseName, Files = [.. targets] },
                options.Value.AttachTimeout,
                CancellationToken.None).ConfigureAwait(false);
            foreach (var line in attach.Log)
            {
                log.Write(line.StartsWith("users in ", StringComparison.Ordinal) ? LogSeverity.Warning : LogSeverity.Info, line);
            }

            if (!attach.Success)
            {
                throw new InvalidOperationException($"{host}: {attach.Error ?? "the agent did not report the attach."} The restored files were deleted again.");
            }

            var folders = string.Join(", ", targets.Select(t => t[..t.LastIndexOf('\\')]).Distinct(StringComparer.OrdinalIgnoreCase));
            var summary = $"Restored {source} from the restore point of {point.CreatedUtc.ToLocalTime():g} as {SqlCatalog.QuoteName(request.DatabaseName)} " +
                $"on {target}: {targets.Count} file(s), {VmBackupTask.Format(plan.Bytes)}, in {folders}.";
            log.Info(summary);
            await FinishTaskAsync(task.Id, SessionState.Succeeded, null, plan.Bytes).ConfigureAwait(false);
            return (SessionState.Succeeded, summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Warning("Stopped before any file was written.");
            await FinishTaskAsync(task.Id, SessionState.Stopped, "Stopped").ConfigureAwait(false);
            return (SessionState.Stopped, "Stopped before any file was written.");
        }
        catch (Exception ex)
        {
            log.Error(ex.Message);
            await FinishTaskAsync(task.Id, SessionState.Failed, ex.Message).ConfigureAwait(false);
            return (SessionState.Failed, ex.Message);
        }
    }

    /// <summary>
    /// The restored files lie beside the database's own: the agent swaps them in while the database is offline (it rolls
    /// back on failure). Not stoppable: it ends with the database online, either with the restored or with its own files.
    /// </summary>
    private async Task<(SessionState State, string Summary)> OverwriteAsync(
        SqlRestoreRequestDto request, AgentEntity agent, SqlAccount account, SqlInstanceInfo instance, string target, string source, RestorePointEntity point,
        IReadOnlyList<string> targets, IReadOnlyList<string> written, long bytes, Guid taskId, IOperationLog log)
    {
        log.Info($"All {targets.Count} file(s) written beside the database's own and checked (SHA-256, {VmBackupTask.Format(bytes)}); overwriting the database. This cannot be stopped halfway.");
        var replace = await jobs.RunAsync(
            agent.Id,
            new SqlJobRequest
            {
                Kind = SqlJobKinds.ReplaceFiles, Instance = instance.Name, Account = account, Database = request.DatabaseName, Files = [.. targets], KeepReplaced = request.KeepReplacedFiles,
            },
            options.Value.AttachTimeout,
            CancellationToken.None).ConfigureAwait(false);
        foreach (var line in replace.Log)
        {
            log.Write(line.Contains("failed", StringComparison.Ordinal) || line.Contains("by hand", StringComparison.Ordinal) ? LogSeverity.Warning : LogSeverity.Info, line);
        }

        if (!replace.Success)
        {
            // The agent put the database's own files back; the restored copies are *.bdfu-new again.
            var removed = await DeleteAsync(agent, account, [.. written], log).ConfigureAwait(false);
            throw new InvalidOperationException($"{target}: {replace.Error ?? "the agent did not report the overwrite."}" +
                (removed ? " The restored files were deleted again." : " Not all restored files could be deleted (see the log)."));
        }

        var summary = $"Overwrote {SqlCatalog.QuoteName(request.DatabaseName)} on {target} with {source} from the restore point of {point.CreatedUtc.ToLocalTime():g}: " +
            $"{targets.Count} file(s), {VmBackupTask.Format(bytes)}; the previous files were {(request.KeepReplacedFiles ? $"kept as *{SqlReplaceNames.ReplacedSuffix}" : "deleted")}.";
        log.Info(summary);
        await FinishTaskAsync(taskId, SessionState.Succeeded, null, bytes).ConfigureAwait(false);
        return (SessionState.Succeeded, summary);
    }

    /// <summary>
    /// What speaks against the restore, all at once: no connection, missing rights, the name taken, an older target
    /// version, the Express size limit, not enough free space on a target volume. For an overwrite: the database must
    /// exist with exactly the backup's files, and the account must be sysadmin.
    /// </summary>
    private static void Check(
        SqlMountDto described, SqlDatabaseDto database, SqlRestoreRequestDto request, SqlInstanceInfo instance, IReadOnlyList<SqlVolumeInfo> volumes,
        IReadOnlyList<SqlFileDto> files, IReadOnlyList<string> targets, SqlAccount account, string target, IOperationLog log)
    {
        if (!instance.Connected)
        {
            throw new InvalidOperationException($"{account.UserName} cannot connect to {target}: {instance.ConnectError ?? "unknown error"}");
        }

        var problems = new List<string>();
        var existing = instance.Databases.FirstOrDefault(d => string.Equals(d.Name, request.DatabaseName, StringComparison.OrdinalIgnoreCase));
        if (request.Overwrite)
        {
            if (instance.IsSysadmin != true)
            {
                problems.Add($"{instance.LoginName ?? account.UserName} is not sysadmin there; overwriting a database (offline, files, online) needs the server role sysadmin");
            }

            if (existing is null)
            {
                problems.Add($"the instance has no database {SqlCatalog.QuoteName(request.DatabaseName)} to overwrite; restore it as a new database instead");
            }
            else if (existing.Files.Count > 0)
            {
                var now = existing.Files.Select(f => f.PhysicalName).ToList();
                if (now.Count != targets.Count || now.Except(targets, StringComparer.OrdinalIgnoreCase).Any())
                {
                    problems.Add($"the database's files are not the ones in the backup (now: {string.Join(", ", now)}; backup: {string.Join(", ", targets)}); restore it as a new database instead");
                }
            }
        }
        else
        {
            if (instance.IsSysadmin != true && instance.IsDbCreator != true)
            {
                problems.Add($"{instance.LoginName ?? account.UserName} may not create databases there; it needs the server role dbcreator or sysadmin");
            }

            if (existing is not null)
            {
                problems.Add($"the instance has a database {SqlCatalog.QuoteName(request.DatabaseName)} already; choose another name");
            }
        }

        var origin = described.Instances.FirstOrDefault(i => string.Equals(i.Name, database.Instance, StringComparison.OrdinalIgnoreCase));
        var sourceMajor = Major(origin?.Version);
        var targetMajor = Major(instance.ProductVersion ?? instance.SetupVersion);
        if (sourceMajor is { } from && targetMajor is { } to)
        {
            if (to < from)
            {
                problems.Add($"the database comes from {origin?.Product ?? "a newer SQL Server"} and the instance is {SqlCatalog.Product(instance.ProductVersion) ?? "older"}; SQL Server attaches databases only on the same or a newer version");
            }
            else if (to > from)
            {
                log.Info($"{SqlCatalog.Product(instance.ProductVersion)} upgrades the database from {origin?.Product} when it attaches it; it cannot be attached to the older version afterwards.");
            }
        }

        var dataBytes = files.Where(f => f.Kind == "data").Sum(f => f.SizeBytes ?? 0);
        var limit = (targetMajor ?? 0) >= 17 ? 50L << 30 : 10L << 30;
        if (instance.EngineEdition == 4 && dataBytes > limit)
        {
            problems.Add($"the instance is SQL Server Express (databases up to {VmBackupTask.Format(limit)}), the database has {VmBackupTask.Format(dataBytes)} of data files");
        }

        foreach (var drive in targets.Select((t, i) => (Root: t[..3], Size: files[i].SizeBytes ?? 0)).GroupBy(t => t.Root, StringComparer.OrdinalIgnoreCase))
        {
            var needed = drive.Sum(d => d.Size);
            var volume = volumes.FirstOrDefault(v => string.Equals(v.Root, drive.Key, StringComparison.OrdinalIgnoreCase));
            if (volume is null)
            {
                problems.Add($"{drive.Key} is not a local fixed volume of the VM");
            }
            else if (volume.FreeBytes < needed + FreeSpaceReserve)
            {
                problems.Add($"{drive.Key} has {VmBackupTask.Format(volume.FreeBytes)} free; the files need {VmBackupTask.Format(needed)} and {VmBackupTask.Format(FreeSpaceReserve)} stay free");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Cannot {(request.Overwrite ? "overwrite the database on" : "restore to")} {target}: {string.Join("; ", problems)}.");
        }
    }

    private static int? Major(string? version) =>
        version?.Split('.') is [var first, ..] && int.TryParse(first, out var major) ? major : null;

    /// <summary>Deletes the files a failed or stopped restore wrote (through the agent, with its guards); true if all are gone.</summary>
    private async Task<bool> DeleteAsync(AgentEntity agent, SqlAccount account, List<string> written, IOperationLog log)
    {
        if (written.Count == 0)
        {
            return true;
        }

        try
        {
            var result = await jobs.RunAsync(
                agent.Id, new SqlJobRequest { Kind = SqlJobKinds.DeleteFiles, Account = account, Files = written }, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            foreach (var line in result.Log)
            {
                log.Info(line);
            }

            if (!result.Success)
            {
                log.Error($"Delete them by hand: {result.Error}");
            }

            return result.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            log.Error($"The restored files could not be deleted ({ex.Message}); delete them by hand: {string.Join(", ", written)}");
            return false;
        }
    }

    /// <summary>Until the agent's final report; on stop the agent finishes the current file and reports.</summary>
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
                throw new TimeoutException($"The agent in {host} has not reported for {silence.TotalMinutes:0} minutes (VM stopped, network down or agent restarted); files it wrote stay in the VM.");
            }
        }
    }

    private Task FinishTaskAsync(Guid taskId, SessionState state, string? error, long bytes = 0) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            t.TransferredBytes = Math.Max(t.TransferredBytes, bytes);
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                t.Progress = 100;
            }
        }, CancellationToken.None);

    /// <summary>Transfer progress for the task (at most every 2 s) and the problems for the log.</summary>
    private sealed class Progress(SessionTracker tracker, Guid taskId, long total, IOperationLog log, TimeProvider time) : IFileRestoreObserver
    {
        private long _copied;
        private long _lastUpdate;

        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes)
        {
        }

        public void Copied(long bytes)
        {
            var copied = Interlocked.Add(ref _copied, bytes);
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var last = Interlocked.Read(ref _lastUpdate);
            if (now - last < 2000 || Interlocked.CompareExchange(ref _lastUpdate, now, last) != last)
            {
                return;
            }

            var percent = total > 0 ? Math.Min(99, copied * 100.0 / total) : 0;
            _ = tracker.UpdateTaskAsync(taskId, t => { t.Progress = percent; t.TransferredBytes = copied; }, CancellationToken.None);
        }

        public void FileRestored(long files)
        {
        }

        public void Renamed(string source, string target)
        {
        }

        public void Problem(FileRestoreProblem problem) =>
            log.Error($"{problem.Source} → {problem.Target}: {problem.Reason}.");
    }
}
