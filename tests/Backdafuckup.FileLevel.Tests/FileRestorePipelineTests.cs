using System.Security.Cryptography;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;

namespace Backdafuckup.FileLevel.Tests;

[Trait("Category", "Unit")]
public sealed class FileRestorePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-restore-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Windows_selection_is_restored_with_content_times_and_attributes()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ntfs-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var target = new LocalFolderTarget(_root);
        var observer = new RecordingObserver();
        var selection = new[] { "folder", "hello.txt", "hidden.txt", "compressed", "pattern-3m.bin", "Ümlaut äöü ß.txt" }.Select(GuestPath.Parse).ToList();

        var result = FileRestorePipeline.Run(fs, selection, target, new FileRestorePipelineOptions(), observer, Ct);

        Assert.Equal(new FileRestoreResult(6, 4, 24 + 7 + 5 + 1048576 + 3145728 + 7, 0, 0, 0, 0), result);
        Assert.Equal((6L, 4L, result.Bytes), observer.Plan);
        Assert.Empty(observer.Problems);
        var manifest = Fixtures.Manifest("ntfs-gpt.p1").ToDictionary(e => e.Path);
        foreach (var file in new[] { "folder/sub/deep/nested.txt", "hello.txt", "hidden.txt", "compressed/packed.bin", "pattern-3m.bin", "Ümlaut äöü ß.txt" })
        {
            Assert.Equal(manifest[file].Hash, Sha256(Path.Combine(_root, file)));
        }

        Assert.Equal(fs.Stat(GuestPath.Parse("hello.txt")).ModifiedUtc, File.GetLastWriteTimeUtc(Path.Combine(_root, "hello.txt")));

        // Folder times are set after their content, which changes them.
        Assert.Equal(fs.Stat(GuestPath.Parse("folder/sub")).ModifiedUtc, Directory.GetLastWriteTimeUtc(Path.Combine(_root, "folder", "sub")));
        Assert.True(target.Metadata["hidden.txt"].WindowsAttributes!.Value.HasFlag(FileAttributes.Hidden));
        Assert.NotNull(target.Metadata["hello.txt"].CreatedUtc);
    }

    [Fact]
    public void Keep_mode_writes_restored_copies_and_overwrite_replaces()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var target = new LocalFolderTarget(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "old");
        var keep = new FileRestorePipelineOptions { Mode = FileRestoreMode.Keep };
        var hello = Fixtures.Manifest("ext4-gpt.p1").Single(e => e.Path == "hello.txt").Hash;

        var observer = new RecordingObserver();
        Assert.Equal(1, FileRestorePipeline.Run(fs, [GuestPath.Parse("hello.txt")], target, keep, observer, Ct).Renamed);
        FileRestorePipeline.Run(fs, [GuestPath.Parse("hello.txt")], target, keep, observer, Ct);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_root, "hello.txt")));
        Assert.Equal(hello, Sha256(Path.Combine(_root, "RESTORED-hello.txt")));
        Assert.Equal(hello, Sha256(Path.Combine(_root, "RESTORED-hello (2).txt")));
        Assert.Equal(["local:/RESTORED-hello.txt", "local:/RESTORED-hello (2).txt"], observer.Renames);

        var result = FileRestorePipeline.Run(fs, [GuestPath.Parse("hello.txt")], target, new FileRestorePipelineOptions { Mode = FileRestoreMode.Overwrite }, new RecordingObserver(), Ct);
        Assert.Equal(0, result.Renamed);
        Assert.Equal(hello, Sha256(Path.Combine(_root, "hello.txt")));
        Assert.Equal(Convert.ToInt32("644", 8), target.Metadata["hello.txt"].UnixMode);
        Assert.Null(target.Metadata["hello.txt"].CreatedUtc);
    }

    [Fact]
    public void Conflicts_invalid_names_and_links_are_reported_and_the_rest_is_restored()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var target = new LocalFolderTarget(_root) { NameRule = name => name.StartsWith("m0", StringComparison.Ordinal) ? "not allowed in this test" : null };
        Directory.CreateDirectory(Path.Combine(_root, "hello.txt"));
        var observer = new RecordingObserver();

        var result = FileRestorePipeline.Run(
            fs, [.. new[] { "folder", "link-to-hello", "hello.txt", "secret.txt", "medium" }.Select(GuestPath.Parse)], target, new FileRestorePipelineOptions { MaxFailuresInARow = 3 }, observer, Ct);

        Assert.Equal(1 + 1 + 40, result.Files);
        Assert.Equal(11, result.Failed);
        Assert.Equal(1, result.Skipped);
        Assert.Contains(observer.Problems, p => p is { Kind: FileRestoreProblemKind.Skipped, Source: "/link-to-hello" } && p.Reason.Contains("symbolic link to hello.txt", StringComparison.Ordinal));
        Assert.Contains(observer.Problems, p => p is { Kind: FileRestoreProblemKind.Failed, Source: "/hello.txt" } && p.Reason.Contains("folder with this name exists", StringComparison.Ordinal));
        Assert.Contains(observer.Problems, p => p is { Kind: FileRestoreProblemKind.Failed, Source: "/medium/m00.txt", Reason: "not allowed in this test" });
        Assert.True(File.Exists(Path.Combine(_root, "medium", "m49.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "link-to-hello")));
    }

    [Fact]
    public void A_target_that_keeps_failing_stops_the_restore()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        var target = new LocalFolderTarget(_root) { WriteFailure = _ => new IOException("disk full") };

        var ex = Assert.Throws<IOException>(() => FileRestorePipeline.Run(
            fs, [GuestPath.Parse("medium")], target, new FileRestorePipelineOptions { MaxFailuresInARow = 5 }, new RecordingObserver(), Ct));
        Assert.StartsWith("Stopped after 5 failures in a row.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("disk full", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_connection_stops_the_restore_at_once()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        LocalFolderTarget? target = null;
        target = new LocalFolderTarget(_root)
        {
            WriteFailure = _ =>
            {
                target!.IsUsable = false;
                return new IOException("connection reset by fs01 (10.0.0.5):445");
            },
        };

        var ex = Assert.Throws<IOException>(() => FileRestorePipeline.Run(fs, [GuestPath.Parse("medium")], target, new FileRestorePipelineOptions(), new RecordingObserver(), Ct));
        Assert.StartsWith("The connection to the target was lost: connection reset by fs01 (10.0.0.5):445", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_ends_the_restore_between_files()
    {
        using var image = GuestImage.Open([Fixtures.Disk("ext4-gpt")]);
        var fs = image.OpenFileSystem("0:2048");
        using var stop = new CancellationTokenSource();
        var observer = new RecordingObserver { OnFileRestored = files => { if (files == 3) stop.Cancel(); } };

        Assert.ThrowsAny<OperationCanceledException>(() => FileRestorePipeline.Run(fs, [GuestPath.Parse("medium")], new LocalFolderTarget(_root), new FileRestorePipelineOptions(), observer, stop.Token));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "medium")).Length);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class RecordingObserver : IFileRestoreObserver
    {
        public (long Files, long Directories, long Bytes) Plan { get; private set; }

        public List<FileRestoreProblem> Problems { get; } = [];

        public List<string> Renames { get; } = [];

        public Action<long>? OnFileRestored { get; init; }

        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes) => Plan = (files, directories, bytes);

        public void Copied(long bytes)
        {
        }

        public void FileRestored(long files) => OnFileRestored?.Invoke(files);

        public void Renamed(string source, string target) => Renames.Add(target);

        public void Problem(FileRestoreProblem problem) => Problems.Add(problem);
    }

    /// <summary>A local folder as restore target; records the metadata it cannot store on Linux (attributes, creation time).</summary>
    private sealed class LocalFolderTarget(string root) : IFileRestoreTarget
    {
        public Func<string, string?> NameRule { get; init; } = _ => null;

        public Func<IReadOnlyList<string>, Exception?> WriteFailure { get; init; } = _ => null;

        public Dictionary<string, RestoredItemMeta> Metadata { get; } = [];

        public bool IsUsable { get; set; } = true;

        public string Describe(IReadOnlyList<string> path) => "local:/" + string.Join('/', path);

        public string? CheckName(string name) => NameRule(name);

        public TargetEntryKind Probe(IReadOnlyList<string> path)
        {
            var full = Full(path);
            var info = new FileInfo(full);
            if (info.LinkTarget is not null)
            {
                return TargetEntryKind.Other;
            }

            return Directory.Exists(full) ? TargetEntryKind.Directory : File.Exists(full) ? TargetEntryKind.File : TargetEntryKind.Missing;
        }

        public void CreateDirectory(IReadOnlyList<string> path) => Directory.CreateDirectory(Full(path));

        public void WriteFile(IReadOnlyList<string> path, Stream content, long length, bool replace, CancellationToken cancellationToken)
        {
            if (WriteFailure(path) is { } failure)
            {
                throw failure;
            }

            var full = Full(path);
            var temp = full + ".tmp";
            using (var output = File.Create(temp))
            {
                content.CopyTo(output);
                if (output.Length != length)
                {
                    throw new IOException("length mismatch");
                }
            }

            File.Move(temp, full, overwrite: replace);
        }

        public void SetMetadata(IReadOnlyList<string> path, RestoredItemMeta meta, bool isDirectory)
        {
            Metadata[string.Join('/', path)] = meta;
            if (meta.ModifiedUtc is { } modified)
            {
                if (isDirectory)
                {
                    Directory.SetLastWriteTimeUtc(Full(path), modified);
                }
                else
                {
                    File.SetLastWriteTimeUtc(Full(path), modified);
                }
            }
        }

        public void Dispose()
        {
        }

        private string Full(IReadOnlyList<string> path) => Path.Combine([root, .. path]);
    }
}
