using System.Buffers.Binary;

namespace Backdafuckup.Tape.Scsi;

public enum SenseKey : byte
{
    NoSense = 0x0,
    RecoveredError = 0x1,
    NotReady = 0x2,
    MediumError = 0x3,
    HardwareError = 0x4,
    IllegalRequest = 0x5,
    UnitAttention = 0x6,
    DataProtect = 0x7,
    BlankCheck = 0x8,
    VendorSpecific = 0x9,
    CopyAborted = 0xA,
    AbortedCommand = 0xB,
    VolumeOverflow = 0xD,
    Miscompare = 0xE,
    Completed = 0xF,
}

/// <summary>
/// Sense data in fixed (70h/71h) or descriptor (72h/73h) format (SPC-4 4.4), with the stream bits of sequential-access
/// devices (FILEMARK, EOM, ILI) and the INFORMATION field.
/// </summary>
public sealed class SenseData
{
    private SenseData(byte[] raw, byte responseCode, SenseKey key, byte asc, byte ascq, bool filemark, bool endOfMedium, bool incorrectLength, long? information)
    {
        Raw = raw;
        ResponseCode = responseCode;
        Key = key;
        Asc = asc;
        Ascq = ascq;
        Filemark = filemark;
        EndOfMedium = endOfMedium;
        IncorrectLength = incorrectLength;
        Information = information;
    }

    public byte[] Raw { get; }

    public byte ResponseCode { get; }

    /// <summary>The sense refers to an earlier command (71h/73h), e.g. a buffered write that failed.</summary>
    public bool Deferred => ResponseCode is 0x71 or 0x73;

    public SenseKey Key { get; }

    public byte Asc { get; }

    public byte Ascq { get; }

    /// <summary>A filemark was read (SPACE or READ stopped at it).</summary>
    public bool Filemark { get; }

    /// <summary>End of medium, or early warning on a write (SSC).</summary>
    public bool EndOfMedium { get; }

    /// <summary>The block on tape had another length than requested.</summary>
    public bool IncorrectLength { get; }

    /// <summary>The INFORMATION field when valid: e.g. the residue of a READ, or of a SPACE.</summary>
    public long? Information { get; }

    public ushort AdditionalCode => (ushort)((Asc << 8) | Ascq);

    public bool Is(SenseKey key, byte asc, byte ascq) => Key == key && Asc == asc && Ascq == ascq;

    public bool Is(SenseKey key, byte asc) => Key == key && Asc == asc;

    /// <summary>Parses sense data; null when there is none (all zero or an unknown response code).</summary>
    public static SenseData? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return null;
        }

        var raw = data.ToArray();
        var responseCode = (byte)(data[0] & 0x7F);
        switch (responseCode)
        {
            case 0x70 or 0x71:
            {
                var valid = (data[0] & 0x80) != 0;
                var key = (SenseKey)(data[2] & 0x0F);
                var asc = data.Length > 12 ? data[12] : (byte)0;
                var ascq = data.Length > 13 ? data[13] : (byte)0;
                long? information = valid ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(3, 4)) : null;
                return new SenseData(raw, responseCode, key, asc, ascq, (data[2] & 0x80) != 0, (data[2] & 0x40) != 0, (data[2] & 0x20) != 0, information);
            }

            case 0x72 or 0x73:
            {
                var key = (SenseKey)(data[1] & 0x0F);
                bool filemark = false, eom = false, ili = false;
                long? information = null;
                var end = Math.Min(data.Length, 8 + data[7]);
                for (var offset = 8; offset + 2 <= end;)
                {
                    var type = data[offset];
                    var length = data[offset + 1];
                    var descriptor = data.Slice(offset, Math.Min(2 + length, end - offset));
                    if (type == 0x00 && descriptor.Length >= 12 && (descriptor[2] & 0x80) != 0)
                    {
                        information = BinaryPrimitives.ReadInt64BigEndian(descriptor.Slice(4, 8));
                    }
                    else if (type == 0x04 && descriptor.Length >= 4)
                    {
                        filemark = (descriptor[3] & 0x80) != 0;
                        eom = (descriptor[3] & 0x40) != 0;
                        ili = (descriptor[3] & 0x20) != 0;
                    }

                    offset += 2 + length;
                }

                return new SenseData(raw, responseCode, key, data[2], data[3], filemark, eom, ili, information);
            }

            default:
                return null;
        }
    }

    /// <summary>Builds fixed-format sense data (for fakes and tests).</summary>
    public static byte[] BuildFixed(SenseKey key, byte asc, byte ascq, bool filemark = false, bool endOfMedium = false, bool incorrectLength = false, int? information = null)
    {
        var sense = new byte[18];
        sense[0] = (byte)(0x70 | (information is null ? 0 : 0x80));
        sense[2] = (byte)((byte)key | (filemark ? 0x80 : 0) | (endOfMedium ? 0x40 : 0) | (incorrectLength ? 0x20 : 0));
        if (information is { } info)
        {
            BinaryPrimitives.WriteInt32BigEndian(sense.AsSpan(3), info);
        }

        sense[7] = 10;
        sense[12] = asc;
        sense[13] = ascq;
        return sense;
    }

    /// <summary>A short text: key, code and what it means, e.g. "MEDIUM ERROR 11/00 (unrecovered read error)".</summary>
    public override string ToString()
    {
        var bits = string.Concat(Filemark ? ", filemark" : "", EndOfMedium ? ", end of medium" : "", IncorrectLength ? ", incorrect length" : "", Deferred ? ", deferred" : "");
        var meaning = SenseCodes.Describe(Key, Asc, Ascq);
        return $"{SenseCodes.Name(Key)} {Asc:X2}/{Ascq:X2}{(meaning is null ? "" : $" ({meaning})")}{bits}";
    }
}

/// <summary>Plain texts for the sense codes tape drives and changers report (SPC-4 Annex D, SSC, SMC).</summary>
public static class SenseCodes
{
    public static string Name(SenseKey key) => key switch
    {
        SenseKey.NoSense => "NO SENSE",
        SenseKey.RecoveredError => "RECOVERED ERROR",
        SenseKey.NotReady => "NOT READY",
        SenseKey.MediumError => "MEDIUM ERROR",
        SenseKey.HardwareError => "HARDWARE ERROR",
        SenseKey.IllegalRequest => "ILLEGAL REQUEST",
        SenseKey.UnitAttention => "UNIT ATTENTION",
        SenseKey.DataProtect => "DATA PROTECT",
        SenseKey.BlankCheck => "BLANK CHECK",
        SenseKey.AbortedCommand => "ABORTED COMMAND",
        SenseKey.VolumeOverflow => "VOLUME OVERFLOW",
        _ => $"sense key {(byte)key:X}",
    };

    public static string? Describe(SenseKey key, byte asc, byte ascq) => (asc, ascq) switch
    {
        (0x00, 0x01) => "filemark detected",
        (0x00, 0x02) => "end of partition or medium detected",
        (0x00, 0x04) => "beginning of partition or medium detected",
        (0x00, 0x05) => "end of data detected",
        (0x00, 0x07) => "programmable early warning detected",
        (0x00, 0x00) when key == SenseKey.BlankCheck => "end of data",
        (0x00, 0x00) => null,
        (0x04, 0x00) => "not ready, cause not reportable",
        (0x04, 0x01) => "becoming ready",
        (0x04, 0x02) => "not ready, a LOAD is needed",
        (0x04, 0x03) => "not ready, manual intervention required",
        (0x04, 0x07) => "operation in progress",
        (0x04, 0x12) => "offline",
        (0x0C, 0x00) => "write error",
        (0x11, 0x00) => "unrecovered read error",
        (0x14, 0x00) => "recorded entity not found",
        (0x14, 0x03) => "end of data not found",
        (0x15, 0x01) => "mechanical positioning error",
        (0x1A, 0x00) => "parameter list length error",
        (0x20, 0x00) => "invalid command operation code",
        (0x21, 0x01) => "invalid element address",
        (0x24, 0x00) => "invalid field in CDB",
        (0x25, 0x00) => "logical unit not supported",
        (0x26, 0x00) => "invalid field in parameter list",
        (0x26, 0x10) => "data decryption key fail limit reached",
        (0x27, 0x00) => "write protected",
        (0x28, 0x00) => "not ready to ready change, medium may have changed",
        (0x28, 0x01) => "import or export element accessed",
        (0x29, _) => "power on, reset or bus device reset occurred",
        (0x2A, 0x01) => "mode parameters changed",
        (0x2A, 0x11) => "encryption parameters changed",
        (0x30, 0x00) => "incompatible medium installed",
        (0x30, 0x01) => "cannot read medium, unknown format",
        (0x30, 0x02) => "cannot read medium, incompatible format",
        (0x30, 0x03) => "cleaning cartridge installed",
        (0x30, 0x05) => "cannot write medium, incompatible format",
        (0x30, 0x0C) => "WORM medium, overwrite attempted",
        (0x30, 0x0D) => "WORM medium, integrity check failed",
        (0x31, 0x00) => "medium format corrupted",
        (0x37, 0x00) => "rounded parameter",
        (0x3A, _) => "medium not present",
        (0x3B, 0x00) => "sequential positioning error",
        (0x3B, 0x0D) => "medium destination element full",
        (0x3B, 0x0E) => "medium source element empty",
        (0x3B, 0x11) => "medium magazine not accessible",
        (0x3B, 0x12) => "medium magazine removed",
        (0x3F, 0x01) => "microcode has been changed",
        (0x44, 0x00) => "internal target failure",
        (0x50, 0x00) => "write append error",
        (0x52, 0x00) => "cartridge fault",
        (0x53, 0x00) => "media load or eject failed",
        (0x53, 0x02) => "medium removal prevented",
        (0x5B, 0x01) => "threshold condition met",
        (0x5D, _) => "failure prediction threshold exceeded",
        (0x74, 0x00) => "security error",
        (0x74, 0x01) => "unable to decrypt data",
        (0x74, 0x02) => "unencrypted data encountered while decrypting",
        (0x74, 0x03) => "incorrect data encryption key",
        (0x74, 0x04) => "cryptographic integrity validation failed",
        (0x74, 0x05) => "error decrypting data",
        _ => null,
    };
}
