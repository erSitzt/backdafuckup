using System;
using System.IO;

namespace Backdafuckup.Agent.Core;

public enum RestorePathKind
{
    Missing,
    File,
    Directory,

    /// <summary>A symbolic link, junction or other reparse point: never written through or replaced.</summary>
    Link,
}

/// <summary>
/// File operations of a file restore in the guest. Paths are absolute Windows paths (<c>C:\Users\x\a.txt</c>) on every
/// platform; the Windows implementation adds <c>\\?\</c>, the tests map them into a sandbox folder.
/// </summary>
public interface IRestoreFileSystem
{
    /// <summary>Why nothing may be restored below <paramref name="root"/> (e.g. not a local fixed volume), or null.</summary>
    string? CheckRoot(string root);

    /// <summary>Why this one path must not be written (e.g. the agent's own folder), or null.</summary>
    string? CheckPath(string path);

    RestorePathKind Probe(string path);

    void CreateDirectory(string path);

    /// <summary>Creates a new, empty temporary file in the folder of <paramref name="path"/>.</summary>
    Stream CreateTemporary(string path, out string temporaryPath);

    /// <summary>Renames the temporary file to <paramref name="path"/>; an existing file is replaced only with <paramref name="replace"/>.</summary>
    void Commit(string temporaryPath, string path, bool replace);

    void DeleteTemporary(string temporaryPath);

    void SetTimesAndAttributes(string path, bool isDirectory, DateTime? createdUtc, DateTime? modifiedUtc, DateTime? accessedUtc, FileAttributes attributes);

    /// <summary>Applies owner, group and DACL of a self-relative security descriptor.</summary>
    void SetSecurity(string path, bool isDirectory, byte[] descriptor);

    /// <summary>Renames a regular file in its folder or volume; <paramref name="newPath"/> must not exist.</summary>
    void Rename(string path, string newPath);

    /// <summary>Deletes a regular file (never a folder or a link).</summary>
    void Delete(string path);

    /// <summary>
    /// Adds an "allow full control" entry for <paramref name="account"/> (a name such as <c>NT SERVICE\MSSQL$SQLEXPRESS</c>
    /// or a SID) to the file's DACL; the other entries stay.
    /// </summary>
    void GrantFullControl(string path, string account);
}

/// <summary>
/// <see cref="IRestoreFileSystem"/> with System.IO. Subclasses map the Windows path to a real one (<see cref="Map"/>) and
/// add what only Windows has (permissions, volume checks).
/// </summary>
public abstract class PortableRestoreFileSystem : IRestoreFileSystem
{
    private const FileAttributes Blocking = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;

    public abstract string? CheckRoot(string root);

    public virtual string? CheckPath(string path) => null;

    public RestorePathKind Probe(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(Map(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return RestorePathKind.Missing;
        }

        return (attributes & FileAttributes.ReparsePoint) != 0 ? RestorePathKind.Link
            : (attributes & FileAttributes.Directory) != 0 ? RestorePathKind.Directory
            : RestorePathKind.File;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Map(path));

    public Stream CreateTemporary(string path, out string temporaryPath)
    {
        temporaryPath = WindowsPath.Join(WindowsPath.Parent(path), ".bdfu-restore-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
        return new FileStream(Map(temporaryPath), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
    }

    public void Commit(string temporaryPath, string path, bool replace)
    {
        var target = Map(path);
        if (replace && File.Exists(target))
        {
            // Replacing fails on read-only (and on some servers hidden or system) files; the restored attributes follow.
            var attributes = File.GetAttributes(target);
            if ((attributes & Blocking) != 0)
            {
                File.SetAttributes(target, attributes & ~Blocking);
            }

            File.Replace(Map(temporaryPath), target, null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(Map(temporaryPath), target);
    }

    public void DeleteTemporary(string temporaryPath)
    {
        try
        {
            File.Delete(Map(temporaryPath));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Left behind; named .bdfu-restore-*.tmp.
        }
    }

    public void SetTimesAndAttributes(string path, bool isDirectory, DateTime? createdUtc, DateTime? modifiedUtc, DateTime? accessedUtc, FileAttributes attributes)
    {
        var mapped = Map(path);
        if (isDirectory)
        {
            SetTimes(mapped, createdUtc, modifiedUtc, accessedUtc, Directory.SetCreationTimeUtc, Directory.SetLastWriteTimeUtc, Directory.SetLastAccessTimeUtc);
        }
        else
        {
            SetTimes(mapped, createdUtc, modifiedUtc, accessedUtc, File.SetCreationTimeUtc, File.SetLastWriteTimeUtc, File.SetLastAccessTimeUtc);
        }

        var current = File.GetAttributes(mapped);
        var wanted = (current & ~(Blocking | FileAttributes.Archive)) | (attributes & (Blocking | FileAttributes.Archive));
        if (wanted != current)
        {
            File.SetAttributes(mapped, wanted == 0 ? FileAttributes.Normal : wanted);
        }
    }

    public virtual void SetSecurity(string path, bool isDirectory, byte[] descriptor) =>
        throw new PlatformNotSupportedException("permissions can only be restored on Windows");

    public void Rename(string path, string newPath)
    {
        Guard(path);
        Guard(newPath);
        if (Probe(path) != RestorePathKind.File)
        {
            throw new IOException($"{path} is not a regular file.");
        }

        if (Probe(newPath) != RestorePathKind.Missing)
        {
            throw new IOException($"{newPath} exists already.");
        }

        File.Move(Map(path), Map(newPath));
    }

    public void Delete(string path)
    {
        Guard(path);
        switch (Probe(path))
        {
            case RestorePathKind.Missing:
                return;
            case RestorePathKind.File:
                var mapped = Map(path);
                var attributes = File.GetAttributes(mapped);
                if ((attributes & Blocking) != 0)
                {
                    File.SetAttributes(mapped, attributes & ~Blocking);
                }

                File.Delete(mapped);
                return;
            default:
                throw new IOException($"{path} is not a regular file; it is not deleted.");
        }
    }

    public virtual void GrantFullControl(string path, string account) =>
        throw new PlatformNotSupportedException("permissions can only be set on Windows");

    /// <summary>The same path guard as for restored items: never the agent's own folder.</summary>
    protected void Guard(string path)
    {
        if (CheckPath(path) is string refused)
        {
            throw new UnauthorizedAccessException($"{path}: {refused}.");
        }
    }

    /// <summary>The real path for an absolute Windows path.</summary>
    protected abstract string Map(string windowsPath);

    private static void SetTimes(string path, DateTime? created, DateTime? modified, DateTime? accessed, Action<string, DateTime> setCreated, Action<string, DateTime> setModified, Action<string, DateTime> setAccessed)
    {
        if (created is DateTime c)
        {
            setCreated(path, DateTime.SpecifyKind(c, DateTimeKind.Utc));
        }

        if (modified is DateTime m)
        {
            setModified(path, DateTime.SpecifyKind(m, DateTimeKind.Utc));
        }

        if (accessed is DateTime a)
        {
            setAccessed(path, DateTime.SpecifyKind(a, DateTimeKind.Utc));
        }
    }
}

/// <summary>Absolute Windows paths as strings, the same on every platform (the agent's tests run on Linux).</summary>
public static class WindowsPath
{
    /// <summary><c>C:\a\b</c> → <c>C:\a</c>; <c>C:\a</c> → <c>C:\</c>.</summary>
    public static string Parent(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var index = trimmed.LastIndexOf('\\');
        return index <= 2 ? trimmed.Substring(0, Math.Min(trimmed.Length, 2)) + "\\" : trimmed.Substring(0, index);
    }

    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('\\');
        return trimmed.Substring(trimmed.LastIndexOf('\\') + 1);
    }

    public static string Join(string folder, string name) => folder.TrimEnd('\\') + "\\" + name;

    /// <summary>True if <paramref name="path"/> is <paramref name="root"/> or below it (case-insensitive, whole components).</summary>
    public static bool IsBelow(string path, string root)
    {
        var r = root.TrimEnd('\\') + "\\";
        var p = path.TrimEnd('\\') + "\\";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A drive-absolute path without relative components: <c>X:\…</c>, no <c>.</c>/<c>..</c>, no empty components, no
    /// alternate data streams, no characters Windows does not allow.
    /// </summary>
    public static bool IsValidAbsolute(string path)
    {
        if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        var rest = path.Substring(3).TrimEnd('\\');
        if (rest.Length == 0)
        {
            return true;
        }

        foreach (var part in rest.Split('\\'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c < ' ')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
