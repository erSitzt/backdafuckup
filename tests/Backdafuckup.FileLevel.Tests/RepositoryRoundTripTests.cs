using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.FileLevel;
using Backdafuckup.Storage;
using Backdafuckup.Testing;

namespace Backdafuckup.FileLevel.Tests;

/// <summary>Fixture disks backed up into a real repository and browsed from the restore point.</summary>
[Trait("Category", "Unit")]
public sealed class RepositoryRoundTripTests : IDisposable
{
    private const int ChunkSize = 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-fl-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Files_read_from_a_restore_point_match_the_source()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var rp = await BackupAsync(repo, ("0", "lvm-disk0"), ("1", "lvm-disk1"));
        await using var reader = await repo.OpenRestorePointAsync(rp, Ct);
        using var cache = new ChunkCache(64L * ChunkSize, 4);
        var sources = new List<GuestDiskSource>();
        foreach (var disk in reader.Manifest.Disks)
        {
            var index = await reader.ReadDiskIndexAsync(disk.Key, Ct);
            sources.Add(new GuestDiskSource(disk.Key, disk.Name, () => new RestorePointDiskStream(index, reader.Chunks, cache)));
        }

        using var image = GuestImage.Open(sources);
        var data = image.Volumes.Single(v => v.MountPoint == "/data");
        var fs = image.OpenFileSystem(data.Id);
        foreach (var line in Fixtures.Manifest("lvm.data").Where(l => l.Type == "file" && !l.Path.StartsWith("many/", StringComparison.Ordinal)))
        {
            using var stream = fs.OpenRead(GuestPath.Parse(line.Path));
            Assert.True(line.Hash == Fixtures.Sha256(stream), line.Path);
        }

        Assert.True(cache.Hits > 0);
        Assert.True(cache.Bytes <= cache.MaxBytes + ChunkSize);
    }

    [Fact]
    public async Task Disk_stream_reads_the_same_bytes_as_the_disk_image_reader()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var rp = await BackupAsync(repo, ("0", "ntfs-gpt"));
        await using var reader = await repo.OpenRestorePointAsync(rp, Ct);
        var index = await reader.ReadDiskIndexAsync("0", Ct);
        var image = new RestorePointDiskImage(reader.Chunks, index);
        using var cache = new ChunkCache(8L * ChunkSize, 2);
        using var stream = new RestorePointDiskStream(index, reader.Chunks, cache);
        var random = new Random(7);
        foreach (var length in new[] { 1, 511, 4096, ChunkSize - 3, ChunkSize + 17, 3 * ChunkSize })
        {
            var offset = (long)(random.NextDouble() * (index.DiskSize - length));
            var expected = new byte[length];
            await image.ReadAsync(offset, expected, Ct);
            var actual = new byte[length];
            stream.Position = offset;
            Assert.Equal(length, stream.Read(actual));
            Assert.Equal(expected, actual);
        }

        stream.Position = index.DiskSize - 10;
        Assert.Equal(10, stream.Read(new byte[100]));
        Assert.Equal(0, stream.Read(new byte[100]));
    }

    private static async Task<Guid> BackupAsync(LocalDirectoryRepository repo, params (string Key, string Fixture)[] disks)
    {
        var rpId = Guid.CreateVersion7();
        await using var writer = repo.BeginWrite(rpId, "vm", null);
        var manifestDisks = new List<ManifestDisk>();
        foreach (var (key, fixture) in disks)
        {
            await using var source = new FileDiskReader(Fixtures.Image(fixture));
            var result = await DiskBackupPipeline.RunAsync(
                new DiskBackupJob { Reader = source, Writer = writer, ChunkSize = ChunkSize, Changes = ChangedExtents.Full("test"), BaseIndex = null },
                Ct);
            var indexRef = await writer.WriteDiskIndexAsync(key, result.Index, Ct);
            manifestDisks.Add(new ManifestDisk { Key = key, SourceDiskId = fixture, Name = fixture, CapacityBytes = source.Capacity, Index = indexRef, Stats = result.Stats });
        }

        await writer.CommitAsync(
            new RestorePointManifest
            {
                RestorePointId = rpId,
                RepositoryId = repo.Info.RepositoryId,
                CreatedUtc = DateTimeOffset.UtcNow,
                Kind = RestorePointKind.Full,
                Source = new ManifestSource(HypervisorKind.XenServer, "pool", "vm", "vm", null),
                Vm = new VmDescriptor { VmId = "vm", Name = "vm" },
                Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"),
                Disks = manifestDisks,
            },
            Ct);
        return rpId;
    }
}
