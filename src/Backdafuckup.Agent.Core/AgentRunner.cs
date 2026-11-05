using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The agent's main loop: enroll once with the enrollment secret, then long-poll the server, execute its commands in the
/// background (a thaw must arrive while a freeze is held) and apply self-updates. Any error: log, wait, try again.
/// </summary>
public sealed class AgentRunner
{
    private readonly AgentPaths _paths;
    private readonly IHostInfoSource _host;
    private readonly AgentLog _log;
    private readonly CommandExecutor _executor;
    private readonly Func<AgentConfig, HttpMessageHandler> _handlerFactory;
    private readonly TimeSpan _retryDelay;
    private readonly IRestoreFileSystem? _restoreFiles;
    private readonly ISqlEnvironment? _sql;
    private readonly IPublishHost? _publish;
    private readonly object _workLock = new object();
    private readonly ConcurrentDictionary<Guid, SqlJobRunner> _sqlJobs = new ConcurrentDictionary<Guid, SqlJobRunner>();
    private readonly SqlInstanceLocks _sqlLocks = new SqlInstanceLocks();
    private Task? _work;
    private string? _workName;
    private string? _postponedUpdate;
    private AgentConfig? _config;
    private AgentHostInfo? _hostInfo;
    private DateTime _hostInfoAt;
    private string? _failedUpdateHash;

    /// <param name="restoreFiles">File operations for file-level restores into this guest; null: the agent cannot restore files.</param>
    /// <param name="sql">The local SQL Server installation; null: the agent runs no SQL jobs.</param>
    /// <param name="publish">The guest's iSCSI initiator and disk management; null: the agent publishes nothing.</param>
    public AgentRunner(
        AgentPaths paths, IHostInfoSource host, IVssBridge vss, AgentLog log, Func<AgentConfig, HttpMessageHandler>? handlerFactory = null, TimeSpan? retryDelay = null,
        IRestoreFileSystem? restoreFiles = null, ISqlEnvironment? sql = null, IPublishHost? publish = null)
    {
        _restoreFiles = restoreFiles;
        _sql = sql;
        _publish = publish;
        _paths = paths;
        _host = host;
        _log = log;
        _executor = new CommandExecutor(vss, host, log);
        _handlerFactory = handlerFactory ?? (config => ServerCertificatePin.CreateHandler(config.ServerPins));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// What occupies the work slot, or null. One piece of work at a time (a file restore, later SQL jobs): they write into
    /// the guest and must not overlap, and the agent takes no self-update while one runs.
    /// </summary>
    public string? Busy
    {
        get
        {
            lock (_workLock)
            {
                if (_work is { IsCompleted: false })
                {
                    return _workName;
                }
            }

            // SQL jobs run beside the work slot, but a restart in the middle of an attach, an overwrite or an export would
            // break it: they count too (only the inventory just reads; the backup lane has its own queue and does not hold
            // back restores, see BackupLane).
            return _sqlJobs.Values.Where(j => j.Kind != SqlJobKinds.Inventory && !IsLane(j.Kind)).Select(j => $"SQL Server job {j.Kind ?? "(starting)"}").FirstOrDefault();
        }
    }

    /// <summary>A running log or full backup, or null. It does not block other work, but a self-update waits for it.</summary>
    public string? BackupLane =>
        _sqlJobs.Values.Where(j => IsLane(j.Kind)).Select(j => $"SQL Server {(j.Kind == SqlJobKinds.LogBackup ? "log" : "full")} backup").FirstOrDefault();

    private static bool IsLane(string? kind) => kind == SqlJobKinds.LogBackup || kind == SqlJobKinds.FullBackup;

    /// <summary>Random for this process: tells the server when the agent restarted (<see cref="AgentPollRequest.RunId"/>).</summary>
    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>Runs until <paramref name="stop"/>; returns <see cref="AgentProtocol.RestartExitCode"/> after a self-update.</summary>
    public async Task<int> RunAsync(CancellationToken stop)
    {
        AgentFiles.RemoveReplaced(_paths.BaseDirectory);
        if (_publish is not null)
        {
            try
            {
                PublishRunner.CleanupStale(_publish, _log);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _log.Write($"Cleaning up publishes left over from before the restart failed: {ex.Message}");
            }
        }

        if (_restoreFiles is not null)
        {
            try
            {
                // A database overwrite interrupted by a crash or restart: its own files go back in place.
                SqlReplace.RecoverFiles(_restoreFiles, _paths.BaseDirectory, _log.Write);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _log.Write($"Recovering an interrupted SQL Server overwrite failed: {ex.Message}");
            }

            try
            {
                // A restore interrupted while it wrote a file: its temporary file is still there.
                TemporaryFileJournal.Sweep(_restoreFiles, _paths.BaseDirectory, _log.Write);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _log.Write($"Deleting the temporary files of an interrupted restore failed: {ex.Message}");
            }
        }

        try
        {
            return await LoopAsync(stop).ConfigureAwait(false);
        }
        finally
        {
            // A publish removes its mounts and iSCSI session when it sees the stop; give it the time for that.
            Task? work;
            lock (_workLock)
            {
                work = _work;
            }

            if (work is { IsCompleted: false })
            {
                await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(25))).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var config = ReadConfig();
                _config = config;
                using (var http = new HttpClient(_handlerFactory(config)) { BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) })
                {
                    http.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, await GetOrEnrollAsync(http, config, stop).ConfigureAwait(false));
                    var exit = await PollAsync(http, stop).ConfigureAwait(false);
                    if (exit is int code)
                    {
                        return code;
                    }
                }
            }
            catch (Exception ex)
            {
                if (stop.IsCancellationRequested)
                {
                    // Stopping: whatever failed meanwhile does not matter any more.
                    break;
                }

                _log.Write($"Error: {Describe(ex)} Retrying in {_retryDelay.TotalSeconds:0} s.");
                await DelayAsync(_retryDelay, stop).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private AgentConfig ReadConfig()
    {
        if (!File.Exists(_paths.ConfigFile))
        {
            throw new InvalidOperationException($"{_paths.ConfigFile} is missing: reinstall the agent from the backup server.");
        }

        var config = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(_paths.ConfigFile, Encoding.UTF8), AgentJson.Options);
        if (config is null || string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            throw new InvalidOperationException($"{_paths.ConfigFile} contains no server address.");
        }

        return config;
    }

    private async Task<string> GetOrEnrollAsync(HttpClient http, AgentConfig config, CancellationToken stop)
    {
        if (File.Exists(_paths.KeyFile))
        {
            return File.ReadAllText(_paths.KeyFile).Trim();
        }

        if (string.IsNullOrEmpty(config.EnrollmentSecret))
        {
            throw new InvalidOperationException("Not enrolled and no enrollment secret in agent.json: reinstall the agent from the backup server.");
        }

        var request = new AgentEnrollRequest { EnrollmentSecret = config.EnrollmentSecret!, Host = HostInfo(force: true) };
        using (var response = await http.PostAsync(AgentProtocol.EnrollPath, Json(request), stop).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The server {http.BaseAddress} rejected the enrollment ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            var enrolled = JsonSerializer.Deserialize<AgentEnrollResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options)!;
            File.WriteAllText(_paths.KeyFile, enrolled.AgentKey);
            _log.Write($"Enrolled at {http.BaseAddress} as agent {enrolled.AgentId}.");
            return enrolled.AgentKey;
        }
    }

    /// <summary>Polls until stopped; null: the key is no longer valid (enroll again); a number: exit code.</summary>
    private async Task<int?> PollAsync(HttpClient http, CancellationToken stop)
    {
        _log.Write($"Connected to {http.BaseAddress}.");
        while (!stop.IsCancellationRequested)
        {
            using (var response = await http.PostAsync(AgentProtocol.PollPath, Json(new AgentPollRequest { Host = HostInfo(force: false), Busy = Busy, RunId = RunId }), stop).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // The server forgot this agent (removed in the console): enroll again with the enrollment secret.
                    _log.Write("The server no longer knows this agent's key; enrolling again.");
                    File.Delete(_paths.KeyFile);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Poll at {http.BaseAddress} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    continue;
                }

                var poll = JsonSerializer.Deserialize<AgentPollResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options);
                if (poll?.Update is { } update && (Busy ?? BackupLane) is { } busy)
                {
                    // A server that does not know about the work (e.g. restarted meanwhile) offered an update: it waits.
                    if (_postponedUpdate != update.FilesHash)
                    {
                        _log.Write($"Update to version {update.Version} postponed: {busy}.");
                        _postponedUpdate = update.FilesHash;
                    }

                    await DelayAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                    continue;
                }

                if (poll?.Update is { } offer)
                {
                    if (await UpdateAsync(http, offer, stop).ConfigureAwait(false))
                    {
                        return AgentProtocol.RestartExitCode;
                    }

                    // The server answers an outdated agent at once instead of holding the poll: do not hammer it.
                    await DelayAsync(_retryDelay, stop).ConfigureAwait(false);
                    continue;
                }

                if (poll?.Command is { } command)
                {
                    // In the background: a freeze blocks until our provider holds, and the thaw arrives with the next poll.
                    _ = Task.Run(() => ExecuteAsync(http, command, stop));
                }
            }
        }

        return 0;
    }

    private async Task ExecuteAsync(HttpClient http, AgentCommand command, CancellationToken stop)
    {
        var result = command.Kind switch
        {
            AgentCommandKinds.RestoreFiles => await StartTransferAsync(http, command, stop).ConfigureAwait(false),
            AgentCommandKinds.SqlJob => StartSqlJob(http, command, stop),
            AgentCommandKinds.Publish => await StartPublishAsync(http, command, stop).ConfigureAwait(false),
            _ => _executor.Execute(command),
        };
        for (var attempt = 1; attempt <= 3 && !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                using (var response = await http.PostAsync(AgentProtocol.ResultPath, Json(result), stop).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _log.Write($"Result of {command.Kind} not delivered ({ex.Message}).");
            }
            catch (TaskCanceledException)
            {
            }

            await DelayAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accepts a file restore and runs it in the background on its own connection (no total timeout: one file can take
    /// long; same certificate pin). One transfer at a time.
    /// </summary>
    private async Task<AgentCommandResult> StartTransferAsync(HttpClient http, AgentCommand command, CancellationToken stop)
    {
        var result = new AgentCommandResult { CommandId = command.Id };
        if (_restoreFiles is null || _config is null)
        {
            result.Error = "This agent cannot restore files.";
            return result;
        }

        var client = new HttpClient(_handlerFactory(_config)) { BaseAddress = http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, http.DefaultRequestHeaders.GetValues(AgentProtocol.KeyHeader));
        var transfer = new FileRestoreTransfer(client, command.TransferId, _restoreFiles, _log, new TemporaryFileJournal(_paths.BaseDirectory));
        var busy = await StartWorkAsync($"file restore {command.TransferId}", async () =>
        {
            using (client)
            {
                await transfer.RunAsync(stop).ConfigureAwait(false);
            }
        }, stop).ConfigureAwait(false);
        if (busy is not null)
        {
            client.Dispose();
            result.Error = $"This guest is busy ({busy}); the server queues the restore until it has finished.";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Accepts a publish and runs it in the work slot until the server says stop: the published disks are in the guest
    /// for that long, and nothing else may write into it meanwhile.
    /// </summary>
    private async Task<AgentCommandResult> StartPublishAsync(HttpClient http, AgentCommand command, CancellationToken stop)
    {
        var result = new AgentCommandResult { CommandId = command.Id };
        if (_publish is null || _config is null)
        {
            result.Error = "This agent cannot publish restore points.";
            return result;
        }

        var client = new HttpClient(_handlerFactory(_config)) { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, http.DefaultRequestHeaders.GetValues(AgentProtocol.KeyHeader));
        var publish = new PublishRunner(client, command.PublishId, _publish, _log, _sql);
        var busy = await StartWorkAsync($"publish {command.PublishId}", async () =>
        {
            using (client)
            {
                await publish.RunAsync(stop).ConfigureAwait(false);
            }
        }, stop).ConfigureAwait(false);
        if (busy is not null)
        {
            client.Dispose();
            result.Error = $"This guest is busy ({busy}); the server queues the publish until it has finished.";
            return result;
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Accepts a SQL job and runs it in the background on its own connection. Jobs that only read (the inventory) run
    /// beside other work; jobs that change the guest will take the work slot.
    /// </summary>
    private AgentCommandResult StartSqlJob(HttpClient http, AgentCommand command, CancellationToken stop)
    {
        var result = new AgentCommandResult { CommandId = command.Id };
        if (_sql is null || _config is null)
        {
            result.Error = "This agent cannot run SQL Server jobs.";
            return result;
        }

        var client = new HttpClient(_handlerFactory(_config)) { BaseAddress = http.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, http.DefaultRequestHeaders.GetValues(AgentProtocol.KeyHeader));
        var job = new SqlJobRunner(client, command.JobId, _sql, _log, _restoreFiles, _paths.BaseDirectory, _sqlLocks);
        _sqlJobs[command.JobId] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                using (client)
                {
                    await job.RunAsync(stop).ConfigureAwait(false);
                }
            }
            finally
            {
                _sqlJobs.TryRemove(command.JobId, out _);
            }

            // A database left attached from a publish kept its disk in use; with it gone, the journal's cleanup can finish
            // now instead of at the next start. Only in a free work slot: a running publish has its own journal entry.
            if (job.Succeeded && job.Kind == SqlJobKinds.DropPublished && _publish is { } publishHost && !stop.IsCancellationRequested)
            {
                await StartWorkAsync("cleanup of left-over publishes", () => Task.Run(() => PublishRunner.CleanupStale(publishHost, _log)), stop).ConfigureAwait(false);
            }
        });
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in the work slot; returns null when it started, or what holds the slot. Work that is
    /// just finishing (the server already has its final report) is waited for briefly instead of refusing.
    /// </summary>
    private async Task<string?> StartWorkAsync(string name, Func<Task> work, CancellationToken stop)
    {
        Task? previous;
        lock (_workLock)
        {
            previous = _work;
        }

        if (previous is { IsCompleted: false })
        {
            await Task.WhenAny(previous, Task.Delay(TimeSpan.FromSeconds(5), stop)).ConfigureAwait(false);
        }

        lock (_workLock)
        {
            if (_work is { IsCompleted: false })
            {
                return _workName;
            }

            _workName = name;
            _work = Task.Run(work);
            return null;
        }
    }

    /// <summary>
    /// Downloads the offered files into the staging folder, checks size and SHA-256, then swaps them in (files in use are
    /// renamed to *.old). Returns true if the agent must restart.
    /// </summary>
    private async Task<bool> UpdateAsync(HttpClient http, AgentUpdateOffer offer, CancellationToken stop)
    {
        if (offer.FilesHash == _failedUpdateHash)
        {
            return false;
        }

        _log.Write($"Update to version {offer.Version} ({offer.Build}, {offer.Files.Count} file(s)).");
        var staging = _paths.StagingDirectory;
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        Directory.CreateDirectory(staging);
        foreach (var file in offer.Files)
        {
            if (file.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !AgentFiles.IsProgramFile(file.Name))
            {
                throw new InvalidOperationException($"Update rejected: invalid file name '{file.Name}'.");
            }

            var target = Path.Combine(staging, file.Name);
            using (var response = await http.GetAsync($"{AgentProtocol.FilesPath}/{offer.Build}/{Uri.EscapeDataString(file.Name)}", stop).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var output = File.Create(target))
                {
                    await response.Content.CopyToAsync(output).ConfigureAwait(false);
                }
            }

            if (new FileInfo(target).Length != file.Size || AgentFiles.Sha256Of(target) != file.Sha256)
            {
                throw new InvalidOperationException($"Update rejected: {file.Name} does not match its checksum.");
            }
        }

        foreach (var file in offer.Files)
        {
            var target = Path.Combine(_paths.BaseDirectory, file.Name);
            if (File.Exists(target))
            {
                var old = target + ".old";
                if (File.Exists(old))
                {
                    File.Delete(old);
                }

                File.Move(target, old);
            }

            File.Move(Path.Combine(staging, file.Name), target);
        }

        Directory.Delete(staging, true);
        var installed = AgentFiles.Hash(AgentFiles.List(_paths.BaseDirectory));
        if (installed != offer.FilesHash)
        {
            // Files the server does not ship are lying around; do not loop on the same update.
            _failedUpdateHash = offer.FilesHash;
            _log.Write($"After the update the files differ from the server's build ({installed} vs {offer.FilesHash}); not updating again.");
            return false;
        }

        _log.Write("Update installed; restarting.");
        return true;
    }

    private AgentHostInfo HostInfo(bool force)
    {
        if (force || _hostInfo is null || DateTime.UtcNow - _hostInfoAt > TimeSpan.FromMinutes(10))
        {
            _hostInfo = _host.Collect();
            if (_sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlRestore))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlRestore);
            }

            if (_publish is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilityPublish))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilityPublish);
            }

            if (_publish is not null && _sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlPublish))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlPublish);
            }

            if (_restoreFiles is not null && _sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlAttachFiles))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlAttachFiles);
            }

            if (_sql is not null && _restoreFiles is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlRestoreChain))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlRestoreChain);
            }

            if (_sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlBackupChain))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlBackupChain);
            }

            if (_sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlSnapshot))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlSnapshot);
            }

            if (_restoreFiles is not null && _sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlReplaceFiles))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlReplaceFiles);
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlReplaceRecovery);
            }

            if (_sql is not null && !_hostInfo.Capabilities.Contains(AgentProtocol.CapabilitySqlBackup))
            {
                _hostInfo.Capabilities.Add(AgentProtocol.CapabilitySqlBackup);
            }

            if (_restoreFiles is not null)
            {
                foreach (var capability in new[] { AgentProtocol.CapabilityRestoreFiles, AgentProtocol.CapabilityRestoreFilesV2 })
                {
                    if (!_hostInfo.Capabilities.Contains(capability))
                    {
                        _hostInfo.Capabilities.Add(capability);
                    }
                }
            }

            _hostInfoAt = DateTime.UtcNow;
        }

        return _hostInfo;
    }

    private static StringContent Json(object value) =>
        new StringContent(JsonSerializer.Serialize(value, value.GetType(), AgentJson.Options), Encoding.UTF8, "application/json");

    private static string Describe(Exception ex)
    {
        var message = ex.Message.TrimEnd('.') + ".";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            message += " " + inner.Message.TrimEnd('.') + ".";
        }

        return message;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
    }
}
