using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Core.DataMover;

public sealed record DiskReaderOptions(int Connections = 4, int MaxInFlightPerConnection = 4);

/// <summary>Random-access, thread-safe reader for a (snapshot) disk.</summary>
public interface IDiskReader : IAsyncDisposable
{
    long Capacity { get; }

    /// <summary>Largest single read the transport accepts.</summary>
    int MaxRequestBytes { get; }

    /// <summary>How many reads may usefully be in flight at once.</summary>
    int MaxConcurrentRequests { get; }

    /// <summary>Fills <paramref name="destination"/> completely with the bytes at <paramref name="offset"/>.</summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
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

    Task<IDiskReader> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken);
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

    Task WriteAsync(IDiskImageSource image, IProgress<long>? bytesWritten, CancellationToken cancellationToken);
}
