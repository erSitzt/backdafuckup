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

    /// <summary>"crash" for now; "application" once the guest agent exists.</summary>
    public string Consistency { get; init; } = "crash";

    /// <summary>Informational only: the restore point whose indexes were reused for unchanged chunks.</summary>
    public Guid? BaseRestorePointId { get; init; }

    public ManifestJob? Job { get; init; }

    public required ManifestSource Source { get; init; }

    public required VmDescriptor Vm { get; init; }

    public ManifestBlob? Metadata { get; init; }

    public required ManifestChunking Chunking { get; init; }

    public required IReadOnlyList<ManifestDisk> Disks { get; init; }
}

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
