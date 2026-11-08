using System.Collections.Concurrent;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.FileRestore;
using Backdafuckup.Server.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Ad;

public sealed class AdExploreOptions
{
    /// <summary>An explore session that nobody used for this long is closed (the DC deletes the backup's directory).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>The agent unmounts by itself after the idle timeout plus this margin (if the server lost track).</summary>
    public TimeSpan AgentIdleMargin { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxOpen { get; set; } = 4;

    /// <summary>A tree, search or object query.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Recovery and dsamain start on the DC.</summary>
    public TimeSpan MountTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>What an AD explore session keeps in its session's parameters (no secrets).</summary>
public sealed record AdExploreParameters(Guid RestorePointId, Guid AgentId, Guid MountId, Guid CredentialId, bool Unmounted);

/// <summary>An open AD explore session: a restore point's directory, copied to a live DC and served there by dsamain.</summary>
public sealed class AdExplore
{
    private long _transferred;

    internal AdExplore(Guid id, Guid sessionId, string user, RestorePointEntity point, AgentEntity agent, string host, AdAccount account, Guid credentialId, IOperationLog log, DateTimeOffset now)
    {
        Id = id;
        SessionId = sessionId;
        User = user;
        Point = point;
        Agent = agent;
        Host = host;
        Account = account;
        CredentialId = credentialId;
        Log = log;
        OpenedUtc = now;
        LastUsedUtc = now;
    }

    /// <summary>Also the mount id on the DC (names its work folder).</summary>
    public Guid Id { get; }

    public Guid SessionId { get; }

    public string User { get; }

    public RestorePointEntity Point { get; }

    public AgentEntity Agent { get; }

    /// <summary>The DC for messages: host name and address.</summary>
    public string Host { get; }

    /// <summary>The Windows account of the domain; in memory only.</summary>
    internal AdAccount Account { get; }

    public Guid CredentialId { get; }

    internal IOperationLog Log { get; }

    public DateTimeOffset OpenedUtc { get; }

    public DateTimeOffset LastUsedUtc { get; internal set; }

    public string State { get; internal set; } = AdExploreStates.Opening;

    public string? Step { get; internal set; }

    public string? Error { get; internal set; }

    public long TotalBytes { get; internal set; }

    public long TransferredBytes => Interlocked.Read(ref _transferred);

    public AdMountInfo? Mount { get; internal set; }

    /// <summary>The work folder exists on the DC (unmount is needed).</summary>
    internal bool Prepared { get; set; }

    internal int Queries;

    internal CancellationTokenSource Stop { get; } = new();

    internal Task Opening { get; set; } = Task.CompletedTask;

    internal int Closed;

    internal void Copied(long bytes) => Interlocked.Add(ref _transferred, bytes);
}

/// <summary>
/// AD explore sessions (session kind AdExplore, not queued; its log is the audit trail). Opening copies the restore
/// point's ntds.dit and logs into a protected work folder on a live DC of the domain (through the agent's file transfer,
/// each file checked with SHA-256), and the agent serves it with dsamain. Queries compare it with the live directory.
/// Closing, the idle timeout and a server restart remove the copy again: it holds every password hash of the domain.
/// </summary>
public sealed class AdExplores(
    IDbContextFactory<AppDbContext> dbFactory,
    RepositoryManager repositories,
    ChunkCache cache,
    RunningSessions running,
    SessionEventWriter events,
    SessionTracker tracker,
    ILiveNotifier notifier,
    AgentService agents,
    AgentRegistry registry,
    AgentTransfers transfers,
    AdJobs jobs,
    CredentialProtector protector,
    IOptions<AgentOptions> agentOptions,
    IOptions<EngineOptions> engine,
    IOptions<AdExploreOptions> options,
    TimeProvider time,
    ILogger<AdExplores> logger)
{
    private readonly ConcurrentDictionary<Guid, AdExplore> _explores = new();
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public AdExploreOptions Options => options.Value;

    /// <summary>The DCs that could open the restore point's directory, usable ones first.</summary>
    public async Task<List<AdTargetDto>> TargetsAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == restorePointId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The restore point no longer exists.");
        var candidates = await db.Agents.AsNoTracking().Where(a => a.DomainRole == 4 || a.DomainRole == 5).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. candidates
            .Select(a => new AdTargetDto(a.Id, a.HostName, a.IpAddresses ?? a.RemoteAddress, a.Domain, agents.IsOnline(a.Id), Usable(point, a) is null, Usable(point, a)))
            .OrderByDescending(t => t.Usable).ThenBy(t => t.HostName, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<AdExplore> OpenAsync(OpenAdExploreDto request, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _explores.Values.FirstOrDefault(e => e.Point.Id == request.RestorePointId && e.Agent.Id == request.AgentId && e.User == user
                && e.State is AdExploreStates.Opening or AdExploreStates.Ready);
            if (existing is not null)
            {
                existing.LastUsedUtc = time.GetUtcNow();
                return existing;
            }

            if (_explores.Values.Count(e => e.State is AdExploreStates.Opening or AdExploreStates.Ready) >= options.Value.MaxOpen)
            {
                throw new InvalidOperationException(
                    $"{options.Value.MaxOpen} Active Directory explore sessions are open already. Close one under Sessions, or wait until one closes after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use.");
            }

            RestorePointEntity point;
            AgentEntity agent;
            Credential credential;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The restore point no longer exists.");
                agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AgentId, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException("The agent no longer exists.");
                credential = await db.Credentials.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CredentialId && c.Kind == CredentialKind.Windows, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Choose a Windows account of the domain (under Credentials).");
            }

            if (point.State != RestorePointState.Available)
            {
                throw new InvalidOperationException($"The restore point of {point.VmName} from {point.CreatedUtc.ToLocalTime():g} is not available ({point.State}).");
            }

            if (Usable(point, agent) is { } reason)
            {
                throw new InvalidOperationException($"{agent.HostName} cannot open the directory of this restore point: {reason}.");
            }

            var account = new AdAccount { UserName = credential.Username, Password = protector.Unprotect(credential.ProtectedSecret) };
            var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
            var id = Guid.NewGuid();
            var sessionId = await CreateSessionAsync(point, agent, new AdExploreParameters(point.Id, agent.Id, id, credential.Id, false), user, cancellationToken).ConfigureAwait(false);
            var explore = new AdExplore(id, sessionId, user, point, agent, host, account, credential.Id, events.CreateLog(sessionId, null), time.GetUtcNow());
            explore.Log.Info($"Opened by {user}: the Active Directory database of {point.VmName} ({(string.IsNullOrEmpty(point.AdDomain) ? "domain unknown" : point.AdDomain)}) " +
                $"from the restore point of {point.CreatedUtc.ToLocalTime():g}, on the domain controller {host} as {account.UserName}.");
            var stop = running.Register(sessionId, SessionKind.AdExplore, CancellationToken.None);
            stop.Token.Register(() => _ = Task.Run(() => CloseAsync(explore, "stopped by a user", SessionState.Stopped)));
            _explores[id] = explore;
            explore.Opening = Task.Run(() => OpenInBackgroundAsync(explore));
            return explore;
        }
        finally
        {
            _openGate.Release();
        }
    }

    public AdExplore Get(Guid id, string user) =>
        _explores.TryGetValue(id, out var explore) && explore.User == user
            ? explore
            : throw new KeyNotFoundException($"This Active Directory explore session is closed (by a user, after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use, or by a restart of the backup service). Open it again.");

    public IReadOnlyList<AdExplore> ForUser(string user) => [.. _explores.Values.Where(e => e.User == user)];

    public AdExploreDto ToDto(AdExplore explore) => new(
        explore.Id,
        explore.SessionId,
        explore.Point.Id,
        explore.Point.VmName,
        explore.Point.CreatedUtc,
        string.IsNullOrEmpty(explore.Point.AdDomain) ? null : explore.Point.AdDomain,
        explore.Host,
        explore.Account.UserName,
        explore.State,
        explore.Step,
        explore.TransferredBytes,
        explore.TotalBytes,
        explore.Error,
        explore.Mount is { } m ? new AdMountDto(m.BackupDomainDn, m.LiveHost, m.RecycleBin, m.TombstoneLifetimeDays, m.DeletedObjectLifetimeDays, m.Recovery) : null,
        explore.OpenedUtc,
        (int)options.Value.IdleTimeout.TotalMinutes);

    public async Task<List<AdEntryDto>> TreeAsync(AdExplore explore, string? baseDn, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(explore, new AdJobRequest { Kind = AdJobKinds.Tree, BaseDn = baseDn }, cancellationToken).ConfigureAwait(false);
        return [.. result.Entries.Select(ToDto)];
    }

    public async Task<AdSearchPageDto> SearchAsync(AdExplore explore, AdSearchDto search, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var result = await QueryAsync(explore, new AdJobRequest
        {
            Kind = AdJobKinds.Search,
            BaseDn = search.BaseDn,
            Text = search.Text,
            Classes = search.Classes ?? [],
            States = search.States ?? [],
            Subtree = search.Subtree,
            PageSize = Math.Clamp(search.PageSize, 1, 1000),
            Cookie = search.Cookie,
        }, cancellationToken).ConfigureAwait(false);
        return new AdSearchPageDto([.. result.Entries.Select(ToDto)], result.Cookie, result.Scanned);
    }

    public async Task<AdObjectDto> ObjectAsync(AdExplore explore, Guid guid, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(explore, new AdJobRequest { Kind = AdJobKinds.Object, ObjectGuid = guid }, cancellationToken).ConfigureAwait(false);
        var o = result.Object ?? throw new InvalidOperationException($"The agent on {explore.Host} returned no object.");
        explore.Log.Info($"{explore.User} viewed {o.Dn} ({o.ObjectClass}, {o.State}{(o.Attributes.Count(a => a.Changed) is > 0 and var changed ? $", {changed} attribute(s) differ" : string.Empty)}).");
        return new AdObjectDto(
            o.Guid, o.Dn, o.LiveDn, o.ObjectClass, o.State, o.Sid, o.ParentDn, o.LiveParentDn,
            [.. o.Attributes.Select(a => new AdAttributeDto(a.Name, a.Syntax, [.. a.Backup.Select(ToDto)], [.. a.Live.Select(ToDto)], a.Changed, a.Restorable, a.Note, a.Linked))],
            [.. o.MemberOfBackup.Select(ToDto)],
            [.. o.MemberOfLive.Select(ToDto)]);
    }

    /// <summary>Unmounts on the DC (deleting the copy), ends the session and forgets the explore.</summary>
    public async Task CloseAsync(AdExplore explore, string reason, SessionState state = SessionState.Succeeded, bool flushLog = true)
    {
        if (Interlocked.Exchange(ref explore.Closed, 1) == 1)
        {
            return;
        }

        await explore.Stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await explore.Opening.WaitAsync(TimeSpan.FromMinutes(3), time).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
        }

        var wasFailed = explore.State == AdExploreStates.Failed;
        explore.State = AdExploreStates.Closed;
        _explores.TryRemove(explore.Id, out _);
        var unmounted = await UnmountAsync(explore.Agent, explore.Host, explore.Id, explore.Account, explore.Log, explore.Prepared || explore.Mount is not null).ConfigureAwait(false);
        running.Unregister(explore.SessionId);
        var summary = wasFailed ? explore.Error ?? reason : $"Closed: {reason}.";
        explore.Log.Info($"Active Directory explore session closed: {reason}.");
        if (flushLog)
        {
            await events.FlushAsync().ConfigureAwait(false);
        }

        await tracker.UpdateSessionAsync(
            explore.SessionId,
            s =>
            {
                s.State = wasFailed ? SessionState.Failed : state;
                s.Summary = summary;
                s.EndedUtc = time.GetUtcNow();
                s.Progress = 100;
                if (unmounted)
                {
                    s.Parameters = JsonSerializer.Serialize(Parameters(s.Parameters) with { Unmounted = true });
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes explore sessions that were not used for the idle timeout, and unmounts the copies of sessions that ended
    /// with a restart of the backup service once their DC's agent is online.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var limit = time.GetUtcNow() - options.Value.IdleTimeout;
        foreach (var explore in _explores.Values.Where(e => Volatile.Read(ref e.Queries) == 0 && e.State != AdExploreStates.Opening && e.LastUsedUtc < limit).ToList())
        {
            await CloseAsync(explore, $"closed after {options.Value.IdleTimeout.TotalMinutes:0} minutes without use").ConfigureAwait(false);
        }

        List<SessionEntity> leftovers;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var since = time.GetUtcNow() - TimeSpan.FromDays(1);
            leftovers = await db.Sessions.AsNoTracking()
                .Where(s => s.Kind == SessionKind.AdExplore && s.EndedUtc != null && s.EndedUtc > since && s.Parameters != null)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var session in leftovers)
        {
            var parameters = Parameters(session.Parameters);
            if (parameters.Unmounted || _explores.ContainsKey(parameters.MountId) || !agents.IsOnline(parameters.AgentId))
            {
                continue;
            }

            AgentEntity? agent;
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == parameters.AgentId, cancellationToken).ConfigureAwait(false);
            }

            var log = events.CreateLog(session.Id, null);
            var unmounted = agent is null || await UnmountAsync(agent, $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress})", parameters.MountId, null, log, true).ConfigureAwait(false);
            if (unmounted)
            {
                await tracker.UpdateSessionAsync(session.Id, s => s.Parameters = JsonSerializer.Serialize(parameters with { Unmounted = true }), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>At shutdown: closes every session; the DCs unmount what could not be reached after their idle time.</summary>
    public async Task CloseAllAsync(string reason)
    {
        foreach (var explore in _explores.Values.ToList())
        {
            try
            {
                await CloseAsync(explore, reason, SessionState.Stopped, flushLog: false).WaitAsync(TimeSpan.FromSeconds(20), time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or InvalidOperationException or DbUpdateException or Npgsql.NpgsqlException)
            {
                logger.LogWarning(ex, "Closing the AD explore session {Session} at shutdown did not finish; the next start ends it and the DC unmounts it.", explore.SessionId);
            }
        }
    }

    internal static AdExploreParameters Parameters(string? json) =>
        (json is null ? null : JsonSerializer.Deserialize<AdExploreParameters>(json)) ?? new AdExploreParameters(Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, true);

    /// <summary>Why the agent cannot open this restore point's directory, or null.</summary>
    private string? Usable(RestorePointEntity point, AgentEntity agent)
    {
        if (point.DomainController != true)
        {
            return $"the restore point of {point.VmName} is not of a domain controller";
        }

        if (agent.DomainRole is not (4 or 5))
        {
            return "it is not a domain controller";
        }

        if (!agents.IsOnline(agent.Id))
        {
            return $"its agent has not contacted the server since {agent.LastContactUtc.ToLocalTime():g}";
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilityAdExplore))
        {
            return $"its agent (version {agent.AgentVersion}) cannot open backups of the directory (it updates itself within a few minutes after a server update; on a DC it needs dsamain.exe)";
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilityRestoreFilesV2))
        {
            return $"its agent (version {agent.AgentVersion}) cannot receive files yet";
        }

        if (!string.IsNullOrEmpty(point.AdDomain) && !string.IsNullOrEmpty(agent.Domain) && !string.Equals(point.AdDomain, agent.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return $"it is a domain controller of {agent.Domain}, the restore point is of {point.AdDomain}";
        }

        return null;
    }

    private async Task OpenInBackgroundAsync(AdExplore explore)
    {
        var stop = explore.Stop.Token;
        var point = explore.Point;
        try
        {
            var repository = await repositories.GetAsync(point.RepositoryId, stop).ConfigureAwait(false);
            List<AdDatabaseFile> files;
            using (var pin = repository.Pin(point.Id))
            await using (var reader = await repository.OpenRestorePointAsync(point.Id, stop).ConfigureAwait(false))
            {
                explore.Step = "Finding the directory database in the restore point";
                var disks = await FileRestoreMounts.OpenDisksAsync(reader, cache, stop).ConfigureAwait(false);
                var stored = await StoredGuestMetadata.ReadAsync(reader, stop).ConfigureAwait(false);
                var notes = new List<string>();
                List<AdDatabaseFile>? located = null;
                AgentTransferPlan? plan = null;
                string? folder = null;
                await FileLevelWorkers.RunDedicated(
                    () =>
                    {
                        // With the volumes described: their drive letters map the Windows paths.
                        using var image = GuestImage.Open(disks);
                        located = AdCatalog.Locate(image, stored, notes);
                    },
                    "ad-explore-locate").ConfigureAwait(false);
                files = located!;
                foreach (var note in notes)
                {
                    explore.Log.Info(note);
                }

                if (stored?.ActiveDirectory is { Consistency: not "application" } ad)
                {
                    explore.Log.Info($"The directory is crash-consistent in this restore point ({ad.ConsistencyReason}); its recovery on the domain controller replays the logs.");
                }

                explore.TotalBytes = files.Sum(f => f.Size);
                explore.Log.Info($"Copying {files.Count} file(s), {VmBackupTask.Format(explore.TotalBytes)}, to {explore.Host}: {string.Join(", ", files.Select(f => f.Name))}.");

                // The work folder: created by the agent, only SYSTEM and Administrators, with the free space checked.
                explore.Step = "Preparing the work folder on the domain controller";
                var prepared = await RunAsync(explore, new AdJobRequest { Kind = AdJobKinds.Prepare, RequiredBytes = explore.TotalBytes }, TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
                explore.Prepared = true;
                folder = prepared.Prepare!.Folder;

                await FileLevelWorkers.RunDedicated(
                    () =>
                    {
                        using var image = GuestImage.Open(disks, describe: false);
                        plan = AgentFilePlan.Plan([.. files.Select(f => (f.Volume, image.OpenFileSystem(f.Volume), f.Path, folder + "\\" + f.Name, f.WindowsPath))]);
                    },
                    "ad-explore-plan").ConfigureAwait(false);

                explore.Step = "Copying the directory database to the domain controller";
                await TransferAsync(explore, disks, files, plan!, folder, stop).ConfigureAwait(false);
            }

            // dsamain: recovery of the copy and the start may take a while on a large directory.
            explore.Step = "Starting dsamain on the domain controller";
            var mounted = await RunAsync(explore, new AdJobRequest
            {
                Kind = AdJobKinds.Mount,
                Account = explore.Account,
                IdleMinutes = (int)(options.Value.IdleTimeout + options.Value.AgentIdleMargin).TotalMinutes,
            }, options.Value.MountTimeout, stop).ConfigureAwait(false);
            explore.Mount = mounted.Mount;
            explore.Step = null;
            explore.LastUsedUtc = time.GetUtcNow();
            explore.State = AdExploreStates.Ready;
            await tracker.UpdateSessionAsync(explore.SessionId, s => s.Progress = 100, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (stop.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Opening AD explore session {Session} stopped.", explore.SessionId);
        }
        catch (Exception ex)
        {
            explore.Error = $"Opening failed: {ex.Message}";
            explore.Step = null;
            explore.Log.Error(explore.Error);
            explore.State = AdExploreStates.Failed;
            explore.LastUsedUtc = time.GetUtcNow();
            if (explore.Prepared)
            {
                // Nothing of a failed open stays on the DC.
                await UnmountAsync(explore.Agent, explore.Host, explore.Id, explore.Account, explore.Log, true).ConfigureAwait(false);
                explore.Prepared = false;
            }

            await tracker.UpdateSessionAsync(explore.SessionId, s => { s.State = SessionState.Failed; s.Summary = explore.Error; }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task TransferAsync(AdExplore explore, IReadOnlyList<GuestDiskSource> disks, List<AdDatabaseFile> files, AgentTransferPlan plan, string folder, CancellationToken stop)
    {
        var info = new FileTransferInfo
        {
            TransferId = Guid.NewGuid(),
            Roots = [folder],
            Overwrite = false,
            IfExists = FileTransferIfExists.Fail,
            RestorePermissions = false,
            ItemCount = plan.Items.Count,
            TotalBytes = plan.Bytes,
        };
        using var reservation = await FileRestoreSessionRunner.ReserveAgentAsync(
            registry, explore.Agent.Id, explore.Host, explore.Log, agentOptions.Value.WorkWaitLimit, time, stop).ConfigureAwait(false);
        using var transfer = new AgentTransfer(explore.Agent.Id, info, plan, disks, files[0].Volume, new Progress(explore), time);
        transfers.Add(transfer);
        FileTransferProgress? report;
        try
        {
            var accepted = await registry.SendAsync(
                explore.Agent.Id,
                new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.RestoreFiles, TransferId = info.TransferId },
                agentOptions.Value.PrepareTimeout,
                stop).ConfigureAwait(false);
            if (!accepted.Success)
            {
                throw new InvalidOperationException($"The agent on {explore.Host} did not start receiving the files: {accepted.Error}");
            }

            report = await WaitAsync(transfer, explore.Host, stop).ConfigureAwait(false);
        }
        finally
        {
            transfers.Remove(transfer);
        }

        stop.ThrowIfCancellationRequested();
        if (!transfer.Verified.All(v => v))
        {
            var missing = files.Where((_, i) => !transfer.Verified[i]).Select(f => f.Name).ToList();
            throw new InvalidOperationException(
                $"Copying to {explore.Host} failed ({report?.Error ?? $"{missing.Count} of {files.Count} file(s) not written or not as in the backup: {string.Join(", ", missing)}"}).");
        }

        explore.Log.Info($"Copied {files.Count} file(s), {VmBackupTask.Format(plan.Bytes)}, into {folder} on {explore.Host}; each checked with SHA-256.");
    }

    private async Task<FileTransferProgress?> WaitAsync(AgentTransfer transfer, string host, CancellationToken stop)
    {
        while (true)
        {
            var finished = await Task.WhenAny(transfer.Finished.Task, Task.Delay(TimeSpan.FromSeconds(2), time, CancellationToken.None)).ConfigureAwait(false);
            if (finished == transfer.Finished.Task)
            {
                return await transfer.Finished.Task.ConfigureAwait(false);
            }

            if (stop.IsCancellationRequested)
            {
                transfer.CancelRequested = true;
                stop.ThrowIfCancellationRequested();
            }

            if (transfer.AgentRestarted)
            {
                throw new TimeoutException($"The agent on {host} restarted (service restart, crash or VM reboot) while it received the files.");
            }

            var silence = time.GetUtcNow() - transfer.LastHeartbeat;
            if (silence > agentOptions.Value.TransferSilenceLimit)
            {
                throw new TimeoutException($"The agent on {host} has not reported for {silence.TotalMinutes:0} minutes (VM stopped, network down or agent restarted).");
            }
        }
    }

    private async Task<AdJobResult> QueryAsync(AdExplore explore, AdJobRequest request, CancellationToken cancellationToken)
    {
        if (explore.State != AdExploreStates.Ready)
        {
            throw new InvalidOperationException(explore.State switch
            {
                AdExploreStates.Opening => $"The directory is still being opened on {explore.Host} ({explore.Step ?? "starting"}).",
                AdExploreStates.Failed => explore.Error ?? "Opening failed.",
                _ => "This explore session is closed. Open it again.",
            });
        }

        Interlocked.Increment(ref explore.Queries);
        try
        {
            explore.LastUsedUtc = time.GetUtcNow();
            request.Account = explore.Account;
            var result = await RunAsync(explore, request, options.Value.QueryTimeout, cancellationToken, failOnError: false).ConfigureAwait(false);
            if (result.NotMounted)
            {
                explore.Error = $"The domain controller {explore.Host} no longer serves the backup's directory ({result.Error}). Open it again.";
                explore.State = AdExploreStates.Failed;
                explore.Prepared = false;
                explore.Mount = null;
                explore.Log.Warning(explore.Error);
                _ = Task.Run(() => CloseAsync(explore, "the domain controller no longer serves it", SessionState.Failed));
                throw new InvalidOperationException(explore.Error);
            }

            if (!result.Success)
            {
                throw new InvalidOperationException(result.Error ?? $"The agent on {explore.Host} reported no details.");
            }

            explore.LastUsedUtc = time.GetUtcNow();
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref explore.Queries);
        }
    }

    private async Task<AdJobResult> RunAsync(AdExplore explore, AdJobRequest request, TimeSpan timeout, CancellationToken cancellationToken, bool failOnError = true)
    {
        request.MountId = explore.Id;
        var result = await jobs.RunAsync(explore.Agent.Id, request, timeout, cancellationToken).ConfigureAwait(false);
        if (request.Kind is AdJobKinds.Prepare or AdJobKinds.Mount or AdJobKinds.Unmount)
        {
            foreach (var line in result.Log)
            {
                explore.Log.Info(line);
            }
        }

        if (failOnError && !result.Success)
        {
            throw new InvalidOperationException(result.Error ?? $"The agent on {explore.Host} reported no details.");
        }

        return result;
    }

    /// <summary>Asks the DC's agent to stop dsamain and delete the work folder; false if it could not be reached.</summary>
    private async Task<bool> UnmountAsync(AgentEntity agent, string host, Guid mountId, AdAccount? account, IOperationLog log, bool needed)
    {
        if (!needed)
        {
            return true;
        }

        var idle = (options.Value.IdleTimeout + options.Value.AgentIdleMargin).TotalMinutes;
        if (!agents.IsOnline(agent.Id))
        {
            log.Warning($"The agent on {host} is offline: it deletes the backup's directory when it is back (at its start) or after {idle:0} minutes without use.");
            return false;
        }

        try
        {
            var result = await jobs.RunAsync(agent.Id, new AdJobRequest { Kind = AdJobKinds.Unmount, MountId = mountId, Account = account }, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            if (result.Success)
            {
                log.Info($"The backup's directory was removed from {host} (dsamain stopped, work folder deleted).");
                return true;
            }

            log.Error($"Removing the backup's directory from {host} failed: {result.Error} The agent tries again after {idle:0} minutes and at its next start.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            log.Warning($"Removing the backup's directory from {host} did not finish: {ex.Message} The agent deletes it after {idle:0} minutes without use or at its next start.");
        }

        return false;
    }

    private async Task<Guid> CreateSessionAsync(RestorePointEntity point, AgentEntity agent, AdExploreParameters parameters, string user, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var session = new SessionEntity
        {
            Kind = SessionKind.AdExplore,
            RepositoryId = point.RepositoryId,
            Name = $"Explore AD {point.VmName} ({point.CreatedUtc.ToLocalTime():g}) on {agent.HostName}",
            TriggeredBy = user,
            State = SessionState.Running,
            CreatedUtc = now,
            StartedUtc = now,
            ServerInstanceId = engine.Value.ServerInstanceId,
            Parameters = JsonSerializer.Serialize(parameters),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
        return session.Id;
    }

    private static AdEntryDto ToDto(AdEntry e) =>
        new(e.Guid, e.Dn, e.LiveDn, e.Name, e.ObjectClass, e.State, e.SamAccountName, e.Description, e.Container, e.ChangedInBackup, e.ChangedLive);

    private static AdValueDto ToDto(AdValue v) => new(v.Text, v.Guid);

    private sealed class Progress(AdExplore explore) : IFileRestoreObserver
    {
        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes)
        {
        }

        public void Copied(long bytes) => explore.Copied(bytes);

        public void FileRestored(long files)
        {
        }

        public void Renamed(string source, string target)
        {
        }

        public void Problem(FileRestoreProblem problem) => explore.Log.Warning($"{problem.Source}{(problem.Target is null ? string.Empty : $" → {problem.Target}")}: {problem.Reason}");
    }
}

/// <summary>Closes idle AD explore sessions, unmounts leftovers, and closes everything when the service stops.</summary>
public sealed class AdExploreSweeper(AdExplores explores, ILogger<AdExploreSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try
                {
                    await explores.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Closing idle AD explore sessions failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await explores.CloseAllAsync("the backup service stopped").ConfigureAwait(false);
    }
}
