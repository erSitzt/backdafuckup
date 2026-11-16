using System.Security.Cryptography;
using Backdafuckup.Core.Repository;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Format;

namespace Backdafuckup.Tape.Tests;

/// <summary>The on-tape format (docs/tape-format.md, version 1) against a tape in memory.</summary>
public sealed class TapeFormatTests
{
    private const int BlockSize = 64 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Records_run_across_blocks_and_read_back_with_their_positions()
    {
        var tape = new MemoryTape();
        var bodies = Enumerable.Range(0, 40).Select(i => RandomNumberGenerator.GetBytes(i * 7919 % 90_000 + 1)).ToList();
        var writer = await TapeFileWriter.BeginAsync(tape, BlockSize, Header(TapeFileTypes.Archive, 7), null, Ct);
        var positions = new List<RecordPosition>();
        foreach (var body in bodies)
        {
            positions.Add(await writer.WriteRecordAsync(TapeFormat.Chunk, body, encrypt: false, Ct));
        }

        var summary = await writer.FinishAsync(synchronize: true, Ct);
        Assert.Equal(41, summary.Records);
        Assert.Equal((int)summary.Blocks, tape.Files[0].Count);
        Assert.True(summary.Blocks > 20);
        Assert.All(tape.Files[0].Take(tape.Files[0].Count - 1), b => Assert.Equal(BlockSize, b.Length));

        tape.Rewind();
        var reader = new TapeFileReader(tape, BlockSize);
        var records = await reader.ReadAllAsync(Ct).ToListAsync(Ct);
        Assert.Equal((TapeFileTypes.Archive, 7UL), (reader.Header!.Type, reader.Header.FileId));
        Assert.Equal(bodies, records.Skip(1).Select(r => r.Body));
        Assert.Equal(positions, records.Skip(1).Select(r => r.Position));

        // From the middle: the block a record starts in, then its offset.
        var at = positions[23];
        tape.Locate(file: 0, block: (int)at.Block);
        var from = await new TapeFileReader(tape, BlockSize).ReadFromAsync(7, at, Ct).Take(2).ToListAsync(Ct);
        Assert.Equal(bodies[23..25], from.Select(r => r.Body));
    }

    [Fact]
    public async Task A_file_without_its_trailer_is_torn_and_damage_is_found()
    {
        // Cut off: the filemark came before the trailer.
        var tape = new MemoryTape();
        var writer = await TapeFileWriter.BeginAsync(tape, BlockSize, Header(TapeFileTypes.RestorePoint, 9), null, Ct);
        await writer.WriteRecordAsync(TapeFormat.Entry, RandomNumberGenerator.GetBytes(3 * BlockSize), encrypt: false, Ct);
        await tape.WriteFilemarkAsync(synchronize: false, Ct);
        tape.Rewind();
        await Assert.ThrowsAsync<TornFileException>(async () => await new TapeFileReader(tape, BlockSize).ReadAllAsync(Ct).ToListAsync(Ct));

        // A flipped byte in a block: its CRC no longer fits.
        var good = await WriteFileAsync(new MemoryTape(), 11);
        good.Files[0][1][100] ^= 1;
        good.Rewind();
        var crc = await Assert.ThrowsAsync<InvalidDataException>(async () => await new TapeFileReader(good, BlockSize).ReadAllAsync(Ct).ToListAsync(Ct));
        Assert.Contains("CRC", crc.Message, StringComparison.Ordinal);

        // A newer format is refused, not misread.
        var newer = await WriteFileAsync(new MemoryTape(), 12);
        newer.Files[0][0][4] = TapeFormat.Version + 1;
        newer.Rewind();
        var version = await Assert.ThrowsAsync<NotSupportedException>(async () => await new TapeFileReader(newer, BlockSize).ReadAllAsync(Ct).ToListAsync(Ct));
        Assert.Contains($"format {TapeFormat.Version + 1}", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocks_read_as_long_as_the_buffer_are_cut_to_their_header()
    {
        // Holo over iSCSI reports no residual: a short block comes back as long as the read buffer.
        var tape = new MemoryTape { PadReadsToBuffer = true };
        var writer = await TapeFileWriter.BeginAsync(tape, BlockSize, Header(TapeFileTypes.Label, 3), null, Ct);
        await writer.WriteRecordAsync(TapeFormat.Label, "{}"u8.ToArray(), encrypt: false, Ct);
        await writer.FinishAsync(synchronize: true, Ct);
        tape.Rewind();
        var records = await new TapeFileReader(tape, 1024 * 1024).ReadAllAsync(Ct).ToListAsync(Ct);
        Assert.Equal([TapeFormat.FileHeader, TapeFormat.Label], records.Select(r => r.Type));
    }

    [Fact]
    public async Task Chunk_locations_entries_and_the_catalog_round_trip()
    {
        var locations = Enumerable.Range(0, 1000).Select(i => new ChunkLocation(ChunkId.Compute(BitConverter.GetBytes(i)), 1 + (i % 3), i % 17, (uint)i * 3, i % 4000, 70_000 + i)).ToList();
        var decoded = ChunkLocations.Decode(ChunkLocations.Encode(locations));
        Assert.Equal(locations.OrderBy(l => l.Id), decoded);

        var (name, content) = TapeEntries.Decode(TapeEntries.Encode("disk-xvda.idx", [1, 2, 3]));
        Assert.Equal("disk-xvda.idx", name);
        Assert.Equal(new byte[] { 1, 2, 3 }, content);

        var catalog = new TapeCatalog
        {
            SetId = Guid.NewGuid(), PoolId = Guid.NewGuid(), PoolName = "Weekly", SetCreatedUtc = DateTimeOffset.UnixEpoch, WrittenUtc = DateTimeOffset.UnixEpoch,
            Media = [new TapeCatalogMedium(1, Guid.NewGuid(), "BDF000L9", "BDF000L9")],
            Files = [new TapeCatalogFile(1, 1, 3, 77, TapeFileTypes.Archive, 10, 655360, null)],
            Copies = [],
        };
        Assert.Equal(catalog.Files, TapeJson.DeserializeCompressed<TapeCatalog>(TapeJson.SerializeCompressed(catalog)).Files);
    }

    [Fact]
    public async Task Files_go_through_a_drive_of_the_fake_library()
    {
        var library = new Backdafuckup.Testing.Tape.FakeTapeLibrary();
        var cartridge = library.AddCartridge("BDF000L9", 64L << 20);
        library.Drives[0].OperatorInsert(cartridge);
        var drive = new TapeDrive(new Backdafuckup.Tape.Scsi.ScsiCommandRunner(library.Drives[0], "drive 1")) { PollInterval = TimeSpan.FromMilliseconds(1) };
        await drive.WaitReadyAsync(TimeSpan.FromMinutes(1), Ct);
        await drive.ReadMediumAsync(Ct);
        await drive.PrepareAsync(Ct);
        var blocks = new DriveBlocks(drive);
        var bodies = new List<byte[]>();
        for (var f = 0; f < 3; f++)
        {
            var writer = await TapeFileWriter.BeginAsync(blocks, BlockSize, Header(TapeFileTypes.Archive, (ulong)f + 1), null, Ct);
            var body = RandomNumberGenerator.GetBytes(150_000);
            bodies.Add(body);
            await writer.WriteRecordAsync(TapeFormat.Chunk, body, encrypt: false, Ct);
            await writer.FinishAsync(synchronize: f == 2, Ct);
        }

        await drive.RewindAsync(Ct);
        for (var f = 0; f < 3; f++)
        {
            var records = await new TapeFileReader(blocks, BlockSize).ReadAllAsync(Ct).ToListAsync(Ct);
            Assert.Equal(bodies[f], records[1].Body);
        }
    }

    private static TapeFileHeader Header(string type, ulong id) => new() { Type = type, FileId = id, Sequence = 1, FileNumber = 1, CreatedUtc = DateTimeOffset.UnixEpoch };

    private static async Task<MemoryTape> WriteFileAsync(MemoryTape tape, ulong id)
    {
        var writer = await TapeFileWriter.BeginAsync(tape, BlockSize, Header(TapeFileTypes.Archive, id), null, Ct);
        await writer.WriteRecordAsync(TapeFormat.Chunk, RandomNumberGenerator.GetBytes(2 * BlockSize), encrypt: false, Ct);
        await writer.FinishAsync(synchronize: false, Ct);
        return tape;
    }
}

/// <summary>A tape in memory: files of blocks, a read position, early warning after a number of bytes.</summary>
internal sealed class MemoryTape : ITapeBlockWriter, ITapeBlockReader
{
    private int _file;
    private int _block;

    public List<List<byte[]>> Files { get; } = [[]];

    public long EarlyWarningAfter { get; set; } = long.MaxValue;

    public long Written { get; private set; }

    /// <summary>Reports every block as long as the read buffer (no residual), as Holo over iSCSI does.</summary>
    public bool PadReadsToBuffer { get; set; }

    public Task<WriteOutcome> WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        Files[^1].Add(block.ToArray());
        Written += block.Length;
        return Task.FromResult(new WriteOutcome(Written >= EarlyWarningAfter));
    }

    public Task<WriteOutcome> WriteFilemarkAsync(bool synchronize, CancellationToken cancellationToken)
    {
        Files.Add([]);
        return Task.FromResult(new WriteOutcome(Written >= EarlyWarningAfter));
    }

    public Task<ReadOutcome> ReadBlockAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_file >= Files.Count || (_file == Files.Count - 1 && _block >= Files[_file].Count))
        {
            return Task.FromResult(new ReadOutcome(ReadOutcomeKind.EndOfData, 0));
        }

        if (_block >= Files[_file].Count)
        {
            _file++;
            _block = 0;
            return Task.FromResult(new ReadOutcome(ReadOutcomeKind.Filemark, 0));
        }

        var block = Files[_file][_block++];
        destination.Span.Clear();
        block.CopyTo(destination);
        return Task.FromResult(new ReadOutcome(ReadOutcomeKind.Block, PadReadsToBuffer ? destination.Length : block.Length));
    }

    public void Rewind() => Locate(0, 0);

    public void Locate(int file, int block)
    {
        _file = file;
        _block = block;
    }
}
