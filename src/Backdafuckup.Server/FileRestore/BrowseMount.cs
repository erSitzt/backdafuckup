using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using Backdafuckup.FileLevel;

namespace Backdafuckup.Server.FileRestore;

/// <summary>
/// One open file browser: a pinned restore point, its disk indexes, and one parser instance for browsing (used under
/// <see cref="Gate"/> on a worker thread). Downloads and restores open their own <see cref="GuestImage"/>.
/// </summary>
public sealed class BrowseMount
{
    private const int CachedListings = 8;
    private readonly LinkedList<(string Key, List<GuestEntry> Entries)> _listings = new();
    private int _leases;
    private long _lastUsedTicks;
    private int _closed;
    private long _downloads;
    private long _downloadedBytes;

    internal BrowseMount(
        Guid id,
        string user,
        BrowsedRestorePoint point,
        Guid sessionId,
        IOperationLog log,
        IRestorePointPin pin,
        IRestorePointReader reader,
        IReadOnlyList<GuestDiskSource> disks,
        GuestImage image,
        DateTimeOffset now)
    {
        Id = id;
        User = user;
        Point = point;
        SessionId = sessionId;
        Log = log;
        Pin = pin;
        Reader = reader;
        Disks = disks;
        Image = image;
        Touch(now);
    }

    public Guid Id { get; }

    public string User { get; }

    public BrowsedRestorePoint Point { get; }

    public Guid SessionId { get; }

    public IOperationLog Log { get; }

    /// <summary>Held while the browser is open: retention and manual deletes skip the restore point.</summary>
    internal IRestorePointPin Pin { get; }

    internal IRestorePointReader Reader { get; }

    internal IReadOnlyList<GuestDiskSource> Disks { get; }

    /// <summary>Parser instance for browsing; only under <see cref="Gate"/>.</summary>
    internal GuestImage Image { get; }

    internal Lock Gate { get; } = new();

    public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

    public int Leases => Volatile.Read(ref _leases);

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public long Downloads => Interlocked.Read(ref _downloads);

    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);

    /// <summary>Keeps the browser open while a download or restore runs from it.</summary>
    public IDisposable Lease()
    {
        Interlocked.Increment(ref _leases);
        return new Releaser(this);
    }

    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

    internal void CountDownload(long bytes)
    {
        Interlocked.Increment(ref _downloads);
        Interlocked.Add(ref _downloadedBytes, bytes);
    }

    /// <summary>Sorted folder content (folders first); the last few listings are cached for paging.</summary>
    internal List<GuestEntry> ListSorted(string volume, GuestPath path)
    {
        var key = volume + "\n" + path;
        for (var node = _listings.First; node is not null; node = node.Next)
        {
            if (node.Value.Key == key)
            {
                _listings.Remove(node);
                _listings.AddFirst(node);
                return node.Value.Entries;
            }
        }

        var entries = Image.OpenFileSystem(volume).List(path)
            .OrderBy(e => e.IsDirectory ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        _listings.AddFirst((key, entries));
        if (_listings.Count > CachedListings)
        {
            _listings.RemoveLast();
        }

        return entries;
    }

    private sealed class Releaser(BrowseMount mount) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref mount._leases);
            }
        }
    }
}

/// <summary>What the browser shows about its restore point.</summary>
public sealed record BrowsedRestorePoint(Guid Id, Guid RepositoryId, string VmName, string VmNativeId, DateTimeOffset CreatedUtc, string Consistency, string? ConsistencyReason);

/// <summary>The browser is closed or belongs to another user (HTTP 404).</summary>
public sealed class MountNotFoundException(string message) : Exception(message);

/// <summary>Too many browsers are open (HTTP 409).</summary>
public sealed class MountLimitException(string message) : Exception(message);
