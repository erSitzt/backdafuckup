using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Backdafuckup.Lvhd;

/// <summary>A physical volume of a volume group: where its extents start on the device.</summary>
public sealed record LvmPhysicalVolume(string Name, string Id, long PeStartSectors, long PeCount);

/// <summary>A segment of a logical volume: its extents and where they lie on the physical volumes.</summary>
public sealed record LvmSegment(long StartExtent, long ExtentCount, string Type, IReadOnlyList<(string Pv, long StartExtent)> Stripes)
{
    /// <summary>A plain linear mapping (LVM writes it as "striped" with one stripe).</summary>
    public bool IsLinear => Type == "striped" && Stripes.Count == 1;
}

public sealed record LvmLogicalVolume(string Name, string Id, IReadOnlyList<string> Status, IReadOnlyList<string> Tags, IReadOnlyList<LvmSegment> Segments)
{
    public long ExtentCount => Segments.Sum(s => s.ExtentCount);
}

/// <summary>A volume group as its newest metadata on the device describes it.</summary>
/// <param name="ExtentSizeSectors">Size of one extent in 512-byte sectors.</param>
public sealed record LvmVolumeGroup(string Name, string Id, long Seqno, long ExtentSizeSectors, IReadOnlyList<LvmPhysicalVolume> PhysicalVolumes, IReadOnlyList<LvmLogicalVolume> LogicalVolumes)
{
    public long ExtentSizeBytes => ExtentSizeSectors * 512;

    public LvmLogicalVolume? Find(string name) => LogicalVolumes.FirstOrDefault(l => l.Name == name);
}

/// <summary>The device is not an LVM physical volume this reader understands; the message says why.</summary>
public sealed class LvmFormatException(string message) : Exception(message);

/// <summary>
/// Reads LVM2 metadata from a physical volume in user space, written from the public LVM2 on-disk format description
/// (libvslvm's documentation): the label (<c>LABELONE</c>) in one of the first four sectors, the PV header with its data and
/// metadata areas, the metadata area header with its raw locations, and the newest metadata text (highest seqno, CRC
/// checked), parsed into the volume group. Nothing is ever written, activated or handed to the system's LVM.
/// </summary>
public static class Lvm2Reader
{
    public const int SectorSize = 512;
    private const uint InitialCrc = 0xf597a6cf;
    private static readonly byte[] LabelId = "LABELONE"u8.ToArray();
    private static readonly byte[] LabelType = "LVM2 001"u8.ToArray();
    private static readonly byte[] MdaMagic = " LVM2 x[5A%r0N*>"u8.ToArray();
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static async Task<LvmVolumeGroup> ReadAsync(IBlockSource device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var head = new byte[4 * SectorSize];
        await device.ReadAsync(0, head, cancellationToken).ConfigureAwait(false);
        var (pvId, areas) = ParseLabel(head);
        LvmVolumeGroup? newest = null;
        var problems = new List<string>();
        foreach (var (offset, size) in areas)
        {
            try
            {
                foreach (var text in await ReadMetadataTextsAsync(device, offset, size, cancellationToken).ConfigureAwait(false))
                {
                    var group = Parse(text, pvId);
                    if (newest is null || group.Seqno > newest.Seqno)
                    {
                        newest = group;
                    }
                }
            }
            catch (LvmFormatException ex)
            {
                problems.Add($"metadata area at {offset}: {ex.Message}");
            }
        }

        return newest ?? throw new LvmFormatException($"No readable LVM metadata ({string.Join("; ", problems)}).");
    }

    /// <summary>The label and the PV header: the PV's id and its metadata areas (offset and size on the device).</summary>
    private static (string PvId, List<(long Offset, long Size)> Areas) ParseLabel(byte[] head)
    {
        var labelSector = Enumerable.Range(0, 4).FirstOrDefault(i => head.AsSpan(i * SectorSize, 8).SequenceEqual(LabelId), -1);
        if (labelSector < 0)
        {
            throw new LvmFormatException("No LVM label (LABELONE) in the first four sectors: the device is not an LVM physical volume.");
        }

        var label = head.AsSpan(labelSector * SectorSize, SectorSize);
        if (!label.Slice(24, 8).SequenceEqual(LabelType))
        {
            throw new LvmFormatException($"The LVM label is of type '{Encoding.ASCII.GetString(label.Slice(24, 8))}', not LVM2 001.");
        }

        if (Crc(InitialCrc, label[20..]) != BinaryPrimitives.ReadUInt32LittleEndian(label[16..]))
        {
            throw new LvmFormatException("The LVM label's checksum does not match.");
        }

        var pvHeader = label[(int)BinaryPrimitives.ReadUInt32LittleEndian(label[20..])..];
        var pvId = Encoding.ASCII.GetString(pvHeader[..32]);
        var position = 40;
        while (BinaryPrimitives.ReadUInt64LittleEndian(pvHeader[position..]) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(pvHeader[(position + 8)..]) != 0)
        {
            position += 16; // data areas: not needed, the metadata names the extents
        }

        position += 16;
        var areas = new List<(long Offset, long Size)>();
        while (position + 16 <= pvHeader.Length && BinaryPrimitives.ReadUInt64LittleEndian(pvHeader[position..]) is var offset and not 0)
        {
            areas.Add(((long)offset, (long)BinaryPrimitives.ReadUInt64LittleEndian(pvHeader[(position + 8)..])));
            position += 16;
        }

        if (areas.Count == 0)
        {
            throw new LvmFormatException("The physical volume has no metadata area.");
        }

        return (pvId, areas);
    }

    /// <summary>The logical volume as a source of its own bytes (its linear segments mapped onto the physical volume).</summary>
    public static IBlockSource Open(IBlockSource device, LvmVolumeGroup group, LvmLogicalVolume volume)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(volume);
        var pieces = new List<(long LvOffset, long PvOffset, long Length)>();
        foreach (var segment in volume.Segments.OrderBy(s => s.StartExtent))
        {
            if (!segment.IsLinear)
            {
                throw new LvmFormatException($"LV {volume.Name} has a {segment.Type} segment with {segment.Stripes.Count} stripe(s); only linear segments are read.");
            }

            var (pvName, pvExtent) = segment.Stripes[0];
            var pv = group.PhysicalVolumes.FirstOrDefault(p => p.Name == pvName) ?? throw new LvmFormatException($"LV {volume.Name} names an unknown PV {pvName}.");
            pieces.Add((segment.StartExtent * group.ExtentSizeBytes, (pv.PeStartSectors * SectorSize) + (pvExtent * group.ExtentSizeBytes), segment.ExtentCount * group.ExtentSizeBytes));
        }

        return new SegmentSource(device, pieces, volume.ExtentCount * group.ExtentSizeBytes, volume.Name);
    }

    /// <summary>The metadata texts the area header's raw locations point to (a circular buffer after the 512-byte header).</summary>
    private static async Task<List<string>> ReadMetadataTextsAsync(IBlockSource device, long areaOffset, long areaSize, CancellationToken cancellationToken)
    {
        var header = new byte[SectorSize];
        await device.ReadAsync(areaOffset, header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(4, 16).SequenceEqual(MdaMagic))
        {
            throw new LvmFormatException("the metadata area header has no LVM2 magic");
        }

        if (Crc(InitialCrc, header.AsSpan(4)) != BinaryPrimitives.ReadUInt32LittleEndian(header))
        {
            throw new LvmFormatException("the metadata area header's checksum does not match");
        }

        var texts = new List<string>();
        for (var position = 40; position + 24 <= SectorSize; position += 24)
        {
            var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(position));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(position + 8));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(position + 16));
            if (offset == 0 && size == 0)
            {
                break;
            }

            if (size <= 0 || size > areaSize || offset < SectorSize || offset >= areaSize)
            {
                throw new LvmFormatException($"a raw location ({offset}, {size}) lies outside the area of {areaSize} bytes");
            }

            var text = new byte[size];
            var first = Math.Min(size, areaSize - offset);
            await device.ReadAsync(areaOffset + offset, text.AsMemory(0, (int)first), cancellationToken).ConfigureAwait(false);
            if (first < size)
            {
                // Wrapped around the end of the circular buffer: the rest follows the header.
                await device.ReadAsync(areaOffset + SectorSize, text.AsMemory((int)first), cancellationToken).ConfigureAwait(false);
            }

            if (Crc(InitialCrc, text) != checksum)
            {
                throw new LvmFormatException($"the metadata text at {offset} has a wrong checksum");
            }

            texts.Add(Encoding.UTF8.GetString(text).TrimEnd('\0'));
        }

        return texts;
    }

    /// <summary>Parses the metadata text: one top-level section, the volume group, with its PVs and LVs.</summary>
    internal static LvmVolumeGroup Parse(string text, string? labelPvId = null)
    {
        var root = new ConfigParser(text).ParseSection();
        var (name, vg) = root.Sections.FirstOrDefault();
        if (vg is null)
        {
            throw new LvmFormatException("the metadata text names no volume group");
        }

        var pvs = vg.Child("physical_volumes")?.Sections.Select(p => new LvmPhysicalVolume(p.Key, p.Value.String("id") ?? string.Empty,
            p.Value.Number("pe_start") ?? throw new LvmFormatException($"PV {p.Key} has no pe_start"), p.Value.Number("pe_count") ?? 0)).ToList() ?? [];
        if (labelPvId is not null && pvs.Count > 0 && !pvs.Any(p => p.Id.Replace("-", string.Empty, StringComparison.Ordinal) == labelPvId))
        {
            throw new LvmFormatException("the metadata does not name the physical volume it was read from");
        }

        var lvs = vg.Child("logical_volumes")?.Sections.Select(l => new LvmLogicalVolume(
            l.Key, l.Value.String("id") ?? string.Empty, l.Value.Strings("status"), l.Value.Strings("tags"),
            [.. l.Value.Sections.Where(s => s.Key.StartsWith("segment", StringComparison.Ordinal)).Select(s => new LvmSegment(
                s.Value.Number("start_extent") ?? 0, s.Value.Number("extent_count") ?? 0, s.Value.String("type") ?? "unknown", Stripes(s.Value)))])).ToList() ?? [];
        return new LvmVolumeGroup(name, vg.String("id") ?? string.Empty, vg.Number("seqno") ?? 0, vg.Number("extent_size") ?? throw new LvmFormatException("the volume group has no extent_size"),
            pvs, lvs);
    }

    private static List<(string, long)> Stripes(Section segment)
    {
        var values = segment.Values.GetValueOrDefault("stripes") as List<object> ?? [];
        var result = new List<(string, long)>();
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            result.Add((values[i] as string ?? string.Empty, values[i + 1] is long start ? start : 0));
        }

        return result;
    }

    /// <summary>LVM's CRC-32 (the IEEE polynomial, reflected, without a final inversion) with its own initial value.</summary>
    public static uint Crc(uint initial, ReadOnlySpan<byte> data)
    {
        var crc = initial;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xff] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>A section of the text format: values (string, number or a list of them) and sub-sections, in order.</summary>
    internal sealed class Section
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

        public List<KeyValuePair<string, Section>> Sections { get; } = [];

        public Section? Child(string name) => Sections.FirstOrDefault(s => s.Key == name).Value;

        public string? String(string name) => Values.GetValueOrDefault(name) as string;

        public long? Number(string name) => Values.GetValueOrDefault(name) is long n ? n : null;

        public List<string> Strings(string name) => Values.GetValueOrDefault(name) is List<object> list ? [.. list.OfType<string>()] : [];
    }

    /// <summary>The LVM text format: <c>name { … }</c>, <c>key = value</c> with strings, integers and <c>[ … ]</c> lists; <c>#</c> comments.</summary>
    private sealed class ConfigParser(string text)
    {
        private int _position;

        public Section ParseSection(bool nested = false)
        {
            var section = new Section();
            while (true)
            {
                SkipSpace();
                if (_position >= text.Length)
                {
                    if (nested)
                    {
                        throw new LvmFormatException("the metadata text ends inside a section");
                    }

                    return section;
                }

                if (text[_position] == '}')
                {
                    if (!nested)
                    {
                        throw new LvmFormatException($"unexpected '}}' at {_position}");
                    }

                    _position++;
                    return section;
                }

                var name = Identifier();
                SkipSpace();
                if (Peek() == '{')
                {
                    _position++;
                    section.Sections.Add(new(name, ParseSection(nested: true)));
                }
                else if (Peek() == '=')
                {
                    _position++;
                    section.Values[name] = Value();
                }
                else
                {
                    throw new LvmFormatException($"expected '{{' or '=' after '{name}' at {_position}");
                }
            }
        }

        private object Value()
        {
            SkipSpace();
            switch (Peek())
            {
                case '"':
                    return QuotedString();
                case '[':
                    _position++;
                    var list = new List<object>();
                    while (true)
                    {
                        SkipSpace();
                        if (Peek() == ']')
                        {
                            _position++;
                            return list;
                        }

                        if (Peek() == ',')
                        {
                            _position++;
                            continue;
                        }

                        list.Add(Value());
                    }

                default:
                    var start = _position;
                    while (_position < text.Length && (char.IsAsciiDigit(text[_position]) || text[_position] is '-' or '.'))
                    {
                        _position++;
                    }

                    var token = text[start.._position];
                    return long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number
                        : double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? (object)(long)real
                        : throw new LvmFormatException($"expected a value at {start}");
            }
        }

        private string QuotedString()
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < text.Length && text[_position] != '"')
            {
                if (text[_position] == '\\' && _position + 1 < text.Length)
                {
                    _position++;
                }

                builder.Append(text[_position++]);
            }

            if (_position >= text.Length)
            {
                throw new LvmFormatException("a string in the metadata text has no end");
            }

            _position++;
            return builder.ToString();
        }

        private string Identifier()
        {
            var start = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] is '_' or '-' or '.' or '+'))
            {
                _position++;
            }

            return _position > start ? text[start.._position] : throw new LvmFormatException($"expected a name at {start}");
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';

        private void SkipSpace()
        {
            while (_position < text.Length)
            {
                if (char.IsWhiteSpace(text[_position]))
                {
                    _position++;
                }
                else if (text[_position] == '#')
                {
                    while (_position < text.Length && text[_position] != '\n')
                    {
                        _position++;
                    }
                }
                else
                {
                    return;
                }
            }
        }
    }

    /// <summary>A logical volume's bytes: its segments, each a range of the physical volume.</summary>
    private sealed class SegmentSource(IBlockSource device, List<(long LvOffset, long PvOffset, long Length)> pieces, long size, string name) : IBlockSource
    {
        public long Size { get; } = size;

        public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (offset < 0 || offset + destination.Length > Size)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"Read {offset}+{destination.Length} is outside LV {name} ({Size} bytes).");
            }

            var done = 0;
            while (done < destination.Length)
            {
                var at = offset + done;
                var piece = pieces.FirstOrDefault(p => at >= p.LvOffset && at < p.LvOffset + p.Length);
                if (piece.Length == 0)
                {
                    throw new LvmFormatException($"LV {name} has no segment at byte {at}.");
                }

                var length = (int)Math.Min(destination.Length - done, piece.LvOffset + piece.Length - at);
                await device.ReadAsync(piece.PvOffset + (at - piece.LvOffset), destination.Slice(done, length), cancellationToken).ConfigureAwait(false);
                done += length;
            }
        }
    }
}
