using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Mover;

/// <summary>
/// Puts the files of a <see cref="MoverFileReader"/> stream into a mounted, live volume, the way the SSH restore script
/// does in a VM: every item below <c>root</c> only, nothing written through a symbolic link; a file is written under a
/// hidden temporary name, flushed, given its owner, mode and times, and then renamed over the old one (or, keeping
/// existing files, to RESTORED-&lt;name&gt;). Symbolic links come after the files, folder times last (writing into a
/// folder changes them). Each item that cannot be put in place is reported and the rest goes on.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class VolumeFiles(string root, bool keep, bool owners, TextWriter log)
{
    private const int DefaultFileMode = 0b110_100_100;   // 0644
    private const int DefaultFolderMode = 0b111_101_101; // 0755

    /// <summary>The user a probe file is given to: whether owners can be set on the volume at all.</summary>
    private const long ProbeUser = 54321;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(30);

    private readonly List<MoverFileIssue> _issues = [];
    private readonly List<(int Index, string Path, string Target, MoverFileMeta Meta)> _links = [];
    private readonly List<(int Index, string Path, MoverFileMeta Meta)> _folders = [];
    private readonly string _suffix = "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
    private Pending? _pending;
    private long _files;
    private long _bytes;
    private long _failed;
    private long _renamed;
    private long _partial;
    private long _lastProgress = Stopwatch.GetTimestamp();
    private bool _squashed;

    /// <summary>Owners are set: asked for, and the volume lets the mover give files to other users.</summary>
    private bool SetsOwners => owners && !_squashed;

    /// <summary>A file written under its temporary name, waiting for its owner, mode and times.</summary>
    private sealed record Pending(int Index, string Temp, string Target, string Relative, long Size, bool Renamed);

    public async Task<MoverFileReport> ApplyAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new MoverFileReader(stream);
        await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (owners)
        {
            ProbeOwners();
        }

        try
        {
            while (true)
            {
                var record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (_pending is { } pending && !(record.Kind == MoverFileRecordKind.Meta && !record.Folder && record.Index == pending.Index))
                {
                    Finish(null);
                }

                switch (record.Kind)
                {
                    case MoverFileRecordKind.Directory:
                        MakeDirectory(record.Index, record.Path!);
                        break;
                    case MoverFileRecordKind.File:
                        await ReceiveAsync(reader, record, cancellationToken).ConfigureAwait(false);
                        break;
                    case MoverFileRecordKind.Meta when record.Folder:
                        _folders.Add((record.Index, record.Path!, record.Meta!));
                        break;
                    case MoverFileRecordKind.Meta:
                        Finish(record.Meta);
                        break;
                    case MoverFileRecordKind.Link:
                        _links.Add((record.Index, record.Path!, record.Text!, record.Meta!));
                        break;
                    case MoverFileRecordKind.End:
                        foreach (var (index, path, target, meta) in _links)
                        {
                            MakeLink(index, path, target, meta);
                        }

                        foreach (var (index, path, meta) in _folders)
                        {
                            FolderMeta(index, path, meta);
                        }

                        return new MoverFileReport(_files, _bytes, _failed, _renamed, _partial, _issues);
                    default:
                        throw new InvalidDataException($"The file stream has a '{(char)record.Kind}' record outside of a file.");
                }

                if (Stopwatch.GetElapsedTime(_lastProgress) >= ProgressInterval)
                {
                    _lastProgress = Stopwatch.GetTimestamp();
                    log.WriteLine($"{_files} file(s), {MoverRunner.Size(_bytes)} put in place so far; {_failed} failed.");
                }
            }
        }
        finally
        {
            // A stream that broke off leaves no temporary file behind.
            if (_pending is { } left)
            {
                TryDelete(left.Temp);
            }
        }
    }

    private async Task ReceiveAsync(MoverFileReader reader, MoverFileRecord file, CancellationToken cancellationToken)
    {
        string? temp = null;
        FileStream? output = null;
        string? problem = null;
        string target = string.Empty;
        var renamed = false;
        if (Resolve(file.Path!, out var full, out problem) && Parents(file.Path!, out problem))
        {
            target = full;
            switch (Probe(full))
            {
                case EntryKind.Link:
                    problem = "a symbolic link with this name exists; it is not replaced";
                    break;
                case EntryKind.Directory:
                    problem = "a folder with this name exists";
                    break;
                case EntryKind.Missing:
                    break;
                default:
                    if (keep)
                    {
                        target = FreeName(full) ?? string.Empty;
                        renamed = true;
                        problem = target.Length == 0 ? "no free RESTORED- name" : null;
                    }

                    break;
            }
        }

        if (problem is null)
        {
            temp = Path.Combine(Path.GetDirectoryName(target)!, $".bdfu-restore-{file.Index.ToString(CultureInfo.InvariantCulture)}{_suffix}");
            try
            {
                TryDelete(temp);
                output = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"it cannot be created: {ex.Message}";
            }
        }

        long written = 0;
        try
        {
            while (true)
            {
                var record = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                switch (record.Kind)
                {
                    case MoverFileRecordKind.Content:
                        if (output is not null && problem is null)
                        {
                            try
                            {
                                await output.WriteAsync(record.Data, cancellationToken).ConfigureAwait(false);
                            }
                            catch (IOException ex)
                            {
                                problem = $"writing failed: {ex.Message}";
                            }
                        }

                        written += record.Length;
                        break;
                    case MoverFileRecordKind.FileEnd:
                        if (problem is null && written != file.Length)
                        {
                            throw new InvalidDataException($"The file {file.Path} came with {written} of its {file.Length} bytes.");
                        }

                        if (problem is null)
                        {
                            try
                            {
                                output!.Flush(flushToDisk: true);
                            }
                            catch (IOException ex)
                            {
                                problem = $"writing failed: {ex.Message}";
                            }
                        }

                        await DisposeAsync().ConfigureAwait(false);
                        if (problem is not null)
                        {
                            Fail(file.Index, problem);
                            DeleteTemp();
                            return;
                        }

                        _pending = new Pending(file.Index, temp!, target, Relative(target), file.Length, renamed);
                        temp = null;
                        return;
                    case MoverFileRecordKind.FileAborted:
                        // The server could not read the file from the restore point; it reports that itself.
                        await DisposeAsync().ConfigureAwait(false);
                        DeleteTemp();
                        return;
                    default:
                        throw new InvalidDataException($"The file stream has a '{(char)record.Kind}' record inside the file {file.Path}.");
                }
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
            DeleteTemp();
        }

        async ValueTask DisposeAsync()
        {
            if (output is not null)
            {
                try
                {
                    await output.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    problem ??= $"writing failed: {ex.Message}";
                }

                output = null;
            }
        }

        void DeleteTemp()
        {
            if (temp is not null)
            {
                TryDelete(temp);
                temp = null;
            }
        }
    }

    /// <summary>Gives the pending file its owner, mode and times and renames it into place.</summary>
    private void Finish(MoverFileMeta? meta)
    {
        if (_pending is not { } file)
        {
            return;
        }

        _pending = null;
        var problem = ApplyMeta(file.Temp, meta, Path.GetDirectoryName(file.Target)!, isFolder: false);
        if (Native.Rename(file.Temp, file.Target) is { } error)
        {
            TryDelete(file.Temp);
            Fail(file.Index, $"replacing failed: {error}");
            return;
        }

        _files++;
        _bytes += file.Size;
        if (problem is not null)
        {
            Issue(file.Index, MoverFileIssueKinds.Partial, problem);
            _partial++;
        }

        if (file.Renamed)
        {
            Issue(file.Index, MoverFileIssueKinds.Renamed, file.Relative);
            _renamed++;
        }
    }

    private void MakeDirectory(int index, string relative)
    {
        if (!Resolve(relative, out var full, out var problem) || !Parents(relative, out problem))
        {
            Fail(index, problem!);
            return;
        }

        switch (Probe(full))
        {
            case EntryKind.Directory:
                return;
            case EntryKind.Link:
                Fail(index, "a symbolic link with this name exists; nothing is written through it");
                return;
            case EntryKind.Missing:
                if (Native.MakeDirectory(full, DefaultFolderMode) is { } error)
                {
                    Fail(index, $"it cannot be created: {error}");
                    return;
                }

                // Until its own owner comes at the end: that of its parent, so the workload can use it.
                if (!_squashed && Native.Owner(Path.GetDirectoryName(full)!) is { } parent)
                {
                    Native.ChangeOwner(full, parent.UserId, parent.GroupId);
                }

                return;
            default:
                Fail(index, "a file with this name exists");
                return;
        }
    }

    /// <summary>A folder's owner, mode and times; the empty path is the volume's root (a whole volume restored into a new one).</summary>
    private void FolderMeta(int index, string relative, MoverFileMeta meta)
    {
        var full = root;
        if (relative.Length > 0 && (!Resolve(relative, out full, out _) || Probe(full) != EntryKind.Directory))
        {
            return;
        }

        if (ApplyMeta(full, meta, relative.Length > 0 ? Path.GetDirectoryName(full)! : root, isFolder: true) is { } problem)
        {
            Issue(index, MoverFileIssueKinds.Partial, problem);
            _partial++;
        }
    }

    private void MakeLink(int index, string relative, string target, MoverFileMeta meta)
    {
        if (!Resolve(relative, out var full, out var problem) || !Parents(relative, out problem))
        {
            Fail(index, problem!);
            return;
        }

        var path = full;
        var renamed = false;
        switch (Probe(full))
        {
            case EntryKind.Directory:
                Fail(index, "a folder with this name exists");
                return;
            case EntryKind.Missing:
                break;
            default:
                if (keep)
                {
                    path = FreeName(full) ?? string.Empty;
                    if (path.Length == 0)
                    {
                        Fail(index, "no free RESTORED- name");
                        return;
                    }

                    renamed = true;
                }

                break;
        }

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".bdfu-restore-{index.ToString(CultureInfo.InvariantCulture)}{_suffix}");
        TryDelete(temp);
        if ((Native.SymbolicLink(target, temp) ?? Native.Rename(temp, path)) is { } error)
        {
            TryDelete(temp);
            Fail(index, error);
            return;
        }

        if (SetsOwners && meta.UserId is { } uid && meta.GroupId is { } gid)
        {
            Native.ChangeOwner(path, uid, gid);
        }

        if (renamed)
        {
            Issue(index, MoverFileIssueKinds.Renamed, Relative(path));
            _renamed++;
        }
    }

    /// <summary>Owner (from the backup, or that of <paramref name="parent"/>), then mode, then times; returns what could not be set.</summary>
    private string? ApplyMeta(string path, MoverFileMeta? meta, string parent, bool isFolder)
    {
        var problems = new List<string>();
        if (_squashed)
        {
            // Reported once for the volume (ProbeOwners): the files belong to whom its server gives them.
        }
        else if (owners && meta is { UserId: { } uid, GroupId: { } gid })
        {
            if (Native.ChangeOwner(path, uid, gid) is { } error)
            {
                problems.Add($"owner {uid}:{gid} not set ({error})");
            }
        }
        else if (Native.Owner(parent) is { } owner && Native.ChangeOwner(path, owner.UserId, owner.GroupId) is { } error)
        {
            problems.Add($"owner not set ({error})");
        }

        var mode = meta?.Mode ?? (isFolder ? DefaultFolderMode : DefaultFileMode);
        try
        {
            File.SetUnixFileMode(path, (UnixFileMode)(mode & 0xFFF));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"mode {Convert.ToString(mode & 0xFFF, 8)} not set ({ex.Message})");
        }

        try
        {
            if (meta?.ModifiedUtc is { } modified)
            {
                File.SetLastWriteTimeUtc(path, modified);
            }

            if (meta?.AccessedUtc is { } accessed)
            {
                File.SetLastAccessTimeUtc(path, accessed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            problems.Add($"times not set ({ex.Message})");
        }

        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }

    /// <summary>The item's place below <c>root</c>; false for a path that is empty or has empty, ".", ".." or NUL components.</summary>
    private bool Resolve(string relative, out string full, out string? problem)
    {
        full = string.Empty;
        var components = relative.Split('/');
        if (relative.Length == 0 || components.Any(c => c.Length == 0 || c is "." or ".." || c.Contains('\0', StringComparison.Ordinal)))
        {
            problem = $"the path {relative} is not a plain path below the volume's root";
            return false;
        }

        full = root + "/" + relative;
        problem = null;
        return true;
    }

    /// <summary>Creates the folders above the item as needed; a symbolic link or a file in the way is an error.</summary>
    private bool Parents(string relative, out string? problem)
    {
        var components = relative.Split('/');
        var current = root;
        for (var i = 0; i < components.Length - 1; i++)
        {
            var parent = current;
            current += "/" + components[i];
            switch (Probe(current))
            {
                case EntryKind.Directory:
                    continue;
                case EntryKind.Link:
                    problem = $"{Relative(current)} is a symbolic link; nothing is written through it";
                    return false;
                case EntryKind.Missing:
                    if (Native.MakeDirectory(current, DefaultFolderMode) is { } error)
                    {
                        problem = $"{Relative(current)} cannot be created: {error}";
                        return false;
                    }

                    if (Native.Owner(parent) is { } owner)
                    {
                        Native.ChangeOwner(current, owner.UserId, owner.GroupId);
                    }

                    continue;
                default:
                    problem = $"{Relative(current)} is not a folder";
                    return false;
            }
        }

        problem = null;
        return true;
    }

    /// <summary>RESTORED-&lt;name&gt;, then RESTORED-&lt;stem&gt; (2)&lt;ext&gt; … in the same folder.</summary>
    private static string? FreeName(string full)
    {
        var folder = Path.GetDirectoryName(full)!;
        var name = "RESTORED-" + Path.GetFileName(full);
        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > "RESTORED-".Length ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var n = 1; n < 1000; n++)
        {
            var candidate = Path.Combine(folder, n == 1 ? name : $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (Probe(candidate) == EntryKind.Missing)
            {
                return candidate;
            }
        }

        return null;
    }

    private string Relative(string full) => full.Length > root.Length ? full[(root.Length + 1)..] : "/";

    /// <summary>
    /// Whether the volume lets the mover give files to other users: a probe file is given to another one once. An NFS export
    /// that maps root to nobody (root_squash) refuses that; then owners are left to its server, with one issue for the volume
    /// (index -1) instead of one per item.
    /// </summary>
    private void ProbeOwners()
    {
        var probe = Path.Combine(root, $".bdfu-owner-probe{_suffix}");
        try
        {
            using (new FileStream(probe, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Nothing can be written here: each item reports it.
        }

        try
        {
            if (Native.ChangeOwner(probe, ProbeUser, ProbeUser) is { } error)
            {
                _squashed = true;
                Issue(-1, MoverFileIssueKinds.Partial,
                    $"owners are not kept: the volume does not let the mover give files to other users ({error}); its server may map root to another user (NFS root_squash). The files belong to the user it gives them.");
                log.WriteLine($"Owners are not kept on {root}: giving a file to another user failed ({error}).");
            }
        }
        finally
        {
            TryDelete(probe);
        }
    }

    private void Fail(int index, string reason)
    {
        Issue(index, MoverFileIssueKinds.Failed, reason);
        _failed++;
    }

    private void Issue(int index, string kind, string text)
    {
        if (_issues.Count < MoverFileReport.MaxIssues)
        {
            _issues.Add(new MoverFileIssue(index, kind, text));
        }
    }

    private static EntryKind Probe(string path) => Native.Probe(path);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a hidden .bdfu-restore- file stays.
        }
    }
}

internal enum EntryKind
{
    Missing,
    File,
    Directory,
    Link,
    Other,
}

/// <summary>The few POSIX calls .NET has no API for (owners, no-follow stat), through libc.</summary>
internal static class Native
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasic = 0x7ff;
    private const int StatxBytes = 256;

    /// <summary>What is at <paramref name="path"/>, without following a symbolic link.</summary>
    public static EntryKind Probe(string path)
    {
        var buffer = new byte[StatxBytes];
        if (Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxBasic, buffer) != 0)
        {
            return EntryKind.Missing;
        }

        return (BitConverter.ToUInt16(buffer, 28) & 0xF000) switch
        {
            0x4000 => EntryKind.Directory,
            0x8000 => EntryKind.File,
            0xA000 => EntryKind.Link,
            _ => EntryKind.Other,
        };
    }

    /// <summary>The owner of a folder or file (following links), or null if it cannot be read.</summary>
    public static (long UserId, long GroupId)? Owner(string path)
    {
        var buffer = new byte[StatxBytes];
        return Statx(AtFdCwd, path, 0, StatxBasic, buffer) == 0 ? (BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24)) : null;
    }

    /// <summary>Changes the owner of the item itself (a link, not what it points to); returns the error or null.</summary>
    public static string? ChangeOwner(string path, long userId, long groupId) =>
        userId is < 0 or > uint.MaxValue || groupId is < 0 or > uint.MaxValue
            ? "the id is out of range"
            : LChown(path, (uint)userId, (uint)groupId) == 0 ? null : Error();

    /// <summary>Renames over an existing item (atomically); returns the error or null.</summary>
    public static string? Rename(string from, string to) => RenameNative(from, to) == 0 ? null : Error();

    public static string? MakeDirectory(string path, int mode) => MkDir(path, (uint)mode) == 0 ? null : Error();

    public static string? SymbolicLink(string target, string path) => SymLink(target, path) == 0 ? null : Error();

    private static string Error() => Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int dirFd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    [DllImport("libc", EntryPoint = "lchown", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LChown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameNative([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int MkDir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SymLink([MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
}
