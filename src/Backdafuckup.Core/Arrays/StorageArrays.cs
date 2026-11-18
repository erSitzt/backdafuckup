using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;

namespace Backdafuckup.Core.Arrays;

/// <summary>How to reach a storage array's management API.</summary>
/// <param name="ServerInstanceId">This backup server's id: written into every object it creates, the proof that it is ours.</param>
public sealed record ArrayConnectionSettings(
    string Address, int Port, string UserName, string Password, TlsTrustMode TrustMode, IReadOnlyList<string> PinnedSpkiSha256, string ServerInstanceId);

/// <summary>A licensed feature of the array and when it expires (null: permanent).</summary>
public sealed record ArrayLicense(string Name, DateTimeOffset? ExpiresUtc);

/// <summary>What the array says about itself and the account, for the connection test.</summary>
/// <param name="Privileges">What the account may do (e.g. the array role "edit").</param>
/// <param name="SessionTimeout">Idle timeout of an API session.</param>
public sealed record ArrayInfo(
    string Name, string Model, string Serial, string SystemWwn, string OsVersion, string ApiVersion, IReadOnlyList<ArrayLicense> Licenses,
    IReadOnlyList<string> Privileges, TimeSpan? SessionTimeout);

/// <summary>A volume of the array (a LUN of a datastore or SR, or a snapshot).</summary>
/// <param name="Id">The array's numeric id (snapshots name their base by it).</param>
/// <param name="CopyOfId">For a snapshot: the id of the volume it is a copy of.</param>
public sealed record ArrayVolume(
    string Uid, long Id, string Name, string Wwn, long SizeBytes, string Type, long? CopyOfId, string? Comment, bool ReadOnly, string? Domain)
{
    public bool IsSnapshot => Type == ArrayVolumeTypes.Snapshot;
}

public static class ArrayVolumeTypes
{
    public const string Base = "base";

    public const string Snapshot = "snapshot";

    public const string Clone = "clone";
}

/// <summary>A host object of the array with its iSCSI initiator names.</summary>
public sealed record ArrayHost(string Uid, string Name, IReadOnlyList<string> IscsiNames, string? Persona, string? Domain);

/// <summary>An iSCSI port of the array: where initiators log in.</summary>
public sealed record ArrayIscsiPortal(string Address, string? TargetIqn, string Port);

/// <summary>
/// Snapshots of several volumes at once (one per volume; consistent across them only if the array can). Never with a
/// retention: <paramref name="Expiry"/> is the safety net that removes a forgotten snapshot.
/// </summary>
/// <param name="NameBase">Our snapshots are named <c>NameBase-01</c>, <c>-02</c>, …</param>
public sealed record ArraySnapshotRequest(IReadOnlyList<ArrayVolume> Volumes, string NameBase, string Comment, bool ReadOnly, TimeSpan Expiry);

/// <summary>A snapshot this server took.</summary>
public sealed record ArraySnapshot(string Uid, long Id, string Name, string Wwn, string SourceUid, long SourceId, string SourceName, DateTimeOffset ExpiresUtc);

/// <summary>A volume presented (exported) to one host object: the host sees it as this LUN.</summary>
public sealed record ArrayPresentation(string Uid, int Lun, string Wwn, string HostName, string VolumeName);

/// <summary>A storage array kind (e.g. HPE Alletra MP) and how to talk to it.</summary>
public interface IStorageArrayProvider
{
    /// <summary>"AlletraMp".</summary>
    string Kind { get; }

    /// <summary>Signs in, reads the facts and signs out; throws with a message naming the address on failure.</summary>
    Task<ArrayInfo> TestConnectionAsync(ArrayConnectionSettings settings, CancellationToken cancellationToken);

    Task<IStorageArraySession> OpenAsync(ArrayConnectionSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// A signed-in session to an array. Every object it creates is recorded in the ledger before the call, and named and
/// commented so that it is recognisably this server's; objects it did not create are never changed or deleted.
/// </summary>
public interface IStorageArraySession : IAsyncDisposable
{
    ArrayInfo Info { get; }

    /// <summary>The volumes with these WWNs (any notation: with or without colons, any case).</summary>
    Task<IReadOnlyList<ArrayVolume>> FindVolumesByWwnAsync(IReadOnlyCollection<string> wwns, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArrayHost>> ListHostsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ArrayIscsiPortal>> GetIscsiPortalsAsync(CancellationToken cancellationToken);

    /// <summary>One snapshot per volume of the request, each recorded in the ledger before it is taken.</summary>
    Task<IReadOnlyList<ArraySnapshot>> CreateSnapshotsAsync(ArraySnapshotRequest request, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken);

    /// <summary>Moves the expiry of a snapshot that is still in use (the safety net would otherwise remove it).</summary>
    Task ExtendExpiryAsync(ArraySnapshot snapshot, TimeSpan expiry, CancellationToken cancellationToken);

    /// <summary>Presents a snapshot to exactly one host object (never a host set), recorded in the ledger first.</summary>
    Task<ArrayPresentation> PresentAsync(ArraySnapshot snapshot, string hostName, IArtifactLedger ledger, CancellationToken cancellationToken);

    Task UnpresentAsync(ArrayPresentation presentation, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a snapshot after checking every proof that it is ours: name prefix, this server's id in its comment, a
    /// snapshot, a copy of the recorded base. Refuses (throws) otherwise.
    /// </summary>
    Task DeleteSnapshotAsync(ArraySnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Snapshots and presentations of this server on the array (for the orphan scan).</summary>
    Task<IReadOnlyList<ProviderArtifact>> FindTaggedAsync(CancellationToken cancellationToken);

    /// <summary>Removes a leftover from the ledger (a presentation, then a snapshot), with the same proofs as above.</summary>
    Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken);
}

/// <summary>Ledger kinds of array objects and of what a host made of them.</summary>
public static class ArrayArtifactKinds
{
    /// <summary>An array snapshot (NativeId: its uid; NativeRef: its name; Data: <see cref="ArraySnapshot"/> as JSON).</summary>
    public const string Snapshot = "array.snapshot";

    /// <summary>A presentation (VLUN) of a snapshot to a host object (NativeId: its uid; Data: <see cref="ArrayPresentation"/>).</summary>
    public const string Presentation = "array.vlun";

    /// <summary>A host object created for a proxy (only when an administrator asks).</summary>
    public const string Host = "array.host";

    /// <summary>A snapshot LUN resignatured and mounted as a temporary datastore on an ESXi host.</summary>
    public const string EsxTempDatastore = "vsphere.temp-datastore";

    /// <summary>A detached SCSI LUN whose state an ESXi host still keeps.</summary>
    public const string EsxScsiLunState = "vsphere.scsi-lun-state";

    /// <summary>A snapshot LUN logged in on a proxy.</summary>
    public const string ProxyLun = "proxy.lun";
}

/// <summary>How the disks of a datastore or SR lie on its LUN.</summary>
public enum StorageLayout
{
    Other,

    /// <summary>XCP-ng LVM SR: VHDs in logical volumes.</summary>
    Lvhd,

    Vmfs5,

    Vmfs6,
}

/// <summary>The LUNs behind a hypervisor storage (SR, datastore), or why it cannot be read from array snapshots.</summary>
public sealed record StorageBacking(string StorageId, StorageLayout Layout, IReadOnlyList<string> LunWwns, string? Refusal);

/// <summary>Optional on a hypervisor session: which LUNs hold a storage.</summary>
public interface IStorageBackingSource
{
    Task<IReadOnlyList<StorageBacking>> GetStorageBackingAsync(IReadOnlyCollection<string> storageIds, CancellationToken cancellationToken);
}

/// <summary>
/// Optional on a hypervisor session: holds off our own snapshot operations on these storages while an array snapshot is
/// taken, so that none of them is half done on the LUN.
/// </summary>
public interface IStorageSnapshotGate
{
    Task<IAsyncDisposable> HoldAsync(IReadOnlyCollection<string> storageIds, CancellationToken cancellationToken);
}

/// <summary>WWN notation: lower-case hex without separators (and without a leading "naa." or SCSI "3").</summary>
public static class Wwns
{
    public static string Normalize(string wwn)
    {
        ArgumentNullException.ThrowIfNull(wwn);
        var text = wwn.Trim().ToLowerInvariant();
        if (text.StartsWith("naa.", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        text = new string(text.Where(Uri.IsHexDigit).ToArray());

        // A SCSI id (XCP-ng's SCSIid) is the designator type "3" followed by the NAA identifier.
        return text.Length == 33 && text[0] == '3' ? text[1..] : text;
    }
}
