namespace Backdafuckup.Contracts;

/// <summary>Opens (or reuses) the file browser for a restore point.</summary>
public sealed record OpenFileMountRequestDto(Guid RestorePointId);

/// <summary>An open file browser: the restore point and the volumes found on its disks.</summary>
public sealed record FileMountDto(
    Guid Id,
    Guid RestorePointId,
    Guid SessionId,
    string VmName,
    DateTimeOffset RestorePointCreatedUtc,
    string Consistency,
    string? ConsistencyReason,
    IReadOnlyList<GuestVolumeDto> Volumes,
    IReadOnlyList<string> Warnings,
    int IdleTimeoutMinutes);

/// <param name="Title">Display name: drive letter or mount point, label, otherwise the id.</param>
/// <param name="Family">"windows" or "unix" for readable volumes.</param>
public sealed record GuestVolumeDto(
    string Id,
    string Title,
    string? MountPoint,
    string? Label,
    string? FileSystem,
    string? Family,
    long Length,
    string? DiskName,
    string? PartitionType,
    bool Readable,
    string? Reason);

/// <param name="Kind">"file", "directory", "symlink", "junction", "reparse" or "special".</param>
/// <param name="Unsupported">Why the entry cannot be restored, or null.</param>
public sealed record GuestEntryDto(
    string Name,
    string Kind,
    long Size,
    DateTimeOffset? ModifiedUtc,
    DateTimeOffset? CreatedUtc,
    string? Attributes,
    string? Mode,
    long? UserId,
    long? GroupId,
    string? LinkTarget,
    string? Unsupported);

public sealed record GuestEntryPageDto(string Volume, string Path, int Total, int Offset, IReadOnlyList<GuestEntryDto> Entries);
