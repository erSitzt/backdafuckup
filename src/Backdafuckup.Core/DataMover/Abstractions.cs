using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;

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

/// <summary>
/// The engine's hook around the VM snapshot (backup from storage-array snapshots): called right after the VM snapshot was
/// taken (the guest is thawed already) and once the backup is finished. Null from <see cref="OnVmSnapshotCreatedAsync"/>
/// means the disks are read the normal way.
/// </summary>
public interface ISnapshotLifecycleHook
{
    Task<ArrayReadLease?> OnVmSnapshotCreatedAsync(VmSnapshotContext context, IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>What the hook needs of a VM's backup: its job run, connection, session and the previous change tokens.</summary>
/// <param name="Previous">The change token of each disk's previous restore point (null: none).</param>
/// <param name="MaxPeers">How many VMs of the job run can be at this point at once (a batch waits for no more).</param>
/// <param name="RunVms">How many VMs this attempt of the job run backs up (0: unknown): once all have come to this point, wherever their
/// disks are read from, a batch waits for no more.</param>
/// <param name="Attempt">The attempt of the job run (retries come later and are counted on their own).</param>
public sealed record VmSnapshotContext(
    Guid SessionId, Guid JobId, Guid ConnectionId, IHypervisorSession Hypervisor, IVmBackupSession Session, IReadOnlyDictionary<string, ChangeToken?> Previous, int MaxPeers = 1,
    int RunVms = 0, int Attempt = 1);

/// <summary>
/// The disks of one VM read from an array snapshot instead of from the VM snapshot: their changed extents (asked while the
/// VM snapshot still existed) and readers. Disposing it gives the VM's share of the array snapshot back; the last VM of a
/// batch unpresents and deletes it.
/// </summary>
public abstract class ArrayReadLease : IAsyncDisposable
{
    /// <summary>"array snapshot bdfu-1a2b3c4d-01 of vdi-ds1 on alletra-test", for messages.</summary>
    public abstract string Description { get; }

    /// <summary>The transport recorded for the disks: <c>array-esxi-hotadd</c>, <c>array-vmfs</c> or <c>array-lvhd</c>.</summary>
    public abstract string Transport { get; }

    /// <summary>For the restore point's manifest: which array snapshots, how, and how long the VM snapshot lived.</summary>
    public abstract Repository.ArraySnapshotInfo Info { get; }

    public abstract ChangedExtents GetChangedExtents(SnapshotDisk disk);

    public abstract ChangeToken? GetNewChangeToken(SnapshotDisk disk);

    public abstract Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken);

    /// <summary>The verify-mode check of one disk (compared with the VM snapshot before it was released); null if none was made.</summary>
    public virtual DiskVerification? GetVerification(SnapshotDisk disk) => null;

    /// <summary>The reader, for messages: "LVHD (XCP-ng LVM storage)".</summary>
    public virtual string Reader => Transport;

    /// <summary>
    /// The reader read a disk wrongly (a check after the VM snapshot was released found it): it is switched off for this
    /// storage system until an administrator turns it back on.
    /// </summary>
    public virtual Task ReportMismatchAsync(SnapshotDisk disk, string details, CancellationToken cancellationToken) => Task.CompletedTask;

    public abstract ValueTask DisposeAsync();
}

/// <summary>A disk read from an array snapshot, checked against another reading: how many regions were compared and how many differed.</summary>
public sealed record DiskVerification(string Method, int Regions, int Mismatches)
{
    public bool Passed => Mismatches == 0;
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

/// <summary>A disk written at any offset (e.g. a disk attached to a proxy, written over NBD).</summary>
public interface IDiskBlockWriter : IAsyncDisposable
{
    long Capacity { get; }

    /// <summary>The largest write in one request.</summary>
    int MaxRequestBytes { get; }

    /// <summary>How many writes may be in flight at once.</summary>
    int MaxConcurrentRequests { get; }

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Makes every acknowledged write durable.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
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

/// <summary>
/// A disk written as files instead of blocks, e.g. a Kubernetes volume into a StorageClass without snapshots: the restore
/// reads the file system of the image and streams its files to <see cref="OpenFilesAsync"/>'s channel (in the format of
/// <see cref="MoverFileWriter"/>); <see cref="FinishFilesAsync"/> waits for what the mover put in place.
/// </summary>
public interface IFileLevelDiskWriter : IDiskWriter
{
    /// <summary>For messages, e.g. <c>PVC shop/data-redis-0 (cluster …)</c>.</summary>
    string Description { get; }

    /// <summary>Starts the mover on the new volume; the channel is ready to write once it is connected.</summary>
    Task<IMoverFilesChannel> OpenFilesAsync(CancellationToken cancellationToken);

    /// <summary>Waits for the mover's report of what it put in place, then stops it (the volume stays).</summary>
    Task<MoverFileReport> FinishFilesAsync(CancellationToken cancellationToken);
}
