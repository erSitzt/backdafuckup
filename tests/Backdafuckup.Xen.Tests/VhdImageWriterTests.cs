using Backdafuckup.Core.Extents;
using Backdafuckup.Testing;
using Backdafuckup.Xen;

namespace Backdafuckup.Xen.Tests;

/// <summary>The VHD stream for import_raw_vdi?format=vhd, checked against an independent reader and our own export reader.</summary>
public sealed class VhdImageWriterTests
{
    private const int MiB = 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<long, int[]> Disks => new()
    {
        { 16L * MiB, [5] },                  // one block with data in the middle
        { 16L * MiB, [0, 1, 2, 15] },        // first two blocks and the last
        { 16L * MiB, [] },                   // empty disk: no blocks at all
        { (5L * MiB) + (3 * 512), [0, 4, 5] }, // partial last block (not a multiple of 2 MiB)
        { 4096, [0] },                       // a tiny disk, smaller than one block
    };

    [Theory]
    [MemberData(nameof(Disks))]
    public async Task Writes_only_blocks_with_data_and_reads_back_identically(long capacity, int[] dataMiBs)
    {
        var disk = Disk(capacity, dataMiBs);
        var extents = Extents(disk);
        var writer = VhdImageWriter.Create(capacity, extents);
        var reports = new List<long>();

        using var output = new MemoryStream();
        await writer.WriteAsync(output, new MemoryStream(disk), new SyncProgress(reports.Add), Ct);
        var vhd = output.ToArray();

        Assert.Equal(writer.Length, vhd.LongLength);
        Assert.Equal(writer.Length, writer.BytesWritten);
        Assert.Equal(capacity, reports[^1]);
        var expectedBlocks = Enumerable.Range(0, writer.BlockCount)
            .Count(b => disk.AsSpan((int)((long)b * VhdImageWriter.BlockSize), (int)Math.Min(VhdImageWriter.BlockSize, capacity - ((long)b * VhdImageWriter.BlockSize))).ContainsAnyExcept((byte)0));
        Assert.Equal(expectedBlocks, writer.AllocatedBlocks);

        // An independent reader (file view, checksums, footer copy) and our streaming reader (as for exports) agree.
        Assert.Equal(disk, VhdTestImage.Read(vhd));
        var reader = await VhdStreamReader.OpenAsync(new MemoryStream(vhd), Ct);
        Assert.Equal(capacity, reader.CurrentSize);
        var streamed = new byte[capacity];
        await reader.StreamAsync(capacity, (offset, data, _) =>
        {
            data.Span[..(int)Math.Min(data.Length, capacity - offset)].CopyTo(streamed.AsSpan((int)offset));
            return ValueTask.CompletedTask;
        }, Ct);
        Assert.Equal(disk, streamed);
        Assert.True(reader.EndedWithFooter);
    }

    [Fact]
    public async Task Data_outside_the_extents_stops_the_upload_instead_of_dropping_it()
    {
        var disk = Disk(8L * MiB, [1, 6]);
        var writer = VhdImageWriter.Create(disk.LongLength, [new DiskExtent(1L * MiB, MiB)]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(new MemoryStream(), new MemoryStream(disk), null, Ct));
        Assert.Contains($"at byte {6L * MiB}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Xen:HttpImportFormat=Raw", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_short_image_is_rejected()
    {
        var writer = VhdImageWriter.Create(8L * MiB, [new DiskExtent(0, 8L * MiB)]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => writer.WriteAsync(new MemoryStream(), new MemoryStream(new byte[5 * MiB]), null, Ct));
    }

    [Fact]
    public void Extents_outside_the_disk_and_odd_sizes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => VhdImageWriter.Create(8L * MiB, [new DiskExtent(7L * MiB, 2L * MiB)]));
        Assert.Throws<ArgumentException>(() => VhdImageWriter.Create((8L * MiB) + 100, []));
    }

    /// <summary>A disk with random data in the given MiB (and a single byte at the very end of the last one).</summary>
    private static byte[] Disk(long capacity, int[] dataMiBs)
    {
        var disk = new byte[capacity];
        var random = new Random(42);
        foreach (var mib in dataMiBs)
        {
            var start = (long)mib * MiB;
            random.NextBytes(disk.AsSpan((int)start, (int)Math.Min(MiB, capacity - start)));
        }

        return disk;
    }

    /// <summary>Data extents as a restore point reports them: the 1 MiB chunks that are not all-zero.</summary>
    private static List<DiskExtent> Extents(byte[] disk)
    {
        var extents = new List<DiskExtent>();
        for (long offset = 0; offset < disk.LongLength; offset += MiB)
        {
            var length = (int)Math.Min(MiB, disk.LongLength - offset);
            if (disk.AsSpan((int)offset, length).ContainsAnyExcept((byte)0))
            {
                extents.Add(new DiskExtent(offset, length));
            }
        }

        return extents;
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
