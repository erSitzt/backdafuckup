using System.Buffers.Binary;
using System.Security.Cryptography;
using Backdafuckup.Nfs;

namespace Backdafuckup.InstantRecovery;

/// <summary>Limits of one instant recovery's overlay.</summary>
public sealed record InstantRecoveryFsOptions
{
    /// <summary>The most bytes the overlay may hold (written blocks and the files ESXi creates); beyond it writes get NOSPC.</summary>
    public long OverlayBudgetBytes { get; init; } = long.MaxValue;

    /// <summary>Space always left free on the overlay's volume.</summary>
    public long FreeSpaceReserveBytes { get; init; } = 10L * 1024 * 1024 * 1024;
}

/// <summary>
/// The datastore of one instant recovery session, served over NFSv3. The namespace (directories, files, attributes) is
/// kept in memory and journaled before each change is answered; a file's data lives in the overlay directory, and a disk
/// of a restore point is read from the backup until written. Handles carry the file id and an HMAC with a per-installation
/// key, so they stay valid across server restarts and cannot be guessed. Opening a directory again replays its journal.
/// </summary>
public sealed class InstantRecoveryFileSystem : INfsFileSystem, IDisposable
{
    private const ulong RootId = 1;
    private const uint Generation = 1;
    private const ulong DirectoryVerifier = 1;

    private readonly string _directory;
    private readonly string _dataDirectory;
    private readonly ulong _exportId;
    private readonly byte[] _key;
    private readonly Func<string, IBaseImage> _bases;
    private readonly InstantRecoveryFsOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _namespace = new(1, 1);
    private readonly Dictionary<ulong, Node> _nodes = [];
    private readonly NamespaceJournal _journal;
    private ulong _nextId = RootId + 1;
    private long _overlayBytes;
    private long _peakOverlayBytes;
    private long _lastIoTicks;

    private sealed class Node(ulong id, ulong parent, string name, NfsFileType type)
    {
        public ulong Id { get; } = id;

        public ulong Parent { get; set; } = parent;

        public string Name { get; set; } = name;

        public NfsFileType Type { get; } = type;

        public uint Mode { get; set; }

        public uint Uid { get; set; }

        public uint Gid { get; set; }

        public long Size { get; set; }

        public long MtimeTicks { get; set; }

        public long CtimeTicks { get; set; }

        public string? BaseKey { get; init; }

        public long BaseLimit { get; set; } = long.MaxValue;

        public ulong? Verifier { get; init; }

        public bool TimesDirty { get; set; }

        public SortedDictionary<string, ulong>? Children { get; } = type == NfsFileType.Directory ? new(StringComparer.Ordinal) : null;

        public OverlayFile? Data { get; set; }
    }

    private InstantRecoveryFileSystem(
        string directory, ulong exportId, byte[] key, Func<string, IBaseImage> bases, InstantRecoveryFsOptions options, TimeProvider time, NamespaceJournal journal)
    {
        _directory = directory;
        _dataDirectory = Path.Combine(directory, "data");
        _exportId = exportId;
        _key = key;
        _bases = bases;
        _options = options;
        _time = time;
        _lastIoTicks = time.GetUtcNow().UtcTicks;
        _journal = journal;
        WriteVerifier = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        RootHandle = Handle(RootId);
    }

    /// <summary>Opens the session's overlay directory, replaying its journal (a new directory starts empty).</summary>
    /// <param name="handleKey">The installation's key for the handles' HMAC (at least 32 bytes).</param>
    /// <param name="bases">The restore point disk of a base key (as given to <see cref="CreateBaseFile"/>).</param>
    public static InstantRecoveryFileSystem Open(
        string directory, ulong exportId, byte[] handleKey, Func<string, IBaseImage> bases, InstantRecoveryFsOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(handleKey);
        if (handleKey.Length < 32)
        {
            throw new ArgumentException("The handle key needs at least 32 bytes.", nameof(handleKey));
        }

        System.IO.Directory.CreateDirectory(Path.Combine(directory, "data"));
        var (journal, records) = NamespaceJournal.Open(Path.Combine(directory, "namespace.log"));
        var fs = new InstantRecoveryFileSystem(directory, exportId, handleKey, bases, options ?? new InstantRecoveryFsOptions(), time ?? TimeProvider.System, journal);
        var now = fs._time.GetUtcNow().UtcTicks;
        fs._nodes[RootId] = new Node(RootId, RootId, string.Empty, NfsFileType.Directory) { Mode = 0x1ED, MtimeTicks = now, CtimeTicks = now };
        foreach (var record in records)
        {
            fs.Apply(record);
        }

        fs.OpenData();
        return fs;
    }

    public ReadOnlyMemory<byte> RootHandle { get; }

    public ulong WriteVerifier { get; }

    /// <summary>Bytes the overlay holds now.</summary>
    public long OverlayBytes => Interlocked.Read(ref _overlayBytes);

    /// <summary>
    /// The most the overlay held since the file system was opened: what the VMs wrote, even after a Storage vMotion
    /// removed their files at its end.
    /// </summary>
    public long PeakOverlayBytes => Math.Max(Interlocked.Read(ref _peakOverlayBytes), OverlayBytes);

    /// <summary>When a host last read or wrote a file (or when the file system was opened).</summary>
    public DateTimeOffset LastIoUtc => new(Interlocked.Read(ref _lastIoTicks), TimeSpan.Zero);

    private void Touch() => Interlocked.Exchange(ref _lastIoTicks, _time.GetUtcNow().UtcTicks);

    private void AddOverlay(long bytes)
    {
        var now = Interlocked.Add(ref _overlayBytes, bytes);
        for (var peak = Interlocked.Read(ref _peakOverlayBytes); now > peak; peak = Interlocked.Read(ref _peakOverlayBytes))
        {
            if (Interlocked.CompareExchange(ref _peakOverlayBytes, now, peak) == peak)
            {
                break;
            }
        }
    }

    public string DirectoryPath => _directory;

    // ---- setup by the session (before the datastore is mounted) ----

    /// <summary>Creates a directory (and its parents) if missing; returns its handle.</summary>
    public ReadOnlyMemory<byte> CreateDirectory(string path) => Setup(() => Handle(Ensure(path, NfsFileType.Directory, null, 0, null).Id));

    /// <summary>Creates a plain file with this content (e.g. a VMDK descriptor or the NVRAM).</summary>
    public ReadOnlyMemory<byte> CreateFile(string path, ReadOnlySpan<byte> content)
    {
        var bytes = content.ToArray();
        return Setup(() =>
        {
            var node = Ensure(path, NfsFileType.Regular, null, bytes.Length, null);
            node.Data!.WriteAsync(0, bytes, bytes.Length, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            node.Data.Flush();
            AddOverlay(bytes.Length);
            return Handle(node.Id);
        });
    }

    /// <summary>Creates a file that reads the restore point disk <paramref name="baseKey"/> until written (the flat VMDK).</summary>
    public ReadOnlyMemory<byte> CreateBaseFile(string path, string baseKey, long size) =>
        Setup(() => Handle(Ensure(path, NfsFileType.Regular, baseKey, size, null).Id));

    private ReadOnlyMemory<byte> Setup(Func<ReadOnlyMemory<byte>> action)
    {
        _namespace.Wait();
        try
        {
            return action();
        }
        finally
        {
            _namespace.Release();
        }
    }

    private Node Ensure(string path, NfsFileType type, string? baseKey, long size, ulong? verifier)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = _nodes[RootId];
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (current.Children!.TryGetValue(parts[i], out var existing))
            {
                current = _nodes[existing];
                continue;
            }

            current = AddNode(current, parts[i], last ? type : NfsFileType.Directory, last ? 0x1A4u : 0x1EDu, 0, 0, last ? baseKey : null, last ? size : 0, last ? verifier : null);
        }

        return current;
    }

    private Node AddNode(Node parent, string name, NfsFileType type, uint mode, uint uid, uint gid, string? baseKey, long size, ulong? verifier)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var record = new JournalRecord.Create(_nextId, parent.Id, name, type, mode, uid, gid, now, baseKey, size, verifier);
        _journal.Append(record);
        var node = Apply(record)!;
        OpenData(node);
        return node;
    }

    // ---- replay ----

    private Node? Apply(JournalRecord record)
    {
        switch (record)
        {
            case JournalRecord.Create c:
            {
                var parent = _nodes[c.Parent];
                var node = new Node(c.FileId, c.Parent, c.Name, c.Type)
                {
                    Mode = c.Mode, Uid = c.Uid, Gid = c.Gid, Size = c.Size, MtimeTicks = c.TimeTicks, CtimeTicks = c.TimeTicks, BaseKey = c.BaseKey, Verifier = c.Verifier,
                };
                _nodes[node.Id] = node;
                parent.Children![c.Name] = node.Id;
                _nextId = Math.Max(_nextId, c.FileId + 1);
                Touch(parent, c.TimeTicks);
                return node;
            }

            case JournalRecord.Remove r:
            {
                var parent = _nodes[r.Parent];
                if (parent.Children!.Remove(r.Name, out var id))
                {
                    Drop(_nodes[id]);
                }

                return null;
            }

            case JournalRecord.Rename r:
            {
                var from = _nodes[r.FromParent];
                var to = _nodes[r.ToParent];
                if (!from.Children!.Remove(r.FromName, out var id))
                {
                    return null;
                }

                if (to.Children!.Remove(r.ToName, out var replaced))
                {
                    Drop(_nodes[replaced]);
                }

                to.Children![r.ToName] = id;
                var node = _nodes[id];
                node.Parent = to.Id;
                node.Name = r.ToName;
                return null;
            }

            case JournalRecord.SetAttributes s:
            {
                var node = _nodes[s.FileId];
                node.Mode = s.Mode ?? node.Mode;
                node.Uid = s.Uid ?? node.Uid;
                node.Gid = s.Gid ?? node.Gid;
                if (s.Size is { } size)
                {
                    node.BaseLimit = Math.Min(node.BaseLimit, size);
                    node.Size = size;
                }

                node.MtimeTicks = Math.Max(node.MtimeTicks, s.TimeTicks);
                node.CtimeTicks = Math.Max(node.CtimeTicks, s.TimeTicks);
                return null;
            }

            case JournalRecord.Times t:
            {
                if (_nodes.TryGetValue(t.FileId, out var node))
                {
                    node.MtimeTicks = Math.Max(node.MtimeTicks, t.MtimeTicks);
                    node.CtimeTicks = Math.Max(node.CtimeTicks, t.MtimeTicks);
                }

                return null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(record));
        }
    }

    /// <summary>Opens every file's data after the replay; data files of nothing any more (a crash between journal and delete) go.</summary>
    private void OpenData()
    {
        foreach (var node in _nodes.Values.Where(n => n.Type == NfsFileType.Regular))
        {
            OpenData(node);
            node.Size = Math.Max(node.Size, node.Data!.DataLength);
            node.MtimeTicks = Math.Max(node.MtimeTicks, System.IO.File.GetLastWriteTimeUtc(DataPath(node.Id)).Ticks);
            AddOverlay(node.Data!.OverlayBytes);
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(_dataDirectory))
        {
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file), out var id) || !_nodes.ContainsKey(id))
            {
                System.IO.File.Delete(file);
            }
        }
    }

    private void OpenData(Node node)
    {
        if (node.Type != NfsFileType.Regular || node.Data is not null)
        {
            return;
        }

        var baseImage = node.BaseKey is { } key ? _bases(key) : null;
        node.Data = new OverlayFile(DataPath(node.Id), baseImage is null ? null : MapPath(node.Id), baseImage, baseImage is null ? 0 : Math.Min(baseImage.Size, node.BaseLimit));
    }

    private string DataPath(ulong id) => Path.Combine(_dataDirectory, $"{id}.data");

    private string MapPath(ulong id) => Path.Combine(_dataDirectory, $"{id}.map");

    /// <summary>A node removed from the namespace: its data files go (also its children's, for a directory replaced by rename).</summary>
    private void Drop(Node node)
    {
        foreach (var child in node.Children?.Values.ToList() ?? [])
        {
            Drop(_nodes[child]);
        }

        _nodes.Remove(node.Id);
        if (node.Data is { } data)
        {
            Interlocked.Add(ref _overlayBytes, -data.OverlayBytes);
            data.Dispose();
            node.Data = null;
        }

        System.IO.File.Delete(DataPath(node.Id));
        System.IO.File.Delete(MapPath(node.Id));
    }

    private static void Touch(Node node, long ticks)
    {
        node.MtimeTicks = Math.Max(node.MtimeTicks, ticks);
        node.CtimeTicks = Math.Max(node.CtimeTicks, ticks);
    }

    // ---- handles and attributes ----

    private byte[] Handle(ulong fileId)
    {
        Span<byte> payload = stackalloc byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(payload, fileId);
        BinaryPrimitives.WriteUInt32BigEndian(payload[8..], Generation);
        Mac(fileId, Generation).CopyTo(payload[12..]);
        return NfsHandles.Create(_exportId, payload);
    }

    private byte[] Mac(ulong fileId, uint generation)
    {
        Span<byte> message = stackalloc byte[20];
        BinaryPrimitives.WriteUInt64BigEndian(message, _exportId);
        BinaryPrimitives.WriteUInt64BigEndian(message[8..], fileId);
        BinaryPrimitives.WriteUInt32BigEndian(message[16..], generation);
        return HMACSHA256.HashData(_key, message)[..8];
    }

    private Node Resolve(ReadOnlyMemory<byte> handle)
    {
        var span = handle.Span;
        if (!NfsHandles.TryGetExportId(span, out var export) || export != _exportId)
        {
            throw new NfsException(NfsStatus.Stale, "The handle belongs to another export.");
        }

        var payload = NfsHandles.Payload(span);
        if (payload.Length != 20)
        {
            throw new NfsException(NfsStatus.BadHandle, "The handle has the wrong length.");
        }

        var fileId = BinaryPrimitives.ReadUInt64BigEndian(payload);
        var generation = BinaryPrimitives.ReadUInt32BigEndian(payload[8..]);
        if (!CryptographicOperations.FixedTimeEquals(payload[12..], Mac(fileId, generation)))
        {
            throw new NfsException(NfsStatus.BadHandle, "The handle's signature does not match.");
        }

        lock (_nodes)
        {
            return _nodes.GetValueOrDefault(fileId) is { } node && generation == Generation
                ? node
                : throw new NfsException(NfsStatus.Stale, $"File {fileId} no longer exists.");
        }
    }

    private Node DirectoryNode(ReadOnlyMemory<byte> handle) =>
        Resolve(handle) is { Type: NfsFileType.Directory } directory ? directory : throw new NfsException(NfsStatus.NotDir, "Not a directory.");

    private Node FileNode(ReadOnlyMemory<byte> handle) => Resolve(handle) switch
    {
        { Type: NfsFileType.Directory } => throw new NfsException(NfsStatus.IsDir, "A directory."),
        var file => file,
    };

    private NfsAttributes Attributes(Node node)
    {
        lock (node)
        {
            var used = node.Type == NfsFileType.Directory ? 4096UL : (ulong)Math.Max(0, Math.Min(node.Size, node.Data?.UsedBytes ?? 0));
            var mtime = NfsTime.From(new DateTimeOffset(node.MtimeTicks, TimeSpan.Zero));
            return new NfsAttributes(
                node.Type, node.Mode, node.Type == NfsFileType.Directory ? 2u : 1u, node.Uid, node.Gid,
                node.Type == NfsFileType.Directory ? 4096UL : (ulong)node.Size, used, _exportId, node.Id, mtime, mtime,
                NfsTime.From(new DateTimeOffset(node.CtimeTicks, TimeSpan.Zero)));
        }
    }

    private NfsWccAttributes Before(Node node) => NfsWccAttributes.Of(Attributes(node));

    private async ValueTask<T> NamespaceAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _namespace.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_nodes)
            {
                return action();
            }
        }
        finally
        {
            _namespace.Release();
        }
    }

    // ---- INfsFileSystem ----

    public ValueTask<NfsAttributes> GetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Attributes(Resolve(handle)));

    public ValueTask<NfsWcc> SetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, NfsSetAttributes attributes, NfsTime? guardCtime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return NamespaceAsync(() =>
        {
            var node = Resolve(handle);
            var before = Before(node);
            if (guardCtime is { } guard && guard != Attributes(node).Ctime)
            {
                throw new NfsException(NfsStatus.NotSync, "The file changed since the client looked.");
            }

            if (attributes.Size is not null && node.Type == NfsFileType.Directory)
            {
                throw new NfsException(NfsStatus.IsDir, "A directory has no size to set.");
            }

            var now = _time.GetUtcNow().UtcTicks;
            var mtime = attributes.MtimeHow switch
            {
                NfsTimeHow.ClientTime => attributes.Mtime.ToDateTimeOffset().UtcTicks,
                NfsTimeHow.ServerTime => now,
                _ => attributes.Size is not null ? now : node.MtimeTicks,
            };
            long? size = attributes.Size is { } s ? checked((long)s) : null;

            // Shorter: the data goes first, so a crash before the journal record cannot bring it back larger.
            if (size is { } newSize && newSize < node.Size)
            {
                node.Data!.Truncate(newSize);
            }

            _journal.Append(new JournalRecord.SetAttributes(node.Id, attributes.Mode, attributes.Uid, attributes.Gid, size, Math.Max(mtime, now)));
            lock (node)
            {
                node.Mode = attributes.Mode ?? node.Mode;
                node.Uid = attributes.Uid ?? node.Uid;
                node.Gid = attributes.Gid ?? node.Gid;
                if (size is { } set)
                {
                    node.Size = set;
                    node.BaseLimit = Math.Min(node.BaseLimit, set);
                }

                node.MtimeTicks = mtime;
                node.CtimeTicks = now;
            }

            return new NfsWcc(before, Attributes(node));
        }, cancellationToken);
    }

    public ValueTask<(ReadOnlyMemory<byte> Handle, NfsAttributes Attributes)> LookupAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken) =>
        NamespaceAsync<(ReadOnlyMemory<byte>, NfsAttributes)>(() =>
        {
            var dir = DirectoryNode(directory);
            var id = name switch
            {
                "." => dir.Id,
                ".." => dir.Parent,
                _ => dir.Children!.TryGetValue(name, out var child) ? child : throw new NfsException(NfsStatus.NoEnt, $"{name} does not exist."),
            };
            return (Handle(id), Attributes(_nodes[id]));
        }, cancellationToken);

    public async ValueTask<NfsReadResult> ReadAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken)
    {
        var node = FileNode(handle);
        long size;
        lock (node)
        {
            size = node.Size;
        }

        var data = await (node.Data ?? throw new NfsException(NfsStatus.Stale, "removed")).ReadAsync(offset, count, size, cancellationToken).ConfigureAwait(false);
        Touch();
        return new NfsReadResult(data, offset + data.Length >= size, Attributes(node));
    }

    public async ValueTask<NfsWriteResult> WriteAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, ReadOnlyMemory<byte> data, NfsStable stable, CancellationToken cancellationToken)
    {
        var node = FileNode(handle);
        var overlay = node.Data ?? throw new NfsException(NfsStatus.Stale, "removed");
        var needed = data.Length + OverlayFile.BlockSize;
        if (OverlayBytes > _options.OverlayBudgetBytes - needed)
        {
            throw new NfsException(NfsStatus.NoSpc, $"The overlay of this instant recovery is full ({OverlayBytes} of {_options.OverlayBudgetBytes} bytes).");
        }

        if (FreeOnVolume() < needed)
        {
            throw new NfsException(NfsStatus.NoSpc,
                $"The volume of the overlay ({_directory}) is full: less than {needed} bytes free above the reserve of {_options.FreeSpaceReserveBytes} bytes.");
        }

        var before = Before(node);
        long size;
        lock (node)
        {
            size = node.Size;
        }

        var taken = await overlay.WriteAsync(offset, data, size, cancellationToken).ConfigureAwait(false);
        AddOverlay(taken);
        Touch();
        InstantRecoveryTelemetry.Written(data.Length);
        var now = _time.GetUtcNow().UtcTicks;
        lock (node)
        {
            node.Size = Math.Max(node.Size, offset + data.Length);
            node.MtimeTicks = now;
            node.CtimeTicks = now;
            node.TimesDirty = true;
        }

        if (stable != NfsStable.Unstable)
        {
            overlay.Flush();
        }

        return new NfsWriteResult(data.Length, stable == NfsStable.Unstable ? NfsStable.Unstable : NfsStable.FileSync, new NfsWcc(before, Attributes(node)));
    }

    public ValueTask<NfsCreateResult> CreateAsync(
        NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsCreateMode mode, NfsSetAttributes attributes, ulong verifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(attributes);
        return NamespaceAsync(() =>
        {
            var dir = DirectoryNode(directory);
            var before = Before(dir);
            if (dir.Children!.TryGetValue(name, out var existingId))
            {
                var existing = _nodes[existingId];
                var retransmission = mode == NfsCreateMode.Exclusive && existing.Verifier == verifier;
                if (existing.Type == NfsFileType.Directory || (mode != NfsCreateMode.Unchecked && !retransmission))
                {
                    throw new NfsException(NfsStatus.Exist, $"{name} exists.");
                }

                if (mode == NfsCreateMode.Unchecked && attributes.Size is { } truncate && (long)truncate < existing.Size)
                {
                    existing.Data!.Truncate((long)truncate);
                    _journal.Append(new JournalRecord.SetAttributes(existing.Id, null, null, null, (long)truncate, _time.GetUtcNow().UtcTicks));
                    lock (existing)
                    {
                        existing.Size = (long)truncate;
                    }
                }

                return new NfsCreateResult(Handle(existing.Id), Attributes(existing), new NfsWcc(before, Attributes(dir)));
            }

            var node = AddNode(dir, name, NfsFileType.Regular, attributes.Mode ?? 0x1A4, caller.Credential?.Uid ?? 0, caller.Credential?.Gid ?? 0, null, 0,
                mode == NfsCreateMode.Exclusive ? verifier : null);
            return new NfsCreateResult(Handle(node.Id), Attributes(node), new NfsWcc(before, Attributes(dir)));
        }, cancellationToken);
    }

    public ValueTask<NfsCreateResult> MakeDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsSetAttributes attributes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(attributes);
        return NamespaceAsync(() =>
        {
            var dir = DirectoryNode(directory);
            var before = Before(dir);
            if (dir.Children!.ContainsKey(name))
            {
                throw new NfsException(NfsStatus.Exist, $"{name} exists.");
            }

            var node = AddNode(dir, name, NfsFileType.Directory, attributes.Mode ?? 0x1ED, caller.Credential?.Uid ?? 0, caller.Credential?.Gid ?? 0, null, 0, null);
            return new NfsCreateResult(Handle(node.Id), Attributes(node), new NfsWcc(before, Attributes(dir)));
        }, cancellationToken);
    }

    public ValueTask<NfsWcc> RemoveAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken) => Unlink(directory, name, false, cancellationToken);

    public ValueTask<NfsWcc> RemoveDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken) => Unlink(directory, name, true, cancellationToken);

    private ValueTask<NfsWcc> Unlink(ReadOnlyMemory<byte> directory, string name, bool isDirectory, CancellationToken cancellationToken) => NamespaceAsync(() =>
    {
        var dir = DirectoryNode(directory);
        var before = Before(dir);
        var node = dir.Children!.TryGetValue(name, out var id) ? _nodes[id] : throw new NfsException(NfsStatus.NoEnt, $"{name} does not exist.");
        if (isDirectory != (node.Type == NfsFileType.Directory))
        {
            throw new NfsException(isDirectory ? NfsStatus.NotDir : NfsStatus.IsDir, $"{name} is {(isDirectory ? "no" : "a")} directory.");
        }

        if (isDirectory && node.Children!.Count > 0)
        {
            throw new NfsException(NfsStatus.NotEmpty, $"{name} is not empty.");
        }

        var record = new JournalRecord.Remove(dir.Id, name);
        _journal.Append(record);
        Apply(record);
        Touch(dir, _time.GetUtcNow().UtcTicks);
        return new NfsWcc(before, Attributes(dir));
    }, cancellationToken);

    public ValueTask<(NfsWcc From, NfsWcc To)> RenameAsync(
        NfsCaller caller, ReadOnlyMemory<byte> fromDirectory, string fromName, ReadOnlyMemory<byte> toDirectory, string toName, CancellationToken cancellationToken) =>
        NamespaceAsync(() =>
        {
            var from = DirectoryNode(fromDirectory);
            var to = DirectoryNode(toDirectory);
            var fromBefore = Before(from);
            var toBefore = Before(to);
            var node = from.Children!.TryGetValue(fromName, out var id) ? _nodes[id] : throw new NfsException(NfsStatus.NoEnt, $"{fromName} does not exist.");
            if (from.Id == to.Id && fromName == toName)
            {
                return (new NfsWcc(fromBefore, Attributes(from)), new NfsWcc(toBefore, Attributes(to)));
            }

            for (var ancestor = to; node.Type == NfsFileType.Directory; ancestor = _nodes[ancestor.Parent])
            {
                if (ancestor.Id == node.Id)
                {
                    throw new NfsException(NfsStatus.Inval, "A directory cannot move into itself.");
                }

                if (ancestor.Id == RootId)
                {
                    break;
                }
            }

            if (to.Children!.TryGetValue(toName, out var targetId))
            {
                var target = _nodes[targetId];
                if (node.Type == NfsFileType.Directory && target.Type != NfsFileType.Directory)
                {
                    throw new NfsException(NfsStatus.NotDir, $"{toName} is not a directory.");
                }

                if (node.Type != NfsFileType.Directory && target.Type == NfsFileType.Directory)
                {
                    throw new NfsException(NfsStatus.IsDir, $"{toName} is a directory.");
                }

                if (target.Children is { Count: > 0 })
                {
                    throw new NfsException(NfsStatus.NotEmpty, $"{toName} is not empty.");
                }
            }

            var record = new JournalRecord.Rename(from.Id, fromName, to.Id, toName);
            _journal.Append(record);
            Apply(record);
            var now = _time.GetUtcNow().UtcTicks;
            Touch(from, now);
            Touch(to, now);
            return (new NfsWcc(fromBefore, Attributes(from)), new NfsWcc(toBefore, Attributes(to)));
        }, cancellationToken);

    /// <summary>Entries in the order of their file ids, so a cookie (2 + file id) stays valid while others come and go.</summary>
    public ValueTask<NfsDirectoryListing> ReadDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, ulong cookie, ulong verifier, bool plus, CancellationToken cancellationToken) =>
        NamespaceAsync(() =>
        {
            var dir = DirectoryNode(directory);
            if (cookie != 0 && verifier != 0 && verifier != DirectoryVerifier)
            {
                throw new NfsException(NfsStatus.BadCookie, "The cookie verifier is not this directory's.");
            }

            var entries = new List<NfsDirectoryEntry>
            {
                new(dir.Id, ".", 1, plus ? Handle(dir.Id) : null, plus ? Attributes(dir) : null),
                new(dir.Parent, "..", 2, plus ? Handle(dir.Parent) : null, plus ? Attributes(_nodes[dir.Parent]) : null),
            };
            foreach (var (name, id) in dir.Children!.OrderBy(c => c.Value))
            {
                entries.Add(new NfsDirectoryEntry(id, name, 2 + id, plus ? Handle(id) : null, plus ? Attributes(_nodes[id]) : null));
            }

            return new NfsDirectoryListing(DirectoryVerifier, [.. entries.Where(e => e.Cookie > cookie)], Attributes(dir));
        }, cancellationToken);

    public ValueTask<NfsFsStat> FsStatAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken)
    {
        Resolve(handle);
        var used = (ulong)OverlayBytes;
        var free = (ulong)Math.Max(0, Math.Min(FreeOnVolume(), _options.OverlayBudgetBytes - OverlayBytes));
        int files;
        lock (_nodes)
        {
            files = _nodes.Count;
        }

        return ValueTask.FromResult(new NfsFsStat(used + free, free, free, 1UL << 32, (1UL << 32) - (ulong)files, (1UL << 32) - (ulong)files));
    }

    public ValueTask<NfsWcc> CommitAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken)
    {
        var node = FileNode(handle);
        var before = Before(node);
        node.Data?.Flush();
        return ValueTask.FromResult(new NfsWcc(before, Attributes(node)));
    }

    /// <summary>Writes the modification times of files written since the last checkpoint (call every 30 s and before closing).</summary>
    public void CheckpointTimes()
    {
        _namespace.Wait();
        try
        {
            List<Node> dirty;
            lock (_nodes)
            {
                dirty = [.. _nodes.Values.Where(n => n.TimesDirty)];
            }

            foreach (var node in dirty)
            {
                long ticks;
                lock (node)
                {
                    ticks = node.MtimeTicks;
                    node.TimesDirty = false;
                }

                _journal.Append(new JournalRecord.Times(node.Id, ticks));
            }
        }
        finally
        {
            _namespace.Release();
        }
    }

    private long FreeOnVolume()
    {
        try
        {
            return VolumeAvailableBytes(_directory) - _options.FreeSpaceReserveBytes;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// The free bytes of the volume that holds <paramref name="directory"/>: on Linux the mount point with the longest
    /// matching path (an overlay directory on its own volume is not measured on the root file system), else its drive.
    /// </summary>
    public static long VolumeAvailableBytes(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!OperatingSystem.IsWindows())
        {
            var mount = DriveInfo.GetDrives()
                .Where(d => d.IsReady && Under(full, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (mount is not null)
            {
                return mount.AvailableFreeSpace;
            }
        }

        return new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;

        static bool Under(string path, string root) =>
            root == "/" || path.Equals(root.TrimEnd('/'), StringComparison.Ordinal) || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        CheckpointTimes();
        lock (_nodes)
        {
            foreach (var node in _nodes.Values)
            {
                node.Data?.Dispose();
                node.Data = null;
            }
        }

        _journal.Dispose();
        _namespace.Dispose();
    }
}
