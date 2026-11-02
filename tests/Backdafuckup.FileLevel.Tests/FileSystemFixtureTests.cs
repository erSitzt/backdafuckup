using Backdafuckup.FileLevel;

namespace Backdafuckup.FileLevel.Tests;

/// <summary>Every file system test image read through <see cref="GuestImage"/> must match its manifest exactly.</summary>
[Trait("Category", "Unit")]
public sealed class FileSystemFixtureTests
{
    public static TheoryData<string, string[], string, string, bool> Volumes => new()
    {
        // manifest, disks, volume id, file system, unix
        { "ntfs-gpt.p1", ["ntfs-gpt"], "0:2048", "NTFS", false },
        { "ntfs-mbr.p1", ["ntfs-mbr"], "0:2048", "NTFS", false },
        { "ntfs-mbr.p5", ["ntfs-mbr"], "0:86016", "NTFS", false },
        { "ext4-gpt.p1", ["ext4-gpt"], "0:2048", "ext", true },
        { "xfs-nrext64.p1", ["xfs-nrext64"], "0:2048", "XFS", true },
        { "xfs-gpt.p1", ["xfs-gpt"], "0:2048", "XFS", true },
        { "lvm.root", ["lvm-disk0", "lvm-disk1"], "/", "ext", true },
        { "lvm.data", ["lvm-disk0", "lvm-disk1"], "/data", "XFS", true },
    };

    [Theory]
    [MemberData(nameof(Volumes))]
    public void Volume_matches_its_manifest(string manifest, string[] disks, string volume, string fileSystem, bool unix)
    {
        using var image = GuestImage.Open([.. disks.Select((d, i) => Fixtures.Disk(d, i.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
        var found = image.Volumes.Single(v => v.Id == volume || v.MountPoint == volume);
        Assert.True(found.Readable, found.Reason);
        Assert.Equal(fileSystem, found.FileSystem);
        var fs = image.OpenFileSystem(found.Id);
        Assert.Equal(unix ? GuestFileSystemFamily.Unix : GuestFileSystemFamily.Windows, fs.Family);

        var expected = Fixtures.Manifest(manifest);
        foreach (var line in expected.Where(l => l.Type != "stream"))
        {
            var path = GuestPath.Parse(line.Path);
            var entry = fs.Stat(path);
            switch (line.Type)
            {
                case "dir":
                    Assert.Equal(GuestEntryKind.Directory, entry.Kind);
                    break;
                case "file":
                    Assert.True(entry.Kind == GuestEntryKind.File, $"{line.Path}: {entry.Kind}");
                    Assert.Equal(line.Size, entry.Size);
                    using (var stream = fs.OpenRead(path))
                    {
                        Assert.True(line.Hash == Fixtures.Sha256(stream), $"content of {line.Path}");
                    }

                    break;
                case "link":
                    Assert.Equal(GuestEntryKind.Symlink, entry.Kind);
                    Assert.Equal(line.Hash, entry.LinkTarget);
                    break;
            }

            if (unix && line.Type != "link")
            {
                Assert.Equal(line.Mode, Convert.ToString(entry.Mode!.Value, 8));
                Assert.Equal(line.Uid, entry.UserId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(line.Gid, entry.GroupId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (line.Mtime != "-" && line.Type != "link")
            {
                Assert.True(
                    long.Parse(line.Mtime, System.Globalization.CultureInfo.InvariantCulture) == new DateTimeOffset(entry.ModifiedUtc!.Value).ToUnixTimeSeconds(),
                    $"mtime of {line.Path}");
            }
        }

        // Nothing more and nothing less in the listings (lost+found is not in the manifests).
        var listed = GuestWalker.Walk(fs, fs.List(GuestPath.Root).Select(e => GuestPath.Root.Append(e.Name)), new GuestWalkLimits(), TestContext.Current.CancellationToken)
            .Select(x => x.Path.ToString()[1..])
            .Where(p => p != "lost+found" && !p.StartsWith("lost+found/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected.Where(l => l.Type != "stream").Select(l => l.Path).Order(StringComparer.Ordinal), listed);
    }

    [Fact]
    public void Ntfs_shows_hidden_files_marks_them_and_hides_metafiles()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ntfs-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var root = fs.List(GuestPath.Root);
        var hidden = Assert.Single(root, e => e.Name == "hidden.txt");
        Assert.True(hidden.Attributes!.Value.HasFlag(FileAttributes.Hidden));
        Assert.DoesNotContain(root, e => e.Name.Equals("$MFT", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fs.GetSecurityDescriptor(GuestPath.Parse("hello.txt")));
        Assert.Equal("FLRNTFS", image.Volumes.Single(v => v.Id == "0:2048").Label);
    }

    [Fact]
    public void Lvm_volumes_get_their_fstab_mount_points_and_physical_volumes_are_hidden()
    {
        using var image = GuestImage.Open([Fixtures.Disk("lvm-disk0", "0"), Fixtures.Disk("lvm-disk1", "1")]);
        Assert.Equal(2, image.Volumes.Count);
        var root = image.Volumes.Single(v => v.LvmName == "root");
        var data = image.Volumes.Single(v => v.LvmName == "data");
        Assert.Equal("/", root.MountPoint);
        Assert.Equal("/data", data.MountPoint);
        Assert.StartsWith("lvm:", data.Id, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", data.FileSystemUuid!);
    }

    [Fact]
    public void Volume_ids_are_stable_between_openings()
    {
        using var first = GuestImage.Open([Fixtures.Disk("ntfs-mbr")]);
        using var second = GuestImage.Open([Fixtures.Disk("ntfs-mbr")]);
        Assert.Equal(first.Volumes.Select(v => v.Id), second.Volumes.Select(v => v.Id));
        Assert.Equal(["0:2048", "0:86016"], first.Volumes.Select(v => v.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Links_are_reported_not_followed_and_paths_through_links_fail()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        Assert.Equal(GuestEntryKind.Symlink, fs.Stat(GuestPath.Parse("link-to-dir")).Kind);
        Assert.ThrowsAny<IOException>(() => fs.Stat(GuestPath.Parse("link-to-dir/deep")));
        Assert.ThrowsAny<IOException>(() => fs.OpenRead(GuestPath.Parse("link-to-hello")));
    }
}
