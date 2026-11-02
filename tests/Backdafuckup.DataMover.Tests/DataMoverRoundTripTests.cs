using System.Security.Cryptography;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.DataMover;
using Backdafuckup.Storage;
using Backdafuckup.Testing;

namespace Backdafuckup.DataMover.Tests;

[Trait("Category", "Unit")]
public sealed class DataMoverRoundTripTests : IDisposable
{
    private const int ChunkSize = 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-datamover-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Full_then_incremental_restore_bit_identical_and_incremental_reads_only_changes()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var live = InMemoryDisk.Random((64L * ChunkSize) + 12345, zeroFraction: 0.4);

        // Full backup.
        var (snap1, _) = live.Snapshot();
        var full = await BackupAsync(repo, snap1, ChangedExtents.Full("first backup"), null);
        Assert.Equal(DiskBackupMode.Full, full.Result.Stats.Mode);
        Assert.Equal(snap1.Content.LongLength, full.Result.Stats.ReadBytes);
        Assert.True(full.Result.Stats.ZeroChunks > 0);

        // Change scattered 64 KiB blocks, including the short last chunk, and rewrite one block with identical data.
        var rng = new Random(7);
        for (var i = 0; i < 20; i++)
        {
            var offset = rng.NextInt64(0, live.Content.LongLength / CbtBitmap.BlockSize) * CbtBitmap.BlockSize;
            live.Write(offset, RandomNumberGenerator.GetBytes((int)Math.Min(CbtBitmap.BlockSize, live.Content.LongLength - offset)));
        }

        live.Write(live.Content.LongLength - 100, RandomNumberGenerator.GetBytes(100));
        live.Write(0, live.Content.AsSpan(0, CbtBitmap.BlockSize).ToArray());

        var (snap2, changed) = live.Snapshot();
        var cbt = CbtBitmap.Decode(CbtBitmap.Encode(changed, snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst), snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst);
        var incremental = await BackupAsync(repo, snap2, new ChangedExtents(false, null, cbt), full);

        Assert.Equal(DiskBackupMode.Cbt, incremental.Result.Stats.Mode);
        Assert.True(snap2.BytesRead <= 23L * ChunkSize, $"incremental read {snap2.BytesRead} bytes");
        Assert.True(incremental.Result.Stats.UnchangedChunks >= 1);

        Assert.Equal(Sha(snap1.Content), Sha(await RestoreAsync(repo, full.RestorePointId)));
        Assert.Equal(Sha(snap2.Content), Sha(await RestoreAsync(repo, incremental.RestorePointId)));

        // Forever-incremental: the full restore point can be deleted; the incremental remains complete.
        await repo.DeleteRestorePointAsync(full.RestorePointId, Ct);
        await repo.CollectGarbageAsync(null, Ct);
        Assert.Equal(Sha(snap2.Content), Sha(await RestoreAsync(repo, incremental.RestorePointId)));
    }

    [Fact]
    public async Task Random_reads_from_the_image_match_the_source()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var (snap, _) = InMemoryDisk.Random((10L * ChunkSize) + 777).Snapshot();
        var backup = await BackupAsync(repo, snap, ChangedExtents.Full("test"), null);

        await using var reader = await repo.OpenRestorePointAsync(backup.RestorePointId, Ct);
        var image = await RestorePointDiskImage.OpenAsync(reader, "0", Ct);
        foreach (var (offset, length) in new (long, int)[] { (0, 10), (ChunkSize - 5, 10), (3L * ChunkSize, 2 * ChunkSize), (snap.Content.LongLength - 777, 777) })
        {
            var buffer = new byte[length];
            await image.ReadAsync(offset, buffer, Ct);
            Assert.True(snap.Content.AsSpan((int)offset, length).SequenceEqual(buffer), $"mismatch at {offset}");
        }
    }

    [Fact]
    public async Task Changed_disk_size_forces_a_full_read()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var (small, _) = InMemoryDisk.Random(4L * ChunkSize).Snapshot();
        var first = await BackupAsync(repo, small, ChangedExtents.Full("first"), null);

        var (bigger, _) = InMemoryDisk.Random(6L * ChunkSize, seed: 2).Snapshot();
        var second = await BackupAsync(repo, bigger, new ChangedExtents(false, null, [new DiskExtent(0, 10)]), first);

        Assert.Equal(DiskBackupMode.Full, second.Result.Stats.Mode);
        Assert.Equal(Sha(bigger.Content), Sha(await RestoreAsync(repo, second.RestorePointId)));
    }

    private sealed record Backup(Guid RestorePointId, DiskBackupResult Result);

    private static async Task<Backup> BackupAsync(LocalDirectoryRepository repo, InMemoryDisk snapshot, ChangedExtents changes, Backup? baseline)
    {
        var rpId = Guid.CreateVersion7();
        using var pin = baseline is null ? null : repo.Pin(baseline.RestorePointId);
        await using var writer = repo.BeginWrite(rpId, "vm", pin);
        await using var reader = snapshot.OpenReader(maxRequestBytes: 256 * 1024);
        var result = await DiskBackupPipeline.RunAsync(
            new DiskBackupJob
            {
                Reader = reader,
                Writer = writer,
                ChunkSize = ChunkSize,
                Changes = changes,
                BaseIndex = baseline?.Result.Index,
            },
            Ct);

        var indexRef = await writer.WriteDiskIndexAsync("0", result.Index, Ct);
        await writer.CommitAsync(
            new RestorePointManifest
            {
                RestorePointId = rpId,
                RepositoryId = repo.Info.RepositoryId,
                CreatedUtc = DateTimeOffset.UtcNow,
                Kind = baseline is null ? RestorePointKind.Full : RestorePointKind.Incremental,
                BaseRestorePointId = baseline?.RestorePointId,
                Source = new ManifestSource(HypervisorKind.XenServer, "pool", "vm", "vm", null),
                Vm = new VmDescriptor { VmId = "vm", Name = "vm" },
                Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"),
                Disks = [new ManifestDisk { Key = "0", SourceDiskId = "d", Name = "d", CapacityBytes = reader.Capacity, Index = indexRef, Stats = result.Stats }],
            },
            Ct);
        return new Backup(rpId, result);
    }

    private static async Task<byte[]> RestoreAsync(LocalDirectoryRepository repo, Guid rpId)
    {
        await using var reader = await repo.OpenRestorePointAsync(rpId, Ct);
        var image = await RestorePointDiskImage.OpenAsync(reader, "0", Ct);
        await using var stream = image.OpenSequential(prefetchChunks: 8, workers: 4);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, 300_000, Ct);
        Assert.Equal(image.Capacity, memory.Length);
        return memory.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
