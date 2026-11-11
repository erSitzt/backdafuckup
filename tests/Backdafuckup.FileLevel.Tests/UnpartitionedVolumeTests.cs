namespace Backdafuckup.FileLevel.Tests;

/// <summary>
/// The volume of a Kubernetes PVC is a file system on the whole device, without a partition table (a backup of a
/// namespace stores it as a disk). Partition 1 of the ext4 fixture, cut out, is such a volume.
/// </summary>
public sealed class UnpartitionedVolumeTests
{
    [Fact]
    public void A_file_system_on_the_whole_disk_is_one_volume_and_matches_its_manifest()
    {
        var volume = Path.Combine(Path.GetTempPath(), "bdfu-fs-fixtures", "ext4-unpartitioned.img");
        using (var source = File.OpenRead(Fixtures.Image("ext4-gpt")))
        using (var target = File.Create(volume))
        {
            source.Position = 2048 * 512; // partition 1 starts at sector 2048
            source.CopyTo(target);
        }

        using var image = GuestImage.Open([new GuestDiskSource("data-redis-test-0", "data-redis-test-0", () => File.OpenRead(volume))]);
        var found = Assert.Single(image.Volumes, v => v.Readable);
        Assert.Equal("ext", found.FileSystem);
        var fs = image.OpenFileSystem(found.Id);
        Assert.Equal(GuestFileSystemFamily.Unix, fs.Family);
        foreach (var line in Fixtures.Manifest("ext4-gpt.p1").Where(l => l.Type == "file").Take(20))
        {
            using var stream = fs.OpenRead(GuestPath.Parse(line.Path));
            Assert.Equal(line.Hash, Fixtures.Sha256(stream));
        }
    }
}
