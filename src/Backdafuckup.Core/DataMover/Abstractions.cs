using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Core.DataMover;

/// <summary>How the disks of a backup are read.</summary>
public enum DiskTransportMode
{
    /// <summary>The fastest transport available (NBD); falls back to a streamed export if it cannot be used.</summary>
    Automatic = 0,

    /// <summary>Only NBD (parallel random access, reads only changed blocks); fails if NBD cannot be used.</summary>
    NbdOnly = 1,

    /// <summary>Only the streamed export of the platform (e.g. XAPI export_raw_vdi over HTTPS).</summary>
    HttpExportOnly = 2,
}

public sealed record DiskReaderOptions(int Connections = 4, int MaxInFlightPerConnection = 4, DiskTransportMode Transport = DiskTransportMode.Automatic);

/// <summary>The data of a snapshot disk: either random access (<see cref="IDiskReader"/>) or a stream (<see cref="ISequentialDiskReader"/>).</summary>
public interface IDiskSource : IAsyncDisposable
{
    long Capacity { get; }

    /// <summary>Short transport name for statistics ("nbd", "http-export-vhd", ...).</summary>
    string TransportName { get; }
}

/// <summary>Random-access, thread-safe reader for a (snapshot) disk.</summary>
public interface IDiskReader : IDiskSource
{
    /// <summary>Largest single read the transport accepts.</summary>
    int MaxRequestBytes { get; }

    /// <summary>How many reads may usefully be in flight at once.</summary>
    int MaxConcurrentRequests { get; }

    /// <summary>Fills <paramref name="destination"/> completely with the bytes at <paramref name="offset"/>.</summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>Receives disk data from an <see cref="ISequentialDiskReader"/>. The data is only valid during the call.</summary>
public delegate ValueTask DiskDataSink(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

/// <summary>Forward-only reader for transports that can only stream a disk (e.g. an HTTP export).</summary>
public interface ISequentialDiskReader : IDiskSource
{
    /// <summary>
    /// Streams the disk to <paramref name="sink"/>: data regions in strictly increasing, non-overlapping offset order,
    /// never concurrently. Regions that are never passed to the sink are zeros. Can be called once.
    /// </summary>
    Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken);
}

/// <summary>
/// A way of reading snapshot disks ("transport mode"): XAPI NBD now; array snapshots over SAN or HotAdd later.
/// The engine picks the transport with the highest priority that can handle a disk.
/// </summary>
public interface IDiskTransport
{
    string Name { get; }

    int Priority { get; }

    ValueTask<bool> CanHandleAsync(SnapshotDisk disk, CancellationToken cancellationToken);

    Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken);
}

/// <summary>Hook around the VM snapshot, e.g. for taking a storage-array snapshot while the VM snapshot exists.</summary>
public interface ISnapshotLifecycleHook
{
    Task OnVmSnapshotCreatedAsync(IVmBackupSession session, CancellationToken cancellationToken);

    Task OnBackupFinishedAsync(IVmBackupSession session, bool success, CancellationToken cancellationToken);
}

/// <summary>A disk of a restore point exposed as a virtual disk image.</summary>
public interface IDiskImageSource
{
    long Capacity { get; }

    int ChunkSize { get; }

    /// <summary>Regions that contain non-zero data (everything else reads as zeros).</summary>
    IReadOnlyList<DiskExtent> DataExtents { get; }

    /// <summary>Sequential stream over the whole image with parallel prefetch and verification.</summary>
    Stream OpenSequential(int prefetchChunks = 16, int workers = 8);

    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

public enum DiskWriterCapabilities
{
    Sequential = 1,
    RandomAccess = 2,
    SkipsZeroRegions = 4,
}

/// <summary>Writes a restored disk to the target platform. The writer pulls the data from the image.</summary>
public interface IDiskWriter : IAsyncDisposable
{
    string DiskId { get; }

    long Capacity { get; }

    DiskWriterCapabilities Capabilities { get; }

    /// <summary>Bytes sent to the platform so far (less than the disk size when empty regions are skipped).</summary>
    long TransferredBytes { get; }

    /// <summary>Writes the whole image; <paramref name="processed"/> receives the image bytes done so far.</summary>
    Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken);
}
