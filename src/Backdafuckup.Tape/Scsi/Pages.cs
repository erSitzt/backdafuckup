using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Tape.Scsi;

/// <summary>Standard INQUIRY data (SPC-4 6.6.2).</summary>
public sealed record InquiryData(byte PeripheralType, bool Removable, string Vendor, string Product, string Revision)
{
    public const byte SequentialAccess = 0x01;
    public const byte MediumChanger = 0x08;

    public static InquiryData Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36)
        {
            throw new InvalidDataException($"INQUIRY data has {data.Length} bytes, at least 36 are needed.");
        }

        return new InquiryData((byte)(data[0] & 0x1F), (data[1] & 0x80) != 0, Ascii(data.Slice(8, 8)), Ascii(data.Slice(16, 16)), Ascii(data.Slice(32, 4)));
    }

    internal static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
    }
}

/// <summary>Identity of a device from VPD pages 80h (unit serial number) and 83h (device identification).</summary>
public static class VpdPages
{
    public static string ParseSerial(ReadOnlySpan<byte> page)
    {
        if (page.Length < 4 || page[1] != 0x80)
        {
            throw new InvalidDataException("Not a unit serial number page (VPD 80h).");
        }

        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(page[2..]), page.Length - 4);
        return InquiryData.Ascii(page.Slice(4, length));
    }

    /// <summary>The designators of page 83h as text: "t10:HPE Ultrium 9-SCSI 1234", "naa:5000E11163D5D001".</summary>
    public static IReadOnlyList<string> ParseDesignators(ReadOnlySpan<byte> page)
    {
        if (page.Length < 4 || page[1] != 0x83)
        {
            throw new InvalidDataException("Not a device identification page (VPD 83h).");
        }

        var result = new List<string>();
        var end = Math.Min(page.Length, 4 + BinaryPrimitives.ReadUInt16BigEndian(page[2..]));
        for (var offset = 4; offset + 4 <= end;)
        {
            var length = page[offset + 3];
            if (offset + 4 + length > end)
            {
                break;
            }

            result.Add(Designator(page.Slice(offset, 4 + length)));
            offset += 4 + length;
        }

        return result;
    }

    /// <summary>One designator (also the device identifier of READ ELEMENT STATUS with DVCID) as text.</summary>
    public static string Designator(ReadOnlySpan<byte> designator)
    {
        var codeSet = designator[0] & 0x0F;
        var type = designator[1] & 0x0F;
        var value = designator[4..];
        var prefix = type switch
        {
            0 => "vendor",
            1 => "t10",
            2 => "eui64",
            3 => "naa",
            8 => "scsi",
            _ => $"type{type}",
        };
        var text = codeSet is 2 or 3 ? InquiryData.Ascii(value) : Convert.ToHexString(value);
        return $"{prefix}:{text}";
    }
}

/// <summary>READ BLOCK LIMITS (SSC-4 7.7).</summary>
public readonly record struct BlockLimits(int MaxBlockLength, int MinBlockLength, int Granularity)
{
    public static BlockLimits Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6)
        {
            throw new InvalidDataException("READ BLOCK LIMITS data is shorter than 6 bytes.");
        }

        return new BlockLimits((data[1] << 16) | (data[2] << 8) | data[3], BinaryPrimitives.ReadUInt16BigEndian(data[4..]), data[0] & 0x1F);
    }
}

/// <summary>READ POSITION (SSC-4 7.10): where the tape is.</summary>
/// <param name="BeginningOfPartition">BOP.</param>
/// <param name="EndOfPartition">EOP: between early warning and the end of the partition.</param>
/// <param name="Block">Logical object (block) number.</param>
/// <param name="File">Logical file (filemark count) number, long form only.</param>
public sealed record TapePosition(bool BeginningOfPartition, bool EndOfPartition, int Partition, long Block, long? File)
{
    public static TapePosition ParseLong(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
        {
            throw new InvalidDataException("READ POSITION long form is shorter than 32 bytes.");
        }

        if ((data[0] & 0x04) != 0)
        {
            throw new InvalidDataException("The drive does not know the position (READ POSITION: LONU).");
        }

        return new TapePosition(
            (data[0] & 0x80) != 0,
            (data[0] & 0x40) != 0,
            BinaryPrimitives.ReadInt32BigEndian(data[4..]),
            BinaryPrimitives.ReadInt64BigEndian(data[8..]),
            (data[0] & 0x02) != 0 ? null : BinaryPrimitives.ReadInt64BigEndian(data[16..]));
    }

    public static TapePosition ParseShort(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
        {
            throw new InvalidDataException("READ POSITION short form is shorter than 20 bytes.");
        }

        if ((data[0] & 0x04) != 0)
        {
            throw new InvalidDataException("The drive does not know the position (READ POSITION: BPU).");
        }

        return new TapePosition((data[0] & 0x80) != 0, (data[0] & 0x40) != 0, data[1], BinaryPrimitives.ReadUInt32BigEndian(data[4..]), null);
    }

    public override string ToString() => $"partition {Partition}, block {Block}{(File is { } f ? $", file {f}" : "")}{(BeginningOfPartition ? ", at the beginning" : "")}{(EndOfPartition ? ", past early warning" : "")}";
}

/// <summary>MODE SENSE(10) data: header, block descriptor and pages (SPC-4 7.5).</summary>
public sealed class ModeParameters
{
    private ModeParameters(byte mediumType, byte deviceSpecific, byte? density, int? blockLength, Dictionary<byte, byte[]> pages, byte[] raw)
    {
        MediumType = mediumType;
        DeviceSpecific = deviceSpecific;
        DensityCode = density;
        BlockLength = blockLength;
        Pages = pages;
        Raw = raw;
    }

    public byte MediumType { get; }

    /// <summary>Sequential-access devices: WP (bit 7), buffered mode (bits 6-4), speed (bits 3-0).</summary>
    public byte DeviceSpecific { get; }

    public bool WriteProtected => (DeviceSpecific & 0x80) != 0;

    public int BufferedMode => (DeviceSpecific >> 4) & 0x07;

    public byte? DensityCode { get; }

    /// <summary>Block length of the block descriptor: 0 is variable-block mode.</summary>
    public int? BlockLength { get; }

    /// <summary>Pages by page code, each from its page code byte on.</summary>
    public IReadOnlyDictionary<byte, byte[]> Pages { get; }

    public byte[] Raw { get; }

    public static ModeParameters Parse10(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            throw new InvalidDataException("MODE SENSE(10) data is shorter than its header.");
        }

        var total = Math.Min(data.Length, BinaryPrimitives.ReadUInt16BigEndian(data) + 2);
        var descriptorsLength = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        byte? density = null;
        int? blockLength = null;
        if (descriptorsLength >= 8 && data.Length >= 16)
        {
            density = data[8];
            blockLength = (data[13] << 16) | (data[14] << 8) | data[15];
        }

        var pages = new Dictionary<byte, byte[]>();
        for (var offset = 8 + descriptorsLength; offset + 2 <= total;)
        {
            var code = (byte)(data[offset] & 0x3F);
            var subpageFormat = (data[offset] & 0x40) != 0;
            var length = subpageFormat ? 4 + BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]) : 2 + data[offset + 1];
            if (offset + length > total)
            {
                break;
            }

            pages.TryAdd(code, data.Slice(offset, length).ToArray());
            offset += length;
        }

        return new ModeParameters(data[2], data[3], density, blockLength, pages, data[..total].ToArray());
    }

    /// <summary>
    /// A MODE SELECT(10) parameter list: header (mode data length 0, buffered mode 1), one block descriptor with
    /// <paramref name="blockLength"/> (0: variable) and the given pages.
    /// </summary>
    public static byte[] BuildSelect10(int blockLength, byte density, params byte[][] pages)
    {
        var length = 8 + 8 + pages.Sum(p => p.Length);
        var list = new byte[length];
        list[3] = 0x10; // buffered mode 1
        BinaryPrimitives.WriteUInt16BigEndian(list.AsSpan(6), 8);
        list[8] = density;
        list[13] = (byte)(blockLength >> 16);
        list[14] = (byte)(blockLength >> 8);
        list[15] = (byte)blockLength;
        var offset = 16;
        foreach (var page in pages)
        {
            page.CopyTo(list, offset);
            list[offset] &= 0x7F; // PS is reserved in MODE SELECT
            offset += page.Length;
        }

        return list;
    }
}

/// <summary>Mode page codes of tape drives and changers.</summary>
public static class ModePages
{
    public const byte DataCompression = 0x0F;
    public const byte DeviceConfiguration = 0x10;

    /// <summary>Sequential-access devices: medium configuration (WORM); medium changers: element address assignment.</summary>
    public const byte MediumConfigurationOrElementAddresses = 0x1D;

    /// <summary>Data compression page: DCE (compress when writing).</summary>
    public static bool CompressionEnabled(byte[] page) => page.Length > 2 && (page[2] & 0x80) != 0;

    public static byte[] WithCompression(byte[] page, bool enabled)
    {
        var copy = (byte[])page.Clone();
        copy[2] = (byte)(enabled ? copy[2] | 0x80 : copy[2] & 0x7F);
        return copy;
    }

    /// <summary>Device configuration page: SEW, write the buffer to the medium at early warning.</summary>
    public static bool SynchronizeAtEarlyWarning(byte[] page) => page.Length > 10 && (page[10] & 0x08) != 0;

    public static byte[] WithSynchronizeAtEarlyWarning(byte[] page, bool enabled)
    {
        var copy = (byte[])page.Clone();
        copy[10] = (byte)(enabled ? copy[10] | 0x08 : copy[10] & ~0x08);
        return copy;
    }

    /// <summary>Medium configuration page (SSC-4 8.3.8): WORMM, a WORM cartridge is loaded.</summary>
    public static bool WormMedium(byte[] page) => page.Length > 2 && (page[2] & 0x01) != 0;
}

/// <summary>Element address assignment page of a medium changer (SMC-3 7.3.4).</summary>
public sealed record ElementAddresses(ushort FirstTransport, ushort Transports, ushort FirstStorage, ushort Storage, ushort FirstImportExport, ushort ImportExport, ushort FirstDrive, ushort Drives)
{
    public static ElementAddresses Parse(byte[] page)
    {
        if (page.Length < 18 || (page[0] & 0x3F) != ModePages.MediumConfigurationOrElementAddresses)
        {
            throw new InvalidDataException("Not an element address assignment page (1Dh).");
        }

        ushort U(int offset) => BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(offset));
        return new ElementAddresses(U(2), U(4), U(6), U(8), U(10), U(12), U(14), U(16));
    }
}

/// <summary>One parameter of a LOG SENSE page.</summary>
public readonly record struct LogParameter(ushort Code, byte Control, byte[] Value)
{
    public long AsNumber()
    {
        long n = 0;
        foreach (var b in Value)
        {
            n = (n << 8) | b;
        }

        return n;
    }
}

public static class LogPages
{
    public const byte SequentialAccess = 0x0C;
    public const byte VolumeStatistics = 0x17;
    public const byte TapeAlert = 0x2E;
    public const byte TapeCapacity = 0x31;

    public static IReadOnlyList<LogParameter> Parse(ReadOnlySpan<byte> data, byte expectedPage)
    {
        if (data.Length < 4 || (data[0] & 0x3F) != expectedPage)
        {
            throw new InvalidDataException($"Not log page {expectedPage:X2}h.");
        }

        var end = Math.Min(data.Length, 4 + BinaryPrimitives.ReadUInt16BigEndian(data[2..]));
        var result = new List<LogParameter>();
        for (var offset = 4; offset + 4 <= end;)
        {
            var length = data[offset + 3];
            if (offset + 4 + length > end)
            {
                break;
            }

            result.Add(new LogParameter(BinaryPrimitives.ReadUInt16BigEndian(data[offset..]), data[offset + 2], data.Slice(offset + 4, length).ToArray()));
            offset += 4 + length;
        }

        return result;
    }

    /// <summary>The TapeAlert flags that are set (parameters 0001h-0040h with bit 0 of their value).</summary>
    public static IReadOnlyList<int> TapeAlertFlags(ReadOnlySpan<byte> data) =>
        [.. Parse(data, TapeAlert).Where(p => p.Code is >= 1 and <= 64 && p.Value.Length > 0 && (p.Value[0] & 0x01) != 0).Select(p => (int)p.Code)];

    /// <summary>Texts of the TapeAlert flags operators act on (SSC-4 Annex A).</summary>
    public static string DescribeTapeAlert(int flag) => flag switch
    {
        1 => "read warning",
        2 => "write warning",
        3 => "hard error",
        4 => "media: the cartridge is not reliable any more",
        5 => "read failure",
        6 => "write failure",
        7 => "media life: the cartridge reached its end of life",
        8 => "not data grade",
        9 => "write protect",
        10 => "no removal: the cartridge is locked in the drive",
        11 => "cleaning cartridge loaded",
        12 => "unsupported format",
        15 => "cartridge memory failure",
        16 => "forced eject",
        18 => "tape directory corrupted",
        20 => "clean now",
        21 => "clean periodic",
        22 => "expired cleaning cartridge",
        23 => "invalid cleaning cartridge",
        30 => "hardware A",
        31 => "hardware B",
        32 => "interface",
        33 => "eject media",
        34 => "download failed",
        37 => "drive voltage",
        39 => "diagnostics required",
        51 => "tape directory invalid at unload",
        52 => "tape system area write failure",
        53 => "tape system area read failure",
        55 => "loading failure",
        56 => "unload failure",
        58 => "microcode update failed",
        59 => "WORM medium integrity check failed",
        60 => "WORM medium overwrite attempted",
        _ => $"flag {flag}",
    };
}

/// <summary>An element of a medium changer (SMC-3 6.10): a slot, a mail slot, a drive or the robot.</summary>
/// <param name="Type">Element type.</param>
/// <param name="Address">Its address.</param>
/// <param name="Full">A cartridge is in it.</param>
/// <param name="Accessible">The robot can reach it (mail slots: the door is closed).</param>
/// <param name="Exception">The element reports a fault (see <paramref name="Asc"/>/<paramref name="Ascq"/>).</param>
/// <param name="SourceAddress">Where the cartridge came from (when <paramref name="SourceValid"/>).</param>
/// <param name="VolumeTag">The primary volume tag (barcode), trimmed; null when the changer reports none.</param>
/// <param name="DeviceId">For drives with DVCID: the drive's identifier, as in its VPD page 83h or 80h.</param>
public sealed record ChangerElement(
    Cdb.ElementType Type,
    ushort Address,
    bool Full,
    bool Accessible,
    bool Exception,
    byte Asc,
    byte Ascq,
    bool SourceValid,
    ushort SourceAddress,
    string? VolumeTag,
    string? DeviceId);

public static class ElementStatus
{
    /// <summary>Parses READ ELEMENT STATUS data; <paramref name="complete"/> is false when the allocation was too small.</summary>
    public static IReadOnlyList<ChangerElement> Parse(ReadOnlySpan<byte> data, out bool complete)
    {
        complete = true;
        if (data.Length < 8)
        {
            throw new InvalidDataException("READ ELEMENT STATUS data is shorter than its header.");
        }

        var reportBytes = (data[5] << 16) | (data[6] << 8) | data[7];
        if (8 + reportBytes > data.Length)
        {
            complete = false;
        }

        var end = Math.Min(data.Length, 8 + reportBytes);
        var result = new List<ChangerElement>();
        for (var offset = 8; offset + 8 <= end;)
        {
            var type = (Cdb.ElementType)(data[offset] & 0x0F);
            var primaryTag = (data[offset + 1] & 0x80) != 0;
            var alternateTag = (data[offset + 1] & 0x40) != 0;
            var descriptorLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var pageBytes = (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7];
            var pageEnd = Math.Min(end, offset + 8 + pageBytes);
            if (descriptorLength < 12)
            {
                throw new InvalidDataException($"READ ELEMENT STATUS: element descriptors of {descriptorLength} bytes are too short.");
            }

            for (var d = offset + 8; d + descriptorLength <= pageEnd; d += descriptorLength)
            {
                result.Add(ParseDescriptor(type, data.Slice(d, descriptorLength), primaryTag, alternateTag));
            }

            if (offset + 8 + pageBytes > end)
            {
                complete = false;
                break;
            }

            offset += 8 + pageBytes;
        }

        return result;
    }

    private static ChangerElement ParseDescriptor(Cdb.ElementType type, ReadOnlySpan<byte> d, bool primaryTag, bool alternateTag)
    {
        var flags = d[2];
        var position = 12;
        string? volumeTag = null;
        if (primaryTag && d.Length >= position + 36)
        {
            volumeTag = InquiryData.Ascii(d.Slice(position, 32));
            if (volumeTag.Length == 0)
            {
                volumeTag = null;
            }

            position += 36;
        }

        if (alternateTag && d.Length >= position + 36)
        {
            position += 36;
        }

        string? deviceId = null;
        if (type == Cdb.ElementType.DataTransfer && d.Length >= position + 4)
        {
            var length = d[position + 3];
            if (length > 0 && d.Length >= position + 4 + length)
            {
                deviceId = VpdPages.Designator(d.Slice(position, 4 + length));
            }
        }

        return new ChangerElement(
            type,
            BinaryPrimitives.ReadUInt16BigEndian(d),
            (flags & 0x01) != 0,
            type == Cdb.ElementType.MediumTransport || (flags & 0x08) != 0,
            (flags & 0x04) != 0,
            d[4],
            d[5],
            (d[9] & 0x80) != 0,
            BinaryPrimitives.ReadUInt16BigEndian(d[10..]),
            volumeTag,
            deviceId);
    }
}

/// <summary>Medium auxiliary memory attributes (SPC-4 7.4, MAM) of a cartridge.</summary>
public static class MamAttributes
{
    public const ushort RemainingCapacity = 0x0000;
    public const ushort MaximumCapacity = 0x0001;
    public const ushort MediumManufacturer = 0x0400;
    public const ushort MediumSerialNumber = 0x0401;
    public const ushort MediumType = 0x0408;
    public const ushort ApplicationVendor = 0x0800;
    public const ushort ApplicationName = 0x0801;
    public const ushort ApplicationVersion = 0x0802;
    public const ushort UserMediumTextLabel = 0x0803;
    public const ushort Barcode = 0x0806;
    public const ushort MediaPool = 0x0808;

    /// <summary>Medium type values of attribute 0408h.</summary>
    public const byte MediumTypeData = 0x00;
    public const byte MediumTypeCleaning = 0x01;
    public const byte MediumTypeWorm = 0x80;

    public static IReadOnlyDictionary<ushort, byte[]> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("READ ATTRIBUTE data is shorter than its header.");
        }

        var end = Math.Min(data.Length, 4 + (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), int.MaxValue - 4));
        var result = new Dictionary<ushort, byte[]>();
        for (var offset = 4; offset + 5 <= end;)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]);
            if (offset + 5 + length > end)
            {
                break;
            }

            result[id] = data.Slice(offset + 5, length).ToArray();
            offset += 5 + length;
        }

        return result;
    }

    /// <summary>A WRITE ATTRIBUTE parameter list. Text attributes are padded with spaces to their fixed length.</summary>
    public static byte[] Build(IEnumerable<(ushort Id, byte Format, byte[] Value)> attributes)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[4]);
        Span<byte> header = stackalloc byte[5];
        foreach (var (id, format, value) in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header, id);
            header[2] = format;
            BinaryPrimitives.WriteUInt16BigEndian(header[3..], (ushort)value.Length);
            stream.Write(header);
            stream.Write(value);
        }

        var list = stream.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(list, (uint)(list.Length - 4));
        return list;
    }

    /// <summary>An ASCII attribute of fixed <paramref name="length"/>, space padded (format 01b).</summary>
    public static (ushort Id, byte Format, byte[] Value) Ascii(ushort id, string text, int length)
    {
        var value = Enumerable.Repeat((byte)' ', length).ToArray();
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, length)).CopyTo(value);
        return (id, 0x01, value);
    }

    public static string? Text(IReadOnlyDictionary<ushort, byte[]> attributes, ushort id) =>
        attributes.TryGetValue(id, out var value) ? Encoding.UTF8.GetString(value).TrimEnd(' ', '\0') : null;

    public static long? Number(IReadOnlyDictionary<ushort, byte[]> attributes, ushort id)
    {
        if (!attributes.TryGetValue(id, out var value) || value.Length is 0 or > 8)
        {
            return null;
        }

        long n = 0;
        foreach (var b in value)
        {
            n = (n << 8) | b;
        }

        return n;
    }
}

/// <summary>The drive's own command timeouts (REPORT SUPPORTED OPERATION CODES with RCTD, SPC-4 6.35).</summary>
public static class CommandTimeouts
{
    /// <summary>Recommended timeouts by operation code (and service action), in seconds.</summary>
    public static IReadOnlyDictionary<(byte OpCode, ushort ServiceAction), TimeSpan> Parse(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<(byte, ushort), TimeSpan>();
        if (data.Length < 4)
        {
            return result;
        }

        var end = Math.Min(data.Length, 4 + (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data), int.MaxValue - 4));
        for (var offset = 4; offset + 8 <= end;)
        {
            var opCode = data[offset];
            var serviceAction = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            var hasTimeouts = (data[offset + 5] & 0x02) != 0;
            var next = offset + 8;
            if (hasTimeouts && next + 12 <= end)
            {
                var recommended = BinaryPrimitives.ReadUInt32BigEndian(data[(next + 8)..]);
                if (recommended > 0)
                {
                    result[(opCode, serviceAction)] = TimeSpan.FromSeconds(recommended);
                }

                next += 12;
            }

            offset = next;
        }

        return result;
    }
}
