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
/// The bit order inside a byte is configurable until it is confirmed against a real host (see spec risk 1).
/// </summary>
public static class CbtBitmap
{
    public const int BlockSize = 64 * 1024;

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
