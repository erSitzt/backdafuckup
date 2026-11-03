using Backdafuckup.Agent.Protocol;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Restore;

namespace Backdafuckup.Server.FileRestore;

/// <summary>Plans a restore into a Windows guest: items in order, target paths, times, attributes and permissions.</summary>
internal static class AgentTargetPaths
{
    private const FileAttributes KeptAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    /// <summary><c>X:\…</c> without relative components or characters Windows does not allow (the agent checks the same).</summary>
    public static bool IsLocalAbsolute(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            return false;
        }

        var rest = path[3..].TrimEnd('\\');
        return rest.Length == 0 || rest.Split('\\').All(part =>
            part.Length > 0 && part is not ("." or "..") && part.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !part.Any(c => c < ' '));
    }

    /// <summary>
    /// Walks the selection (no recursion, links not followed). Folders get an end item after their content, where the
    /// agent sets their times and permissions. Links, junctions and content we cannot read are reported as skipped.
    /// </summary>
    public static AgentTransferPlan Plan(
        IGuestFileSystem fs,
        IReadOnlyList<GuestPath> selection,
        Func<GuestPath, string> targetOf,
        bool permissions,
        Func<GuestPath, string> describe,
        IFileRestoreObserver observer,
        long maxItems,
        CancellationToken cancellationToken)
    {
        var plan = new AgentTransferPlan();
        var descriptors = new Dictionary<string, int>(StringComparer.Ordinal);
        var visited = new HashSet<long>();
        var stack = new Stack<(GuestPath Path, GuestEntry? Entry, int Depth, bool End)>();
        for (var i = selection.Count - 1; i >= 0; i--)
        {
            stack.Push((selection[i], null, 0, false));
        }

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, known, depth, end) = stack.Pop();
            if (plan.Items.Count >= maxItems)
            {
                throw new GuestWalkLimitException($"The selection has more than {maxItems:N0} files and folders. Restore smaller parts one after the other.");
            }

            if (plan.Items.Count % 1000 == 999)
            {
                observer.Counting(plan.Files, plan.Bytes);
            }

            GuestEntry entry;
            try
            {
                entry = known ?? fs.Stat(path);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                plan.Failed++;
                observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, describe(path), null, ex is FileNotFoundException ? "it does not exist in the restore point" : ex.Message));
                continue;
            }

            if (end)
            {
                Add(FileTransferItemKinds.EndDirectory, path, entry, null);
                continue;
            }

            switch (entry.Kind)
            {
                case GuestEntryKind.Directory:
                    if (entry.FileId is { } id && !visited.Add(id))
                    {
                        Skip(path, "the folder appears twice in the file system (damaged metadata?); it is restored once");
                        continue;
                    }

                    Add(FileTransferItemKinds.Directory, path, entry, null);
                    plan.Directories++;
                    stack.Push((path, entry, depth, true));
                    if (depth >= 256)
                    {
                        plan.Failed++;
                        observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, describe(path), targetOf(path), "nested deeper than 256 levels; its content is not restored"));
                        continue;
                    }

                    IReadOnlyList<GuestEntry> children;
                    try
                    {
                        children = fs.List(path);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        plan.Failed++;
                        observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Failed, describe(path), targetOf(path), $"the folder cannot be read from the restore point: {ex.Message}"));
                        continue;
                    }

                    for (var i = children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((path.Append(children[i].Name), children[i], depth + 1, false));
                    }

                    break;
                case GuestEntryKind.File when entry.Unsupported is not null:
                    Skip(path, entry.Unsupported);
                    break;
                case GuestEntryKind.File:
                    Add(FileTransferItemKinds.File, path, entry, path);
                    plan.Files++;
                    plan.Bytes += entry.Size;
                    break;
                case GuestEntryKind.Symlink:
                    Skip(path, $"symbolic link to {entry.LinkTarget}; links are not restored into the VM yet");
                    break;
                case GuestEntryKind.Junction:
                    Skip(path, $"junction to {entry.LinkTarget}; junctions are not restored into the VM yet");
                    break;
                default:
                    Skip(path, entry.Unsupported ?? "reparse point without readable content");
                    break;
            }
        }

        return plan;

        void Skip(GuestPath path, string reason)
        {
            plan.Skipped++;
            observer.Problem(new FileRestoreProblem(FileRestoreProblemKind.Skipped, describe(path), null, reason));
        }

        void Add(string kind, GuestPath path, GuestEntry entry, GuestPath? source)
        {
            plan.Items.Add(new FileTransferItem
            {
                Index = plan.Items.Count,
                Kind = kind,
                Path = targetOf(path),
                Size = kind == FileTransferItemKinds.File ? entry.Size : 0,
                CreatedUtc = entry.CreatedUtc,
                ModifiedUtc = entry.ModifiedUtc,
                AccessedUtc = entry.AccessedUtc,
                Attributes = (int)((entry.Attributes ?? 0) & KeptAttributes),
                Security = permissions && kind != FileTransferItemKinds.Directory ? Descriptor(path) : -1,
            });
            plan.Sources.Add(source is null ? null : new TransferSource(null, source));
            plan.Descriptions.Add(describe(path));
        }

        int Descriptor(GuestPath path)
        {
            byte[]? descriptor;
            try
            {
                descriptor = fs.GetSecurityDescriptor(path);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                return -1;
            }

            if (descriptor is null)
            {
                return -1;
            }

            // NTFS shares one descriptor among many files ($Secure); send each once per page.
            var key = Convert.ToBase64String(descriptor);
            if (!descriptors.TryGetValue(key, out var index))
            {
                index = plan.Descriptors.Count;
                plan.Descriptors.Add(descriptor);
                descriptors[key] = index;
            }

            return index;
        }
    }
}

/// <summary>Plans the transfer of single files from any volumes to given target paths (e.g. the files of a database).</summary>
internal static class AgentFilePlan
{
    /// <param name="files">Volume and file system of the source, its path in the backup, the target path in the guest, and how it is logged.</param>
    public static AgentTransferPlan Plan(IReadOnlyList<(string Volume, IGuestFileSystem Fs, GuestPath Source, string Target, string Description)> files)
    {
        var plan = new AgentTransferPlan();
        foreach (var (volume, fs, source, target, description) in files)
        {
            if (!AgentTargetPaths.IsLocalAbsolute(target))
            {
                throw new ArgumentException($"{target} is not an absolute local path.");
            }

            var entry = fs.Stat(source);
            if (entry.Kind != GuestEntryKind.File || entry.Unsupported is not null)
            {
                throw new InvalidDataException($"{description} cannot be restored: {entry.Unsupported ?? "it is not a regular file"}.");
            }

            plan.Items.Add(new FileTransferItem
            {
                Index = plan.Items.Count,
                Kind = FileTransferItemKinds.File,
                Path = target,
                Size = entry.Size,
                CreatedUtc = entry.CreatedUtc,
                ModifiedUtc = entry.ModifiedUtc,
                AccessedUtc = entry.AccessedUtc,
                Attributes = (int)((entry.Attributes ?? 0) & (FileAttributes.ReadOnly | FileAttributes.Archive)),
            });
            plan.Sources.Add(new TransferSource(volume, source));
            plan.Descriptions.Add(description);
            plan.Files++;
            plan.Bytes += entry.Size;
        }

        return plan;
    }
}

/// <summary>Folders in a Linux guest.</summary>
internal static class SshTargetPaths
{
    /// <summary>An absolute path without "." or ".." components and without trailing "/"; null if it is none.</summary>
    public static string? Normalize(string? folder)
    {
        var trimmed = folder?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '/' || trimmed.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p is "." or "..") ? null : "/" + string.Join('/', parts);
    }
}
