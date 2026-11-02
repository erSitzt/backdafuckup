using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;

namespace Backdafuckup.Testing;

/// <summary>A disk image in memory that records which ranges were read and written.</summary>
public sealed class InMemoryDisk
{
    private readonly Lock _lock = new();
    private readonly List<DiskExtent> _writtenSinceSnapshot = [];
    private long _bytesRead;

    public InMemoryDisk(byte[] content)
    {
        Content = content;
    }

    public byte[] Content { get; private set; }

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public static InMemoryDisk Random(long size, double zeroFraction = 0.3, int seed = 1)
    {
        var rng = new Random(seed);
        var content = new byte[size];
        const int block = 64 * 1024;
        for (long offset = 0; offset < size; offset += block)
        {
            if (rng.NextDouble() >= zeroFraction)
            {
                rng.NextBytes(content.AsSpan((int)offset, (int)Math.Min(block, size - offset)));
            }
        }

        return new InMemoryDisk(content);
    }

    /// <summary>Writes data and remembers the range as changed (like CBT).</summary>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            data.CopyTo(Content.AsSpan((int)offset));
            _writtenSinceSnapshot.Add(new DiskExtent(offset, data.Length));
        }
    }

    /// <summary>Takes a snapshot: returns a frozen copy and the extents written since the previous snapshot.</summary>
    public (InMemoryDisk Snapshot, IReadOnlyList<DiskExtent> Changed) Snapshot()
    {
        lock (_lock)
        {
            var changed = ExtentMath.Normalize(_writtenSinceSnapshot);
            _writtenSinceSnapshot.Clear();
            return (new InMemoryDisk((byte[])Content.Clone()), changed);
        }
    }

    public IDiskReader OpenReader(int maxRequestBytes = 4 * 1024 * 1024, int maxConcurrentRequests = 16) =>
        new Reader(this, maxRequestBytes, maxConcurrentRequests);

    private sealed class Reader(InMemoryDisk disk, int maxRequestBytes, int maxConcurrentRequests) : IDiskReader
    {
        public long Capacity => disk.Content.LongLength;

        public int MaxRequestBytes => maxRequestBytes;

        public int MaxConcurrentRequests => maxConcurrentRequests;

        public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destination.Length > maxRequestBytes)
            {
                throw new ArgumentException("Request larger than MaxRequestBytes.");
            }

            disk.Content.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
            Interlocked.Add(ref disk._bytesRead, destination.Length);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
