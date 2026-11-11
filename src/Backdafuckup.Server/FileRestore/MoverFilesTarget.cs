using Backdafuckup.Core.DataMover;
using Backdafuckup.FileLevel.Restore;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// Restores into a live PVC: the pipeline's items go as a stream (<see cref="MoverFileWriter"/>) to the mover pod that
/// mounts the PVC. Conflicts, keep mode, owners, times and links are decided there, in the volume, so <see cref="Probe"/>
/// reports nothing (as with <see cref="SshStagingTarget"/>); the mover's report names items by their index here.
/// </summary>
public sealed class MoverFilesTarget : IFileRestoreTarget, ILinkRestoreTarget
{
    private readonly IMoverFilesChannel _channel;
    private readonly BufferedStream _buffer;
    private readonly MoverFileWriter _writer;
    private readonly string[] _base;
    private readonly string _display;
    private readonly List<StagedEntry> _entries = [];
    private (int Index, string Path)? _pendingFile;

    /// <param name="baseFolder">The folder in the PVC that the paths are below (empty: its root).</param>
    /// <param name="display">The PVC for messages, e.g. <c>PVC shop/data-redis-0</c>.</param>
    public MoverFilesTarget(IMoverFilesChannel channel, IReadOnlyList<string> baseFolder, string display)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _base = [.. baseFolder];
        _display = display;
        _buffer = new BufferedStream(channel.Content, 256 * 1024);
        _writer = new MoverFileWriter(_buffer);
        _writer.WriteHeader();
    }

    public bool IsUsable => _channel.IsUsable;

    public string Describe(IReadOnlyList<string> path) => $"{_display}:{Absolute(path)}";

    /// <summary>Linux names may contain anything but "/" and NUL, which guest paths never do.</summary>
    public string? CheckName(string name) => null;

    public TargetEntryKind Probe(IReadOnlyList<string> path)
    {
        _pendingFile = null;
        return TargetEntryKind.Missing;
    }

    public void CreateDirectory(IReadOnlyList<string> path)
    {
        _pendingFile = null;
        _writer.Directory(Add('D', path, 0), Relative(path));
    }

    public void WriteFile(IReadOnlyList<string> path, Stream content, long length, bool replace, CancellationToken cancellationToken)
    {
        _pendingFile = null;
        var index = Add('F', path, length);
        _writer.File(index, Relative(path), content, length, cancellationToken);
        _pendingFile = (index, Relative(path));
    }

    public void SetMetadata(IReadOnlyList<string> path, RestoredItemMeta meta, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var relative = Relative(path);
        var mover = new MoverFileMeta(meta.UnixMode, meta.UserId, meta.GroupId, meta.ModifiedUtc, meta.AccessedUtc);
        if (!isDirectory)
        {
            // Owner, mode and times go with the file, so the mover sets them before it renames the file into place.
            if (_pendingFile is { } file && file.Path == relative)
            {
                _writer.Meta(file.Index, relative, folder: false, mover);
            }

            _pendingFile = null;
            return;
        }

        _pendingFile = null;
        _writer.Meta(Add('M', path, 0), relative, folder: true, mover);
    }

    public void CreateSymbolicLink(IReadOnlyList<string> path, string linkTarget, RestoredItemMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        _pendingFile = null;
        _writer.Link(Add('L', path, 0), Relative(path), linkTarget, meta.UserId, meta.GroupId);
    }

    /// <summary>Ends the stream: the mover puts the links and folder times in place and reports.</summary>
    public void Finish()
    {
        _writer.End(_entries.Count);
        _buffer.Flush();
        _buffer.Dispose();
    }

    /// <summary>The item the mover reports under <paramref name="index"/>, or null for an index it should not know.</summary>
    public StagedEntry? Entry(int index) => index >= 0 && index < _entries.Count ? _entries[index] : null;

    public string Display(StagedEntry entry) => $"{_display}:{entry.Path}";

    /// <summary>Display of a path the mover reports, relative to the PVC's root.</summary>
    public string Display(string relative) => $"{_display}:/{relative}";

    /// <summary>An unfinished stream stays without its end, which the mover reports as broken off.</summary>
    public void Dispose()
    {
    }

    private int Add(char kind, IReadOnlyList<string> path, long size)
    {
        _entries.Add(new StagedEntry(kind, Absolute(path), size));
        return _entries.Count - 1;
    }

    private string Relative(IReadOnlyList<string> path) => string.Join('/', _base.Concat(path));

    private string Absolute(IReadOnlyList<string> path) => "/" + Relative(path);
}
