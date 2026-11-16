using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Devices;

/// <summary>
/// Where tape devices are attached: this server, or a tape agent on another host. Devices are found and opened by their
/// serial number, never by their node, which can change (an iSCSI re-login renumbers them).
/// </summary>
public interface ITapeHost
{
    /// <summary>For messages: "this server" or "tape agent backup-02".</summary>
    string Name { get; }

    /// <summary>The tape drives and changers attached. A device another program holds open is listed without serial.</summary>
    Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens the device with <paramref name="serial"/> for this process alone; fails with a message naming the host when
    /// it is not attached or another program has it open.
    /// </summary>
    Task<IScsiDevice> OpenAsync(string serial, CancellationToken cancellationToken);
}

/// <summary>Tape devices attached to the machine this process runs on (Linux SG_IO or Windows SCSI pass-through).</summary>
/// <param name="name">For messages: "this server" on the backup server, "the tape agent on lab-01" in a tape agent.</param>
public sealed class LocalTapeHost(string name = "this server") : ITapeHost
{
    /// <summary>Where each serial was found last; checked again after every open.</summary>
    private readonly ConcurrentDictionary<string, string> _paths = new(StringComparer.Ordinal);

    public string Name => name;

    public async Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ScsiDeviceInfo> found;
        if (OperatingSystem.IsLinux())
        {
            found = await DiscoverLinuxAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (OperatingSystem.IsWindows())
        {
            found = await DiscoverWindowsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return [];
        }

        foreach (var device in found.Where(d => d.Serial is not null))
        {
            _paths[device.Serial!] = device.Path;
        }

        return found;
    }

    public async Task<IScsiDevice> OpenAsync(string serial, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Tape devices are supported on Linux and Windows.");
        }

        if (_paths.TryGetValue(serial, out var known) && await TryOpenAsync(known, serial, cancellationToken).ConfigureAwait(false) is { } device)
        {
            return device;
        }

        var found = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var match = found.FirstOrDefault(d => d.Serial == serial);
        if (match is not null && await TryOpenAsync(match.Path, serial, cancellationToken).ConfigureAwait(false) is { } opened)
        {
            return opened;
        }

        var busy = found.Where(d => d.Serial is null).Select(d => d.Path).ToList();
        if (match is null && _paths.TryGetValue(serial, out var last) && busy.Contains(last))
        {
            throw new IOException($"The tape device with serial {serial} ({last} on {Name}) is open in another program or another operation; try again when it is free.");
        }

        var where = OperatingSystem.IsLinux() ? "/sys/class/scsi_generic" : @"\\.\Scsi0: to \\.\Scsi63:";
        throw new IOException(
            $"No tape device with serial {serial} is attached to {Name} (scanned {where}, found {found.Count} device(s)" +
            (busy.Count > 0 ? $"; could not ask {string.Join(", ", busy)}, open in another program" : string.Empty) +
            "). Is the iSCSI session or the cable connected?");
    }

    /// <summary>Opens <paramref name="path"/> and checks that it still is the device with <paramref name="serial"/>.</summary>
    private async Task<IScsiDevice?> TryOpenAsync(string path, string serial, CancellationToken cancellationToken)
    {
        IScsiDevice device;
        try
        {
            device = OpenPath(path);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            var identity = await TapeDrive.Identify(new ScsiCommandRunner(device, path), cancellationToken).ConfigureAwait(false);
            if (identity.Serial == serial)
            {
                return device;
            }
        }
        catch (IOException)
        {
        }

        await device.DisposeAsync().ConfigureAwait(false);
        _paths.TryRemove(new KeyValuePair<string, string>(serial, path));
        return null;
    }

    private static IScsiDevice OpenPath(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            return LinuxScsiDevice.Open(path);
        }

        if (OperatingSystem.IsWindows())
        {
            return WindowsScsiDevice.Discover().FirstOrDefault(u => WindowsPath(u) == path) is { } unit
                ? WindowsScsiDevice.Open(unit)
                : throw new IOException($"{path} is no longer attached.");
        }

        throw new PlatformNotSupportedException("Tape devices are supported on Linux and Windows.");
    }

    [SupportedOSPlatform("linux")]
    private static async Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverLinuxAsync(CancellationToken cancellationToken)
    {
        var result = new List<ScsiDeviceInfo>();
        foreach (var device in LinuxScsiDiscovery.Discover())
        {
            if (device.Serial is not null)
            {
                result.Add(device);
                continue;
            }

            // sysfs has no VPD files for changers: ask the device itself.
            try
            {
                await using var opened = LinuxScsiDevice.Open(device.Path);
                result.Add(await IdentifyAsync(device, opened, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException)
            {
                result.Add(device); // Another program has it open: listed without serial.
            }
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverWindowsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ScsiDeviceInfo>();
        foreach (var unit in WindowsScsiDevice.Discover())
        {
            var info = new ScsiDeviceInfo(
                WindowsPath(unit), unit.Inquiry.PeripheralType, unit.Inquiry.Vendor, unit.Inquiry.Product, unit.Inquiry.Revision, null, [],
                $"port {unit.Port}, bus {unit.PathId}, target {unit.TargetId}, LUN {unit.Lun}{(unit.Claimed ? ", with its Windows driver" : string.Empty)}");
            try
            {
                await using var opened = WindowsScsiDevice.Open(unit);
                result.Add(await IdentifyAsync(info, opened, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException)
            {
                result.Add(info); // Another program has it open: listed without serial.
            }
        }

        return result;
    }

    private static string WindowsPath(WindowsScsiUnit unit) => $@"\\.\Scsi{unit.Port}: bus {unit.PathId} target {unit.TargetId} LUN {unit.Lun}";

    private static async Task<ScsiDeviceInfo> IdentifyAsync(ScsiDeviceInfo info, IScsiDevice device, CancellationToken cancellationToken)
    {
        var identity = await TapeDrive.Identify(new ScsiCommandRunner(device, info.Path), cancellationToken).ConfigureAwait(false);
        return info with { Serial = identity.Serial, Designators = identity.Designators, Vendor = identity.Vendor, Model = identity.Product, Revision = identity.Revision };
    }
}
