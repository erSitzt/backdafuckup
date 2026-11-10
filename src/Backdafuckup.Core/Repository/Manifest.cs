using System.Text.Json;
using System.Text.Json.Serialization;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Core.Repository;

public enum RestorePointKind
{
    Full,
    Incremental,
}

public enum DiskBackupMode
{
    Full,
    Cbt,
}

/// <summary>
/// The commit record of a restore point (<c>manifest.json</c>). A restore point exists exactly when its manifest
/// exists. Every manifest references complete chunk indexes, so each restore point is restorable on its own.
/// </summary>
public sealed record RestorePointManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid RestorePointId { get; init; }

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required RestorePointKind Kind { get; init; }

    /// <summary>"crash" or "application" (guest frozen through the agent while the snapshot was taken).</summary>
    public string Consistency { get; init; } = "crash";

    /// <summary>Why a backup with guest processing is only crash-consistent.</summary>
    public string? ConsistencyReason { get; init; }

    /// <summary>What the guest contained (volumes, VSS writers, SQL databases), as reported by the agent.</summary>
    public ManifestBlob? Guest { get; init; }

    /// <summary>Number of SQL Server databases in <see cref="Guest"/>; null in manifests written before it was recorded.</summary>
    public int? SqlDatabaseCount { get; init; }

    /// <summary>Whether the VM was a domain controller (its directory facts are in <see cref="Guest"/>); null in older manifests.</summary>
    public bool? DomainController { get; init; }

    /// <summary>The DNS name of the domain controller's domain ("" if the agent could not read it).</summary>
    public string? AdDomain { get; init; }

    /// <summary>The VSS documents of the guest (SQL Server writer metadata, Backup Components Document), if any.</summary>
    public ManifestBlob? VssDocuments { get; init; }

    /// <summary>The SQL Server snapshot (recorded databases and their <c>.bkm</c>), if the guest was frozen that way.</summary>
    public ManifestBlob? SqlSnapshot { get; init; }

    /// <summary>Informational only: the restore point whose indexes were reused for unchanged chunks.</summary>
    public Guid? BaseRestorePointId { get; init; }

    public ManifestJob? Job { get; init; }

    public required ManifestSource Source { get; init; }

    public required VmDescriptor Vm { get; init; }

    public ManifestBlob? Metadata { get; init; }

    public required ManifestChunking Chunking { get; init; }

    public required IReadOnlyList<ManifestDisk> Disks { get; init; }

    /// <summary>Disks of the VM that were deliberately not backed up; a restore of the VM creates them empty.</summary>
    public IReadOnlyList<ManifestExcludedDisk> ExcludedDisks { get; init; } = [];

    /// <summary>
    /// The RAM image of a snapshot with memory (key <c>memory</c>), stored like a disk but not a disk of the VM; null without
    /// memory. A restore that resumes the VM uploads it as the VM's suspend image.
    /// </summary>
    public ManifestDisk? Memory { get; init; }

    /// <summary>The metadata of the snapshot with memory itself (Xen: export_metadata of the checkpoint), for a resume.</summary>
    public ManifestBlob? MemoryMetadata { get; init; }

    /// <summary>The disks and, with memory, the RAM image: everything whose chunks the restore point references.</summary>
    [JsonIgnore]
    public IEnumerable<ManifestDisk> AllImages => Memory is null ? Disks : Disks.Append(Memory);
}

/// <summary>A disk that was left out of the backup (e.g. marked [NOBAK]): what is needed to create it empty again.</summary>
public sealed record ManifestExcludedDisk(
    string Key,
    string SourceDiskId,
    string Name,
    long CapacityBytes,
    bool Bootable,
    string? StorageId,
    string? StorageName);

public sealed record ManifestJob(Guid Id, string Name);

public sealed record ManifestSource(
    HypervisorKind Provider,
    string PlatformId,
    string VmId,
    string VmName,
    string? SnapshotId);

public sealed record ManifestBlob(string File, string Sha256, long Length, string Format);

public sealed record ManifestChunking(int ChunkSize, string Hash, string Compression);

public sealed record ManifestDisk
{
    public required string Key { get; init; }

    public required string SourceDiskId { get; init; }

    public required string Name { get; init; }

    public required long CapacityBytes { get; init; }

    public bool Bootable { get; init; }

    public string? StorageId { get; init; }

    public string? StorageName { get; init; }

    public required ManifestIndexRef Index { get; init; }

    public ChangeToken? ChangeToken { get; init; }

    public required DiskBackupStats Stats { get; init; }
}

public sealed record ManifestIndexRef(string File, string Sha256, long ChunkCount);

public sealed record DiskBackupStats
{
    public DiskBackupMode Mode { get; init; }

    public string? FullReason { get; init; }

    public long ReadBytes { get; init; }

    public long ChangedChunks { get; init; }

    public long NewChunks { get; init; }

    public long NewStoredBytes { get; init; }

    public long ZeroChunks { get; init; }

    public long UnchangedChunks { get; init; }

    /// <summary>Transport the disk was read with ("nbd", "http-export-vhd", ...); null in restore points of older versions.</summary>
    public string? Transport { get; init; }

    /// <summary>Non-zero chunks that were already in the repository (or identical to the previous restore point).</summary>
    public long DedupedChunks { get; init; }

    /// <summary>Uncompressed size of the chunks stored new (compression ratio = NewDataBytes / NewStoredBytes).</summary>
    public long NewDataBytes { get; init; }

    /// <summary>Time the disk transfer took.</summary>
    public double DurationSeconds { get; init; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RestorePointManifest))]
[JsonSerializable(typeof(RepositoryDescriptor))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>Contents of <c>repo.json</c>, which makes a repository self-describing.</summary>
public sealed record RepositoryDescriptor
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required Guid RepositoryId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required int ChunkSize { get; init; }

    public string Hash { get; init; } = "sha256";

    public required RepositoryCompression Compression { get; init; }
}

public sealed record RepositoryCompression(string Codec, int Level);
