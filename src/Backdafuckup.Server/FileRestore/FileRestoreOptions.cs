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

    /// <summary>How long a ZIP download link works (it also works only once and only for the user who made it).</summary>
    public TimeSpan DownloadLinkLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Files and folders in one ZIP download; larger selections should be restored to a share.</summary>
    public long MaxZipEntries { get; set; } = 1_000_000;

    /// <summary>Files and folders in one restore into a VM (the plan is kept in memory while the agent works).</summary>
    public long MaxAgentTransferItems { get; set; } = 1_000_000;
}
