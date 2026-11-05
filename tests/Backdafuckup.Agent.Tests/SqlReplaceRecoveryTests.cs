using System.Text.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

/// <summary>
/// The agent's recovery of an overwrite that an agent crash or VM reboot interrupted: before the database came online it
/// gets its own files back; afterwards the overwrite stands and only the previous files are cleaned up.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqlReplaceRecoveryTests : IDisposable
{
    private const string File1 = @"D:\SQL\shop.mdf";
    private const string File2 = @"L:\Log\shop_log.ldf";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-replace-" + Guid.NewGuid().ToString("N"));
    private readonly Sandbox _fs;
    private readonly List<string> _log = [];

    public SqlReplaceRecoveryTests()
    {
        Directory.CreateDirectory(_root);
        _fs = new Sandbox(Path.Combine(_root, "fs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void After_the_database_came_online_the_restored_files_stay_and_the_previous_ones_are_deleted_or_kept(bool keepReplaced)
    {
        // Crashed after SET ONLINE, before the previous files were deleted.
        Write(File1, "restored data");
        Write(File1 + SqlReplace.ReplacedSuffix, "previous data");
        Write(File2, "restored log");
        Write(File2 + SqlReplace.ReplacedSuffix, "previous log");
        WriteJournal(new SqlReplace.Journal
        {
            Instance = "SQLEXPRESS", Database = "shop", Files = [File1, File2], Offline = true, Online = true, KeepReplaced = keepReplaced,
            Swapped = [File1, File2], MovedAside = [File1, File2],
        });

        SqlReplace.RecoverFiles(_fs, _root, _log.Add);

        Assert.Equal(("restored data", "restored log"), (Read(File1), Read(File2)));
        Assert.Equal(keepReplaced, File.Exists(_fs.Local(File1 + SqlReplace.ReplacedSuffix)));
        Assert.Equal(keepReplaced, File.Exists(_fs.Local(File2 + SqlReplace.ReplacedSuffix)));
        Assert.False(File.Exists(_fs.Local(File1 + SqlReplace.NewSuffix)));
        Assert.False(File.Exists(Path.Combine(_root, SqlReplace.JournalFile)));
        Assert.Contains(_log, l => l.Contains("interrupted after the database came online with the restored files; the overwrite is complete", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains(keepReplaced ? "kept the database's previous files" : "deleted the database's previous files", StringComparison.Ordinal));
    }

    [Fact]
    public void Before_the_database_came_online_it_gets_its_own_files_back()
    {
        // Crashed after the first file was swapped, while the database was offline.
        Write(File1, "restored data");
        Write(File1 + SqlReplace.ReplacedSuffix, "previous data");
        Write(File2, "previous log");
        Write(File2 + SqlReplace.NewSuffix, "restored log");
        WriteJournal(new SqlReplace.Journal
        {
            Instance = "SQLEXPRESS", Database = "shop", Files = [File1, File2], Offline = true, Swapped = [File1], MovedAside = [File1],
        });

        SqlReplace.RecoverFiles(_fs, _root, _log.Add);

        Assert.Equal(("previous data", "previous log"), (Read(File1), Read(File2)));
        Assert.Equal(("restored data", "restored log"), (Read(File1 + SqlReplace.NewSuffix), Read(File2 + SqlReplace.NewSuffix)));
        Assert.False(File.Exists(_fs.Local(File1 + SqlReplace.ReplacedSuffix)));
        Assert.False(File.Exists(Path.Combine(_root, SqlReplace.JournalFile)));
        Assert.Contains(_log, l => l.Contains("its own files are back in place", StringComparison.Ordinal) && l.Contains("ALTER DATABASE [shop] SET ONLINE", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fs.Local(path))!);
        File.WriteAllText(_fs.Local(path), content);
    }

    private string Read(string path) => File.ReadAllText(_fs.Local(path));

    private void WriteJournal(SqlReplace.Journal journal) =>
        File.WriteAllText(Path.Combine(_root, SqlReplace.JournalFile), JsonSerializer.Serialize(journal, AgentJson.Options));

    /// <summary>Windows paths in a local folder: <c>D:\a\b</c> is <c>{root}/D/a/b</c>.</summary>
    private sealed class Sandbox(string root) : PortableRestoreFileSystem
    {
        public override string? CheckRoot(string path) => null;

        public string Local(string windowsPath) => Map(windowsPath);

        protected override string Map(string windowsPath) =>
            Path.Combine([root, windowsPath[..1], .. windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
    }
}
