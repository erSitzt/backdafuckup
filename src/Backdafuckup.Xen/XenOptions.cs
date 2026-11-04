using Backdafuckup.Core.Extents;

namespace Backdafuckup.Xen;

/// <summary>Server-wide settings of the XCP-ng / XenServer integration (configuration section "Xen").</summary>
public sealed record XenOptions
{
    /// <summary>
    /// Bit order of the bitmap returned by VDI.list_changed_blocks, for a bit-exact decoding. <c>null</c> (default): the
    /// bitmap is read in whole bytes (512 KiB each, see <see cref="CbtBitmap.DecodeByteRanges"/>), which is right for
    /// any bit order and reads exactly the same chunks when the repository's chunk size is a multiple of 512 KiB (the
    /// default is 1 MiB). Setting it only saves reads with smaller chunks; XenServer documents
    /// <see cref="Core.Extents.CbtBitOrder.MostSignificantBitFirst"/>.
    /// </summary>
    public CbtBitOrder? CbtBitOrder { get; init; }

    /// <summary>Timeout for XAPI tasks such as snapshots and imports.</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Timeout for a whole disk import (restore).</summary>
    public TimeSpan ImportTimeout { get; init; } = TimeSpan.FromDays(1);

    public int XapiRetryAttempts { get; init; } = 10;

    public TimeSpan XapiRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pass force=true to import_metadata (needed when the target lacks e.g. the original ISO).</summary>
    public bool ForceMetadataImport { get; init; } = true;

    /// <summary>
    /// Format of the HTTP export (export_raw_vdi) used when NBD is not available: <see cref="XenHttpExportFormat.Vhd"/>
    /// transfers only allocated blocks, <see cref="XenHttpExportFormat.Raw"/> every byte of the disk (slowest, simplest).
    /// </summary>
    public XenHttpExportFormat HttpExportFormat { get; init; } = XenHttpExportFormat.Vhd;

    /// <summary>
    /// Format of the restore upload (import_raw_vdi): <see cref="XenHttpImportFormat.Vhd"/> sends only the 2 MiB blocks
    /// that contain data, <see cref="XenHttpImportFormat.Raw"/> every byte of the disk (slowest, simplest).
    /// </summary>
    public XenHttpImportFormat HttpImportFormat { get; init; } = XenHttpImportFormat.Vhd;

    /// <summary>
    /// How long a backup waits for XAPI to release a snapshot disk (VDI_IN_USE) before it reduces the snapshot to
    /// change-tracking data. After that the snapshot is kept and finished in the background.
    /// </summary>
    public TimeSpan SnapshotReleaseTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Takes the VM snapshots on a storage repository one after another (default). XAPI snapshots the disks of a VM one
    /// by one, and on shared LVM storage the snapshots of different VMs also wait for each other in the storage manager:
    /// started together, each one takes about as long as all of them. With guest processing every second counts against
    /// the 10 s for which Windows holds the writes, so the wait happens here, before the guest is frozen.
    /// </summary>
    public bool OneSnapshotPerStorage { get; init; } = true;

    /// <summary>
    /// Free space a storage repository must have before a VM with disks on it is snapshotted, in percent of its size
    /// (0: no check). A snapshot takes space until it is released; on a nearly full storage it can leave the running VMs
    /// and the storage's own cleanup (coalesce) without space.
    /// </summary>
    public double MinStorageFreePercent { get; init; } = 5;
}

public enum XenHttpExportFormat
{
    Vhd,
    Raw,
}

public enum XenHttpImportFormat
{
    Vhd,
    Raw,
}

/// <summary>Per-connection option keys stored with the connection (ConnectionSettings.Options).</summary>
public static class XenConnectionOptions
{
    /// <summary>"true" allows NBD without TLS (networks with purpose insecure_nbd). Default false.</summary>
    public const string AllowInsecureNbd = "allowInsecureNbd";
}

public static class XenArtifactKinds
{
    public const string XapiTask = "XapiTask";
    public const string VmSnapshot = "VmSnapshot";
    public const string SnapshotVdi = "SnapshotVdi";
    public const string CbtMetadataVdi = "CbtMetadataVdi";
    public const string RestoredVdi = "RestoredVdi";
    public const string RestoredVm = "RestoredVm";
}

public static class XenTags
{
    public const string Session = "backdafuckup.session";
    public const string Job = "backdafuckup.job";
    public const string RestorePoint = "backdafuckup.restorepoint";
    public const string ChangeTokenKind = "xen-cbt";
    public const string MetadataFormat = "xapi-export-metadata";

    /// <summary>
    /// Disks with this text in their name are left out of snapshots and backups. The same marker as in Xen Orchestra, so
    /// a disk marked there is also skipped here.
    /// </summary>
    public const string ExcludeDiskMarker = "[NOBAK]";
}

/// <summary>No NBD-enabled network reaches the storage of a disk.</summary>
public sealed class XenNbdUnavailableException(string message) : InvalidOperationException(message);

/// <summary>XAPI kept reporting VDI_IN_USE for a snapshot disk until the wait timed out.</summary>
public sealed class XenSnapshotInUseException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>A storage of the VM has less free space than <see cref="XenOptions.MinStorageFreePercent"/>; no snapshot was taken.</summary>
public sealed class XenStorageSpaceException(string message) : InvalidOperationException(message);

/// <summary>The HTTP export (export_raw_vdi) of a disk failed.</summary>
public sealed class XenHttpExportException(string message, Exception? inner = null) : IOException(message, inner);
