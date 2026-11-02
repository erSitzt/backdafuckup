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
using System.Buffers;
using System.Collections.Generic;
using DiscUtils.Internal;
using DiscUtils.Streams;
using DiscUtils.Vfs;

namespace DiscUtils.Ext;

internal class Directory : File, IVfsDirectory<DirEntry, File>
{
    public Directory(Context context, uint inodeNum, Inode inode)
        : base(context, inodeNum, inode) { }

    FastDictionary<DirEntry> dirEntries;

    private List<DirEntry> _rawEntries;

    /// <summary>
    /// backdafuckup: every entry with its raw name, including entries whose sanitized names collide in
    /// <see cref="AllEntries"/> (e.g. "a:b" and "a_b").
    /// </summary>
    internal IReadOnlyList<DirEntry> RawEntries
    {
        get
        {
            _ = AllEntries;
            return _rawEntries;
        }
    }

    public IReadOnlyDictionary<string, DirEntry> AllEntries
    {
        get
        {
            if (dirEntries == null)
            {
                var raw = new List<DirEntry>();

                var content = FileContent;
                var blockSize = Context.SuperBlock.BlockSize;

                var blockData = ArrayPool<byte>.Shared.Rent((int)blockSize);
                try
                {
                    uint relBlock = 0;

                    long pos = 0;
                    while (pos < Inode.FileSize)
                    {
                        content.ReadMaximum(blockSize * (long)relBlock, blockData, 0, (int)blockSize);

                        var blockPos = 0;
                        while (blockPos < blockSize)
                        {
                            var r = new DirectoryRecord(Context.Options.FileNameEncoding);
                            var numRead = r.ReadFrom(blockData.AsSpan(blockPos, (int)(blockSize - blockPos)));

                            if (r.Inode != 0 && r.RawName != "." && r.RawName != "..")
                            {
                                raw.Add(new DirEntry(r));
                            }

                            blockPos += numRead;
                        }

                        ++relBlock;
                        pos += blockSize;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(blockData);
                }

                // backdafuckup: sanitized names may collide; the first entry wins here, RawEntries keeps all.
                var result = new FastDictionary<DirEntry>(StringComparer.Ordinal, static entry => entry.FileName);
                foreach (var entry in raw)
                {
                    if (!result.ContainsKey(entry.FileName))
                    {
                        result.Add(entry);
                    }
                }

                _rawEntries = raw;
                dirEntries = result;
            }

            return dirEntries;
        }
    }

    public DirEntry Self => null;

    public DirEntry GetEntryByName(string name)
        => AllEntries.TryGetValue(name, out var entry) ? entry : null;

    public DirEntry CreateNewFile(string name)
    {
        // Clear cache
        dirEntries = null;

        throw new NotImplementedException();
    }
}