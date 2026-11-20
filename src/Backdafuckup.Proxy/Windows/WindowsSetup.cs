namespace Backdafuckup.Proxy.Windows;

/// <summary>
/// Installs the Windows proxy agent as a service, and removes it: the program in its own folder, its key and log in a
/// folder only SYSTEM and Administrators may read, the service with restart on failure (how the agent comes back after a
/// self-update: it stops with exit code 3). Through <see cref="CommandRunner"/> (sc.exe), so the steps are tested on Linux.
/// </summary>
public sealed class WindowsSetup(CommandRunner run, Action<string> output, string programDirectory, string dataDirectory, Action<string>? protectDirectory = null)
{
    public const string ServiceName = "backdafuckup-proxy";

    public const string DisplayName = "backdafuckup backup proxy";

    /// <summary>sc.exe's exit code when the service does not exist.</summary>
    private const int ServiceMissing = 1060;

    /// <summary>sc.exe's exit code when the service runs already.</summary>
    private const int AlreadyRunning = 1056;

    public string ProgramPath => Path.Combine(programDirectory, ProxySelfUpdate.WindowsProgramName);

    /// <summary>How long a started service has to keep running before the install counts as done.</summary>
    public TimeSpan Watch { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Stops an installed agent, puts <paramref name="sourceProgram"/> in place, runs <paramref name="enroll"/> (which
    /// writes the key into the data folder), creates or updates the service and starts it. Run again, it replaces.
    /// </summary>
    public async Task<int> InstallAsync(string sourceProgram, Func<CancellationToken, Task<int>> enroll, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        ArgumentNullException.ThrowIfNull(enroll);
        await StopAsync(cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(programDirectory);
        if (!string.Equals(Path.GetFullPath(sourceProgram), Path.GetFullPath(ProgramPath), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceProgram, ProgramPath + ".new", overwrite: true);
            File.Move(ProgramPath + ".new", ProgramPath, overwrite: true);
        }

        if (protectDirectory is not null)
        {
            protectDirectory(dataDirectory);
        }
        else
        {
            Directory.CreateDirectory(dataDirectory);
        }

        var enrolled = await enroll(cancellationToken).ConfigureAwait(false);
        if (enrolled != 0)
        {
            return enrolled;
        }

        var bin = $"\"{ProgramPath}\" run";
        var (exists, _) = await QueryAsync(cancellationToken).ConfigureAwait(false);
        var (code, said) = exists
            ? await Sc(cancellationToken, "config", ServiceName, "binPath=", bin, "start=", "delayed-auto").ConfigureAwait(false)
            : await Sc(cancellationToken, "create", ServiceName, "binPath=", bin, "start=", "delayed-auto", "obj=", "LocalSystem", "DisplayName=", DisplayName).ConfigureAwait(false);
        if (code != 0)
        {
            output($"The service {ServiceName} could not be {(exists ? "updated" : "created")} (sc.exe {code}): {said.Trim()}");
            return 1;
        }

        await Sc(cancellationToken, "description", ServiceName, "Reads storage-snapshot LUNs for backdafuckup backups, read-only.").ConfigureAwait(false);
        await EnsureRecoveryAsync(run, cancellationToken).ConfigureAwait(false);
        (code, said) = await Sc(cancellationToken, "start", ServiceName).ConfigureAwait(false);
        if (code is not (0 or AlreadyRunning))
        {
            output($"The service {ServiceName} did not start (sc.exe {code}): {said.Trim()}. Its log: {Path.Combine(dataDirectory, "proxy.log")}");
            return 1;
        }

        if (!await StaysRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            output($"The service {ServiceName} does not keep running; see {Path.Combine(dataDirectory, "proxy.log")} and the event log.");
            return 1;
        }

        output($"Installed and started: {ProgramPath}. The proxy shows as online in the console within seconds; its log: {Path.Combine(dataDirectory, "proxy.log")}");
        return 0;
    }

    /// <summary>
    /// Stops and removes the service, the key and the log; the program folder too, or right after this process ended when
    /// it runs from there (<paramref name="removeLater"/>).
    /// </summary>
    public async Task<int> UninstallAsync(string? runningProgram, Action<string> removeLater, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(removeLater);
        await StopAsync(cancellationToken).ConfigureAwait(false);
        var (exists, _) = await QueryAsync(cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            var (code, said) = await Sc(cancellationToken, "delete", ServiceName).ConfigureAwait(false);
            if (code != 0)
            {
                output($"The service {ServiceName} could not be removed (sc.exe {code}): {said.Trim()}");
                return 1;
            }
        }

        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        if (Directory.Exists(programDirectory))
        {
            if (runningProgram is not null && Path.GetFullPath(runningProgram).StartsWith(Path.GetFullPath(programDirectory), StringComparison.OrdinalIgnoreCase))
            {
                removeLater(programDirectory);
            }
            else
            {
                Directory.Delete(programDirectory, recursive: true);
            }
        }

        output($"Removed: the service {ServiceName}, {dataDirectory} and {programDirectory}.");
        return 0;
    }

    /// <summary>Restart on failure, quickly the first time: how the agent comes back after a self-update (exit code 3).</summary>
    public static async Task EnsureRecoveryAsync(CommandRunner run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await run(ScPath, ["failure", ServiceName, "reset=", "86400", "actions=", "restart/10000/restart/10000/restart/60000"], cancellationToken).ConfigureAwait(false);
        await run(ScPath, ["failureflag", ServiceName, "1"], cancellationToken).ConfigureAwait(false);
    }

    private static string ScPath => OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "sc.exe") : "sc.exe";

    private Task<(int ExitCode, string Output)> Sc(CancellationToken cancellationToken, params string[] arguments) => run(ScPath, arguments, cancellationToken);

    private async Task<(bool Exists, string State)> QueryAsync(CancellationToken cancellationToken)
    {
        var (code, said) = await Sc(cancellationToken, "query", ServiceName).ConfigureAwait(false);
        return code == ServiceMissing ? (false, string.Empty)
            : said.Contains("RUNNING", StringComparison.Ordinal) ? (true, "RUNNING")
            : said.Contains("STOPPED", StringComparison.Ordinal) ? (true, "STOPPED")
            : (code == 0, "OTHER");
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var (exists, state) = await QueryAsync(cancellationToken).ConfigureAwait(false);
        if (!exists || state == "STOPPED")
        {
            return;
        }

        output($"Stopping the installed agent ({ServiceName}) ...");
        await Sc(cancellationToken, "stop", ServiceName).ConfigureAwait(false);
        for (var i = 0; i < 60; i++)
        {
            if ((await QueryAsync(cancellationToken).ConfigureAwait(false)).State == "STOPPED")
            {
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new IOException($"The service {ServiceName} did not stop within 30 s.");
    }

    private async Task<bool> StaysRunningAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Watch;
        do
        {
            var (_, state) = await QueryAsync(cancellationToken).ConfigureAwait(false);
            if (state == "STOPPED")
            {
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return state == "RUNNING";
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, Watch.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }
}
