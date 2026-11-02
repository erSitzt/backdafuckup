//
// Copyright (c) 2008-2011, Kenneth Bell
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
//

// Modified by the backdafuckup project (see NOTICE.md): rewritten block run resolution. Holes between and after
// extents read as zeros, unwritten (preallocated) extents read as zeros, and the extent tree depth is bounded.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Streams;
using Buffer = DiscUtils.Streams.Buffer;

namespace DiscUtils.Ext;

internal class ExtentsFileBuffer : Buffer, IFileBuffer
{
    /// <summary>ext4 allows a depth of 5; anything deeper is a corrupt or hostile image.</summary>
    private const int MaxDepth = 8;

    /// <summary>ee_len above this value marks an unwritten extent of (ee_len - 32768) blocks.</summary>
    private const int InitializedMaxLength = 32768;

    private readonly Context _context;
    private readonly Inode _inode;

    public ExtentsFileBuffer(Context context, Inode inode)
    {
        _context = context;
        _inode = inode;
    }

    public override bool CanRead => true;

    public override bool CanWrite => false;

    public override long Capacity => _inode.FileSize;

    private int BlockSize => (int)_context.SuperBlock.BlockSize;

    public IEnumerable<Range<long, long>> EnumerateAllocationClusters()
    {
        foreach (var (logical, run) in Runs(0, _inode.FileSize))
        {
            if (run.IsData)
            {
                yield return new(run.PhysicalBlock, run.Blocks);
            }
        }
    }

    public IEnumerable<StreamExtent> EnumerateAllocationExtents()
    {
        var blockSize = BlockSize;
        foreach (var (logical, run) in Runs(0, _inode.FileSize))
        {
            if (run.IsData)
            {
                var length = Math.Min(run.Blocks * blockSize, _inode.FileSize - logical * blockSize);
                yield return new(run.PhysicalBlock * blockSize, length);
            }
        }
    }

    public override int Read(long pos, byte[] buffer, int offset, int count) => Read(pos, buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(long pos, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Read(pos, buffer.Span));
    }

    public override int Read(long pos, Span<byte> buffer)
    {
        if (pos < 0 || pos >= _inode.FileSize || buffer.IsEmpty)
        {
            return 0;
        }

        var blockSize = BlockSize;
        var total = (int)Math.Min(buffer.Length, _inode.FileSize - pos);
        var done = 0;
        while (done < total)
        {
            var position = pos + done;
            var inBlock = (int)(position % blockSize);
            var run = Resolve(position / blockSize);
            var count = (int)Math.Min(total - done, (run.Blocks * blockSize) - inBlock);
            var destination = buffer.Slice(done, count);
            if (run.IsData)
            {
                _context.RawStream.Position = (run.PhysicalBlock * blockSize) + inBlock;
                _context.RawStream.ReadExactly(destination);
            }
            else
            {
                destination.Clear();
            }

            done += count;
        }

        return total;
    }

    public override void Write(long pos, byte[] buffer, int offset, int count) => throw new NotImplementedException();

    public override void Write(long pos, ReadOnlySpan<byte> buffer) => throw new NotImplementedException();

    public override void SetCapacity(long value) => throw new NotImplementedException();

    /// <summary>The ranges of the file that hold data (holes and unwritten extents are left out).</summary>
    public override IEnumerable<StreamExtent> GetExtentsInRange(long start, long count)
    {
        var end = Math.Min(start + count, _inode.FileSize);
        var blockSize = BlockSize;
        foreach (var (logical, run) in Runs(start, end))
        {
            if (run.IsData)
            {
                var from = Math.Max(start, logical * blockSize);
                var to = Math.Min(end, (logical + run.Blocks) * blockSize);
                if (to > from)
                {
                    yield return new(from, to - from);
                }
            }
        }
    }

    private IEnumerable<(long Logical, BlockRun Run)> Runs(long start, long end)
    {
        var blockSize = BlockSize;
        var logical = start / blockSize;
        var last = (end + blockSize - 1) / blockSize;
        while (logical < last)
        {
            var run = Resolve(logical);
            var blocks = Math.Min(run.Blocks, last - logical);
            yield return (logical, run with { Blocks = blocks });
            logical += blocks;
        }
    }

    /// <summary>Finds the run of blocks that starts at <paramref name="logicalBlock"/>: data, hole or unwritten.</summary>
    private BlockRun Resolve(long logicalBlock)
    {
        // Holes end at the end of the file at the latest, so run lengths never overflow when scaled to bytes.
        var fileBlocks = Math.Max(logicalBlock + 1, (_inode.FileSize + BlockSize - 1) / BlockSize);
        return Resolve(_inode.Extents, logicalBlock, fileBlocks, 0);
    }

    private BlockRun Resolve(ExtentBlock node, long logical, long limit, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new IOException($"The extent tree of inode is deeper than {MaxDepth} levels; the file system is corrupt.");
        }

        if (node.Index != null)
        {
            var i = LastAtOrBefore(node.Index.Length, n => node.Index[n].FirstLogicalBlock, logical);
            if (i < 0)
            {
                return BlockRun.Hole(node.Index.Length == 0 ? limit - logical : Math.Min(limit, node.Index[0].FirstLogicalBlock) - logical);
            }

            var next = i + 1 < node.Index.Length ? Math.Min(limit, node.Index[i + 1].FirstLogicalBlock) : limit;
            return Resolve(LoadExtentBlock(node.Index[i]), logical, next, depth + 1);
        }

        var extents = node.Extents ?? [];
        var e = LastAtOrBefore(extents.Length, n => extents[n].FirstLogicalBlock, logical);
        if (e >= 0)
        {
            var extent = extents[e];
            var unwritten = extent.NumBlocks > InitializedMaxLength;
            long length = unwritten ? extent.NumBlocks - InitializedMaxLength : extent.NumBlocks;
            var offset = logical - extent.FirstLogicalBlock;
            if (offset < length)
            {
                return new BlockRun(unwritten ? BlockKind.Unwritten : BlockKind.Data, (long)extent.FirstPhysicalBlock + offset, length - offset);
            }
        }

        var nextStart = e + 1 < extents.Length ? Math.Min(limit, extents[e + 1].FirstLogicalBlock) : limit;
        return BlockRun.Hole(nextStart - logical);
    }

    private static int LastAtOrBefore(int count, Func<int, long> first, long logical)
    {
        var result = -1;
        for (var i = 0; i < count; i++)
        {
            if (first(i) > logical)
            {
                break;
            }

            result = i;
        }

        return result;
    }

    private ExtentBlock LoadExtentBlock(ExtentIndex idxEntry)
    {
        var blockSize = BlockSize;
        _context.RawStream.Position = idxEntry.LeafPhysicalBlock * blockSize;
        var buffer = ArrayPool<byte>.Shared.Rent(blockSize);
        try
        {
            var span = buffer.AsSpan(0, blockSize);
            _context.RawStream.ReadExactly(span);
            var block = EndianUtilities.ToStruct<ExtentBlock>(span);
            if ((block.Header.Entries * 12) + 12 > blockSize)
            {
                throw new IOException("An ext4 extent block claims more entries than fit into the block; the file system is corrupt.");
            }

            return block;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private enum BlockKind
    {
        Data,
        Hole,
        Unwritten,
    }

    private readonly record struct BlockRun(BlockKind Kind, long PhysicalBlock, long Blocks)
    {
        public bool IsData => Kind == BlockKind.Data;

        /// <summary>A hole of at least one block (a corrupt tree must not stop the reader from making progress).</summary>
        public static BlockRun Hole(long blocks) => new(BlockKind.Hole, 0, Math.Max(1, blocks));
    }
}
