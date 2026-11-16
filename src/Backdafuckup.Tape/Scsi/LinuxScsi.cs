using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Tape.Scsi;

/// <summary>A tape drive or medium changer found in sysfs, before it is opened.</summary>
/// <param name="Path">The SCSI generic node, e.g. /dev/sg3 (it can change after a re-login; the serial does not).</param>
/// <param name="PeripheralType">1: tape drive, 8: medium changer.</param>
/// <param name="Serial">Unit serial number (VPD 80h), when sysfs has it.</param>
/// <param name="Designators">Device identification (VPD 83h), when sysfs has it.</param>
/// <param name="Connection">How it is attached, e.g. "iSCSI iqn.2026-04.cloud.backupnext.holo:drive-bdf-drv-01", or the SCSI address.</param>
public sealed record ScsiDeviceInfo(string Path, byte PeripheralType, string Vendor, string Model, string Revision, string? Serial, IReadOnlyList<string> Designators, string Connection);

/// <summary>Finds tape drives and changers through sysfs without opening them (Linux).</summary>
public static class LinuxScsiDiscovery
{
    /// <param name="sysfs">The sysfs root (tests use a fixture tree).</param>
    /// <param name="resolveNode">
    /// Resolves /sys/class/scsi_generic/sgN to its device path, e.g. /sys/devices/platform/host2/session1/target2:0:0/2:0:0:0/scsi_generic/sg0
    /// (default: the real link target).
    /// </param>
    public static IReadOnlyList<ScsiDeviceInfo> Discover(string sysfs = "/sys", Func<string, string>? resolveNode = null)
    {
        resolveNode ??= path => new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        var classDirectory = Path.Combine(sysfs, "class", "scsi_generic");
        if (!Directory.Exists(classDirectory))
        {
            return [];
        }

        var result = new List<ScsiDeviceInfo>();
        foreach (var node in Directory.EnumerateFileSystemEntries(classDirectory).Order(StringComparer.Ordinal))
        {
            var device = Path.Combine(node, "device");
            if (!byte.TryParse(Read(device, "type"), out var type) || type is not (1 or 8))
            {
                continue;
            }

            var serial = ReadBinary(device, "vpd_pg80") is { Length: > 4 } pg80 ? VpdPages.ParseSerial(pg80) : null;
            var designators = ReadBinary(device, "vpd_pg83") is { Length: > 4 } pg83 ? VpdPages.ParseDesignators(pg83) : [];
            var full = resolveNode(node);
            result.Add(new ScsiDeviceInfo(
                "/dev/" + Path.GetFileName(node),
                type,
                Read(device, "vendor") ?? string.Empty,
                Read(device, "model") ?? string.Empty,
                Read(device, "rev") ?? string.Empty,
                string.IsNullOrEmpty(serial) ? null : serial,
                designators,
                Connection(sysfs, full)));
        }

        return result;
    }

    private static string Connection(string sysfs, string devicePath)
    {
        // .../hostN/sessionM/target2:0:0/2:0:0:0/scsi_generic/sgN for iSCSI; otherwise the SCSI address (h:c:t:l).
        var parts = devicePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var session = parts.FirstOrDefault(p => p.StartsWith("session", StringComparison.Ordinal));
        if (session is not null && Read(Path.Combine(sysfs, "class", "iscsi_session", session), "targetname") is { } target)
        {
            return $"iSCSI {target}";
        }

        return $"SCSI {parts.LastOrDefault(p => p.Count(c => c == ':') == 3) ?? "?"}";
    }

    private static string? Read(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static byte[]? ReadBinary(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// A tape drive or changer on Linux through the SCSI generic driver (<c>/dev/sgN</c>, ioctl SG_IO). The tape driver
/// (<c>/dev/st*</c>) is never used: closing it rewinds the tape. Access needs the group <c>tape</c>, not root.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxScsiDevice : IScsiDevice
{
    private const int SgIo = 0x2285;
    private const int SgSetReservedSize = 0x2275;
    private const int BlkSectGet = 0x1267;
    private const int SgDxferNone = -1;
    private const int SgDxferToDev = -2;
    private const int SgDxferFromDev = -3;
    private const int ORdwr = 0x2;
    private const int OExcl = 0x80;
    private const int ONonBlock = 0x800;
    private const int Ebusy = 16;
    private const int SenseLength = 96;
    private readonly SafeFileHandle _fd;
    private readonly SemaphoreSlim _one = new(1, 1);

    private LinuxScsiDevice(string path, SafeFileHandle fd, int maxTransfer)
    {
        Path = path;
        _fd = fd;
        MaxTransferLength = maxTransfer;
    }

    public string Path { get; }

    public int MaxTransferLength { get; }

    /// <summary>Opens the device exclusively; another process using it gives a clear error instead of shared access.</summary>
    public static LinuxScsiDevice Open(string path, int reservedSize = 1024 * 1024)
    {
        var fd = OpenNative(path, ORdwr | OExcl | ONonBlock);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == Ebusy
                ? new IOException($"{path} is in use by another process (opened exclusively).")
                : new IOException($"{path} cannot be opened: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}). The service needs read and write access, usually through the group 'tape'.");
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        int maxSectors = 0;
        var maxTransfer = IoctlInt(fd, BlkSectGet, ref maxSectors) == 0 && maxSectors > 0 ? maxSectors * 512 : 256 * 1024;
        var reserved = Math.Min(reservedSize, maxTransfer);
        _ = IoctlInt(fd, SgSetReservedSize, ref reserved);
        return new LinuxScsiDevice(path, handle, maxTransfer);
    }

    public async ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken)
    {
        if (command.Buffer.Length > MaxTransferLength)
        {
            throw new ScsiTransportException($"{command.Name} on {Path} needs {command.Buffer.Length} bytes in one transfer; this path allows {MaxTransferLength}.");
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // SG_IO blocks for the whole command (a LOCATE can take hours): a dedicated thread, not a pool thread.
            return await Task.Factory.StartNew(() => Execute(command), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _fd.Dispose();
        _one.Dispose();
        return ValueTask.CompletedTask;
    }

    private unsafe ScsiResult Execute(ScsiCommand command)
    {
        var sense = new byte[SenseLength];
        using var data = command.Buffer.Pin();
        fixed (byte* cdb = command.Cdb)
        fixed (byte* sb = sense)
        {
            var header = new SgIoHeader
            {
                InterfaceId = 'S',
                DxferDirection = command.Direction switch
                {
                    ScsiDataDirection.In => SgDxferFromDev,
                    ScsiDataDirection.Out => SgDxferToDev,
                    _ => SgDxferNone,
                },
                CmdLen = (byte)command.Cdb.Length,
                MxSbLen = SenseLength,
                DxferLen = (uint)command.Buffer.Length,
                Dxferp = (nint)data.Pointer,
                Cmdp = (nint)cdb,
                Sbp = (nint)sb,
                Timeout = (uint)Math.Min(command.Timeout.TotalMilliseconds, uint.MaxValue - 1),
            };

            if (Ioctl(_fd.DangerousGetHandle().ToInt32(), SgIo, &header) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new ScsiTransportException($"SG_IO on {Path} failed: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");
            }

            if (header.HostStatus != 0 || (header.DriverStatus & 0x0F) is not (0 or 0x08))
            {
                // DID_* (host) or DRIVER_* (driver) errors: the command did not complete (timeout, path lost, reset).
                throw new ScsiTransportException($"{command.Name} on {Path} did not complete: host status 0x{header.HostStatus:X2}, driver status 0x{header.DriverStatus:X2}{(header.HostStatus == 0x03 ? " (timeout)" : "")}.");
            }

            var senseData = header.Status == ScsiStatus.CheckCondition && header.SbLenWr > 0 ? SenseData.Parse(sense.AsSpan(0, header.SbLenWr)) : null;
            return new ScsiResult(header.Status, senseData, Math.Max(0, header.Resid));
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int Ioctl(int fd, nuint request, SgIoHeader* header);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int IoctlInt(int fd, nuint request, ref int value);

    private static int IoctlInt(int fd, int request, ref int value) => IoctlInt(fd, (nuint)request, ref value);

    private static unsafe int Ioctl(int fd, int request, SgIoHeader* header) => Ioctl(fd, (nuint)request, header);
}

/// <summary>sg_io_hdr of the Linux SCSI generic driver (&lt;scsi/sg.h&gt;); 88 bytes on LP64.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SgIoHeader
{
    public int InterfaceId;
    public int DxferDirection;
    public byte CmdLen;
    public byte MxSbLen;
    public ushort IovecCount;
    public uint DxferLen;
    public nint Dxferp;
    public nint Cmdp;
    public nint Sbp;
    public uint Timeout;
    public uint Flags;
    public int PackId;
    public nint UsrPtr;
    public byte Status;
    public byte MaskedStatus;
    public byte MsgStatus;
    public byte SbLenWr;
    public ushort HostStatus;
    public ushort DriverStatus;
    public int Resid;
    public uint Duration;
    public uint Info;
}
