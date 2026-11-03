using System.Net;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Agent.Core;
using Backdafuckup.Testing;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using ISCSI.Client;

namespace Backdafuckup.Agent.Tests;

/// <summary>The guest-independent parts of publishing: block source, copy-on-write disk, identity patch, loopback target.</summary>
public sealed class PublishingTests : IDisposable
{
    private const int BlockSize = 1 << 20;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-publish-tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public PublishingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void The_cow_disk_reads_the_backup_and_keeps_writes_in_the_overlay()
    {
        var image = Pattern((8 * BlockSize) + (3 * 512), seed: 1);
        var hash = SHA256.HashData(image);
        var server = new BlockServer(image);
        var overlay = Path.Combine(_root, "overlay-0.bin");
        var disk = Cow(server, image.LongLength, overlay, new OverlayBudget(4 * CowDisk.OverlayBlock));

        Assert.Equal(image.LongLength, disk.Size);
        Assert.Equal(image, ReadAll(disk));
        Assert.Equal(image.LongLength, disk.FetchedBytes);

        // Three sectors across the first 64 KiB boundary: two overlay blocks, the rest of each copied from the backup.
        var data = Pattern(3 * 512, seed: 2);
        disk.WriteSectors(127, data);
        Assert.Equal(data, disk.ReadSectors(127, 3));
        Assert.Equal(image.AsSpan(126 * 512, 512).ToArray(), disk.ReadSectors(126, 1));
        Assert.Equal(image.AsSpan(130 * 512, 512).ToArray(), disk.ReadSectors(130, 1));
        Assert.Equal(2L * CowDisk.OverlayBlock, disk.OverlayBytes);

        // The last, shorter block of the disk can be written too.
        var tail = Pattern(512, seed: 3);
        disk.WriteSectors(disk.TotalSectors - 1, tail);
        Assert.Equal(tail, disk.ReadSectors(disk.TotalSectors - 1, 1));
        Assert.Equal(hash, SHA256.HashData(image));

        // The budget is shared by the disks of a publish: a fifth overlay block fails as a medium error (IOException).
        disk.WriteSectors(4 * BlockSize / 512, Pattern(512, seed: 4));
        var full = Assert.Throws<IOException>(() => disk.WriteSectors(5 * BlockSize / 512, Pattern(512, seed: 5)));
        Assert.Contains("overlay of the publish is full", full.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => disk.ReadSectors(disk.TotalSectors, 1));

        disk.Dispose();
        Assert.False(File.Exists(overlay));
    }

    [Fact]
    public void The_cow_disk_reports_an_unreachable_server_as_an_io_error()
    {
        var image = Pattern(2 * BlockSize, seed: 6);
        var server = new BlockServer(image) { Fail = true };
        using var disk = Cow(server, image.LongLength, Path.Combine(_root, "overlay.bin"), new OverlayBudget(1 << 20));

        var error = Assert.Throws<IOException>(() => disk.ReadSectors(0, 1));
        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, server.Requests);
    }

    [Fact]
    public void The_identity_patch_gives_a_gpt_disk_new_guids_and_no_drive_letters()
    {
        var image = WindowsTestDisk.Create(
            [
                new WindowsTestDisk.Volume('C', 8, new Dictionary<string, byte[]>()),
                new WindowsTestDisk.Volume('D', 4, new Dictionary<string, byte[]> { ["data.txt"] = Encoding.UTF8.GetBytes("published") }),
            ]);
        var hash = SHA256.HashData(image);
        using var disk = Cow(new BlockServer(image), image.LongLength, Path.Combine(_root, "overlay.bin"), new OverlayBudget(1 << 20));

        Assert.StartsWith("GPT disk GUID", DiskIdentity.Patch(disk), StringComparison.Ordinal);

        var patched = ReadAll(disk);
        var before = Gpt(image);
        var after = Gpt(patched);
        Assert.NotEqual(before.DiskGuid, after.DiskGuid);
        Assert.Equal(before.Partitions.Count, after.Partitions.Count);
        for (var i = 0; i < before.Partitions.Count; i++)
        {
            var (old, now) = ((GuidPartitionInfo)before.Partitions[i], (GuidPartitionInfo)after.Partitions[i]);
            Assert.NotEqual(old.Identity, now.Identity);
            Assert.Equal(old.FirstSector, now.FirstSector);
            Assert.Equal(old.GuidType, now.GuidType);
            Assert.NotEqual(0UL, (ulong)now.Attributes & 0x8000000000000000UL);
        }

        // Both headers and both partition arrays carry the new identity with valid CRCs.
        foreach (var lba in new[] { 1L, (patched.LongLength / 512) - 1 })
        {
            var header = patched.AsSpan((int)(lba * 512), 92).ToArray();
            Assert.Equal("EFI PART", Encoding.ASCII.GetString(header, 0, 8));
            Assert.Equal(after.DiskGuid, new Guid(header.AsSpan(56, 16)));
            var crc = BitConverter.ToUInt32(header, 16);
            Array.Clear(header, 16, 4);
            Assert.Equal(crc, Crc32(header));
            var entries = patched.AsSpan((int)(BitConverter.ToInt64(header, 72) * 512), BitConverter.ToInt32(header, 80) * BitConverter.ToInt32(header, 84)).ToArray();
            Assert.Equal(BitConverter.ToUInt32(header, 88), Crc32(entries));
        }

        // The partitions themselves are untouched, and the backup did not change.
        var first = (GuidPartitionInfo)after.Partitions[1];
        Assert.Equal(
            image.AsSpan((int)(first.FirstSector * 512), 4096).ToArray(),
            patched.AsSpan((int)(first.FirstSector * 512), 4096).ToArray());
        Assert.Equal(hash, SHA256.HashData(image));
    }

    [Fact]
    public void The_identity_patch_gives_an_mbr_disk_a_new_signature()
    {
        var size = 4L * 1024 * 1024;
        var stream = new MemoryStream();
        stream.SetLength(size);
        var raw = DiscUtils.Raw.Disk.Initialize(stream, Ownership.None, size);
        BiosPartitionTable.Initialize(raw, WellKnownPartitionType.WindowsNtfs);
        var image = stream.ToArray();
        image[0x1B8] = 0x12;
        image[0x1B9] = 0x34;
        using var disk = Cow(new BlockServer(image), image.LongLength, Path.Combine(_root, "overlay.bin"), new OverlayBudget(1 << 20));

        Assert.StartsWith("MBR signature", DiskIdentity.Patch(disk), StringComparison.Ordinal);

        var mbr = disk.ReadSectors(0, 1);
        Assert.NotEqual(image.AsSpan(0x1B8, 4).ToArray(), mbr.AsSpan(0x1B8, 4).ToArray());
        Assert.Equal(image.AsSpan(0x1BE, 66).ToArray(), mbr.AsSpan(0x1BE, 66).ToArray());
        Assert.Equal("no partition table", DiskIdentity.Patch(Cow(new BlockServer(new byte[1 << 20]), 1 << 20, Path.Combine(_root, "empty.bin"), new OverlayBudget(1 << 20))));
    }

    [Fact]
    public void The_loopback_target_serves_its_disks_only_to_the_authorized_initiator()
    {
        var images = new[] { Pattern(2 * BlockSize, seed: 7), Pattern(BlockSize + 4096, seed: 8) };
        var budget = new OverlayBudget(1 << 20);
        var disks = images.Select((image, i) => (DiskAccessLibrary.Disk)Cow(new BlockServer(image), image.LongLength, Path.Combine(_root, $"overlay-{i}.bin"), budget)).ToList();
        var refused = new List<string>();
        using var target = new PublishTarget(
            "iqn.2026-11.de.backdafuckup:publish-test",
            disks,
            "BDFUTEST",
            (endpoint, name) => IPAddress.IsLoopback(endpoint.Address) && name == "iqn.1991-05.com.microsoft:guest",
            line => refused.Add(line));
        Assert.Equal("BDFUTEST-1", PublishTarget.Serial(target.SerialPrefix, 1));

        var intruder = new ISCSIClient("iqn.1991-05.com.microsoft:intruder");
        Assert.True(intruder.Connect(IPAddress.Loopback, target.Port));
        Assert.False(intruder.Login(target.TargetName));
        intruder.Disconnect();
        Assert.Contains(refused, l => l.Contains("intruder", StringComparison.Ordinal));

        var guest = new ISCSIClient("iqn.1991-05.com.microsoft:guest");
        Assert.True(guest.Connect(IPAddress.Loopback, target.Port));
        Assert.True(guest.Login(target.TargetName));
        try
        {
            Assert.Equal([(ushort)0, (ushort)1], guest.GetLUNList());
            Assert.Equal(images[1].AsSpan(BlockSize, 4096).ToArray(), guest.Read(1, BlockSize / 512, 8, 512));
            var data = Pattern(1024, seed: 9);
            Assert.True(guest.Write(0, 100, data, 512));
            Assert.Equal(data, guest.Read(0, 100, 2, 512));
            Assert.Equal(images[0].AsSpan(102 * 512, 512).ToArray(), guest.Read(0, 102, 1, 512));
            Assert.Equal(CowDisk.OverlayBlock, budget.Used);
        }
        finally
        {
            guest.Logout();
            guest.Disconnect();
        }
    }

    private CowDisk Cow(BlockServer server, long size, string overlay, OverlayBudget budget)
    {
        var http = new HttpClient(server) { BaseAddress = new Uri("https://backup.test/") };
        _disposables.Add(http);
        var disk = new CowDisk(new PublishBlockSource(http, "api/agent/publishes/1/disks/0/blocks/", size, BlockSize), overlay, budget);
        _disposables.Add(disk);
        return disk;
    }

    private static byte[] ReadAll(DiskAccessLibrary.Disk disk)
    {
        var result = new byte[disk.Size];
        for (long sector = 0; sector < disk.TotalSectors; sector += 2048)
        {
            var chunk = disk.ReadSectors(sector, (int)Math.Min(2048, disk.TotalSectors - sector));
            System.Buffer.BlockCopy(chunk, 0, result, (int)(sector * 512), chunk.Length);
        }

        return result;
    }

    private static GuidPartitionTable Gpt(byte[] image) =>
        new(new DiscUtils.Raw.Disk(new MemoryStream(image, writable: false), Ownership.None));

    private static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>The server's block endpoint: block n of the image, or 503 while <see cref="Fail"/> is set.</summary>
    private sealed class BlockServer(byte[] image) : HttpMessageHandler
    {
        private int _requests;

        public bool Fail { get; init; }

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (Fail)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var block = long.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            var offset = (int)(block * BlockSize);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image, offset, Math.Min(BlockSize, image.Length - offset)) });
        }
    }
}
