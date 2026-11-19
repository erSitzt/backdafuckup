using System.Diagnostics;

namespace Backdafuckup.Proxy;

/// <summary>Runs a program and returns its exit code and output (stdout and stderr); replaced in tests.</summary>
public delegate Task<(int ExitCode, string Output)> CommandRunner(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>The machine's iSCSI initiator: logging in to an array's target, so its snapshot LUNs appear as block devices.</summary>
public interface IIscsiInitiator
{
    /// <summary>This machine's initiator name (the array's host object must list it), or null without an initiator.</summary>
    Task<string?> InitiatorNameAsync(CancellationToken cancellationToken);

    /// <summary>Why there is no initiator name (what to do about it); null when there is one.</summary>
    Task<string?> InitiatorProblemAsync(CancellationToken cancellationToken);

    /// <summary>Discovers the target at the portals and logs in once (never at boot: node.startup stays manual).</summary>
    Task LoginAsync(IReadOnlyList<string> portals, string targetIqn, CancellationToken cancellationToken);

    /// <summary>Asks the iSCSI sessions for new LUNs (a snapshot just presented).</summary>
    Task RescanAsync(CancellationToken cancellationToken);
}

/// <summary>open-iscsi through <c>iscsiadm</c> (no initiator of our own).</summary>
public sealed class IscsiadmInitiator(CommandRunner? run = null, string etcRoot = "/etc") : IIscsiInitiator
{
    /// <summary>iscsiadm's exit codes for "already logged in" and "no active sessions".</summary>
    private const int SessionExists = 15;
    private const int NoSessions = 21;

    private readonly CommandRunner _run = run ?? Commands.RunAsync;

    private string InitiatorFile => Path.Combine(etcRoot, "iscsi", "initiatorname.iscsi");

    public async Task<string?> InitiatorNameAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(InitiatorFile))
        {
            return null;
        }

        foreach (var line in await File.ReadAllLinesAsync(InitiatorFile, cancellationToken).ConfigureAwait(false))
        {
            var text = line.Trim();
            if (text.StartsWith("InitiatorName=", StringComparison.Ordinal) && text["InitiatorName=".Length..].Trim() is { Length: > 0 } name)
            {
                return name;
            }
        }

        return null;
    }

    public async Task<string?> InitiatorProblemAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(InitiatorFile))
        {
            return $"no iSCSI initiator (open-iscsi) is installed: {InitiatorFile} is missing";
        }

        if (await InitiatorNameAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            return null;
        }

        var lines = await File.ReadAllLinesAsync(InitiatorFile, cancellationToken).ConfigureAwait(false);
        return lines.Any(l => l.Trim().Equals("GenerateName=yes", StringComparison.OrdinalIgnoreCase))
            // Debian and Ubuntu write this at install; iscsid's first start replaces it with a generated name.
            ? $"open-iscsi is installed, but has no initiator name yet ({InitiatorFile} says GenerateName=yes): start iscsid once (systemctl start iscsid)"
            : $"{InitiatorFile} names no initiator (a line InitiatorName=iqn.…)";
    }

    public async Task LoginAsync(IReadOnlyList<string> portals, string targetIqn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portals);
        var errors = new List<string>();
        var loggedIn = 0;
        foreach (var portal in portals)
        {
            var (discovered, discovery) = await _run("iscsiadm", ["-m", "discovery", "-t", "sendtargets", "-p", portal, "-o", "new"], cancellationToken).ConfigureAwait(false);
            if (discovered != 0 || !discovery.Contains(targetIqn, StringComparison.Ordinal))
            {
                errors.Add($"{portal}: discovery {(discovered != 0 ? $"failed ({discovered}): {discovery.Trim()}" : $"did not list {targetIqn}")}");
                continue;
            }

            await _run("iscsiadm", ["-m", "node", "-T", targetIqn, "-p", portal, "-o", "update", "-n", "node.startup", "-v", "manual"], cancellationToken).ConfigureAwait(false);
            var (code, output) = await _run("iscsiadm", ["-m", "node", "-T", targetIqn, "-p", portal, "--login"], cancellationToken).ConfigureAwait(false);
            if (code is 0 or SessionExists)
            {
                loggedIn++;
            }
            else
            {
                errors.Add($"{portal}: login failed ({code}): {output.Trim()}");
            }
        }

        if (loggedIn == 0)
        {
            throw new InvalidOperationException($"No iSCSI login to {targetIqn}: {string.Join("; ", errors)}");
        }
    }

    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await _run("iscsiadm", ["-m", "session", "--rescan"], cancellationToken).ConfigureAwait(false);
        if (code is not (0 or NoSessions))
        {
            throw new InvalidOperationException($"iscsiadm -m session --rescan failed ({code}): {output.Trim()}");
        }

        await _run("udevadm", ["settle", "--timeout=30"], cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Whether this machine may see an array's snapshot LUNs: the system's LVM must leave foreign physical volumes alone (a
/// devices file or a global filter, and no event activation), or it would activate the snapshot's volume groups, and no
/// auto-mounter may run. Until then the proxy offers no SAN access.
/// </summary>
public static class SanSelfCheck
{
    /// <summary>Null if SAN access is safe; otherwise why not, with what to set.</summary>
    public static async Task<string?> CheckAsync(CommandRunner? run, CancellationToken cancellationToken)
    {
        run ??= Commands.RunAsync;
        var (code, output) = await run("lvmconfig", ["--typeconfig", "full", "devices/use_devicesfile", "devices/global_filter", "global/event_activation"], cancellationToken)
            .ConfigureAwait(false);
        if (code == Commands.NotFound)
        {
            // No LVM tools: nothing would activate a volume group.
        }
        else if (code != 0)
        {
            return $"lvmconfig failed ({code}): {output.Trim()}";
        }
        else
        {
            var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
            var devicesFile = values.GetValueOrDefault("use_devicesfile") == "1";
            var filter = values.GetValueOrDefault("global_filter") is { Length: > 2 } f && f.Contains("r|", StringComparison.Ordinal);
            if (!devicesFile && !filter)
            {
                return "LVM would scan every disk, also an array snapshot's: set devices/use_devicesfile=1 with only the local disks in /etc/lvm/devices/system.devices, " +
                       "or a devices/global_filter that rejects the others.";
            }

            if (values.GetValueOrDefault("event_activation") != "0")
            {
                return "LVM activates volume groups as disks appear: set global/event_activation=0 in /etc/lvm/lvm.conf.";
            }
        }

        var (udisks, _) = await run("systemctl", ["is-active", "--quiet", "udisks2"], cancellationToken).ConfigureAwait(false);
        return udisks == 0 ? "udisks2 runs and may mount file systems of new disks: stop and disable it (systemctl disable --now udisks2)." : null;
    }
}

/// <summary>Running a program for the proxy (iscsiadm, lvmconfig, systemctl).</summary>
public static class Commands
{
    /// <summary>The exit code reported when the program does not exist.</summary>
    public const int NotFound = 127;

    public static async Task<(int ExitCode, string Output)> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (NotFound, $"{program} is not installed");
        }

        if (process is null)
        {
            return (NotFound, $"{program} could not be started");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
        }
    }
}
