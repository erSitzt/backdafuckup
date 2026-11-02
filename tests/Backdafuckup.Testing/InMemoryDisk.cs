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

    /// <summary>
    /// A streaming reader like an HTTP export: segments of <paramref name="segmentSize"/> bytes in order. With
    /// <paramref name="sparse"/>, all-zero segments are left out (like unallocated blocks of a VHD stream).
    /// </summary>
    public ISequentialDiskReader OpenSequentialReader(int segmentSize = 3 * 1024 * 1024 / 2, bool sparse = true) =>
        new SequentialReader(this, segmentSize, sparse);

    private sealed class SequentialReader(InMemoryDisk disk, int segmentSize, bool sparse) : ISequentialDiskReader
    {
        private int _streamed;

        public long Capacity => disk.Content.LongLength;

        public string TransportName => "memory-stream";

        public async Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _streamed, 1) != 0)
            {
                throw new InvalidOperationException("The stream can only be read once.");
            }

            for (long offset = 0; offset < disk.Content.LongLength; offset += segmentSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = disk.Content.AsMemory((int)offset, (int)Math.Min(segmentSize, disk.Content.LongLength - offset));
                if (sparse && !segment.Span.ContainsAnyExcept((byte)0))
                {
                    continue;
                }

                Interlocked.Add(ref disk._bytesRead, segment.Length);
                await sink(offset, segment, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Reader(InMemoryDisk disk, int maxRequestBytes, int maxConcurrentRequests) : IDiskReader
    {
        public long Capacity => disk.Content.LongLength;

        public string TransportName => "memory";

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
