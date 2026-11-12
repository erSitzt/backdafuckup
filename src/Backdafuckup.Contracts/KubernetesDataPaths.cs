namespace Backdafuckup.Contracts;

/// <summary>How a Kubernetes volume's data is backed up, as the inventory and restore points name it.</summary>
public static class KubernetesDataPaths
{
    /// <summary>Through a CSI snapshot, read as a block volume.</summary>
    public const string Snapshot = "snapshot";

    /// <summary>File by file (NFS, hostPath, local), into a file tree.</summary>
    public const string Files = "files";

    /// <summary>Only its objects.</summary>
    public const string ObjectsOnly = "objects-only";

    public static bool HasData(string? dataPath) => dataPath is Snapshot or Files;

    /// <summary>A short label for the console.</summary>
    public static string Label(string? dataPath) => dataPath switch
    {
        Snapshot => "snapshot",
        Files => "files",
        _ => "objects only",
    };
}
