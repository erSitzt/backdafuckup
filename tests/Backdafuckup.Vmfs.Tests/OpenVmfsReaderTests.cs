using System.Security.Cryptography;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Testing.Arrays;

namespace Backdafuckup.Vmfs.Tests;

/// <summary>
/// The OpenVmfs adapter against datastores ESXi wrote (tests/fixtures/vmfs6): what it reads must hash like ESXi's own reads
/// of the same disks, and its refusals must arrive as the fallback reasons the snapshot service knows.
/// </summary>
public sealed class OpenVmfsReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_disk_with_three_vm_snapshots_on_esxi_8_reads_like_esxi_at_every_level()
    {
        var truth = VmfsFixtures.Truth("fixture-chain-esxi8-v1");
        await using var lun = VmfsFixtures.Open("fixture-chain-esxi8-v1");
        await using var volume = await new OpenVmfsReader().OpenVolumeAsync(lun, "fixture-chain-esxi8-v1", Ct);
        Assert.Equal($"VMFS 6.82 (OpenVmfs {OpenVmfsReader.LibraryVersion})", volume.FormatVersion);
        Assert.StartsWith("0.3.0", OpenVmfsReader.LibraryVersion, StringComparison.Ordinal);

        // The base, then each level of the chain against ESXi's consolidated clone of that level.
        foreach (var (descriptor, expected) in new[]
                 {
                     ("guest/t.vmdk", "guest/t-flat.vmdk"), ("guest/t-000001.vmdk", "clone8/at1-flat.vmdk"),
                     ("guest/t-000002.vmdk", "clone8/at2-flat.vmdk"), ("guest/t-000003.vmdk", "clone8/at3-flat.vmdk"),
                 })
        {
            await using var disk = await volume.OpenDiskAsync(descriptor, Ct);
            Assert.Equal("array-vmfs", disk.TransportName);
            Assert.True(truth[expected] == await HashAsync(disk), $"{descriptor} differs from ESXi's {expected}");
        }
    }

    [Fact]
    public async Task Reading_only_the_allocated_extents_gives_the_same_disk()
    {
        var truth = VmfsFixtures.Truth("fixture-chain-esxi8-v1");
        await using var lun = VmfsFixtures.Open("fixture-chain-esxi8-v1");
        await using var volume = await new OpenVmfsReader().OpenVolumeAsync(lun, "fixture-chain-esxi8-v1", Ct);
        await using var disk = await volume.OpenDiskAsync("guest/t-000003.vmdk", Ct);

        // What a full backup reads: the allocated extents, zeros elsewhere; and far fewer bytes from the LUN than the disk holds.
        Assert.NotEmpty(disk.AllocatedExtents);
        var before = lun.BytesRead;
        Assert.Equal(truth["clone8/at3-flat.vmdk"], await HashAsync(disk, onlyAllocated: true));
        Assert.True(lun.BytesRead - before <= disk.AllocatedExtents.Sum(e => e.Length), $"{lun.BytesRead - before} bytes read");
    }

    [Fact]
    public async Task Disks_on_a_datastore_over_16_tib_read_like_esxi()
    {
        // Cluster datastores of 30 TiB are the normal case: above 16 TiB the volume continues behind a gap.
        var truth = VmfsFixtures.Truth("fixture-big-v1");
        await using var lun = VmfsFixtures.Open("fixture-big-v1");
        Assert.True(lun.Capacity > 16L << 40);
        await using var volume = await new OpenVmfsReader().OpenVolumeAsync(lun, "fixture-big-v1", Ct);
        foreach (var name in new[] { "t/thin", "t/lazy" })
        {
            await using var disk = await volume.OpenDiskAsync($"{name}.vmdk", Ct);
            Assert.True(truth[$"{name}-flat.vmdk"] == await HashAsync(disk), $"{name} differs from ESXi");
        }
    }

    [Fact]
    public async Task A_disk_a_host_was_writing_to_is_refused_as_in_use_and_the_one_under_the_vm_snapshot_opens()
    {
        await using var lun = VmfsFixtures.Open("fixture-inuse-v1");
        await using var volume = await new OpenVmfsReader().OpenVolumeAsync(lun, "array snapshot bdfu-test-01", Ct);

        var refused = await Assert.ThrowsAsync<VmfsRefusedException>(() => volume.OpenDiskAsync("probe/m-000001.vmdk", Ct));
        Assert.Equal(VmfsRefusalKinds.InUse, refused.Kind);
        Assert.StartsWith("probe/m-000001.vmdk on array snapshot bdfu-test-01: ", refused.Message, StringComparison.Ordinal);

        await using var under = await volume.OpenDiskAsync("probe/m.vmdk", Ct);
        Assert.True(under.Capacity > 0);

        var missing = await Assert.ThrowsAsync<VmfsRefusedException>(() => volume.OpenDiskAsync("probe/nothing.vmdk", Ct));
        Assert.Equal(VmfsRefusalKinds.NotFound, missing.Kind);
    }

    [Fact]
    public async Task A_lun_without_a_datastore_is_refused_and_a_failing_lun_keeps_its_exception()
    {
        await using var empty = new MemoryLun(new byte[64 << 20]);
        var none = await Assert.ThrowsAsync<VmfsRefusedException>(() => new OpenVmfsReader().OpenVolumeAsync(empty, "array snapshot bdfu-empty-01", Ct));
        Assert.StartsWith("array snapshot bdfu-empty-01: ", none.Message, StringComparison.Ordinal);
        Assert.NotEqual(VmfsRefusalKinds.Io, none.Kind);

        await using var failing = new MemoryLun(new byte[64 << 20]) { Failure = new IOException("NBD read at 0 failed: proxy gone") };
        var io = await Assert.ThrowsAsync<VmfsRefusedException>(() => new OpenVmfsReader().OpenVolumeAsync(failing, "array snapshot bdfu-gone-01", Ct));
        Assert.Equal(VmfsRefusalKinds.Io, io.Kind);
        Assert.Equal("NBD read at 0 failed: proxy gone", Assert.IsType<IOException>(io.InnerException).Message);
    }

    [Fact]
    public async Task Many_reads_of_one_disk_at_once_give_the_same_bytes()
    {
        var truth = VmfsFixtures.Truth("fixture-chain-esxi8-v1");
        await using var lun = VmfsFixtures.Open("fixture-chain-esxi8-v1", maxRequestBytes: 256 * 1024, maxConcurrentRequests: 16);
        await using var volume = await new OpenVmfsReader().OpenVolumeAsync(lun, "fixture-chain-esxi8-v1", Ct);
        await using var disk = await volume.OpenDiskAsync("guest/t-000002.vmdk", Ct);
        Assert.Equal(truth["clone8/at2-flat.vmdk"], await HashAsync(disk, parallel: 16));
    }

    /// <summary>The SHA-256 of the whole disk, read in requests of the disk's size limit, <paramref name="parallel"/> at a time.</summary>
    private static async Task<string> HashAsync(IVmfsDisk disk, bool onlyAllocated = false, int parallel = 4)
    {
        const int Piece = 4 * 1024 * 1024;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffers = Enumerable.Range(0, parallel).Select(_ => new byte[Piece]).ToArray();
        for (long offset = 0; offset < disk.Capacity; offset += (long)Piece * parallel)
        {
            var reads = new List<Task<int>>();
            for (var i = 0; i < parallel && offset + ((long)i * Piece) < disk.Capacity; i++)
            {
                var at = offset + ((long)i * Piece);
                var length = (int)Math.Min(Piece, disk.Capacity - at);
                var buffer = buffers[i];
                reads.Add(Task.Run(async () =>
                {
                    if (onlyAllocated)
                    {
                        Array.Clear(buffer);
                        foreach (var extent in disk.AllocatedExtents.Where(e => e.Offset < at + length && e.Offset + e.Length > at))
                        {
                            var from = Math.Max(extent.Offset, at);
                            var to = Math.Min(extent.Offset + extent.Length, at + length);
                            await ReadSplitAsync(disk, from, buffer.AsMemory((int)(from - at), (int)(to - from)));
                        }
                    }
                    else
                    {
                        await ReadSplitAsync(disk, at, buffer.AsMemory(0, length));
                    }

                    return length;
                }));
            }

            var lengths = await Task.WhenAll(reads);
            for (var i = 0; i < lengths.Length; i++)
            {
                hash.AppendData(buffers[i], 0, lengths[i]);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task ReadSplitAsync(IDiskReader disk, long offset, Memory<byte> destination)
    {
        for (var done = 0; done < destination.Length; done += disk.MaxRequestBytes)
        {
            await disk.ReadAsync(offset + done, destination.Slice(done, Math.Min(disk.MaxRequestBytes, destination.Length - done)), Ct);
        }
    }

    private sealed class MemoryLun(byte[] data) : IDiskReader
    {
        public Exception? Failure { get; init; }

        public long Capacity => data.LongLength;

        public string TransportName => "memory";

        public int MaxRequestBytes => 1024 * 1024;

        public int MaxConcurrentRequests => 4;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            data.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
