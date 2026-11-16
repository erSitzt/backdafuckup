using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Tape.Scsi;

/// <summary>A logical unit as the SCSI port driver lists it (IOCTL_SCSI_GET_INQUIRY_DATA).</summary>
/// <param name="Claimed">A class driver (tape or changer driver) owns it: reach it through \\.\TapeN or \\.\ChangerN.</param>
public sealed record WindowsScsiUnit(int Port, byte PathId, byte TargetId, byte Lun, bool Claimed, InquiryData Inquiry);

/// <summary>Parsing of the Windows port driver's structures; pure functions, so they are tested on every OS.</summary>
public static class WindowsScsiLayout
{
    /// <summary>Size of SCSI_PASS_THROUGH_DIRECT on 64-bit Windows.</summary>
    public const int PassThroughDirectSize = 56;

    /// <summary>Offset of the sense buffer behind SCSI_PASS_THROUGH_DIRECT and a ULONG filler.</summary>
    public const int SenseOffset = PassThroughDirectSize + 4;

    /// <summary>Parses SCSI_ADAPTER_BUS_INFO with its SCSI_INQUIRY_DATA chain.</summary>
    public static IReadOnlyList<WindowsScsiUnit> ParseInquiryData(int port, ReadOnlySpan<byte> info)
    {
        var result = new List<WindowsScsiUnit>();
        if (info.Length < 1)
        {
            return result;
        }

        var buses = info[0];
        for (var bus = 0; bus < buses; bus++)
        {
            // SCSI_BUS_DATA: NumberOfLogicalUnits (1), InitiatorBusId (1), 2 bytes padding, InquiryDataOffset (4).
            var busData = 4 + (bus * 8);
            if (busData + 8 > info.Length)
            {
                break;
            }

            var units = info[busData];
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(busData + 4)..]);
            for (var n = 0; n < units && offset > 0 && offset + 12 <= info.Length; n++)
            {
                // SCSI_INQUIRY_DATA: PathId, TargetId, Lun, DeviceClaimed, InquiryDataLength (4), NextInquiryDataOffset (4), data.
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(offset + 4)..]);
                var next = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[(offset + 8)..]);
                if (length >= 36 && offset + 12 + length <= info.Length)
                {
                    result.Add(new WindowsScsiUnit(port, info[offset], info[offset + 1], info[offset + 2], info[offset + 3] != 0, InquiryData.Parse(info.Slice(offset + 12, length))));
                }

                offset = next;
            }
        }

        return result;
    }

    /// <summary>The largest transfer an adapter allows (STORAGE_ADAPTER_DESCRIPTOR).</summary>
    public static int MaxTransfer(ReadOnlySpan<byte> adapterDescriptor)
    {
        if (adapterDescriptor.Length < 16)
        {
            return 64 * 1024;
        }

        var maxTransfer = BinaryPrimitives.ReadUInt32LittleEndian(adapterDescriptor[8..]);
        var maxPages = BinaryPrimitives.ReadUInt32LittleEndian(adapterDescriptor[12..]);
        var byPages = maxPages > 1 ? (long)(maxPages - 1) * 4096 : long.MaxValue;
        return (int)Math.Min(Math.Min(maxTransfer, byPages), 16 * 1024 * 1024);
    }
}

/// <summary>
/// A tape drive or changer on Windows through SCSI pass-through (IOCTL_SCSI_PASS_THROUGH_DIRECT): through its class
/// device (\\.\TapeN, \\.\ChangerN) when a driver claims it, else through the adapter (\\.\ScsiN:) with its address. No
/// vendor driver is needed. The service runs as LocalSystem, which may open both.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsScsiDevice : IScsiDevice
{
    private const uint IoctlScsiPassThroughDirect = 0x4D014;
    private const uint IoctlScsiGetInquiryData = 0x4100C;
    private const uint IoctlScsiGetAddress = 0x41018;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint OpenExisting = 3;
    private const int SenseLength = 32;
    private readonly SafeFileHandle _handle;
    private readonly byte _pathId;
    private readonly byte _targetId;
    private readonly byte _lun;
    private readonly int _alignment;
    private readonly SemaphoreSlim _one = new(1, 1);

    private WindowsScsiDevice(string path, SafeFileHandle handle, byte pathId, byte targetId, byte lun, int maxTransfer, int alignment)
    {
        Path = path;
        _handle = handle;
        _pathId = pathId;
        _targetId = targetId;
        _lun = lun;
        MaxTransferLength = maxTransfer;
        _alignment = alignment;
    }

    public string Path { get; }

    public int MaxTransferLength { get; }

    /// <summary>Lists the tape drives and changers on every SCSI port (\\.\Scsi0: … \\.\Scsi63:).</summary>
    public static IReadOnlyList<WindowsScsiUnit> Discover()
    {
        var result = new List<WindowsScsiUnit>();
        for (var port = 0; port < 64; port++)
        {
            using var adapter = OpenFile($@"\\.\Scsi{port}:", share: true);
            if (adapter.IsInvalid)
            {
                continue;
            }

            var buffer = new byte[64 * 1024];
            if (DeviceIoControl(adapter, IoctlScsiGetInquiryData, null, 0, buffer, buffer.Length, out var returned, 0))
            {
                result.AddRange(WindowsScsiLayout.ParseInquiryData(port, buffer.AsSpan(0, returned)).Where(u => u.Inquiry.PeripheralType is InquiryData.SequentialAccess or InquiryData.MediumChanger));
            }
        }

        return result;
    }

    /// <summary>Opens a unit: through its class device when claimed, else through the adapter (shared with others).</summary>
    public static WindowsScsiDevice Open(WindowsScsiUnit unit)
    {
        if (unit.Claimed)
        {
            var prefix = unit.Inquiry.PeripheralType == InquiryData.MediumChanger ? "Changer" : "Tape";
            for (var n = 0; n < 64; n++)
            {
                var path = $@"\\.\{prefix}{n}";
                var handle = OpenFile(path, share: false);
                if (handle.IsInvalid)
                {
                    continue;
                }

                var address = new byte[8];
                if (DeviceIoControl(handle, IoctlScsiGetAddress, null, 0, address, address.Length, out _, 0)
                    && address[4] == unit.Port && address[5] == unit.PathId && address[6] == unit.TargetId && address[7] == unit.Lun)
                {
                    var (max, alignment) = Limits(handle);
                    return new WindowsScsiDevice(path, handle, 0, 0, 0, max, alignment);
                }

                handle.Dispose();
            }

            throw new IOException($"The {(unit.Inquiry.PeripheralType == InquiryData.MediumChanger ? "changer" : "tape drive")} {unit.Inquiry.Vendor} {unit.Inquiry.Product} at port {unit.Port}, target {unit.TargetId}, LUN {unit.Lun} is claimed by a driver, but no \\\\.\\{prefix}N device answers for it; it may be in use by another program.");
        }

        var adapterPath = $@"\\.\Scsi{unit.Port}:";
        var adapter = OpenFile(adapterPath, share: true);
        if (adapter.IsInvalid)
        {
            throw new IOException($"{adapterPath} cannot be opened (error {Marshal.GetLastPInvokeError()}); the service needs to run as LocalSystem or an administrator.");
        }

        var (adapterMax, adapterAlignment) = Limits(adapter);
        return new WindowsScsiDevice($"{adapterPath} target {unit.TargetId} LUN {unit.Lun}", adapter, unit.PathId, unit.TargetId, unit.Lun, adapterMax, adapterAlignment);
    }

    public async ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken)
    {
        if (command.Buffer.Length > MaxTransferLength)
        {
            throw new ScsiTransportException($"{command.Name} on {Path} needs {command.Buffer.Length} bytes in one transfer; this adapter allows {MaxTransferLength} (MaximumTransferLength of the iSCSI initiator or HBA).");
        }

        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(() => Execute(command), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        _one.Dispose();
        return ValueTask.CompletedTask;
    }

    private unsafe ScsiResult Execute(ScsiCommand command)
    {
        // The adapter's alignment mask: copy into an aligned buffer when the caller's is not aligned.
        var length = command.Buffer.Length;
        var aligned = length == 0 ? null : (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)Math.Max(_alignment + 1, 8));
        try
        {
            if (command.Direction == ScsiDataDirection.Out && length > 0)
            {
                command.Buffer.Span.CopyTo(new Span<byte>(aligned, length));
            }

            var request = new byte[WindowsScsiLayout.SenseOffset + SenseLength];
            BinaryPrimitives.WriteUInt16LittleEndian(request, WindowsScsiLayout.PassThroughDirectSize); // Length
            request[3] = _pathId;
            request[4] = _targetId;
            request[5] = _lun;
            request[6] = (byte)command.Cdb.Length;
            request[7] = SenseLength;
            request[8] = command.Direction switch { ScsiDataDirection.Out => 0, ScsiDataDirection.In => 1, _ => 2 };
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), (uint)Math.Clamp(command.Timeout.TotalSeconds, 1, 108000));
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(24), (ulong)aligned);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(32), WindowsScsiLayout.SenseOffset);
            command.Cdb.CopyTo(request, 36);
            if (!DeviceIoControl(_handle, IoctlScsiPassThroughDirect, request, request.Length, request, request.Length, out _, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new ScsiTransportException($"{command.Name} on {Path} did not complete: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).");
            }

            var status = request[2];
            var transferred = (int)BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12));
            if (command.Direction == ScsiDataDirection.In && transferred > 0)
            {
                new ReadOnlySpan<byte>(aligned, Math.Min(transferred, length)).CopyTo(command.Buffer.Span);
            }

            var sense = status == ScsiStatus.CheckCondition && request[7] > 0 ? SenseData.Parse(request.AsSpan(WindowsScsiLayout.SenseOffset, request[7])) : null;
            return new ScsiResult(status, sense, Math.Max(0, length - transferred));
        }
        finally
        {
            if (aligned is not null)
            {
                NativeMemory.AlignedFree(aligned);
            }
        }
    }

    private static (int Max, int Alignment) Limits(SafeFileHandle handle)
    {
        // STORAGE_PROPERTY_QUERY: PropertyId StorageAdapterProperty (1), QueryType PropertyStandardQuery (0).
        var query = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(query, 1);
        var descriptor = new byte[64];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, descriptor, descriptor.Length, out var returned, 0))
        {
            return (64 * 1024, 7);
        }

        var alignment = returned >= 20 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16)) : 7;
        return (WindowsScsiLayout.MaxTransfer(descriptor.AsSpan(0, returned)), alignment);
    }

    private static SafeFileHandle OpenFile(string path, bool share) =>
        CreateFile(path, GenericRead | GenericWrite, share ? FileShareRead | FileShareWrite : 0, 0, OpenExisting, 0, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputLength, byte[]? output, int outputLength, out int returned, nint overlapped);
}
