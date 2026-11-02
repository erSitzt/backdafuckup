// Added by the backdafuckup project (MIT, like the rest of this assembly; see NOTICE.md).
using System;

namespace DiscUtils.Ext;

/// <summary>
/// A directory entry as stored, without following symbolic links (lstat semantics) and with the name as stored
/// (upstream sanitizes \ / * ? : to _ in names it returns).
/// </summary>
public sealed class RawUnixEntry
{
    public string Name { get; init; } = string.Empty;

    /// <summary>False when the stored name bytes are not valid in the file name encoding (UTF-8).</summary>
    public bool NameIsExact { get; init; } = true;

    public ulong InodeNumber { get; init; }

    public UnixFileType FileType { get; init; }

    /// <summary>Permission bits including setuid, setgid and sticky (mode &amp; 07777).</summary>
    public int Mode { get; init; }

    public long UserId { get; init; }

    public long GroupId { get; init; }

    public long Length { get; init; }

    public long LinkCount { get; init; }

    public DateTime AccessTimeUtc { get; init; }

    public DateTime ModificationTimeUtc { get; init; }

    public DateTime ChangeTimeUtc { get; init; }
}
