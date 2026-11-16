using System.Buffers.Binary;

namespace Backdafuckup.Tape.Scsi;

/// <summary>Command descriptor blocks of the commands this product uses (T10 SPC-4, SSC-4, SMC-3). Big-endian fields.</summary>
public static class Cdb
{
    public const byte TestUnitReadyCode = 0x00;
    public const byte RewindCode = 0x01;
    public const byte RequestSenseCode = 0x03;
    public const byte ReadBlockLimitsCode = 0x05;
    public const byte InitializeElementStatusCode = 0x07;
    public const byte Read6Code = 0x08;
    public const byte Write6Code = 0x0A;
    public const byte WriteFilemarks6Code = 0x10;
    public const byte InquiryCode = 0x12;
    public const byte Erase6Code = 0x19;
    public const byte LoadUnloadCode = 0x1B;
    public const byte PreventAllowCode = 0x1E;
    public const byte ReadPositionCode = 0x34;
    public const byte ReportDensitySupportCode = 0x44;
    public const byte LogSenseCode = 0x4D;
    public const byte ModeSelect10Code = 0x55;
    public const byte ModeSense10Code = 0x5A;
    public const byte ReadAttributeCode = 0x8C;
    public const byte WriteAttributeCode = 0x8D;
    public const byte Space16Code = 0x91;
    public const byte Locate16Code = 0x92;
    public const byte SecurityProtocolInCode = 0xA2;
    public const byte MaintenanceInCode = 0xA3;
    public const byte MoveMediumCode = 0xA5;
    public const byte SecurityProtocolOutCode = 0xB5;
    public const byte ReadElementStatusCode = 0xB8;

    // ---- SPC ----

    public static byte[] TestUnitReady() => new byte[6];

    public static byte[] RequestSense(byte allocation = 252) => [RequestSenseCode, 0, 0, 0, allocation, 0];

    public static byte[] Inquiry(ushort allocation = 96, byte? vpdPage = null) =>
        [InquiryCode, (byte)(vpdPage is null ? 0 : 1), vpdPage ?? 0, (byte)(allocation >> 8), (byte)allocation, 0];

    /// <param name="pageControl">0 current values, 1 changeable, 2 default, 3 saved.</param>
    public static byte[] ModeSense10(byte page, byte subpage = 0, ushort allocation = 512, bool disableBlockDescriptors = false, byte pageControl = 0) =>
        [ModeSense10Code, (byte)(disableBlockDescriptors ? 0x08 : 0), (byte)((pageControl << 6) | (page & 0x3F)), subpage, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    public static byte[] ModeSelect10(ushort parameterLength, bool savePages = false) =>
        [ModeSelect10Code, (byte)(0x10 | (savePages ? 1 : 0)), 0, 0, 0, 0, 0, (byte)(parameterLength >> 8), (byte)parameterLength, 0];

    /// <param name="pageControl">1: current cumulative values.</param>
    public static byte[] LogSense(byte page, byte subpage = 0, ushort allocation = 4096, byte pageControl = 1) =>
        [LogSenseCode, 0, (byte)((pageControl << 6) | (page & 0x3F)), subpage, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    /// <summary>READ ATTRIBUTE, service action 00h (attribute values) from <paramref name="firstAttribute"/> on.</summary>
    public static byte[] ReadAttribute(ushort firstAttribute = 0, uint allocation = 8192, byte partition = 0)
    {
        var cdb = new byte[16];
        cdb[0] = ReadAttributeCode;
        cdb[7] = partition;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(8), firstAttribute);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(10), allocation);
        return cdb;
    }

    public static byte[] WriteAttribute(uint parameterLength, byte partition = 0)
    {
        var cdb = new byte[16];
        cdb[0] = WriteAttributeCode;
        cdb[1] = 0x01; // WTC: write through to the medium auxiliary memory
        cdb[7] = partition;
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(10), parameterLength);
        return cdb;
    }

    public static byte[] SecurityProtocolIn(byte protocol, ushort specific, uint allocation)
    {
        var cdb = new byte[12];
        cdb[0] = SecurityProtocolInCode;
        cdb[1] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), specific);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(6), allocation);
        return cdb;
    }

    public static byte[] SecurityProtocolOut(byte protocol, ushort specific, uint length)
    {
        var cdb = new byte[12];
        cdb[0] = SecurityProtocolOutCode;
        cdb[1] = protocol;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), specific);
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(6), length);
        return cdb;
    }

    /// <summary>REPORT SUPPORTED OPERATION CODES (MAINTENANCE IN, service action 0Ch) with the command timeouts descriptors.</summary>
    public static byte[] ReportSupportedOperationCodes(uint allocation = 65536)
    {
        var cdb = new byte[12];
        cdb[0] = MaintenanceInCode;
        cdb[1] = 0x0C;
        cdb[2] = 0x80; // RCTD, reporting option 000b: all commands
        BinaryPrimitives.WriteUInt32BigEndian(cdb.AsSpan(6), allocation);
        return cdb;
    }

    public static byte[] PreventAllowMediumRemoval(bool prevent) => [PreventAllowCode, 0, 0, 0, (byte)(prevent ? 1 : 0), 0];

    // ---- SSC ----

    public static byte[] ReadBlockLimits() => [ReadBlockLimitsCode, 0, 0, 0, 0, 0];

    public static byte[] ReportDensitySupport(bool currentMedium, ushort allocation = 1024) =>
        [ReportDensitySupportCode, (byte)(currentMedium ? 1 : 0), 0, 0, 0, 0, 0, (byte)(allocation >> 8), (byte)allocation, 0];

    /// <summary>READ(6) of one variable-length block; SILI: no CHECK CONDITION for a shorter block.</summary>
    public static byte[] Read6Variable(int length, bool suppressIncorrectLength = true)
    {
        CheckLength24(length);
        return [Read6Code, (byte)(suppressIncorrectLength ? 0x02 : 0), (byte)(length >> 16), (byte)(length >> 8), (byte)length, 0];
    }

    /// <summary>WRITE(6) of one variable-length block.</summary>
    public static byte[] Write6Variable(int length)
    {
        CheckLength24(length);
        return [Write6Code, 0, (byte)(length >> 16), (byte)(length >> 8), (byte)length, 0];
    }

    /// <summary>WRITE FILEMARKS(6). A count of 0 with IMMED off flushes the drive's buffer to the medium.</summary>
    public static byte[] WriteFilemarks(int count, bool immediate)
    {
        CheckLength24(count);
        return [WriteFilemarks6Code, (byte)(immediate ? 1 : 0), (byte)(count >> 16), (byte)(count >> 8), (byte)count, 0];
    }

    public static byte[] Rewind(bool immediate) => [RewindCode, (byte)(immediate ? 1 : 0), 0, 0, 0, 0];

    /// <param name="load">True: load (and position at the beginning); false: unload (rewind and eject).</param>
    public static byte[] LoadUnload(bool load, bool immediate) => [LoadUnloadCode, (byte)(immediate ? 1 : 0), 0, 0, (byte)(load ? 1 : 0), 0];

    /// <summary>ERASE(6), short: writes end of data at the current position (at the beginning: the tape counts as blank).</summary>
    public static byte[] EraseShort(bool immediate) => [Erase6Code, (byte)(immediate ? 2 : 0), 0, 0, 0, 0];

    public enum SpaceCode : byte
    {
        Blocks = 0,
        Filemarks = 1,
        EndOfData = 3,
    }

    public static byte[] Space16(SpaceCode code, long count)
    {
        var cdb = new byte[16];
        cdb[0] = Space16Code;
        cdb[1] = (byte)code;
        BinaryPrimitives.WriteInt64BigEndian(cdb.AsSpan(4), count);
        return cdb;
    }

    /// <summary>LOCATE(16) to a logical object (block) number, or with <paramref name="toFile"/> to the start of a logical file.</summary>
    public static byte[] Locate16(long target, bool toFile = false, byte partition = 0, bool changePartition = false, bool immediate = false)
    {
        var cdb = new byte[16];
        cdb[0] = Locate16Code;
        cdb[1] = (byte)((toFile ? 0x08 : 0) | (changePartition ? 0x02 : 0) | (immediate ? 0x01 : 0));
        cdb[3] = partition;
        BinaryPrimitives.WriteInt64BigEndian(cdb.AsSpan(4), target);
        return cdb;
    }

    /// <summary>READ POSITION; service action 06h: long form (32 bytes), 00h: short form (20 bytes).</summary>
    public static byte[] ReadPosition(bool longForm) => [ReadPositionCode, (byte)(longForm ? 0x06 : 0x00), 0, 0, 0, 0, 0, 0, 0, 0];

    // ---- SMC ----

    public enum ElementType : byte
    {
        All = 0,
        MediumTransport = 1,
        Storage = 2,
        ImportExport = 3,
        DataTransfer = 4,
    }

    public static byte[] ReadElementStatus(ElementType type, ushort start, ushort count, bool volumeTags, bool deviceIds, int allocation)
    {
        var cdb = new byte[12];
        cdb[0] = ReadElementStatusCode;
        cdb[1] = (byte)((volumeTags ? 0x10 : 0) | (byte)type);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), start);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4), count);
        cdb[6] = (byte)(deviceIds ? 0x01 : 0);
        cdb[7] = (byte)(allocation >> 16);
        cdb[8] = (byte)(allocation >> 8);
        cdb[9] = (byte)allocation;
        return cdb;
    }

    public static byte[] MoveMedium(ushort transport, ushort source, ushort destination)
    {
        var cdb = new byte[12];
        cdb[0] = MoveMediumCode;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(2), transport);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4), source);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(6), destination);
        return cdb;
    }

    public static byte[] InitializeElementStatus() => [InitializeElementStatusCode, 0, 0, 0, 0, 0];

    private static void CheckLength24(int length)
    {
        if (length is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A 6-byte CDB carries at most 24 bits.");
        }
    }
}
