using System.Text;

namespace Backdafuckup.Nfs.Tests;

/// <summary>An NFS file system in memory for the protocol tests: directories, files, stable fileids and cookies.</summary>
internal sealed class MemoryFileSystem : INfsFileSystem
{
    private readonly Lock _lock = new();
    private readonly ulong _exportId;
    private readonly Dictionary<ulong, Node> _nodes = [];
    private ulong _nextId = 2;

    public MemoryFileSystem(ulong exportId)
    {
        _exportId = exportId;
        _nodes[1] = new Node(1, 0, NfsFileType.Directory);
        RootHandle = Handle(1);
    }

    public ReadOnlyMemory<byte> RootHandle { get; }

    public ulong WriteVerifier { get; } = 0x0102030405060708;

    /// <summary>Delays a READ of this file until released (pipelining tests).</summary>
    public TaskCompletionSource? HoldReads { get; set; }

    public int Commits { get; private set; }

    private sealed class Node(ulong id, ulong parent, NfsFileType type)
    {
        public ulong Id { get; } = id;

        public ulong Parent { get; } = parent;

        public NfsFileType Type { get; } = type;

        public uint Mode { get; set; } = type == NfsFileType.Directory ? 0x1EDu : 0x1A4u;

        public byte[] Data { get; set; } = [];

        public SortedDictionary<string, ulong> Children { get; } = new(StringComparer.Ordinal);

        public NfsTime Mtime { get; set; } = NfsTime.From(DateTimeOffset.UtcNow);

        public ulong? Verifier { get; set; }
    }

    private byte[] Handle(ulong id)
    {
        var payload = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload, id);
        return NfsHandles.Create(_exportId, payload);
    }

    private Node Get(ReadOnlyMemory<byte> handle)
    {
        var payload = NfsHandles.Payload(handle.Span);
        if (payload.Length != 8)
        {
            throw new NfsException(NfsStatus.BadHandle, "bad handle");
        }

        return _nodes.GetValueOrDefault(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(payload)) ?? throw new NfsException(NfsStatus.Stale, "stale");
    }

    private Node Directory(ReadOnlyMemory<byte> handle) => Get(handle) is { Type: NfsFileType.Directory } dir ? dir : throw new NfsException(NfsStatus.NotDir, "not a directory");

    private NfsAttributes Attributes(Node node) => new(
        node.Type, node.Mode, node.Type == NfsFileType.Directory ? 2u : 1u, 0, 0, (ulong)node.Data.Length, (ulong)node.Data.Length, _exportId, node.Id, node.Mtime, node.Mtime, node.Mtime);

    private NfsWcc Wcc(Node node, Action change)
    {
        var before = NfsWccAttributes.Of(Attributes(node));
        change();
        return new NfsWcc(before, Attributes(node));
    }

    public ValueTask<NfsAttributes> GetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return ValueTask.FromResult(Attributes(Get(handle)));
        }
    }

    public ValueTask<NfsWcc> SetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, NfsSetAttributes attributes, NfsTime? guardCtime, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var node = Get(handle);
            if (guardCtime is { } guard && guard != node.Mtime)
            {
                throw new NfsException(NfsStatus.NotSync, "guard");
            }

            return ValueTask.FromResult(Wcc(node, () =>
            {
                if (attributes.Size is { } size)
                {
                    var data = node.Data;
                    Array.Resize(ref data, checked((int)size));
                    node.Data = data;
                }

                node.Mode = attributes.Mode ?? node.Mode;
                node.Mtime = attributes.MtimeHow == NfsTimeHow.ClientTime ? attributes.Mtime : NfsTime.From(DateTimeOffset.UtcNow);
            }));
        }
    }

    public ValueTask<(ReadOnlyMemory<byte> Handle, NfsAttributes Attributes)> LookupAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var dir = Directory(directory);
            var id = name switch
            {
                "." => dir.Id,
                ".." => dir.Parent == 0 ? dir.Id : dir.Parent,
                _ => dir.Children.TryGetValue(name, out var child) ? child : throw new NfsException(NfsStatus.NoEnt, name),
            };
            return ValueTask.FromResult<(ReadOnlyMemory<byte>, NfsAttributes)>((Handle(id), Attributes(_nodes[id])));
        }
    }

    public async ValueTask<NfsReadResult> ReadAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken)
    {
        if (HoldReads is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        lock (_lock)
        {
            var node = Get(handle);
            if (node.Type == NfsFileType.Directory)
            {
                throw new NfsException(NfsStatus.IsDir, "directory");
            }

            var start = (int)Math.Min(offset, node.Data.Length);
            var length = Math.Min(count, node.Data.Length - start);
            return new NfsReadResult(node.Data.AsMemory(start, length).ToArray(), start + length >= node.Data.Length, Attributes(node));
        }
    }

    public ValueTask<NfsWriteResult> WriteAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, ReadOnlyMemory<byte> data, NfsStable stable, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var node = Get(handle);
            var wcc = Wcc(node, () =>
            {
                var content = node.Data;
                if (offset + data.Length > content.Length)
                {
                    Array.Resize(ref content, checked((int)(offset + data.Length)));
                }

                data.Span.CopyTo(content.AsSpan((int)offset));
                node.Data = content;
                node.Mtime = NfsTime.From(DateTimeOffset.UtcNow);
            });
            return ValueTask.FromResult(new NfsWriteResult(data.Length, stable == NfsStable.Unstable ? NfsStable.Unstable : NfsStable.FileSync, wcc));
        }
    }

    public ValueTask<NfsCreateResult> CreateAsync(
        NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsCreateMode mode, NfsSetAttributes attributes, ulong verifier, CancellationToken cancellationToken) =>
        Make(directory, name, NfsFileType.Regular, mode, verifier);

    public ValueTask<NfsCreateResult> MakeDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsSetAttributes attributes, CancellationToken cancellationToken) =>
        Make(directory, name, NfsFileType.Directory, NfsCreateMode.Guarded, 0);

    private ValueTask<NfsCreateResult> Make(ReadOnlyMemory<byte> directory, string name, NfsFileType type, NfsCreateMode mode, ulong verifier)
    {
        lock (_lock)
        {
            var dir = Directory(directory);
            if (dir.Children.TryGetValue(name, out var existing))
            {
                var node = _nodes[existing];
                var same = mode == NfsCreateMode.Unchecked || (mode == NfsCreateMode.Exclusive && node.Verifier == verifier);
                if (!same || type == NfsFileType.Directory)
                {
                    throw new NfsException(NfsStatus.Exist, name);
                }

                return ValueTask.FromResult(new NfsCreateResult(Handle(existing), Attributes(node), new NfsWcc(null, Attributes(dir))));
            }

            Node created = null!;
            var wcc = Wcc(dir, () =>
            {
                created = new Node(_nextId++, dir.Id, type) { Verifier = mode == NfsCreateMode.Exclusive ? verifier : null };
                _nodes[created.Id] = created;
                dir.Children[name] = created.Id;
                dir.Mtime = NfsTime.From(DateTimeOffset.UtcNow);
            });
            return ValueTask.FromResult(new NfsCreateResult(Handle(created.Id), Attributes(created), wcc));
        }
    }

    public ValueTask<NfsWcc> RemoveAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken) => Unlink(directory, name, directory: false);

    public ValueTask<NfsWcc> RemoveDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken) => Unlink(directory, name, directory: true);

    private ValueTask<NfsWcc> Unlink(ReadOnlyMemory<byte> handle, string name, bool directory)
    {
        lock (_lock)
        {
            var dir = Directory(handle);
            var node = dir.Children.TryGetValue(name, out var id) ? _nodes[id] : throw new NfsException(NfsStatus.NoEnt, name);
            if (directory != (node.Type == NfsFileType.Directory))
            {
                throw new NfsException(directory ? NfsStatus.NotDir : NfsStatus.IsDir, name);
            }

            if (directory && node.Children.Count > 0)
            {
                throw new NfsException(NfsStatus.NotEmpty, name);
            }

            return ValueTask.FromResult(Wcc(dir, () =>
            {
                dir.Children.Remove(name);
                _nodes.Remove(node.Id);
            }));
        }
    }

    public ValueTask<(NfsWcc From, NfsWcc To)> RenameAsync(
        NfsCaller caller, ReadOnlyMemory<byte> fromDirectory, string fromName, ReadOnlyMemory<byte> toDirectory, string toName, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var from = Directory(fromDirectory);
            var to = Directory(toDirectory);
            var id = from.Children.TryGetValue(fromName, out var found) ? found : throw new NfsException(NfsStatus.NoEnt, fromName);
            var before = NfsWccAttributes.Of(Attributes(to));
            var fromWcc = Wcc(from, () => from.Children.Remove(fromName));
            if (to.Children.TryGetValue(toName, out var replaced))
            {
                _nodes.Remove(replaced);
            }

            to.Children[toName] = id;
            return ValueTask.FromResult((fromWcc, new NfsWcc(before, Attributes(to))));
        }
    }

    public ValueTask<NfsDirectoryListing> ReadDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, ulong cookie, ulong verifier, bool plus, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var dir = Directory(directory);
            if (cookie != 0 && verifier != 7)
            {
                throw new NfsException(NfsStatus.BadCookie, "verifier");
            }

            // Cookies are the fileids (stable while an entry exists): ".", "..", then the children by name.
            var all = new List<NfsDirectoryEntry> { new(dir.Id, ".", 1), new(dir.Parent == 0 ? dir.Id : dir.Parent, "..", 2) };
            all.AddRange(dir.Children.Select(c => new NfsDirectoryEntry(c.Value, c.Key, c.Value + 2,
                plus ? Handle(c.Value) : null, plus ? Attributes(_nodes[c.Value]) : null)));
            return ValueTask.FromResult(new NfsDirectoryListing(7, [.. all.Where(e => e.Cookie > cookie)], Attributes(dir)));
        }
    }

    public ValueTask<NfsFsStat> FsStatAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new NfsFsStat(1UL << 40, 1UL << 39, 1UL << 39, 1000, 900, 900));

    public ValueTask<NfsWcc> CommitAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Commits++;
            var node = Get(handle);
            return ValueTask.FromResult(new NfsWcc(NfsWccAttributes.Of(Attributes(node)), Attributes(node)));
        }
    }

    public static string Text(ReadOnlyMemory<byte> data) => Encoding.UTF8.GetString(data.Span);
}
