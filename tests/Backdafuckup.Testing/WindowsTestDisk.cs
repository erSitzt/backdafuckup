using System.Text;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Registry;
using DiscUtils.Streams;

namespace Backdafuckup.Testing;

/// <summary>
/// A small Windows disk built at run time: GPT with one NTFS partition per drive letter, a SYSTEM hive whose
/// MountedDevices maps the letters to the partitions (as Windows stores them for GPT disks), an optional SOFTWARE hive,
/// and any files. The first volume holds the registry (the system volume).
/// </summary>
public static class WindowsTestDisk
{
    /// <param name="Files">Paths with "/" separators, e.g. "SQL/shop.mdf".</param>
    public sealed record Volume(char Letter, int SizeMiB, IReadOnlyDictionary<string, byte[]> Files);

    public static byte[] Create(IReadOnlyList<Volume> volumes, Action<RegistryKey>? software = null, Action<RegistryKey>? system = null)
    {
        var size = (volumes.Sum(v => (long)v.SizeMiB) + 2) * 1024 * 1024;
        using var stream = new MemoryStream();
        stream.SetLength(size);
        var disk = DiscUtils.Raw.Disk.Initialize(stream, Ownership.None, size);
        var geometry = disk.Geometry ?? Geometry.FromCapacity(size);
        var gpt = GuidPartitionTable.Initialize(disk);
        var partitions = new List<(Volume Volume, GuidPartitionInfo Partition)>();
        foreach (var volume in volumes)
        {
            var index = gpt.Create(volume.SizeMiB * 1024L * 1024, WellKnownPartitionType.WindowsNtfs, false);
            partitions.Add((volume, (GuidPartitionInfo)gpt.Partitions[index]));
        }

        var systemHive = Hive(root =>
        {
            var devices = root.CreateSubKey("MountedDevices");
            foreach (var (volume, partition) in partitions)
            {
                var value = new byte[24];
                Encoding.ASCII.GetBytes("DMIO:ID:").CopyTo(value, 0);
                partition.Identity.ToByteArray().CopyTo(value, 8);
                devices.SetValue($@"\DosDevices\{char.ToUpperInvariant(volume.Letter)}:", value, RegistryValueType.Binary);
            }

            root.CreateSubKey("Select").SetValue("Current", 1, RegistryValueType.Dword);
            system?.Invoke(root);
        });
        var softwareHive = software is null ? null : Hive(software);

        for (var i = 0; i < partitions.Count; i++)
        {
            var (volume, partition) = partitions[i];
            using var fs = NtfsFileSystem.Format(partition.Open(), "VOL" + volume.Letter, geometry, partition.FirstSector, partition.SectorCount);
            var files = new Dictionary<string, byte[]>(volume.Files, StringComparer.OrdinalIgnoreCase);
            if (i == 0)
            {
                files["Windows/System32/config/SYSTEM"] = systemHive;
                if (softwareHive is not null)
                {
                    files["Windows/System32/config/SOFTWARE"] = softwareHive;
                }
            }

            foreach (var (path, content) in files)
            {
                var windowsPath = path.Replace('/', '\\');
                var slash = windowsPath.LastIndexOf('\\');
                if (slash > 0)
                {
                    fs.CreateDirectory(windowsPath[..slash]);
                }

                using var file = fs.OpenFile(windowsPath, FileMode.Create, FileAccess.ReadWrite);
                file.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// The start of a SQL Server database file: page 0 is a file header page (m_type 15) whose page header holds the
    /// file id at offset 0x24 (1 for the primary data file). The rest is a recognizable pattern.
    /// </summary>
    public static byte[] SqlFile(int fileId, int size, byte fill)
    {
        var content = new byte[size];
        Array.Fill(content, fill);
        content[0] = 1;
        content[1] = 15;
        BitConverter.GetBytes((ushort)fileId).CopyTo(content, 0x24);
        return content;
    }

    private static byte[] Hive(Action<RegistryKey> fill)
    {
        using var stream = new MemoryStream();
        using (var hive = RegistryHive.Create(stream, Ownership.None))
        {
            fill(hive.Root);
        }

        return stream.ToArray();
    }
}
