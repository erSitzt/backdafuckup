using System.Globalization;

namespace Backdafuckup.Proxy.Windows;

/// <summary>
/// The Windows proxy's disks: found by the LUN's NAA identifier, never by disk number (numbers change after rescans);
/// set offline and read-only before they are opened; opened for reading only, without the cache. Nothing is opened for
/// writing, and nothing is deleted: Windows drops a LUN the array no longer presents at the next rescan.
/// </summary>
public sealed class WindowsBlockDevices(IWindowsDisks disks, PowerShellRunner powershell) : IBlockDevices
{
    public Task<IReadOnlyList<ProxyDevice>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProxyDevice>>([.. disks.List().Select(ToDevice)]);

    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await powershell("Update-HostStorageCache", cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new IOException($"Update-HostStorageCache failed ({code}): {output}");
        }
    }

    public async Task<IOpenDevice> OpenAsync(ProxyDevice device, bool writable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (writable)
        {
            throw new InvalidOperationException($"{device.Path}: a Windows proxy opens disks read-only only (restores go through a Linux proxy).");
        }

        var naa = device.Serial is { Length: > 0 } serial ? DeviceSerials.Normalize(serial) : throw new InvalidOperationException($"{device.Path} has no WWN; only array LUNs are opened.");
        var disk = Find(naa) ?? throw new IOException($"No disk with WWN {naa} on this server (any more).");

        // Offline (nothing mounts it) and read-only, found again by its WWN: the number may have changed since the list.
        var (code, output) = await powershell(
            $"$d = Get-Disk | Where-Object {{ ($_.UniqueId -replace '[^0-9A-Fa-f]', '').ToLowerInvariant() -eq {PowerShell.Quote(naa)} }} | Select-Object -First 1; " +
            $"if (-not $d) {{ throw 'No disk with unique id {naa}.' }}; " +
            "if (-not $d.IsOffline) { Set-Disk -InputObject $d -IsOffline $true }; " +
            "if (-not $d.IsReadOnly) { Set-Disk -InputObject $d -IsReadOnly $true }; " +
            "$d = Get-Disk -Number $d.Number; \"$($d.Number) $($d.IsOffline) $($d.IsReadOnly)\"",
            cancellationToken).ConfigureAwait(false);
        var state = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (code != 0 || state.Length != 3 || !int.TryParse(state[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number != disk.Number || state[1] != "True" || state[2] != "True")
        {
            throw new IOException($"Disk {disk.Path} (WWN {naa}) could not be set offline and read-only before it is read: {(code != 0 ? output : $"Windows reports '{output}'")}.");
        }

        return new AlignedDiskDevice(ToDevice(disk), disks.OpenRead(disk), disk.SectorSize);
    }

    public Task DeleteAsync(ProxyDevice device, CancellationToken cancellationToken) => Task.CompletedTask;

    private WindowsDisk? Find(string naa) => disks.List().FirstOrDefault(d => d.Naa is { } id && DeviceSerials.Normalize(id) == naa);

    private static ProxyDevice ToDevice(WindowsDisk disk) =>
        new($"PhysicalDrive{disk.Number.ToString(CultureInfo.InvariantCulture)}", disk.Path, disk.SizeBytes, disk.Naa is { } naa ? DeviceSerials.Normalize(naa) : null, disk.Model);
}
