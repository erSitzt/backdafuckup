using System.Security.Cryptography;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Microsoft.Extensions.Time.Testing;

namespace Backdafuckup.Storage.Tests;

/// <summary>
/// SQL backup objects in the repository: backup files streamed into chunks (deduplicated, compressed), one index per
/// file, a manifest as commit point; read back with SHA-256 checks; kept by GC, deletable unless pinned.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqlBackupObjectTests : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-sqlbackup-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow.AddHours(1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_files_are_stored_as_chunks_and_read_back_exactly()
    {
        await using var repo = await CreateAsync();
        // 2.5 chunks with a zero chunk in the middle, and a file smaller than one chunk.
        var log = RandomNumberGenerator.GetBytes((ChunkSize * 5) / 2);
        log.AsSpan(ChunkSize, ChunkSize).Clear();
        var small = RandomNumberGenerator.GetBytes(1000);

        var id = await WriteAsync(repo, "vm-1", ("0", "shop", log), ("1", "small", small));

        var info = Assert.Single(await ToListAsync(repo.ListSqlBackupsAsync(Ct)));
        Assert.Equal((id, SqlBackupKind.Log, "SQLEXPRESS"), (info.SqlBackupId, info.Manifest.Kind, info.Manifest.Instance));
        Assert.Equal([log.Length, small.Length], info.Manifest.Files.Select(f => f.Length));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(log)), info.Manifest.Files[0].Sha256);
        Assert.Equal(42000000040800001m, info.Manifest.Files[0].BackupSet.LastLsn);

        await using var reader = await repo.OpenSqlBackupAsync(id, Ct);
        Assert.Equal(log, await ReadAllAsync(await reader.OpenFileAsync("0", Ct)));
        Assert.Equal(small, await ReadAllAsync(await reader.OpenFileAsync("1", Ct)));
        Assert.Equal(log, ReadAllSync(await reader.OpenFileAsync("0", Ct)));

        // Two data chunks of the log plus the small file; the zero chunk is not stored.
        Assert.Equal(3, repo.CountStoredChunks());
        Assert.True((await repo.VerifySqlBackupAsync(id, VerifyDepth.Full, null, Ct)).Ok);
        Assert.True((await repo.VerifySqlBackupAsync(id, VerifyDepth.Quick, null, Ct)).Ok);
    }

    [Fact]
    public async Task A_file_that_does_not_match_its_recorded_hash_fails_at_its_end()
    {
        Guid id;
        await using (var repo = await CreateAsync())
        {
            id = await WriteAsync(repo, "vm-1", ("0", "shop", RandomNumberGenerator.GetBytes(ChunkSize + 10)));
        }

        // Tamper with the recorded SHA-256 (as if the manifest and the data disagreed).
        var manifestPath = Directory.EnumerateFiles(Path.Combine(_root, "sql-backups"), "manifest.json", SearchOption.AllDirectories).Single();
        var text = await File.ReadAllTextAsync(manifestPath, Ct);
        var sha = System.Text.RegularExpressions.Regex.Match(text, "\"sha256\": \"([0-9a-f]{64})\"").Groups[1].Value;
        await File.WriteAllTextAsync(manifestPath, text.Replace(sha, new string('0', 64), StringComparison.Ordinal), Ct);

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        await using var reader = await reopened.OpenSqlBackupAsync(id, Ct);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await ReadAllAsync(await reader.OpenFileAsync("0", Ct)));
        Assert.Contains("shop_20261105T100000Z.trn", error.Message, StringComparison.Ordinal);
        Assert.False((await reopened.VerifySqlBackupAsync(id, VerifyDepth.Full, null, Ct)).Ok);
    }

    [Fact]
    public async Task Gc_keeps_the_chunks_of_sql_backups_until_they_are_deleted()
    {
        await using var repo = await CreateAsync();
        var shared = RandomNumberGenerator.GetBytes(ChunkSize);
        var first = await WriteAsync(repo, "vm-1", ("0", "shop", [.. shared, .. RandomNumberGenerator.GetBytes(ChunkSize)]));
        var second = await WriteAsync(repo, "vm-1", ("0", "shop", [.. shared, .. RandomNumberGenerator.GetBytes(100)]));
        Assert.Equal(3, repo.CountStoredChunks());

        Assert.Equal(0, (await repo.CollectGarbageAsync(null, Ct)).DeletedChunks);

        await repo.DeleteSqlBackupAsync(first, Ct);
        var gc = await repo.CollectGarbageAsync(null, Ct);

        Assert.Equal(1, gc.DeletedChunks);
        Assert.True((await repo.VerifySqlBackupAsync(second, VerifyDepth.Full, null, Ct)).Ok);
        Assert.Single(await ToListAsync(repo.ListSqlBackupsAsync(Ct)));
    }

    [Fact]
    public async Task A_pinned_sql_backup_cannot_be_deleted()
    {
        await using var repo = await CreateAsync();
        var id = await WriteAsync(repo, "vm-1", ("0", "shop", RandomNumberGenerator.GetBytes(100)));

        using (repo.PinSqlBackup(id))
        {
            await Assert.ThrowsAsync<SqlBackupPinnedException>(() => repo.DeleteSqlBackupAsync(id, Ct));
        }

        await repo.DeleteSqlBackupAsync(id, Ct);
        Assert.Empty(await ToListAsync(repo.ListSqlBackupsAsync(Ct)));
        Assert.Throws<KeyNotFoundException>(() => repo.PinSqlBackup(id));
    }

    [Fact]
    public async Task Sql_backups_survive_reopening_and_uncommitted_ones_are_removed()
    {
        Guid id;
        await using (var repo = await CreateAsync())
        {
            id = await WriteAsync(repo, "vm-1", ("0", "shop", RandomNumberGenerator.GetBytes(100)));

            // Not committed: gone once the writer is disposed.
            await using (var writer = repo.BeginSqlBackup(Guid.NewGuid(), "vm-1"))
            {
                using var content = new MemoryStream(RandomNumberGenerator.GetBytes(100));
                await writer.WriteFileAsync("0", content, Ct);
            }
        }

        // A crash before commit leaves a directory without manifest; opening the repository removes it.
        var orphan = Path.Combine(_root, "sql-backups", "vm-2", $"20260101T000000Z_{Guid.NewGuid():N}");
        Directory.CreateDirectory(orphan);

        await using var reopened = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.Equal([id], (await ToListAsync(reopened.ListSqlBackupsAsync(Ct))).Select(b => b.SqlBackupId));
        Assert.False(Directory.Exists(orphan));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(_root, "sql-backups", "vm-1")));
    }

    [Fact]
    public async Task A_manifest_must_describe_the_files_the_writer_stored()
    {
        await using var repo = await CreateAsync();
        var id = Guid.CreateVersion7();
        await using var writer = repo.BeginSqlBackup(id, "vm-1");
        using var content = new MemoryStream(RandomNumberGenerator.GetBytes(100));
        var stored = await writer.WriteFileAsync("0", content, Ct);

        var manifest = Manifest(repo, id, [Described(stored, "shop") with { Length = 99 }]);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.CommitAsync(manifest, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteFileAsync("0", new MemoryStream([1]), Ct));
    }

    [Fact]
    public async Task Repositories_created_before_sql_backups_get_the_folder_when_opened()
    {
        await (await CreateAsync()).DisposeAsync();
        Directory.Delete(Path.Combine(_root, "sql-backups"));

        await using var repo = await LocalDirectoryRepository.OpenAsync(_root, Ct, _time);
        Assert.True(Directory.Exists(Path.Combine(_root, "sql-backups")));
        Assert.Empty(await ToListAsync(repo.ListSqlBackupsAsync(Ct)));
    }

    private async Task<LocalDirectoryRepository> CreateAsync() =>
        await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time);

    internal static async Task<Guid> WriteAsync(LocalDirectoryRepository repo, string vm, params (string Key, string Database, byte[] Content)[] files)
    {
        var id = Guid.CreateVersion7();
        await using var writer = repo.BeginSqlBackup(id, vm);
        var described = new List<SqlBackupFile>();
        foreach (var (key, database, content) in files)
        {
            using var stream = new MemoryStream(content);
            described.Add(Described(await writer.WriteFileAsync(key, stream, Ct), database));
        }

        await writer.CommitAsync(Manifest(repo, id, described), Ct);
        return id;
    }

    internal static SqlBackupManifest Manifest(LocalDirectoryRepository repo, Guid id, IReadOnlyList<SqlBackupFile> files) => new()
    {
        SqlBackupId = id,
        RepositoryId = repo.Info.RepositoryId,
        CreatedUtc = DateTimeOffset.UtcNow,
        Kind = SqlBackupKind.Log,
        Source = new ManifestSource(HypervisorKind.XenServer, "pool-1", "vm-1", "sql01", null),
        Instance = "SQLEXPRESS",
        ProductVersion = "17.0.1000.7",
        Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"),
        Files = files,
    };

    internal static SqlBackupFile Described(SqlBackupStoredFile stored, string database) => new()
    {
        Key = stored.Key,
        Database = database,
        FileName = $"{database}_20261105T100000Z.trn",
        Length = stored.Length,
        Sha256 = stored.Sha256,
        Index = stored.Index,
        BackupSet = new SqlBackupSet
        {
            BackupSetUuid = Guid.NewGuid(),
            Type = "L",
            FirstLsn = 42000000027200212m,
            LastLsn = 42000000040800001m,
            CheckpointLsn = 42000000027200212m,
            FamilyGuid = Guid.NewGuid(),
            DatabaseGuid = Guid.NewGuid(),
            StartUtc = DateTimeOffset.UtcNow,
            FinishUtc = DateTimeOffset.UtcNow,
            UtcOffsetMinutes = 60,
        },
    };

    internal static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var memory = new MemoryStream();
            var buffer = new byte[7000];
            int n;
            while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
            {
                memory.Write(buffer, 0, n);
            }

            return memory.ToArray();
        }
    }

    private static byte[] ReadAllSync(Stream stream)
    {
        using (stream)
        {
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
