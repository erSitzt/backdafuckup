using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Backdafuckup.Nbd;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Proxy;

/// <summary>The proxy's block devices: what is there, rescanning, opening and removing (Linux; files in tests).</summary>
public interface IBlockDevices
{
    Task<IReadOnlyList<ProxyDevice>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Asks the kernel to look for new disks (after a hot-add or a new LUN) and waits for udev.</summary>
    Task RescanAsync(CancellationToken cancellationToken);

    /// <summary>Opens a device; read-only devices are also set read-only in the kernel.</summary>
    Task<IOpenDevice> OpenAsync(ProxyDevice device, bool writable, CancellationToken cancellationToken);

    /// <summary>Removes a device from the kernel (before its disk is detached, so no I/O hangs on a vanished disk).</summary>
    Task DeleteAsync(ProxyDevice device, CancellationToken cancellationToken);
}

/// <summary>An opened device: served as NBD, closed on dispose.</summary>
public interface IOpenDevice : INbdExport, IDisposable
{
    ProxyDevice Device { get; }
}

/// <summary>Serial numbers as the proxy and the server compare them: hex digits only, lower case.</summary>
public static class DeviceSerials
{
    public static string Normalize(string serial)
    {
        ArgumentNullException.ThrowIfNull(serial);
        return new string([.. serial.Where(char.IsAsciiHexDigit).Select(char.ToLowerInvariant)]);
    }
}

/// <summary>
/// Linux block devices from sysfs and udev. The serial comes from udev's by-id links (<c>wwn-0x…</c>, <c>scsi-3…</c>):
/// for PVSCSI disks the kernel does not fill <c>device/vpd_pg80</c>/<c>wwid</c>, udev reads them with an INQUIRY.
/// </summary>
public sealed class LinuxBlockDevices(string sysRoot = "/sys", string devRoot = "/dev") : IBlockDevices
{
    private static readonly string[] Prefixes = ["sd", "vd", "xvd", "nvme"];

    public async Task<IReadOnlyList<ProxyDevice>> ListAsync(CancellationToken cancellationToken)
    {
        var serials = Serials();
        var devices = new List<ProxyDevice>();
        foreach (var directory in Directory.EnumerateDirectories(Path.Combine(sysRoot, "block")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!Prefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            var sectors = long.TryParse((await ReadAsync(Path.Combine(directory, "size"), cancellationToken).ConfigureAwait(false))?.Trim(), out var s) ? s : 0;
            var model = (await ReadAsync(Path.Combine(directory, "device", "model"), cancellationToken).ConfigureAwait(false))?.Trim();
            devices.Add(new ProxyDevice(name, Path.Combine(devRoot, name), sectors * 512, serials.GetValueOrDefault(name), string.IsNullOrEmpty(model) ? null : model));
        }

        return devices;
    }

    public async Task RescanAsync(CancellationToken cancellationToken)
    {
        foreach (var scan in Directory.EnumerateFiles(Path.Combine(sysRoot, "class", "scsi_host"), "scan", SearchOption.AllDirectories))
        {
            try
            {
                await File.WriteAllTextAsync(scan, "- - -", cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A host that cannot scan (e.g. the CD-ROM's ATA host) is left alone.
            }
        }

        await RunAsync("udevadm", ["settle", "--timeout=20"], cancellationToken).ConfigureAwait(false);
    }

    public async Task<IOpenDevice> OpenAsync(ProxyDevice device, bool writable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        await RunAsync("blockdev", [writable ? "--setrw" : "--setro", device.Path], cancellationToken).ConfigureAwait(false);
        return new FileDevice(device, File.OpenHandle(device.Path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite), writable);
    }

    public async Task DeleteAsync(ProxyDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var delete = Path.Combine(sysRoot, "block", device.Name, "device", "delete");
        if (!File.Exists(delete))
        {
            return;
        }

        await RunAsync("blockdev", ["--flushbufs", device.Path], cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(delete, "1", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Kernel name → serial, from <c>/dev/disk/by-id</c> (wwn-0x… first, then scsi-3…).</summary>
    private Dictionary<string, string> Serials()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var byId = Path.Combine(devRoot, "disk", "by-id");
        if (!Directory.Exists(byId))
        {
            return result;
        }

        foreach (var prefix in new[] { "wwn-0x", "scsi-3" })
        {
            foreach (var link in Directory.EnumerateFileSystemEntries(byId, prefix + "*"))
            {
                var linkName = Path.GetFileName(link);
                if (linkName.Contains("-part", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = new FileInfo(link).LinkTarget is { } t ? Path.GetFileName(t) : null;
                if (target is not null && !result.ContainsKey(target))
                {
                    result[target] = DeviceSerials.Normalize(linkName[prefix.Length..]);
                }
            }
        }

        return result;
    }

    private static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task RunAsync(string file, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new IOException($"{file} could not be started.");
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException($"{file} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {error.Trim()}");
        }
    }
}

/// <summary>A device (or, in tests, a file) opened for NBD: positional reads and writes, fsync on flush.</summary>
public sealed class FileDevice(ProxyDevice device, SafeFileHandle handle, bool writable) : IOpenDevice
{
    private static readonly byte[] Zeros = new byte[1 << 20];

    public ProxyDevice Device { get; } = device;

    public long Size => Device.SizeBytes > 0 ? Device.SizeBytes : RandomAccess.GetLength(handle);

    public bool ReadOnly => !writable;

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, destination[done..], offset + done, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // Past the end of a device whose size the kernel reports in whole sectors: zeros.
                destination.Span[done..].Clear();
                return;
            }

            done += read;
        }
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(handle, data, offset, cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && Fsync((int)handle.DangerousGetHandle()) != 0)
        {
            throw new IOException($"fsync of {Device.Path} failed (errno {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}).");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteZeroesAsync(long offset, long length, CancellationToken cancellationToken)
    {
        for (var done = 0L; done < length;)
        {
            var piece = (int)Math.Min(Zeros.Length, length - done);
            await RandomAccess.WriteAsync(handle, Zeros.AsMemory(0, piece), offset + done, cancellationToken).ConfigureAwait(false);
            done += piece;
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
}
