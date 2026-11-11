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
    int IdleTimeoutMinutes,
    string VmNativeId,
    bool ConsistencyWarning = false,
    string ObjectKind = RestorePointObjectKinds.Vm);

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

/// <summary>Files and folders of one folder to download as a ZIP.</summary>
/// <param name="Paths">Paths inside the volume with "/" as separator; all in the same folder.</param>
/// <param name="TimeZone">IANA or Windows id of the browser's time zone, for the MS-DOS times in the ZIP.</param>
public sealed record FileDownloadRequestDto(string Volume, IReadOnlyList<string> Paths, string? TimeZone);

/// <param name="Url">Relative URL of the ZIP. It works once, for the same user, within <paramref name="ExpiresInSeconds"/>.</param>
public sealed record FileDownloadTicketDto(string Url, string FileName, int ExpiresInSeconds);

public enum FileRestoreModeDto
{
    /// <summary>Existing files are replaced.</summary>
    Overwrite,

    /// <summary>Existing files stay; restored files are written as RESTORED-&lt;name&gt;.</summary>
    Keep,
}

public enum FileRestoreTargetKindDto
{
    /// <summary>A folder on an SMB share (\\server\share\folder) with a Windows credential.</summary>
    Share,

    /// <summary>Into a Windows VM through its guest agent: the original location or another folder.</summary>
    Agent,

    /// <summary>Into a Linux VM over SSH/SFTP (root login or sudo): the original location or another folder.</summary>
    Ssh,
}

public sealed record FileRestoreTargetDto
{
    public FileRestoreTargetKindDto Kind { get; init; }

    /// <summary>Share target: \\server\share\folder. Missing folders are created.</summary>
    public string? SharePath { get; init; }

    /// <summary>Share target: a Windows credential (DOMAIN\user) with write access. SSH target: a Linux credential.</summary>
    public Guid? CredentialId { get; init; }

    /// <summary>SSH target: address of the VM; null tries the addresses its guest tools report.</summary>
    public string? Address { get; init; }

    /// <summary>SSH target: port (22 if null).</summary>
    public int? Port { get; init; }

    /// <summary>Agent target: the VM to restore into; null is the VM of the restore point.</summary>
    public string? VmNativeId { get; init; }

    /// <summary>Agent or SSH target: a folder in the VM (D:\Restore, /srv/restore); null restores to the original location.</summary>
    public string? Folder { get; init; }

    /// <summary>Agent target: owner and permissions from the backup. SSH target: owner and group from the backup.</summary>
    public bool RestorePermissions { get; init; }
}

/// <summary>Restores files and folders of one folder of a restore point volume (a session of kind FileRestore).</summary>
public sealed record FileRestoreRequestDto
{
    public required Guid RestorePointId { get; init; }

    public required string Volume { get; init; }

    /// <summary>Paths inside the volume with "/" as separator; all in the same folder.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    public FileRestoreModeDto Mode { get; init; }

    public required FileRestoreTargetDto Target { get; init; }

    /// <summary>The file browser the restore was started from; its session log records the restore.</summary>
    public Guid? MountId { get; init; }
}
