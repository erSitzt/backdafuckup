using Backdafuckup.FileLevel.Restore;
using Backdafuckup.Smb;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// Restores into a folder of an SMB share (its real location, after DFS links). Windows name rules apply; there are no
/// permissions on shares.
/// </summary>
public sealed class ShareRestoreTarget(ISmbShare share) : IFileRestoreTarget
{
    private readonly UncPath folder = share.Location;

    public bool IsUsable => share.IsConnected;

    /// <summary>Creates the target folder (and missing parents); a link in the way is an error.</summary>
    public void EnsureFolder()
    {
        for (var depth = 1; depth <= folder.Folder.Count; depth++)
        {
            var path = string.Join('\\', folder.Folder.Take(depth));
            switch (share.Probe(path))
            {
                case SmbEntryKind.Missing:
                    share.CreateDirectory(path);
                    break;
                case SmbEntryKind.Directory:
                    break;
                case SmbEntryKind.File:
                    throw new IOException($@"\\{folder.Server}\{folder.Share}\{path} on {share.Endpoint} is a file, not a folder.");
                default:
                    throw new IOException($@"\\{folder.Server}\{folder.Share}\{path} on {share.Endpoint} is a link; choose the folder it points to.");
            }
        }
    }

    public string Describe(IReadOnlyList<string> path) => path.Count == 0 ? folder.ToString() : $@"{folder}\{string.Join('\\', path)}";

    public string? CheckName(string name) => WindowsNames.Check(name);

    public TargetEntryKind Probe(IReadOnlyList<string> path) => share.Probe(folder.Relative(path)) switch
    {
        SmbEntryKind.Missing => TargetEntryKind.Missing,
        SmbEntryKind.File => TargetEntryKind.File,
        SmbEntryKind.Directory => TargetEntryKind.Directory,
        _ => TargetEntryKind.Other,
    };

    public void CreateDirectory(IReadOnlyList<string> path) => share.CreateDirectory(folder.Relative(path));

    public void WriteFile(IReadOnlyList<string> path, Stream content, long length, bool replace, CancellationToken cancellationToken) =>
        share.WriteFile(folder.Relative(path), content, length, replace, cancellationToken);

    public void SetMetadata(IReadOnlyList<string> path, RestoredItemMeta meta, bool isDirectory) =>
        share.SetBasicInformation(folder.Relative(path), isDirectory, meta.CreatedUtc, meta.ModifiedUtc, meta.AccessedUtc, meta.WindowsAttributes ?? 0);

    public void Dispose() => share.Dispose();
}
