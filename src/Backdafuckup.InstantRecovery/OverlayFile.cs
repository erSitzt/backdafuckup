using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.InstantRecovery;

/// <summary>A disk of a restore point as the base of an overlay file (read-only, random access).</summary>
public interface IBaseImage
{
    long Size { get; }

    /// <summary>Bytes of the image that hold data (for the "used" size of the file, so the disk shows as thin).</summary>
    long AllocatedBytes { get; }

    /// <summary>Fills <paramref name="destination"/> with the image's bytes at <paramref name="offset"/> (inside <see cref="Size"/>).</summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>
/// The data of one file of an instant recovery. A plain file is a sparse data file. A file backed by a restore point disk
/// reads every 64 KiB block from the backup until the block is written; the first write of a block copies the block's base
/// data, merges the write and sets the block's bit. Bits reach the map file only after the data (COMMIT or a stable
/// write), so a set bit always means the data file holds the block. Base data at or beyond <see cref="BaseLimit"/> (after
/// a file was shortened) reads as zeros.
/// </summary>
internal sealed partial class OverlayFile : IDisposable
{
    public const int BlockSize = 64 * 1024;
    private const int MapPage = 4096;
    private const int Stripes = 64;

    private readonly SafeFileHandle _data;
    private readonly SafeFileHandle? _map;
    private readonly IBaseImage? _base;
    private readonly SemaphoreSlim[] _stripes = [.. Enumerable.Range(0, Stripes).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly Lock _bitmapLock = new();
    private readonly HashSet<long> _dirtyPages = [];
    private byte[] _bitmap = [];
    private long _setBlocks;

    public OverlayFile(string dataPath, string? mapPath, IBaseImage? baseImage, long baseLimit)
    {
        _data = File.OpenHandle(dataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        MarkSparse(_data);
        _base = baseImage;
        BaseLimit = baseImage is null ? 0 : Math.Min(baseLimit, baseImage.Size);
        if (mapPath is not null)
        {
            _map = File.OpenHandle(mapPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var length = RandomAccess.GetLength(_map);
            _bitmap = new byte[length];
            RandomAccess.Read(_map, _bitmap, 0);
            _setBlocks = _bitmap.Sum(b => (long)BitOperations.PopCount(b));
        }
    }

    public bool IsBaseBacked => _base is not null;

    /// <summary>Base data below this offset is valid; above it the file reads zeros where no block was written.</summary>
    public long BaseLimit { get; private set; }

    /// <summary>The data file's length (it grows with writes; after a crash the file size is at least this).</summary>
    public long DataLength => RandomAccess.GetLength(_data);

    /// <summary>Bytes this file holds in the overlay (written blocks, or the plain data).</summary>
    public long OverlayBytes => IsBaseBacked ? Interlocked.Read(ref _setBlocks) * BlockSize : DataLength;

    /// <summary>Bytes that hold data: the base's allocated bytes (at most) plus what was written.</summary>
    public long UsedBytes => IsBaseBacked ? Math.Min(_base!.AllocatedBytes, BaseLimit) + OverlayBytes : DataLength;

    /// <summary>Reads up to <paramref name="count"/> bytes, not beyond <paramref name="fileSize"/>.</summary>
    public async ValueTask<byte[]> ReadAsync(long offset, int count, long fileSize, CancellationToken cancellationToken)
    {
        var length = (int)Math.Max(0, Math.Min(count, fileSize - offset));
        var result = new byte[length];
        if (length == 0)
        {
            return result;
        }

        if (!IsBaseBacked)
        {
            await ReadDataAsync(offset, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var done = 0;
        while (done < length)
        {
            var at = offset + done;
            var block = at / BlockSize;
            var piece = (int)Math.Min(length - done, ((block + 1) * BlockSize) - at);
            var target = result.AsMemory(done, piece);
            var stripe = _stripes[block % Stripes];
            await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsSet(block))
                {
                    await ReadDataAsync(at, target, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ReadBaseAsync(at, target, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                stripe.Release();
            }

            done += piece;
        }

        return result;
    }

    /// <summary>
    /// Writes <paramref name="data"/>; for a base-backed file, a block written for the first time gets its base data around
    /// the write (up to <paramref name="fileSize"/>). Returns the bytes newly taken in the overlay (for the budget).
    /// </summary>
    public async ValueTask<long> WriteAsync(long offset, ReadOnlyMemory<byte> data, long fileSize, CancellationToken cancellationToken)
    {
        if (!IsBaseBacked)
        {
            var before = DataLength;
            await RandomAccess.WriteAsync(_data, data, offset, cancellationToken).ConfigureAwait(false);
            return Math.Max(0, DataLength - before);
        }

        long taken = 0;
        var done = 0;
        var end = offset + data.Length;
        while (done < data.Length)
        {
            var at = offset + done;
            var block = at / BlockSize;
            var blockStart = block * BlockSize;
            var piece = (int)Math.Min(data.Length - done, blockStart + BlockSize - at);
            var stripe = _stripes[block % Stripes];
            await stripe.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsSet(block) || (at == blockStart && piece == BlockSize))
                {
                    await RandomAccess.WriteAsync(_data, data.Slice(done, piece), at, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // First write into this block: its base data around the write, up to the file's end.
                    var blockEnd = Math.Min(blockStart + BlockSize, Math.Max(fileSize, end));
                    var merged = new byte[blockEnd - blockStart];
                    await ReadBaseAsync(blockStart, merged.AsMemory(0, (int)Math.Min(merged.Length, Math.Max(0, fileSize - blockStart))), cancellationToken).ConfigureAwait(false);
                    data.Slice(done, piece).CopyTo(merged.AsMemory((int)(at - blockStart)));
                    await RandomAccess.WriteAsync(_data, merged, blockStart, cancellationToken).ConfigureAwait(false);
                }

                if (Set(block))
                {
                    taken += BlockSize;
                }
            }
            finally
            {
                stripe.Release();
            }

            done += piece;
        }

        return taken;
    }

    /// <summary>Data to stable storage, then the bits of the blocks written since (in that order).</summary>
    public void Flush()
    {
        RandomAccess.FlushToDisk(_data);
        if (_map is null)
        {
            return;
        }

        List<(long Page, byte[] Bytes)> pages;
        lock (_bitmapLock)
        {
            pages = [.. _dirtyPages.Select(p => (p, _bitmap.AsSpan((int)(p * MapPage), (int)Math.Min(MapPage, _bitmap.Length - (p * MapPage))).ToArray()))];
            _dirtyPages.Clear();
        }

        if (pages.Count == 0)
        {
            return;
        }

        foreach (var (page, bytes) in pages)
        {
            RandomAccess.Write(_map, bytes, page * MapPage);
        }

        RandomAccess.FlushToDisk(_map);
    }

    /// <summary>The file was shortened: data beyond goes, base data beyond is no longer valid, bits beyond are cleared.</summary>
    public void Truncate(long newSize)
    {
        RandomAccess.SetLength(_data, Math.Min(DataLength, newSize));
        if (!IsBaseBacked)
        {
            return;
        }

        BaseLimit = Math.Min(BaseLimit, newSize);
        lock (_bitmapLock)
        {
            var first = (newSize + BlockSize - 1) / BlockSize;
            for (var block = first; block < (long)_bitmap.Length * 8; block++)
            {
                if ((_bitmap[block / 8] & (1 << (int)(block % 8))) != 0)
                {
                    _bitmap[block / 8] &= (byte)~(1 << (int)(block % 8));
                    _dirtyPages.Add(block / 8 / MapPage);
                    Interlocked.Decrement(ref _setBlocks);
                }
            }
        }
    }

    /// <summary>Replay: the base limit as the journal's shortest size set it.</summary>
    public void LimitBase(long limit) => BaseLimit = Math.Min(BaseLimit, limit);

    private bool IsSet(long block)
    {
        lock (_bitmapLock)
        {
            return block / 8 < _bitmap.Length && (_bitmap[block / 8] & (1 << (int)(block % 8))) != 0;
        }
    }

    /// <summary>Sets a block's bit; true if it was not set.</summary>
    private bool Set(long block)
    {
        lock (_bitmapLock)
        {
            var index = block / 8;
            if (index >= _bitmap.Length)
            {
                Array.Resize(ref _bitmap, (int)Math.Max(index + 1, _bitmap.Length * 2L));
            }

            var mask = (byte)(1 << (int)(block % 8));
            if ((_bitmap[index] & mask) != 0)
            {
                return false;
            }

            _bitmap[index] |= mask;
            _dirtyPages.Add(index / MapPage);
            Interlocked.Increment(ref _setBlocks);
            return true;
        }
    }

    private async ValueTask ReadDataAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var n = await RandomAccess.ReadAsync(_data, destination[read..], offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                destination.Span[read..].Clear(); // beyond the data file: a hole
                return;
            }

            read += n;
        }
    }

    private async ValueTask ReadBaseAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var valid = (int)Math.Max(0, Math.Min(destination.Length, BaseLimit - offset));
        if (valid > 0)
        {
            await _base!.ReadAsync(offset, destination[..valid], cancellationToken).ConfigureAwait(false);
        }

        destination.Span[valid..].Clear();
    }

    public void Dispose()
    {
        Flush();
        _data.Dispose();
        _map?.Dispose();
        foreach (var stripe in _stripes)
        {
            stripe.Dispose();
        }
    }

    /// <summary>NTFS allocates the gaps of a file unless it is sparse; a 100 GB disk written at random places would fill the volume.</summary>
    private static void MarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const uint fsctlSetSparse = 0x000900C4;
        if (!NativeMethods.DeviceIoControl(handle, fsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException($"The overlay file cannot be made sparse (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
    }
}
