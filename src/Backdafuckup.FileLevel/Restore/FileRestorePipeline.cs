namespace Backdafuckup.FileLevel.Restore;

public enum FileRestoreMode
{
    /// <summary>Existing files are replaced (only after the new content is complete).</summary>
    Overwrite,

    /// <summary>Existing files stay; the restored file is written as RESTORED-&lt;name&gt;.</summary>
    Keep,
}

public sealed record FileRestorePipelineOptions
{
    public FileRestoreMode Mode { get; init; }

    public int MaxDepth { get; init; } = 256;

    public long MaxEntries { get; init; } = 10_000_000;

    /// <summary>The restore stops after this many failed items in a row (e.g. the target is full or read-only).</summary>
    public int MaxFailuresInARow { get; init; } = 100;

    public string KeepPrefix { get; init; } = "RESTORED-";

    /// <summary>How a guest path appears in the log, e.g. <c>C:\Users\x</c> or <c>/home/x</c>.</summary>
    public Func<GuestPath, string> Describe { get; init; } = path => path.ToString();
}

public enum FileRestoreProblemKind
{
    /// <summary>Could not be restored (target error, unreadable content).</summary>
    Failed,

    /// <summary>Not restored by design: links, special files, content we cannot read correctly.</summary>
    Skipped,

    /// <summary>Restored, but times or attributes could not be set.</summary>
    Incomplete,
}

public sealed record FileRestoreProblem(FileRestoreProblemKind Kind, string Source, string? Target, string Reason);

/// <summary>Called from the restore thread; implementations throttle their own updates.</summary>
public interface IFileRestoreObserver
{
    void Counting(long files, long bytes);

    void Planned(long files, long directories, long bytes);

    /// <summary>Bytes of file content read and written (called for every block).</summary>
    void Copied(long bytes);

    void FileRestored(long files);

    void Renamed(string source, string target);

    void Problem(FileRestoreProblem problem);
}

public sealed record FileRestoreResult(long Files, long Directories, long Bytes, long Renamed, long Failed, long Skipped, long Incomplete);

/// <summary>
/// Restores selected files and folders of one guest file system to a target. The selection is counted first (for the
/// progress), then copied front to back. Problems with single items are reported and the restore goes on; it stops
/// when the target becomes unusable or after <see cref="FileRestorePipelineOptions.MaxFailuresInARow"/> failures.
/// Links are never followed, on the source or the target.
/// </summary>
public static class FileRestorePipeline
{
    public static FileRestoreResult Run(
        IGuestFileSystem fs,
        IReadOnlyList<GuestPath> selection,
        IFileRestoreTarget target,
        FileRestorePipelineOptions options,
        IFileRestoreObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observer);
        var (plannedFiles, plannedDirectories, plannedBytes) = Count(fs, selection, options, observer, cancellationToken);
        observer.Planned(plannedFiles, plannedDirectories, plannedBytes);
        return new Copier(fs, target, options, observer, cancellationToken).Run(selection);
    }

    /// <summary>Files, folders and bytes of the selection. Unreadable folders are counted as empty here.</summary>
    private static (long Files, long Directories, long Bytes) Count(
        IGuestFileSystem fs, IReadOnlyList<GuestPath> selection, FileRestorePipelineOptions options, IFileRestoreObserver observer, CancellationToken cancellationToken)
    {
        long files = 0;
        long directories = 0;
        long bytes = 0;
        long entries = 0;
        var visited = new HashSet<long>();
        var stack = new Stack<(GuestPath Path, GuestEntry? Entry, int Depth)>();
        foreach (var path in selection.Reverse())
        {
            stack.Push((path, null, 0));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, known, depth) = stack.Pop();
            if (++entries > options.MaxEntries)
            {
                throw new GuestWalkLimitException($"The selection contains more than {options.MaxEntries:N0} files and folders. Restore smaller parts one after the other.");
            }

            if (entries % 1000 == 0)
            {
                observer.Counting(files, bytes);
            }

            GuestEntry entry;
            try
            {
                entry = known ?? fs.Stat(path);
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                continue;
            }

            if (entry.Kind == GuestEntryKind.File && entry.Unsupported is null)
            {
                files++;
                bytes += entry.Size;
                continue;
            }

            if (!entry.IsDirectory || (entry.FileId is { } id && !visited.Add(id)) || depth >= options.MaxDepth)
            {
                continue;
            }

            directories++;
            try
            {
                var children = fs.List(path);
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push((path.Append(children[i].Name), children[i], depth + 1));
                }
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                // Reported by the copy pass.
            }
        }

        return (files, directories, bytes);
    }

    /// <summary>The image is untrusted input: a damaged folder or inode can make the parser throw nearly anything.</summary>
    private static bool IsDataError(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

    private sealed class Copier(IGuestFileSystem fs, IFileRestoreTarget target, FileRestorePipelineOptions options, IFileRestoreObserver observer, CancellationToken cancellationToken)
    {
        /// <summary>Marks a reason from <see cref="Guard"/> as "skipped by design" instead of "failed".</summary>
        private const string SkipPrefix = "\u0001skip:";

        private readonly HashSet<long> _visited = [];
        private readonly bool _unix = fs.Family == GuestFileSystemFamily.Unix;
        private long _files;
        private long _directories;
        private long _bytes;
        private long _renamed;
        private long _failed;
        private long _skipped;
        private long _incomplete;
        private int _failuresInARow;

        public FileRestoreResult Run(IReadOnlyList<GuestPath> selection)
        {
            var stack = new Stack<Step>();
            for (var i = selection.Count - 1; i >= 0; i--)
            {
                stack.Push(new Step(selection[i], null, [selection[i].Name], 0, Exit: false));
            }

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = stack.Pop();
                if (step.Exit)
                {
                    // Folder times last: creating the content changes them.
                    ApplyMetadata(step.Source, step.Entry!, step.Target, isDirectory: true);
                    continue;
                }

                var source = options.Describe(step.Source);
                GuestEntry entry;
                try
                {
                    entry = step.Entry ?? fs.Stat(step.Source);
                }
                catch (Exception ex) when (IsDataError(ex))
                {
                    Fail(source, null, ex is FileNotFoundException ? "it does not exist in the restore point" : ex.Message);
                    continue;
                }

                if (target.CheckName(entry.Name) is { } invalid)
                {
                    Fail(source, target.Describe(step.Target), invalid);
                    continue;
                }

                switch (entry.Kind)
                {
                    case GuestEntryKind.Directory:
                        EnterDirectory(stack, step with { Entry = entry }, source);
                        break;
                    case GuestEntryKind.File when entry.Unsupported is not null:
                        Skip(source, entry.Unsupported);
                        break;
                    case GuestEntryKind.File:
                        RestoreFile(step.Source, entry, step.Target, source);
                        break;
                    case GuestEntryKind.Symlink when target is ILinkRestoreTarget links && entry.LinkTarget is { Length: > 0 } linkTarget:
                        Guard(source, target.Describe(step.Target), () =>
                        {
                            links.CreateSymbolicLink(step.Target, linkTarget, Meta(entry));
                            return null;
                        });
                        break;
                    case GuestEntryKind.Symlink:
                        Skip(source, $"symbolic link to {entry.LinkTarget}; links are not restored to this target");
                        break;
                    case GuestEntryKind.Junction:
                        Skip(source, $"junction to {entry.LinkTarget}; junctions are not restored");
                        break;
                    default:
                        Skip(source, entry.Unsupported ?? (entry.Kind == GuestEntryKind.Special ? "device, FIFO or socket" : "reparse point without readable content"));
                        break;
                }
            }

            return new FileRestoreResult(_files, _directories, _bytes, _renamed, _failed, _skipped, _incomplete);
        }

        private void EnterDirectory(Stack<Step> stack, Step step, string source)
        {
            var entry = step.Entry!;
            if (entry.FileId is { } id && !_visited.Add(id))
            {
                Skip(source, "the folder appears twice in the file system (damaged metadata?); it was restored once");
                return;
            }

            var where = target.Describe(step.Target);
            if (!Guard(source, where, () =>
            {
                switch (target.Probe(step.Target))
                {
                    case TargetEntryKind.Missing:
                        target.CreateDirectory(step.Target);
                        return null;
                    case TargetEntryKind.Directory:
                        return null;
                    case TargetEntryKind.File:
                        return "a file with this name exists on the target";
                    default:
                        return "a link with this name exists on the target; nothing is written through it";
                }
            }))
            {
                return;
            }

            _directories++;
            stack.Push(step with { Exit = true });
            if (step.Depth >= options.MaxDepth)
            {
                Fail(source, where, $"nested deeper than {options.MaxDepth} levels; its content is not restored");
                return;
            }

            IReadOnlyList<GuestEntry> children;
            try
            {
                children = fs.List(step.Source);
            }
            catch (Exception ex) when (IsDataError(ex))
            {
                Fail(source, where, $"the folder cannot be read from the restore point: {ex.Message}");
                return;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                var child = children[i];
                stack.Push(new Step(step.Source.Append(child.Name), child, [.. step.Target, child.Name], step.Depth + 1, Exit: false));
            }
        }

        private void RestoreFile(GuestPath sourcePath, GuestEntry entry, string[] path, string source)
        {
            var finalPath = path;
            var written = Guard(source, target.Describe(path), () =>
            {
                var replace = false;
                switch (target.Probe(path))
                {
                    case TargetEntryKind.Missing:
                        break;
                    case TargetEntryKind.File when options.Mode == FileRestoreMode.Overwrite:
                        replace = true;
                        break;
                    case TargetEntryKind.File:
                        finalPath = FreeName(path) ?? throw new IOException($"no free {options.KeepPrefix} name was found");
                        break;
                    case TargetEntryKind.Directory:
                        return "a folder with this name exists on the target";
                    default:
                        return "a link with this name exists on the target; it is not replaced";
                }

                Stream input;
                try
                {
                    input = fs.OpenRead(sourcePath);
                }
                catch (GuestFileUnsupportedException ex)
                {
                    return SkipPrefix + ex.Reason;
                }

                using (input)
                using (var counted = new CountingStream(input, observer))
                {
                    target.WriteFile(finalPath, counted, entry.Size, replace, cancellationToken);
                }

                return null;
            });

            if (!written)
            {
                return;
            }

            _files++;
            _bytes += entry.Size;
            observer.FileRestored(_files);
            if (!ReferenceEquals(finalPath, path))
            {
                _renamed++;
                observer.Renamed(source, target.Describe(finalPath));
            }

            ApplyMetadata(sourcePath, entry, finalPath, isDirectory: false);
        }

        /// <summary>RESTORED-name, then RESTORED-name (2), … for Keep mode.</summary>
        private string[]? FreeName(string[] path)
        {
            var name = options.KeepPrefix + path[^1];
            for (var n = 1; n < 1000; n++)
            {
                var candidate = n == 1 ? name : WithSuffix(name, n);
                string[] full = [.. path[..^1], candidate];
                if (target.CheckName(candidate) is null && target.Probe(full) == TargetEntryKind.Missing)
                {
                    return full;
                }
            }

            return null;
        }

        private RestoredItemMeta Meta(GuestEntry entry)
        {
            const FileAttributes kept = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;
            return new RestoredItemMeta(
                _unix ? null : entry.CreatedUtc,
                entry.ModifiedUtc,
                entry.AccessedUtc,
                _unix ? null : (entry.Attributes ?? 0) & kept,
                entry.Mode,
                entry.UserId,
                entry.GroupId);
        }

        private void ApplyMetadata(GuestPath sourcePath, GuestEntry entry, string[] path, bool isDirectory)
        {
            var meta = Meta(entry);
            try
            {
                target.SetMetadata(path, meta, isDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _incomplete++;
                observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Incomplete, options.Describe(sourcePath), target.Describe(path), $"restored, but its times and attributes could not be set: {ex.Message}"));
            }
        }

        /// <summary>Runs one item; a returned reason or an exception is a problem with that item only.</summary>
        private bool Guard(string source, string where, Func<string?> action)
        {
            string? reason;
            try
            {
                reason = action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Only errors count towards the stop limit; a conflict or an invalid name concerns that item alone.
                _failuresInARow++;
                Fail(source, where, ex.Message);
                if (!target.IsUsable)
                {
                    throw new IOException($"The connection to the target was lost: {ex.Message}", ex);
                }

                if (_failuresInARow >= options.MaxFailuresInARow)
                {
                    throw new IOException($"Stopped after {options.MaxFailuresInARow} failures in a row. The last one: {where}: {ex.Message}", ex);
                }

                return false;
            }

            if (reason is null)
            {
                _failuresInARow = 0;
                return true;
            }

            if (reason.StartsWith(SkipPrefix, StringComparison.Ordinal))
            {
                Skip(source, reason[SkipPrefix.Length..]);
            }
            else
            {
                Fail(source, where, reason);
            }

            return false;
        }

        private void Fail(string source, string? where, string reason)
        {
            _failed++;
            observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, source, where, reason));
        }

        private void Skip(string source, string reason)
        {
            _skipped++;
            observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Skipped, source, null, reason));
        }

        private static string WithSuffix(string name, int n)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
        }

        private readonly record struct Step(GuestPath Source, GuestEntry? Entry, string[] Target, int Depth, bool Exit);
    }

    /// <summary>Reports file content as it is read.</summary>
    private sealed class CountingStream(Stream inner, IFileRestoreObserver observer) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            if (n > 0)
            {
                observer.Copied(n);
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
