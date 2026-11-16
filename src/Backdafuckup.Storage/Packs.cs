using Backdafuckup.Core.Repository;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Storage;

internal enum PackState
{
    /// <summary>Being written in <c>incoming/</c>; readable, usable for dedup, not yet durable.</summary>
    Open,

    /// <summary>Durable in <c>packs/</c>.</summary>
    Sealed,

    /// <summary>Chosen by GC for repacking: still readable, but new writes no longer deduplicate against it.</summary>
    Retiring,

    /// <summary>Deleted, or its seal failed: never read or deduplicated against.</summary>
    Dead,
}

internal sealed class PackFile(Guid id, string path, PackState state, long length, DateTime sealedUtc)
{
    private int _state = (int)state;
    private string _path = path;

    public Guid Id { get; } = id;

    public string Name => Id.ToString("N");

    public string Path
    {
        get => Volatile.Read(ref _path);
        set => Volatile.Write(ref _path, value);
    }

    public PackState State
    {
        get => (PackState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public long Length { get; set; } = length;

    public DateTime SealedUtc { get; set; } = sealedUtc;

    /// <summary>The appender writing this pack, while it is open.</summary>
    public PackAppender? Owner { get; set; }

    /// <summary>Completes when the pack is sealed and durable; faults when sealing failed.</summary>
    public TaskCompletionSource Sealed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool UsableForDedup => State is PackState.Open or PackState.Sealed;

    public bool Readable => State is not PackState.Dead;

    public override string ToString() => Path;
}

/// <summary>Where a chunk's BDCK record lies.</summary>
internal readonly record struct PackLocation(PackFile Pack, uint Offset, uint StoredLength, uint RawLength);

/// <summary>A pack being written: its file handle, its records so far and the next free offset.</summary>
internal sealed class OpenPack(PackFile pack, SafeFileHandle handle)
{
    public PackFile Pack { get; } = pack;

    public SafeFileHandle Handle { get; } = handle;

    public List<PackTrailerEntry> Entries { get; } = [];

    public long Position { get; set; } = PackFormat.HeaderSize;
}

/// <summary>
/// Appends chunk records to one open pack at a time for one owner (a restore point or SQL backup writer, GC or packing).
/// Appends are synchronous and short (page cache writes); sealing, which flushes to disk, runs outside every lock.
/// </summary>
internal sealed class PackAppender(PackStore store, long targetSize)
{
    private readonly Lock _lock = new();
    private readonly List<Task> _seals = [];
    private OpenPack? _current;

    /// <summary>Appends a record and returns where it lies. <paramref name="full"/> is a pack the caller must seal now.</summary>
    public PackLocation Append(ChunkId id, ReadOnlyMemory<byte> stored, uint rawLength, out OpenPack? full)
    {
        full = null;
        var recordLength = PackFormat.RecordIdSize + stored.Length;
        lock (_lock)
        {
            if (_current is not null && _current.Entries.Count > 0
                && _current.Position + recordLength + ((_current.Entries.Count + 1) * PackFormat.TrailerEntrySize) + PackFormat.FooterSize > targetSize)
            {
                full = _current;
                _current = null;
            }

            _current ??= store.CreateOpenPack(this);
            var position = _current.Position;
            if (position + recordLength + ((_current.Entries.Count + 1L) * PackFormat.TrailerEntrySize) + PackFormat.FooterSize > PackFormat.MaxPackSize)
            {
                throw new InvalidOperationException($"Pack '{_current.Pack.Path}' cannot take a record of {recordLength} bytes.");
            }

            var idBytes = new byte[PackFormat.RecordIdSize];
            id.WriteTo(idBytes);
            RandomAccess.Write(_current.Handle, new ReadOnlyMemory<byte>[] { idBytes, stored }, position);
            var entry = new PackTrailerEntry(id, (uint)(position + PackFormat.RecordIdSize), (uint)stored.Length, rawLength);
            _current.Entries.Add(entry);
            _current.Position = position + recordLength;
            return new PackLocation(_current.Pack, entry.Offset, entry.StoredLength, entry.RawLength);
        }
    }

    /// <summary>Seals a pack that <see cref="Append"/> handed back.</summary>
    public Task SealAsync(OpenPack pack)
    {
        var task = store.SealAsync(pack);
        lock (_lock)
        {
            _seals.RemoveAll(t => t.IsCompletedSuccessfully);
            _seals.Add(task);
        }

        return task;
    }

    /// <summary>Seals the open pack, if it holds anything, and waits for every seal of this appender.</summary>
    public async Task FlushAsync()
    {
        OpenPack? current;
        Task[] seals;
        lock (_lock)
        {
            current = _current;
            _current = null;
        }

        if (current is not null)
        {
            if (current.Entries.Count == 0)
            {
                store.DiscardOpenPack(current);
            }
            else
            {
                _ = SealAsync(current);
            }
        }

        lock (_lock)
        {
            seals = [.. _seals];
        }

        await Task.WhenAll(seals).ConfigureAwait(false);
    }

    /// <summary>Seals <paramref name="pack"/> if it is this appender's open pack (another writer depends on it).</summary>
    public Task SealIfCurrentAsync(PackFile pack)
    {
        OpenPack? current = null;
        lock (_lock)
        {
            if (_current?.Pack == pack)
            {
                current = _current;
                _current = null;
            }
        }

        if (current is not null)
        {
            _ = SealAsync(current);
        }

        return pack.Sealed.Task;
    }
}

/// <summary>Read handles of packs, shared and bounded; a handle in use is never closed under its reader.</summary>
internal sealed class PackHandleCache(int capacity) : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PackFile, Entry> _entries = [];
    private long _clock;

    public Lease Acquire(PackFile pack)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(pack, out var existing) && !existing.Closing)
            {
                existing.Refs++;
                existing.LastUse = ++_clock;
                return new Lease(this, existing);
            }
        }

        // Open outside the lock; a racing opener's handle is simply closed again.
        var handle = File.OpenHandle(pack.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        lock (_lock)
        {
            if (_entries.TryGetValue(pack, out var existing) && !existing.Closing)
            {
                handle.Dispose();
                existing.Refs++;
                existing.LastUse = ++_clock;
                return new Lease(this, existing);
            }

            var entry = new Entry(pack, handle) { Refs = 1, LastUse = ++_clock };
            _entries[pack] = entry;
            EvictLocked();
            return new Lease(this, entry);
        }
    }

    /// <summary>Closes the handle of <paramref name="pack"/> once its readers are done (before the pack is deleted).</summary>
    public void Forget(PackFile pack)
    {
        lock (_lock)
        {
            if (_entries.Remove(pack, out var entry))
            {
                entry.Closing = true;
                if (entry.Refs == 0)
                {
                    entry.Handle.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Closing = true;
                if (entry.Refs == 0)
                {
                    entry.Handle.Dispose();
                }
            }

            _entries.Clear();
        }
    }

    private void Release(Entry entry)
    {
        lock (_lock)
        {
            entry.Refs--;
            if (entry.Refs == 0 && entry.Closing)
            {
                entry.Handle.Dispose();
            }
        }
    }

    private void EvictLocked()
    {
        if (_entries.Count <= capacity)
        {
            return;
        }

        foreach (var victim in _entries.Values.Where(e => e.Refs == 0).OrderBy(e => e.LastUse).Take(_entries.Count - capacity).ToList())
        {
            _entries.Remove(victim.Pack);
            victim.Closing = true;
            victim.Handle.Dispose();
        }
    }

    internal sealed class Entry(PackFile pack, SafeFileHandle handle)
    {
        public PackFile Pack { get; } = pack;

        public SafeFileHandle Handle { get; } = handle;

        public int Refs { get; set; }

        public long LastUse { get; set; }

        public bool Closing { get; set; }
    }

    internal readonly struct Lease(PackHandleCache cache, Entry entry) : IDisposable
    {
        public SafeFileHandle Handle => entry.Handle;

        public void Dispose() => cache.Release(entry);
    }
}
