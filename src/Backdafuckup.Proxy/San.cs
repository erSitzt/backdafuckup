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
/// Whether this machine may see an array's snapshot LUNs. LVM must not activate the volume groups it finds on one (an
/// XCP-ng SR holds one), multipathd must not claim the LUN (the proxy finds it by its serial as a plain SCSI disk), and
/// udisks2 must not run. Until all of that holds the proxy offers no SAN access; <c>backdafuckup-proxy prepare-san</c>
/// sets it (<see cref="SanPreparation"/>).
/// </summary>
public static class SanSelfCheck
{
    /// <summary>The marker of the block <see cref="SanPreparation"/> writes into lvmlocal.conf.</summary>
    public const string LvmBlockBegin = "# backdafuckup-proxy prepare-san (begin)";
    public const string LvmBlockEnd = "# backdafuckup-proxy prepare-san (end)";

    /// <summary>Every reason why SAN access is not safe yet, each with what to set; empty if it is.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(CommandRunner? run, CancellationToken cancellationToken, string etcRoot = "/etc")
    {
        run ??= Commands.RunAsync;
        var problems = new List<string>();
        if ((await LvmAsync(run, etcRoot, cancellationToken).ConfigureAwait(false)).Problem is { } lvm)
        {
            problems.Add(lvm);
        }

        if (await MultipathProblemAsync(run, cancellationToken).ConfigureAwait(false) is { } multipath)
        {
            problems.Add(multipath);
        }

        if (await UdisksProblemAsync(run, cancellationToken).ConfigureAwait(false) is { } udisks)
        {
            problems.Add(udisks);
        }

        return problems;
    }

    /// <summary>
    /// How LVM is kept off foreign volume groups (for the report), or why it is not: a devices file in use (new disks are
    /// not even scanned), an accept list as global filter, or a defined <c>activation/auto_activation_volume_list</c>
    /// (only the listed local volume groups activate by themselves). <c>global/event_activation=0</c> is no answer: since
    /// LVM 2.03.15 nothing activates volume groups at boot then.
    /// </summary>
    internal static async Task<(string? Ok, string? Problem)> LvmAsync(CommandRunner run, string etcRoot, CancellationToken cancellationToken)
    {
        var (devicesCode, devicesFile) = await run("lvmconfig", ["--typeconfig", "full", "devices/use_devicesfile"], cancellationToken).ConfigureAwait(false);
        if (devicesCode == Commands.NotFound)
        {
            return ("LVM is not installed: nothing activates volume groups", null);
        }

        // Older LVM (before 2.03.12) does not know the setting: no devices file then.
        var devicesFilePath = Path.Combine(etcRoot, "lvm", "devices", "system.devices");
        if (devicesCode == 0 && Value(devicesFile) == "1" && File.Exists(devicesFilePath))
        {
            return ($"LVM uses only the disks in its devices file ({devicesFilePath})", null);
        }

        var (listCode, list) = await run("lvmconfig", ["activation/auto_activation_volume_list"], cancellationToken).ConfigureAwait(false);
        if (listCode == 0 && Value(list) is { } listed && listed.StartsWith('[') && !listed.Contains("\"@*\"", StringComparison.Ordinal))
        {
            return ($"LVM activates by itself only {(listed == "[]" ? "no volume group" : listed)} (activation/auto_activation_volume_list)", null);
        }

        var (filterCode, filter) = await run("lvmconfig", ["--typeconfig", "full", "devices/global_filter"], cancellationToken).ConfigureAwait(false);
        if (filterCode == 0 && Value(filter) is { } patterns && patterns.Replace(" ", string.Empty, StringComparison.Ordinal).EndsWith("\"r|.*|\"]", StringComparison.Ordinal))
        {
            return ($"LVM sees only the disks its global filter accepts ({patterns})", null);
        }

        return (null, "LVM would activate the volume groups it finds on an array snapshot (an XCP-ng SR holds one): " +
                      "list the local ones in activation/auto_activation_volume_list (an empty list if there are none), " +
                      "or use a devices file with only the local disks");
    }

    internal static async Task<string?> MultipathProblemAsync(CommandRunner run, CancellationToken cancellationToken)
    {
        var (service, _) = await run("systemctl", ["is-active", "--quiet", "multipathd.service"], cancellationToken).ConfigureAwait(false);
        var (socket, _) = await run("systemctl", ["is-active", "--quiet", "multipathd.socket"], cancellationToken).ConfigureAwait(false);
        return service == 0 || socket == 0
            ? "multipathd runs: it would claim a snapshot LUN reached over two iSCSI paths, and the proxy would not find it by its serial; " +
              "stop and mask it (systemctl mask --now multipathd.service multipathd.socket) if this machine uses no multipath devices"
            : null;
    }

    internal static async Task<string?> UdisksProblemAsync(CommandRunner run, CancellationToken cancellationToken)
    {
        var (udisks, _) = await run("systemctl", ["is-active", "--quiet", "udisks2.service"], cancellationToken).ConfigureAwait(false);
        return udisks == 0 ? "udisks2 runs and may mount file systems of new disks: stop and mask it (systemctl mask --now udisks2.service)" : null;
    }

    /// <summary>The value of lvmconfig's "name=value" line.</summary>
    private static string? Value(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2)?[1].Trim();
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
