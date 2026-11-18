using System.Security.Cryptography;
using Backdafuckup.Testing.Lvhd;

namespace Backdafuckup.Lvhd.Tests;

/// <summary>Reading an LVHD SR from a snapshot LUN in user space: LVM2 metadata, VHD chains, and every refusal.</summary>
public sealed class LvhdTests
{
    private const string Sr = "3b1f0c55-7a1e-4c1f-9e2a-0d6c3b8e9f10";
    private const string BaseVdi = "11111111-2222-3333-4444-555555555555";
    private const string LeafVdi = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const int Block = 64 * 1024;
    private const long Capacity = 1L << 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("", 0xf597a6cfu)]
    [InlineData("LVM2", 0x34ff1083u)]
    [InlineData("VG_XenStorage-1234 {\nseqno = 1\n}\n", 0x724d745au)]
    public void Lvm_crc_matches_zlib_derived_vectors(string text, uint expected) =>
        Assert.Equal(expected, Lvm2Reader.Crc(0xf597a6cf, System.Text.Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void Lvm_crc_of_512_bytes_matches()
    {
        var data = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        Assert.Equal(0xc95beec5u, Lvm2Reader.Crc(0xf597a6cf, data));
    }

    [Fact]
    public async Task The_newest_metadata_names_the_volumes_and_a_volume_of_two_segments_reads_in_order()
    {
        var content = RandomNumberGenerator.GetBytes((int)(3 * PvBuilder.ExtentBytes) + 1000);
        var older = PvBuilder.Text($"VG_XenStorage-{Sr}", [], seqno: 6, peCount: 1);
        var image = PvBuilder.Build($"VG_XenStorage-{Sr}", [new PvBuilder.Volume("MGT", Extents: 1), new PvBuilder.Volume("VHD-data", content, Segments: 2)], seqno: 7, olderText: older);
        var source = new MemorySource(image);

        var group = await Lvm2Reader.ReadAsync(source, Ct);

        Assert.Equal(($"VG_XenStorage-{Sr}", 7L, PvBuilder.ExtentSectors), (group.Name, group.Seqno, group.ExtentSizeSectors));
        Assert.Equal(["MGT", "VHD-data"], group.LogicalVolumes.Select(l => l.Name));
        Assert.Equal(["xs_ver=1"], group.LogicalVolumes[1].Tags);
        var volume = Lvm2Reader.Open(source, group, group.Find("VHD-data")!);
        var read = new byte[content.Length];
        await volume.ReadAsync(0, read, Ct);
        Assert.Equal(content, read);
    }

    [Fact]
    public async Task A_wrong_checksum_or_no_label_is_refused()
    {
        var bad = PvBuilder.Build($"VG_XenStorage-{Sr}", [new PvBuilder.Volume("MGT")], corruptText: true);
        Assert.Contains("wrong checksum", (await Assert.ThrowsAsync<LvmFormatException>(() => Lvm2Reader.ReadAsync(new MemorySource(bad), Ct))).Message, StringComparison.Ordinal);
        Assert.Contains("not an LVM physical volume", (await Assert.ThrowsAsync<LvmFormatException>(() => Lvm2Reader.ReadAsync(new MemorySource(new byte[1 << 20]), Ct))).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chain_reads_each_sector_from_the_newest_member_and_lists_the_allocated_blocks()
    {
        var (baseVhd, leafVhd, expected) = Chain();
        var reader = await VhdChainReader.OpenAsync([new MemorySource(leafVhd), new MemorySource(baseVhd)], Ct);

        Assert.Equal(Capacity, reader.Capacity);
        var all = new byte[Capacity];
        await reader.ReadAsync(0, all, Ct);
        Assert.Equal(expected, all);
        Assert.Equal([new Core.Extents.DiskExtent(0, Block), new Core.Extents.DiskExtent(2 * Block, 2 * Block)], reader.AllocatedExtents);
    }

    [Fact]
    public async Task An_lvhd_sr_on_a_lun_reads_the_vdis_chain_as_xapi_reported_it()
    {
        var (baseVhd, leafVhd, expected) = Chain();
        var image = PvBuilder.Build($"VG_XenStorage-{Sr}", [new PvBuilder.Volume("MGT"), new PvBuilder.Volume($"VHD-{BaseVdi}", baseVhd), new PvBuilder.Volume($"VHD-{LeafVdi}", leafVhd)]);
        var reader = await LvhdSnapshot.OpenAsync(new MemorySource(image), Sr, Expected(baseVhd, leafVhd), Ct);
        var all = new byte[Capacity];
        await reader.ReadAsync(0, all, Ct);
        Assert.Equal(expected, all);
    }

    [Fact]
    public async Task Every_doubt_refuses_the_snapshot_with_the_reason()
    {
        var (baseVhd, leafVhd, _) = Chain();
        var good = new[] { new PvBuilder.Volume("MGT"), new PvBuilder.Volume($"VHD-{BaseVdi}", baseVhd), new PvBuilder.Volume($"VHD-{LeafVdi}", leafVhd) };

        async Task<string> Refusal(byte[] image, IReadOnlyList<ExpectedVdi>? chain = null, string sr = Sr) =>
            (await Assert.ThrowsAsync<LvhdRefusedException>(() => LvhdSnapshot.OpenAsync(new MemorySource(image), sr, chain ?? Expected(baseVhd, leafVhd), Ct))).Message;

        var image = PvBuilder.Build($"VG_XenStorage-{Sr}", good);
        Assert.Contains("not the one of SR 00000000", await Refusal(image, sr: "00000000-0000-0000-0000-000000000000"), StringComparison.Ordinal);
        Assert.Contains($"Logical volume coalesce_{LeafVdi}_1 shows an operation of the storage manager on VDI {LeafVdi}",
            await Refusal(PvBuilder.Build($"VG_XenStorage-{Sr}", [.. good, new PvBuilder.Volume($"coalesce_{LeafVdi}_1")])), StringComparison.Ordinal);
        Assert.Contains($"is stored as LV-{LeafVdi}; only VHD", await Refusal(PvBuilder.Build($"VG_XenStorage-{Sr}",
            [good[0], good[1], new PvBuilder.Volume($"LV-{LeafVdi}", leafVhd)])), StringComparison.Ordinal);
        Assert.Contains("but XAPI reported 1 (inflated or deflated since?)", await Refusal(image, [Expected(baseVhd, leafVhd)[0] with { PhysicalUtilisation = 1 }, Expected(baseVhd, leafVhd)[1]]),
            StringComparison.Ordinal);
        Assert.Contains("only linear segments are read", await Refusal(PvBuilder.Build($"VG_XenStorage-{Sr}",
            [good[0], good[1], new PvBuilder.Volume($"VHD-{LeafVdi}", leafVhd, Striped: true)])), StringComparison.Ordinal);

        // The VHD of the leaf names another parent than the base on the LUN.
        var stranger = VhdBuilder.Build(Capacity, Block, Guid.NewGuid(), Guid.NewGuid(), new Dictionary<long, byte[]> { [0] = new byte[512] });
        var strange = PvBuilder.Build($"VG_XenStorage-{Sr}", [good[0], good[1], new PvBuilder.Volume($"VHD-{LeafVdi}", stranger)]);
        Assert.Contains("the chain is not what was expected", await Refusal(strange, Expected(baseVhd, stranger)), StringComparison.Ordinal);

        // XAPI reported another virtual size.
        Assert.Contains($"VDI {LeafVdi} is {Capacity} bytes on the LUN, but XAPI reported 4096", await Refusal(image, [Expected(baseVhd, leafVhd)[0] with { VirtualSize = 4096 },
            Expected(baseVhd, leafVhd)[1]]), StringComparison.Ordinal);

        // A fixed VHD as the base is not read.
        var fixedBase = VhdBuilder.Build(Capacity, Block, BaseId, null, new Dictionary<long, byte[]>(), diskTypeOverride: 2);
        Assert.Contains("is a fixed disk", await Refusal(PvBuilder.Build($"VG_XenStorage-{Sr}", [good[0], new PvBuilder.Volume($"VHD-{BaseVdi}", fixedBase), good[2]]),
            Expected(fixedBase, leafVhd)), StringComparison.Ordinal);
    }

    private static readonly Guid BaseId = Guid.Parse("0aaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid LeafId = Guid.Parse("0bbbbbbb-0000-4000-8000-000000000002");

    /// <summary>A base with data in blocks 0 and 2, a leaf that overwrites sectors of block 0 and writes block 3.</summary>
    private static (byte[] Base, byte[] Leaf, byte[] Expected) Chain()
    {
        var random = new Random(42);
        byte[] Sector()
        {
            var bytes = new byte[512];
            random.NextBytes(bytes);
            return bytes;
        }

        var baseSectors = new Dictionary<long, byte[]>();
        foreach (var s in new long[] { 0, 1, 2, 5, 127, 256, 300 })
        {
            baseSectors[s] = Sector();
        }

        var leafSectors = new Dictionary<long, byte[]> { [1] = Sector(), [3] = Sector(), [400] = Sector(), [511] = Sector() };
        var expected = new byte[Capacity];
        foreach (var (s, data) in baseSectors.Concat(leafSectors))
        {
            data.CopyTo(expected, s * 512);
        }

        return (VhdBuilder.Build(Capacity, Block, BaseId, null, baseSectors), VhdBuilder.Build(Capacity, Block, LeafId, BaseId, leafSectors), expected);
    }

    private static List<ExpectedVdi> Expected(byte[] baseVhd, byte[] leafVhd) =>
    [
        new(LeafVdi, Capacity, Round(leafVhd.Length)),
        new(BaseVdi, Capacity, Round(baseVhd.Length)),
    ];

    private static long Round(long bytes) => Math.Max(1, (bytes + PvBuilder.ExtentBytes - 1) / PvBuilder.ExtentBytes) * PvBuilder.ExtentBytes;
}
