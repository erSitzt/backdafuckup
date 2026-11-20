using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Proxy.Windows;

/// <summary>A disk as Windows lists it: <c>\\.\PhysicalDriveN</c> (the multipath disk with MPIO, one per LUN).</summary>
/// <param name="Naa">The LUN's NAA identifier from VPD page 0x83 (lower-case hex), or null.</param>
public sealed record WindowsDisk(int Number, string Path, long SizeBytes, int SectorSize, string? Naa, string? Model);

/// <summary>The Windows disk calls the proxy needs, behind an interface so the logic is tested on Linux.</summary>
public interface IWindowsDisks
{
    IReadOnlyList<WindowsDisk> List();

    /// <summary>Opens a disk for reading only, without the cache: every read must be sector-aligned (<see cref="AlignedDiskDevice"/>).</summary>
    SafeFileHandle OpenRead(WindowsDisk disk);
}

/// <summary><see cref="IWindowsDisks"/> through <c>DeviceIoControl</c>; nothing is ever opened for writing.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32Disks : IWindowsDisks
{
    /// <summary>Disk numbers can have gaps after LUNs went away: the whole range is looked at.</summary>
    private const int MaxDiskNumber = 1024;

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceUniqueIdProperty = 3;
    private const int StorageAccessAlignmentProperty = 6;

    /// <summary>FILE_FLAG_NO_BUFFERING: .NET passes it through to CreateFile.</summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public IReadOnlyList<WindowsDisk> List()
    {
        var disks = new List<WindowsDisk>();
        for (var number = 0; number < MaxDiskNumber; number++)
        {
            var path = $@"\\.\PhysicalDrive{number}";
            using var handle = CreateFileW(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                continue;
            }

            var length = new byte[8];
            var size = DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, length, length.Length, out _, IntPtr.Zero) ? BitConverter.ToInt64(length, 0) : 0;
            var uniqueId = Query(handle, StorageDeviceUniqueIdProperty);
            var device = Query(handle, StorageDeviceProperty);
            var alignment = Query(handle, StorageAccessAlignmentProperty);
            disks.Add(new WindowsDisk(number, path, size, alignment is null ? 512 : WindowsStorageLayout.LogicalSector(alignment),
                uniqueId is null ? null : WindowsStorageLayout.NaaFromUniqueId(uniqueId), device is null ? null : WindowsStorageLayout.Model(device)));
        }

        return disks;
    }

    public SafeFileHandle OpenRead(WindowsDisk disk)
    {
        ArgumentNullException.ThrowIfNull(disk);
        return File.OpenHandle(disk.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, NoBuffering);
    }

    private static byte[]? Query(SafeFileHandle handle, int property)
    {
        var query = new byte[12];
        BitConverter.GetBytes(property).CopyTo(query, 0);
        var answer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) || returned <= 0)
        {
            return null;
        }

        return answer.AsSpan(0, returned).ToArray();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
