using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Backdafuckup.VSphere;

/// <summary>
/// The descriptor of a monolithic flat disk as ESXi writes it on a datastore (<c>createType="vmfs"</c>, one extent that
/// is the <c>-flat.vmdk</c>), from the public VMDK descriptor format. An instant recovery puts one next to each disk's
/// flat file, which is the restore point's disk.
/// </summary>
internal static class VSphereVmdkDescriptor
{
    public const int SectorSize = 512;

    /// <summary>The descriptor text for a flat file of <paramref name="capacityBytes"/> (a multiple of 512).</summary>
    /// <param name="adapterType">"ide", "buslogic" or "lsilogic" (<see cref="AdapterType"/>).</param>
    /// <param name="hardwareVersion">The VM's hardware version ("vmx-19"), or null.</param>
    /// <param name="uuid">The disk's <c>ddb.uuid</c> in the descriptor's form ("60 00 C2 9a …", <see cref="Uuid"/>).</param>
    public static string Create(string flatFile, long capacityBytes, string adapterType, string? hardwareVersion, string uuid)
    {
        ArgumentException.ThrowIfNullOrEmpty(flatFile);
        if (capacityBytes <= 0 || capacityBytes % SectorSize != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes), capacityBytes, "A disk's capacity is a positive multiple of 512 bytes.");
        }

        var sectors = capacityBytes / SectorSize;
        var (heads, perTrack) = adapterType == "ide" ? (16, 63) : (255, 63);
        var cylinders = Math.Max(1, sectors / (heads * perTrack));
        if (adapterType == "ide")
        {
            cylinders = Math.Min(cylinders, 16383);
        }

        var cid = RandomCid();
        var text = new StringBuilder();
        text.Append("# Disk DescriptorFile\n")
            .Append("version=1\n")
            .Append("encoding=\"UTF-8\"\n")
            .Append(CultureInfo.InvariantCulture, $"CID={cid}\n")
            .Append("parentCID=ffffffff\n")
            .Append("createType=\"vmfs\"\n\n")
            .Append("# Extent description\n")
            .Append(CultureInfo.InvariantCulture, $"RW {sectors} VMFS \"{flatFile}\"\n\n")
            .Append("# The Disk Data Base\n")
            .Append("#DDB\n\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.adapterType = \"{adapterType}\"\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.geometry.cylinders = \"{cylinders}\"\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.geometry.heads = \"{heads}\"\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.geometry.sectors = \"{perTrack}\"\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.longContentID = \"{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12))}{cid}\"\n")
            .Append("ddb.thinProvisioned = \"1\"\n")
            .Append(CultureInfo.InvariantCulture, $"ddb.uuid = \"{uuid}\"\n");
        if (hardwareVersion is { Length: > 4 } version && version.StartsWith("vmx-", StringComparison.Ordinal) && int.TryParse(version.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            text.Append(CultureInfo.InvariantCulture, $"ddb.virtualHWVersion = \"{number}\"\n");
        }

        return text.ToString();
    }

    /// <summary>The descriptor's adapter type for a disk on a controller of this type: it only sets the geometry, the VM's controller is what the guest sees.</summary>
    public static string AdapterType(string? controllerType) => controllerType switch
    {
        "VirtualIDEController" => "ide",
        "VirtualBusLogicController" => "buslogic",
        _ => "lsilogic",
    };

    /// <summary>
    /// A disk UUID in the descriptor's form, 16 bytes in hex separated by spaces and a dash after the eighth
    /// ("60 00 c2 9a 3b 4c 5d 6e-7f 80 91 a2 b3 c4 d5 e6"): from a backing's UUID
    /// ("6000C29a-3b4c-…", keeping the disk's identity), or a new one (null; with VMware's prefix like the ones ESXi makes).
    /// </summary>
    public static string Uuid(string? backingUuid = null)
    {
        byte[] bytes;
        if (backingUuid is not null && backingUuid.Replace("-", string.Empty, StringComparison.Ordinal) is { Length: 32 } hex && hex.All(char.IsAsciiHexDigit))
        {
            bytes = Convert.FromHexString(hex);
        }
        else
        {
            bytes = RandomNumberGenerator.GetBytes(16);
            bytes[0] = 0x60;
            bytes[1] = 0x00;
            bytes[2] = 0xC2;
            bytes[3] = (byte)(0x90 | (bytes[3] & 0x0F));
        }

        string Hex(IEnumerable<byte> part) => string.Join(' ', part.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        return $"{Hex(bytes[..8])}-{Hex(bytes[8..])}";
    }

    private static string RandomCid()
    {
        while (true)
        {
            var cid = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
            if (cid is not (0xffffffff or 0xfffffffe))
            {
                return cid.ToString("x8", CultureInfo.InvariantCulture);
            }
        }
    }
}
