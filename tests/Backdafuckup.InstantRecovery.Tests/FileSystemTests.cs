using System.Net;
using System.Text;
using Backdafuckup.Nfs;

namespace Backdafuckup.InstantRecovery.Tests;

/// <summary>The instant recovery file system (spec 2, task 2): overlay over a restore point disk, journal, handles, limits.</summary>
public sealed class FileSystemTests : IDisposable
{
    private const ulong ExportId = 0xabcdef0012345678;
    private const long DiskSize = 8L * 1024 * 1024 + 4096; // not a multiple of the 64 KiB block
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly NfsCaller Esx = new(new AuthSys(0, "esx01", 0, 0, []), new IPEndPoint(IPAddress.Parse("10.26.0.21"), 800));

    private readonly string _root = Directory.CreateTempSubdirectory("bdfu-ir-").FullName;
    private readonly MemoryBase _base = new(DiskSize, seed: 11);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InstantRecoveryFileSystem Open(InstantRecoveryFsOptions? options = null) =>
        InstantRecoveryFileSystem.Open(_root, ExportId, Key, key => key == "rp1/scsi0:0" ? _base : throw new InvalidOperationException(key), options ?? new InstantRecoveryFsOptions { FreeSpaceReserveBytes = 0 });

    /// <summary>A restore point disk in memory: random data with zero regions.</summary>
    private sealed class MemoryBase : IBaseImage
    {
        public MemoryBase(long size, int seed)
        {
            Data = new byte[size];
            new Random(seed).NextBytes(Data);
            Array.Clear(Data, 1 << 20, 1 << 20); // a zero MiB
        }

        public byte[] Data { get; }

        public long Size => Data.Length;

        public long AllocatedBytes => Data.LongLength - (1 << 20);

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            Data.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<byte[]> ReadAllAsync(InstantRecoveryFileSystem fs, ReadOnlyMemory<byte> file, long size)
    {
        var all = new byte[size];
        for (long at = 0; at < size; at += 1 << 20)
        {
            var read = await fs.ReadAsync(Esx, file, at, (int)Math.Min(1 << 20, size - at), Ct);
            read.Data.CopyTo(all.AsMemory((int)at));
        }

        return all;
    }

    [Fact]
    public async Task Random_reads_and_writes_match_a_reference_model_also_after_reopening()
    {
        var model = (byte[])_base.Data.Clone();
        byte[] handle;
        using (var fs = Open())
        {
            handle = fs.CreateBaseFile("vm01/disk0-flat.vmdk", "rp1/scsi0:0", DiskSize).ToArray();
            Assert.Equal(_base.Data, await ReadAllAsync(fs, handle, DiskSize));

            // Concurrent writes to disjoint regions (partial blocks, block-crossing, whole blocks), each also in the model.
            var random = new Random(5);
            var writes = Enumerable.Range(0, 64).Select(i =>
            {
                var start = (i * 128L * 1024) + random.Next(0, 60_000);
                var length = random.Next(1, 120 * 1024);
                var data = new byte[length];
                random.NextBytes(data);
                return (start, data);
            }).Where(w => w.start + w.data.Length <= DiskSize).ToList();
            foreach (var (start, data) in writes)
            {
                data.CopyTo(model, start);
            }

            await Parallel.ForEachAsync(writes, Ct, async (w, ct) =>
                Assert.Equal(w.data.Length, (await fs.WriteAsync(Esx, handle, w.start, w.data, NfsStable.Unstable, ct)).Count));

            // Then sequential random operations, compared with the model as they go.
            for (var i = 0; i < 300; i++)
            {
                var at = random.NextInt64(0, DiskSize);
                var length = (int)Math.Min(random.Next(1, 200 * 1024), DiskSize - at);
                if (random.Next(3) == 0)
                {
                    var data = new byte[length];
                    random.NextBytes(data);
                    data.CopyTo(model, at);
                    await fs.WriteAsync(Esx, handle, at, data, i % 10 == 0 ? NfsStable.FileSync : NfsStable.Unstable, Ct);
                }
                else
                {
                    var read = await fs.ReadAsync(Esx, handle, at, length, Ct);
                    Assert.True(read.Data.Span.SequenceEqual(model.AsSpan((int)at, length)), $"read {at}+{length}");
                }
            }

            Assert.Equal(model, await ReadAllAsync(fs, handle, DiskSize));
            Assert.True(fs.OverlayBytes > 0 && fs.OverlayBytes < DiskSize + (1 << 20), $"overlay {fs.OverlayBytes}");
            await fs.CommitAsync(Esx, handle, 0, 0, Ct);
        }

        // Reopened: the same handle reads the same bytes.
        using (var fs = Open())
        {
            Assert.Equal(model, await ReadAllAsync(fs, handle, DiskSize));
            Assert.Equal((ulong)DiskSize, (await fs.GetAttributesAsync(Esx, handle, Ct)).Size);
        }
    }

    [Fact]
    public async Task After_a_crash_committed_writes_survive_and_a_torn_journal_record_is_dropped()
    {
        var fs = Open();
        var disk = fs.CreateBaseFile("vm01/disk0-flat.vmdk", "rp1/scsi0:0", DiskSize).ToArray();
        var vmx = (await fs.CreateAsync(Esx, fs.CreateDirectory("vm01"), "vm01.vmx", NfsCreateMode.Guarded, NfsSetAttributes.None, 0, Ct)).Handle.ToArray();
        await fs.WriteAsync(Esx, vmx, 0, Encoding.ASCII.GetBytes("displayName = \"vm01\"\n"), NfsStable.FileSync, Ct);
        var committed = Enumerable.Repeat((byte)0xC0, 4096).ToArray();
        await fs.WriteAsync(Esx, disk, 65536 + 100, committed, NfsStable.Unstable, Ct);
        await fs.CommitAsync(Esx, disk, 0, 0, Ct);
        var lost = Enumerable.Repeat((byte)0xEE, 4096).ToArray();
        await fs.WriteAsync(Esx, disk, 5 * 65536, lost, NfsStable.Unstable, Ct); // never committed
        var verifier = fs.WriteVerifier;

        // The process dies: no orderly close; a record was being appended.
        await File.AppendAllTextAsync(Path.Combine(_root, "namespace.log"), "@\0\0\0garbage", Ct);

        using var again = Open();
        Assert.NotEqual(verifier, again.WriteVerifier); // so ESXi sends its uncommitted writes again
        Assert.True((await again.ReadAsync(Esx, disk, 65536 + 100, 4096, Ct)).Data.Span.SequenceEqual(committed));
        Assert.True((await again.ReadAsync(Esx, disk, 5 * 65536, 4096, Ct)).Data.Span.SequenceEqual(_base.Data.AsSpan(5 * 65536, 4096)));
        Assert.Equal("displayName = \"vm01\"\n", Encoding.ASCII.GetString((await again.ReadAsync(Esx, vmx, 0, 100, Ct)).Data.Span));
        var created = await again.CreateAsync(Esx, again.RootHandle, "after-crash.log", NfsCreateMode.Guarded, NfsSetAttributes.None, 0, Ct);
        Assert.NotNull(created);
    }

    [Fact]
    public async Task The_namespace_survives_reopening_and_handles_are_signed()
    {
        byte[] root, vm, log, renamed;
        using (var fs = Open())
        {
            root = fs.RootHandle.ToArray();
            vm = (await fs.MakeDirectoryAsync(Esx, root, "vm01", NfsSetAttributes.None, Ct)).Handle.ToArray();
            log = (await fs.CreateAsync(Esx, vm, "vmware.log", NfsCreateMode.Unchecked, NfsSetAttributes.None, 0, Ct)).Handle.ToArray();
            await fs.WriteAsync(Esx, log, 0, "boot\n"u8.ToArray(), NfsStable.FileSync, Ct);
            var lck = (await fs.CreateAsync(Esx, vm, ".lck-0011", NfsCreateMode.Exclusive, NfsSetAttributes.None, 42, Ct)).Handle.ToArray();
            Assert.Equal(lck, (await fs.CreateAsync(Esx, vm, ".lck-0011", NfsCreateMode.Exclusive, NfsSetAttributes.None, 42, Ct)).Handle.ToArray()); // retransmission
            await Assert.ThrowsAsync<NfsException>(async () => await fs.CreateAsync(Esx, vm, ".lck-0011", NfsCreateMode.Exclusive, NfsSetAttributes.None, 43, Ct));
            await fs.RenameAsync(Esx, vm, "vmware.log", vm, "vmware-1.log", Ct);
            await fs.RemoveAsync(Esx, vm, ".lck-0011", Ct);
            renamed = (await fs.LookupAsync(Esx, vm, "vmware-1.log", Ct)).Handle.ToArray();
            Assert.Equal(log, renamed); // a rename keeps the file's handle
        }

        using (var fs = Open())
        {
            Assert.Equal(root, fs.RootHandle.ToArray());
            var listing = await fs.ReadDirectoryAsync(Esx, vm, 0, 0, plus: true, Ct);
            Assert.Equal([".", "..", "vmware-1.log"], listing.Entries.Select(e => e.Name));
            Assert.Equal("boot\n", Encoding.ASCII.GetString((await fs.ReadAsync(Esx, renamed, 0, 100, Ct)).Data.Span));

            var tampered = (byte[])log.Clone();
            tampered[^1] ^= 1;
            Assert.Equal(NfsStatus.BadHandle, (await Assert.ThrowsAsync<NfsException>(async () => await fs.GetAttributesAsync(Esx, tampered, Ct))).Status);
            await fs.RemoveAsync(Esx, vm, "vmware-1.log", Ct);
            Assert.Equal(NfsStatus.Stale, (await Assert.ThrowsAsync<NfsException>(async () => await fs.GetAttributesAsync(Esx, log, Ct))).Status);
            // The removed files' data went with them (vmware.log was file 3, the lock file 4).
            Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_root, "data")), f => Path.GetFileName(f) is "3.data" or "4.data");
        }

        // Another installation's key: not one of ours.
        using var other = InstantRecoveryFileSystem.Open(_root, ExportId, [.. Enumerable.Repeat((byte)9, 32)], _ => _base);
        Assert.Equal(NfsStatus.BadHandle, (await Assert.ThrowsAsync<NfsException>(async () => await other.GetAttributesAsync(Esx, vm, Ct))).Status);
    }

    [Fact]
    public async Task Writes_beyond_the_budget_get_nospc_and_a_shortened_disk_reads_zeros_when_grown_again()
    {
        using var fs = Open(new InstantRecoveryFsOptions { OverlayBudgetBytes = 4L * 65536, FreeSpaceReserveBytes = 0 });
        var disk = fs.CreateBaseFile("vm01/disk0-flat.vmdk", "rp1/scsi0:0", DiskSize).ToArray();
        await fs.WriteAsync(Esx, disk, 0, new byte[65536], NfsStable.Unstable, Ct);
        await fs.WriteAsync(Esx, disk, 65536, new byte[65536], NfsStable.Unstable, Ct);
        var full = await Assert.ThrowsAsync<NfsException>(async () => await fs.WriteAsync(Esx, disk, 9 * 65536, new byte[2 * 65536], NfsStable.Unstable, Ct));
        Assert.Equal(NfsStatus.NoSpc, full.Status);
        Assert.Contains("overlay of this instant recovery is full", full.Message, StringComparison.Ordinal);

        // Shortened to 1 MiB + 10 bytes, then grown: the base beyond no longer shows.
        await fs.SetAttributesAsync(Esx, disk, NfsSetAttributes.None with { Size = (1 << 20) + 10 }, null, Ct);
        await fs.SetAttributesAsync(Esx, disk, NfsSetAttributes.None with { Size = 3 << 20 }, null, Ct);
        var read = await fs.ReadAsync(Esx, disk, 1 << 20, 64, Ct);
        Assert.True(read.Data.Span[..10].SequenceEqual(_base.Data.AsSpan(1 << 20, 10)));
        Assert.True(read.Data.Span[10..].IndexOfAnyExcept((byte)0) < 0);
        Assert.True((await fs.ReadAsync(Esx, disk, 2 << 20, 4096, Ct)).Data.Span.IndexOfAnyExcept((byte)0) < 0);
        var stat = await fs.FsStatAsync(Esx, fs.RootHandle, Ct);
        Assert.Equal(4UL * 65536, stat.TotalBytes);
    }
}
