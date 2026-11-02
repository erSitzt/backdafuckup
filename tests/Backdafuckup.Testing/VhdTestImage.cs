using System.Buffers.Binary;

namespace Backdafuckup.Testing;

public sealed record VhdTestOptions
{
    public int BlockSize { get; init; } = 2 * 1024 * 1024;

    /// <summary>Mark only sectors with data as used in the sector bitmaps (default: all sectors of an allocated block).</summary>
    public bool SparseBitmaps { get; init; }

    /// <summary>Mark the first non-zero sector of the first allocated block as unused (a stream a reader must reject).</summary>
    public bool CorruptBitmap { get; init; }

    /// <summary>Leave out the footer copy at the end.</summary>
    public bool OmitTrailingFooter { get; init; }
}

/// <summary>
/// Builds a dynamic VHD stream the way an export of a VDI looks (Microsoft VHD specification v1.0): footer copy,
/// dynamic header, BAT, allocated blocks in disk order, footer. All-zero blocks stay unallocated.
/// </summary>
public static class VhdTestImage
{
    private const int Sector = 512;

    public static byte[] Create(byte[] disk, VhdTestOptions? options = null)
    {
        options ??= new VhdTestOptions();
        var blockSize = options.BlockSize;
        var entries = (int)((disk.LongLength + blockSize - 1) / blockSize);
        var batBytes = RoundUp(entries * 4, Sector);
        var bitmapBytes = RoundUp((blockSize / Sector + 7) / 8, Sector);
        const int tableOffset = Sector + 1024;

        var footer = Footer(disk.LongLength);
        using var output = new MemoryStream();
        output.Write(footer);
        output.Write(Header(tableOffset, entries, blockSize));

        var bat = new byte[batBytes];
        bat.AsSpan().Fill(0xFF);
        var blocks = new List<int>();
        for (var i = 0; i < entries; i++)
        {
            if (Slice(disk, i, blockSize).ContainsAnyExcept((byte)0))
            {
                blocks.Add(i);
            }
        }

        long position = tableOffset + batBytes;
        foreach (var block in blocks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bat.AsSpan(block * 4), (uint)(position / Sector));
            position += bitmapBytes + blockSize;
        }

        output.Write(bat);
        var corrupted = false;
        foreach (var block in blocks)
        {
            var data = new byte[blockSize];
            Slice(disk, block, blockSize).CopyTo(data);
            var bitmap = new byte[bitmapBytes];
            for (var sector = 0; sector < blockSize / Sector; sector++)
            {
                var hasData = data.AsSpan(sector * Sector, Sector).ContainsAnyExcept((byte)0);
                var used = !options.SparseBitmaps || hasData;
                if (options.CorruptBitmap && !corrupted && hasData)
                {
                    used = false;
                    corrupted = true;
                }

                if (used)
                {
                    bitmap[sector >> 3] |= (byte)(0x80 >> (sector & 7));
                }
            }

            output.Write(bitmap);
            output.Write(data);
        }

        if (!options.OmitTrailingFooter)
        {
            output.Write(footer);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Reads a complete dynamic VHD back into the raw disk, independently of backdafuckup's own reader and writer. It
    /// checks what an importer has to rely on: footer at the end and its identical copy at the start (cookie, checksum,
    /// dynamic disk type), dynamic header (cookie, checksum, BAT size matching the disk size), and blocks that lie inside
    /// the file. Sectors whose bitmap bit is clear stay zero.
    /// </summary>
    public static byte[] Read(byte[] vhd)
    {
        if (vhd.Length < (3 * Sector) + 1024)
        {
            throw new InvalidDataException($"A dynamic VHD needs at least {(3 * Sector) + 1024} bytes, got {vhd.Length}.");
        }

        var footer = vhd.AsSpan(vhd.Length - Sector, Sector);
        if (!footer.SequenceEqual(vhd.AsSpan(0, Sector)))
        {
            throw new InvalidDataException("The footer copy at the start differs from the footer at the end.");
        }

        Check(footer, "conectix"u8, 64, "footer");
        if (BinaryPrimitives.ReadUInt32BigEndian(footer[60..]) != 3)
        {
            throw new InvalidDataException("Not a dynamic VHD (disk type 3).");
        }

        var size = BinaryPrimitives.ReadInt64BigEndian(footer[48..]);
        var header = vhd.AsSpan(checked((int)BinaryPrimitives.ReadInt64BigEndian(footer[16..])), 1024);
        Check(header, "cxsparse"u8, 36, "dynamic header");
        var tableOffset = checked((int)BinaryPrimitives.ReadInt64BigEndian(header[16..]));
        var entries = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        var blockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[32..]);
        if (entries != (size + blockSize - 1) / blockSize)
        {
            throw new InvalidDataException($"The BAT has {entries} entries, but a disk of {size} bytes needs {(size + blockSize - 1) / blockSize}.");
        }

        var bitmapBytes = RoundUp(blockSize / Sector / 8, Sector);
        var disk = new byte[size];
        for (var block = 0; block < entries; block++)
        {
            var entry = BinaryPrimitives.ReadUInt32BigEndian(vhd.AsSpan(tableOffset + (block * 4)));
            if (entry == 0xFFFFFFFF)
            {
                continue;
            }

            var bitmapStart = (long)entry * Sector;
            var dataStart = bitmapStart + bitmapBytes;
            if (bitmapStart < tableOffset + (entries * 4) || dataStart + blockSize > vhd.Length - Sector)
            {
                throw new InvalidDataException($"Block {block} at byte {bitmapStart} lies outside the data area of the file.");
            }

            for (var sector = 0; sector < blockSize / Sector; sector++)
            {
                var offset = ((long)block * blockSize) + ((long)sector * Sector);
                if (offset >= size)
                {
                    break;
                }

                if ((vhd[bitmapStart + (sector >> 3)] & (0x80 >> (sector & 7))) != 0)
                {
                    vhd.AsSpan((int)(dataStart + ((long)sector * Sector)), Sector).CopyTo(disk.AsSpan((int)offset));
                }
            }
        }

        return disk;
    }

    private static void Check(ReadOnlySpan<byte> structure, ReadOnlySpan<byte> cookie, int checksumOffset, string what)
    {
        if (!structure[..8].SequenceEqual(cookie))
        {
            throw new InvalidDataException($"The {what} has no '{System.Text.Encoding.ASCII.GetString(cookie)}' cookie.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(structure[checksumOffset..]) != Checksum(structure.ToArray(), checksumOffset))
        {
            throw new InvalidDataException($"The {what} checksum is wrong.");
        }
    }

    private static ReadOnlySpan<byte> Slice(byte[] disk, int block, int blockSize)
    {
        var start = (long)block * blockSize;
        return disk.AsSpan((int)start, (int)Math.Min(blockSize, disk.LongLength - start));
    }

    private static byte[] Footer(long size)
    {
        var footer = new byte[Sector];
        "conectix"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(16), Sector);
        "bdfu"u8.CopyTo(footer.AsSpan(28));
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(40), size);
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(48), size);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(60), 3);
        Guid.NewGuid().TryWriteBytes(footer.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), Checksum(footer, 64));
        return footer;
    }

    private static byte[] Header(long tableOffset, int entries, int blockSize)
    {
        var header = new byte[1024];
        "cxsparse"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), ulong.MaxValue);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), (uint)entries);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(32), (uint)blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), Checksum(header, 36));
        return header;
    }

    private static uint Checksum(byte[] structure, int checksumOffset)
    {
        uint sum = 0;
        for (var i = 0; i < structure.Length; i++)
        {
            if (i < checksumOffset || i >= checksumOffset + 4)
            {
                sum += structure[i];
            }
        }

        return ~sum;
    }

    private static int RoundUp(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
}
