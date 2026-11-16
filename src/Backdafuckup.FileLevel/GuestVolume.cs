namespace Backdafuckup.FileLevel;

/// <summary>A disk of a restore point for <see cref="GuestImage"/>; <see cref="Open"/> creates a new stream per call.</summary>
/// <summary>
/// A disk of a restore point (its image through <see cref="Open"/>), or a volume backed up file by file
/// (<see cref="OpenTree"/>, its file system directly; <see cref="Open"/> is not called).
/// </summary>
public sealed record GuestDiskSource(string Key, string Name, Func<Stream> Open)
{
    /// <summary>A file tree: opens its file system (one per call, as images do).</summary>
    public Func<IGuestFileSystem>? OpenTree { get; init; }

    /// <summary>A file tree's bytes (its files' sizes).</summary>
    public long TreeBytes { get; init; }

    /// <summary>The volume id of a file tree (<c>&lt;key&gt;:files</c>).</summary>
    public string TreeVolumeId => $"{Key}:{FileTreeFileSystem.TypeName}";
}

/// <summary>A volume found on the disks of a restore point.</summary>
public sealed record GuestVolume
{
    /// <summary>
    /// Stable across openings of the same restore point: "&lt;disk key&gt;:&lt;start sector&gt;" for partitions and whole
    /// disks, "lvm:&lt;LV identity&gt;" for LVM logical volumes.
    /// </summary>
    public required string Id { get; init; }

    public string? DiskKey { get; init; }

    public string? DiskName { get; init; }

    /// <summary>Byte offset on the disk (0 for LVM logical volumes).</summary>
    public long StartOffset { get; init; }

    public long Length { get; init; }

    /// <summary>Partition or volume type as text (e.g. "Windows Basic Data", "Linux LVM root").</summary>
    public string? PartitionType { get; init; }

    public Guid? PartitionGuid { get; init; }

    public uint? MbrDiskSignature { get; init; }

    public string? LvmName { get; init; }

    /// <summary>"NTFS", "FAT", "ext" or "XFS"; null when no readable file system was found.</summary>
    public string? FileSystem { get; init; }

    public string? Label { get; init; }

    /// <summary>File system UUID as in /etc/fstab (ext, XFS).</summary>
    public string? FileSystemUuid { get; init; }

    /// <summary>NTFS volume serial number (lower 32 bits) as Windows shows it.</summary>
    public uint? VolumeSerial { get; init; }

    /// <summary>Drive letter ("C:") or mount point ("/var") from the guest's own configuration, if known.</summary>
    public string? MountPoint { get; init; }

    public bool Readable { get; init; }

    /// <summary>Why the volume cannot be browsed (encrypted, unsupported, corrupt, ...).</summary>
    public string? Reason { get; init; }
}
