namespace Backdafuckup.FileLevel;

/// <summary>
/// One file system of a guest volume. Not thread-safe: one instance per thread (see <see cref="GuestImage"/>).
/// Paths are never resolved through links; a link inside a path is an error.
/// </summary>
public interface IGuestFileSystem : IDisposable
{
    GuestFileSystemFamily Family { get; }

    /// <summary>"NTFS", "FAT", "ext" or "XFS".</summary>
    string Type { get; }

    GuestEntry Stat(GuestPath path);

    /// <summary>All entries of a folder (unsorted).</summary>
    IReadOnlyList<GuestEntry> List(GuestPath directory);

    /// <summary>Opens a regular file. Throws <see cref="GuestFileUnsupportedException"/> for content we cannot read correctly.</summary>
    Stream OpenRead(GuestPath path);

    /// <summary>The NTFS security descriptor (self-relative binary form), or null where the file system has none.</summary>
    byte[]? GetSecurityDescriptor(GuestPath path);
}

/// <summary>
/// Files larger than this are reported as unsupported: a corrupt inode can claim exabytes, which would read as an
/// endless stream of zeros. Set by <see cref="GuestImage"/> from the volume size.
/// </summary>
internal interface ISizeBoundedFileSystem
{
    long MaxFileSize { get; set; }
}

internal static class FileSizeBound
{
    /// <summary>16 times the volume, at least 1 TiB: real sparse files that large are very unusual.</summary>
    public static long For(long volumeLength) => Math.Max(1L << 40, volumeLength > long.MaxValue / 16 ? long.MaxValue : volumeLength * 16);

    public static string? Check(long size, long max) =>
        size > max ? $"implausible size of {size:N0} bytes for this volume (corrupt metadata?)" : null;
}
