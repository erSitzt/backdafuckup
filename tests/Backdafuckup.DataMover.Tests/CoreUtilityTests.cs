using System.Security.Cryptography;
using System.Text.Json;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover.Tests;

[Trait("Category", "Unit")]
public sealed class CoreUtilityTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void Normalize_merges_overlapping_and_adjacent_extents()
    {
        var result = ExtentMath.Normalize([new(100, 50), new(0, 10), new(10, 5), new(140, 100), new(500, 0)]);

        Assert.Equal([new DiskExtent(0, 15), new DiskExtent(100, 140)], result);
    }

    [Fact]
    public void DirtyChunks_maps_extents_to_distinct_sorted_chunks_and_clips_at_disk_end()
    {
        var diskSize = (3L * MiB) + 100; // 4 chunks, the last one is 100 bytes
        var dirty = ExtentMath.DirtyChunks(
            [new(MiB - 1, 2), new(10, 1), new(3L * MiB, 10_000_000)],
            diskSize,
            MiB);

        Assert.Equal([0L, 1L, 3L], dirty);
        Assert.Equal(100, ExtentMath.ChunkLength(3, diskSize, MiB));
        Assert.Equal(4, ExtentMath.ChunkCount(diskSize, MiB));
    }

    [Theory]
    [InlineData(CbtBitOrder.MostSignificantBitFirst)]
    [InlineData(CbtBitOrder.LeastSignificantBitFirst)]
    public void CbtBitmap_roundtrips_extents(CbtBitOrder order)
    {
        var diskSize = (10L * CbtBitmap.BlockSize) + 123; // 11 blocks, the last one partial
        DiskExtent[] changed = [new(0, CbtBitmap.BlockSize), new(3L * CbtBitmap.BlockSize, 2L * CbtBitmap.BlockSize), new(10L * CbtBitmap.BlockSize, 123)];

        var bitmap = CbtBitmap.Encode(changed, diskSize, order);
        var decoded = CbtBitmap.DecodeBase64(Convert.ToBase64String(bitmap), diskSize, order);

        Assert.Equal(2, bitmap.Length);
        Assert.Equal(changed, decoded);
    }

    [Fact]
    public void CbtBitmap_msb_first_means_bit_0x80_of_first_byte_is_block_0()
    {
        var decoded = CbtBitmap.Decode([0x80, 0x01], 16L * CbtBitmap.BlockSize, CbtBitOrder.MostSignificantBitFirst);

        Assert.Equal([new DiskExtent(0, CbtBitmap.BlockSize), new DiskExtent(15L * CbtBitmap.BlockSize, CbtBitmap.BlockSize)], decoded);
    }

    [Fact]
    public void CbtBitmap_rejects_too_short_bitmaps()
    {
        Assert.Throws<FormatException>(() => CbtBitmap.Decode([0xFF], 9L * CbtBitmap.BlockSize, CbtBitOrder.MostSignificantBitFirst));
    }

    [Theory]
    [InlineData(CbtBitOrder.MostSignificantBitFirst)]
    [InlineData(CbtBitOrder.LeastSignificantBitFirst)]
    public void CbtBitmap_byte_ranges_cover_every_changed_block_in_either_bit_order(CbtBitOrder order)
    {
        // 21 blocks, the last one partial: 3 bitmap bytes, the last one describes only 5 blocks of the disk.
        var diskSize = (20L * CbtBitmap.BlockSize) + 4096;
        // Blocks 1 and 7 (first byte; the order moves them to other bits of it) and block 20 (third byte).
        DiskExtent[] changed = [new(CbtBitmap.BlockSize, 10), new(7L * CbtBitmap.BlockSize, CbtBitmap.BlockSize), new(20L * CbtBitmap.BlockSize, 100)];
        var bitmap = CbtBitmap.Encode(changed, diskSize, order);

        var ranges = CbtBitmap.DecodeByteRanges(bitmap, diskSize);

        Assert.Equal([new DiskExtent(0, CbtBitmap.ByteSpan), new DiskExtent(2L * CbtBitmap.ByteSpan, diskSize - (2L * CbtBitmap.ByteSpan))], ranges);
        Assert.All(changed, c => Assert.Contains(ranges, r => r.Offset <= c.Offset && c.End <= r.End));
        Assert.Equal(3L * CbtBitmap.BlockSize, CbtBitmap.ChangedBytes(bitmap, diskSize));
    }

    [Fact]
    public void CbtBitmap_byte_ranges_are_the_same_for_both_orders_and_select_the_same_chunks_as_an_exact_decoding()
    {
        var diskSize = 64L * CbtBitmap.ByteSpan;
        var random = new Random(7);
        var bitmap = new byte[64];
        for (var i = 0; i < bitmap.Length; i += 3)
        {
            bitmap[i] = (byte)random.Next(1, 256);
        }

        var ranges = CbtBitmap.DecodeByteRanges(bitmap, diskSize);
        Assert.Equal(22, ranges.Count);
        foreach (var chunkSize in new[] { 512 * 1024, 1024 * 1024, 4 * 1024 * 1024 })
        {
            var byBytes = ExtentMath.DirtyChunks(ranges, diskSize, chunkSize);
            Assert.Equal(byBytes, ExtentMath.DirtyChunks(CbtBitmap.Decode(bitmap, diskSize, CbtBitOrder.MostSignificantBitFirst), diskSize, chunkSize));
            Assert.Equal(byBytes, ExtentMath.DirtyChunks(CbtBitmap.Decode(bitmap, diskSize, CbtBitOrder.LeastSignificantBitFirst), diskSize, chunkSize));
        }

        Assert.Throws<FormatException>(() => CbtBitmap.DecodeByteRanges([0xFF], 9L * CbtBitmap.BlockSize));
        Assert.Empty(CbtBitmap.DecodeByteRanges(new byte[2], 16L * CbtBitmap.BlockSize));
    }

    [Fact]
    public void ChunkId_matches_sha256_and_roundtrips_hex()
    {
        var data = RandomNumberGenerator.GetBytes(4096);
        var id = ChunkId.Compute(data);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), id.ToHex());
        Assert.Equal(id, ChunkId.Parse(id.ToHex()));
        Assert.False(id.IsZero);
        Assert.True(ChunkId.Zero.IsZero);
        Assert.Equal(id.ToHex()[..4], id.Prefix);
    }

    [Fact]
    public void Manifest_json_roundtrips_with_string_enums()
    {
        var manifest = new RestorePointManifest
        {
            RestorePointId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            CreatedUtc = DateTimeOffset.UtcNow,
            Kind = RestorePointKind.Incremental,
            Source = new ManifestSource(HypervisorKind.XenServer, "pool", "vm", "VM 1", "snap"),
            Vm = new VmDescriptor { VmId = "vm", Name = "VM 1", VCpus = 2, Nics = [new VmNic("0", "aa:bb", "net", "Net", 1500)] },
            Chunking = new ManifestChunking(MiB, "sha256", "zstd"),
            Disks =
            [
                new ManifestDisk
                {
                    Key = "0",
                    SourceDiskId = "vdi",
                    Name = "disk",
                    CapacityBytes = 10 * MiB,
                    Index = new ManifestIndexRef("disk-0.idx", "abc", 10),
                    ChangeToken = new ChangeToken("xen-cbt", "uuid"),
                    Stats = new DiskBackupStats { Mode = DiskBackupMode.Cbt, ReadBytes = 42 },
                },
            ],
        };

        var json = JsonSerializer.Serialize(manifest, CoreJsonContext.Default.RestorePointManifest);
        var back = JsonSerializer.Deserialize(json, CoreJsonContext.Default.RestorePointManifest)!;

        Assert.Contains("\"kind\": \"Incremental\"", json, StringComparison.Ordinal);
        Assert.Equal(manifest.Disks[0].ChangeToken, back.Disks[0].ChangeToken);
        Assert.Equal(DiskBackupMode.Cbt, back.Disks[0].Stats.Mode);
        Assert.Equal("VM 1", back.Vm.Name);
    }
}
