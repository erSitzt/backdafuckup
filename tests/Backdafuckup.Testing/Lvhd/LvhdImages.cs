using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Backdafuckup.Lvhd;

namespace Backdafuckup.Testing.Lvhd;

/// <summary>A byte array as a block source.</summary>
public sealed class MemorySource(byte[] data) : IBlockSource
{
    public long Size => data.Length;

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        data.AsMemory((int)offset, destination.Length).CopyTo(destination);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds dynamic and differencing VHDs after the Microsoft specification (independent of the reader).</summary>
public static class VhdBuilder
{
    public const int Sector = 512;

    /// <param name="sectors">Written sectors: sector number → 512 bytes.</param>
    public static byte[] Build(long capacity, int blockSize, Guid uniqueId, Guid? parentId, IReadOnlyDictionary<long, byte[]> sectors, int? diskTypeOverride = null)
    {
        var entries = (int)((capacity + blockSize - 1) / blockSize);
        var batBytes = ((entries * 4) + Sector - 1) / Sector * Sector;
        var bitmapBytes = ((blockSize / Sector / 8) + Sector - 1) / Sector * Sector;
        var blocks = sectors.Keys.Select(s => s * Sector / blockSize).Distinct().Order().ToList();
        var image = new byte[512 + 1024 + batBytes + (blocks.Count * (bitmapBytes + blockSize)) + 512];
        var footer = Footer(capacity, uniqueId, diskTypeOverride ?? (parentId is null ? 3 : 4));
        footer.CopyTo(image, 0);
        footer.CopyTo(image, image.Length - 512);

        var header = new byte[1024];
        "cxsparse"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), ulong.MaxValue);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), 1536);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 0x00010000);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(28), entries);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(32), blockSize);
        if (parentId is { } parent)
        {
            parent.ToByteArray(bigEndian: true).CopyTo(header, 40);
            Encoding.BigEndianUnicode.GetBytes($"VHD-{parent}").CopyTo(header, 64);
        }

        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(36), Checksum(header, 36));
        header.CopyTo(image, 512);

        var bat = image.AsSpan(1536, batBytes);
        bat.Fill(0xFF);
        var next = 1536L + batBytes;
        foreach (var block in blocks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bat[(int)(block * 4)..], (uint)(next / Sector));
            foreach (var (sector, data) in sectors.Where(s => s.Key * Sector / blockSize == block))
            {
                var inBlock = (int)(sector - (block * blockSize / Sector));
                image[next + (inBlock / 8)] |= (byte)(0x80 >> (inBlock % 8));
                data.CopyTo(image, next + bitmapBytes + (inBlock * Sector));
            }

            next += bitmapBytes + blockSize;
        }

        return image;
    }

    private static byte[] Footer(long capacity, Guid uniqueId, int type)
    {
        var footer = new byte[512];
        "conectix"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(16), 512);
        "tap\0"u8.CopyTo(footer.AsSpan(28));
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(40), capacity);
        BinaryPrimitives.WriteInt64BigEndian(footer.AsSpan(48), capacity);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(60), (uint)type);
        uniqueId.ToByteArray(bigEndian: true).CopyTo(footer, 68);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), Checksum(footer, 64));
        return footer;
    }

    private static uint Checksum(byte[] data, int offset)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (i < offset || i >= offset + 4)
            {
                sum += data[i];
            }
        }

        return ~sum;
    }
}

/// <summary>Builds an LVM2 physical volume after the public format description, with logical volumes placed one after another.</summary>
public static class PvBuilder
{
    public const int ExtentSectors = 128;
    public const long ExtentBytes = ExtentSectors * 512L;
    private const long PeStartSectors = 2048;
    private const long MdaOffset = 4096;
    private const long MdaSize = 1L << 20;
    private const string PvId = "abcdef0123456789abcdef0123456789";

    /// <summary>An LV: its name and content, or a name with a size only (journal and other volumes), or several segments.</summary>
    public sealed record Volume(string Name, byte[]? Content = null, long Extents = 1, bool Striped = false, int Segments = 1);

    public static byte[] Build(string vgName, IReadOnlyList<Volume> volumes, int seqno = 7, bool corruptText = false, string? olderText = null)
    {
        var placed = new List<(Volume Volume, long Extents, long FirstExtent)>();
        var nextExtent = 0L;
        foreach (var volume in volumes)
        {
            var extents = volume.Content is null ? volume.Extents : Math.Max(1, (volume.Content.Length + ExtentBytes - 1) / ExtentBytes);
            extents = (extents + volume.Segments - 1) / volume.Segments * volume.Segments;
            placed.Add((volume, extents, nextExtent));
            nextExtent += extents + 1; // a free extent between volumes, so mapping errors show
        }

        var image = new byte[(PeStartSectors * 512) + (nextExtent * ExtentBytes)];
        var text = Text(vgName, placed, seqno, nextExtent);
        foreach (var (volume, extents, first) in placed.Where(p => p.Volume.Content is not null))
        {
            // Segments lie on the PV in reverse order, so that a reader that ignores them reads wrong data.
            var per = extents / volume.Segments;
            for (var segment = 0; segment < volume.Segments; segment++)
            {
                var from = segment * per * ExtentBytes;
                var length = (int)Math.Min(per * ExtentBytes, Math.Max(0, volume.Content!.Length - from));
                if (length > 0)
                {
                    volume.Content.AsSpan((int)from, length).CopyTo(image.AsSpan((int)((PeStartSectors * 512) + ((first + ((volume.Segments - 1 - segment) * per)) * ExtentBytes))));
                }
            }
        }

        // Label in sector 1, PV header after it.
        var label = image.AsSpan(512, 512);
        "LABELONE"u8.CopyTo(label);
        BinaryPrimitives.WriteUInt64LittleEndian(label[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(label[20..], 32);
        "LVM2 001"u8.CopyTo(label[24..]);
        var pv = label[32..];
        Encoding.ASCII.GetBytes(PvId).CopyTo(pv);
        BinaryPrimitives.WriteUInt64LittleEndian(pv[32..], (ulong)image.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(pv[40..], (ulong)(PeStartSectors * 512));
        BinaryPrimitives.WriteUInt64LittleEndian(pv[72..], (ulong)MdaOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(pv[80..], (ulong)MdaSize);
        BinaryPrimitives.WriteUInt32LittleEndian(label[16..], Lvm2Reader.Crc(0xf597a6cf, label[20..]));

        // Metadata area: header, then the texts (an older one first if given).
        var mda = image.AsSpan((int)MdaOffset, 512);
        " LVM2 x[5A%r0N*>"u8.CopyTo(mda[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(mda[20..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(mda[24..], (ulong)MdaOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(mda[32..], (ulong)MdaSize);
        var position = 512L;
        var slot = 40;
        foreach (var t in new[] { olderText, text }.OfType<string>())
        {
            var bytes = Encoding.UTF8.GetBytes(t);
            bytes.CopyTo(image, MdaOffset + position);
            BinaryPrimitives.WriteUInt64LittleEndian(mda[slot..], (ulong)position);
            BinaryPrimitives.WriteUInt64LittleEndian(mda[(slot + 8)..], (ulong)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(mda[(slot + 16)..], Lvm2Reader.Crc(0xf597a6cf, bytes) ^ (corruptText && t == text ? 1u : 0u));
            position += (bytes.Length + 511) / 512 * 512;
            slot += 24;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(mda, Lvm2Reader.Crc(0xf597a6cf, mda[4..]));
        return image;
    }

    public static string Text(string vgName, IEnumerable<(Volume Volume, long Extents, long FirstExtent)> placed, int seqno, long peCount)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{vgName} {{\nid = \"vgid01-0000-0000-0000-0000-0000-000001\"\nseqno = {seqno}\nformat = \"lvm2\" # written by the test\n");
        builder.Append(CultureInfo.InvariantCulture, $"status = [\"RESIZEABLE\", \"READ\", \"WRITE\"]\nflags = []\nextent_size = {ExtentSectors}\nmax_lv = 0\nmax_pv = 0\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"physical_volumes {{\n\npv0 {{\nid = \"abcdef-0123-4567-89ab-cdef-0123-456789\"\ndevice = \"/dev/sdb\"\nstatus = [\"ALLOCATABLE\"]\nflags = []\npe_start = {PeStartSectors}\npe_count = {peCount}\n}}\n}}\n\n");
        builder.Append("logical_volumes {\n\n");
        foreach (var (volume, extents, first) in placed)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{volume.Name} {{\nid = \"lvid-{volume.Name.GetHashCode(StringComparison.Ordinal):x8}\"\nstatus = [\"READ\", \"WRITE\", \"VISIBLE\"]\nflags = []\ntags = [\"xs_ver=1\"]\n");
            var segments = volume.Segments;
            builder.Append(CultureInfo.InvariantCulture, $"segment_count = {segments}\n");
            var per = extents / segments;
            for (var s = 0; s < segments; s++)
            {
                builder.Append(CultureInfo.InvariantCulture, $"\nsegment{s + 1} {{\nstart_extent = {per * s}\nextent_count = {per}\n\ntype = \"striped\"\nstripe_count = {(volume.Striped ? 2 : 1)}\n\nstripes = [\n\"pv0\", {first + ((segments - 1 - s) * per)}{(volume.Striped ? ",\n\"pv1\", 0" : string.Empty)}\n]\n}}\n");
            }

            builder.Append("}\n\n");
        }

        builder.Append("}\n}\n# Generated by LVM2 version 2.02.180(2)-RHEL7\n\ncontents = \"Text Format Volume Group\"\nversion = 1\n\ndescription = \"\"\n\ncreation_host = \"xcp1\"\ncreation_time = 1763500000\n");
        return builder.ToString();
    }
}
