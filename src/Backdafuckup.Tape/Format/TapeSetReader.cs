using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Repository;
using Backdafuckup.Tape.Devices;

namespace Backdafuckup.Tape.Format;

/// <summary>A file found on a tape: its number, its first block and its header.</summary>
public sealed record TapeFileFound(int FileNumber, long StartBlock, TapeFileHeader Header);

/// <summary>Positions a tape of a set at a block: the server loads it with the changer, the recovery command asks for it.</summary>
public interface ITapeVolumes
{
    Task<DriveBlocks> AtAsync(int sequence, long block, CancellationToken cancellationToken);
}

/// <summary>
/// Reads sets without the server's database: the files on a tape (their headers), the newest complete catalog, and a
/// copy back into a repository. The server and <c>bdfu-tape-recover</c> share it.
/// </summary>
public static class TapeSetReader
{
    /// <summary>Every file's header, from the beginning of the tape to the end of data (one block read and one filemark spaced per file).</summary>
    public static async Task<IReadOnlyList<TapeFileFound>> ScanAsync(TapeDrive drive, int blockSize, CancellationToken cancellationToken)
    {
        var result = new List<TapeFileFound>();
        await drive.RewindAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Max(blockSize, 1024 * 1024)];
        for (var number = 0; ; number++)
        {
            var start = (await drive.ReadPositionAsync(cancellationToken).ConfigureAwait(false)).Block;
            var first = await drive.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (first.Kind == ReadOutcomeKind.EndOfData)
            {
                return result;
            }

            if (first.Kind == ReadOutcomeKind.Filemark)
            {
                continue;
            }

            if (ParseHeader(buffer.AsSpan(0, first.Length)) is { } header)
            {
                result.Add(new TapeFileFound(number, start, header));
            }

            try
            {
                await drive.SpaceFilemarksAsync(1, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return result; // The last file has no filemark: it is torn, the end of the tape.
            }
        }
    }

    /// <summary>The newest catalog on the tape that reads complete (an older one when the newest is torn).</summary>
    public static async Task<TapeCatalog?> ReadCatalogAsync(TapeDrive drive, IReadOnlyList<TapeFileFound> files, int blockSize, ITapeRecordCipher? cipher, CancellationToken cancellationToken)
    {
        foreach (var file in files.Where(f => f.Header.Type == TapeFileTypes.Catalog).OrderByDescending(f => f.FileNumber))
        {
            await drive.LocateBlockAsync(file.StartBlock, cancellationToken).ConfigureAwait(false);
            try
            {
                await foreach (var record in new TapeFileReader(new DriveBlocks(drive), blockSize, cipher).ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (record.Type == TapeFormat.Catalog)
                    {
                        return TapeJson.DeserializeCompressed<TapeCatalog>(record.Body);
                    }
                }
            }
            catch (Exception ex) when (ex is TornFileException or InvalidDataException)
            {
                // Torn or damaged: the one before.
            }
        }

        return null;
    }

    /// <summary>
    /// Brings a copy back into <paramref name="target"/> under <paramref name="newId"/>: its restore-point file, then its
    /// chunks tape by tape and file by file (from the first needed record of a file to its last), stored unchanged after
    /// checking. <paramref name="fileOf"/> gives a file's first block and id from the set's catalog. <paramref name="progress"/>
    /// is told the stored bytes of the chunks done so far and of all the copy needs.
    /// </summary>
    public static async Task<TapeRestoreResult> RestoreAsync(
        ITapeVolumes tapes,
        int blockSize,
        ITapeRecordCipher? cipher,
        int sequence,
        long startBlock,
        Func<int, int, (long StartBlock, ulong FileId)> fileOf,
        IRepositoryTransfer target,
        Guid newId,
        Func<RestorePointManifest, RestorePointManifest>? adjust,
        Action<string> log,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var (entries, locations) = await ReadCopyFileAsync(tapes, blockSize, cipher, sequence, startBlock, cancellationToken).ConfigureAwait(false);
        var manifestJson = entries.FirstOrDefault(e => e.Name == "manifest.json").Content ?? throw new InvalidDataException("The restore-point file has no manifest.");
        var manifest = JsonSerializer.Deserialize(manifestJson, CoreJsonContext.Default.RestorePointManifest)!;
        log($"Restore-point file of {manifest.Vm.Name} read from tape #{sequence}: {entries.Count} entries, {locations.Count} chunk(s) on tape(s) {string.Join(", ", locations.Select(l => l.Sequence).Distinct().Order().Select(s => $"#{s}"))}.");

        await using var import = target.BeginImport(newId, manifest);
        var total = locations.Sum(l => (long)l.Length);
        progress?.Invoke(0, total);
        var chunks = await ReadChunksAsync(
            tapes, blockSize, cipher, locations, fileOf, import.PutEncodedChunkAsync, manifest.Vm.Name, progress is null ? null : done => progress(done, total), cancellationToken).ConfigureAwait(false);
        foreach (var (name, content) in entries.Where(e => e.Name is not ("copy.json" or "manifest.json")))
        {
            await import.WriteFileAsync(name, content, cancellationToken).ConfigureAwait(false);
        }

        var committed = await import.CommitAsync(adjust, cancellationToken).ConfigureAwait(false);
        return new TapeRestoreResult(committed, chunks, locations.Select(l => l.Sequence).Distinct().Count());
    }

    /// <summary>Brings a copy of a SQL Server backup object back, like <see cref="RestoreAsync"/>.</summary>
    public static async Task<(SqlBackupInfo Committed, long Chunks)> RestoreSqlAsync(
        ITapeVolumes tapes,
        int blockSize,
        ITapeRecordCipher? cipher,
        int sequence,
        long startBlock,
        Func<int, int, (long StartBlock, ulong FileId)> fileOf,
        IRepositoryTransfer target,
        Guid newId,
        Func<SqlBackupManifest, SqlBackupManifest>? adjust,
        CancellationToken cancellationToken)
    {
        var (entries, locations) = await ReadCopyFileAsync(tapes, blockSize, cipher, sequence, startBlock, cancellationToken).ConfigureAwait(false);
        var manifestJson = entries.FirstOrDefault(e => e.Name == "manifest.json").Content ?? throw new InvalidDataException("The SQL backup file has no manifest.");
        var manifest = JsonSerializer.Deserialize(manifestJson, SqlBackupJsonContext.Default.SqlBackupManifest)!;
        await using var import = target.BeginSqlImport(newId, manifest);
        var chunks = await ReadChunksAsync(tapes, blockSize, cipher, locations, fileOf, import.PutEncodedChunkAsync, $"SQL backup {manifest.SqlBackupId}", null, cancellationToken).ConfigureAwait(false);
        foreach (var (name, content) in entries.Where(e => e.Name is not ("copy.json" or "manifest.json")))
        {
            await import.WriteFileAsync(name, content, cancellationToken).ConfigureAwait(false);
        }

        return (await import.CommitAsync(adjust, cancellationToken).ConfigureAwait(false), chunks);
    }

    /// <summary>A copy's file on its own (a catalog rebuild): its entries (<c>copy.json</c>, the manifest, …) and where its chunks are.</summary>
    public static Task<(List<(string Name, byte[] Content)> Entries, IReadOnlyList<ChunkLocation> Locations)> ReadCopyAsync(
        ITapeVolumes tapes, int blockSize, ITapeRecordCipher? cipher, int sequence, long startBlock, CancellationToken cancellationToken) =>
        ReadCopyFileAsync(tapes, blockSize, cipher, sequence, startBlock, cancellationToken);

    /// <summary>A copy's file: its entries (files byte for byte) and where its chunks are.</summary>
    private static async Task<(List<(string Name, byte[] Content)> Entries, IReadOnlyList<ChunkLocation> Locations)> ReadCopyFileAsync(
        ITapeVolumes tapes, int blockSize, ITapeRecordCipher? cipher, int sequence, long startBlock, CancellationToken cancellationToken)
    {
        var entries = new List<(string Name, byte[] Content)>();
        IReadOnlyList<ChunkLocation> locations = [];
        var reader = await tapes.AtAsync(sequence, startBlock, cancellationToken).ConfigureAwait(false);
        await foreach (var record in new TapeFileReader(reader, blockSize, cipher).ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.Type == TapeFormat.Entry)
            {
                entries.Add(TapeEntries.Decode(record.Body));
            }
            else if (record.Type == TapeFormat.Locations)
            {
                locations = ChunkLocations.Decode(record.Body);
            }
        }

        return (entries, locations);
    }

    /// <summary>The chunks at <paramref name="locations"/>, tape by tape and file by file, from the first needed record of a file to its last.</summary>
    private static async Task<long> ReadChunksAsync(
        ITapeVolumes tapes,
        int blockSize,
        ITapeRecordCipher? cipher,
        IReadOnlyList<ChunkLocation> locations,
        Func<int, int, (long StartBlock, ulong FileId)> fileOf,
        Func<ChunkId, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ChunkPutResult>> put,
        string what,
        Action<long>? stored,
        CancellationToken cancellationToken)
    {
        long chunks = 0;
        long bytes = 0;
        foreach (var group in locations.GroupBy(l => (l.Sequence, l.FileNumber)).OrderBy(g => g.Key.Sequence).ThenBy(g => g.Key.FileNumber))
        {
            var (fileStart, fileId) = fileOf(group.Key.Sequence, group.Key.FileNumber);
            var needed = group.ToDictionary(l => l.Id);
            var first = group.OrderBy(l => l.Block).ThenBy(l => l.Offset).First();
            var blocks = await tapes.AtAsync(group.Key.Sequence, fileStart + first.Block, cancellationToken).ConfigureAwait(false);
            await foreach (var record in new TapeFileReader(blocks, blockSize, cipher).ReadFromAsync(fileId, new RecordPosition(first.Block, first.Offset), cancellationToken).ConfigureAwait(false))
            {
                if (record.Type != TapeFormat.Chunk)
                {
                    continue;
                }

                var (id, body) = TapeEntries.DecodeChunk(record.Body);
                if (needed.Remove(id, out var location))
                {
                    await put(id, body, cancellationToken).ConfigureAwait(false);
                    chunks++;
                    bytes += location.Length;
                    stored?.Invoke(bytes);
                    if (needed.Count == 0)
                    {
                        break;
                    }
                }
            }

            if (needed.Count > 0)
            {
                throw new InvalidDataException($"{needed.Count} chunk(s) of {what} were not in tape #{group.Key.Sequence} file {group.Key.FileNumber}.");
            }
        }

        return chunks;
    }

    /// <summary>The header record of a file from its first block, or null when the block is not ours.</summary>
    public static TapeFileHeader? ParseHeader(ReadOnlySpan<byte> block)
    {
        if (block.Length < TapeFormat.BlockHeaderSize + TapeFormat.RecordHeaderSize || !block[..4].SequenceEqual("BDTB"u8))
        {
            return null;
        }

        var payload = (int)BinaryPrimitives.ReadUInt32BigEndian(block[20..]);
        var record = block.Slice(TapeFormat.BlockHeaderSize, Math.Min(payload, block.Length - TapeFormat.BlockHeaderSize));
        if (record.Length < TapeFormat.RecordHeaderSize || Encoding.ASCII.GetString(record[..4]) != TapeFormat.FileHeader)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt64BigEndian(record[8..]);
        if (length <= 0 || TapeFormat.RecordHeaderSize + length > record.Length)
        {
            return null;
        }

        try
        {
            return TapeJson.Deserialize<TapeFileHeader>(record.Slice(TapeFormat.RecordHeaderSize, (int)length));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record TapeRestoreResult(ManifestInfo Committed, long Chunks, int Tapes);
