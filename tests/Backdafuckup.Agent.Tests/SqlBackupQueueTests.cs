using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

/// <summary>The agent's durable queue of SQL Server backup files: survives a restart, never turns a corrupt file into an empty queue silently.</summary>
[Trait("Category", "Unit")]
public sealed class SqlBackupQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-queue-" + Guid.NewGuid().ToString("N"));

    public SqlBackupQueueTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Entries_survive_reloading_and_go_away_when_removed()
    {
        var queue = SqlBackupQueue.Load(_root, _ => { });
        var started = new SqlBackupQueueEntry { Instance = "SQLEXPRESS", Database = "shop", Kind = SqlBackupActions.Log, Path = @"C:\Backup\shop.trn", StartedUtc = DateTime.UtcNow };
        queue.Add(started);
        queue.Add(new SqlBackupQueueEntry { Instance = "SQLEXPRESS", Database = "small", Kind = SqlBackupActions.Full, Path = @"C:\Backup\small.bak" });
        started.BackupSet = new SqlBackupSetFacts { BackupSetUuid = Guid.NewGuid().ToString(), FirstLsn = "1", LastLsn = "2" };
        started.Sha256 = "ab";
        started.Length = 3;
        queue.Update(started);

        var reloaded = SqlBackupQueue.Load(_root, _ => { });
        Assert.Equal([("shop", true), ("small", false)], reloaded.Entries.Select(e => (e.Database, e.IsComplete)));
        Assert.Equal("2", reloaded.Entries[0].BackupSet!.LastLsn);
        Assert.False(File.Exists(Path.Combine(_root, SqlBackupQueue.FolderName, SqlBackupQueue.FileName + ".tmp")));

        reloaded.Remove([started.Id]);
        Assert.Equal(["small"], SqlBackupQueue.Load(_root, _ => { }).Entries.Select(e => e.Database));
    }

    [Fact]
    public void A_corrupt_queue_is_moved_aside_and_reported()
    {
        var folder = Path.Combine(_root, SqlBackupQueue.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SqlBackupQueue.FileName), "[{ \"id\": ");
        var log = new List<string>();

        var queue = SqlBackupQueue.Load(_root, log.Add);

        Assert.Empty(queue.Entries);
        Assert.Single(Directory.GetFiles(folder, SqlBackupQueue.FileName + ".corrupt-*"));
        Assert.Contains(log, l => l.Contains("was unreadable", StringComparison.Ordinal) && l.Contains("stay in the staging folders", StringComparison.Ordinal));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
