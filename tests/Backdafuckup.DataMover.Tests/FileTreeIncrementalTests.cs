using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Storage;

namespace Backdafuckup.DataMover.Tests;

/// <summary>Incremental file trees (spec 2026-11-12-1952-kubernetes-file-level-volumes): the previous tree, its cursor, the server's check, stalls.</summary>
[Trait("Category", "Unit")]
public sealed class FileTreeIncrementalTests : IDisposable
{
    private const int ChunkSize = 64 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-tree-incremental-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_cursor_follows_the_walk_skips_what_is_gone_and_finds_nothing_below_new_or_retyped_folders()
    {
        // Before: a/{b/{y}, x}, c, d/{z}, e (a file that is now a folder), f/{g} (a folder that is now a file).
        var stream = new MemoryStream();
        var w = new MoverPreviousWriter(stream);
        w.Header();
        w.Enter("a"u8);
        w.Enter("b"u8);
        w.File("y"u8, new PreviousFile(1, 1, 1, 21));
        w.Leave();
        w.File("x"u8, new PreviousFile(1, 1, 1, 20));
        w.Leave();
        w.File("c"u8, new PreviousFile(3, 30, 31, 30));
        w.Enter("d"u8);
        w.File("z"u8, new PreviousFile(1, 1, 1, 40));
        w.Leave();
        w.File("e"u8, new PreviousFile(1, 1, 1, 50));
        w.Enter("f"u8);
        w.File("g"u8, new PreviousFile(1, 1, 1, 60));
        w.Leave();
        w.End();
        stream.Position = 0;

        // Now: a is gone, c is the same, d/z changed, e is a folder, f is a file, n is a new folder.
        var cursor = new MoverPreviousCursor(stream);
        Assert.True(cursor.IsUnchanged("c"u8, new TreeStat(0644, 0, 0, 30, 31, 30, 1, 3)));
        cursor.Enter("d"u8);
        Assert.False(cursor.IsUnchanged("z"u8, new TreeStat(0644, 0, 0, 2, 2, 40, 1, 1)));
        cursor.Leave();
        cursor.Enter("e"u8);
        Assert.Null(cursor.File("inside"u8));
        cursor.Enter("deeper"u8);
        Assert.Null(cursor.File("x"u8));
        cursor.Leave();
        cursor.Leave();
        Assert.Null(cursor.File("f"u8));
        cursor.Enter("n"u8);
        Assert.Null(cursor.File("g"u8));
        cursor.Leave();
        cursor.Finish();
        Assert.Equal(1, cursor.Unchanged);
    }

    [Fact]
    public async Task The_previous_tree_holds_folders_and_only_the_files_whose_content_can_be_taken_over()
    {
        await using var repository = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var (id, _) = await BackupAsync(repository, FirstRun, null);
        await using var reader = await repository.OpenRestorePointAsync(id, Ct);
        var previous = await BaseAsync(reader, id);

        var stream = new MemoryStream();
        await previous.WriteAsync(stream, Ct);
        stream.Position = 0;
        var prv = new MoverPreviousReader(stream);
        prv.ReadHeader();
        var records = new List<string>();
        for (var record = prv.Read(); record.Kind != PreviousRecordKind.End; record = prv.Read())
        {
            records.Add(record.Kind switch
            {
                PreviousRecordKind.File => $"F {Encoding.UTF8.GetString(record.Name)} {record.File.Size}/{record.File.Inode}",
                PreviousRecordKind.Enter => $"D {Encoding.UTF8.GetString(record.Name)}",
                _ => "U",
            });
        }

        // The racy file, the one changed while read and the unreadable one are read again next time; so are links and specials.
        Assert.Equal(["F a.txt 600/10", "D dir", "F b.bin 100000/11", "U", "F link1 700/13"], records);
    }

    [Fact]
    public async Task Unchanged_files_take_their_content_from_the_previous_tree_and_the_server_checks_each_one()
    {
        await using var repository = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        var (firstId, first) = await BackupAsync(repository, FirstRun, null);
        await using var firstReader = await repository.OpenRestorePointAsync(firstId, Ct);
        var previous = await BaseAsync(firstReader, firstId);

        // Second run: a.txt, dir/b.bin and link1 unchanged (link2 another name of link1), racy read again.
        var racy = RandomNumberGenerator.GetBytes(900);
        var (secondId, second) = await BackupAsync(repository, w =>
        {
            w.Header(Folder);
            w.Unchanged("a.txt"u8, Stat(10, 600));
            w.Enter("dir"u8, Folder);
            w.Unchanged("b.bin"u8, Stat(11, 100_000));
            File(w, "racy"u8, Stat(12, racy.Length, mtime: 5), racy);
            w.Leave();
            w.Unchanged("link1"u8, Stat(13, 700, links: 2));
            w.HardLink("link2"u8, Stat(13, 700, links: 2));
            w.End(5, racy.Length);
        }, previous);

        Assert.Equal((5L, 3L, 600L + 100_000 + 700, (long)racy.Length), (second.Stats.Files, second.Stats.UnchangedFiles, second.Stats.UnchangedBytes, second.Stats.ReadBytes));
        Assert.Equal((101_900L, 600L + 100_000 + 900 + 700 + 700), (first.Stats.Bytes, second.Stats.Bytes));
        await using var secondReader = await repository.OpenRestorePointAsync(secondId, Ct);
        var entries = await EntriesAsync(secondReader);
        var before = await EntriesAsync(firstReader);
        Assert.Equal(ChunkStreams.ReadAll(before["a.txt"].Content!, firstReader.Chunks, ChunkSize), ChunkStreams.ReadAll(entries["a.txt"].Content!, secondReader.Chunks, ChunkSize));
        Assert.Equal(ChunkStreams.ReadAll(before["dir/b.bin"].Content!, firstReader.Chunks, ChunkSize), ChunkStreams.ReadAll(entries["dir/b.bin"].Content!, secondReader.Chunks, ChunkSize));
        Assert.Equal(racy, ChunkStreams.ReadAll(entries["dir/racy"].Content!, secondReader.Chunks, ChunkSize));
        Assert.Equal(ChunkStreams.ReadAll(before["link1"].Content!, firstReader.Chunks, ChunkSize), ChunkStreams.ReadAll(entries["link2"].Content!, secondReader.Chunks, ChunkSize));
        Assert.True((await repository.VerifyAsync(secondId, VerifyDepth.Full, null, Ct)).Ok);

        // The mover's word is checked: a file that is not in the previous tree as it says breaks the stream.
        foreach (var (stat, name, why) in new[]
                 {
                     (Stat(10, 600, mtime: 2), "a.txt", "its size, times or inode were different then"),
                     (Stat(12, 300, mtime: 1), "racy", "its copy then cannot be taken over"),
                     (Stat(99, 1), "nope", "the previous tree has no such file"),
                 })
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => BackupAsync(repository, w =>
            {
                w.Header(Folder);
                if (name == "racy")
                {
                    w.Enter("dir"u8, Folder);
                    w.Unchanged(Encoding.UTF8.GetBytes(name), stat);
                    w.Leave();
                }
                else
                {
                    w.Unchanged(Encoding.UTF8.GetBytes(name), stat);
                }

                w.End(1, 0);
            }, previous));
            Assert.Contains(why, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_mover_that_sends_only_the_same_heartbeat_is_given_up_on_naming_where_it_is()
    {
        await using var repository = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        await using var writer = repository.BeginWrite(Guid.CreateVersion7(), "stall", null);
        var pipe = new Pipe();
        var w = new MoverTreeWriter(pipe.Writer.AsStream());
        using var stop = new CancellationTokenSource();
        var sending = Task.Run(async () =>
        {
            w.Header(Folder);
            w.Enter("data"u8, Folder);
            while (!stop.IsCancellationRequested)
            {
                w.Heartbeat(7, 1234, "data/slow.bin");
                await Task.Delay(50, CancellationToken.None);
            }
        }, CancellationToken.None);

        var builder = new FileTreeBuilder(writer, ChunkSize) { StallTimeout = TimeSpan.FromSeconds(1) };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => builder.BuildAsync(pipe.Reader.AsStream(), Ct));
        await stop.CancelAsync();
        await sending;
        Assert.Contains("nothing new for 1 second(s): it is at data/slow.bin, after 7 entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mover_that_goes_silent_is_given_up_on()
    {
        await using var repository = await LocalDirectoryRepository.CreateAsync(_root, Ct, ChunkSize);
        await using var writer = repository.BeginWrite(Guid.CreateVersion7(), "silent", null);
        var pipe = new Pipe();
        var w = new MoverTreeWriter(pipe.Writer.AsStream());
        w.Header(Folder);
        w.Enter("db"u8, Folder);
        w.FileStart("table"u8, Stat(5, 10_000));
        w.Piece(new byte[100]);

        var builder = new FileTreeBuilder(writer, ChunkSize) { StallTimeout = TimeSpan.FromSeconds(1) };
        var error = await Assert.ThrowsAsync<TimeoutException>(() => builder.BuildAsync(pipe.Reader.AsStream(), Ct));
        Assert.Contains("it is at db", error.Message, StringComparison.Ordinal);
        await pipe.Writer.CompleteAsync();
    }

    private static readonly TreeStat Folder = new(0755, 0, 0, 1, 1, 2, 2, 0);

    /// <summary>First run: a.txt (600 B), dir/b.bin (100 kB), dir/racy (racy), dir/torn (changed while read), dir/denied (not read), link1 (two names), a symlink.</summary>
    private static void FirstRun(MoverTreeWriter w)
    {
        w.Header(Folder);
        File(w, "a.txt"u8, Stat(10, 600), Bytes(600, 1));
        w.Enter("dir"u8, Folder);
        File(w, "b.bin"u8, Stat(11, 100_000), Bytes(100_000, 2));
        File(w, "denied"u8, null, null);
        File(w, "racy"u8, Stat(12, 300), Bytes(300, 3), racy: true);
        File(w, "torn"u8, Stat(14, 300), Bytes(300, 4), end: TreeFileEnd.Changed);
        w.Leave();
        File(w, "link1"u8, Stat(13, 700, links: 2), Bytes(700, 5));
        w.Symlink("sym"u8, Stat(15, 5), "a.txt"u8);
        w.End(8, 101_900);
    }

    private static void File(MoverTreeWriter w, ReadOnlySpan<byte> name, TreeStat? stat, byte[]? content, bool racy = false, TreeFileEnd end = TreeFileEnd.Complete)
    {
        if (stat is not { } s || content is null)
        {
            w.Issue(name, "Permission denied");
            return;
        }

        w.FileStart(name, s);
        w.Piece(content);
        w.FileEnd(end, content.Length, holes: false, racy);
    }

    private static TreeStat Stat(ulong inode, long size, long mtime = 1, long links = 1) => new(0644, 1000, 1000, mtime, mtime, inode, links, size);

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<FileTreeBase> BaseAsync(IRestorePointReader reader, Guid id)
    {
        var root = await reader.ReadFileTreeIndexAsync("data", Ct);
        return new FileTreeBase(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, ChunkSize, id);
    }

    private static async Task<Dictionary<string, FileTreeEntry>> EntriesAsync(IRestorePointReader reader)
    {
        var root = await reader.ReadFileTreeIndexAsync("data", Ct);
        return FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, ChunkSize).ToDictionary(e => e.Path, e => e.Entry);
    }

    /// <summary>Builds the tree the mover would send (<paramref name="send"/>) into a new restore point.</summary>
    private static async Task<(Guid Id, FileTreeBuilder.Result Result)> BackupAsync(LocalDirectoryRepository repository, Action<MoverTreeWriter> send, FileTreeBase? previous)
    {
        var stream = new MemoryStream();
        send(new MoverTreeWriter(stream));
        stream.Position = 0;
        var id = Guid.CreateVersion7();
        await using var writer = repository.BeginWrite(id, "tree-test", null);
        var result = await new FileTreeBuilder(writer, ChunkSize) { Previous = previous }.BuildAsync(stream, Ct);
        var index = await writer.WriteFileTreeIndexAsync("data", result.Root, Ct);
        await writer.CommitAsync(
            new RestorePointManifest
            {
                RestorePointId = id, RepositoryId = repository.Info.RepositoryId, CreatedUtc = DateTimeOffset.UtcNow, Kind = RestorePointKind.Full,
                Source = new ManifestSource(HypervisorKind.Kubernetes, "cluster", "tree-test", "tree-test", null), Vm = new VmDescriptor { VmId = "tree-test", Name = "tree-test" },
                Chunking = new ManifestChunking(ChunkSize, "sha256", "zstd"), Disks = [],
                FileTrees = [new ManifestFileTree { Key = "data", Name = "data", Index = index, RootLength = result.Root.DiskSize, Root = result.RootMeta, Stats = result.Stats }],
            },
            Ct);
        return (id, result);
    }
}
