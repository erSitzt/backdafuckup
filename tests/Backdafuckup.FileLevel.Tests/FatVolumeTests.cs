namespace Backdafuckup.FileLevel.Tests;

/// <summary>FAT volumes (EFI system partitions): a folder that is not there is reported as missing, not as a failure.</summary>
public sealed class FatVolumeTests
{
    [Fact]
    public void Listing_a_missing_folder_on_a_fat_volume_says_it_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "bdfu-fs-fixtures", $"fat-{Guid.NewGuid():N}.img");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = File.Create(path))
        {
            // An MBR disk with one FAT partition, as an EFI system partition would be.
            file.SetLength(64L << 20);
            var geometry = DiscUtils.Geometry.FromCapacity(file.Length);
            var table = DiscUtils.Partitions.BiosPartitionTable.Initialize(file, geometry);
            var partition = table[table.Create(DiscUtils.Partitions.WellKnownPartitionType.WindowsFat, active: true)];
            using var content = partition.Open();
            using var fat = DiscUtils.Fat.FatFileSystem.FormatPartition(content, "EFI", geometry, (int)partition.FirstSector, (int)partition.SectorCount, 0);
            fat.CreateDirectory(@"EFI\BOOT");
        }

        try
        {
            using var image = GuestImage.Open([new GuestDiskSource("efi", "efi", () => File.OpenRead(path))]);
            var volume = Assert.Single(image.Volumes, v => v.Readable);
            Assert.Equal("FAT", volume.FileSystem);
            var fs = image.OpenFileSystem(volume.Id);
            Assert.Equal("EFI", Assert.Single(fs.List(GuestPath.Parse("/"))).Name);
            Assert.Equal("BOOT", Assert.Single(fs.List(GuestPath.Parse("/EFI"))).Name);

            var missing = Assert.Throws<FileNotFoundException>(() => fs.List(GuestPath.Parse("/home/user")));
            Assert.Contains("/home/user", missing.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
