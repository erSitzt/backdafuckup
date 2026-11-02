namespace Backdafuckup.FileLevel;

/// <summary>Limits for walking a guest folder tree; the image is untrusted input.</summary>
public sealed record GuestWalkLimits(int MaxDepth = 256, long MaxEntries = 5_000_000);

/// <summary>A walk exceeded <see cref="GuestWalkLimits"/>.</summary>
public sealed class GuestWalkLimitException(string message) : IOException(message);

/// <summary>Iterative walk over selected files and folders (no recursion, links are reported but not followed).</summary>
public static class GuestWalker
{
    /// <summary>
    /// Yields every selected path and, for folders, everything below it: folders before their content. A folder that
    /// appears twice (by file id) is skipped, so a corrupt image cannot make the walk loop.
    /// </summary>
    public static IEnumerable<(GuestPath Path, GuestEntry Entry)> Walk(
        IGuestFileSystem fs,
        IEnumerable<GuestPath> selection,
        GuestWalkLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(limits);
        var visited = new HashSet<long>();
        var stack = new Stack<(GuestPath Path, GuestEntry Entry, int Depth)>();
        foreach (var path in selection.Reverse())
        {
            stack.Push((path, fs.Stat(path), 0));
        }

        long count = 0;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, entry, depth) = stack.Pop();
            if (++count > limits.MaxEntries)
            {
                throw new GuestWalkLimitException($"The selection contains more than {limits.MaxEntries:N0} entries.");
            }

            if (entry.IsDirectory && entry.FileId is { } id && !visited.Add(id))
            {
                continue;
            }

            yield return (path, entry);
            if (!entry.IsDirectory)
            {
                continue;
            }

            if (depth >= limits.MaxDepth)
            {
                throw new GuestWalkLimitException($"{path} is nested deeper than {limits.MaxDepth} levels.");
            }

            var children = fs.List(path);
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((path.Append(children[i].Name), children[i], depth + 1));
            }
        }
    }
}
