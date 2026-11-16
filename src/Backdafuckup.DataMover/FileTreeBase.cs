using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.DataMover;

/// <summary>
/// A volume's file tree in an earlier restore point, the base of an incremental read (spec
/// 2026-11-12-1952-kubernetes-file-level-volumes). It is served to the mover as <c>BDFUPRV1</c> (folders and the files
/// whose content can be taken over), and the server's builder looks up each file the mover sends as unchanged in it, with
/// its own cursor, to take the content from there. Listings are read as the walk reaches them; a listing that cannot be
/// read leaves its folder out (its files are read again).
/// </summary>
public sealed class FileTreeBase(ChunkStreamRef root, IChunkReader chunks, int chunkSize, Guid restorePointId) : IPreviousFileTree
{
    private const int FlushBytes = 64 * 1024;

    /// <summary>The restore point the tree is from (its chunks are pinned while the run takes content from it).</summary>
    public Guid RestorePointId => restorePointId;

    /// <summary>Whether a file's content can be taken over: read in full, not changed while read, not racy.</summary>
    public static bool IsReusable(FileTreeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry is { Type: FileTreeEntryType.File, Content: not null }
               && (entry.Flags & (FileTreeEntryFlags.Unreadable | FileTreeEntryFlags.Racy | FileTreeEntryFlags.ChangedWhileRead)) == 0;
    }

    public async Task WriteAsync(Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new MemoryStream();
        var writer = new MoverPreviousWriter(buffer);
        writer.Header();
        var folders = new Stack<(List<FileTreeEntry> Entries, int Next)>();
        folders.Push((Root() ?? [], 0));
        while (folders.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folder.Next == folder.Entries.Count)
            {
                if (folders.Count > 0)
                {
                    writer.Leave();
                }
                else
                {
                    writer.End();
                }
            }
            else
            {
                folders.Push((folder.Entries, folder.Next + 1));
                var entry = folder.Entries[folder.Next];
                if (IsReusable(entry))
                {
                    writer.File(entry.Name, new PreviousFile(entry.Size, entry.MtimeNs, entry.CtimeNs, entry.Inode));
                }
                else if (entry is { Type: FileTreeEntryType.Directory, Listing: { } listing, SubtreeFiles: > 0 } && Load(listing) is { } entries)
                {
                    writer.Enter(entry.Name);
                    folders.Push((entries, 0));
                }
            }

            if (buffer.Length >= FlushBytes)
            {
                await FlushAsync(buffer, output, cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushAsync(buffer, output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The root folder's entries, or null if its listing cannot be read.</summary>
    internal List<FileTreeEntry>? Root() => Load(root);

    /// <summary>A folder's entries (sorted by name bytes), or null if its listing cannot be read.</summary>
    internal List<FileTreeEntry>? Load(ChunkStreamRef listing)
    {
        try
        {
            return FileTreeListing.Deserialize(ChunkStreams.ReadAll(listing, chunks, chunkSize));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The entry named <paramref name="name"/> in a folder's sorted entries.</summary>
    internal static FileTreeEntry? Find(List<FileTreeEntry> entries, ReadOnlySpan<byte> name)
    {
        int low = 0, high = entries.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var order = entries[middle].Name.AsSpan().SequenceCompareTo(name);
            if (order == 0)
            {
                return entries[middle];
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return null;
    }

    private static async Task FlushAsync(MemoryStream buffer, Stream output, CancellationToken cancellationToken)
    {
        if (buffer.Length > 0)
        {
            await output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken).ConfigureAwait(false);
            buffer.SetLength(0);
        }
    }
}
