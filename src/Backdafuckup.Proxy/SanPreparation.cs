using System.Text.RegularExpressions;

namespace Backdafuckup.Proxy;

/// <summary>
/// <c>backdafuckup-proxy prepare-san</c>: makes the settings the SAN self-check asks for, each only where it is safe,
/// and says what it did. An admin runs it as root on the proxy; the agent itself never changes system settings.
/// <list type="bullet">
/// <item>iSCSI initiator name: starts iscsid once (Debian and Ubuntu generate the name then), else writes one from
/// <c>iscsi-iname</c>.</item>
/// <item>LVM: <c>activation/auto_activation_volume_list</c> in lvmlocal.conf with the machine's own volume groups, so a
/// volume group on an array snapshot never activates; refused while iSCSI sessions exist (a backup may be reading).</item>
/// <item>multipathd and udisks2: stopped and masked; multipathd only when the machine has no multipath devices.</item>
/// </list>
/// </summary>
public sealed partial class SanPreparation(Action<string> output, CommandRunner? run = null, string etcRoot = "/etc", bool? privileged = null)
{
    private readonly CommandRunner _run = run ?? Commands.RunAsync;

    private string InitiatorFile => Path.Combine(etcRoot, "iscsi", "initiatorname.iscsi");

    private string LvmLocalFile => Path.Combine(etcRoot, "lvm", "lvmlocal.conf");

    /// <summary>0 when SAN access is ready afterwards, 1 when something is left (to do by hand, or with a dry run), 2 without root.</summary>
    public async Task<int> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun && !(privileged ?? Environment.IsPrivilegedProcess))
        {
            output("prepare-san changes system settings: run it as root (sudo backdafuckup-proxy prepare-san), or with --dry-run to only look.");
            return 2;
        }

        output(dryRun ? "SAN access of this proxy (dry run, nothing is changed):" : "Preparing this proxy for SAN access (backup from storage snapshots):");
        await InitiatorAsync(dryRun, cancellationToken).ConfigureAwait(false);
        await LvmAsync(dryRun, cancellationToken).ConfigureAwait(false);
        await MultipathAsync(dryRun, cancellationToken).ConfigureAwait(false);
        await UdisksAsync(dryRun, cancellationToken).ConfigureAwait(false);

        // The same check the agent makes.
        var initiator = new IscsiadmInitiator(_run, etcRoot);
        var problems = new List<string>();
        if (await initiator.InitiatorNameAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            problems.Add(await initiator.InitiatorProblemAsync(cancellationToken).ConfigureAwait(false) ?? "no iSCSI initiator name");
        }

        problems.AddRange(await SanSelfCheck.CheckAsync(_run, cancellationToken, etcRoot).ConfigureAwait(false));
        if (problems.Count == 0)
        {
            output("SAN access is ready. The agent tells the server within a minute; the console then shows the initiator name.");
            return 0;
        }

        output(dryRun ? $"{Count(problems.Count)} for SAN access (prepare-san without --dry-run makes what it can):" : $"{Count(problems.Count)} for SAN access:");
        foreach (var problem in problems)
        {
            output($"  - {problem}");
        }

        return 1;
    }

    private static string Count(int problems) => problems == 1 ? "1 thing is missing" : $"{problems} things are missing";

    private void Report(string state, string text) => output($"  [{state}] {text}");

    private async Task InitiatorAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var initiator = new IscsiadmInitiator(_run, etcRoot);
        var (iscsiadm, _) = await _run("iscsiadm", ["--version"], cancellationToken).ConfigureAwait(false);
        if (iscsiadm == Commands.NotFound || !File.Exists(InitiatorFile))
        {
            Report("by hand", "open-iscsi is not installed: install it (apt install open-iscsi, or dnf install iscsi-initiator-utils), then run prepare-san again");
            return;
        }

        if (await initiator.InitiatorNameAsync(cancellationToken).ConfigureAwait(false) is { } name)
        {
            Report("ok", $"iSCSI initiator name {name}");
            return;
        }

        if (dryRun)
        {
            Report("would change", "start iscsid once, so that it generates the initiator name");
            return;
        }

        var (started, startOutput) = await _run("systemctl", ["start", "iscsid.service"], cancellationToken).ConfigureAwait(false);
        if (await initiator.InitiatorNameAsync(cancellationToken).ConfigureAwait(false) is { } generated)
        {
            Report("changed", $"started iscsid, which generated the initiator name {generated}: list it in the proxy's host object on the array");
            return;
        }

        // Not Debian or Ubuntu: iscsid generates nothing. iscsi-iname (part of open-iscsi) makes a name.
        var (iname, fresh) = await _run("iscsi-iname", [], cancellationToken).ConfigureAwait(false);
        if (iname == 0 && fresh.Trim() is { } freshName && freshName.StartsWith("iqn.", StringComparison.Ordinal) && !freshName.Contains('\n', StringComparison.Ordinal))
        {
            var backup = InitiatorFile + ".bdfu-backup";
            File.Copy(InitiatorFile, backup, overwrite: true);
            await File.WriteAllTextAsync(InitiatorFile, $"InitiatorName={freshName}\n", cancellationToken).ConfigureAwait(false);
            await _run("systemctl", ["try-restart", "iscsid.service"], cancellationToken).ConfigureAwait(false);
            Report("changed", $"wrote the initiator name {freshName} to {InitiatorFile} (the old file is {backup}): list it in the proxy's host object on the array");
            return;
        }

        Report("by hand", $"{InitiatorFile} names no initiator, and neither iscsid ({(started == 0 ? "started" : $"systemctl start failed ({started}): {startOutput.Trim()}")}) " +
                          $"nor iscsi-iname ({(iname == Commands.NotFound ? "not installed" : $"exit {iname}")}) made one: write a line InitiatorName=iqn.… into it");
    }

    private async Task LvmAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var (ok, problem) = await SanSelfCheck.LvmAsync(_run, etcRoot, cancellationToken).ConfigureAwait(false);
        if (ok is not null)
        {
            Report("ok", ok);
            return;
        }

        // While a snapshot LUN is attached, its volume group would land in the list.
        var (sessions, sessionList) = await _run("iscsiadm", ["-m", "session"], cancellationToken).ConfigureAwait(false);
        if (sessions == 0 && sessionList.Trim().Length > 0)
        {
            Report("by hand", $"{problem}. This machine has iSCSI sessions ({FirstLine(sessionList)}), so a backup may be reading an array snapshot: " +
                              "run prepare-san again when no backup uses the proxy");
            return;
        }

        var (vgs, vgOutput) = await _run("vgs", ["--noheadings", "-o", "vg_name"], cancellationToken).ConfigureAwait(false);
        if (vgs != 0)
        {
            Report("by hand", $"{problem}. vgs failed ({vgs}): {vgOutput.Trim()}");
            return;
        }

        // Warnings (on stderr) have spaces; volume group names do not. An SR's volume group is never local.
        var names = vgOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => VolumeGroupName().IsMatch(n) && !n.StartsWith("VG_XenStorage-", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var list = names.Count == 0 ? "[ ]" : $"[ {string.Join(", ", names.Select(n => $"\"{n}\""))} ]";
        var original = File.Exists(LvmLocalFile) ? await File.ReadAllTextAsync(LvmLocalFile, cancellationToken).ConfigureAwait(false) : null;
        var rest = WithoutOurBlock(original ?? string.Empty);
        if (ActivationSection().IsMatch(rest))
        {
            Report("by hand", $"{problem}. {LvmLocalFile} has an activation section already: add auto_activation_volume_list = {list} to it");
            return;
        }

        if (dryRun)
        {
            Report("would change", $"write activation/auto_activation_volume_list = {list} into {LvmLocalFile}");
            return;
        }

        var block = $"{SanSelfCheck.LvmBlockBegin}: only these volume groups activate by themselves, never one found on an array snapshot.\n" +
                    $"# Add a volume group made later on this machine, or it does not activate at boot.\n" +
                    $"activation {{\n\tauto_activation_volume_list = {list}\n}}\n{SanSelfCheck.LvmBlockEnd}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(LvmLocalFile)!);
        await File.WriteAllTextAsync(LvmLocalFile, (rest.Length == 0 ? string.Empty : rest.TrimEnd('\n') + "\n\n") + block, cancellationToken).ConfigureAwait(false);
        var (valid, validation) = await _run("lvmconfig", ["--validate"], cancellationToken).ConfigureAwait(false);
        var (nowOk, _) = await SanSelfCheck.LvmAsync(_run, etcRoot, cancellationToken).ConfigureAwait(false);
        if (valid != 0 || nowOk is null)
        {
            if (original is null)
            {
                File.Delete(LvmLocalFile);
            }
            else
            {
                await File.WriteAllTextAsync(LvmLocalFile, original, cancellationToken).ConfigureAwait(false);
            }

            Report("by hand", $"{problem}. LVM did not take auto_activation_volume_list = {list} ({(valid != 0 ? validation.Trim() : "lvmconfig does not show it")}); {LvmLocalFile} is as it was");
            return;
        }

        Report("changed", $"{nowOk}, written to {LvmLocalFile}; a volume group made later on this machine has to be added there");
    }

    private async Task MultipathAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (await SanSelfCheck.MultipathProblemAsync(_run, cancellationToken).ConfigureAwait(false) is not { } problem)
        {
            Report("ok", "multipathd does not run");
            return;
        }

        var (maps, mapList) = await _run("multipath", ["-l", "-v1"], cancellationToken).ConfigureAwait(false);
        if (maps != 0 || mapList.Trim().Length > 0)
        {
            Report("by hand", maps == 0
                ? $"multipathd runs and this machine uses multipath devices ({string.Join(", ", mapList.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}): " +
                  "it stays; SAN access stays off on this machine (the proxy reads array snapshots as plain SCSI disks)"
                : $"{problem}. multipath -l failed ({maps}): {mapList.Trim()}");
            return;
        }

        await MaskAsync("multipathd (no multipath devices on this machine)", ["multipathd.service", "multipathd.socket"], dryRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task UdisksAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (await SanSelfCheck.UdisksProblemAsync(_run, cancellationToken).ConfigureAwait(false) is null)
        {
            Report("ok", "udisks2 does not run");
            return;
        }

        await MaskAsync("udisks2", ["udisks2.service"], dryRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaskAsync(string what, IReadOnlyList<string> units, bool dryRun, CancellationToken cancellationToken)
    {
        var command = $"systemctl mask --now {string.Join(' ', units)}";
        if (dryRun)
        {
            Report("would change", $"stop and mask {what}: {command}");
            return;
        }

        var (code, result) = await _run("systemctl", ["mask", "--now", .. units], cancellationToken).ConfigureAwait(false);
        Report(code == 0 ? "changed" : "by hand", code == 0 ? $"stopped and masked {what} ({command}; systemctl unmask undoes it)" : $"{command} failed ({code}): {result.Trim()}");
    }

    /// <summary>The file without the block an earlier run wrote.</summary>
    public static string WithoutOurBlock(string text)
    {
        var begin = text.IndexOf(SanSelfCheck.LvmBlockBegin, StringComparison.Ordinal);
        var end = begin < 0 ? -1 : text.IndexOf(SanSelfCheck.LvmBlockEnd, begin, StringComparison.Ordinal);
        return begin < 0 || end < 0 ? text : (text[..begin] + text[(end + SanSelfCheck.LvmBlockEnd.Length)..].TrimStart('\n')).TrimEnd('\n') is { Length: > 0 } rest ? rest + "\n" : string.Empty;
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    [GeneratedRegex("^[A-Za-z0-9+_.-]+$")]
    private static partial Regex VolumeGroupName();

    [GeneratedRegex(@"^\s*activation\s*\{", RegexOptions.Multiline)]
    private static partial Regex ActivationSection();
}
