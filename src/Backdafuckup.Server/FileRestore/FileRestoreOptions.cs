namespace Backdafuckup.Server.FileRestore;

/// <summary>Configuration section "FileRestore".</summary>
public sealed class FileRestoreOptions
{
    /// <summary>A file browser without requests for this long is closed (and its restore point released).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Open file browsers at the same time (each holds a restore point and parser state in memory).</summary>
    public int MaxMounts { get; set; } = 4;

    /// <summary>Shared cache of decompressed chunks for all browsers, downloads and restores.</summary>
    public long CacheBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Threads for short file system work (listing folders).</summary>
    public int WorkerThreads { get; set; } = 4;

    public int MaxPageSize { get; set; } = 1000;
}
