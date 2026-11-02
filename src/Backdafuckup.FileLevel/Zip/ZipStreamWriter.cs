using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Backdafuckup.FileLevel.Zip;

/// <summary>Which operating system the external attributes of an entry belong to (the "version made by" host).</summary>
public enum ZipHost : byte
{
    /// <summary>MS-DOS/Windows attributes (read-only, hidden, system, directory, archive).</summary>
    Dos = 0,

    /// <summary>Unix mode bits in the upper 16 bits of the external attributes.</summary>
    Unix = 3,
}

/// <summary>Name, time and attributes of one ZIP entry. Names use "/" and no leading "/".</summary>
public sealed record ZipEntryMeta(string Name, DateTime? ModifiedUtc, ZipHost Host, uint ExternalAttributes);

public sealed record ZipWriterOptions
{
    public CompressionLevel Compression { get; init; } = CompressionLevel.Fastest;

    /// <summary>Zone for the MS-DOS timestamp (Windows Explorer shows only that one; other tools read the UTC extra field).</summary>
    public TimeZoneInfo DosTimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Writes every entry and the end record in ZIP64 form (tests the ZIP64 paths without gigabytes of data).</summary>
    public bool ForceZip64 { get; init; }
}

/// <summary>
/// Writes a ZIP archive front to back to a non-seekable stream (an HTTP response). Files are deflated with a data
/// descriptor; ZIP64 is used where sizes, offsets or the entry count need it. Unlike <see cref="ZipArchive"/> in
/// streaming mode, an entry that may exceed 4 GiB gets the ZIP64 extra field in its local header too, as APPNOTE
/// 4.3.9 requires for streaming readers. Not thread-safe; after an exception the archive is unusable.
/// </summary>
public sealed class ZipStreamWriter : IDisposable
{
    /// <summary>
    /// Files from this size on get ZIP64 local headers. Deflate can grow incompressible data a little, so the limit
    /// stays well below 4 GiB.
    /// </summary>
    public const long Zip64Threshold = 0xF000_0000;

    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint DataDescriptorSignature = 0x08074b50;
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const ushort FlagDataDescriptor = 0x0008;
    private const ushort FlagUtf8 = 0x0800;
    private const ushort MethodStored = 0;
    private const ushort MethodDeflate = 8;
    private const ushort VersionZip64 = 45;
    private const ushort VersionDeflate = 20;
    private const ushort TagZip64 = 0x0001;
    private const ushort TagUnixTime = 0x5455;
    private const int BufferSize = 1 << 20;
    private const int SpillThreshold = 8 << 20;

    private readonly CountingStream _output;
    private readonly ZipWriterOptions _options;
    private Stream _central = new MemoryStream();
    private long _entries;
    private bool _finished;
    private bool _faulted;

    public ZipStreamWriter(Stream output, ZipWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = new CountingStream(output);
        _options = options ?? new ZipWriterOptions();
    }

    public long EntryCount => _entries;

    /// <summary>Bytes of the archive written so far.</summary>
    public long Position => _output.Position;

    /// <summary>The longest entry name in UTF-8 bytes that a ZIP header can hold.</summary>
    public static bool FitsName(string name) => Encoding.UTF8.GetByteCount(name) <= ushort.MaxValue;

    public void AddDirectory(ZipEntryMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var name = meta.Name.EndsWith('/') ? meta.Name : meta.Name + "/";
        Guard(() =>
        {
            var offset = _output.Position;
            var nameBytes = EncodeName(name);
            var zip64 = _options.ForceZip64;
            WriteLocalHeader(nameBytes, meta, MethodStored, 0, zip64);
            AddCentral(nameBytes, meta, MethodStored, 0, 0, 0, 0, offset, zip64);
        });
    }

    /// <summary>
    /// Deflates <paramref name="content"/> into a new entry and returns the bytes read. <paramref name="expectedSize"/>
    /// decides whether the local header is ZIP64; the content may not grow past 4 GiB without it.
    /// </summary>
    public long AddFile(ZipEntryMeta meta, long expectedSize, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(content);
        long read = 0;
        Guard(() =>
        {
            var offset = _output.Position;
            var nameBytes = EncodeName(meta.Name);
            var zip64 = _options.ForceZip64 || expectedSize >= Zip64Threshold;
            WriteLocalHeader(nameBytes, meta, MethodDeflate, FlagDataDescriptor, zip64);
            var start = _output.Position;
            var crc = new Crc32();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var deflate = new DeflateStream(_output, _options.Compression, leaveOpen: true);
                int n;
                while ((n = content.Read(buffer, 0, BufferSize)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    crc.Append(buffer.AsSpan(0, n));
                    deflate.Write(buffer, 0, n);
                    read += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // DeflateStream writes nothing for empty input; Info-ZIP and others need at least the final empty block.
            if (_output.Position == start)
            {
                _output.Write([0x03, 0x00]);
            }

            var compressed = _output.Position - start;
            if (!zip64 && (compressed >= uint.MaxValue || read >= uint.MaxValue))
            {
                throw new InvalidDataException($"{meta.Name} grew to {read:N0} bytes, more than the expected {expectedSize:N0}.");
            }

            var checksum = crc.GetCurrentHashAsUInt32();
            Span<byte> descriptor = stackalloc byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, DataDescriptorSignature);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], checksum);
            int length;
            if (zip64)
            {
                BinaryPrimitives.WriteInt64LittleEndian(descriptor[8..], compressed);
                BinaryPrimitives.WriteInt64LittleEndian(descriptor[16..], read);
                length = 24;
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], (uint)compressed);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], (uint)read);
                length = 16;
            }

            _output.Write(descriptor[..length]);
            AddCentral(nameBytes, meta, MethodDeflate, FlagDataDescriptor, checksum, compressed, read, offset, zip64);
        });
        return read;
    }

    /// <summary>Writes the central directory and the end records. The output stream stays open.</summary>
    public void Finish()
    {
        Guard(() =>
        {
            if (_finished)
            {
                return;
            }

            var centralOffset = _output.Position;
            _central.Position = 0;
            _central.CopyTo(_output, BufferSize);
            var centralSize = _output.Position - centralOffset;
            var zip64 = _options.ForceZip64 || _entries >= ushort.MaxValue || centralOffset >= uint.MaxValue || centralSize >= uint.MaxValue;
            Span<byte> end = stackalloc byte[56 + 20 + 22];
            var length = 0;
            if (zip64)
            {
                var zip64EndOffset = _output.Position;
                BinaryPrimitives.WriteUInt32LittleEndian(end, Zip64EndSignature);
                BinaryPrimitives.WriteInt64LittleEndian(end[4..], 44);
                BinaryPrimitives.WriteUInt16LittleEndian(end[12..], VersionZip64);
                BinaryPrimitives.WriteUInt16LittleEndian(end[14..], VersionZip64);
                BinaryPrimitives.WriteUInt32LittleEndian(end[16..], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(end[20..], 0);
                BinaryPrimitives.WriteInt64LittleEndian(end[24..], _entries);
                BinaryPrimitives.WriteInt64LittleEndian(end[32..], _entries);
                BinaryPrimitives.WriteInt64LittleEndian(end[40..], centralSize);
                BinaryPrimitives.WriteInt64LittleEndian(end[48..], centralOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(end[56..], Zip64LocatorSignature);
                BinaryPrimitives.WriteUInt32LittleEndian(end[60..], 0);
                BinaryPrimitives.WriteInt64LittleEndian(end[64..], zip64EndOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(end[72..], 1);
                length = 76;
            }

            var count = zip64 && (_options.ForceZip64 || _entries >= ushort.MaxValue) ? ushort.MaxValue : (ushort)_entries;
            BinaryPrimitives.WriteUInt32LittleEndian(end[length..], EndSignature);
            BinaryPrimitives.WriteUInt16LittleEndian(end[(length + 4)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(end[(length + 6)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(end[(length + 8)..], count);
            BinaryPrimitives.WriteUInt16LittleEndian(end[(length + 10)..], count);
            BinaryPrimitives.WriteUInt32LittleEndian(end[(length + 12)..], Clamp32(centralSize, _options.ForceZip64));
            BinaryPrimitives.WriteUInt32LittleEndian(end[(length + 16)..], Clamp32(centralOffset, _options.ForceZip64));
            BinaryPrimitives.WriteUInt16LittleEndian(end[(length + 20)..], 0);
            _output.Write(end[..(length + 22)]);
            _output.Flush();
            _finished = true;
        });
    }

    public void Dispose() => _central.Dispose();

    private void Guard(Action action)
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The ZIP archive is incomplete after an earlier error.");
        }

        if (_finished)
        {
            throw new InvalidOperationException("The ZIP archive is finished.");
        }

        try
        {
            action();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"The name is longer than a ZIP entry name can be ({bytes.Length:N0} bytes): {name[..80]}…", nameof(name));
        }

        return bytes;
    }

    private void WriteLocalHeader(byte[] name, ZipEntryMeta meta, ushort method, ushort flags, bool zip64)
    {
        var unixTime = UnixTime(meta.ModifiedUtc);
        var extraLength = (zip64 ? 20 : 0) + (unixTime is null ? 0 : 9);
        var header = new byte[30 + name.Length + extraLength];
        var span = header.AsSpan();
        var (time, date) = DosTime(meta.ModifiedUtc);
        BinaryPrimitives.WriteUInt32LittleEndian(span, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], zip64 ? VersionZip64 : VersionDeflate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)(flags | FlagUtf8));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], date);

        // CRC and sizes follow in the data descriptor (files) or are zero (folders).
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], zip64 ? uint.MaxValue : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], zip64 ? uint.MaxValue : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)extraLength);
        name.CopyTo(span[30..]);
        var extra = span[(30 + name.Length)..];
        if (zip64)
        {
            // Both sizes, as APPNOTE 4.5.3 requires in a local header; zero because the descriptor carries them.
            BinaryPrimitives.WriteUInt16LittleEndian(extra, TagZip64);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
            BinaryPrimitives.WriteInt64LittleEndian(extra[4..], 0);
            BinaryPrimitives.WriteInt64LittleEndian(extra[12..], 0);
            extra = extra[20..];
        }

        if (unixTime is { } seconds)
        {
            WriteUnixTime(extra, seconds);
        }

        _output.Write(header);
    }

    private void AddCentral(byte[] name, ZipEntryMeta meta, ushort method, ushort flags, uint crc, long compressed, long size, long offset, bool zip64Entry)
    {
        var force = _options.ForceZip64;
        var bigSize = zip64Entry || size >= uint.MaxValue;
        var bigCompressed = zip64Entry || compressed >= uint.MaxValue;
        var bigOffset = force || offset >= uint.MaxValue;
        var zip64Length = (bigSize ? 8 : 0) + (bigCompressed ? 8 : 0) + (bigOffset ? 8 : 0);
        var unixTime = UnixTime(meta.ModifiedUtc);
        var extraLength = (zip64Length > 0 ? 4 + zip64Length : 0) + (unixTime is null ? 0 : 9);
        var header = new byte[46 + name.Length + extraLength];
        var span = header.AsSpan();
        var (time, date) = DosTime(meta.ModifiedUtc);
        var needed = zip64Length > 0 ? VersionZip64 : VersionDeflate;
        BinaryPrimitives.WriteUInt32LittleEndian(span, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(((int)meta.Host << 8) | VersionZip64));
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], needed);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], (ushort)(flags | FlagUtf8));
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], date);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], bigCompressed ? uint.MaxValue : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], bigSize ? uint.MaxValue : (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)extraLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[36..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[38..], meta.ExternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[42..], bigOffset ? uint.MaxValue : (uint)offset);
        name.CopyTo(span[46..]);
        var extra = span[(46 + name.Length)..];
        if (zip64Length > 0)
        {
            // Only the fields that are 0xFFFFFFFF above, in this fixed order (APPNOTE 4.5.3).
            BinaryPrimitives.WriteUInt16LittleEndian(extra, TagZip64);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], (ushort)zip64Length);
            var field = extra[4..];
            if (bigSize)
            {
                BinaryPrimitives.WriteInt64LittleEndian(field, size);
                field = field[8..];
            }

            if (bigCompressed)
            {
                BinaryPrimitives.WriteInt64LittleEndian(field, compressed);
                field = field[8..];
            }

            if (bigOffset)
            {
                BinaryPrimitives.WriteInt64LittleEndian(field, offset);
            }

            extra = extra[(4 + zip64Length)..];
        }

        if (unixTime is { } seconds)
        {
            WriteUnixTime(extra, seconds);
        }

        _central.Write(header);
        _entries++;
        SpillCentralIfLarge();
    }

    /// <summary>A million entries make a central directory of ~150 MB: keep it in a temporary file beyond a few MB.</summary>
    private void SpillCentralIfLarge()
    {
        if (_central is not MemoryStream memory || memory.Length < SpillThreshold)
        {
            return;
        }

        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"bdfu-zip-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            1 << 16,
            FileOptions.DeleteOnClose);
        memory.Position = 0;
        memory.CopyTo(file);
        _central = file;
        memory.Dispose();
    }

    private static void WriteUnixTime(Span<byte> extra, int seconds)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(extra, TagUnixTime);
        BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 5);
        extra[4] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(extra[5..], seconds);
    }

    private static int? UnixTime(DateTime? utc)
    {
        if (utc is not { } value)
        {
            return null;
        }

        var seconds = new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return seconds is >= int.MinValue and <= int.MaxValue ? (int)seconds : null;
    }

    private (ushort Time, ushort Date) DosTime(DateTime? utc)
    {
        if (utc is not { } value)
        {
            return (0, (1 << 5) | 1);
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), _options.DosTimeZone);
        if (local.Year < 1980)
        {
            return (0, (1 << 5) | 1);
        }

        if (local.Year > 2107)
        {
            local = new DateTime(2107, 12, 31, 23, 59, 58);
        }

        var time = (ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2));
        var date = (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day);
        return (time, date);
    }

    private static uint Clamp32(long value, bool force) => force || value >= uint.MaxValue ? uint.MaxValue : (uint)value;

    /// <summary>Forwards writes and counts them (the output cannot seek).</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        private long _position;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            _position += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _position += buffer.Length;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
