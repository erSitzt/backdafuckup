using System.Security.Cryptography;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Backdafuckup.Storage.Tests;

/// <summary>Runs every test against a format 1 repository (one file per chunk) and a format 2 repository (packs).</summary>
public abstract class LocalDirectoryRepositoryTests : IDisposable
{
    internal const int ChunkSize = 64 * 1024;
    protected readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-repo-tests", Guid.NewGuid().ToString("N"));

    // GC only deletes chunks older than its safety window; tests run "in the future" so that window is passed.
    protected readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow.AddHours(1));

    /// <summary>0 for format 1; small packs for format 2, so that writers fill several of them.</summary>
    protected abstract long PackTargetSize { get; }

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Writes_commits_and_reads_back_a_restore_point()
    {
        await using var repo = await CreateAsync();
        var disk = RandomDisk(5);
        var (rpId, _) = await BackupAsync(repo, "vm-1", disk);

        await using var reader = await repo.OpenRestorePointAsync(rpId, Ct);
        var index = await reader.ReadDiskIndexAsync("0", Ct);
        var restored = await RestoreAsync(reader.Chunks, index);
        Assert.Equal(disk, restored);

        var metadata = await reader.OpenBlobAsync(reader.Manifest.Metadata!, Ct);
        Assert.Equal("metadata", new StreamReader(metadata).ReadToEnd());

        var verify = await repo.VerifyAsync(rpId, VerifyDepth.Full, null, Ct);
        Assert.True(verify.Ok, string.Join("; ", verify.Problems));
    }

    [Fact]
    public async Task A_restore_point_moves_to_another_repository_as_files_and_stored_chunks()
    {
        await using var source = await CreateAsync();
        var disk = RandomDisk(6);
        var (rpId, _) = await BackupAsync(source, "vm-1", disk);
        var files = await source.ReadRestorePointFilesAsync(rpId, Ct);
        var ids = await source.ListRestorePointChunksAsync(rpId, Ct);
        Assert.Equal(LocalDirectoryRepository.ManifestFileName, files[0].Name);
        Assert.Contains(files, f => f.Name == "disk-0.idx");
        Assert.Equal(6, ids.Count);

        // The other format as target: packs into loose chunks and back.
        var targetRoot = _root + "-target";
        try
        {
            await using var target = await LocalDirectoryRepository.CreateAsync(targetRoot, Ct, ChunkSize, 3, _time, PackTargetSize == 0 ? 64L * 1024 * 1024 : 0);
            var original = System.Text.Json.JsonSerializer.Deserialize(files[0].Content, CoreJsonContext.Default.RestorePointManifest)!;
            var newId = Guid.NewGuid();
            await using (var import = target.BeginImport(newId, original))
            {
                await foreach (var chunk in ((IChunkLocator)source).ReadStoredAsync(((IChunkLocator)source).PlanRead(ids), Ct))
                {
                    Assert.Equal(ChunkPutOutcome.Stored, (await import.PutEncodedChunkAsync(chunk.Id, chunk.Record.ToArray(), Ct)).Outcome);
                }

                // A damaged record is refused before anything is stored.
                var damaged = ids[0];
                await foreach (var chunk in ((IChunkLocator)source).ReadStoredAsync(((IChunkLocator)source).PlanRead([damaged]), Ct))
                {
                    var bytes = chunk.Record.ToArray();
                    bytes[^1] ^= 0x5A;
                    await Assert.ThrowsAsync<ChunkCorruptException>(async () => await import.PutEncodedChunkAsync(ChunkId.Compute(RandomDisk(1)), bytes, Ct));
                }

                await Assert.ThrowsAsync<InvalidDataException>(() => import.CommitAsync(null, Ct)); // its files are not there yet
                await Assert.ThrowsAsync<ArgumentException>(() => import.WriteFileAsync("../escape.idx", new byte[1], Ct));
                foreach (var file in files.Skip(1))
                {
                    await import.WriteFileAsync(file.Name, file.Content, Ct);
                }

                var info = await import.CommitAsync(m => m with { Job = null }, Ct);
                Assert.Equal((newId, target.Info.RepositoryId), (info.Manifest.RestorePointId, info.Manifest.RepositoryId));
            }

            var verify = await target.VerifyAsync(newId, VerifyDepth.Full, null, Ct);
            Assert.True(verify.Ok, string.Join("; ", verify.Problems));
            await using var reader = await target.OpenRestorePointAsync(newId, Ct);
            Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
            Assert.Equal("metadata", new StreamReader(await reader.OpenBlobAsync(reader.Manifest.Metadata!, Ct)).ReadToEnd());
        }
        finally
        {
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_chain_moves_with_its_ids_reading_only_missing_chunks_and_the_source_copy_goes_when_free()
    {
        await using var source = await CreateAsync();
        var first = RandomDisk(6);
        var second = (byte[])first.Clone();
        RandomNumberGenerator.Fill(second.AsSpan(0, 2 * ChunkSize)); // two chunks changed, four shared
        var (firstId, _) = await BackupAsync(source, "vm-1", first);
        var (secondId, _) = await BackupAsync(source, "vm-1", second);

        var targetRoot = _root + "-extent";
        try
        {
            await using var target = await LocalDirectoryRepository.CreateAsync(targetRoot, Ct, ChunkSize, 3, _time, PackTargetSize == 0 ? 64L * 1024 * 1024 : 0);
            async Task<int> CopyAsync(Guid id)
            {
                var files = await source.ReadRestorePointFilesAsync(id, Ct);
                var manifest = System.Text.Json.JsonSerializer.Deserialize(files[0].Content, CoreJsonContext.Default.RestorePointManifest)!;
                await using var import = target.BeginImport(id, manifest);
                var missing = (await source.ListRestorePointChunksAsync(id, Ct)).Where(c => !import.TryReuseChunk(c)).ToList();
                await foreach (var chunk in ((IChunkLocator)source).ReadStoredAsync(((IChunkLocator)source).PlanRead(missing), Ct))
                {
                    await import.PutEncodedChunkAsync(chunk.Id, chunk.Record, Ct);
                }

                foreach (var file in files.Skip(1))
                {
                    await import.WriteFileAsync(file.Name, file.Content, Ct);
                }

                var info = await import.CommitAsync(null, Ct);
                Assert.Equal((id, target.Info.RepositoryId), (info.Manifest.RestorePointId, info.Manifest.RepositoryId));
                return missing.Count;
            }

            // Oldest first: the second point reads only its two changed chunks; both keep their ids.
            Assert.Equal(6, await CopyAsync(firstId));
            Assert.Equal(2, await CopyAsync(secondId));
            Assert.True(target.ContainsRestorePoint(firstId) && target.ContainsRestorePoint(secondId));
            Assert.False(target.ContainsSqlBackup(firstId));

            // The source copy: not while a reader holds it, nor while garbage collection holds the repository.
            using (source.Pin(firstId))
            {
                Assert.Equal(new RepositoryActivity(1, 0, false), source.Activity);
                Assert.Equal(DeleteOutcome.Pinned, await source.TryDeleteRestorePointAsync(firstId, TimeSpan.FromSeconds(5), Ct));
            }

            var marking = new TaskCompletionSource();
            var proceed = new TaskCompletionSource();
            var gc = Task.Run(() => source.CollectGarbageAsync(new BlockingProgress(marking, proceed.Task), Ct), Ct);
            await marking.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(source.Activity.Collecting);
            Assert.Equal(DeleteOutcome.Busy, await source.TryDeleteRestorePointAsync(firstId, TimeSpan.FromMilliseconds(50), Ct));
            proceed.SetResult();
            await gc;
            Assert.True(source.Activity.Idle, source.Activity.ToString());
            Assert.Equal(DeleteOutcome.Deleted, await source.TryDeleteRestorePointAsync(firstId, TimeSpan.FromSeconds(5), Ct));
            Assert.Equal(DeleteOutcome.NotFound, await source.TryDeleteRestorePointAsync(firstId, TimeSpan.FromSeconds(5), Ct));
            Assert.Equal(DeleteOutcome.Deleted, await source.TryDeleteRestorePointAsync(secondId, TimeSpan.FromSeconds(5), Ct));

            foreach (var (id, disk) in new[] { (firstId, first), (secondId, second) })
            {
                var verify = await target.VerifyAsync(id, VerifyDepth.Full, null, Ct);
                Assert.True(verify.Ok, string.Join("; ", verify.Problems));
                await using var reader = await target.OpenRestorePointAsync(id, Ct);
                Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
            }
        }
        finally
        {
            if (Directory.Exists(targetRoot))
            {
                Directory.Delete(targetRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_chunk_reused_by_an_import_survives_garbage_collection_before_the_commit()
    {
        await using var target = await CreateAsync();
        var disk = RandomDisk(4);
        var (oldId, _) = await BackupAsync(target, "vm-1", disk);
        var files = await target.ReadRestorePointFilesAsync(oldId, Ct);
        var manifest = System.Text.Json.JsonSerializer.Deserialize(files[0].Content, CoreJsonContext.Default.RestorePointManifest)!;
        var ids = await target.ListRestorePointChunksAsync(oldId, Ct);
        var (otherId, _) = await BackupAsync(target, "vm-2", RandomDisk(3));
        await target.DeleteRestorePointAsync(oldId, Ct); // both points' chunks are unreferenced now
        await target.DeleteRestorePointAsync(otherId, Ct);
        _time.Advance(TimeSpan.FromHours(1)); // and older than the safety window

        var movedId = Guid.NewGuid();
        await using (var import = target.BeginImport(movedId, manifest))
        {
            Assert.All(ids, id => Assert.True(import.TryReuseChunk(id)));
            await target.CollectGarbageAsync(null, Ct);
            Assert.Equal(ids.Count, target.CountStoredChunks()); // the other point's chunks are gone, the reused ones kept
            foreach (var file in files.Skip(1))
            {
                await import.WriteFileAsync(file.Name, file.Content, Ct);
            }

            await import.CommitAsync(null, Ct);
        }

        var verify = await target.VerifyAsync(movedId, VerifyDepth.Full, null, Ct);
        Assert.True(verify.Ok, string.Join("; ", verify.Problems));
        await using var reader = await target.OpenRestorePointAsync(movedId, Ct);
        Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
    }

    /// <summary>Holds garbage collection at its first progress report (it holds the repository's GC mutex there).</summary>
    private sealed class BlockingProgress(TaskCompletionSource reached, Task proceed) : IProgress<string>
    {
        public void Report(string value)
        {
            reached.TrySetResult();
            proceed.Wait();
        }
    }

    [Fact]
    public async Task Identical_chunks_are_stored_once_and_zero_chunks_not_at_all()
    {
        await using var repo = await CreateAsync();
        var block = RandomNumberGenerator.GetBytes(ChunkSize);
        var disk = new byte[ChunkSize * 4];
        block.CopyTo(disk, 0);
        block.CopyTo(disk, ChunkSize * 2);

        var (_, stats) = await BackupAsync(repo, "vm-1", disk);

        Assert.Equal(1, stats.Stored);
        Assert.Equal(1, stats.Deduplicated);
        Assert.Equal(2, stats.Zero);
        Assert.Equal(1, repo.CountStoredChunks());
    }

    [Fact]
    public async Task Uncommitted_writes_leave_no_restore_point_behind()
    {
        await using (var repo = await CreateAsync())
        {
            var writer = repo.BeginWrite(Guid.NewGuid(), "vm-1", null);
            var data = RandomNumberGenerator.GetBytes(ChunkSize);
            await writer.PutChunkAsync(ChunkId.Compute(data), data, Ct);
            await writer.DisposeAsync();

            Assert.Empty(await ToListAsync(repo.ListRestorePointsAsync(Ct)));
            Assert.False(Directory.Exists(Path.Combine(_root, "restore-points", "vm-1")));
        }

        // A crash before commit leaves a directory without manifest; opening the repository removes it.
        var orphan = Path.Combine(_root, "restore-points", "vm-2", $"20260101T000000Z_{Guid.NewGuid():N}");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "disk-0.idx"), "partial", Ct);
        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task Second_owner_cannot_open_the_repository()
    {
        await using var repo = await CreateAsync();

        await Assert.ThrowsAsync<RepositoryLockedException>(() => LocalDirectoryRepository.OpenAsync(_root, Ct));
    }

    [Fact]
    public async Task Pinned_restore_points_cannot_be_deleted()
    {
        await using var repo = await CreateAsync();
        var (rpId, _) = await BackupAsync(repo, "vm-1", RandomDisk(2));

        using (repo.Pin(rpId))
        {
            await Assert.ThrowsAsync<RestorePointPinnedException>(() => repo.DeleteRestorePointAsync(rpId, Ct));
        }

        await repo.DeleteRestorePointAsync(rpId, Ct);
        Assert.Empty(await ToListAsync(repo.ListRestorePointsAsync(Ct)));
    }

    [Fact]
    public async Task Gc_removes_only_chunks_that_are_no_longer_referenced()
    {
        await using var repo = await CreateAsync();
        var first = RandomDisk(4);
        var second = (byte[])first.Clone();
        RandomNumberGenerator.Fill(second.AsSpan(0, ChunkSize)); // one chunk differs
        var (rp1, _) = await BackupAsync(repo, "vm-1", first);
        var (rp2, _) = await BackupAsync(repo, "vm-1", second);
        Assert.Equal(5, repo.CountStoredChunks());

        await repo.DeleteRestorePointAsync(rp1, Ct);
        var gc = await repo.CollectGarbageAsync(null, Ct);

        Assert.Equal(1, gc.DeletedChunks);
        Assert.Equal(4, repo.CountStoredChunks());
        Assert.True((await repo.VerifyAsync(rp2, VerifyDepth.Full, null, Ct)).Ok);
    }

    [Fact]
    public async Task Restore_points_survive_reopening_the_repository()
    {
        Guid rpId;
        var disk = RandomDisk(3);
        await using (var repo = await CreateAsync())
        {
            (rpId, _) = await BackupAsync(repo, "vm-1", disk);
        }

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        var all = await ToListAsync(reopened.ListRestorePointsAsync(Ct));
        Assert.Single(all);
        Assert.Equal(rpId, all[0].RestorePointId);
        await using var reader = await reopened.OpenRestorePointAsync(rpId, Ct);
        Assert.Equal(disk, await RestoreAsync(reader.Chunks, await reader.ReadDiskIndexAsync("0", Ct)));
    }

    [Fact]
    public async Task Gc_running_concurrently_with_backups_never_deletes_referenced_chunks()
    {
        await using var repo = await CreateAsync();
        repo.RepackUnusedPercent = 0; // the final count expects every unreferenced chunk gone

        // A small pool of chunk contents so that writers constantly deduplicate against each other.
        var pool = Enumerable.Range(0, 24).Select(_ => RandomNumberGenerator.GetBytes(ChunkSize)).ToArray();
        var committed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        using var stop = new CancellationTokenSource();

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                var disk = new byte[ChunkSize * 6];
                for (var c = 0; c < 6; c++)
                {
                    pool[Random.Shared.Next(pool.Length)].CopyTo(disk, c * ChunkSize);
                }

                var commit = Random.Shared.Next(3) != 0;
                var (rpId, _) = await BackupAsync(repo, $"vm-{w}", disk, commit);
                if (commit)
                {
                    committed.Add(rpId);

                    // Retention-like deletes of older restore points interleave with GC.
                    if (Random.Shared.Next(4) == 0 && committed.TryTake(out var old))
                    {
                        try
                        {
                            await repo.DeleteRestorePointAsync(old, Ct);
                        }
                        catch (RestorePointPinnedException)
                        {
                            committed.Add(old);
                        }
                    }
                }
            }
        }, Ct)).ToArray();

        var gcLoop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await repo.CollectGarbageAsync(null, Ct);
            }
        }, Ct);

        await Task.WhenAll(writers);
        await stop.CancelAsync();
        await gcLoop;
        await repo.CollectGarbageAsync(null, Ct);

        foreach (var rpId in committed)
        {
            var result = await repo.VerifyAsync(rpId, VerifyDepth.Full, null, Ct);
            Assert.True(result.Ok, $"{rpId}: {string.Join("; ", result.Problems)}");
        }

        // After the final GC, every chunk on disk is referenced by some restore point.
        var referenced = new HashSet<ChunkId>();
        foreach (var rpId in committed)
        {
            await using var reader = await repo.OpenRestorePointAsync(rpId, Ct);
            referenced.UnionWith((await reader.ReadDiskIndexAsync("0", Ct)).Entries.Where(e => !e.IsZero));
        }

        Assert.Equal(referenced.Count, repo.CountStoredChunks());
    }

    protected async Task<LocalDirectoryRepository> CreateAsync() =>
        await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time, PackTargetSize);

    internal static byte[] RandomDisk(int chunks) => RandomNumberGenerator.GetBytes(ChunkSize * chunks);

    internal sealed record PutStats(int Stored, int Deduplicated, int Zero);

    internal static async Task<(Guid RestorePointId, PutStats Stats)> BackupAsync(LocalDirectoryRepository repo, string vm, byte[] disk, bool commit = true)
    {
        var rpId = Guid.CreateVersion7();
        await using var writer = repo.BeginWrite(rpId, vm, null);
        var index = ChunkIndex.CreateEmpty(ChunkSize, disk.Length);
        int stored = 0, deduplicated = 0, zero = 0;
        for (var i = 0; i < index.Entries.Length; i++)
        {
            var data = disk.AsMemory(i * ChunkSize, Math.Min(ChunkSize, disk.Length - (i * ChunkSize)));
            if (!data.Span.ContainsAnyExcept((byte)0))
            {
                zero++;
                continue;
            }

            var id = ChunkId.Compute(data.Span);
            index.Entries[i] = id;
            var result = await writer.PutChunkAsync(id, data, Ct);
            if (result.Outcome == ChunkPutOutcome.Stored)
            {
                stored++;
            }
            else
            {
                deduplicated++;
            }
        }

        var indexRef = await writer.WriteDiskIndexAsync("0", index, Ct);
        var blob = await writer.WriteBlobAsync("vm-metadata", "test", new MemoryStream("metadata"u8.ToArray()), Ct);
        if (commit)
        {
            await writer.CommitAsync(
                new RestorePointManifest
                {
                    RestorePointId = rpId,
                    RepositoryId = repo.Info.RepositoryId,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Kind = RestorePointKind.Full,
                    Source = new ManifestSource(HypervisorKind.XenServer, "pool", vm, vm, null),
                    Vm = new VmDescriptor { VmId = vm, Name = vm },
                    Metadata = blob,
                    Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"),
                    Disks =
                    [
                        new ManifestDisk
                        {
                            Key = "0",
                            SourceDiskId = "disk",
                            Name = "disk",
                            CapacityBytes = disk.Length,
                            Index = indexRef,
                            Stats = new DiskBackupStats(),
                        },
                    ],
                },
                Ct);
        }

        return (rpId, new PutStats(stored, deduplicated, zero));
    }

    internal static async Task<byte[]> RestoreAsync(IChunkReader chunks, ChunkIndex index)
    {
        var disk = new byte[index.DiskSize];
        for (var i = 0; i < index.Entries.Length; i++)
        {
            if (!index.Entries[i].IsZero)
            {
                await chunks.ReadAsync(index.Entries[i], disk.AsMemory(i * index.ChunkSize, Math.Min(index.ChunkSize, (int)(index.DiskSize - (i * (long)index.ChunkSize)))), Ct);
            }
        }

        return disk;
    }

    internal static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}

[Trait("Category", "Unit")]
public sealed class LooseRepositoryTests : LocalDirectoryRepositoryTests
{
    protected override long PackTargetSize => 0;

    [Fact]
    public async Task Detects_corrupt_and_missing_chunks()
    {
        await using var repo = await CreateAsync();
        var (rpId, _) = await BackupAsync(repo, "vm-1", RandomDisk(3));
        var chunks = Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*", SearchOption.AllDirectories).Order().ToList();

        var bytes = await File.ReadAllBytesAsync(chunks[0], Ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(chunks[0], bytes, Ct);
        File.Delete(chunks[1]);

        var full = await repo.VerifyAsync(rpId, VerifyDepth.Full, null, Ct);
        Assert.False(full.Ok);
        Assert.Equal(2, full.Problems.Count);

        var quick = await repo.VerifyAsync(rpId, VerifyDepth.Quick, null, Ct);
        Assert.Contains(quick.Problems, p => p.Contains("missing", StringComparison.Ordinal));

        var id = ChunkId.Parse(Path.GetFileName(chunks[0]));
        await Assert.ThrowsAsync<ChunkCorruptException>(async () => await repo.Chunks.ReadAsync(id, new byte[ChunkSize], Ct));
    }
}

[Trait("Category", "Unit")]
public sealed class PackedRepositoryTests : LocalDirectoryRepositoryTests
{
    // Four 64 KiB chunks per pack: writers seal several packs, GC repacks and deletes them.
    protected override long PackTargetSize => 4 * (ChunkSize + 1024);
}
