using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The agent's durable queue of SQL Server backup files (<c>sql-backups\queue.json</c> in the agent's folder). An entry
/// is written before its BACKUP starts; it gets the backup set and the file's hash once msdb lists the backup, and goes
/// away only after the server committed the file. So an agent crash never loses a backup and never reports one that
/// did not finish. Written durably (temp file, flush, replace); a corrupt file is moved aside and reported.
/// </summary>
public sealed class SqlBackupQueue
{
    public const string FolderName = "sql-backups";
    public const string FileName = "queue.json";
    private readonly string _path;
    private readonly object _lock = new object();
    private List<SqlBackupQueueEntry> _entries;

    private SqlBackupQueue(string path, List<SqlBackupQueueEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Loads the queue of <paramref name="agentDirectory"/>; a corrupt file is renamed and <paramref name="log"/> says so.</summary>
    public static SqlBackupQueue Load(string agentDirectory, Action<string> log)
    {
        var folder = Path.Combine(agentDirectory, FolderName);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        var entries = new List<SqlBackupQueueEntry>();
        if (File.Exists(path))
        {
            try
            {
                entries = JsonSerializer.Deserialize<List<SqlBackupQueueEntry>>(File.ReadAllText(path), AgentJson.Options) ?? new List<SqlBackupQueueEntry>();
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                var aside = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(path, aside);
                log($"the SQL backup queue {path} was unreadable ({ex.Message}); it was moved to {aside}. Backup files it listed stay in the staging folders until removed by hand.");
            }
        }

        return new SqlBackupQueue(path, entries);
    }

    public List<SqlBackupQueueEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public void Add(SqlBackupQueueEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            Save();
        }
    }

    public void Update(SqlBackupQueueEntry entry)
    {
        lock (_lock)
        {
            var index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0)
            {
                throw new InvalidOperationException($"The SQL backup queue has no entry {entry.Id}.");
            }

            _entries[index] = entry;
            Save();
        }
    }

    public void Remove(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        lock (_lock)
        {
            if (_entries.RemoveAll(e => set.Contains(e.Id)) > 0)
            {
                Save();
            }
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(_entries, AgentJson.Options);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }
}

/// <summary>One backup file in <see cref="SqlBackupQueue"/>.</summary>
public sealed class SqlBackupQueueEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Instance { get; set; } = string.Empty;

    public string? ProductVersion { get; set; }

    public string Database { get; set; } = string.Empty;

    /// <summary><see cref="SqlBackupActions.Log"/>, <see cref="SqlBackupActions.Full"/> or <c>system</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The backup file in the guest.</summary>
    public string Path { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public Guid? BackupJobId { get; set; }

    public Guid? SessionId { get; set; }

    /// <summary>Set once msdb lists the finished backup; null while the BACKUP runs (or if it was interrupted).</summary>
    public SqlBackupSetFacts? BackupSet { get; set; }

    public string? Sha256 { get; set; }

    public long Length { get; set; }

    public bool IsComplete => BackupSet is not null && Sha256 is not null;
}
