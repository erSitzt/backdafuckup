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
using System.Text;
using DiscUtils.CoreCompat;
using DiscUtils.Streams;

namespace DiscUtils.Ext;

internal struct DirectoryRecord : IByteArraySerializable
{
    public const byte FileTypeUnknown = 0;
    public const byte FileTypeRegularFile = 1;
    public const byte FileTypeDirectory = 2;
    public const byte FileTypeCharacterDevice = 3;
    public const byte FileTypeBlockDevice = 4;
    public const byte FileTypeFifo = 5;
    public const byte FileTypeSocket = 6;
    public const byte FileTypeSymlink = 7;

    private readonly Encoding _nameEncoding;
    public byte FileType;

    public uint Inode;
    public string Name;

    /// <summary>backdafuckup: the name as stored (not sanitized); exact when it round-trips through the encoding.</summary>
    public string RawName;

    public bool RawNameExact;

    public DirectoryRecord(Encoding nameEncoding) : this()
    {
        _nameEncoding = nameEncoding;
    }

    public int Size => MathUtilities.RoundUp(8 + Name.Length, 4);

    public int ReadFrom(ReadOnlySpan<byte> buffer)
    {
        Inode = EndianUtilities.ToUInt32LittleEndian(buffer);
        var recordLen = EndianUtilities.ToUInt16LittleEndian(buffer[4..]);
        int nameLen = buffer[6];
        FileType = buffer[7];

        // backdafuckup: a record shorter than its header or than its name, or running past the block, is corrupt
        // (a zero length would loop forever).
        if (recordLen < 8 || recordLen > buffer.Length || 8 + nameLen > recordLen)
        {
            throw new System.IO.IOException($"Corrupt ext directory record (length {recordLen}, name length {nameLen}).");
        }

        var nameBytes = buffer.Slice(8, nameLen);
        RawName = _nameEncoding.GetString(nameBytes);
        RawNameExact = nameBytes.SequenceEqual(_nameEncoding.GetBytes(RawName));
        Name = RawName.SanitizeFileName();

        return recordLen;
    }

    void IByteArraySerializable.WriteTo(Span<byte> buffer)
    {
        throw new NotImplementedException();
    }
}