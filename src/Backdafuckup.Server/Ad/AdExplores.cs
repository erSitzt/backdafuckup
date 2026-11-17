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

    /// <summary>Restores running from this session (it is not closed by a user meanwhile).</summary>
    internal int Restores;

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
    RestorePointAccess access,
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

    /// <summary>
    /// The DCs that could open the restore point's directory: usable ones first, among them the DC the backup is from (a DC
    /// with a VM generation id cannot open the backup of a DC without one).
    /// </summary>
    public async Task<List<AdTargetDto>> TargetsAsync(Guid restorePointId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == restorePointId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The restore point no longer exists.");
        var candidates = await db.Agents.AsNoTracking().Where(a => a.DomainRole == 4 || a.DomainRole == 5).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. candidates
            .Select(a => new AdTargetDto(a.Id, a.HostName, a.IpAddresses ?? a.RemoteAddress, a.Domain, agents.IsOnline(a.Id), Usable(point, a) is null, Usable(point, a),
                string.Equals(a.VmNativeId, point.VmNativeId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.Usable).ThenByDescending(t => t.BackupSource).ThenBy(t => t.HostName, StringComparer.OrdinalIgnoreCase)];
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

    /// <summary>What a restore of the chosen objects would write, step by step; writes nothing.</summary>
    public async Task<AdRestorePlanDto> PlanAsync(AdExplore explore, AdRestoreRequestDto request, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(explore, Job(request, AdJobKinds.Plan), cancellationToken).ConfigureAwait(false);
        return new AdRestorePlanDto(explore.Host, LiveDomain(explore), [.. result.Restore.Select(ToDto)]);
    }

    /// <summary>
    /// Restores the chosen objects into the live directory (session kind AdRestore, one task per object). Not queued: the
    /// password for accounts without one stays in memory and goes to the agent in the job request only.
    /// </summary>
    public async Task<Guid> RestoreAsync(AdExplore explore, AdRestoreRequestDto request, string user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Objects.Count + (request.Gpos?.Count ?? 0) is 0 or > 1000)
        {
            throw new ArgumentException("Choose 1 to 1,000 objects and group policies to restore.");
        }

        var domain = LiveDomain(explore);
        if (domain is null || !string.Equals(request.ConfirmDomain?.Trim(), domain, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Type the live domain ({domain ?? "unknown"}) to confirm the restore into {explore.Host}.");
        }

        if (explore.State != AdExploreStates.Ready)
        {
            throw new InvalidOperationException(explore.State == AdExploreStates.Opening ? "The directory is still being opened." : explore.Error ?? "This explore session is closed. Open it again.");
        }

        Guid sessionId;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = time.GetUtcNow();
            var session = new SessionEntity
            {
                Kind = SessionKind.AdRestore,
                RepositoryId = explore.Point.RepositoryId,
                Name = $"Restore AD objects of {explore.Point.VmName} ({explore.Point.CreatedUtc.ToLocalTime():g}) into {explore.Agent.HostName}",
                TriggeredBy = user,
                State = SessionState.Running,
                CreatedUtc = now,
                StartedUtc = now,
                ServerInstanceId = engine.Value.ServerInstanceId,
                Parameters = JsonSerializer.Serialize(new
                {
                    ExploreSessionId = explore.SessionId,
                    explore.Point.Id,
                    AgentId = explore.Agent.Id,
                    request.Objects,
                    request.Gpos,
                    request.Recreate,
                    request.TargetParentDn,
                    PasswordEntered = !string.IsNullOrEmpty(request.NewPassword),
                }),
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await notifier.SessionUpdatedAsync(session.ToDto()).ConfigureAwait(false);
            sessionId = session.Id;
        }

        explore.Log.Info($"{user} started restoring {request.Objects.Count} object(s){(request.Gpos is { Count: > 0 } g ? $" and {g.Count} group policy(ies)" : string.Empty)} into {explore.Host} (session {sessionId}).");
        Interlocked.Increment(ref explore.Queries);
        Interlocked.Increment(ref explore.Restores);
        _ = Task.Run(async () =>
        {
            try
            {
                await RunRestoreAsync(explore, request, sessionId, user).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref explore.Restores);
                Interlocked.Decrement(ref explore.Queries);
                explore.LastUsedUtc = time.GetUtcNow();
            }
        }, CancellationToken.None);
        return sessionId;
    }

    private async Task RunRestoreAsync(AdExplore explore, AdRestoreRequestDto request, Guid sessionId, string user)
    {
        var log = events.CreateLog(sessionId, null);
        var point = explore.Point;
        log.Info($"Restoring {request.Objects.Count} object(s){(request.Gpos is { Count: > 0 } gpos ? $" and {gpos.Count} group policy(ies)" : string.Empty)} of {point.VmName} from the restore point of {point.CreatedUtc.ToLocalTime():g} into {LiveDomain(explore)} " +
            $"on {explore.Host}, started by {user}, written as {explore.Account.UserName}." +
            (request.Recreate ? " Objects that are gone are re-created with a new SID." : string.Empty) +
            (string.IsNullOrEmpty(request.NewPassword) ? " Accounts without a password stay disabled." : " Accounts without a password get the password entered for the restore."));
        SessionState state;
        string summary;
        try
        {
            if (request.Gpos is { Count: > 0 })
            {
                await StageGposAsync(explore, request, log).ConfigureAwait(false);
            }

            var job = Job(request, AdJobKinds.Restore);
            job.Account = explore.Account;
            job.MountId = explore.Id;
            var result = await jobs.RunAsync(explore.Agent.Id, job, TimeSpan.FromMinutes(30), CancellationToken.None).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.NotMounted
                    ? $"The domain controller {explore.Host} no longer serves the backup's directory ({result.Error}). Open the explore session again."
                    : result.Error ?? $"The agent on {explore.Host} reported no details.");
            }

            foreach (var line in result.Log)
            {
                log.Info(line);
            }

            foreach (var item in result.Restore)
            {
                var task = await tracker.AddTaskAsync(sessionId, (item.NewGuid ?? item.Guid).ToString("D"), item.Dn, CancellationToken.None).ConfigureAwait(false);
                var taskLog = events.CreateLog(sessionId, task.Id);
                var taskState = item.Outcome switch
                {
                    AdRestoreOutcomes.Failed => SessionState.Failed,
                    AdRestoreOutcomes.Skipped => SessionState.Warning,
                    _ => item.Warnings.Count > 0 ? SessionState.Warning : SessionState.Succeeded,
                };
                var verb = item.Action switch
                {
                    AdRestoreActions.Reanimate => "reanimated",
                    AdRestoreActions.Recreate => "re-created",
                    AdRestoreActions.Gpo => "group policy restored (files, container, version" + (item.Steps.Any(s => s.Operation == "link") ? ", links)" : ")") + ",",
                    _ => "restored",
                };
                var text = item.Outcome switch
                {
                    AdRestoreOutcomes.Done => $"{item.Dn} ({item.ObjectClass}, was {item.State}): {verb} as {item.LiveDn}{(item.NewGuid is { } g ? $" (new objectGUID {g})" : string.Empty)}.",
                    AdRestoreOutcomes.Skipped => $"{item.Dn}: skipped. {item.Reason}",
                    _ => $"{item.Dn}: failed. {item.Reason}",
                };
                if (taskState == SessionState.Failed)
                {
                    taskLog.Error(text);
                }
                else
                {
                    taskLog.Info(text);
                }

                foreach (var warning in item.Warnings)
                {
                    taskLog.Warning($"{item.Dn}: {warning}");
                }

                await tracker.UpdateTaskAsync(task.Id, t =>
                {
                    t.State = taskState;
                    t.Error = taskState == SessionState.Succeeded ? null : item.Reason ?? string.Join(" ", item.Warnings);
                    t.Progress = 100;
                    t.StartedUtc = time.GetUtcNow();
                    t.EndedUtc = time.GetUtcNow();
                }, CancellationToken.None).ConfigureAwait(false);
            }

            var done = result.Restore.Count(i => i.Outcome == AdRestoreOutcomes.Done);
            var skipped = result.Restore.Count(i => i.Outcome == AdRestoreOutcomes.Skipped);
            var failed = result.Restore.Count(i => i.Outcome == AdRestoreOutcomes.Failed);
            var warned = result.Restore.Count(i => i.Outcome == AdRestoreOutcomes.Done && i.Warnings.Count > 0);
            summary = $"{done} of {result.Restore.Count} object(s) restored into {explore.Agent.HostName}" +
                (warned > 0 ? $" ({warned} with warnings)" : string.Empty) + (skipped > 0 ? $", {skipped} skipped" : string.Empty) + (failed > 0 ? $", {failed} failed" : string.Empty) + ".";
            state = failed == result.Restore.Count ? SessionState.Failed : failed + skipped + warned > 0 ? SessionState.Warning : SessionState.Succeeded;
            log.Info(summary);
            explore.Log.Info($"Restore session {sessionId}: {summary}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            summary = $"The restore failed: {ex.Message}";
            state = SessionState.Failed;
            log.Error(summary);
        }

        await events.FlushAsync().ConfigureAwait(false);
        await tracker.UpdateSessionAsync(sessionId, s =>
        {
            s.State = state;
            s.Summary = summary;
            s.EndedUtc = time.GetUtcNow();
            s.Progress = 100;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The files of the group policies: the agent creates a staging folder in SYSVOL for each, the server copies the GPT
    /// from the restore point into it with the backup's permissions (each file checked with SHA-256).
    /// </summary>
    private async Task StageGposAsync(AdExplore explore, AdRestoreRequestDto request, IOperationLog log)
    {
        var prepare = Job(request, AdJobKinds.GpoPrepare);
        prepare.Account = explore.Account;
        var staged = await RunAsync(explore, prepare, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
        foreach (var line in staged.Log)
        {
            log.Info(line);
        }

        var point = explore.Point;
        var domain = LiveDomain(explore) ?? throw new InvalidOperationException("The live domain is unknown.");
        // Opened again where the catalog says now (a scale-out move may have moved it since the explore opened).
        await using var opened = await access.OpenRestorePointAsync(point, CancellationToken.None).ConfigureAwait(false);
        var reader = opened.Reader;
        var disks = await FileRestoreMounts.OpenDisksAsync(reader, cache, CancellationToken.None).ConfigureAwait(false);
        var stored = await StoredGuestMetadata.ReadAsync(reader, CancellationToken.None).ConfigureAwait(false);
        foreach (var gpo in staged.GpoStaging)
        {
            AgentTransferPlan? plan = null;
            string? source = null;
            string? volume = null;
            await FileLevelWorkers.RunDedicated(
                () =>
                {
                    using var image = GuestImage.Open(disks);
                    var located = AdCatalog.LocatePolicy(image, stored, domain, gpo.Name)
                        ?? throw new InvalidOperationException($"The restore point has no folder for the group policy {gpo.DisplayName ?? gpo.Name} ({gpo.Name}) in SYSVOL.");
                    (volume, var root, source) = located;
                    var fs = image.OpenFileSystem(volume);
                    string Target(GuestPath path) => gpo.Folder + string.Concat(path.Components.Skip(root.Components.Count).Select(c => "\\" + c));
                    string Describe(GuestPath path) => source + string.Concat(path.Components.Skip(root.Components.Count).Select(c => "\\" + c));
                    plan = AgentTargetPaths.Plan(fs, [root], Target, permissions: true, Describe, new Quiet(), 10_000, CancellationToken.None);
                },
                "ad-gpo-plan").ConfigureAwait(false);
            await TransferAsync(explore, disks, plan!, gpo.Folder, volume!, permissions: true, new Quiet(), CancellationToken.None).ConfigureAwait(false);
            log.Info($"Copied the files of the group policy {gpo.DisplayName ?? gpo.Name} ({plan!.Files} file(s), {VmBackupTask.Format(plan.Bytes)}) from {source} " +
                $"in the restore point into {gpo.Folder} on {explore.Host}, with their permissions; each file checked with SHA-256.");
        }
    }

    private static AdJobRequest Job(AdRestoreRequestDto request, string kind) => new()
    {
        Kind = kind,
        Gpos = [.. (request.Gpos ?? []).Select(g => new AdGpoChoice { Guid = g.Guid, Links = g.Links })],
        Objects = [.. request.Objects.Select(o => new AdRestoreObject { Guid = o.Guid, Attributes = o.Attributes, MoveBack = o.MoveBack, Memberships = o.Memberships })],
        Recreate = request.Recreate,
        TargetParentDn = string.IsNullOrWhiteSpace(request.TargetParentDn) ? null : request.TargetParentDn.Trim(),
        NewPassword = kind == AdJobKinds.Restore && !string.IsNullOrEmpty(request.NewPassword) ? request.NewPassword : null,
        PasswordEntered = request.PasswordEntered || !string.IsNullOrEmpty(request.NewPassword),
    };

    /// <summary>The live domain's DNS name (from the mount, else the restore point's domain).</summary>
    private static string? LiveDomain(AdExplore explore) =>
        explore.Mount?.LiveDomainDn is { Length: > 0 } dn
            ? string.Join('.', dn.Split(',').Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..]))
            : string.IsNullOrEmpty(explore.Point.AdDomain) ? null : explore.Point.AdDomain;

    private static AdRestoreItemDto ToDto(AdRestoreItem i) => new(
        i.Guid, i.Dn, i.ObjectClass, i.State, i.Action, i.Outcome, i.Reason, i.LiveDn, i.NewGuid,
        [.. i.Steps.Select(s => new AdRestoreStepDto(s.Operation, s.Target, s.Text, s.Done))], [.. i.Warnings]);

    /// <summary>A user closes the explore session; not while one of its restores runs (it needs the backup's directory).</summary>
    public Task CloseByUserAsync(AdExplore explore, string user)
    {
        ArgumentNullException.ThrowIfNull(explore);
        if (Volatile.Read(ref explore.Restores) > 0)
        {
            throw new InvalidOperationException("A restore from this explore session is still running; close it when the restore has finished.");
        }

        return CloseAsync(explore, $"closed by {user}");
    }

    /// <summary>
    /// The agent restarted: its new process removed every copy at its start (journal), so its explore sessions end now
    /// instead of at their next query. Sessions still opening fail through their running job.
    /// </summary>
    public void AgentRestarted(Guid agentId)
    {
        foreach (var explore in _explores.Values.Where(e => e.Agent.Id == agentId && e.State == AdExploreStates.Ready).ToList())
        {
            explore.Error = $"The agent on {explore.Host} restarted (service restart, crash or VM reboot) and removed the backup's directory at its start. Open it again.";
            explore.State = AdExploreStates.Failed;
            explore.Prepared = false;
            explore.Mount = null;
            explore.Log.Warning(explore.Error);
            _ = Task.Run(() => CloseAsync(explore, "the agent restarted", SessionState.Failed));
        }
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
            List<AdDatabaseFile> files;
            await using (var opened = await access.OpenRestorePointAsync(point, stop).ConfigureAwait(false))
            {
                var reader = opened.Reader;
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
                await TransferAsync(explore, disks, plan!, folder, files[0].Volume, permissions: false, new Progress(explore), stop).ConfigureAwait(false);
                explore.Log.Info($"Copied {files.Count} file(s), {VmBackupTask.Format(plan!.Bytes)}, into {folder} on {explore.Host}; each checked with SHA-256.");
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

    /// <summary>Copies the planned files into <paramref name="folder"/> on the DC through the agent; every file checked with SHA-256.</summary>
    private async Task TransferAsync(
        AdExplore explore, IReadOnlyList<GuestDiskSource> disks, AgentTransferPlan plan, string folder, string volume, bool permissions, IFileRestoreObserver observer, CancellationToken stop)
    {
        if (plan.Failed > 0 || plan.Skipped > 0)
        {
            throw new InvalidOperationException($"{plan.Failed + plan.Skipped} item(s) for {folder} cannot be read from the restore point (links, junctions or damaged entries).");
        }

        var info = new FileTransferInfo
        {
            TransferId = Guid.NewGuid(),
            Roots = [folder],
            Overwrite = false,
            IfExists = FileTransferIfExists.Fail,
            RestorePermissions = permissions,
            ItemCount = plan.Items.Count,
            TotalBytes = plan.Bytes,
        };
        using var reservation = await FileRestoreSessionRunner.ReserveAgentAsync(
            registry, explore.Agent.Id, explore.Host, explore.Log, agentOptions.Value.WorkWaitLimit, time, stop).ConfigureAwait(false);
        using var transfer = new AgentTransfer(explore.Agent.Id, info, plan, disks, volume, observer, time);
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
        List<string> missing = [.. Enumerable.Range(0, plan.Items.Count).Where(i => plan.Sources[i] is not null && !transfer.Verified[i]).Select(i => plan.Descriptions[i])];
        if (missing.Count > 0 || report?.Error is not null)
        {
            throw new InvalidOperationException(
                $"Copying to {folder} on {explore.Host} failed ({report?.Error ?? $"{missing.Count} file(s) not written or not as in the backup: {string.Join(", ", missing.Take(5))}"}).");
        }
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
                explore.Error = $"The domain controller {explore.Host} no longer serves the backup's directory ({result.Error?.TrimEnd('.')}). Open it again.";
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
        new(e.Guid, e.Dn, e.LiveDn, e.Name, e.ObjectClass, e.State, e.SamAccountName, e.DisplayName, e.Description, e.Container, e.ChangedInBackup, e.ChangedLive);

    private static AdValueDto ToDto(AdValue v) => new(v.Text, v.Guid);

    private sealed class Quiet : IFileRestoreObserver
    {
        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes)
        {
        }

        public void Copied(long bytes)
        {
        }

        public void FileRestored(long files)
        {
        }

        public void Renamed(string source, string target)
        {
        }

        public void Problem(FileRestoreProblem problem)
        {
        }
    }

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
