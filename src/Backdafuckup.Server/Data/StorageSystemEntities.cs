using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Server.Data;

/// <summary>A storage array the server takes snapshots on (backup from storage snapshots), e.g. an HPE Alletra MP.</summary>
public sealed class StorageSystemEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The provider kind, e.g. "AlletraMp".</summary>
    public required string Kind { get; set; }

    public required string Name { get; set; }

    public required string Address { get; set; }

    public int Port { get; set; } = 443;

    public Guid CredentialId { get; set; }

    public Credential Credential { get; set; } = null!;

    public TlsTrustMode TrustMode { get; set; } = TlsTrustMode.Pinned;

    /// <summary>Trusted certificates (SPKI SHA-256, lower-case hex).</summary>
    public List<string>? PinnedSpki { get; set; }

    public StorageSystemOptions Options { get; set; } = new();

    /// <summary>What the last connection test found (model, serial, OS, licences, privileges).</summary>
    public ArrayInfo? Facts { get; set; }

    public ConnectionStatus Status { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public uint Version { get; set; }
}

/// <summary>How the server uses a storage system's snapshots.</summary>
public sealed class StorageSystemOptions
{
    /// <summary>The array removes a snapshot by itself after this many hours unless it is extended (the safety net).</summary>
    public int ExpiryHours { get; set; } = 6;

    /// <summary>How many VMs of a job run share one array snapshot of a volume.</summary>
    public int MaxVmsPerSnapshot { get; set; } = 8;

    /// <summary>How long a batch waits for more VMs after its first one joined.</summary>
    public int BatchWindowSeconds { get; set; } = 60;

    /// <summary>Experimental readers (VMFS6 in the proxy, LVHD) may be used; off by default.</summary>
    public bool ExperimentalReaders { get; set; }

    /// <summary>The first backups per storage system compare samples with the normal path (verify mode).</summary>
    public int ProbationRuns { get; set; } = 5;
}

/// <summary>A volume of a storage system and the datastore or SR of a connection that lies on it.</summary>
public sealed class StorageVolumeMapping
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid StorageSystemId { get; set; }

    public required string VolumeUid { get; set; }

    public required string VolumeName { get; set; }

    /// <summary>Normalised WWN (<see cref="Wwns.Normalize"/>).</summary>
    public required string Wwn { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>The connection whose storage lies on this volume; null if none was found.</summary>
    public Guid? ConnectionId { get; set; }

    public string? StorageNativeId { get; set; }

    public string? StorageName { get; set; }

    public StorageLayout Layout { get; set; }

    /// <summary>Backups may use snapshots of this volume (off until an administrator turns it on).</summary>
    public bool Enabled { get; set; }

    /// <summary>Why the storage on this volume cannot be read from array snapshots (e.g. "spans 2 LUNs").</summary>
    public string? Refusal { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
