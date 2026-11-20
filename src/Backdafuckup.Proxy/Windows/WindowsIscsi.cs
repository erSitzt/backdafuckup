using System.Globalization;
using System.Text.Json;

namespace Backdafuckup.Proxy.Windows;

/// <summary>
/// The Microsoft iSCSI initiator as the Windows proxy uses it: only the server's existing sessions. The proxy never logs in
/// or out and never changes an iSCSI or MPIO setting; a LUN presented to the server's host object appears after a rescan.
/// </summary>
public sealed class WindowsIscsiInitiator(PowerShellRunner powershell, Func<string, int, CancellationToken, Task>? connect = null, Func<IReadOnlyList<string>>? ownAddresses = null)
    : IIscsiInitiator
{
    private readonly Func<IReadOnlyList<string>> _ownAddresses = ownAddresses ?? NetworkAddresses.Ipv4;

    public async Task<string?> InitiatorNameAsync(CancellationToken cancellationToken) => (await StateAsync(cancellationToken).ConfigureAwait(false)).Node;

    public async Task<string?> InitiatorProblemAsync(CancellationToken cancellationToken)
    {
        var state = await StateAsync(cancellationToken).ConfigureAwait(false);
        return state.Service != "Running"
            ? $"the Microsoft iSCSI initiator service (MSiSCSI) is {(state.Service is { Length: > 0 } s ? s.ToLowerInvariant() : "missing")}: the proxy reads the array's LUNs through the server's existing iSCSI sessions"
            : state.Node is null
                ? "the Microsoft iSCSI initiator has no node name"
                : null;
    }

    public Task<IReadOnlyList<ProxyPortalReach>> ProbeAsync(IReadOnlyList<string> portals, CancellationToken cancellationToken) =>
        IscsiPortals.ProbeAsync(portals, connect, cancellationToken);

    /// <summary>Checks that a connected session to the target exists; logs in to nothing.</summary>
    public async Task LoginAsync(IReadOnlyList<string> portals, string targetIqn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portals);
        var reach = await ProbeAsync(portals, cancellationToken).ConfigureAwait(false);
        if (reach.All(r => !r.Reachable))
        {
            throw new InvalidOperationException(IscsiPortals.NoneReachable(reach, _ownAddresses()));
        }

        var (code, output) = await powershell(
            $"@(Get-IscsiSession -ErrorAction SilentlyContinue | Where-Object {{ $_.TargetNodeAddress -eq {PowerShell.Quote(targetIqn)} -and $_.IsConnected }}).Count",
            cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"Get-IscsiSession failed ({code}): {output}");
        }

        if (!int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessions) || sessions == 0)
        {
            throw new InvalidOperationException(
                $"this server has no iSCSI session to {targetIqn}: connect the array's target as for its other LUNs (with MPIO); " +
                "the Windows proxy uses the existing sessions only and does not log in itself");
        }
    }

    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await powershell("Update-HostStorageCache", cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"Update-HostStorageCache failed ({code}): {output}");
        }
    }

    private async Task<(string? Service, string? Node)> StateAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await powershell(
            "$s = Get-Service MSiSCSI -ErrorAction SilentlyContinue; " +
            "$n = (Get-InitiatorPort -ErrorAction SilentlyContinue | Where-Object { $_.ConnectionType -eq 'iSCSI' } | Select-Object -First 1).NodeAddress; " +
            "if (-not $n) { $n = (Get-CimInstance -Namespace root\\wmi -ClassName MSiSCSIInitiator_MethodClass -ErrorAction SilentlyContinue).iSCSINodeName }; " +
            "[pscustomobject]@{ service = if ($s) { [string]$s.Status } else { $null }; node = $n } | ConvertTo-Json -Compress",
            cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            return (null, null);
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var service = json.RootElement.TryGetProperty("service", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var node = json.RootElement.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name ? name : null;
            return (service, node);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// What a Windows server needs before the proxy offers SAN access, all problems at once: a SAN policy that keeps new
/// shared disks offline (Windows would otherwise bring a snapshot LUN online and might mount it), and the iSCSI
/// initiator (<see cref="WindowsIscsiInitiator.InitiatorProblemAsync"/>).
/// </summary>
public static class WindowsSanCheck
{
    public static readonly string[] SafePolicies = ["OfflineShared", "OfflineAll"];

    public static async Task<IReadOnlyList<string>> CheckAsync(PowerShellRunner powershell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(powershell);
        var policy = await PolicyAsync(powershell, cancellationToken).ConfigureAwait(false);
        return SafePolicies.Contains(policy, StringComparer.OrdinalIgnoreCase)
            ? []
            : [$"the SAN policy is {policy ?? "unknown"}: Windows would bring a new SAN disk online and might mount it; it must be OfflineShared or OfflineAll"];
    }

    /// <summary>Get-StorageSetting's NewDiskPolicy (OnlineAll, OfflineShared, OfflineAll, OfflineInternal), or null.</summary>
    public static async Task<string?> PolicyAsync(PowerShellRunner powershell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(powershell);
        var (code, output) = await powershell("[string](Get-StorageSetting).NewDiskPolicy", cancellationToken).ConfigureAwait(false);
        return code == 0 && output.Trim() is { Length: > 0 } policy ? policy : null;
    }
}

/// <summary>
/// What the Windows proxy sets itself (as the other backup product does on its proxies): the SAN policy Offline Shared,
/// only when the policy is unsafe. It changes nothing else; iSCSI and MPIO stay as the administrator set them.
/// </summary>
public sealed class WindowsSanPreparation(PowerShellRunner powershell, Action<string> output)
{
    /// <summary>Returns 0 when nothing is left to do, 1 when a change failed.</summary>
    public async Task<int> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var policy = await WindowsSanCheck.PolicyAsync(powershell, cancellationToken).ConfigureAwait(false);
        if (WindowsSanCheck.SafePolicies.Contains(policy, StringComparer.OrdinalIgnoreCase))
        {
            output($"SAN policy {policy}: nothing to do.");
            return 0;
        }

        if (dryRun)
        {
            output($"Would set the SAN policy from {policy ?? "unknown"} to OfflineShared (Set-StorageSetting -NewDiskPolicy OfflineShared).");
            return 0;
        }

        var (code, result) = await powershell("Set-StorageSetting -NewDiskPolicy OfflineShared", cancellationToken).ConfigureAwait(false);
        output(code == 0
            ? $"Set the SAN policy from {policy ?? "unknown"} to OfflineShared, so Windows keeps new SAN disks offline (as backup proxies need)."
            : $"Setting the SAN policy to OfflineShared failed ({code}): {result}");
        return code == 0 ? 0 : 1;
    }
}

/// <summary>The Windows machine's SMBIOS UUID and BIOS serial (shown in the console; HotAdd does not apply).</summary>
public static class WindowsHostFacts
{
    public static async Task<(string? SmbiosUuid, string? BiosSerial)> ReadAsync(PowerShellRunner powershell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(powershell);
        var (code, output) = await powershell(
            "\"$((Get-CimInstance Win32_ComputerSystemProduct).UUID)|$((Get-CimInstance Win32_BIOS).SerialNumber)\"", cancellationToken).ConfigureAwait(false);
        var parts = code == 0 ? output.Trim().Split('|') : [];
        return parts.Length == 2 ? (Value(parts[0]), Value(parts[1])) : (null, null);

        static string? Value(string text) => text.Trim() is { Length: > 0 } value ? value : null;
    }
}
