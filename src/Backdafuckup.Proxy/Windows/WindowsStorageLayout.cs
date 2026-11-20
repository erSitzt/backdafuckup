using System.Buffers.Binary;
using System.Text;

namespace Backdafuckup.Proxy.Windows;

/// <summary>
/// The storage descriptors Windows returns for <c>IOCTL_STORAGE_QUERY_PROPERTY</c>, parsed without Windows (tested on
/// Linux): the LUN's VPD page 0x83 NAA identifier and the vendor and product names.
/// </summary>
public static class WindowsStorageLayout
{
    /// <summary>STORAGE_IDENTIFIER_TYPE StorageIdTypeFCPHName: an NAA designator (what arrays report as the LUN's WWN).</summary>
    private const int TypeNaa = 3;

    /// <summary>STORAGE_IDENTIFIER_CODE_SET StorageIdCodeSetBinary.</summary>
    private const int CodeSetBinary = 1;

    /// <summary>STORAGE_ASSOCIATION_TYPE StorageIdAssocDevice: the identifier names the logical unit, not a port.</summary>
    private const int AssociationDevice = 0;

    /// <summary>
    /// The NAA identifier (lower-case hex) in a STORAGE_DEVICE_UNIQUE_IDENTIFIER (<c>StorageDeviceUniqueIdProperty</c>),
    /// or null. On a VMware disk <c>StorageDeviceIdProperty</c> returned only its header; the unique identifier carries the
    /// whole page 0x83 descriptor (the spike).
    /// </summary>
    public static string? NaaFromUniqueId(ReadOnlySpan<byte> uniqueId)
    {
        // Version, Size, StorageDeviceIdOffset, StorageDeviceOffset, DriveLayoutSignatureOffset: 5 × uint32.
        if (uniqueId.Length < 20)
        {
            return null;
        }

        var size = Math.Min(uniqueId.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(uniqueId[4..]));
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(uniqueId[8..]);
        return offset > 0 && offset < size ? NaaFromDeviceId(uniqueId[offset..size]) : null;
    }

    /// <summary>The NAA identifier in a STORAGE_DEVICE_ID_DESCRIPTOR, the longest when there are several; null if none.</summary>
    public static string? NaaFromDeviceId(ReadOnlySpan<byte> descriptor)
    {
        // Version, Size, NumberOfIdentifiers: 3 × uint32; then STORAGE_IDENTIFIER entries:
        // CodeSet (int32), Type (int32), IdentifierSize (uint16), NextOffset (uint16), Association (int32), Identifier[].
        if (descriptor.Length < 12)
        {
            return null;
        }

        var size = Math.Min(descriptor.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        string? best = null;
        var offset = 12;
        for (var i = 0; i < count && offset + 16 <= size; i++)
        {
            var entry = descriptor[offset..size];
            var codeSet = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var type = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]);
            var next = BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]);
            var association = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (16 + length > entry.Length)
            {
                break;
            }

            if (type == TypeNaa && codeSet == CodeSetBinary && association == AssociationDevice && length > 0)
            {
                var hex = Convert.ToHexStringLower(entry.Slice(16, length));
                best = best is null || hex.Length > best.Length ? hex : best;
            }

            if (next == 0)
            {
                break;
            }

            offset += next;
        }

        return best;
    }

    /// <summary>"Vendor Product" from a STORAGE_DEVICE_DESCRIPTOR (<c>StorageDeviceProperty</c>), or null.</summary>
    public static string? Model(ReadOnlySpan<byte> device)
    {
        // Version, Size (uint32); DeviceType, Modifier, Removable, Queueing (bytes); VendorIdOffset @12, ProductIdOffset @16.
        if (device.Length < 20)
        {
            return null;
        }

        var size = Math.Min(device.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(device[4..]));
        var vendor = Text(device[..size], (int)BinaryPrimitives.ReadUInt32LittleEndian(device[12..]));
        var product = Text(device[..size], (int)BinaryPrimitives.ReadUInt32LittleEndian(device[16..]));
        var model = $"{vendor} {product}".Trim();
        return model.Length == 0 ? null : model;
    }

    /// <summary>BytesPerLogicalSector of a STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR (<c>StorageAccessAlignmentProperty</c>), or 512.</summary>
    public static int LogicalSector(ReadOnlySpan<byte> alignment) =>
        alignment.Length >= 20 && BinaryPrimitives.ReadInt32LittleEndian(alignment[16..]) is var sector and >= 512 and <= 65536 && (sector & (sector - 1)) == 0
            ? sector
            : 512;

    private static string Text(ReadOnlySpan<byte> data, int offset)
    {
        if (offset <= 0 || offset >= data.Length)
        {
            return string.Empty;
        }

        var end = data[offset..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? data[offset..] : data.Slice(offset, end)).Trim();
    }
}
