using Backdafuckup.Core.Extents;

namespace Backdafuckup.Xen;

/// <summary>Server-wide settings of the XCP-ng / XenServer integration (configuration section "Xen").</summary>
public sealed record XenOptions
{
    /// <summary>
    /// Bit order of the bitmap returned by VDI.list_changed_blocks. <c>null</c> = not yet verified against a real
    /// host: incremental backups are then disabled and every backup reads the full disk (safe default, spec risk 1).
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
}

/// <summary>No NBD-enabled network reaches the storage of a disk.</summary>
public sealed class XenNbdUnavailableException(string message) : InvalidOperationException(message);
