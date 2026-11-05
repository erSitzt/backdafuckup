using Backdafuckup.Agent.Core;

namespace Backdafuckup.Agent.Tests;

/// <summary>Partly written temporary files of a restore that a crash interrupted are deleted at the next agent start.</summary>
[Trait("Category", "Unit")]
public sealed class TemporaryFileJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-temporaries-" + Guid.NewGuid().ToString("N"));
    private readonly Sandbox _fs;

    public TemporaryFileJournalTests()
    {
        Directory.CreateDirectory(_root);
        _fs = new Sandbox(Path.Combine(_root, "fs"));
    }

    [Fact]
    public void A_crash_leaves_temporary_files_that_the_next_start_deletes_and_nothing_else()
    {
        // The restore notes the folder, creates a temporary file in it, and the process dies.
        var journal = new TemporaryFileJournal(_root);
        journal.Record(@"D:\SQL");
        journal.Record(@"L:\Log");
        using (var stream = _fs.CreateTemporary(@"D:\SQL\shop_copy.mdf", out var temporary))
        {
            stream.Write(new byte[4096]);
            Assert.StartsWith(@"D:\SQL\.bdfu-restore-", temporary, StringComparison.Ordinal);
        }

        File.WriteAllText(_fs.Local(@"D:\SQL\shop.mdf"), "someone's database");
        File.WriteAllText(_fs.Local(@"D:\SQL\other.tmp"), "someone's temporary file");
        var log = new List<string>();

        TemporaryFileJournal.Sweep(_fs, _root, log.Add);

        Assert.Empty(_fs.Temporaries(@"D:\SQL"));
        Assert.True(File.Exists(_fs.Local(@"D:\SQL\shop.mdf")));
        Assert.True(File.Exists(_fs.Local(@"D:\SQL\other.tmp")));
        Assert.False(File.Exists(Path.Combine(_root, TemporaryFileJournal.FileName)));
        var line = Assert.Single(log);
        Assert.StartsWith(@"A restore was interrupted before this start: deleted 1 partly written temporary file(s) (D:\SQL\.bdfu-restore-", line, StringComparison.Ordinal);

        // Nothing recorded: nothing to do; a finished restore clears its journal.
        TemporaryFileJournal.Sweep(_fs, _root, log.Add);
        journal.Record(@"D:\SQL");
        journal.Clear();
        Assert.False(File.Exists(Path.Combine(_root, TemporaryFileJournal.FileName)));
        Assert.Single(log);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Windows paths in a local folder: <c>D:\a\b</c> is <c>{root}/D/a/b</c>.</summary>
    private sealed class Sandbox(string root) : PortableRestoreFileSystem
    {
        public override string? CheckRoot(string path) => null;

        public string Local(string windowsPath)
        {
            var local = Map(windowsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            return local;
        }

        protected override string Map(string windowsPath)
        {
            var local = Path.Combine([root, windowsPath[..1], .. windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            return local;
        }
    }
}
