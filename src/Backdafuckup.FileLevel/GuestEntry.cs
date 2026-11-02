namespace Backdafuckup.FileLevel;

public enum GuestEntryKind
{
    File,
    Directory,

    /// <summary>A symbolic link (Unix, or an NTFS symlink reparse point). Never followed.</summary>
    Symlink,

    /// <summary>An NTFS junction (mount point reparse point). Never followed.</summary>
    Junction,

    /// <summary>Another NTFS reparse point that is neither a link nor readable data.</summary>
    ReparsePoint,

    /// <summary>A device, FIFO or socket.</summary>
    Special,
}

public enum GuestFileSystemFamily
{
    Windows,
    Unix,
}

/// <summary>A file or folder in a guest volume as stored (links are reported, not followed).</summary>
public sealed record GuestEntry
{
    public required string Name { get; init; }

    public required GuestEntryKind Kind { get; init; }

    public long Size { get; init; }

    public DateTime? ModifiedUtc { get; init; }

    /// <summary>Creation time (Windows) or inode change time (Unix).</summary>
    public DateTime? CreatedUtc { get; init; }

    public DateTime? AccessedUtc { get; init; }

    /// <summary>Windows file attributes (NTFS, FAT).</summary>
    public FileAttributes? Attributes { get; init; }

    /// <summary>Unix permission bits (mode &amp; 07777).</summary>
    public int? Mode { get; init; }

    public long? UserId { get; init; }

    public long? GroupId { get; init; }

    public string? LinkTarget { get; init; }

    /// <summary>Inode or MFT record number (cycle detection, hard links).</summary>
    public long? FileId { get; init; }

    public int LinkCount { get; init; } = 1;

    /// <summary>Why the content of this entry cannot be restored (e.g. deduplicated or encrypted); null if it can.</summary>
    public string? Unsupported { get; init; }

    public bool IsDirectory => Kind == GuestEntryKind.Directory;
}

/// <summary>An entry exists, but its content cannot be read correctly (we refuse instead of returning zeros).</summary>
public sealed class GuestFileUnsupportedException(string path, string reason)
    : IOException($"{path}: {reason}")
{
    public string Path { get; } = path;

    public string Reason { get; } = reason;
}
