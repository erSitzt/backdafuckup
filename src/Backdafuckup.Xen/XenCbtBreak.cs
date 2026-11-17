using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Backdafuckup.Xen.Xapi;

namespace Backdafuckup.Xen;

/// <summary>
/// Why a VM's change tracking chain may have broken: what XAPI recorded since the previous backup's snapshot. On XCP-ng a
/// migration of the VM, also within the pool (for example when hosts are evacuated for updates), can reset changed block
/// tracking; then <c>VDI.list_changed_blocks</c> calls the previous tracking data "unrelated" to the new snapshot.
/// </summary>
internal static partial class XenCbtBreak
{
    private const int Shown = 6;

    /// <summary>
    /// A log line with the VM's migrations (<c>message.get</c>, VM_MIGRATED) and the hosts' restarts (<c>other_config</c>
    /// <c>boot_time</c>) after <paramref name="since"/>, or that XAPI recorded none.
    /// </summary>
    public static async Task<string> DescribeAsync(XapiSession xapi, string vmUuid, string vmName, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var events = new List<(DateTimeOffset At, string Text)>();
        if (await xapi.CallAsync("message.get", cancellationToken, "VM", vmUuid, XapiTime(since)).ConfigureAwait(false) is JsonObject messages)
        {
            foreach (var (_, node) in messages)
            {
                if (node is JsonObject message && message.Str("name") == "VM_MIGRATED" && message.Time("timestamp") is { } at && at >= since)
                {
                    var hosts = MigratedHosts().Match(message.Str("body"));
                    events.Add((at, hosts.Success
                        ? $"{vmName} was migrated from host {hosts.Groups["from"].Value} to host {hosts.Groups["to"].Value}"
                        : $"{vmName} was migrated"));
                }
            }
        }

        foreach (var host in (await xapi.GetAllRecordsAsync("host", cancellationToken).ConfigureAwait(false)).Values)
        {
            if (BootTime(host) is { } boot && boot > since)
            {
                events.Add((boot, $"host {host.Str("name_label")} was restarted"));
            }
        }

        var window = $"since the previous backup's snapshot ({Utc(since)})";
        if (events.Count == 0)
        {
            return $"Changed block tracking of {vmName}: XAPI recorded no migration of the VM and no host restart {window} " +
                "(VM messages and host boot times; XAPI keeps only a limited number of messages). Other causes: the disk was moved " +
                "to another storage, or changed block tracking was turned off and on again.";
        }

        var ordered = events.OrderBy(e => e.At).ToList();
        var list = string.Join("; ", ordered.Take(Shown).Select(e => $"{e.Text} at {Utc(e.At)}"));
        var more = ordered.Count > Shown ? $"; and {ordered.Count - Shown} more" : string.Empty;
        return $"Changed block tracking of {vmName}: {window} XAPI recorded: {list}{more}. On XCP-ng a migration, also within the " +
            "pool (for example when hosts are evacuated for updates), can reset changed block tracking; then this backup reads the " +
            "disks in full, and the backups after it are incremental again.";
    }

    /// <summary>A host's last boot: <c>other_config</c> <c>boot_time</c>, seconds since 1970 (e.g. "1762505438.").</summary>
    internal static DateTimeOffset? BootTime(JsonObject host) =>
        host.Map("other_config").TryGetValue("boot_time", out var text) &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : null;

    private static string XapiTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // "VM 'app01' (uuid: …) migrated from host 'xcp-host-2' (uuid: …) to host 'xcp-host-1' (uuid: …)"
    [GeneratedRegex(@"from host '(?<from>[^']*)'.*?to host '(?<to>[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex MigratedHosts();
}
