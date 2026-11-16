using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Tests;

/// <summary>The Linux and Windows transports: structure layouts and discovery (tasks 4 and 5).</summary>
[Trait("Category", "Unit")]
public sealed class TransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-sysfs-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_sg_io_header_has_the_kernels_layout()
    {
        Assert.SkipUnless(Environment.Is64BitProcess, "The layout is asserted for LP64.");
        Assert.Equal(88, Marshal.SizeOf<SgIoHeader>());
        Assert.Equal(16, (int)Marshal.OffsetOf<SgIoHeader>(nameof(SgIoHeader.Dxferp)));
        Assert.Equal(56, (int)Marshal.OffsetOf<SgIoHeader>(nameof(SgIoHeader.UsrPtr)));
        Assert.Equal(64, (int)Marshal.OffsetOf<SgIoHeader>(nameof(SgIoHeader.Status)));
        Assert.Equal(72, (int)Marshal.OffsetOf<SgIoHeader>(nameof(SgIoHeader.Resid)));
    }

    [Fact]
    public void Sysfs_discovery_finds_drives_and_changers_by_serial_with_their_iscsi_target_whatever_their_sg_number()
    {
        Device("sg0", 0, "ATA", "disk", null); // a disk: left out
        Device("sg3", 8, "HPE", "MSL6480", "LIB0001");
        Device("sg4", 1, "HPE", "Ultrium 9-SCSI", "DRV0001");
        Directory.CreateDirectory(Path.Combine(_root, "class", "iscsi_session", "session7"));
        File.WriteAllText(Path.Combine(_root, "class", "iscsi_session", "session7", "targetname"), "iqn.2026-04.cloud.backupnext.holo:drive-bdf-drv-01\n");

        var found = LinuxScsiDiscovery.Discover(_root, path => path.EndsWith("sg4", StringComparison.Ordinal)
            ? "/sys/devices/platform/host5/session7/target5:0:0/5:0:0:0/scsi_generic/sg4"
            : "/sys/devices/pci0000:00/host2/target2:0:3/2:0:3:0/scsi_generic/sg3");

        Assert.Equal(["/dev/sg3", "/dev/sg4"], found.Select(f => f.Path));
        var drive = found[1];
        Assert.Equal((1, "HPE", "Ultrium 9-SCSI", "DRV0001"), (drive.PeripheralType, drive.Vendor, drive.Model, drive.Serial));
        Assert.Equal("iSCSI iqn.2026-04.cloud.backupnext.holo:drive-bdf-drv-01", drive.Connection);
        Assert.Contains("t10:HPE     Ultrium 9-SCSI  DRV0001", drive.Designators);
        Assert.Equal("SCSI 2:0:3:0", found[0].Connection);

        // After an iSCSI re-login the numbers change; the serials stay.
        Directory.Delete(Path.Combine(_root, "class", "scsi_generic"), recursive: true);
        Device("sg7", 1, "HPE", "Ultrium 9-SCSI", "DRV0001");
        Assert.Equal(("/dev/sg7", "DRV0001"), LinuxScsiDiscovery.Discover(_root, p => p).Select(f => (f.Path, f.Serial)).Single());
    }

    [Fact]
    public void Opening_a_device_that_is_not_there_names_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux transport.");
        var error = Assert.Throws<IOException>(() => OpenLinux("/dev/sg-does-not-exist"));
        Assert.Contains("/dev/sg-does-not-exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_port_drivers_inquiry_data_is_parsed()
    {
        // SCSI_ADAPTER_BUS_INFO with one bus and two logical units: a claimed changer and an unclaimed LTO drive.
        var buffer = new byte[4 + 8 + (2 * (12 + 36))];
        buffer[0] = 1;
        buffer[4] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 12);
        Unit(buffer, 12, target: 0, lun: 0, claimed: true, type: 8, "HPE", "MSL6480", next: 12 + 48);
        Unit(buffer, 60, target: 0, lun: 1, claimed: false, type: 1, "HPE", "Ultrium 9-SCSI", next: 0);

        var units = WindowsScsiLayout.ParseInquiryData(3, buffer);
        Assert.Equal(2, units.Count);
        Assert.Equal((3, (byte)0, (byte)0, true, "MSL6480"), (units[0].Port, units[0].TargetId, units[0].Lun, units[0].Claimed, units[0].Inquiry.Product));
        Assert.Equal(((byte)1, false, (byte)1), (units[1].Lun, units[1].Claimed, units[1].Inquiry.PeripheralType));
    }

    [Fact]
    public void The_largest_transfer_follows_the_adapters_limits()
    {
        var descriptor = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), 256 * 1024); // the Microsoft iSCSI initiator's default
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), 65);
        Assert.Equal(256 * 1024, WindowsScsiLayout.MaxTransfer(descriptor));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), 4 * 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), 17);
        Assert.Equal(64 * 1024, WindowsScsiLayout.MaxTransfer(descriptor));
    }

    private static LinuxScsiDevice OpenLinux(string path) =>
        OperatingSystem.IsLinux() ? LinuxScsiDevice.Open(path) : throw new PlatformNotSupportedException();

    private void Device(string node, byte type, string vendor, string model, string? serial)
    {
        var device = Path.Combine(_root, "class", "scsi_generic", node, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "type"), $"{type}\n");
        File.WriteAllText(Path.Combine(device, "vendor"), vendor.PadRight(8) + "\n");
        File.WriteAllText(Path.Combine(device, "model"), model.PadRight(16) + "\n");
        File.WriteAllText(Path.Combine(device, "rev"), "6.24\n");
        if (serial is not null)
        {
            var bytes = Encoding.ASCII.GetBytes(serial);
            File.WriteAllBytes(Path.Combine(device, "vpd_pg80"), [0x01, 0x80, 0, (byte)bytes.Length, .. bytes]);
            var id = Encoding.ASCII.GetBytes(vendor.PadRight(8) + model.PadRight(16) + serial);
            File.WriteAllBytes(Path.Combine(device, "vpd_pg83"), [0x01, 0x83, 0, (byte)(4 + id.Length), 0x02, 0x01, 0, (byte)id.Length, .. id]);
        }
    }

    private static void Unit(byte[] buffer, int offset, byte target, byte lun, bool claimed, byte type, string vendor, string product, int next)
    {
        buffer[offset + 1] = target;
        buffer[offset + 2] = lun;
        buffer[offset + 3] = (byte)(claimed ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8), (uint)next);
        var inquiry = buffer.AsSpan(offset + 12, 36);
        inquiry[0] = type;
        Encoding.ASCII.GetBytes(vendor.PadRight(8)).CopyTo(inquiry[8..]);
        Encoding.ASCII.GetBytes(product.PadRight(16)).CopyTo(inquiry[16..]);
    }
}
