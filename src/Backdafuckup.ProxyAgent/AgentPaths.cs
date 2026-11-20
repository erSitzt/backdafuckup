namespace Backdafuckup.ProxyAgent;

/// <summary>Where the agent keeps its settings, key and log, and (Windows) its program.</summary>
internal static class AgentPaths
{
    /// <summary>Linux: /etc/backdafuckup-proxy. Windows: %ProgramData%\backdafuckup-proxy (SYSTEM and Administrators only).</summary>
    public static string DataDirectory =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "backdafuckup-proxy")
            : "/etc/backdafuckup-proxy";

    public static string Config => Path.Combine(DataDirectory, "agent.json");

    /// <summary>The Windows agent's log (on Linux the journal has the console output).</summary>
    public static string Log => Path.Combine(DataDirectory, "proxy.log");

    /// <summary>Windows: %ProgramFiles%\backdafuckup Proxy.</summary>
    public static string ProgramDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "backdafuckup Proxy");
}

/// <summary>
/// The agent's log: the console (the journal on Linux), and on Windows also <see cref="AgentPaths.Log"/>, kept to 5 MiB
/// with one older file.
/// </summary>
internal static class AgentLog
{
    private const long MaxSize = 5L * 1024 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        Console.WriteLine(message);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var path = AgentPaths.Log;
                if (File.Exists(path) && new FileInfo(path).Length > MaxSize)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is for people; the agent goes on without it.
            }
        }
    }
}
