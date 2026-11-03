using System.Text;

namespace Backdafuckup.FileLevel.Zip;

public sealed record GuestZipOptions
{
    /// <summary>Files and folders in one archive (each costs ~150 bytes of central directory).</summary>
    public long MaxEntries { get; init; } = 1_000_000;

    public int MaxDepth { get; init; } = 256;

    public ZipWriterOptions Writer { get; init; } = new();

    /// <summary>How a guest path is shown in the list of skipped items (e.g. "C:\Users\x" or "/home/x").</summary>
    public Func<GuestPath, string> Describe { get; init; } = path => path.ToString();
}

/// <summary>An item that is not in the archive, and why.</summary>
public sealed record GuestZipSkip(string Path, string Reason);

public sealed record GuestZipResult(long Files, long Directories, long ContentBytes, long ArchiveBytes, long SkippedCount, IReadOnlyList<GuestZipSkip> Skipped, string? SkippedListName);

/// <summary>
/// Writes selected files and folders of one guest folder as a ZIP. Names are made safe to extract (no "\", drive
/// letters, or names Windows reads as "." or ".."); links, special files and content we cannot read are left out and
/// listed in a text file inside the archive. A read error inside a file aborts the archive: a truncated file with a
/// valid checksum would look restored.
/// </summary>
public static class GuestZipExport
{
    /// <summary>Name of the list of skipped items at the archive root (only present when something was skipped).</summary>
    public const string SkippedListName = "_backdafuckup-skipped.txt";

    private const int KeptSkips = 10_000;

    public static GuestZipResult Write(IGuestFileSystem fs, IReadOnlyList<GuestPath> selection, Stream output, GuestZipOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        if (selection.Count == 0)
        {
            throw new ArgumentException("Select at least one file or folder.", nameof(selection));
        }

        var unix = fs.Family == GuestFileSystemFamily.Unix;
        var skipped = new List<GuestZipSkip>();
        long skippedCount = 0;
        long files = 0;
        long directories = 0;
        long content = 0;
        long count = 0;
        var visited = new HashSet<long>();

        void Skip(GuestPath path, string reason)
        {
            skippedCount++;
            if (skipped.Count < KeptSkips)
            {
                skipped.Add(new GuestZipSkip(options.Describe(path), reason));
            }
        }

        using var zip = new ZipStreamWriter(output, options.Writer);
        var top = selection.Select(p => (Path: p, Entry: fs.Stat(p))).ToList();
        var topNames = ZipNames.Unique(top.Select(t => t.Entry.Name).ToList());
        var stack = new Stack<(GuestPath Path, GuestEntry Entry, string ZipName, int Depth)>();
        for (var i = top.Count - 1; i >= 0; i--)
        {
            stack.Push((top[i].Path, top[i].Entry, topNames[i], 0));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, entry, zipName, depth) = stack.Pop();
            if (++count > options.MaxEntries)
            {
                throw new GuestWalkLimitException(
                    $"The selection contains more than {options.MaxEntries:N0} files and folders, too many for one ZIP download. Select fewer, or restore to a share.");
            }

            if (!ZipStreamWriter.FitsName(zipName + "/"))
            {
                Skip(path, "the path is too long for a ZIP file");
                continue;
            }

            switch (entry.Kind)
            {
                case GuestEntryKind.Directory:
                    if (entry.FileId is { } id && !visited.Add(id))
                    {
                        Skip(path, "the folder appears twice in the file system (damaged metadata?); its content is in the archive once");
                        continue;
                    }

                    zip.AddDirectory(new ZipEntryMeta(zipName, entry.ModifiedUtc, Host(unix), Attributes(entry, unix)));
                    directories++;
                    if (depth >= options.MaxDepth)
                    {
                        Skip(path, $"nested deeper than {options.MaxDepth} levels; the content of this folder is not in the archive");
                        continue;
                    }

                    IReadOnlyList<GuestEntry> children;
                    try
                    {
                        children = fs.List(path);
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        Skip(path, $"the folder cannot be read: {ex.Message}");
                        continue;
                    }

                    var names = ZipNames.Unique(children.Select(c => c.Name).ToList());
                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((path.Append(children[i].Name), children[i], zipName + "/" + names[i], depth + 1));
                    }

                    break;

                case GuestEntryKind.File when entry.Unsupported is not null:
                    Skip(path, entry.Unsupported);
                    break;

                case GuestEntryKind.File:
                    Stream input;
                    try
                    {
                        input = fs.OpenRead(path);
                    }
                    catch (GuestFileUnsupportedException ex)
                    {
                        Skip(path, ex.Reason);
                        continue;
                    }
                    catch (Exception ex) when (IsDataError(ex))
                    {
                        // Nothing of this entry is written yet, so leaving it out keeps the archive intact.
                        Skip(path, $"the file cannot be opened: {ex.Message}");
                        continue;
                    }

                    using (input)
                    {
                        var read = zip.AddFile(new ZipEntryMeta(zipName, entry.ModifiedUtc, Host(unix), Attributes(entry, unix)), entry.Size, input, cancellationToken);
                        if (read != entry.Size)
                        {
                            throw new InvalidDataException($"{options.Describe(path)} has {entry.Size:N0} bytes, but {read:N0} bytes were read (damaged file system metadata?).");
                        }

                        content += read;
                    }

                    files++;
                    break;

                case GuestEntryKind.Symlink:
                    Skip(path, $"symbolic link to {entry.LinkTarget} (ZIP downloads contain no links)");
                    break;

                case GuestEntryKind.Junction:
                    Skip(path, $"junction to {entry.LinkTarget} (ZIP downloads contain no links)");
                    break;

                default:
                    Skip(path, entry.Unsupported ?? (entry.Kind == GuestEntryKind.Special ? "device, FIFO or socket" : "reparse point without readable content"));
                    break;
            }
        }

        string? listName = null;
        if (skippedCount > 0)
        {
            listName = ZipNames.Unique([.. topNames, SkippedListName])[^1];
            var text = new StringBuilder();
            text.Append("These items of the selection are not in this ZIP file.\r\n");
            text.Append("Restore them into the VM or onto a share instead where possible.\r\n\r\n");
            foreach (var skip in skipped)
            {
                text.Append(skip.Path).Append('\t').Append(skip.Reason).Append("\r\n");
            }

            if (skippedCount > skipped.Count)
            {
                text.Append($"… and {skippedCount - skipped.Count:N0} more.\r\n");
            }

            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            using var list = new MemoryStream(bytes, writable: false);
            zip.AddFile(new ZipEntryMeta(listName, DateTime.UtcNow, ZipHost.Dos, (uint)FileAttributes.Archive), bytes.Length, list, cancellationToken);
        }

        zip.Finish();
        return new GuestZipResult(files, directories, content, zip.Position, skippedCount, skipped, listName);
    }

    /// <summary>
    /// Writes single files, possibly from different volumes, under the given names at the archive root (plus optional
    /// extra text files, e.g. a script). Every file must be readable: a file that cannot be read fails the archive,
    /// because a database without one of its files is useless.
    /// </summary>
    public static GuestZipResult WriteFiles(
        IReadOnlyList<(IGuestFileSystem Fs, GuestPath Path, string Name)> files,
        IReadOnlyList<(string Name, string Text)> extras,
        Stream output,
        GuestZipOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        var names = ZipNames.Unique([.. files.Select(f => f.Name), .. extras.Select(e => e.Name)]);
        using var zip = new ZipStreamWriter(output, options.Writer);
        long content = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (fs, path, _) = files[i];
            var entry = fs.Stat(path);
            if (entry.Kind != GuestEntryKind.File || entry.Unsupported is not null)
            {
                throw new InvalidDataException($"{options.Describe(path)} cannot be exported: {entry.Unsupported ?? "it is not a regular file"}.");
            }

            using var input = fs.OpenRead(path);
            var read = zip.AddFile(new ZipEntryMeta(names[i], entry.ModifiedUtc, ZipHost.Dos, Attributes(entry, unix: false)), entry.Size, input, cancellationToken);
            if (read != entry.Size)
            {
                throw new InvalidDataException($"{options.Describe(path)} has {entry.Size:N0} bytes, but {read:N0} bytes were read (damaged file system metadata?).");
            }

            content += read;
        }

        for (var i = 0; i < extras.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(extras[i].Text);
            using var text = new MemoryStream(bytes, writable: false);
            zip.AddFile(new ZipEntryMeta(names[files.Count + i], DateTime.UtcNow, ZipHost.Dos, (uint)FileAttributes.Archive), bytes.Length, text, cancellationToken);
        }

        zip.Finish();
        return new GuestZipResult(files.Count, 0, content, zip.Position, 0, [], null);
    }

    /// <summary>The image is untrusted input: a damaged folder or inode can make the parser throw nearly anything.</summary>
    private static bool IsDataError(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    private static ZipHost Host(bool unix) => unix ? ZipHost.Unix : ZipHost.Dos;

    private static uint Attributes(GuestEntry entry, bool unix)
    {
        const uint dosDirectory = 0x10;
        if (unix)
        {
            var type = entry.IsDirectory ? 0x4000u : 0x8000u;
            var mode = (uint)(entry.Mode ?? (entry.IsDirectory ? 0x1ED : 0x1A4)) & 0xFFF;
            return ((type | mode) << 16) | (entry.IsDirectory ? dosDirectory : 0);
        }

        const FileAttributes kept = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
        var dos = (uint)((entry.Attributes ?? 0) & kept);
        return entry.IsDirectory ? dos | dosDirectory : dos;
    }
}

/// <summary>Entry names that extract safely everywhere and are unique among their siblings.</summary>
public static class ZipNames
{
    /// <summary>
    /// Replaces what extractors could read as a path separator, a drive or a relative component: "\", control
    /// characters, a leading drive letter ("C:"), and names of only dots and spaces (Windows trims those to "." or
    /// ".."). Everything else stays as stored, including characters Windows does not allow.
    /// </summary>
    public static string Sanitize(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '\\' or '/' || char.IsControl(chars[i]))
            {
                chars[i] = '_';
            }
        }

        if (chars.Length >= 2 && chars[1] == ':' && char.IsAsciiLetter(chars[0]))
        {
            chars[1] = '_';
        }

        if (chars.All(c => c is '.' or ' '))
        {
            Array.Fill(chars, '_');
        }

        return new string(chars);
    }

    /// <summary>Sanitized names; a name that collides with a sibling gets " (2)", " (3)", … before its extension.</summary>
    public static string[] Unique(IReadOnlyList<string> names)
    {
        var result = new string[names.Count];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var later = new List<int>();

        // Names that need no change keep them; changed or duplicate names yield.
        for (var i = 0; i < names.Count; i++)
        {
            var safe = Sanitize(names[i]);
            if (safe == names[i] && taken.Add(safe))
            {
                result[i] = safe;
            }
            else
            {
                later.Add(i);
            }
        }

        foreach (var i in later)
        {
            var safe = Sanitize(names[i]);
            var candidate = safe;
            for (var n = 2; !taken.Add(candidate); n++)
            {
                candidate = WithSuffix(safe, n);
            }

            result[i] = candidate;
        }

        return result;
    }

    private static string WithSuffix(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }
}
