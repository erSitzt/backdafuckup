using System.Buffers;
using System.Buffers.Binary;
using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Xen;

/// <summary>
/// Reads a dynamic VHD from a forward-only stream, as produced by XAPI's <c>export_raw_vdi?format=vhd</c>.
/// Implemented from the public "Virtual Hard Disk Image Format Specification" (Microsoft, v1.0): footer copy,
/// dynamic disk header, block allocation table (BAT), then the allocated blocks (sector bitmap + data) in file order.
/// Unallocated blocks are zeros and are not passed to the sink.
/// </summary>
/// <remarks>
/// The data of an allocated block is passed on as a whole. Sectors that the block's sector bitmap marks as unused
/// must contain zeros; anything else is rejected instead of guessed, so a misread stream fails loudly.
/// </remarks>
public sealed class VhdStreamReader
{
    public const int SectorSize = 512;
    private const uint Unallocated = 0xFFFFFFFF;
    private static readonly byte[] FooterCookie = "conectix"u8.ToArray();
    private static readonly byte[] HeaderCookie = "cxsparse"u8.ToArray();

    private readonly Stream _stream;
    private readonly (long FileOffset, long Block)[] _blocks;
    private long _position;

    private VhdStreamReader(Stream stream, long position, long currentSize, int blockSize, long maxTableEntries, (long FileOffset, long Block)[] blocks)
    {
        _stream = stream;
        _position = position;
        CurrentSize = currentSize;
        BlockSize = blockSize;
        MaxTableEntries = maxTableEntries;
        _blocks = blocks;
    }

    /// <summary>Virtual size of the disk ("current size" in the footer).</summary>
    public long CurrentSize { get; }

    public int BlockSize { get; }

    public long MaxTableEntries { get; }

    public int AllocatedBlocks => _blocks.Length;

    /// <summary>Bytes of block data that follow in the stream (allocated blocks × block size).</summary>
    public long AllocatedBytes => _blocks.LongLength * BlockSize;

    /// <summary>True once <see cref="StreamAsync"/> found the trailing footer at the end of the stream.</summary>
    public bool EndedWithFooter { get; private set; }

    /// <summary>Reads footer, header and BAT. The stream is positioned at the first block afterwards.</summary>
    public static async Task<VhdStreamReader> OpenAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long position = 0;

        var footer = new byte[SectorSize];
        position += await ReadExactAsync(stream, footer, "footer", cancellationToken).ConfigureAwait(false);
        if (!footer.AsSpan(0, 8).SequenceEqual(FooterCookie))
        {
            throw Invalid("the stream does not start with a VHD footer (cookie 'conectix' missing)");
        }

        VerifyChecksum(footer, 64, "footer");
        var dataOffset = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(16));
        var currentSize = BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(48));
        var diskType = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
        if (diskType != 3)
        {
            var name = diskType switch { 2 => "fixed", 4 => "differencing", _ => "unknown" };
            throw Invalid($"disk type {diskType} ({name}) is not supported, expected a dynamic disk (3)");
        }

        if (dataOffset < position || currentSize < 0)
        {
            throw Invalid($"invalid footer (data offset {dataOffset}, current size {currentSize})");
        }

        position += await SkipAsync(stream, dataOffset - position, cancellationToken).ConfigureAwait(false);
        var header = new byte[1024];
        position += await ReadExactAsync(stream, header, "dynamic disk header", cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 8).SequenceEqual(HeaderCookie))
        {
            throw Invalid("dynamic disk header not found (cookie 'cxsparse' missing)");
        }

        VerifyChecksum(header, 36, "dynamic disk header");
        var tableOffset = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16));
        var maxTableEntries = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32));
        if (blockSize == 0 || blockSize % SectorSize != 0 || blockSize > 256u * 1024 * 1024)
        {
            throw Invalid($"unsupported block size {blockSize}");
        }

        if ((long)maxTableEntries * blockSize < currentSize)
        {
            throw Invalid($"the BAT ({maxTableEntries} entries of {blockSize} bytes) does not cover the disk size {currentSize}");
        }

        if (tableOffset < position)
        {
            throw Invalid($"the BAT at offset {tableOffset} lies before the current stream position {position}");
        }

        position += await SkipAsync(stream, tableOffset - position, cancellationToken).ConfigureAwait(false);
        var batBytes = RoundUp(maxTableEntries * 4L, SectorSize);
        if (batBytes > 64L * 1024 * 1024)
        {
            throw Invalid($"BAT too large ({maxTableEntries} entries)");
        }

        var bat = new byte[batBytes];
        position += await ReadExactAsync(stream, bat, "block allocation table", cancellationToken).ConfigureAwait(false);

        var blocks = new List<(long FileOffset, long Block)>();
        for (long i = 0; i < maxTableEntries; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32BigEndian(bat.AsSpan((int)(i * 4)));
            if (sector != Unallocated)
            {
                blocks.Add((sector * (long)SectorSize, i));
            }
        }

        blocks.Sort((a, b) => a.FileOffset.CompareTo(b.FileOffset));
        for (var i = 1; i < blocks.Count; i++)
        {
            if (blocks[i].Block <= blocks[i - 1].Block)
            {
                throw Invalid($"the blocks are not stored in disk order (block {blocks[i].Block} follows block {blocks[i - 1].Block}); a stream cannot be read like this");
            }
        }

        return new VhdStreamReader(stream, position, currentSize, (int)blockSize, maxTableEntries, blocks.ToArray());
    }

    /// <summary>Streams the allocated blocks (clipped to <paramref name="capacity"/>) to <paramref name="sink"/> and reads the stream to its end.</summary>
    public async Task StreamAsync(long capacity, DiskDataSink sink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var sectorsPerBlock = BlockSize / SectorSize;
        var bitmapBytes = (int)RoundUp((sectorsPerBlock + 7) / 8, SectorSize);
        var bitmap = new byte[bitmapBytes];
        var data = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            foreach (var (fileOffset, block) in _blocks)
            {
                if (fileOffset < _position)
                {
                    throw Invalid($"block {block} at offset {fileOffset} overlaps the previous data (stream position {_position})");
                }

                _position += await SkipAsync(_stream, fileOffset - _position, cancellationToken).ConfigureAwait(false);
                _position += await ReadExactAsync(_stream, bitmap, $"sector bitmap of block {block}", cancellationToken).ConfigureAwait(false);
                _position += await ReadExactAsync(_stream, data.AsMemory(0, BlockSize), $"data of block {block}", cancellationToken).ConfigureAwait(false);
                VerifyUnusedSectorsAreZero(bitmap, data.AsSpan(0, BlockSize), block);

                // The last block may extend beyond the end of the disk; that part must be empty.
                var virtualOffset = block * BlockSize;
                var diskEnd = Math.Min(capacity, CurrentSize);
                var usable = (int)Math.Clamp(diskEnd - virtualOffset, 0, BlockSize);
                if (usable < BlockSize && data.AsSpan(usable, BlockSize - usable).ContainsAnyExcept((byte)0))
                {
                    throw Invalid($"block {block} contains data beyond the end of the disk ({diskEnd} bytes)");
                }

                if (usable > 0)
                {
                    await sink(virtualOffset, data.AsMemory(0, usable), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }

        // The rest of the stream should be the footer copy at the end of the file.
        var tail = new byte[SectorSize];
        var tailLength = 0;
        var scratch = new byte[64 * 1024];
        while (true)
        {
            var read = await _stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            _position += read;
            if (read >= SectorSize)
            {
                scratch.AsSpan(read - SectorSize, SectorSize).CopyTo(tail);
                tailLength = SectorSize;
            }
            else
            {
                var keep = Math.Min(SectorSize - read, tailLength);
                tail.AsSpan(tailLength - keep, keep).CopyTo(tail);
                scratch.AsSpan(0, read).CopyTo(tail.AsSpan(keep));
                tailLength = keep + read;
            }
        }

        EndedWithFooter = tailLength == SectorSize && tail.AsSpan(0, 8).SequenceEqual(FooterCookie);
    }

    private void VerifyUnusedSectorsAreZero(byte[] bitmap, ReadOnlySpan<byte> data, long block)
    {
        var sectors = BlockSize / SectorSize;
        for (var sector = 0; sector < sectors; sector++)
        {
            var bits = bitmap[sector >> 3];
            if (bits == 0xFF && (sector & 7) == 0)
            {
                sector += 7;
                continue;
            }

            // Most significant bit first: bit 7 of byte 0 describes sector 0.
            var used = (bits & (0x80 >> (sector & 7))) != 0;
            if (!used && data.Slice(sector * SectorSize, SectorSize).ContainsAnyExcept((byte)0))
            {
                throw Invalid(
                    $"sector {sector} of block {block} contains data but is marked unused in the sector bitmap; refusing to guess. " +
                    "Set Xen:HttpExportFormat to Raw to read the disk unprocessed");
            }
        }
    }

    private static void VerifyChecksum(byte[] structure, int checksumOffset, string what)
    {
        uint sum = 0;
        for (var i = 0; i < structure.Length; i++)
        {
            if (i < checksumOffset || i >= checksumOffset + 4)
            {
                sum += structure[i];
            }
        }

        var expected = BinaryPrimitives.ReadUInt32BigEndian(structure.AsSpan(checksumOffset));
        if (~sum != expected)
        {
            throw Invalid($"{what} checksum mismatch (stored 0x{expected:x8}, computed 0x{~sum:x8})");
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, string what, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.Length;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"VHD stream: the stream ended while reading the {what} (truncated export?).", ex);
        }
    }

    private static async Task<long> SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
        {
            return 0;
        }

        var scratch = ArrayPool<byte>.Shared.Rent((int)Math.Min(count, 1024 * 1024));
        try
        {
            var remaining = count;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(remaining, scratch.Length)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException($"VHD stream: the stream ended while skipping {count} bytes of padding (truncated export?).");
                }

                remaining -= read;
            }

            return count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private static long RoundUp(long value, int multiple) => (value + multiple - 1) / multiple * multiple;

    private static InvalidDataException Invalid(string message) => new($"VHD stream: {message}.");
}
