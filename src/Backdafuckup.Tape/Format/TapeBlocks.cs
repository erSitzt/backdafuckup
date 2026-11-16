using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Tape.Devices;

namespace Backdafuckup.Tape.Format;

/// <summary>
/// The on-tape format, version 1 (docs/tape-format.md). A file is a run of blocks between filemarks; each block is a
/// 32-byte header and a payload; the payloads of a file form a stream of records; the first record is <c>FHDR</c>, the
/// last <c>FTRL</c>. A file without its trailer is torn and never used.
/// </summary>
public static class TapeFormat
{
    /// <summary>The format this version writes; readers refuse blocks of a newer one.</summary>
    public const byte Version = 1;

    public const int BlockHeaderSize = 32;

    public const int RecordHeaderSize = 16;

    public const string FileHeader = "FHDR";
    public const string Trailer = "FTRL";
    public const string Label = "LABL";
    public const string Chunk = "CHNK";
    public const string ArchiveIndex = "AIDX";
    public const string Entry = "ENTR";
    public const string Locations = "LOCS";
    public const string Catalog = "CATL";

    internal static ReadOnlySpan<byte> BlockMagic => "BDTB"u8;

    internal static void WriteRecordHeader(Span<byte> destination, string type, RecordFlags flags, long length)
    {
        if (type.Length != 4 || !Ascii.IsValid(type))
        {
            throw new ArgumentException($"A record type has 4 ASCII letters, not '{type}'.", nameof(type));
        }

        Encoding.ASCII.GetBytes(type, destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)flags);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], length);
    }
}

[Flags]
public enum RecordFlags : uint
{
    None = 0,

    /// <summary>The body is sealed with AES-256-GCM (nonce, ciphertext, tag); see <see cref="ITapeRecordCipher"/>.</summary>
    Encrypted = 1,
}

/// <summary>Where a record starts: the block of its file (counted from 0) and the offset in that block's payload.</summary>
public readonly record struct RecordPosition(uint Block, int Offset);

/// <param name="Position">Where the record starts in its file.</param>
/// <param name="Body">The body as stored (sealed when <see cref="RecordFlags.Encrypted"/>; <see cref="TapeFileReader"/> opens it).</param>
public sealed record TapeRecord(string Type, RecordFlags Flags, byte[] Body, RecordPosition Position);

/// <summary>Seals and opens record bodies (software encryption); the record header, the set and the file are authenticated.</summary>
public interface ITapeRecordCipher
{
    byte[] Seal(ReadOnlySpan<byte> recordHeader, ulong fileId, ReadOnlySpan<byte> body);

    /// <summary>Throws <see cref="CryptographicException"/> when the body or its header was changed, or the key is wrong.</summary>
    byte[] Open(ReadOnlySpan<byte> recordHeader, ulong fileId, ReadOnlySpan<byte> sealedBody);

    /// <summary>Bytes a sealed body has more than the plain one.</summary>
    int Overhead { get; }
}

/// <summary>Where blocks are written: a drive (<see cref="DriveBlocks"/>), or memory in tests.</summary>
public interface ITapeBlockWriter
{
    Task<WriteOutcome> WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken cancellationToken);

    /// <summary>Ends a file. <paramref name="synchronize"/> waits until the drive has the data on tape (a commit point).</summary>
    Task<WriteOutcome> WriteFilemarkAsync(bool synchronize, CancellationToken cancellationToken);
}

public interface ITapeBlockReader
{
    Task<ReadOutcome> ReadBlockAsync(Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>A drive as block writer and reader.</summary>
public sealed class DriveBlocks(TapeDrive drive) : ITapeBlockWriter, ITapeBlockReader
{
    public Task<WriteOutcome> WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken cancellationToken) => drive.WriteBlockAsync(block, cancellationToken);

    public Task<WriteOutcome> WriteFilemarkAsync(bool synchronize, CancellationToken cancellationToken) => drive.WriteFilemarksAsync(1, immediate: !synchronize, cancellationToken);

    public Task<ReadOutcome> ReadBlockAsync(Memory<byte> destination, CancellationToken cancellationToken) => drive.ReadBlockAsync(destination, cancellationToken);
}

/// <summary>A file was cut off (a filemark or the end of data before its trailer): it is torn and is not used.</summary>
public sealed class TornFileException(string message) : IOException(message);

/// <summary>
/// Writes one file: its header record at once, then records, then <see cref="FinishAsync"/> with the trailer, the last
/// block and a filemark. Blocks are full except the last; records run across blocks.
/// </summary>
public sealed class TapeFileWriter
{
    private readonly ITapeBlockWriter _sink;
    private readonly byte[] _block;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly ITapeRecordCipher? _cipher;
    private int _fill = TapeFormat.BlockHeaderSize;
    private uint _blockNumber;
    private long _records;
    private bool _finished;

    private TapeFileWriter(ITapeBlockWriter sink, int blockSize, ulong fileId, ITapeRecordCipher? cipher)
    {
        if (blockSize < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "Tape blocks have at least 4 KiB.");
        }

        _sink = sink;
        _block = new byte[blockSize];
        _cipher = cipher;
        FileId = fileId;
    }

    public ulong FileId { get; }

    /// <summary>Whether a block of this file reached the early-warning zone: finish it and change the tape.</summary>
    public bool EarlyWarning { get; private set; }

    /// <summary>Bytes handed to the drive so far (headers included).</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Blocks written so far.</summary>
    public uint Blocks => _blockNumber;

    /// <summary>Starts a file with its header record (type, set, sequence, file number: in clear, so that a scan can name it).</summary>
    public static async Task<TapeFileWriter> BeginAsync(ITapeBlockWriter sink, int blockSize, TapeFileHeader header, ITapeRecordCipher? cipher, CancellationToken cancellationToken)
    {
        var writer = new TapeFileWriter(sink, blockSize, header.FileId, cipher);
        await writer.WriteRecordAsync(TapeFormat.FileHeader, TapeJson.Serialize(header), encrypt: false, cancellationToken).ConfigureAwait(false);
        return writer;
    }

    /// <summary>Appends a record; returns where it starts. With a cipher and <paramref name="encrypt"/>, the body is sealed.</summary>
    public async Task<RecordPosition> WriteRecordAsync(string type, ReadOnlyMemory<byte> body, bool encrypt, CancellationToken cancellationToken)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The file is finished.");
        }

        var position = new RecordPosition(_blockNumber, _fill - TapeFormat.BlockHeaderSize);
        var flags = encrypt && _cipher is not null ? RecordFlags.Encrypted : RecordFlags.None;
        var header = new byte[TapeFormat.RecordHeaderSize];
        ReadOnlyMemory<byte> stored = body;
        if (flags.HasFlag(RecordFlags.Encrypted))
        {
            TapeFormat.WriteRecordHeader(header, type, flags, body.Length + _cipher!.Overhead);
            stored = _cipher.Seal(header, FileId, body.Span);
        }
        else
        {
            TapeFormat.WriteRecordHeader(header, type, flags, body.Length);
        }

        if (type != TapeFormat.Trailer)
        {
            _hash.AppendData(header);
            _hash.AppendData(stored.Span);
            _records++;
        }

        await AppendAsync(header, cancellationToken).ConfigureAwait(false);
        await AppendAsync(stored, cancellationToken).ConfigureAwait(false);
        return position;
    }

    /// <summary>The trailer (record count and SHA-256 of everything before it), the last block, a filemark.</summary>
    public async Task<TapeFileSummary> FinishAsync(bool synchronize, CancellationToken cancellationToken)
    {
        var digest = _hash.GetHashAndReset();
        var trailer = new byte[8 + 32];
        BinaryPrimitives.WriteInt64BigEndian(trailer, _records);
        digest.CopyTo(trailer, 8);
        await WriteRecordAsync(TapeFormat.Trailer, trailer, encrypt: false, cancellationToken).ConfigureAwait(false);
        _finished = true;
        await FlushAsync(last: true, cancellationToken).ConfigureAwait(false);
        var outcome = await _sink.WriteFilemarkAsync(synchronize, cancellationToken).ConfigureAwait(false);
        EarlyWarning |= outcome.EarlyWarning;
        return new TapeFileSummary(FileId, _blockNumber, BytesWritten, _records, Convert.ToHexString(digest));
    }

    private async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        while (data.Length > 0)
        {
            var n = Math.Min(data.Length, _block.Length - _fill);
            data.Span[..n].CopyTo(_block.AsSpan(_fill));
            _fill += n;
            data = data[n..];
            if (_fill == _block.Length)
            {
                await FlushAsync(last: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushAsync(bool last, CancellationToken cancellationToken)
    {
        var header = _block.AsSpan(0, TapeFormat.BlockHeaderSize);
        header.Clear();
        TapeFormat.BlockMagic.CopyTo(header);
        header[4] = TapeFormat.Version;
        header[5] = (byte)(last ? 1 : 0);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], FileId);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], _blockNumber);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)(_fill - TapeFormat.BlockHeaderSize));
        BinaryPrimitives.WriteUInt32BigEndian(header[24..], Crc32.Compute(_block.AsSpan(0, _fill)));
        var outcome = await _sink.WriteBlockAsync(_block.AsMemory(0, _fill), cancellationToken).ConfigureAwait(false);
        EarlyWarning |= outcome.EarlyWarning;
        BytesWritten += _fill;
        _blockNumber++;
        _fill = TapeFormat.BlockHeaderSize;
    }
}

/// <param name="Sha256">Of the records before the trailer, hex.</param>
public sealed record TapeFileSummary(ulong FileId, uint Blocks, long Bytes, long Records, string Sha256);

/// <summary>
/// Reads one file from the current position: from its first block (after a filemark or a LOCATE), or from a record in
/// its middle (<see cref="ReadFromAsync"/>). Checks every block (magic, version, CRC, file, numbering) and, read from the
/// start, the trailer; a filemark or the end of data before the last block means the file is torn.
/// </summary>
public sealed class TapeFileReader(ITapeBlockReader source, int maxBlockSize, ITapeRecordCipher? cipher = null)
{
    /// <summary>The file's header, once its first record was read.</summary>
    public TapeFileHeader? Header { get; private set; }

    /// <summary>Every record of the file, the header included, the trailer checked but not returned. Encrypted bodies come opened.</summary>
    public async IAsyncEnumerable<TapeRecord> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long records = 0;
        var stream = new BlockStream(source, maxBlockSize, null, 0, 0);
        while (true)
        {
            var position = stream.Position;
            var header = await stream.ReadExactlyAsync(TapeFormat.RecordHeaderSize, cancellationToken).ConfigureAwait(false);
            var (type, flags, length) = ParseRecordHeader(header);
            var body = await stream.ReadExactlyAsync(checked((int)length), cancellationToken).ConfigureAwait(false);
            if (type == TapeFormat.Trailer)
            {
                var expected = hash.GetHashAndReset();
                if (body.Length != 40 || BinaryPrimitives.ReadInt64BigEndian(body) != records || !body.AsSpan(8).SequenceEqual(expected))
                {
                    throw new InvalidDataException($"File {stream.FileId:X16}: its trailer does not match its records (count or SHA-256).");
                }

                if (!stream.AtLastBlockEnd)
                {
                    throw new InvalidDataException($"File {stream.FileId:X16}: data after its trailer.");
                }

                await stream.ExpectFilemarkAsync(cancellationToken).ConfigureAwait(false);
                yield break;
            }

            hash.AppendData(header);
            hash.AppendData(body);
            records++;
            var record = Open(new TapeRecord(type, flags, body, position), header, stream.FileId);
            if (records == 1)
            {
                Header = record.Type == TapeFormat.FileHeader
                    ? TapeJson.Deserialize<TapeFileHeader>(record.Body)
                    : throw new InvalidDataException($"File {stream.FileId:X16} does not start with a header record.");
            }

            yield return record;
        }
    }

    /// <summary>
    /// Reads records starting at <paramref name="position"/> of the file <paramref name="fileId"/>; the tape must stand at
    /// that block. Stops at the trailer; nothing before the position is checked.
    /// </summary>
    public async IAsyncEnumerable<TapeRecord> ReadFromAsync(ulong fileId, RecordPosition position, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = new BlockStream(source, maxBlockSize, fileId, position.Block, position.Offset);
        while (true)
        {
            var at = stream.Position;
            var header = await stream.ReadExactlyAsync(TapeFormat.RecordHeaderSize, cancellationToken).ConfigureAwait(false);
            var (type, flags, length) = ParseRecordHeader(header);
            if (type == TapeFormat.Trailer)
            {
                yield break;
            }

            var body = await stream.ReadExactlyAsync(checked((int)length), cancellationToken).ConfigureAwait(false);
            yield return Open(new TapeRecord(type, flags, body, at), header, fileId);
        }
    }

    private TapeRecord Open(TapeRecord record, byte[] header, ulong fileId)
    {
        if (!record.Flags.HasFlag(RecordFlags.Encrypted))
        {
            return record;
        }

        if (cipher is null)
        {
            throw new CryptographicException($"Record {record.Type} of file {fileId:X16} is encrypted; the key is needed to read it.");
        }

        return record with { Body = cipher.Open(header, fileId, record.Body) };
    }

    private static (string Type, RecordFlags Flags, long Length) ParseRecordHeader(ReadOnlySpan<byte> header)
    {
        var type = Encoding.ASCII.GetString(header[..4]);
        var flags = (RecordFlags)BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var length = BinaryPrimitives.ReadInt64BigEndian(header[8..]);
        if (!Ascii.IsValid(header[..4]) || length is < 0 or > 1L << 31 || (flags & ~RecordFlags.Encrypted) != 0)
        {
            throw new InvalidDataException($"A record header is damaged (type '{type}', length {length}, flags 0x{(uint)flags:X}).");
        }

        return (type, flags, length);
    }

    /// <summary>The payloads of a file's blocks as one stream.</summary>
    private sealed class BlockStream(ITapeBlockReader source, int maxBlockSize, ulong? fileId, uint firstBlock, int skip)
    {
        private readonly byte[] _block = new byte[maxBlockSize];
        private int _payloadStart;
        private int _payloadEnd;
        private int _read;
        private uint _nextBlock = firstBlock;
        private bool _last;
        private int _skip = skip;
        private bool _started;

        public ulong FileId { get; private set; } = fileId ?? 0;

        /// <summary>Where the next byte is: in the current block, or at the start of the next one when this one is used up.</summary>
        public RecordPosition Position
        {
            get
            {
                if (!_started)
                {
                    return new RecordPosition(_nextBlock, _skip);
                }

                return _read == _payloadEnd && !_last
                    ? new RecordPosition(_nextBlock, 0)
                    : new RecordPosition(_nextBlock - 1, _read - _payloadStart);
            }
        }

        public bool AtLastBlockEnd => _last && _read == _payloadEnd;

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var result = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                if (_read == _payloadEnd)
                {
                    if (_last)
                    {
                        throw new InvalidDataException($"File {FileId:X16} ends in the middle of a record.");
                    }

                    await NextBlockAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var n = Math.Min(count - filled, _payloadEnd - _read);
                _block.AsSpan(_read, n).CopyTo(result.AsSpan(filled));
                _read += n;
                filled += n;
            }

            return result;
        }

        public async Task ExpectFilemarkAsync(CancellationToken cancellationToken)
        {
            var outcome = await source.ReadBlockAsync(_block, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind != ReadOutcomeKind.Filemark)
            {
                throw new InvalidDataException($"File {FileId:X16}: no filemark after its last block.");
            }
        }

        private async Task NextBlockAsync(CancellationToken cancellationToken)
        {
            var outcome = await source.ReadBlockAsync(_block, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind != ReadOutcomeKind.Block)
            {
                throw new TornFileException(
                    $"File {FileId:X16} is torn: {(outcome.Kind == ReadOutcomeKind.Filemark ? "a filemark" : "the end of data")} after block {_nextBlock}, before its trailer.");
            }

            var block = _block.AsSpan(0, outcome.Length);
            if (block.Length < TapeFormat.BlockHeaderSize || !block[..4].SequenceEqual(TapeFormat.BlockMagic))
            {
                throw new InvalidDataException($"Block {_nextBlock} of file {FileId:X16} is not a backdafuckup tape block.");
            }

            if (block[4] > TapeFormat.Version)
            {
                throw new NotSupportedException($"The tape was written in format {block[4]}; this version reads up to {TapeFormat.Version}. Update backdafuckup to read it.");
            }

            var payload = (int)BinaryPrimitives.ReadUInt32BigEndian(block[20..]);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(block[24..]);
            if (TapeFormat.BlockHeaderSize + payload != block.Length)
            {
                throw new InvalidDataException($"Block {_nextBlock} of file {FileId:X16}: length {block.Length} does not match its header.");
            }

            BinaryPrimitives.WriteUInt32BigEndian(block[24..], 0);
            if (Crc32.Compute(block) != crc)
            {
                throw new InvalidDataException($"Block {_nextBlock} of file {FileId:X16} is damaged (CRC mismatch).");
            }

            var id = BinaryPrimitives.ReadUInt64BigEndian(block[8..]);
            var number = BinaryPrimitives.ReadUInt32BigEndian(block[16..]);
            if (!_started && fileId is null)
            {
                FileId = id;
            }

            if (id != FileId || number != _nextBlock)
            {
                throw new InvalidDataException($"Expected block {_nextBlock} of file {FileId:X16}, found block {number} of file {id:X16}.");
            }

            _started = true;
            _last = (block[5] & 1) != 0;
            _payloadStart = TapeFormat.BlockHeaderSize;
            _payloadEnd = block.Length;
            _read = _payloadStart + _skip;
            _skip = 0;
            if (_read > _payloadEnd)
            {
                throw new InvalidDataException($"Block {number} of file {FileId:X16} has no offset {_read - _payloadStart}.");
            }

            _nextBlock++;
        }
    }
}

/// <summary>CRC-32 (IEEE 802.3, reflected, polynomial EDB88320h) over a block with its CRC field zero.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
