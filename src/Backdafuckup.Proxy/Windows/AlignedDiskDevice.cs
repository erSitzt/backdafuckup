using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Proxy.Windows;

/// <summary>Reads <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>; returns how many it read (0 at the end).</summary>
public delegate int SectorRead(Span<byte> buffer, long offset);

/// <summary>
/// A disk opened without the cache, served read-only: every read goes through a sector-aligned buffer and covers whole
/// sectors (Windows refuses anything else with error 87), so NBD requests of any offset and length are answered.
/// </summary>
public sealed unsafe class AlignedDiskDevice : IOpenDevice
{
    /// <summary>The largest single read; larger requests are read in pieces.</summary>
    private const int Piece = 4 << 20;

    private readonly SafeFileHandle? _handle;
    private readonly SectorRead _read;
    private readonly int _sectorSize;

    public AlignedDiskDevice(ProxyDevice device, SafeFileHandle handle, int sectorSize)
        : this(device, sectorSize, (buffer, offset) => RandomAccess.Read(handle, buffer, offset)) => _handle = handle;

    /// <summary>Over any reader (tests check that every read it gets is aligned).</summary>
    public AlignedDiskDevice(ProxyDevice device, int sectorSize, SectorRead read)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), sectorSize, "A sector is a power of two of at least 512 bytes.");
        }

        Device = device;
        _sectorSize = sectorSize;
        _read = read;
    }

    public ProxyDevice Device { get; }

    public long Size => Device.SizeBytes;

    public bool ReadOnly => true;

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Size)
        {
            throw new IOException($"Read of {destination.Length} bytes at {offset} is beyond {Device.Path} ({Size} bytes).");
        }

        // The handle is synchronous: the read runs on a pool thread, as RandomAccess would.
        return new ValueTask(Task.Run(() => Read(offset, destination), cancellationToken));
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        throw new IOException($"{Device.Path} is opened read-only.");

    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask WriteZeroesAsync(long offset, long length, CancellationToken cancellationToken) =>
        throw new IOException($"{Device.Path} is opened read-only.");

    public void Dispose() => _handle?.Dispose();

    private void Read(long offset, Memory<byte> destination)
    {
        var sector = (long)_sectorSize;
        var bounce = (byte*)NativeMemory.AlignedAlloc(Piece, 4096);
        try
        {
            var done = 0;
            while (done < destination.Length)
            {
                var position = offset + done;
                var start = position / sector * sector;
                var skip = (int)(position - start);
                var want = Math.Min(destination.Length - done, Piece - skip);
                var length = (int)Math.Min(((skip + want + sector - 1) / sector) * sector, Size - start);
                var span = new Span<byte>(bounce, length);
                var read = 0;
                while (read < length)
                {
                    var n = _read(span[read..], start + read);
                    if (n == 0)
                    {
                        throw new IOException($"{Device.Path} ended at {start + read} bytes, before its size of {Size}.");
                    }

                    read += n;
                }

                var take = Math.Min(want, length - skip);
                span.Slice(skip, take).CopyTo(destination.Span[done..]);
                done += take;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(bounce);
        }
    }
}
