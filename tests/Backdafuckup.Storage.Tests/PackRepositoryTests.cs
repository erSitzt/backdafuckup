using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Repository;
using Microsoft.Extensions.Time.Testing;
using static Backdafuckup.Storage.Tests.LocalDirectoryRepositoryTests;

namespace Backdafuckup.Storage.Tests;

/// <summary>
/// Repository format 2 (spec 2026-11-16-1804-repository-pack-files): packs and their index survive crashes and damage,
/// writers that share chunks commit safely, GC repacks, format 1 repositories convert, and chunks read in storage order.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PackRepositoryTests : IDisposable
{
    private const long SmallPacks = 4 * (ChunkSize + 1024);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-pack-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow.AddHours(1));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Writes_fill_several_packs_and_read_back_after_reopening()
    {
        var disk = RandomDisk(11);
        Guid id;
        await using (var repo = await CreateAsync())
        {
            (id, _) = await BackupAsync(repo, "vm-1", disk);
            Assert.Equal(2, repo.FormatVersion);
        }

        Assert.True(Packs().Count >= 3);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "incoming")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_root, "index")));
        Assert.False(Directory.Exists(Path.Combine(_root, "chunks")));
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "repo.json"), Ct))!["formatVersion"]!.GetValue<int>());

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Empty(reopened.OpenReport);
        await using var reader = await reopened.OpenRestorePointAsync(id, Ct);
        Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
        Assert.True((await reopened.VerifyAsync(id, VerifyDepth.Full, null, Ct)).Ok);
    }

    [Fact]
    public async Task An_unsealed_pack_of_an_interrupted_write_is_removed_when_the_repository_opens()
    {
        await using (var repo = await CreateAsync())
        {
            await BackupAsync(repo, "vm-1", RandomDisk(2));
        }

        var stray = Path.Combine(_root, "incoming", Guid.NewGuid().ToString("N") + ".pack");
        await File.WriteAllBytesAsync(stray, new byte[100], Ct);

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.False(File.Exists(stray));
        Assert.Contains(reopened.OpenReport, r => r.Contains("unsealed pack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Packs_the_index_does_not_cover_are_indexed_from_their_trailers()
    {
        var disk = RandomDisk(9);
        Guid id;
        await using (var repo = await CreateAsync())
        {
            (id, _) = await BackupAsync(repo, "vm-1", disk);
        }

        // As after a crash between sealing packs and writing the index.
        foreach (var segment in Directory.EnumerateFiles(Path.Combine(_root, "index")))
        {
            File.Delete(segment);
        }

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Contains(reopened.OpenReport, r => r.Contains("did not cover", StringComparison.Ordinal));
        await using var reader = await reopened.OpenRestorePointAsync(id, Ct);
        Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
    }

    [Fact]
    public async Task A_damaged_index_is_rebuilt_from_the_packs()
    {
        var disk = RandomDisk(6);
        Guid id;
        await using (var repo = await CreateAsync())
        {
            (id, _) = await BackupAsync(repo, "vm-1", disk);
        }

        var segment = Directory.EnumerateFiles(Path.Combine(_root, "index")).First();
        var bytes = await File.ReadAllBytesAsync(segment, Ct);
        bytes[10] ^= 0x5A;
        await File.WriteAllBytesAsync(segment, bytes, Ct);

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Contains(reopened.OpenReport, r => r.Contains("rebuilt from the packs", StringComparison.Ordinal) && r.Contains(Path.Combine(_root, "index"), StringComparison.Ordinal));
        Assert.True((await reopened.VerifyAsync(id, VerifyDepth.Full, null, Ct)).Ok);
    }

    [Fact]
    public async Task A_pack_with_a_damaged_trailer_is_kept_reported_and_its_chunks_count_as_missing()
    {
        Guid id;
        await using (var repo = await CreateAsync())
        {
            (id, _) = await BackupAsync(repo, "vm-1", RandomDisk(9));
        }

        foreach (var segment in Directory.EnumerateFiles(Path.Combine(_root, "index")))
        {
            File.Delete(segment);
        }

        var pack = Packs()[0];
        var bytes = await File.ReadAllBytesAsync(pack, Ct);
        bytes[^70] ^= 0xFF; // inside the trailer
        await File.WriteAllBytesAsync(pack, bytes, Ct);

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Contains(reopened.OpenReport, r => r.Contains(pack, StringComparison.Ordinal) && r.Contains("damaged trailer", StringComparison.Ordinal));
        Assert.True(File.Exists(pack));
        var quick = await reopened.VerifyAsync(id, VerifyDepth.Quick, null, Ct);
        Assert.False(quick.Ok);
        Assert.Contains(quick.Problems, p => p.Contains("missing", StringComparison.Ordinal));

        // The records themselves are intact: a salvage scan still reads them.
        using var handle = File.OpenHandle(pack);
        Assert.NotEmpty(PackFormat.Salvage(handle, ChunkSize));
    }

    [Fact]
    public async Task Verify_names_the_pack_and_offset_of_a_damaged_chunk_and_a_pack_that_is_gone()
    {
        Guid id;
        string other;
        await using (var repo = await CreateAsync())
        {
            (id, _) = await BackupAsync(repo, "vm-1", RandomDisk(9));
            var plan = repo.PlanRead((await (await repo.OpenRestorePointAsync(id, Ct)).ReadDiskIndexAsync("0", Ct)).Entries);
            var first = plan.Chunks[0];
            var path = plan.Sources[first.Source];
            var bytes = await File.ReadAllBytesAsync(path, Ct);
            bytes[first.Offset + first.StoredLength - 1] ^= 0xFF;
            await File.WriteAllBytesAsync(path, bytes, Ct);

            var full = await repo.VerifyAsync(id, VerifyDepth.Full, null, Ct);
            var problem = Assert.Single(full.Problems);
            Assert.Contains($"pack '{path}', offset {first.Offset}", problem, StringComparison.Ordinal);

            other = plan.Sources[plan.Chunks[^1].Source];
            Assert.NotEqual(path, other);
        }

        // A pack that disappeared while the server was down: named when the repository opens, its chunks missing.
        File.Delete(other);
        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Contains(reopened.OpenReport, r => r.Contains(Path.GetFileName(other), StringComparison.Ordinal) && r.Contains("missing", StringComparison.Ordinal));
        var quick = await reopened.VerifyAsync(id, VerifyDepth.Quick, null, Ct);
        Assert.Contains(quick.Problems, p => p.Contains("missing", StringComparison.Ordinal) && p.Contains(_root, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_writer_that_shares_chunks_with_another_writers_open_pack_seals_it_before_committing()
    {
        var disk = RandomDisk(2);
        Guid committed;
        await using (var repo = await CreateAsync(packTargetSize: 64L * 1024 * 1024))
        {
            // Writer A stores the chunks into its open pack and never commits.
            var a = repo.BeginWrite(Guid.CreateVersion7(), "vm-a", null);
            for (var i = 0; i < 2; i++)
            {
                var data = disk.AsMemory(i * ChunkSize, ChunkSize);
                await a.PutChunkAsync(ChunkId.Compute(data.Span), data, Ct);
            }

            // Writer B deduplicates against A's open pack and commits first: that must seal and index A's pack.
            var (id, stats) = await BackupAsync(repo, "vm-b", disk);
            committed = id;
            Assert.Equal(2, stats.Deduplicated);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "incoming")));
            await a.DisposeAsync();
        }

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        await using var reader = await reopened.OpenRestorePointAsync(committed, Ct);
        Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
    }

    [Fact]
    public async Task Gc_repacks_sparse_packs_deletes_empty_ones_and_keeps_restores_working_while_it_runs()
    {
        await using var repo = await CreateAsync();
        var keep = RandomDisk(8);
        var (kept, _) = await BackupAsync(repo, "vm-1", keep);
        var (gone, _) = await BackupAsync(repo, "vm-2", RandomDisk(8));
        var mixed = (byte[])keep.Clone();
        RandomNumberGenerator.Fill(mixed.AsSpan(0, ChunkSize * 3));
        var (alsoGone, _) = await BackupAsync(repo, "vm-3", mixed);
        var packsBefore = Packs().Count;

        await repo.DeleteRestorePointAsync(gone, Ct);
        await repo.DeleteRestorePointAsync(alsoGone, Ct);

        using var stop = new CancellationTokenSource();
        var restores = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var reader = await repo.OpenRestorePointAsync(kept, Ct);
                Assert.Equal(keep, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
            }
        }, Ct);

        var gc = await repo.CollectGarbageAsync(null, Ct);
        await stop.CancelAsync();
        await restores;

        Assert.Equal(11, gc.DeletedChunks);
        Assert.True(gc.PacksDeleted >= 2);
        Assert.True(gc.FreedBytes > 10L * ChunkSize);
        Assert.Equal(8, repo.CountStoredChunks());
        Assert.True(Packs().Count < packsBefore);
        Assert.True((await repo.VerifyAsync(kept, VerifyDepth.Full, null, Ct)).Ok);

        // Nothing left to do: a second run deletes nothing.
        Assert.Equal(0, (await repo.CollectGarbageAsync(null, Ct)).DeletedChunks);
    }

    [Fact]
    public async Task A_format_1_repository_converts_to_packs_and_keeps_every_restore_point_sql_backup_and_tree()
    {
        var disk = RandomDisk(6);
        var log = RandomNumberGenerator.GetBytes(ChunkSize * 3);
        Guid rp, sql, tree;
        await using (var repo = await CreateAsync(packTargetSize: 0))
        {
            Assert.Equal(1, repo.FormatVersion);
            (rp, _) = await BackupAsync(repo, "vm-1", disk);
            sql = await SqlBackupObjectTests.WriteAsync(repo, "vm-1", ("0", "shop", log));
            (tree, _) = await FileTreeTests.WriteTreeAsync(repo, async (w, put) =>
                await FileTreeTests.ListingAsync(put, FileTreeTests.FileEntry("f", await FileTreeTests.ContentAsync(RandomNumberGenerator.GetBytes(ChunkSize * 2), put))));

            await repo.UpgradeToPacksAsync(SmallPacks, Ct);
            Assert.Equal(2, repo.FormatVersion);
            Assert.True(repo.LooseChunksMayExist);

            // Backups after the switch go into packs and deduplicate against the loose chunks.
            var (after, stats) = await BackupAsync(repo, "vm-1", disk);
            Assert.Equal(6, stats.Deduplicated);
            Assert.True((await repo.VerifyAsync(after, VerifyDepth.Full, null, Ct)).Ok);

            // Without time nothing is packed; then everything referenced is, and the loose files are gone.
            var none = await repo.PackLooseChunksAsync(TimeSpan.Zero, null, Ct);
            Assert.False(none.Finished);
            var result = await repo.PackLooseChunksAsync(TimeSpan.FromHours(1), null, Ct);
            Assert.True(result.Finished);
            Assert.Equal(6 + 3 + 3, result.Packed); // disk, SQL file, tree (content and listing)
            Assert.False(result.LooseChunksRemain);
            Assert.False(repo.LooseChunksMayExist);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*", SearchOption.AllDirectories));
        }

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Equal(2, reopened.FormatVersion);
        Assert.False(reopened.LooseChunksMayExist);
        Assert.True((await reopened.VerifyAsync(rp, VerifyDepth.Full, null, Ct)).Ok);
        Assert.True((await reopened.VerifyAsync(tree, VerifyDepth.Full, null, Ct)).Ok);
        Assert.True((await reopened.VerifySqlBackupAsync(sql, VerifyDepth.Full, null, Ct)).Ok);
        await using var reader = await reopened.OpenSqlBackupAsync(sql, Ct);
        Assert.Equal(log, await SqlBackupObjectTests.ReadAllAsync(await reader.OpenFileAsync("0", Ct)));
    }

    [Fact]
    public async Task Versions_this_code_does_not_know_are_refused_before_anything_is_touched()
    {
        await using (await CreateAsync())
        {
        }

        var descriptor = Path.Combine(_root, "repo.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(descriptor, Ct))!;
        json["formatVersion"] = 3;
        await File.WriteAllTextAsync(descriptor, json.ToJsonString(), Ct);
        var packs = Packs();

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => LocalDirectoryRepository.OpenAsync(_root, Ct, _time));
        Assert.Contains(_root, error.Message, StringComparison.Ordinal);
        Assert.Equal(packs, Packs());
    }

    [Fact]
    public async Task Chunks_are_planned_and_read_in_storage_order_with_their_stored_sizes()
    {
        await using var repo = await CreateAsync(packTargetSize: 0);
        var disk = RandomDisk(5);
        var (before, _) = await BackupAsync(repo, "vm-1", disk);
        await repo.UpgradeToPacksAsync(SmallPacks, Ct);
        var more = RandomDisk(7);
        var (after, _) = await BackupAsync(repo, "vm-2", more);

        var ids = new List<ChunkId>();
        foreach (var rp in new[] { before, after })
        {
            await using var reader = await repo.OpenRestorePointAsync(rp, Ct);
            ids.AddRange((await reader.ReadDiskIndexAsync("0", Ct)).Entries);
        }

        var unknown = ChunkId.Compute("not stored"u8);
        var plan = repo.PlanRead([.. ids, .. ids, ChunkId.Zero, unknown]);

        Assert.Equal(12, plan.Chunks.Count);
        Assert.Equal([unknown], plan.Missing);
        Assert.Equal(12L * ChunkSize, plan.RawBytes);
        Assert.Equal(plan.Chunks.Sum(c => (long)c.StoredLength), plan.StoredBytes);

        // Packed chunks first, pack by pack in offset order; the five loose ones last.
        var packed = plan.Chunks.Take(7).ToList();
        Assert.All(packed.Zip(packed.Skip(1)), p => Assert.True(p.First.Source < p.Second.Source || (p.First.Source == p.Second.Source && p.First.Offset < p.Second.Offset)));
        Assert.All(plan.Chunks.Skip(7), c => Assert.EndsWith(c.Id.ToHex(), plan.Sources[c.Source], StringComparison.Ordinal));

        var content = new Dictionary<ChunkId, byte[]>();
        var buffer = new byte[ChunkSize];
        await foreach (var stored in repo.ReadStoredAsync(plan, Ct))
        {
            var n = ChunkCodec.Decode(stored.Id, stored.Record.Span, buffer);
            Assert.Equal(stored.RawLength, n);
            content[stored.Id] = buffer[..n];
        }

        Assert.Equal(12, content.Count);
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(more.AsSpan(i * ChunkSize, ChunkSize).ToArray(), content[ChunkId.Compute(more.AsSpan(i * ChunkSize, ChunkSize))]);
        }
    }

    [Fact]
    public async Task Index_segments_find_every_entry_and_detect_damage()
    {
        Directory.CreateDirectory(_root);
        var packs = Enumerable.Range(0, 50).Select(i => new PackFile(Guid.NewGuid(), $"p{i}", PackState.Sealed, 1000, DateTime.UtcNow)).ToArray();
        var entries = Enumerable.Range(0, 200_000)
            .Select(i => (Id: ChunkId.Compute(BitConverter.GetBytes(i)), Pack: packs[i % packs.Length], Offset: (uint)i))
            .OrderBy(e => e.Id)
            .ToList();
        var path = Path.Combine(_root, "B-1.bdpi");
        using (var writer = new PackSegmentWriter(path + ".tmp", PackSegmentFormat.KindBase, entries.Count))
        {
            foreach (var (id, pack, offset) in entries)
            {
                writer.Add(id, pack, offset, 100, 200);
            }

            writer.Finish(1);
        }

        File.Move(path + ".tmp", path);
        var byId = packs.ToDictionary(p => p.Id);
        using (var segment = DiskSegment.Open(path, id => byId.GetValueOrDefault(id)))
        {
            Assert.Equal(entries.Count, segment.EntryCount);
            foreach (var (id, pack, offset) in entries.Where((_, i) => i % 97 == 0))
            {
                Assert.True(segment.TryFind(id, out var found));
                Assert.Equal((pack, offset), (segment.Packs[found.Pack], found.Offset));
            }

            for (var i = 0; i < 10_000; i++)
            {
                Assert.False(segment.TryFind(ChunkId.Compute(BitConverter.GetBytes(-1 - i)), out _));
            }

            Assert.Equal(entries.Count, segment.ReadAll().Count());
            Assert.True(segment.MemoryBytes < entries.Count * 4L);
        }

        var memory = MemorySegment.Load(path, id => byId.GetValueOrDefault(id));
        Assert.True(memory.TryFind(entries[123].Id, out var hit));
        Assert.Equal(entries[123].Offset, hit.Offset);

        // A flipped bit in the entries: lookups may not notice, a full read does.
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[4800] ^= 1;
        await File.WriteAllBytesAsync(path, bytes, Ct);
        using (var damaged = DiskSegment.Open(path, id => byId.GetValueOrDefault(id)))
        {
            Assert.Throws<InvalidDataException>(() => damaged.ReadAll().Count());
        }

        Assert.Throws<InvalidDataException>(() => MemorySegment.Load(path, id => byId.GetValueOrDefault(id)));

        // A flipped bit in the tables: refused when opened.
        bytes[^120] ^= 1;
        await File.WriteAllBytesAsync(path, bytes, Ct);
        Assert.Throws<InvalidDataException>(() => DiskSegment.Open(path, id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void A_pack_trailer_round_trips_and_damage_is_detected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "test.pack");
        var entries = new List<PackTrailerEntry>();
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            var header = new byte[PackFormat.HeaderSize];
            var repositoryId = Guid.NewGuid();
            PackFormat.WriteHeader(header, repositoryId);
            Assert.Equal(repositoryId, PackFormat.ReadHeader(header, path));
            stream.Write(header);
            for (var i = 0; i < 5; i++)
            {
                var data = Encoding.UTF8.GetBytes($"chunk {i} " + new string('x', 1000 * i));
                var id = ChunkId.Compute(data);
                var (record, length) = ChunkCodec.Encode(data, 3);
                var idBytes = new byte[32];
                id.WriteTo(idBytes);
                stream.Write(idBytes);
                entries.Add(new PackTrailerEntry(id, (uint)stream.Position, (uint)length, (uint)data.Length));
                stream.Write(record, 0, length);
                System.Buffers.ArrayPool<byte>.Shared.Return(record);
            }

            stream.Write(PackFormat.BuildTail(entries, stream.Position));
        }

        using (var handle = File.OpenHandle(path))
        {
            Assert.Equal(entries, PackFormat.ReadTrailer(handle, path));
            Assert.Equal(entries, PackFormat.Salvage(handle, 64 * 1024));
        }

        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1; // reserved byte of the footer: not covered, still fine
        bytes[^10] ^= 1; // inside the trailer hash
        File.WriteAllBytes(path, bytes);
        using (var handle = File.OpenHandle(path))
        {
            Assert.Throws<InvalidDataException>(() => PackFormat.ReadTrailer(handle, path));
            Assert.Equal(5, PackFormat.Salvage(handle, 64 * 1024).Count);
        }
    }

    private async Task<LocalDirectoryRepository> CreateAsync(long packTargetSize = SmallPacks) =>
        await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time, packTargetSize);

    private List<string> Packs() => [.. Directory.EnumerateFiles(Path.Combine(_root, "packs"), "*.pack", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];
}
