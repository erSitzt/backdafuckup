using System;
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
    private AgentHostInfo? _hostInfo;
    private DateTime _hostInfoAt;
    private string? _failedUpdateHash;

    public AgentRunner(AgentPaths paths, IHostInfoSource host, IVssBridge vss, AgentLog log, Func<AgentConfig, HttpMessageHandler>? handlerFactory = null, TimeSpan? retryDelay = null)
    {
        _paths = paths;
        _host = host;
        _log = log;
        _executor = new CommandExecutor(vss, host, log);
        _handlerFactory = handlerFactory ?? (config => ServerCertificatePin.CreateHandler(config.ServerPins));
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Runs until <paramref name="stop"/>; returns <see cref="AgentProtocol.RestartExitCode"/> after a self-update.</summary>
    public async Task<int> RunAsync(CancellationToken stop)
    {
        AgentFiles.RemoveReplaced(_paths.BaseDirectory);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var config = ReadConfig();
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
            using (var response = await http.PostAsync(AgentProtocol.PollPath, Json(new AgentPollRequest { Host = HostInfo(force: false) }), stop).ConfigureAwait(false))
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
                if (poll?.Update is { } update)
                {
                    if (await UpdateAsync(http, update, stop).ConfigureAwait(false))
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
        var result = _executor.Execute(command);
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
