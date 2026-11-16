using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Tests;

/// <summary>CDBs and parsers against the byte layouts of the T10 standards (SPC-4, SSC-4, SMC-3).</summary>
[Trait("Category", "Unit")]
public sealed class ScsiFormatTests
{
    [Fact]
    public void Cdbs_have_the_standard_layout()
    {
        Assert.Equal(new byte[] { 0x12, 0x01, 0x80, 0x00, 0xFF, 0x00 }, Cdb.Inquiry(255, 0x80));
        Assert.Equal(new byte[] { 0x08, 0x02, 0x04, 0x00, 0x00, 0x00 }, Cdb.Read6Variable(256 * 1024));
        Assert.Equal(new byte[] { 0x0A, 0x00, 0x10, 0x00, 0x00, 0x00 }, Cdb.Write6Variable(1024 * 1024));
        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x00, 0x01, 0x00 }, Cdb.WriteFilemarks(1, immediate: false));
        Assert.Equal(new byte[] { 0x10, 0x01, 0x00, 0x00, 0x00, 0x00 }, Cdb.WriteFilemarks(0, immediate: true));
        Assert.Equal(new byte[] { 0x1B, 0x01, 0x00, 0x00, 0x01, 0x00 }, Cdb.LoadUnload(load: true, immediate: true));
        Assert.Equal(new byte[] { 0x34, 0x06, 0, 0, 0, 0, 0, 0, 0, 0 }, Cdb.ReadPosition(longForm: true));
        Assert.Equal(new byte[] { 0x92, 0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0 }, Cdb.Locate16(7, toFile: true));
        Assert.Equal(new byte[] { 0x91, 0x01, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE, 0, 0, 0, 0 }, Cdb.Space16(Cdb.SpaceCode.Filemarks, -2));
        Assert.Equal(new byte[] { 0x5A, 0x08, 0x1D, 0, 0, 0, 0, 0x02, 0x00, 0 }, Cdb.ModeSense10(0x1D, allocation: 512, disableBlockDescriptors: true));
        Assert.Equal(new byte[] { 0xB8, 0x14, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x10, 0x00, 0, 0 }, Cdb.ReadElementStatus(Cdb.ElementType.DataTransfer, 256, 2, true, true, 4096));
        Assert.Equal(new byte[] { 0xA5, 0, 0x00, 0x01, 0x04, 0x00, 0x01, 0x00, 0, 0, 0, 0 }, Cdb.MoveMedium(1, 1024, 256));
        Assert.Equal(new byte[] { 0xA2, 0x20, 0x00, 0x10, 0, 0, 0x00, 0x00, 0x20, 0x00, 0, 0 }, Cdb.SecurityProtocolIn(0x20, 0x0010, 8192));
        Assert.Equal(new byte[] { 0x8D, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0 }, Cdb.WriteAttribute(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cdb.Write6Variable(16 * 1024 * 1024));
    }

    [Fact]
    public void Fixed_sense_data_is_decoded_with_its_stream_bits_and_information()
    {
        // A READ that ran into a filemark, 4096 bytes not transferred.
        var sense = SenseData.Parse(SenseData.BuildFixed(SenseKey.NoSense, 0x00, 0x01, filemark: true, information: 4096))!;
        Assert.Equal((SenseKey.NoSense, (byte)0, (byte)1, true, false, false, 4096L), (sense.Key, sense.Asc, sense.Ascq, sense.Filemark, sense.EndOfMedium, sense.IncorrectLength, sense.Information));
        Assert.Equal("NO SENSE 00/01 (filemark detected), filemark", sense.ToString());

        // A block longer than the read: ILI with a negative residue.
        var longer = SenseData.Parse(SenseData.BuildFixed(SenseKey.NoSense, 0, 0, incorrectLength: true, information: -65536))!;
        Assert.True(longer.IncorrectLength);
        Assert.Equal(-65536, longer.Information);

        var worm = SenseData.Parse(SenseData.BuildFixed(SenseKey.DataProtect, 0x30, 0x0C))!;
        Assert.Equal("DATA PROTECT 30/0C (WORM medium, overwrite attempted)", worm.ToString());
        Assert.Null(SenseData.Parse(new byte[18]));
    }

    [Fact]
    public void Descriptor_sense_data_is_decoded_with_the_stream_commands_descriptor()
    {
        byte[] sense =
        [
            0x72, 0x00, 0x00, 0x02, 0, 0, 0, 0x14,
            0x00, 0x0A, 0x80, 0x00, 0, 0, 0, 0, 0, 0, 0x10, 0x00, // information: 4096
            0x04, 0x02, 0x00, 0x40, // stream commands: EOM
        ];
        var parsed = SenseData.Parse(sense)!;
        Assert.Equal((SenseKey.NoSense, (byte)0, (byte)2, true, 4096L), (parsed.Key, parsed.Asc, parsed.Ascq, parsed.EndOfMedium, parsed.Information));
        Assert.False(parsed.Deferred);
        Assert.True(SenseData.Parse([0x73, 0x03, 0x11, 0x00, 0, 0, 0, 0])!.Deferred);
    }

    [Fact]
    public void Positions_mode_pages_log_pages_and_attributes_parse()
    {
        var longForm = new byte[32];
        longForm[0] = 0x40; // EOP
        longForm[15] = 42;
        longForm[23] = 3;
        Assert.Equal(new TapePosition(false, true, 0, 42, 3), TapePosition.ParseLong(longForm));
        var unknown = new byte[32];
        unknown[0] = 0x04;
        Assert.Throws<InvalidDataException>(() => TapePosition.ParseLong(unknown));

        var select = ModeParameters.BuildSelect10(0, 0x60, [0x0F, 0x0E, 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(16 + 16, select.Length);
        var mode = ModeParameters.Parse10([0, 30, 0, 0x90, 0, 0, 0, 8, 0x60, 0, 0, 0, 0, 0, 0, 0, .. select[16..]]);
        Assert.Equal((true, 1, (byte?)0x60, (int?)0), (mode.WriteProtected, mode.BufferedMode, mode.DensityCode, mode.BlockLength));
        Assert.False(ModePages.CompressionEnabled(mode.Pages[ModePages.DataCompression]));
        Assert.True(ModePages.CompressionEnabled(ModePages.WithCompression(mode.Pages[ModePages.DataCompression], true)));

        byte[] alerts = [0x2E, 0, 0, 15, 0, 3, 3, 1, 1, 0, 20, 3, 1, 1, 0, 0x41, 3, 1, 1];
        Assert.Equal([3, 20], LogPages.TapeAlertFlags(alerts));
        Assert.Equal("clean now", LogPages.DescribeTapeAlert(20));

        var mam = MamAttributes.Build([MamAttributes.Ascii(MamAttributes.Barcode, "BDF000L9", 32), (MamAttributes.MediumType, 0, [MamAttributes.MediumTypeWorm]), (MamAttributes.MaximumCapacity, 0, [0, 0, 0, 0, 0, 0x01, 0x00, 0x00])]);
        var parsed = MamAttributes.Parse(mam);
        Assert.Equal("BDF000L9", MamAttributes.Text(parsed, MamAttributes.Barcode));
        Assert.Equal(65536, MamAttributes.Number(parsed, MamAttributes.MaximumCapacity));
        Assert.Equal(MamAttributes.MediumTypeWorm, parsed[MamAttributes.MediumType][0]);
    }

    [Fact]
    public void Command_timeouts_are_read_from_the_drives_own_report()
    {
        byte[] report =
        [
            0, 0, 0, 40,
            0x01, 0, 0, 0, 0, 0x02, 0, 6, 0, 0x0A, 0, 0, 0, 0, 0, 60, 0, 0, 0x0E, 0x10, // REWIND: recommended 3600 s
            0x08, 0, 0, 0, 0, 0x00, 0, 6, // READ(6) without timeouts
        ];
        var timeouts = CommandTimeouts.Parse(report);
        Assert.Equal(TimeSpan.FromHours(1), timeouts[(0x01, 0)]);
        Assert.False(timeouts.ContainsKey((0x08, 0)));
    }
}
