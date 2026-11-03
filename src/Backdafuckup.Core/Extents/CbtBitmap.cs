namespace Backdafuckup.Core.Extents;

/// <summary>Bit order inside each byte of a changed-block bitmap.</summary>
public enum CbtBitOrder
{
    /// <summary>Bit 7 (0x80) of byte 0 describes block 0.</summary>
    MostSignificantBitFirst,

    /// <summary>Bit 0 (0x01) of byte 0 describes block 0.</summary>
    LeastSignificantBitFirst,
}

/// <summary>
/// Decodes the changed-block bitmap returned by XAPI's <c>VDI.list_changed_blocks</c>:
/// base64, one bit per 64 KiB block, the first bit is the first block, 1 = changed.
/// </summary>
/// <remarks>
/// XenServer documents the first bit as the most significant bit of the first byte, and the storage manager (sm,
/// <c>VDI.list_changed_blocks</c>) reverses the bits of each byte of blktap's little-endian bitmap to give exactly that.
/// <see cref="DecodeByteRanges"/> does not depend on the order at all: a byte always describes the same 512 KiB, only
/// the bits within it would move. That is what backups use unless an order is configured.
/// </remarks>
public static class CbtBitmap
{
    public const int BlockSize = 64 * 1024;

    /// <summary>The part of the disk one byte of the bitmap describes (8 blocks).</summary>
    public const int ByteSpan = 8 * BlockSize;

    /// <summary>
    /// The changed parts of the disk in units of whole bitmap bytes: every byte with any bit set marks its 512 KiB
    /// (clipped at the end of the disk). Covers every changed block whatever the bit order inside a byte is; at chunk
    /// sizes that are multiples of 512 KiB it selects exactly the chunks a bit-exact decoding would.
    /// </summary>
    public static IReadOnlyList<DiskExtent> DecodeByteRanges(ReadOnlySpan<byte> bitmap, long diskSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskSize);
        var requiredBytes = RequiredBytes(diskSize);
        if (bitmap.Length < requiredBytes)
        {
            throw new FormatException(
                $"Changed-block bitmap too short: {bitmap.Length} bytes for {(diskSize + BlockSize - 1) / BlockSize} blocks (need {requiredBytes}).");
        }

        var extents = new List<DiskExtent>();
        long runStart = -1;
        for (long i = 0; i < requiredBytes; i++)
        {
            var set = bitmap[(int)i] != 0;
            if (set && runStart < 0)
            {
                runStart = i;
            }
            else if (!set && runStart >= 0)
            {
                extents.Add(ByteRange(runStart, i, diskSize));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            extents.Add(ByteRange(runStart, requiredBytes, diskSize));
        }

        return extents;
    }

    /// <summary>Bytes CBT reports as changed: set bits times 64 KiB, at most the disk size (independent of the bit order).</summary>
    public static long ChangedBytes(ReadOnlySpan<byte> bitmap, long diskSize)
    {
        var requiredBytes = (int)Math.Min(bitmap.Length, RequiredBytes(diskSize));
        long blocks = 0;
        for (var i = 0; i < requiredBytes; i++)
        {
            blocks += System.Numerics.BitOperations.PopCount(bitmap[i]);
        }

        return Math.Min(blocks * BlockSize, diskSize);
    }

    private static long RequiredBytes(long diskSize) => (((diskSize + BlockSize - 1) / BlockSize) + 7) / 8;

    private static DiskExtent ByteRange(long firstByte, long endByte, long diskSize)
    {
        var offset = firstByte * ByteSpan;
        var end = Math.Min(endByte * ByteSpan, diskSize);
        return new DiskExtent(offset, end - offset);
    }

    public static IReadOnlyList<DiskExtent> DecodeBase64(string base64, long diskSize, CbtBitOrder order)
    {
        ArgumentNullException.ThrowIfNull(base64);
        return Decode(Convert.FromBase64String(base64), diskSize, order);
    }

    public static IReadOnlyList<DiskExtent> Decode(ReadOnlySpan<byte> bitmap, long diskSize, CbtBitOrder order)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(diskSize);
        var blockCount = (diskSize + BlockSize - 1) / BlockSize;
        var requiredBytes = (blockCount + 7) / 8;
        if (bitmap.Length < requiredBytes)
        {
            throw new FormatException(
                $"Changed-block bitmap too short: {bitmap.Length} bytes for {blockCount} blocks (need {requiredBytes}).");
        }

        var extents = new List<DiskExtent>();
        long runStart = -1;
        for (long block = 0; block < blockCount; block++)
        {
            var b = bitmap[(int)(block / 8)];
            var bit = (int)(block % 8);
            var set = order == CbtBitOrder.MostSignificantBitFirst
                ? (b & (0x80 >> bit)) != 0
                : (b & (1 << bit)) != 0;

            if (set && runStart < 0)
            {
                runStart = block;
            }
            else if (!set && runStart >= 0)
            {
                extents.Add(ToExtent(runStart, block, diskSize));
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            extents.Add(ToExtent(runStart, blockCount, diskSize));
        }

        return extents;
    }

    /// <summary>Encodes extents as a bitmap (for tests and fakes).</summary>
    public static byte[] Encode(IEnumerable<DiskExtent> extents, long diskSize, CbtBitOrder order)
    {
        var blockCount = (diskSize + BlockSize - 1) / BlockSize;
        var bitmap = new byte[(blockCount + 7) / 8];
        foreach (var extent in ExtentMath.Normalize(extents))
        {
            var end = Math.Min(extent.End, diskSize);
            for (var block = extent.Offset / BlockSize; block * BlockSize < end; block++)
            {
                var bit = (int)(block % 8);
                bitmap[block / 8] |= (byte)(order == CbtBitOrder.MostSignificantBitFirst ? 0x80 >> bit : 1 << bit);
            }
        }

        return bitmap;
    }

    private static DiskExtent ToExtent(long firstBlock, long endBlock, long diskSize)
    {
        var offset = firstBlock * BlockSize;
        var end = Math.Min(endBlock * BlockSize, diskSize);
        return new DiskExtent(offset, end - offset);
    }
}
