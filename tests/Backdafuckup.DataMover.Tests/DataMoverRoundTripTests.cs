using System.Security.Cryptography;
using Backdafuckup.Core.DataMover;
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
    public async Task A_full_read_of_the_allocated_areas_reads_only_their_chunks_and_records_the_rest_as_zeros()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        // 32 MiB plus a short last chunk: data in chunk 2 (two areas), across the border of chunks 9/10, and in the last chunk.
        var content = new byte[(32L * ChunkSize) + 4096];
        var allocated = new List<DiskExtent> { new(2L * ChunkSize, 4096), new((2L * ChunkSize) + 65536, 8192), new((10L * ChunkSize) - 4096, 8192), new(32L * ChunkSize, 4096) };
        foreach (var area in allocated)
        {
            RandomNumberGenerator.Fill(content.AsSpan((int)area.Offset, (int)area.Length));
        }

        // What the platform reports as unallocated holds no data; a stray byte there is not read and comes back as zero.
        content[20L * ChunkSize] = 0x55;
        var (snap, _) = new InMemoryDisk(content).Snapshot();
        var full = await BackupAsync(repo, snap, ChangedExtents.FullAllocated("first backup", allocated), null);

        Assert.Equal(DiskBackupMode.Full, full.Result.Stats.Mode);
        Assert.Equal((3L * ChunkSize) + 4096, snap.BytesRead);
        Assert.Equal(33 - 4, full.Result.Stats.ZeroChunks);
        Assert.Equal(4, full.Result.Stats.ChangedChunks);
        var restored = await RestoreAsync(repo, full.RestorePointId);
        content[20L * ChunkSize] = 0;
        Assert.Equal(Sha(content), Sha(restored));
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

    [Fact]
    public async Task Streaming_source_gives_the_same_restore_points_as_random_access()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var live = InMemoryDisk.Random((40L * ChunkSize) + 4321, zeroFraction: 0.5);
        live.Write(5L * ChunkSize, new byte[3 * ChunkSize]);

        var (snap1, _) = live.Snapshot();
        var streamedFull = await BackupAsync(repo, snap1, ChangedExtents.Full("first backup"), null, streaming: true);
        var randomFull = await BackupAsync(repo, snap1, ChangedExtents.Full("first backup"), null);
        Assert.Equal(randomFull.Result.Index.Entries, streamedFull.Result.Index.Entries);
        Assert.Equal("memory-stream", streamedFull.Result.Stats.Transport);
        Assert.Equal(randomFull.Result.Stats.ZeroChunks, streamedFull.Result.Stats.ZeroChunks);
        Assert.True(streamedFull.Result.Stats.ReadBytes < snap1.Content.LongLength, "zero segments are not transferred");

        // Incremental with CBT: the stream still carries the whole disk, but only dirty chunks are processed.
        var rng = new Random(3);
        for (var i = 0; i < 12; i++)
        {
            var offset = rng.NextInt64(0, live.Content.LongLength / CbtBitmap.BlockSize) * CbtBitmap.BlockSize;
            live.Write(offset, RandomNumberGenerator.GetBytes((int)Math.Min(CbtBitmap.BlockSize, live.Content.LongLength - offset)));
        }

        live.Write(6L * ChunkSize, new byte[ChunkSize]);
        live.Write(20L * ChunkSize, new byte[ChunkSize]);
        var (snap2, changed) = live.Snapshot();
        var cbt = CbtBitmap.Decode(CbtBitmap.Encode(changed, snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst), snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst);
        var streamedIncremental = await BackupAsync(repo, snap2, new ChangedExtents(false, null, cbt), streamedFull, streaming: true);

        Assert.Equal(DiskBackupMode.Cbt, streamedIncremental.Result.Stats.Mode);
        Assert.Equal(Sha(snap1.Content), Sha(await RestoreAsync(repo, streamedFull.RestorePointId)));
        Assert.Equal(Sha(snap2.Content), Sha(await RestoreAsync(repo, streamedIncremental.RestorePointId)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Counters_add_up_and_show_dedupe_and_compression(bool streaming)
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var live = InMemoryDisk.Random(24L * ChunkSize, zeroFraction: 0.3);

        // Compressible data in chunks 0-3: a repeating text pattern.
        var text = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("backdafuckup compressible data ", 140_000)));
        live.Write(0, text.AsSpan(0, 4 * ChunkSize).ToArray());

        // Chunks 10 and 11 are identical to chunks 8 and 9: deduplicated within the same backup.
        live.Write(10L * ChunkSize, live.Content.AsSpan(8 * ChunkSize, 2 * ChunkSize).ToArray());
        live.Write(16L * ChunkSize, new byte[2 * ChunkSize]);

        var (snap1, _) = live.Snapshot();
        var full = await BackupAsync(repo, snap1, ChangedExtents.Full("first"), null, streaming);
        var c = full.Result.Counters;
        Assert.Equal(snap1.Content.LongLength, c.ProcessedBytes);
        Assert.Equal(c.ProcessedBytes, c.UnchangedBytes + c.ZeroBytes + c.DedupedBytes + c.NewDataBytes);
        Assert.Equal(0, c.UnchangedBytes);
        Assert.True(c.ZeroBytes >= 2L * ChunkSize);
        Assert.True(c.DedupedBytes >= 2L * ChunkSize, $"deduped {c.DedupedBytes}");
        Assert.True(c.NewDataBytes > c.StoredBytes, "the text chunks compress");
        Assert.Equal(c.NewDataBytes, full.Result.Stats.NewDataBytes);
        Assert.True(c.SourceLoad + c.ProcessingLoad + c.TargetLoad > 0);
        Assert.InRange(c.SourceLoad, 0, 1);

        // Incremental: two changed chunks; the rest of the disk counts as unchanged.
        live.Write(5L * ChunkSize, RandomNumberGenerator.GetBytes(ChunkSize));
        live.Write(6L * ChunkSize, live.Content.AsSpan(6 * ChunkSize, ChunkSize).ToArray());
        var (snap2, changed) = live.Snapshot();
        var cbt = CbtBitmap.Decode(CbtBitmap.Encode(changed, snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst), snap2.Content.LongLength, CbtBitOrder.MostSignificantBitFirst);
        var incremental = await BackupAsync(repo, snap2, new ChangedExtents(false, null, cbt), full, streaming);
        var i = incremental.Result.Counters;
        Assert.Equal(snap2.Content.LongLength, i.ProcessedBytes);
        Assert.Equal(i.ProcessedBytes, i.UnchangedBytes + i.ZeroBytes + i.DedupedBytes + i.NewDataBytes);
        Assert.Equal(ChunkSize, i.NewDataBytes);
        Assert.Equal(ChunkSize, i.DedupedBytes);
        Assert.Equal(22L * ChunkSize, i.UnchangedBytes);
        if (streaming)
        {
            // The stream carries the whole disk except empty regions; only the two dirty chunks are processed.
            Assert.InRange(i.ReadBytes, 20L * ChunkSize, snap2.Content.LongLength);
        }
        else
        {
            Assert.Equal(2L * ChunkSize, i.ReadBytes);
        }
    }

    [Fact]
    public async Task Streaming_source_with_decreasing_offsets_is_rejected()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        await using var writer = repo.BeginWrite(Guid.CreateVersion7(), "vm", null);
        var job = new DiskBackupJob { Reader = new BackwardsStream(), Writer = writer, ChunkSize = ChunkSize, Changes = ChangedExtents.Full("test") };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => DiskBackupPipeline.RunAsync(job, Ct));
        Assert.Contains("offset", ex.Message, StringComparison.Ordinal);
    }

    private sealed class BackwardsStream : ISequentialDiskReader
    {
        public long Capacity => 4L * ChunkSize;

        public string TransportName => "test";

        public async Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken)
        {
            var data = new byte[1000];
            data[0] = 1;
            await sink(2L * ChunkSize, data, cancellationToken);
            await sink(ChunkSize, data, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Backup(Guid RestorePointId, DiskBackupResult Result);

    private static async Task<Backup> BackupAsync(LocalDirectoryRepository repo, InMemoryDisk snapshot, ChangedExtents changes, Backup? baseline, bool streaming = false)
    {
        var rpId = Guid.CreateVersion7();
        using var pin = baseline is null ? null : repo.Pin(baseline.RestorePointId);
        await using var writer = repo.BeginWrite(rpId, "vm", pin);
        await using IDiskSource reader = streaming ? snapshot.OpenSequentialReader() : snapshot.OpenReader(maxRequestBytes: 256 * 1024);
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
