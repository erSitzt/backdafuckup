using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Microsoft.Extensions.Time.Testing;

namespace Backdafuckup.Storage.Tests;

/// <summary>
/// File trees in the repository (spec 2026-11-12-1952-kubernetes-file-level-volumes): listings and file contents as chunk
/// streams, the root in tree-&lt;key&gt;.idx; read back exactly, kept and freed by GC, checked by verify.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileTreeTests : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-filetree-tests", Guid.NewGuid().ToString("N"));
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
    public async Task A_tree_is_stored_and_read_back_exactly_with_names_metadata_links_and_sparse_content()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time);
        var big = RandomNumberGenerator.GetBytes((ChunkSize * 2) + 1234);
        Array.Clear(big, ChunkSize, ChunkSize); // a hole: the middle piece is all zeros and not stored
        var huge = RandomNumberGenerator.GetBytes(ChunkSize * (ChunkStreamRef.MaxDirect + 3)); // its id list goes into a stream of its own
        var oddName = new byte[] { (byte)'r', (byte)'a', 0xFF, (byte)'w' }; // not UTF-8

        var (id, _) = await WriteTreeAsync(repo, async (w, put) =>
        {
            var inner = await ListingAsync(put,
                FileEntry("big", await ContentAsync(big, put)),
                FileEntry("small", await ContentAsync("hello"u8.ToArray(), put)),
                FileEntry("huge", await ContentAsync(huge, put)));
            return await ListingAsync(put,
                Dir("data", inner, 3, big.Length + 5 + huge.Length),
                new FileTreeEntry { Name = oddName, Type = FileTreeEntryType.File, Mode = 0600, Uid = 999, Gid = 999, MtimeNs = 1_700_000_000_123_456_789, Content = ChunkStreamRef.Empty },
                new FileTreeEntry { Name = "link"u8.ToArray(), Type = FileTreeEntryType.Symlink, LinkTarget = "data/small"u8.ToArray() },
                new FileTreeEntry { Name = "pipe"u8.ToArray(), Type = FileTreeEntryType.Fifo, Mode = 0644 },
                new FileTreeEntry { Name = "secret"u8.ToArray(), Type = FileTreeEntryType.File, Flags = FileTreeEntryFlags.Unreadable, Reason = "Permission denied (root_squash)" });
        });

        await using var reader = await repo.OpenRestorePointAsync(id, Ct);
        Assert.Equal(RestorePointManifest.FileTreeFormatVersion, reader.Manifest.FormatVersion);
        var root = await reader.ReadFileTreeIndexAsync("data-redis-0", Ct);
        var entries = FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, ChunkSize).ToDictionary(e => e.Path, e => e.Entry);

        Assert.Equal(["data", "data/big", "data/huge", "data/small", "link", "pipe", "ra�w", "secret"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(big, Content(entries["data/big"], reader.Chunks));
        Assert.Equal(huge, Content(entries["data/huge"], reader.Chunks));
        Assert.NotNull(entries["data/huge"].Content!.IdList);
        Assert.Equal("hello"u8.ToArray(), entries["data/small"].Content!.Inline);
        Assert.Equal(oddName, entries["ra�w"].Name);
        Assert.Equal((0600, 999L, 1_700_000_000_123_456_789L), (entries["ra�w"].Mode, entries["ra�w"].Uid, entries["ra�w"].MtimeNs));
        Assert.Equal("data/small", Encoding.UTF8.GetString(entries["link"].LinkTarget!));
        Assert.Equal(FileTreeEntryType.Fifo, entries["pipe"].Type);
        Assert.Equal(("Permission denied (root_squash)", (ChunkStreamRef?)null), (entries["secret"].Reason, entries["secret"].Content));
        Assert.Equal((3L, (long)big.Length + 5 + huge.Length), (entries["data"].SubtreeFiles, entries["data"].SubtreeBytes));

        var verified = await repo.VerifyAsync(id, VerifyDepth.Full, null, Ct);
        Assert.True(verified.Ok, string.Join("; ", verified.Problems));
        Assert.True(verified.CheckedChunks > ChunkStreamRef.MaxDirect);
    }

    [Fact]
    public async Task Gc_keeps_a_trees_chunks_while_its_restore_point_exists_and_frees_them_after()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time);
        var content = RandomNumberGenerator.GetBytes(ChunkSize * 3);
        var (id, _) = await WriteTreeAsync(repo, async (w, put) =>
            await ListingAsync(put, Dir("d", await ListingAsync(put, FileEntry("f", await ContentAsync(content, put))), 1, content.Length)));
        var before = CountChunkFiles();

        var gc = await repo.CollectGarbageAsync(null, Ct);
        Assert.Equal(0, gc.DeletedChunks);
        Assert.Equal(before, CountChunkFiles());

        await repo.DeleteRestorePointAsync(id, Ct);
        gc = await repo.CollectGarbageAsync(null, Ct);
        Assert.Equal(before, gc.DeletedChunks);
        Assert.Equal(0, CountChunkFiles());
    }

    /// <summary>
    /// A file whose bytes equal a directory's listing marks that listing's chunk first; the walk must still go into the
    /// directory, or GC would free the files in it.
    /// </summary>
    [Fact]
    public async Task A_file_with_the_bytes_of_a_listing_does_not_hide_that_listings_subtree_from_gc()
    {
        await using var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time);
        var deep = RandomNumberGenerator.GetBytes(ChunkSize + 7);
        byte[]? listingBytes = null;
        var (id, _) = await WriteTreeAsync(repo, async (w, put) =>
        {
            // A listing over the inline size (long names), stored as a chunk; then a file "a-copy" (walked first) with its bytes.
            var entries = Enumerable.Range(0, 12).Select(i => FileEntry($"a-rather-long-file-name-to-make-the-listing-big-{i:00}", ChunkStreamRef.Empty)).ToList();
            entries.Add(FileEntry("deep", await ContentAsync(deep, put)));
            listingBytes = FileTreeListing.Serialize(entries);
            var listing = await ChunkStreams.WriteAsync(listingBytes, ChunkSize, put, allowInline: false, Ct);
            return await ListingAsync(put, FileEntry("a-copy", await ContentAsync(listingBytes, put)), Dir("z-dir", listing, 13, deep.Length));
        });

        await repo.CollectGarbageAsync(null, Ct);

        await using var reader = await repo.OpenRestorePointAsync(id, Ct);
        var root = await reader.ReadFileTreeIndexAsync("data-redis-0", Ct);
        var entries = FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, ChunkSize).ToDictionary(e => e.Path, e => e.Entry);
        Assert.Equal(listingBytes, Content(entries["a-copy"], reader.Chunks));
        Assert.Equal(deep, Content(entries["z-dir/deep"], reader.Chunks));
    }

    [Fact]
    public async Task Only_restore_points_with_trees_get_the_new_format_and_a_newer_one_is_refused()
    {
        await using (var repo = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize, 3, _time))
        {
            var (withTree, path) = await WriteTreeAsync(repo, async (w, put) => await ListingAsync(put, FileEntry("f", ChunkStreamRef.Empty)));
            var (plain, plainPath) = await WriteTreeAsync(repo, null);
            Assert.Equal(2, ReadVersion(path));
            Assert.Equal(1, ReadVersion(plainPath));

            // A manifest from a version newer than this one is not read (and so never collected without its references).
            var json = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path.Combine(path, "manifest.json")), "\"formatVersion\":\\s*2", "\"formatVersion\": 3");
            File.WriteAllText(Path.Combine(path, "manifest.json"), json);
            await Assert.ThrowsAsync<NotSupportedException>(() => repo.OpenRestorePointAsync(withTree, Ct));
            _ = plain;
        }

        static int ReadVersion(string directory) => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json"))).RootElement.GetProperty("formatVersion").GetInt32();
    }

    private int CountChunkFiles() => Directory.EnumerateFiles(Path.Combine(_root, "chunks"), "*", SearchOption.AllDirectories).Count();

    private static FileTreeEntry FileEntry(string name, ChunkStreamRef content) =>
        new() { Name = Encoding.UTF8.GetBytes(name), Type = FileTreeEntryType.File, Mode = 0644, Size = content.Length, Content = content };

    private static FileTreeEntry Dir(string name, ChunkStreamRef listing, long files, long bytes) =>
        new() { Name = Encoding.UTF8.GetBytes(name), Type = FileTreeEntryType.Directory, Mode = 0755, Listing = listing, SubtreeFiles = files, SubtreeBytes = bytes };

    private static async Task<ChunkStreamRef> ContentAsync(byte[] data, ChunkStreams.PutChunk put) =>
        await ChunkStreams.WriteAsync(data, ChunkSize, put, allowInline: true, Ct);

    private static async Task<ChunkStreamRef> ListingAsync(ChunkStreams.PutChunk put, params FileTreeEntry[] entries) =>
        await ChunkStreams.WriteAsync(FileTreeListing.Serialize(entries), ChunkSize, put, allowInline: false, Ct);

    private static byte[] Content(FileTreeEntry entry, IChunkReader chunks) => ChunkStreams.ReadAll(entry.Content!, chunks, ChunkSize);

    /// <summary>Writes a restore point with one file tree (<paramref name="build"/> returns its root listing; null: no tree).</summary>
    private static async Task<(Guid Id, string Directory)> WriteTreeAsync(LocalDirectoryRepository repo, Func<IRepositoryWriter, ChunkStreams.PutChunk, Task<ChunkStreamRef>>? build)
    {
        var id = Guid.CreateVersion7();
        await using var writer = repo.BeginWrite(id, "redis", null);
        var trees = new List<ManifestFileTree>();
        if (build is not null)
        {
            ChunkStreams.PutChunk put = async (chunk, data, ct) => await writer.PutChunkAsync(chunk, data, ct);
            var root = await build(writer, put);
            var index = new ChunkIndex(ChunkSize, root.Length, root.Ids!);
            trees.Add(new ManifestFileTree
            {
                Key = "data-redis-0", Name = "data-redis-0", Index = await writer.WriteFileTreeIndexAsync("data-redis-0", index, Ct), RootLength = root.Length,
                Root = new FileTreeRootMeta(0755, 0, 999, 0),
            });
        }

        var info = await writer.CommitAsync(
            new RestorePointManifest
            {
                RestorePointId = id,
                RepositoryId = repo.Info.RepositoryId,
                CreatedUtc = DateTimeOffset.UtcNow,
                Kind = RestorePointKind.Full,
                Source = new ManifestSource(HypervisorKind.Kubernetes, "cluster", "redis", "redis", null),
                Vm = new VmDescriptor { VmId = "redis", Name = "redis" },
                Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"),
                Disks = [],
                FileTrees = trees,
            },
            Ct);
        return (id, info.Path);
    }
}
