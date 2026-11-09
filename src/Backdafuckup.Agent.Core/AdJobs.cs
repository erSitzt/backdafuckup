using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The backups' directories this DC serves with dsamain, one per explore session. Each lives in a work folder that holds
/// every password hash of the domain, so the folder is always deleted: at unmount, after the idle time, and at the next
/// agent start from the journal (<c>ad-mounts.json</c>) if the agent stopped in between.
/// </summary>
public sealed class AdMounts
{
    private const long SpareBytes = 1L << 30;

    private readonly IAdEnvironment _env;
    private readonly AgentLog _log;
    private readonly string _journalFile;
    private readonly object _lock = new object();
    private readonly Dictionary<Guid, MountState> _mounts = new Dictionary<Guid, MountState>();

    /// <summary>Journal entries of earlier runs whose cleanup failed; tried again with every idle sweep.</summary>
    private readonly List<JournalEntry> _stale = new List<JournalEntry>();
    private readonly Func<DateTime> _now;

    /// <param name="journalDirectory">The agent's folder (holds <c>ad-mounts.json</c>).</param>
    public AdMounts(IAdEnvironment env, AgentLog log, string journalDirectory, Func<DateTime>? now = null)
    {
        _env = env;
        _log = log;
        _journalFile = Path.Combine(journalDirectory, "ad-mounts.json");
        _now = now ?? (() => DateTime.UtcNow);
    }

    public IAdEnvironment Environment => _env;

    /// <summary>What is mounted (the agent postpones a self-update while it is), or null.</summary>
    public string? Active
    {
        get
        {
            lock (_lock)
            {
                return _mounts.Count == 0 ? null : $"{_mounts.Count} Active Directory explore session(s)";
            }
        }
    }

    /// <summary>
    /// At agent start: stops the dsamain processes and deletes the work folders that the journal lists, and any other
    /// work folder (named by a session id) left in the work root.
    /// </summary>
    public void SweepAtStart()
    {
        lock (_lock)
        {
            _stale.AddRange(ReadJournal());
        }

        SweepStale("left over from before the agent restarted");
        foreach (var folder in _env.WorkFolders().Where(f => Guid.TryParse(f.Split('\\', '/').Last(), out _)))
        {
            try
            {
                Delete(folder, "left over in the work root");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lock (_lock)
                {
                    _stale.Add(new JournalEntry { Folder = folder });
                    WriteJournal();
                }
            }
        }
    }

    private void SweepStale(string reason)
    {
        List<JournalEntry> stale;
        lock (_lock)
        {
            stale = _stale.ToList();
        }

        foreach (var entry in stale)
        {
            try
            {
                Remove(entry, reason);
                lock (_lock)
                {
                    _stale.Remove(entry);
                    WriteJournal();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Logged by Delete; stays in the journal for the next try.
            }
        }
    }

    /// <summary>Unmounts what no job used for its idle time (the server lost track of it, or its session ended badly).</summary>
    public void SweepIdle()
    {
        SweepStale("left over, tried again");
        List<MountState> idle;
        lock (_lock)
        {
            idle = _mounts.Values.Where(m => _now() - m.LastUsed > m.Idle).ToList();
        }

        foreach (var mount in idle)
        {
            try
            {
                Unmount(mount.Id, $"unused for {mount.Idle.TotalMinutes:0} minutes");
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Stays in the list: the next sweep tries again.
                _log.Write($"AD explore {mount.Id}: unmounting after the idle time failed: {ex.Message}");
            }
        }
    }

    public AdPrepareInfo Prepare(Guid mountId, long requiredBytes, Action<string> log)
    {
        var folder = _env.Join(_env.WorkRoot, mountId.ToString("D"));
        lock (_lock)
        {
            if (_mounts.ContainsKey(mountId))
            {
                throw new InvalidOperationException($"AD explore session {mountId} is already prepared on {_env.HostName}.");
            }

            if (_env.FolderExists(folder))
            {
                _env.DeleteFolder(folder);
            }

            _env.CreateWorkFolder(folder);
            var free = _env.FreeBytes(folder);
            var needed = requiredBytes + Math.Max(SpareBytes, requiredBytes / 10);
            if (free < needed)
            {
                _env.DeleteFolder(folder);
                throw new InvalidOperationException(
                    $"Not enough free space for the backup's directory on {_env.HostName} in {folder}: {Mib(free)} free, {Mib(needed)} needed (the database and its logs plus room for the recovery).");
            }

            var mount = new MountState(mountId, folder) { LastUsed = _now(), Idle = TimeSpan.FromMinutes(75) };
            _mounts[mountId] = mount;
            WriteJournal();
            log($"Work folder {folder} created on {_env.HostName} (only SYSTEM and Administrators), {Mib(free)} free.");
            return new AdPrepareInfo { Folder = folder, FreeBytes = free };
        }
    }

    /// <summary>Recovers the copied ntds.dit, starts dsamain and waits until it answers LDAP as the account.</summary>
    public AdMountInfo Mount(Guid mountId, AdAccount account, TimeSpan idle, TimeSpan startTimeout, Action<string> log, CancellationToken stop)
    {
        MountState mount;
        lock (_lock)
        {
            if (!_mounts.TryGetValue(mountId, out mount!))
            {
                throw new InvalidOperationException($"AD explore session {mountId} has no work folder on {_env.HostName}; prepare it first.");
            }

            if (mount.ProcessId != 0)
            {
                throw new InvalidOperationException($"AD explore session {mountId} is already mounted on {_env.HostName} (dsamain, port {mount.Port}).");
            }
        }

        var dit = _env.Join(mount.Folder, "ntds.dit");
        if (!_env.FileExists(dit))
        {
            throw new InvalidOperationException($"{dit} is missing on {_env.HostName}: the transfer of the backup's directory did not complete.");
        }

        // A database copied without logs (the copy ntdsutil IFM made before the snapshot) was shut down cleanly; esentutl /r
        // would only fail on the missing logs (-528, JET_errMissingLogFile).
        var withLogs = _env.FileExists(_env.Join(mount.Folder, "edb.chk")) || _env.FileExists(_env.Join(mount.Folder, "edb.log"));
        var recovery = withLogs ? _env.Recover(mount.Folder) : new AdToolResult { ExitCode = 0, Output = string.Empty };
        var recoveryText = !withLogs ? "not needed, the database was copied without logs (shut down cleanly)"
            : recovery.ExitCode == 0 ? "the database is consistent" : $"esentutl ended with {recovery.ExitCode}: {Tail(recovery.Output)}";
        log($"Recovery of the copied database: {recoveryText}.");

        var port = _env.FreePort();
        var server = _env.StartServer(dit, mount.Folder, port);
        lock (_lock)
        {
            mount.ProcessId = server.ProcessId;
            mount.Port = port;
            mount.Idle = idle;
            mount.LastUsed = _now();
            WriteJournal();
        }

        log($"dsamain started on {_env.HostName} (process {server.ProcessId}, LDAP port {port}).");
        try
        {
            var info = Serve(server, port, account, recovery, recoveryText, startTimeout, stop);
            log($"The backup's directory ({info.BackupDomainDn}) answers on {_env.HostName}:{port}; live: Recycle Bin {(info.RecycleBin ? "enabled" : "not enabled")}, tombstone lifetime {info.TombstoneLifetimeDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} days.");
            lock (_lock)
            {
                mount.Info = info;
            }

            return info;
        }
        catch
        {
            // Nothing keeps serving the hashes after a failed mount; the folder goes with the unmount (or the idle sweep).
            if (_env.IsServer(server.ProcessId))
            {
                _env.StopServer(server.ProcessId);
            }

            lock (_lock)
            {
                mount.ProcessId = 0;
                WriteJournal();
            }

            throw;
        }
    }

    private AdMountInfo Serve(IAdServer server, int port, AdAccount account, AdToolResult recovery, string recoveryText, TimeSpan startTimeout, CancellationToken stop)
    {
        var watch = Stopwatch.StartNew();
        AdRecord? root = null;
        while (root is null)
        {
            stop.ThrowIfCancellationRequested();
            if (server.HasExited)
            {
                throw new InvalidOperationException(
                    $"dsamain on {_env.HostName} (port {port}) ended with {server.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown code"} before it served the backup: {DsamainExit(server.Output)}" +
                    (recovery.ExitCode == 0 ? string.Empty : $" (the recovery reported: {Tail(recovery.Output)})"));
            }

            try
            {
                using (var backup = _env.Connect(port, account))
                {
                    root = backup.RootDse();
                }
            }
            catch (AdDirectoryException ex) when (ex.ResultCode == 49)
            {
                throw new InvalidOperationException($"The account {account.UserName} cannot log on to the backup's directory on {_env.HostName}:{port}: {ex.Message}", ex);
            }
            catch (AdDirectoryException ex)
            {
                if (watch.Elapsed > startTimeout)
                {
                    throw new InvalidOperationException($"dsamain on {_env.HostName} did not answer on port {port} within {startTimeout.TotalSeconds:0} s: {ex.Message}", ex);
                }

                Thread.Sleep(500);
            }
        }

        var info = new AdMountInfo
        {
            Port = port,
            ProcessId = server.ProcessId,
            LiveHost = _env.HostName,
            BackupDomainDn = root.Text("defaultNamingContext"),
            Recovery = recoveryText,
        };
        using (var live = _env.Connect(389, account))
        {
            var liveRoot = live.RootDse();
            info.LiveDomainDn = liveRoot.Text("defaultNamingContext");
            if (!string.Equals(info.BackupDomainDn, info.LiveDomainDn, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The backup is of domain {info.BackupDomainDn}, but {_env.HostName} is a domain controller of {info.LiveDomainDn}: choose a domain controller of the backup's domain.");
            }

            ReadLiveFacts(live, liveRoot, info);
        }

        // rootDSE answers anyone. dsamain (without /allowNonAdminAccess, which would open the backup to every account of the
        // domain that reaches the port) serves the data only to Domain Admins and Enterprise Admins; to anyone else each
        // search ends with operationsError (1, 000004DC "a successful bind must be completed").
        try
        {
            using (var backup = _env.Connect(port, account))
            {
                backup.Search(info.BackupDomainDn ?? string.Empty, "(objectClass=*)", AdScope.Base, new[] { "objectGUID" }, 1, null);
            }
        }
        catch (AdDirectoryException ex) when (ex.ResultCode == 1)
        {
            throw new InvalidOperationException(
                $"The account {account.UserName} may not read the backup's directory on {_env.HostName}:{port}: dsamain serves it only to Domain Admins and Enterprise Admins of the domain. Open it with such an account. ({ex.Message.TrimEnd('.')})", ex);
        }
        catch (AdDirectoryException ex)
        {
            throw new InvalidOperationException($"Reading the backup's directory on {_env.HostName}:{port} as {account.UserName} failed: {ex.Message}", ex);
        }

        return info;
    }

    /// <summary>Stops dsamain and deletes the work folder; a folder that cannot be deleted is an error naming its path.</summary>
    public void Unmount(Guid mountId, string reason)
    {
        MountState? mount;
        lock (_lock)
        {
            _mounts.TryGetValue(mountId, out mount);
        }

        if (mount is null)
        {
            // Nothing in memory: the agent restarted, and its start already swept the journal.
            var folder = _env.Join(_env.WorkRoot, mountId.ToString("D"));
            if (_env.FolderExists(folder))
            {
                Delete(folder, reason);
            }

            return;
        }

        lock (_lock)
        {
            // No more queries: the server process stops now.
            mount.Info = null;
        }

        Remove(new JournalEntry { MountId = mount.Id, Folder = mount.Folder, ProcessId = mount.ProcessId, Port = mount.Port, PolicyFolders = mount.PolicyFolders.ToList() }, reason);
        lock (_lock)
        {
            _mounts.Remove(mountId);
            WriteJournal();
        }
    }

    /// <summary>The mounted session (refreshing its idle time); throws if it is not mounted here.</summary>
    public (AdMountInfo Info, AdSchema? Schema, Action<AdSchema> KeepSchema) Use(Guid mountId)
    {
        lock (_lock)
        {
            if (!_mounts.TryGetValue(mountId, out var mount) || mount.Info is null)
            {
                throw new AdNotMountedException($"AD explore session {mountId} is not mounted on {_env.HostName} (it was unmounted after its idle time or the agent restarted).");
            }

            mount.LastUsed = _now();
            return (mount.Info, mount.Schema, schema => mount.Schema = schema);
        }
    }

    private static void ReadLiveFacts(IAdDirectory live, AdRecord root, AdMountInfo info)
    {
        var configuration = root.Text("configurationNamingContext");
        if (configuration is null)
        {
            return;
        }

        try
        {
            var service = live.Search("CN=Directory Service,CN=Windows NT,CN=Services," + configuration, "(objectClass=*)", AdScope.Base,
                new[] { "tombstoneLifetime", "msDS-DeletedObjectLifetime" }, 1, null).Records.FirstOrDefault();
            info.TombstoneLifetimeDays = int.TryParse(service?.Text("tombstoneLifetime"), out var tombstone) ? tombstone : 60;
            info.DeletedObjectLifetimeDays = int.TryParse(service?.Text("msDS-DeletedObjectLifetime"), out var deleted) ? deleted : info.TombstoneLifetimeDays;
            var feature = live.Search("CN=Recycle Bin Feature,CN=Optional Features,CN=Directory Service,CN=Windows NT,CN=Services," + configuration,
                "(objectClass=*)", AdScope.Base, new[] { "msDS-EnabledFeatureBL" }, 1, null).Records.FirstOrDefault();
            info.RecycleBin = feature?.Values("msDS-EnabledFeatureBL").Count > 0;
        }
        catch (AdDirectoryException)
        {
            // Not fatal: the states come from the objects themselves.
        }
    }

    /// <summary>Notes a GPO folder in SYSVOL that the restore created or set aside (cleaned up at unmount and agent start).</summary>
    public void TrackPolicyFolder(Guid mountId, string folder, bool add)
    {
        lock (_lock)
        {
            if (!_mounts.TryGetValue(mountId, out var mount))
            {
                return;
            }

            mount.PolicyFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (add)
            {
                mount.PolicyFolders.Add(folder);
            }

            WriteJournal();
        }
    }

    /// <summary>
    /// GPO folders of an interrupted restore: a staging folder is deleted; a set-aside folder goes back if the policy has
    /// no folder now, else it stays (named in the log) for the admin to check, as it holds a policy's earlier files.
    /// </summary>
    private void RemovePolicyFolders(JournalEntry entry, string reason)
    {
        foreach (var folder in entry.PolicyFolders)
        {
            if (!_env.FolderExists(folder))
            {
                continue;
            }

            if (folder.EndsWith(".bdfu-new", StringComparison.OrdinalIgnoreCase))
            {
                _env.DeletePolicyFolder(folder);
                _log.Write($"Deleted the GPO staging folder {folder} ({reason}).");
            }
            else if (folder.EndsWith(".bdfu-replaced", StringComparison.OrdinalIgnoreCase))
            {
                var original = folder.Substring(0, folder.Length - ".bdfu-replaced".Length);
                if (!_env.FolderExists(original))
                {
                    _env.MovePolicyFolder(folder, original);
                    _log.Write($"Moved the policy's previous files back from {folder} to {original} ({reason}).");
                }
                else
                {
                    _log.Write($"Warning: {folder} holds a policy's previous files from an interrupted restore; {original} exists. Check both and delete one by hand.");
                }
            }
        }
    }

    private void Remove(JournalEntry entry, string reason)
    {
        RemovePolicyFolders(entry, reason);
        if (entry.ProcessId != 0 && _env.IsServer(entry.ProcessId))
        {
            _env.StopServer(entry.ProcessId);
            _log.Write($"AD explore {entry.MountId}: dsamain (process {entry.ProcessId}, port {entry.Port}) stopped ({reason}).");
        }

        if (_env.FolderExists(entry.Folder))
        {
            Delete(entry.Folder, reason);
        }
    }

    private void Delete(string folder, string reason)
    {
        // A dsamain that just ended may hold the files for a moment.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _env.DeleteFolder(folder);
                _log.Write($"Deleted the work folder {folder} with the backup's directory ({reason}).");
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(1000 * attempt);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.Write($"Error: the work folder {folder} on {_env.HostName} could not be deleted ({reason}): {ex.Message} It holds the domain's password hashes; delete it by hand.");
                throw;
            }
        }
    }

    private List<JournalEntry> ReadJournal()
    {
        try
        {
            return File.Exists(_journalFile) ? JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(_journalFile), AgentJson.Options) ?? new List<JournalEntry>() : new List<JournalEntry>();
        }
        catch (JsonException)
        {
            return new List<JournalEntry>();
        }
    }

    private void WriteJournal()
    {
        var entries = _mounts.Values
            .Select(m => new JournalEntry { MountId = m.Id, Folder = m.Folder, ProcessId = m.ProcessId, Port = m.Port, PolicyFolders = m.PolicyFolders.ToList() })
            .Concat(_stale).ToList();
        if (entries.Count == 0)
        {
            if (File.Exists(_journalFile))
            {
                File.Delete(_journalFile);
            }

            return;
        }

        var temporary = _journalFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, AgentJson.Options), Encoding.UTF8);
        if (File.Exists(_journalFile))
        {
            File.Delete(_journalFile);
        }

        File.Move(temporary, _journalFile);
    }

    /// <summary>
    /// Why dsamain ended: the events it printed (localized lines <c>EVENTLOG (…): source / category : id</c>), with an
    /// explanation for those we know, and its last lines.
    /// </summary>
    internal static string DsamainExit(string output)
    {
        var ids = Regex.Matches(output ?? string.Empty, @"^\s*EVENTLOG \([^)]*\):.*:\s*(\d+)\s*$", RegexOptions.Multiline)
            .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
        var text = ids.Count == 0 ? Tail(output ?? string.Empty) : $"events {string.Join(", ", ids)}; {Tail(output ?? string.Empty)}";

        // On a VM with a VM generation id (event 2168) dsamain compares it with msDS-GenerationId of the backup's DC object
        // and does not start when it cannot read it (2173): the backup is from another DC whose VM had no generation id.
        return ids.Contains("2173")
            ? "it runs on a hypervisor with a VM generation id and could not read msDS-GenerationId of the domain controller the backup is from " +
              $"(that DC had none, e.g. its VM was made from a template without one). Open the backup on the domain controller it is from, or on one without a VM generation id ({text})"
            : text;
    }

    private static string Tail(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? "no output" : string.Join(" ", lines.Skip(Math.Max(0, lines.Count - 4)));
    }

    private static string Mib(long bytes) => $"{bytes / (1024 * 1024):N0} MiB";

    public sealed class JournalEntry
    {
        public Guid MountId { get; set; }

        public string Folder { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        public int Port { get; set; }

        /// <summary>GPO staging (<c>.bdfu-new</c>) and replaced (<c>.bdfu-replaced</c>) folders in SYSVOL of a restore in progress.</summary>
        public List<string> PolicyFolders { get; set; } = new List<string>();
    }

    private sealed class MountState
    {
        public MountState(Guid id, string folder)
        {
            Id = id;
            Folder = folder;
        }

        public Guid Id { get; }

        public string Folder { get; }

        public int ProcessId { get; set; }

        public int Port { get; set; }

        public DateTime LastUsed { get; set; }

        public TimeSpan Idle { get; set; }

        public AdMountInfo? Info { get; set; }

        public AdSchema? Schema { get; set; }

        public List<string> PolicyFolders { get; } = new List<string>();
    }
}

/// <summary>The explore session is not mounted on this DC.</summary>
public sealed class AdNotMountedException : InvalidOperationException
{
    public AdNotMountedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Runs one Active Directory job: fetches the request (<c>GET api/agent/ad-jobs/{id}</c>), does it, posts the result. The
/// account's password is only in memory and never logged.
/// </summary>
public sealed class AdJobRunner
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    private readonly AdMounts _mounts;
    private readonly AgentLog _log;

    public AdJobRunner(HttpClient http, Guid id, AdMounts mounts, AgentLog log)
    {
        _http = http;
        _id = id;
        _mounts = mounts;
        _log = log;
    }

    /// <summary>How long dsamain may take to answer after its start.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public string? Kind { get; private set; }

    private string Base => AgentProtocol.AdJobsPath + "/" + _id.ToString("D");

    public async Task RunAsync(CancellationToken stop)
    {
        var result = new AdJobResult { JobId = _id };
        try
        {
            AdJobRequest request;
            using (var response = await _http.GetAsync(Base, stop).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"The server did not hand out AD job {_id} ({(int)response.StatusCode}).");
                }

                request = JsonSerializer.Deserialize<AdJobRequest>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options)
                    ?? throw new InvalidOperationException("The AD job request is empty.");
            }

            Kind = request.Kind;
            void Log(string line)
            {
                _log.Write($"AD job {_id}: {line}");
                result.Log.Add(line);
            }

            if (request.Kind is not (AdJobKinds.Search or AdJobKinds.Tree or AdJobKinds.Object or AdJobKinds.Plan))
            {
                _log.Write($"AD job {_id}: {request.Kind} of explore session {request.MountId}{(request.Account is { } account ? $" as {account.UserName}" : string.Empty)}.");
            }

            await Task.Run(() => Run(request, result, Log, stop), stop).ConfigureAwait(false);
            result.Success = true;
        }
        catch (Exception ex) when (!(ex is OutOfMemoryException) && !stop.IsCancellationRequested)
        {
            result.Error = ex.Message;
            result.NotMounted = ex is AdNotMountedException;
            _log.Write($"AD job {_id} failed: {ex.Message}");
        }

        if (stop.IsCancellationRequested)
        {
            return;
        }

        using (var content = new StringContent(JsonSerializer.Serialize(result, AgentJson.Options), Encoding.UTF8, "application/json"))
        using (var response = await _http.PostAsync(Base + "/result", content, stop).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                _log.Write($"AD job {_id}: the server did not take the result ({(int)response.StatusCode}).");
            }
        }
    }

    private void Run(AdJobRequest request, AdJobResult result, Action<string> log, CancellationToken stop)
    {
        switch (request.Kind)
        {
            case AdJobKinds.Prepare:
                result.Prepare = _mounts.Prepare(request.MountId, request.RequiredBytes, log);
                return;
            case AdJobKinds.Mount:
                result.Mount = _mounts.Mount(request.MountId, Account(request), TimeSpan.FromMinutes(Math.Max(5, request.IdleMinutes)), StartTimeout, log, stop);
                return;
            case AdJobKinds.Unmount:
                _mounts.Unmount(request.MountId, "the explore session ended");
                log($"The backup's directory of explore session {request.MountId} is unmounted and its work folder deleted on {_mounts.Environment.HostName}.");
                return;
        }

        var (info, schema, keepSchema) = _mounts.Use(request.MountId);
        var account = Account(request);
        using (var backup = _mounts.Environment.Connect(info.Port, account))
        using (var live = _mounts.Environment.Connect(389, account))
        {
            AdGpoRestorer Gpos() => new AdGpoRestorer(_mounts.Environment, backup, live, info.LiveDomainDn ?? info.BackupDomainDn ?? string.Empty, Schema(), info.RecycleBin,
                (folder, add) => _mounts.TrackPolicyFolder(request.MountId, folder, add));

            AdSchema Schema()
            {
                if (schema is null)
                {
                    schema = AdSchema.Load(live);
                    keepSchema(schema);
                }

                return schema;
            }

            var queries = new AdQueries(backup, live, info.LiveDomainDn ?? info.BackupDomainDn ?? string.Empty, info.RecycleBin);
            switch (request.Kind)
            {
                case AdJobKinds.Tree:
                    result.Entries = queries.Tree(request.BaseDn);
                    break;
                case AdJobKinds.Search:
                    var (entries, cookie, scanned) = queries.Search(request);
                    result.Entries = entries;
                    result.Cookie = cookie is null ? null : Convert.ToBase64String(cookie);
                    result.Scanned = scanned;
                    break;
                case AdJobKinds.Object:
                    result.Object = queries.Object(request.ObjectGuid, Schema());
                    break;
                case AdJobKinds.GpoPrepare:
                    result.GpoStaging = Gpos().Prepare(request.Gpos, log);
                    break;
                case AdJobKinds.Plan:
                case AdJobKinds.Restore:
                    var write = request.Kind == AdJobKinds.Restore;
                    if (write)
                    {
                        log($"Restoring {request.Objects.Count} object(s){(request.Gpos.Count > 0 ? $" and {request.Gpos.Count} group policy(ies)" : string.Empty)} into {live.Endpoint} as {account.UserName}" +
                            $"{(string.IsNullOrEmpty(request.NewPassword) ? string.Empty : " (with a password entered for accounts without one)")}.");
                    }

                    var restorer = new AdRestorer(backup, live, info.LiveDomainDn ?? info.BackupDomainDn ?? string.Empty, Schema(), info.RecycleBin);
                    result.Restore = request.Objects.Count > 0 ? restorer.Run(request, write, log) : new List<AdRestoreItem>();
                    if (request.Gpos.Count > 0)
                    {
                        result.Restore.AddRange(Gpos().Run(request, write, log));
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown AD job kind '{request.Kind}'.");
            }
        }
    }

    private static AdAccount Account(AdJobRequest request) =>
        request.Account is { } account && account.UserName.Length > 0 ? account : throw new InvalidOperationException("The AD job names no account.");
}
