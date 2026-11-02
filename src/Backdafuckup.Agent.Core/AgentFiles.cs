using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>JSON of the agent API: camelCase, case-insensitive (the same as the server).</summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>The agent's folder: program files plus <c>agent.json</c>, <c>agent.key</c> and the log.</summary>
public sealed class AgentPaths
{
    public AgentPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    public string BaseDirectory { get; }

    public string ConfigFile => Path.Combine(BaseDirectory, AgentProtocol.ConfigFile);

    public string KeyFile => Path.Combine(BaseDirectory, AgentProtocol.KeyFile);

    public string LogFile => Path.Combine(BaseDirectory, AgentProtocol.LogFile);

    /// <summary>Downloads of a self-update land here first; only complete and verified files are swapped in.</summary>
    public string StagingDirectory => Path.Combine(BaseDirectory, "update.tmp");
}

/// <summary>Hash over the installed program files, the same way the server hashes its agent build.</summary>
public static class AgentFiles
{
    /// <summary>Files in the agent folder that are not part of the program (configuration, key, log, leftovers).</summary>
    public static bool IsProgramFile(string name)
    {
        if (string.Equals(name, AgentProtocol.ConfigFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, AgentProtocol.KeyFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, AgentProtocol.LogFile, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(AgentProtocol.LogFile + ".", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "version.txt", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension != ".old" && extension != ".tmp";
    }

    public static List<AgentFile> List(string directory) =>
        Directory.GetFiles(directory)
            .Where(f => IsProgramFile(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .Select(f => new AgentFile { Name = Path.GetFileName(f), Size = new FileInfo(f).Length, Sha256 = Sha256Of(f) })
            .ToList();

    /// <summary>SHA-256 over the lines <c>name:sha256</c>, sorted by name and joined with LF (as <c>AgentPackages.FilesHash</c> on the server).</summary>
    public static string Hash(IEnumerable<AgentFile> files)
    {
        var text = string.Join("\n", files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Name + ":" + f.Sha256));
        using (var sha = SHA256.Create())
        {
            return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }
    }

    public static string Sha256Of(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            return Hex(sha.ComputeHash(stream));
        }
    }

    /// <summary>Removes <c>*.old</c> files left by a self-update (they were in use until the restart).</summary>
    public static void RemoveReplaced(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.old"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static string Hex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            builder.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}

/// <summary>A small log file next to the agent (rotated at 5 MB); the service runs without a console.</summary>
/// <remarks>
/// While VSS holds the writes of the volumes, a write to the log file would block until Windows gives up the hold (10 s)
/// – the agent would freeze itself. <see cref="Pause"/> keeps lines in memory until <see cref="Resume"/>.
/// </remarks>
public sealed class AgentLog
{
    private const long MaxSize = 5L * 1024 * 1024;
    private readonly object _lock = new object();
    private readonly string _path;
    private readonly List<string> _deferred = new List<string>();
    private bool _paused;

    public AgentLog(string path)
    {
        _path = path;
    }

    /// <summary>Also writes to the console (interactive runs and the setup).</summary>
    public bool Echo { get; set; }

    /// <summary>True while lines are kept in memory (guest frozen).</summary>
    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    /// <summary>From now on keep lines in memory: the volumes are about to be frozen.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
        }
    }

    /// <summary>Writes the lines kept in memory and continues writing directly.</summary>
    public void Resume()
    {
        List<string> lines;
        lock (_lock)
        {
            _paused = false;
            lines = new List<string>(_deferred);
            _deferred.Clear();
        }

        foreach (var line in lines)
        {
            Append(line);
        }
    }

    public void Write(string message)
    {
        var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + message;
        if (Echo)
        {
            Console.WriteLine(line);
        }

        lock (_lock)
        {
            if (_paused)
            {
                _deferred.Add(line);
                return;
            }
        }

        Append(line);
    }

    private void Append(string line)
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Copy(_path, _path + ".1", true);
                    File.Delete(_path);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
