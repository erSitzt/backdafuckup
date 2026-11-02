using System.Buffers;
using System.Buffers.Binary;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Xen;

/// <summary>
/// Writes a disk image as a dynamic VHD for XAPI's <c>import_raw_vdi?format=vhd</c>. Only the 2 MiB blocks that contain
/// data are sent, so empty regions cost neither network transfer nor writes on the host.
/// Implemented from the public "Virtual Hard Disk Image Format Specification" (Microsoft, v1.0). The layout is a footer
/// copy, the dynamic disk header, the block allocation table (BAT), the allocated blocks (sector bitmap + data) in disk
/// order, and the footer.
/// </summary>
/// <remarks>
/// Which blocks are allocated is decided up front from the image's data extents, because the BAT precedes the data.
/// A block outside these extents must read as zeros. If it does not, writing stops with an error rather than dropping data.
/// </remarks>
public sealed class VhdImageWriter
{
    public const int BlockSize = 2 * 1024 * 1024;
    private const int Sector = 512;
    private const int SectorsPerBlock = BlockSize / Sector;
    private const int BitmapSize = SectorsPerBlock / 8; // 512 bytes: already a whole sector
    private const int HeaderOffset = Sector;
    private const int BatOffset = HeaderOffset + 1024;
    private const uint Unallocated = 0xFFFFFFFF;
    private static readonly DateTimeOffset VhdEpoch = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly bool[] _allocated;
    private readonly byte[] _footer;
    private readonly byte[] _header;
    private readonly byte[] _bat;
    private long _written;

    private VhdImageWriter(long capacity, bool[] allocated, byte[] footer, byte[] header, byte[] bat)
    {
        Capacity = capacity;
        _allocated = allocated;
        _footer = footer;
        _header = header;
        _bat = bat;
        AllocatedBlocks = allocated.Count(a => a);
        Length = Sector + 1024 + bat.Length + ((long)AllocatedBlocks * (BitmapSize + BlockSize)) + Sector;
    }

    /// <summary>Virtual size of the disk.</summary>
    public long Capacity { get; }

    public int BlockCount => _allocated.Length;

    public int AllocatedBlocks { get; }

    /// <summary>Total length of the VHD stream in bytes.</summary>
    public long Length { get; }

    /// <summary>Bytes of the VHD stream written so far (for transfer statistics while <see cref="WriteAsync"/> runs).</summary>
    public long BytesWritten => Interlocked.Read(ref _written);

    /// <summary>Plans the VHD: a block is allocated if any of its bytes lies in <paramref name="dataExtents"/>.</summary>
    public static VhdImageWriter Create(long capacity, IReadOnlyList<DiskExtent> dataExtents, Guid? uniqueId = null, DateTimeOffset? timestamp = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentNullException.ThrowIfNull(dataExtents);
        if (capacity % Sector != 0)
        {
            throw new ArgumentException($"The disk size {capacity} is not a multiple of {Sector} bytes.", nameof(capacity));
        }

        var blockCount = checked((int)((capacity + BlockSize - 1) / BlockSize));
        var allocated = new bool[blockCount];
        foreach (var extent in dataExtents)
        {
            if (extent.Length <= 0)
            {
                continue;
            }

            if (extent.Offset < 0 || extent.End > capacity)
            {
                throw new ArgumentException($"Data extent {extent} lies outside the disk (0..{capacity}).", nameof(dataExtents));
            }

            for (var block = extent.Offset / BlockSize; block <= (extent.End - 1) / BlockSize; block++)
            {
                allocated[block] = true;
            }
        }

        var bat = new byte[(((blockCount * 4) + Sector - 1) / Sector) * Sector];
        bat.AsSpan().Fill(0xFF); // unused entries and padding: 0xFFFFFFFF
        var nextSector = (long)(BatOffset + bat.Length) / Sector;
        for (var block = 0; block < blockCount; block++)
        {
            if (allocated[block])
            {
                BinaryPrimitives.WriteUInt32BigEndian(bat.AsSpan(block * 4), checked((uint)nextSector));
                nextSector += (BitmapSize + BlockSize) / Sector;
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bat.AsSpan(block * 4), Unallocated);
            }
        }

        var seconds = (uint)Math.Max(0, ((timestamp ?? DateTimeOffset.UtcNow) - VhdEpoch).TotalSeconds);
        return new VhdImageWriter(
            capacity, allocated, Footer(capacity, seconds, uniqueId ?? Guid.NewGuid()), DynamicHeader(blockCount), bat);
    }

    /// <summary>Is the block that contains this disk offset sent?</summary>
    public bool IsAllocated(long offset) => _allocated[offset / BlockSize];

    /// <summary>
    /// Writes the VHD to <paramref name="output"/>, reading the disk image sequentially from <paramref name="image"/>
    /// (exactly <see cref="Capacity"/> bytes). <paramref name="processed"/> receives the image bytes done so far.
    /// </summary>
    public async Task WriteAsync(Stream output, Stream image, IProgress<long>? processed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(image);
        Interlocked.Exchange(ref _written, 0);
        await WriteAsync(output, _footer, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, _header, cancellationToken).ConfigureAwait(false);
        await WriteAsync(output, _bat, cancellationToken).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(BitmapSize + BlockSize);
        try
        {
            for (var block = 0; block < _allocated.Length; block++)
            {
                var start = (long)block * BlockSize;
                var length = (int)Math.Min(BlockSize, Capacity - start);
                var data = buffer.AsMemory(BitmapSize, length);
                var read = await image.ReadAtLeastAsync(data, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (read < length)
                {
                    throw new EndOfStreamException($"The disk image ended at byte {start + read} of {Capacity}.");
                }

                if (_allocated[block])
                {
                    FillBitmap(buffer.AsSpan(0, BitmapSize), length / Sector);
                    buffer.AsSpan(BitmapSize + length, BlockSize - length).Clear(); // pad a partial last block
                    await WriteAsync(output, buffer.AsMemory(0, BitmapSize + BlockSize), cancellationToken).ConfigureAwait(false);
                }
                else if (data.Span.IndexOfAnyExcept((byte)0) is var nonZero and >= 0)
                {
                    throw new InvalidDataException(
                        $"The disk image has data at byte {start + nonZero} outside its data extents; the VHD would lose it. Restore with Xen:HttpImportFormat=Raw.");
                }

                processed?.Report(start + length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (await image.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException($"The disk image is longer than {Capacity} bytes.");
        }

        await WriteAsync(output, _footer, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteAsync(Stream output, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _written, data.Length);
    }

    /// <summary>Marks the first <paramref name="sectors"/> sectors as present, most significant bit first (VHD spec).</summary>
    private static void FillBitmap(Span<byte> bitmap, int sectors)
    {
        bitmap.Clear();
        bitmap[..(sectors / 8)].Fill(0xFF);
        if (sectors % 8 != 0)
        {
            bitmap[sectors / 8] = (byte)(0xFF << (8 - (sectors % 8)));
        }
    }

    private static byte[] Footer(long capacity, uint timestamp, Guid uniqueId)
    {
        var footer = new byte[Sector];
        var span = footer.AsSpan();
        "conectix"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0x00000002); // features: reserved bit, always set
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], 0x00010000); // file format version 1.0
        BinaryPrimitives.WriteUInt64BigEndian(span[16..], HeaderOffset); // data offset: the dynamic header
        BinaryPrimitives.WriteUInt32BigEndian(span[24..], timestamp);
        "bdfu"u8.CopyTo(span[28..]); // creator application
        BinaryPrimitives.WriteUInt32BigEndian(span[32..], 0x00010000); // creator version
        BinaryPrimitives.WriteUInt32BigEndian(span[36..], 0x5769326B); // creator host OS "Wi2k"
        BinaryPrimitives.WriteUInt64BigEndian(span[40..], (ulong)capacity); // original size
        BinaryPrimitives.WriteUInt64BigEndian(span[48..], (ulong)capacity); // current size
        var (cylinders, heads, sectorsPerTrack) = Geometry(capacity);
        BinaryPrimitives.WriteUInt16BigEndian(span[56..], cylinders);
        span[58] = heads;
        span[59] = sectorsPerTrack;
        BinaryPrimitives.WriteUInt32BigEndian(span[60..], 3); // disk type: dynamic
        uniqueId.TryWriteBytes(span[68..84], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(span[64..], Checksum(span));
        return footer;
    }

    private static byte[] DynamicHeader(int blockCount)
    {
        var header = new byte[1024];
        var span = header.AsSpan();
        "cxsparse"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt64BigEndian(span[8..], ulong.MaxValue); // data offset: unused
        BinaryPrimitives.WriteUInt64BigEndian(span[16..], BatOffset); // table offset
        BinaryPrimitives.WriteUInt32BigEndian(span[24..], 0x00010000); // header version
        BinaryPrimitives.WriteUInt32BigEndian(span[28..], (uint)blockCount); // max table entries
        BinaryPrimitives.WriteUInt32BigEndian(span[32..], BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(span[36..], Checksum(span));
        return header;
    }

    /// <summary>One's complement of the byte sum, with the checksum field itself counted as zero.</summary>
    private static uint Checksum(ReadOnlySpan<byte> structure)
    {
        var checksumField = structure.Length == Sector ? 64 : 36;
        uint sum = 0;
        for (var i = 0; i < structure.Length; i++)
        {
            if (i < checksumField || i >= checksumField + 4)
            {
                sum += structure[i];
            }
        }

        return ~sum;
    }

    /// <summary>CHS geometry as defined in appendix "CHS Calculation" of the VHD specification.</summary>
    private static (ushort Cylinders, byte Heads, byte SectorsPerTrack) Geometry(long capacity)
    {
        var totalSectors = Math.Min(capacity / Sector, 65535L * 16 * 255);
        long sectorsPerTrack;
        long heads;
        long cylinderTimesHeads;
        if (totalSectors >= 65535L * 16 * 63)
        {
            sectorsPerTrack = 255;
            heads = 16;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;
        }
        else
        {
            sectorsPerTrack = 17;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;
            heads = Math.Max(4, (cylinderTimesHeads + 1023) / 1024);
            if (cylinderTimesHeads >= heads * 1024 || heads > 16)
            {
                sectorsPerTrack = 31;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }

            if (cylinderTimesHeads >= heads * 1024)
            {
                sectorsPerTrack = 63;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }
        }

        return ((ushort)(cylinderTimesHeads / heads), (byte)heads, (byte)sectorsPerTrack);
    }
}
