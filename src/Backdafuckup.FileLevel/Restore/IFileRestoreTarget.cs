namespace Backdafuckup.FileLevel.Restore;

public enum TargetEntryKind
{
    Missing,
    File,
    Directory,

    /// <summary>A link, junction or other reparse point: never written through or replaced.</summary>
    Other,
}

/// <summary>Times and attributes for a restored file or folder; a target applies what it supports.</summary>
/// <param name="CreatedUtc">Creation time (Windows sources only; Unix has none).</param>
/// <param name="WindowsAttributes">Read-only, hidden, system and archive of Windows sources.</param>
public sealed record RestoredItemMeta(DateTime? CreatedUtc, DateTime? ModifiedUtc, DateTime? AccessedUtc, FileAttributes? WindowsAttributes, int? UnixMode, long? UserId, long? GroupId);

/// <summary>
/// Where restored files go. Paths are name components below the target folder. Used from one thread. Errors are
/// thrown as exceptions with a message that names the target (host, address, path).
/// </summary>
public interface IFileRestoreTarget : IDisposable
{
    /// <summary>How a target path appears in the log, e.g. <c>\\fs01\restore\a\b.txt</c>.</summary>
    string Describe(IReadOnlyList<string> path);

    /// <summary>Why <paramref name="name"/> cannot be created on this target, or null if it can.</summary>
    string? CheckName(string name);

    /// <summary>False once the connection is lost; the restore then stops instead of failing every remaining file.</summary>
    bool IsUsable { get; }

    /// <summary>What exists at <paramref name="path"/>, without following links.</summary>
    TargetEntryKind Probe(IReadOnlyList<string> path);

    /// <summary>Creates one folder level; an existing folder is fine.</summary>
    void CreateDirectory(IReadOnlyList<string> path);

    /// <summary>
    /// Writes <paramref name="content"/> under a temporary name and renames it when complete, so a failure never leaves
    /// a partial file. With <paramref name="replace"/> an existing file is replaced, otherwise the rename fails if one
    /// appeared meanwhile. Fails without renaming if the content is not exactly <paramref name="length"/> bytes.
    /// </summary>
    void WriteFile(IReadOnlyList<string> path, Stream content, long length, bool replace, CancellationToken cancellationToken);

    void SetMetadata(IReadOnlyList<string> path, RestoredItemMeta meta, bool isDirectory);
}
