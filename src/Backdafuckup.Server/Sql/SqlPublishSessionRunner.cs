using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.FileLevel;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.FileRestore;
using Backdafuckup.Server.Publishing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Publishes a SQL Server database of a restore point (session kind SqlPublish): the agent in the target VM mounts the
/// volumes that hold the database's files (reads come from the backup, writes go to an overlay in the VM) and attaches
/// the database. The session stays running until it is stopped (Unpublish) or its time is over; then the agent drops the
/// database (offline first, so the files stay untouched) and removes the volumes. A database the agent could not remove
/// (it or its VM restarted) is removed here with the session's account as soon as the agent is back.
/// </summary>
public sealed class SqlPublishSessionRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    ChunkCache cache,
    SessionEventWriter events,
    SessionTracker tracker,
    CredentialProtector protector,
    AgentService agents,
    AgentRegistry registry,
    DiskPublishes publishes,
    SqlJobs jobs,
    IHostApplicationLifetime lifetime,
    IOptions<AgentOptions> agentOptions,
    IOptions<SqlRestoreOptions> options,
    TimeProvider time)
{
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(10);

    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<SqlPublishRequestDto>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The publish session has no parameters.");
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
        var target = $"instance {request.TargetInstance}";
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
            Plan? plan = null;
            await FileLevelWorkers.RunDedicated(() => plan = PlanPublish(sources, stored, request, point), "sql-publish-plan").ConfigureAwait(false);
            var planned = plan!;

            var agent = await SqlEndpoints.SqlAgentAsync(request.TargetVmNativeId, agents, registry, cancellationToken).ConfigureAwait(false);
            var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
            target = $"{host}, instance {request.TargetInstance}";
            if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlPublish))
            {
                throw new InvalidOperationException($"The agent in {host} (version {agent.AgentVersion}) cannot publish SQL Server databases yet. It updates itself within a few minutes after a server update; try again then.");
            }

            var account = await SqlEndpoints.AccountAsync(request.CredentialId, dbFactory, protector, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Choose the account that attaches the database.");
            planned.Info.Sql = new PublishSqlDatabase { Instance = request.TargetInstance, Account = account, Database = request.DatabaseName, Files = planned.Files };
            planned.Info.MaxOverlayBytes = options.Value.MaxOverlayBytes;
            log.Info(
                $"Publishing SQL Server database {source} ({planned.Database.Files.Count} file(s), {VmBackupTask.Format(planned.Database.TotalBytes)}, {planned.Database.Consistency}) " +
                $"from the restore point of {point.CreatedUtc.ToLocalTime():g} to {target} as {SqlCatalog.QuoteName(request.DatabaseName)}, attached as {account.UserName}. " +
                $"Volumes: {string.Join(", ", planned.Info.Volumes.Select(v => v.Name))}. Nothing is copied: the VM reads from the backup, and writes go to an overlay in the VM that is deleted when it is unpublished.");
            if (planned.Database.Consistency != "application")
            {
                log.Info("The database is crash-consistent in this restore point; SQL Server recovers it when it is attached, like after a power failure.");
            }

            using var reservation = await FileRestoreSessionRunner.ReserveAgentAsync(
                registry, agent.Id, host, log, agentOptions.Value.WorkWaitLimit, time, cancellationToken).ConfigureAwait(false);
            DiskPublish publish;
            try
            {
                publish = await publishes.PublishAsync(agent.Id, planned.Info, planned.Disks, line => log.Info(line), options.Value.PublishTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (PublishFailedException ex)
            {
                var left = await RemoveLeftoverAsync(agent, account, request, ex.LastStatus, ex.TimedOut, log).ConfigureAwait(false);
                throw new InvalidOperationException($"Publishing {source} to {target} failed: {ex.Message}{(left is null ? string.Empty : " " + left)}", ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                log.Warning("Stopped while publishing; the agent removes what it has set up when it sees the stop.");
                await FinishTaskAsync(task.Id, SessionState.Stopped, "Stopped").ConfigureAwait(false);
                return (SessionState.Stopped, "Stopped while publishing.");
            }

            var publishedAt = time.GetUtcNow();
            var until = publishedAt + TimeSpan.FromMinutes(request.AutoUnpublishMinutes);
            var folder = publish.Status?.Folder;
            var where = $"{SqlCatalog.QuoteName(request.DatabaseName)} on {target}";
            log.Info($"Published: {where} is ready (files under {folder}). It is unpublished at {until.ToLocalTime():g}, or earlier with Unpublish.");
            await tracker.UpdateSessionAsync(session.Id, s => s.Summary = $"Published as {where} until {until.ToLocalTime():g}.", CancellationToken.None).ConfigureAwait(false);

            var reason = await WatchAsync(publish, until, cancellationToken).ConfigureAwait(false);
            var duration = Duration(time.GetUtcNow() - publishedAt);
            switch (reason)
            {
                case EndReason.ServerStopping:
                    // The agent sees the stop (or 404 after the restart) within seconds and unpublishes on its own.
                    log.Warning("The backup service is stopping; the agent unpublishes on its own when it sees the stop.");
                    await publishes.UnpublishAsync(publish, TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
                    await FinishTaskAsync(task.Id, SessionState.Stopped, "Service stopped").ConfigureAwait(false);
                    return (SessionState.Stopped, $"Unpublished when the backup service stopped, after {duration}.");
                case EndReason.Lost when !DiskPublishes.HasEnded(publish):
                    log.Warning($"The agent in {host} has not reported for {(time.GetUtcNow() - publish.LastHeartbeat).TotalMinutes:0} minutes (agent or VM restarted, network?). " +
                        "When it is back, it removes the volumes from its journal.");
                    await publishes.UnpublishAsync(publish, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    break;
                case EndReason.Lost:
                    log.Warning($"The agent in {host} ended the publish on its own{(publish.Status?.Error is { } error ? $": {error}" : " (see its lines above)")}.");
                    await publishes.UnpublishAsync(publish, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    log.Info(reason == EndReason.Expired
                        ? $"The publish time of {Duration(TimeSpan.FromMinutes(request.AutoUnpublishMinutes))} is over; unpublishing."
                        : "Unpublishing.");
                    if (!await publishes.UnpublishAsync(publish, TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false))
                    {
                        log.Warning($"The agent in {host} did not confirm the unpublish within 5 minutes.");
                    }

                    break;
            }

            var ended = DiskPublishes.HasEnded(publish);
            var status = publish.Status;
            var leftover = await RemoveLeftoverAsync(agent, account, request, status, timedOut: !ended, log).ConfigureAwait(false);
            var notes = new List<string>();
            if (reason == EndReason.Lost)
            {
                notes.Add(ended ? $"the agent in {agent.HostName} ended it on its own" : $"the agent in {agent.HostName} stopped reporting");
            }

            if (status?.DatabaseLeft is not null && leftover is null)
            {
                notes.Add("the agent could not remove the database, the backup server removed it");
            }

            if (ended && status?.CleanedUp != true)
            {
                notes.Add("not everything could be removed in the VM (see the log); the agent retries before its next publish and when it starts");
            }
            else if (!ended && reason != EndReason.Lost)
            {
                notes.Add("the agent did not confirm the unpublish");
            }

            if (leftover is not null)
            {
                notes.Add(leftover);
            }

            var statistics = $"read {VmBackupTask.Format(publish.ServedBytes)} from the backup, {VmBackupTask.Format(status?.OverlayBytes ?? 0)} written to the overlay";
            var summary = $"Published as {where} for {duration} ({statistics}); unpublished" +
                (notes.Count == 0 ? ", nothing is left in the VM." : $", but {string.Join("; ", notes)}.");
            var state = leftover is not null ? SessionState.Failed : notes.Count > 0 ? SessionState.Warning : SessionState.Succeeded;
            log.Write(state == SessionState.Succeeded ? LogSeverity.Info : state == SessionState.Warning ? LogSeverity.Warning : LogSeverity.Error, summary);
            await FinishTaskAsync(task.Id, state, state == SessionState.Succeeded ? null : summary).ConfigureAwait(false);
            return (state, summary);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            log.Error(ex.Message);
            await FinishTaskAsync(task.Id, SessionState.Failed, ex.Message).ConfigureAwait(false);
            return (SessionState.Failed, ex.Message);
        }
        catch (OperationCanceledException)
        {
            log.Warning("Stopped before the database was published.");
            await FinishTaskAsync(task.Id, SessionState.Stopped, "Stopped").ConfigureAwait(false);
            return (SessionState.Stopped, "Stopped before the database was published.");
        }
    }

    private enum EndReason
    {
        Stopped,
        Expired,
        Lost,
        ServerStopping,
    }

    /// <summary>Until Unpublish, the publish time is over, or the agent ends the publish or stops reporting.</summary>
    private async Task<EndReason> WatchAsync(DiskPublish publish, DateTimeOffset until, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return EndReason.ServerStopping;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return EndReason.Stopped;
            }

            if (time.GetUtcNow() >= until)
            {
                return EndReason.Expired;
            }

            if (DiskPublishes.HasEnded(publish) || time.GetUtcNow() - publish.LastHeartbeat > DiskPublishes.PublishSilenceLimit)
            {
                return EndReason.Lost;
            }

            try
            {
                await Task.Delay(WatchInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Removes the database when the agent could not (its final report names it) or may not have (no final report): with
    /// the session's account, once the agent is reachable. Only a database whose files all lie in the publish's folder is
    /// dropped. Returns null when nothing is left, otherwise what the admin must do.
    /// </summary>
    private async Task<string?> RemoveLeftoverAsync(AgentEntity agent, SqlAccount account, SqlPublishRequestDto request, PublishStatus? status, bool timedOut, IOperationLog log)
    {
        if (status?.Folder is not { } folder || (!timedOut && status.DatabaseLeft is null))
        {
            return null;
        }

        var name = status.DatabaseLeft ?? request.DatabaseName;
        var manual = $"database {SqlCatalog.QuoteName(name)} may still be attached to instance {request.TargetInstance} in {agent.HostName}; " +
            $"remove it there with ALTER DATABASE {SqlCatalog.QuoteName(name)} SET OFFLINE WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlCatalog.QuoteName(name)};";
        var deadline = time.GetUtcNow() + options.Value.LeftoverWaitLimit;
        var announced = false;
        string? lastError = null;
        while (time.GetUtcNow() < deadline && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            if (agents.IsOnline(agent.Id) && registry.HasCapability(agent.Id, AgentProtocol.CapabilitySqlPublish))
            {
                try
                {
                    var result = await jobs.RunAsync(
                        agent.Id,
                        new SqlJobRequest { Kind = SqlJobKinds.DropPublished, Instance = request.TargetInstance, Account = account, Database = name, PublishFolder = folder },
                        TimeSpan.FromMinutes(5),
                        lifetime.ApplicationStopping).ConfigureAwait(false);
                    foreach (var line in result.Log)
                    {
                        log.Info(line);
                    }

                    if (result.Success)
                    {
                        return null;
                    }

                    lastError = result.Error;
                }
                catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
                {
                    lastError = ex.Message;
                }
            }
            else if (!announced)
            {
                log.Info($"Waiting up to {options.Value.LeftoverWaitLimit.TotalMinutes:0} minutes for the agent in {agent.HostName} to remove database {SqlCatalog.QuoteName(name)} from instance {request.TargetInstance}.");
                announced = true;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), time, lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.Error($"The {manual}{(lastError is null ? string.Empty : $" (last attempt: {lastError})")}");
        return "the " + manual;
    }

    private Task FinishTaskAsync(Guid taskId, SessionState state, string? error) =>
        tracker.UpdateTaskAsync(taskId, t =>
        {
            t.State = state;
            t.Error = error;
            t.EndedUtc = time.GetUtcNow();
            if (state is SessionState.Succeeded or SessionState.Warning)
            {
                t.Progress = 100;
            }
        }, CancellationToken.None);

    private static string Duration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{span.Seconds} s";

    private sealed record Plan(SqlDatabaseDto Database, PublishInfo Info, List<GuestDiskSource> Disks, List<PublishSqlFile> Files);

    /// <summary>
    /// The disks and volumes that hold the database's files, named by their drive letter in the backup, and the files
    /// within them (primary data file first). Refuses databases that cannot be restored, with the reasons.
    /// </summary>
    private static Plan PlanPublish(List<GuestDiskSource> sources, StoredGuestMetadata? stored, SqlPublishRequestDto request, RestorePointEntity point)
    {
        using var image = GuestImage.Open(sources);
        var described = SqlCatalog.Describe(image, stored);
        var database = described.Databases.FirstOrDefault(d =>
                string.Equals(d.Instance, request.Instance, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Name, request.Database, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"The restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} has no SQL Server database {request.Instance}\\{request.Database}.");
        if (!database.Restorable)
        {
            throw new InvalidOperationException($"SQL Server database {request.Instance}\\{request.Database} cannot be published: {string.Join("; ", database.Problems)}");
        }

        var broken = database.Files.Where(f => f.Problem is not null || f.Volume is null || f.GuestPath is null).ToList();
        if (broken.Count > 0 || database.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"SQL Server database {request.Instance}\\{request.Database} cannot be published: " +
                (broken.Count == 0 ? "the backup lists no files." : string.Join("; ", broken.Select(f => $"{f.Path}: {f.Problem ?? "not found in the backup"}"))));
        }

        var info = new PublishInfo { Title = $"{point.VmName}, restore point of {point.CreatedUtc.ToLocalTime():g}" };
        var disks = new List<GuestDiskSource>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new List<PublishSqlFile>();
        foreach (var file in database.Files.OrderByDescending(f => f.Primary))
        {
            if (!names.TryGetValue(file.Volume!, out var name))
            {
                var volume = image.Volumes.First(v => v.Id == file.Volume);
                var disk = disks.FindIndex(d => d.Key == volume.DiskKey);
                if (disk < 0)
                {
                    var diskSource = sources.First(s => s.Key == volume.DiskKey);
                    long size;
                    using (var stream = diskSource.Open())
                    {
                        size = stream.Length;
                    }

                    disk = disks.Count;
                    disks.Add(diskSource);
                    info.Disks.Add(new PublishDisk { Index = disk, Key = diskSource.Key, SizeBytes = size });
                }

                var letter = volume.MountPoint is { Length: 2 } mount && mount[1] == ':' && char.IsAsciiLetter(mount[0]) ? mount[..1].ToUpperInvariant() : null;
                name = letter is not null && names.Values.All(n => n != letter) ? letter : $"volume{names.Count + 1}";
                names[file.Volume!] = name;
                info.Volumes.Add(new PublishVolume { Name = name, DiskIndex = disk, StartOffset = volume.StartOffset });
            }

            files.Add(new PublishSqlFile
            {
                Volume = name,
                Path = string.Join('\\', GuestPath.Parse(file.GuestPath!).Components),
                Kind = file.Kind == "log" ? "log" : "data",
            });
        }

        return new Plan(database, info, disks, files);
    }
}
