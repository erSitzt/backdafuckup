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

using System;
using System.IO;
using DiscUtils.Streams;

namespace DiscUtils.Ext;

internal struct Inode : IByteArraySerializable
{
    public uint AccessTime;
    public uint BlocksCount;
    public uint CreationTime;
    public uint DeletionTime;
    public uint DirAcl;
    public uint[] DirectBlocks;
    public uint DoubleIndirectBlock;

    public ExtentBlock Extents;
    public byte[] FastSymlink;
    public uint FileAcl;
    public long FileSize;
    public uint FileVersion;
    public InodeFlags Flags;
    public uint FragAddress;
    public byte Fragment;
    public byte FragmentSize;
    public ushort GroupIdHigh;
    public ushort GroupIdLow;
    public uint IndirectBlock;
    public ushort LinksCount;
    public ushort Mode;
    public uint ModificationTime;
    public uint TripleIndirectBlock;
    public ushort UserIdHigh;
    public ushort UserIdLow;

    public UnixFileType FileType => (UnixFileType)((Mode >> 12) & 0xff);

    // backdafuckup: ext4 large inode fields (i_extra_isize and the *_extra timestamps: epoch bits + nanoseconds).
    public ushort ExtraIsize;
    public uint ChangeTimeExtra;
    public uint ModificationTimeExtra;
    public uint AccessTimeExtra;

    public DateTime AccessTimeUtc => ToDateTime(AccessTime, AccessTimeExtra, ExtraIsize >= 16);

    public DateTime ModificationTimeUtc => ToDateTime(ModificationTime, ModificationTimeExtra, ExtraIsize >= 12);

    /// <summary>Inode change time (i_ctime; upstream calls the raw field CreationTime).</summary>
    public DateTime ChangeTimeUtc => ToDateTime(CreationTime, ChangeTimeExtra, ExtraIsize >= 8);

    private static DateTime ToDateTime(uint raw, uint extra, bool hasExtra)
    {
        long seconds = (int)raw;
        long nanoSeconds = 0;
        if (hasExtra)
        {
            seconds += (long)(extra & 3) << 32;
            nanoSeconds = Math.Min(extra >> 2, 999_999_999u);
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanoSeconds / 100).UtcDateTime;
    }

    public int Size => throw new NotImplementedException();

    public int ReadFrom(ReadOnlySpan<byte> buffer)
    {
        Mode = EndianUtilities.ToUInt16LittleEndian(buffer);
        UserIdLow = EndianUtilities.ToUInt16LittleEndian(buffer[2..]);
        FileSize = EndianUtilities.ToUInt32LittleEndian(buffer[4..]);
        AccessTime = EndianUtilities.ToUInt32LittleEndian(buffer[8..]);
        CreationTime = EndianUtilities.ToUInt32LittleEndian(buffer[12..]);
        ModificationTime = EndianUtilities.ToUInt32LittleEndian(buffer[16..]);
        DeletionTime = EndianUtilities.ToUInt32LittleEndian(buffer[20..]);
        GroupIdLow = EndianUtilities.ToUInt16LittleEndian(buffer[24..]);
        LinksCount = EndianUtilities.ToUInt16LittleEndian(buffer[26..]);
        BlocksCount = EndianUtilities.ToUInt32LittleEndian(buffer[28..]);
        Flags = (InodeFlags)EndianUtilities.ToUInt32LittleEndian(buffer[32..]);

        FastSymlink = null;
        Extents = default;
        DirectBlocks = null;
        // backdafuckup: a fast symlink keeps its target (< 60 bytes) in i_block; i_blocks is not zero when the
        // inode has an extended attribute block, so the size decides (as in the kernel).
        if (FileType == UnixFileType.Link && FileSize < 60 && (Flags & InodeFlags.ExtentsUsed) == 0)
        {
            FastSymlink = buffer.Slice(40, 60).ToArray();
        }
        else if ((Flags & InodeFlags.ExtentsUsed) != 0)
        {
            Extents = EndianUtilities.ToStruct<ExtentBlock>(buffer[40..]);
        }
        else
        {
            DirectBlocks = new uint[12];
            for (var i = 0; i < 12; ++i)
            {
                DirectBlocks[i] = EndianUtilities.ToUInt32LittleEndian(buffer[(40 + i * 4)..]);
            }

            IndirectBlock = EndianUtilities.ToUInt32LittleEndian(buffer[88..]);
            DoubleIndirectBlock = EndianUtilities.ToUInt32LittleEndian(buffer[92..]);
            TripleIndirectBlock = EndianUtilities.ToUInt32LittleEndian(buffer[96..]);
        }

        FileVersion = EndianUtilities.ToUInt32LittleEndian(buffer[100..]);
        FileAcl = EndianUtilities.ToUInt32LittleEndian(buffer[104..]);
        DirAcl = EndianUtilities.ToUInt32LittleEndian(buffer[108..]);
        
        if (FileType == UnixFileType.Regular
            && DirAcl != 0)
        {
            FileSize |= ((long)DirAcl) << 32;
        }
        
        FragAddress = EndianUtilities.ToUInt32LittleEndian(buffer[112..]);
        Fragment = buffer[116];
        FragmentSize = buffer[117];
        UserIdHigh = EndianUtilities.ToUInt16LittleEndian(buffer[120..]);
        GroupIdHigh = EndianUtilities.ToUInt16LittleEndian(buffer[122..]);

        ExtraIsize = 0;
        ChangeTimeExtra = ModificationTimeExtra = AccessTimeExtra = 0;
        if (buffer.Length >= 0x90)
        {
            ExtraIsize = EndianUtilities.ToUInt16LittleEndian(buffer[0x80..]);
            if (0x80 + ExtraIsize > buffer.Length)
            {
                ExtraIsize = 0;
            }

            ChangeTimeExtra = ExtraIsize >= 8 ? EndianUtilities.ToUInt32LittleEndian(buffer[0x84..]) : 0;
            ModificationTimeExtra = ExtraIsize >= 12 ? EndianUtilities.ToUInt32LittleEndian(buffer[0x88..]) : 0;
            AccessTimeExtra = ExtraIsize >= 16 ? EndianUtilities.ToUInt32LittleEndian(buffer[0x8C..]) : 0;
        }

        return 128;
    }

    void IByteArraySerializable.WriteTo(Span<byte> buffer)
    {
        throw new NotImplementedException();
    }

    public IBuffer GetContentBuffer(Context context)
    {
        if (FastSymlink != null)
        {
            return new StreamBuffer(new MemoryStream(FastSymlink, false), Ownership.Dispose);
        }

        if ((Flags & InodeFlags.ExtentsUsed) != 0)
        {
            return new ExtentsFileBuffer(context, this);
        }

        return new FileBuffer(context, this);
    }
}