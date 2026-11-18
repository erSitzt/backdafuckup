using System.Buffers.Binary;
using System.Collections.Concurrent;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Lvhd;

/// <summary>The VHD of one chain member is not usable; the message says why.</summary>
public sealed class VhdFormatException(string message) : Exception(message);

/// <summary>What the footer and dynamic header of one VHD say.</summary>
/// <param name="EndOffset">Where the VHD's data ends in its container (the last block's end, or the BAT's end).</param>
public sealed record VhdInfo(
    int DiskType, Guid UniqueId, long CurrentSize, int BlockSize, long TableOffset, int MaxTableEntries, Guid ParentUniqueId, string? ParentName, long EndOffset, int AllocatedBlocks);

/// <summary>
/// Random reads through a chain of VHDs (leaf first, then its parents down to a dynamic base), written from the public
/// "Virtual Hard Disk Image Format Specification" (Microsoft): the footer copy at the start, the dynamic disk header with
/// the block allocation table (BAT), and per block a sector bitmap before the data. A sector comes from the first member,
/// from the leaf towards the base, whose block is allocated and whose bitmap marks it; otherwise it reads as zeros.
/// </summary>
public sealed class VhdChainReader
{
    public const int SectorSize = 512;
    private const uint Unallocated = 0xFFFFFFFF;
    private static readonly byte[] FooterCookie = "conectix"u8.ToArray();
    private static readonly byte[] HeaderCookie = "cxsparse"u8.ToArray();

    private readonly Member[] _members;
    private readonly ConcurrentDictionary<(int Member, long Block), byte[]> _bitmaps = new();

    private VhdChainReader(Member[] members, IReadOnlyList<DiskExtent> allocated)
    {
        _members = members;
        AllocatedExtents = allocated;
    }

    /// <summary>The disk's size (the leaf's current size).</summary>
    public long Capacity => _members[0].Info.CurrentSize;

    public int BlockSize => _members[0].Info.BlockSize;

    public IReadOnlyList<VhdInfo> Members => [.. _members.Select(m => m.Info)];

    /// <summary>The blocks allocated in any member (merged): the regions a sparse full backup reads.</summary>
    public IReadOnlyList<DiskExtent> AllocatedExtents { get; }

    /// <summary>
    /// Opens the chain and checks it: every member a dynamic or differencing VHD with valid checksums and the same block
    /// size, each member's parent id equal to the next member's own id, the last one a dynamic disk without parent.
    /// </summary>
    public static async Task<VhdChainReader> OpenAsync(IReadOnlyList<IBlockSource> chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
        {
            throw new ArgumentException("A VHD chain needs at least one member.", nameof(chain));
        }

        var members = new Member[chain.Count];
        for (var i = 0; i < chain.Count; i++)
        {
            members[i] = await ReadMemberAsync(chain[i], i, cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < members.Length; i++)
        {
            var info = members[i].Info;
            var last = i == members.Length - 1;
            if (last && info.DiskType != 3)
            {
                throw new VhdFormatException($"Member {i} (the base) is a {Type(info.DiskType)} disk; the chain must end with a dynamic disk.");
            }

            if (!last && info.DiskType != 4)
            {
                throw new VhdFormatException($"Member {i} is a {Type(info.DiskType)} disk but has a parent in the chain; only differencing disks have one.");
            }

            if (!last && info.ParentUniqueId != members[i + 1].Info.UniqueId)
            {
                throw new VhdFormatException($"Member {i} names parent {info.ParentUniqueId}, but the next member is {members[i + 1].Info.UniqueId}: the chain is not what was expected.");
            }

            if (info.BlockSize != members[0].Info.BlockSize)
            {
                throw new VhdFormatException($"Member {i} has blocks of {info.BlockSize} bytes, the leaf {members[0].Info.BlockSize}.");
            }

            if (info.EndOffset > chain[i].Size)
            {
                throw new VhdFormatException($"Member {i} ends at {info.EndOffset}, beyond its container of {chain[i].Size} bytes.");
            }
        }

        var blockSize = (long)members[0].Info.BlockSize;
        var blocks = new SortedSet<long>();
        foreach (var member in members)
        {
            for (var b = 0; b < member.Bat.Length; b++)
            {
                if (member.Bat[b] != Unallocated && b * blockSize < members[0].Info.CurrentSize)
                {
                    blocks.Add(b);
                }
            }
        }

        var extents = ExtentMath.Normalize(blocks.Select(b => new DiskExtent(b * blockSize, Math.Min(blockSize, members[0].Info.CurrentSize - (b * blockSize)))).ToList());
        return new VhdChainReader(members, extents);
    }

    /// <summary>Reads the chain's view of the disk; regions no member holds are zeros.</summary>
    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"Read {offset}+{destination.Length} is outside the disk ({Capacity} bytes).");
        }

        if (offset % SectorSize != 0 || destination.Length % SectorSize != 0)
        {
            throw new ArgumentException("Reads are whole sectors.", nameof(offset));
        }

        var done = 0;
        while (done < destination.Length)
        {
            var at = offset + done;
            var block = at / BlockSize;
            var sector = (int)((at % BlockSize) / SectorSize);
            var sectorsInBlock = BlockSize / SectorSize;

            // A run of sectors from the same member (or none).
            var owner = await OwnerAsync(block, sector, cancellationToken).ConfigureAwait(false);
            var run = 1;
            while (done + ((run + 1) * SectorSize) <= destination.Length && sector + run < sectorsInBlock
                   && await OwnerAsync(block, sector + run, cancellationToken).ConfigureAwait(false) == owner)
            {
                run++;
            }

            var target = destination.Slice(done, run * SectorSize);
            if (owner < 0)
            {
                target.Span.Clear();
            }
            else
            {
                var member = _members[owner];
                var dataStart = ((long)member.Bat[block] * SectorSize) + member.BitmapBytes;
                await member.Source.ReadAsync(dataStart + ((long)sector * SectorSize), target, cancellationToken).ConfigureAwait(false);
            }

            done += run * SectorSize;
        }
    }

    /// <summary>The first member (leaf first) holding this sector, or -1.</summary>
    private async ValueTask<int> OwnerAsync(long block, int sector, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _members.Length; i++)
        {
            var member = _members[i];
            if (block >= member.Bat.Length || member.Bat[block] == Unallocated)
            {
                continue;
            }

            if (!_bitmaps.TryGetValue((i, block), out var bitmap))
            {
                bitmap = new byte[member.BitmapBytes];
                await member.Source.ReadAsync((long)member.Bat[block] * SectorSize, bitmap, cancellationToken).ConfigureAwait(false);
                _bitmaps[(i, block)] = bitmap;
            }

            if ((bitmap[sector / 8] & (0x80 >> (sector % 8))) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task<Member> ReadMemberAsync(IBlockSource source, int index, CancellationToken cancellationToken)
    {
        var footer = new byte[SectorSize];
        await source.ReadAsync(0, footer, cancellationToken).ConfigureAwait(false);
        var (type, uniqueId, currentSize, dataOffset) = ParseFooter(footer, index);
        var header = new byte[1024];
        await source.ReadAsync(dataOffset, header, cancellationToken).ConfigureAwait(false);
        var (tableOffset, maxEntries, blockSize, parentId, parentName) = ParseHeader(header, index);
        if (maxEntries < (currentSize + blockSize - 1) / blockSize)
        {
            throw new VhdFormatException($"Member {index}: {maxEntries} BAT entries do not cover {currentSize} bytes in blocks of {blockSize}.");
        }

        var batBytes = new byte[((maxEntries * 4) + SectorSize - 1) / SectorSize * SectorSize];
        await source.ReadAsync(tableOffset, batBytes, cancellationToken).ConfigureAwait(false);
        var bat = new uint[maxEntries];
        for (var i = 0; i < maxEntries; i++)
        {
            bat[i] = BinaryPrimitives.ReadUInt32BigEndian(batBytes.AsSpan(i * 4));
        }

        var bitmapBytes = ((blockSize / SectorSize / 8) + SectorSize - 1) / SectorSize * SectorSize;
        var end = tableOffset + batBytes.Length;
        var allocated = 0;
        foreach (var entry in bat.Where(e => e != Unallocated))
        {
            allocated++;
            end = Math.Max(end, ((long)entry * SectorSize) + bitmapBytes + blockSize);
        }

        return new Member(source, new VhdInfo(type, uniqueId, currentSize, blockSize, tableOffset, maxEntries, parentId, parentName, end, allocated), bat, bitmapBytes);
    }

    private static (int Type, Guid UniqueId, long CurrentSize, long DataOffset) ParseFooter(byte[] footer, int index)
    {
        if (!footer.AsSpan(0, 8).SequenceEqual(FooterCookie))
        {
            throw new VhdFormatException($"Member {index} does not start with a VHD footer copy (cookie 'conectix' missing).");
        }

        Checksum(footer, 64, $"member {index}'s footer");
        var type = (int)BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
        if (type is not (3 or 4))
        {
            throw new VhdFormatException($"Member {index} is a {Type(type)} disk; only dynamic and differencing VHDs are read.");
        }

        return (type, new Guid(footer.AsSpan(68, 16), bigEndian: true), BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(48)), BinaryPrimitives.ReadInt64BigEndian(footer.AsSpan(16)));
    }

    private static (long TableOffset, int MaxEntries, int BlockSize, Guid ParentId, string? ParentName) ParseHeader(byte[] header, int index)
    {
        if (!header.AsSpan(0, 8).SequenceEqual(HeaderCookie))
        {
            throw new VhdFormatException($"Member {index} has no dynamic disk header (cookie 'cxsparse' missing).");
        }

        Checksum(header, 36, $"member {index}'s dynamic disk header");
        var blockSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32));
        if (blockSize <= 0 || blockSize % SectorSize != 0 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new VhdFormatException($"Member {index} has an invalid block size {blockSize}.");
        }

        var name = System.Text.Encoding.BigEndianUnicode.GetString(header.AsSpan(64, 512)).TrimEnd('\0');
        return (BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(28)), blockSize,
            new Guid(header.AsSpan(40, 16), bigEndian: true), name.Length == 0 ? null : name);
    }

    /// <summary>The ones' complement of the byte sum without the checksum field.</summary>
    private static void Checksum(byte[] structure, int offset, string what)
    {
        uint sum = 0;
        for (var i = 0; i < structure.Length; i++)
        {
            if (i < offset || i >= offset + 4)
            {
                sum += structure[i];
            }
        }

        if (~sum != BinaryPrimitives.ReadUInt32BigEndian(structure.AsSpan(offset)))
        {
            throw new VhdFormatException($"The checksum of {what} does not match.");
        }
    }

    private static string Type(int type) => type switch { 2 => "fixed", 3 => "dynamic", 4 => "differencing", _ => $"unknown ({type})" };

    private sealed record Member(IBlockSource Source, VhdInfo Info, uint[] Bat, int BitmapBytes);
}
