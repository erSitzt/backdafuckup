using System.Collections.Concurrent;
using System.Text;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Testing.Arrays;

/// <summary>
/// A stand-in for the OpenVmfs reader. A fake datastore on a LUN starts with <see cref="Magic"/>; its files are byte ranges
/// of the LUN registered by descriptor path, so what the tests read really comes from the array snapshot's LUN.
/// </summary>
public sealed class FakeVmfsReader : IVmfsReader
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("FAKEVMFS");

    /// <summary>A file of the datastore: where its data starts on the LUN, its size, the areas holding data, and whether a host had it open for writing.</summary>
    public sealed record FakeFile(long LunOffset, long Capacity, IReadOnlyList<DiskExtent> Allocated, bool InUse = false);

    public ConcurrentDictionary<string, FakeFile> Files { get; } = new(StringComparer.Ordinal);

    public string FormatVersion { get; set; } = "VMFS 6.82";

    public int VolumesOpened;

    public int DisksOpen;

    /// <summary>A LUN image of <paramref name="size"/> bytes with the datastore's magic at the start.</summary>
    public static byte[] NewLun(long size)
    {
        var lun = new byte[size];
        Magic.CopyTo(lun, 0);
        return lun;
    }

    public async Task<IVmfsVolume> OpenVolumeAsync(IDiskReader lun, string where, CancellationToken cancellationToken)
    {
        var header = new byte[Magic.Length];
        try
        {
            await lun.ReadAsync(0, header, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new VmfsRefusedException($"{where} cannot be read: {ex.Message}", VmfsRefusalKinds.Io, ex);
        }

        if (!header.AsSpan().SequenceEqual(Magic))
        {
            throw new VmfsRefusedException($"{where} holds no VMFS volume", VmfsRefusalKinds.Unsupported);
        }

        Interlocked.Increment(ref VolumesOpened);
        return new Volume(this, lun, FormatVersion);
    }

    private sealed class Volume(FakeVmfsReader owner, IDiskReader lun, string version) : IVmfsVolume
    {
        public string FormatVersion => version;

        public string Label => "ds-test";

        public Task<IVmfsDisk> OpenDiskAsync(string descriptorPath, CancellationToken cancellationToken)
        {
            if (!owner.Files.TryGetValue(descriptorPath, out var file))
            {
                throw new VmfsRefusedException($"{descriptorPath} does not exist on the datastore", VmfsRefusalKinds.NotFound);
            }

            if (file.InUse)
            {
                throw new VmfsRefusedException($"{descriptorPath} was open for writing on a host (lock mode exclusive)", VmfsRefusalKinds.InUse);
            }

            Interlocked.Increment(ref owner.DisksOpen);
            return Task.FromResult<IVmfsDisk>(new Disk(owner, lun, file));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Disk(FakeVmfsReader owner, IDiskReader lun, FakeFile file) : IVmfsDisk
    {
        private int _disposed;

        public long Capacity => file.Capacity;

        public string TransportName => "array-vmfs";

        public int MaxRequestBytes => 4 * 1024 * 1024;

        public int MaxConcurrentRequests => lun.MaxConcurrentRequests;

        public IReadOnlyList<DiskExtent> AllocatedExtents => file.Allocated;

        public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            for (var done = 0; done < destination.Length; done += lun.MaxRequestBytes)
            {
                var piece = destination.Slice(done, Math.Min(lun.MaxRequestBytes, destination.Length - done));
                await lun.ReadAsync(file.LunOffset + offset + done, piece, cancellationToken);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.DisksOpen);
            }

            return ValueTask.CompletedTask;
        }
    }
}
