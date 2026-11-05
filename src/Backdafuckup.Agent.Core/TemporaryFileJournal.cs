using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The folders in which the running restore creates temporary files (<c>.bdfu-restore-*.tmp</c>). A restore removes its
/// temporary file itself when a file fails; only a crash, a kill or a reboot leaves one behind, which can be as large as
/// the file it was to become. The agent deletes those at its next start (<see cref="Sweep"/>).
/// </summary>
public sealed class TemporaryFileJournal
{
    public const string FileName = "restore-temporaries.json";

    private readonly string _path;
    private readonly HashSet<string> _folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public TemporaryFileJournal(string agentDirectory)
    {
        _path = Path.Combine(agentDirectory, FileName);
    }

    /// <summary>Notes a folder before the first temporary file is created in it.</summary>
    public void Record(string folder)
    {
        lock (_folders)
        {
            if (_folders.Add(folder))
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_folders.ToList(), AgentJson.Options));
            }
        }
    }

    /// <summary>The restore ended (it removed its temporary files itself).</summary>
    public void Clear()
    {
        lock (_folders)
        {
            _folders.Clear();
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    /// <summary>At agent start: deletes the temporary files left in the recorded folders by an interrupted restore.</summary>
    public static void Sweep(IRestoreFileSystem fs, string agentDirectory, Action<string> log)
    {
        var path = Path.Combine(agentDirectory, FileName);
        if (!File.Exists(path))
        {
            return;
        }

        List<string> folders;
        try
        {
            folders = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), AgentJson.Options) ?? new List<string>();
        }
        catch (JsonException ex)
        {
            log($"The list of folders of an interrupted restore ({path}) cannot be read: {ex.Message}");
            File.Delete(path);
            return;
        }

        var deleted = new List<string>();
        var failed = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                foreach (var temporary in fs.Temporaries(folder))
                {
                    try
                    {
                        fs.Delete(temporary);
                        deleted.Add(temporary);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        failed.Add($"{temporary} ({ex.Message})");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                failed.Add($"{folder} ({ex.Message})");
            }
        }

        if (deleted.Count > 0 || failed.Count > 0)
        {
            log($"A restore was interrupted before this start: deleted {deleted.Count} partly written temporary file(s)" +
                (deleted.Count > 0 ? $" ({string.Join(", ", deleted)})" : string.Empty) +
                (failed.Count > 0 ? $"; could not delete: {string.Join("; ", failed)}" : string.Empty) + ".");
        }

        File.Delete(path);
    }
}
