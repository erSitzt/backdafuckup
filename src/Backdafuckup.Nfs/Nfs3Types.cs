using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace Backdafuckup.Nfs;

/// <summary>nfsstat3 (RFC 1813, section 2.6).</summary>
public enum NfsStatus : uint
{
    Ok = 0,
    Perm = 1,
    NoEnt = 2,
    Io = 5,
    NxIo = 6,
    Access = 13,
    Exist = 17,
    XDev = 18,
    NoDev = 19,
    NotDir = 20,
    IsDir = 21,
    Inval = 22,
    FBig = 27,
    NoSpc = 28,
    RoFs = 30,
    MLink = 31,
    NameTooLong = 63,
    NotEmpty = 66,
    DQuot = 69,
    Stale = 70,
    Remote = 71,
    BadHandle = 10001,
    NotSync = 10002,
    BadCookie = 10003,
    NotSupported = 10004,
    TooSmall = 10005,
    ServerFault = 10006,
    BadType = 10007,
    Jukebox = 10008,
}

/// <summary>A file system operation failed with an NFS status (the dispatcher answers with it).</summary>
public sealed class NfsException(NfsStatus status, string message) : Exception(message)
{
    public NfsStatus Status { get; } = status;
}

public enum NfsFileType : uint
{
    Regular = 1,
    Directory = 2,
    Block = 3,
    Character = 4,
    Link = 5,
    Socket = 6,
    Fifo = 7,
}

/// <summary>stable_how: how far a WRITE must be on stable storage before the reply.</summary>
public enum NfsStable : uint
{
    Unstable = 0,
    DataSync = 1,
    FileSync = 2,
}

/// <summary>createmode3.</summary>
public enum NfsCreateMode : uint
{
    Unchecked = 0,
    Guarded = 1,
    Exclusive = 2,
}

/// <summary>nfstime3: seconds and nanoseconds since the epoch.</summary>
public readonly record struct NfsTime(uint Seconds, uint Nanoseconds)
{
    public static NfsTime From(DateTimeOffset time)
    {
        var ticks = time.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks;
        return new NfsTime((uint)(ticks / TimeSpan.TicksPerSecond), (uint)(ticks % TimeSpan.TicksPerSecond * 100));
    }

    public DateTimeOffset ToDateTimeOffset() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds).AddTicks(Nanoseconds / 100);
}

/// <summary>fattr3: the attributes of a file.</summary>
public sealed record NfsAttributes(
    NfsFileType Type, uint Mode, uint Links, uint Uid, uint Gid, ulong Size, ulong Used, ulong FsId, ulong FileId, NfsTime Atime, NfsTime Mtime, NfsTime Ctime);

/// <summary>wcc_attr: what a client needs to see whether its cache is still valid.</summary>
public readonly record struct NfsWccAttributes(ulong Size, NfsTime Mtime, NfsTime Ctime)
{
    public static NfsWccAttributes Of(NfsAttributes attributes) => new(attributes.Size, attributes.Mtime, attributes.Ctime);
}

/// <summary>wcc_data: the attributes before (optional) and after (optional) an operation.</summary>
public sealed record NfsWcc(NfsWccAttributes? Before, NfsAttributes? After)
{
    public static NfsWcc None { get; } = new(null, null);
}

/// <summary>How SETATTR sets a time.</summary>
public enum NfsTimeHow : uint
{
    DontChange = 0,
    ServerTime = 1,
    ClientTime = 2,
}

/// <summary>sattr3: the attributes a client sets (null: unchanged).</summary>
public sealed record NfsSetAttributes(uint? Mode, uint? Uid, uint? Gid, ulong? Size, NfsTimeHow AtimeHow, NfsTime Atime, NfsTimeHow MtimeHow, NfsTime Mtime)
{
    public static NfsSetAttributes None { get; } = new(null, null, null, null, NfsTimeHow.DontChange, default, NfsTimeHow.DontChange, default);
}

/// <summary>A directory entry (READDIR, READDIRPLUS); handle and attributes only when asked for (PLUS).</summary>
public sealed record NfsDirectoryEntry(ulong FileId, string Name, ulong Cookie, ReadOnlyMemory<byte>? Handle = null, NfsAttributes? Attributes = null);

/// <summary>The entries of a directory after a cookie, with the directory's cookie verifier.</summary>
public sealed record NfsDirectoryListing(ulong Verifier, IReadOnlyList<NfsDirectoryEntry> Entries, NfsAttributes Attributes);

public sealed record NfsReadResult(ReadOnlyMemory<byte> Data, bool Eof, NfsAttributes Attributes);

public sealed record NfsWriteResult(int Count, NfsStable Committed, NfsWcc Wcc);

public sealed record NfsCreateResult(ReadOnlyMemory<byte> Handle, NfsAttributes Attributes, NfsWcc DirectoryWcc);

public sealed record NfsFsStat(ulong TotalBytes, ulong FreeBytes, ulong AvailableBytes, ulong TotalFiles, ulong FreeFiles, ulong AvailableFiles);

/// <summary>Who calls (AUTH_SYS identity, address).</summary>
public sealed record NfsCaller(AuthSys? Credential, IPEndPoint Remote);

/// <summary>
/// A file system served over NFSv3. Handles are opaque to the protocol; each begins with <see cref="NfsHandles"/>'s prefix
/// so that calls reach their export. Operations throw <see cref="NfsException"/> with the status to answer.
/// </summary>
public interface INfsFileSystem
{
    /// <summary>The handle of the export's root directory (what MOUNT returns).</summary>
    ReadOnlyMemory<byte> RootHandle { get; }

    /// <summary>The 8-byte write verifier: changes when unstable writes may have been lost (a restart).</summary>
    ulong WriteVerifier { get; }

    ValueTask<NfsAttributes> GetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken);

    ValueTask<NfsWcc> SetAttributesAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, NfsSetAttributes attributes, NfsTime? guardCtime, CancellationToken cancellationToken);

    ValueTask<(ReadOnlyMemory<byte> Handle, NfsAttributes Attributes)> LookupAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken);

    ValueTask<NfsReadResult> ReadAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken);

    ValueTask<NfsWriteResult> WriteAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, ReadOnlyMemory<byte> data, NfsStable stable, CancellationToken cancellationToken);

    ValueTask<NfsCreateResult> CreateAsync(
        NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsCreateMode mode, NfsSetAttributes attributes, ulong verifier, CancellationToken cancellationToken);

    ValueTask<NfsCreateResult> MakeDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, NfsSetAttributes attributes, CancellationToken cancellationToken);

    ValueTask<NfsWcc> RemoveAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken);

    ValueTask<NfsWcc> RemoveDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, string name, CancellationToken cancellationToken);

    ValueTask<(NfsWcc From, NfsWcc To)> RenameAsync(
        NfsCaller caller, ReadOnlyMemory<byte> fromDirectory, string fromName, ReadOnlyMemory<byte> toDirectory, string toName, CancellationToken cancellationToken);

    /// <summary>The entries after <paramref name="cookie"/> (0: from the start); with handles and attributes when <paramref name="plus"/>.</summary>
    ValueTask<NfsDirectoryListing> ReadDirectoryAsync(NfsCaller caller, ReadOnlyMemory<byte> directory, ulong cookie, ulong verifier, bool plus, CancellationToken cancellationToken);

    ValueTask<NfsFsStat> FsStatAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, CancellationToken cancellationToken);

    /// <summary>Makes the unstable writes of the file durable.</summary>
    ValueTask<NfsWcc> CommitAsync(NfsCaller caller, ReadOnlyMemory<byte> handle, long offset, int count, CancellationToken cancellationToken);
}

/// <summary>
/// The common prefix of every handle: version 1 and the export's id (big-endian), followed by the file system's own bytes
/// (at most 64 in total, NFS3_FHSIZE). It lets one server serve several exports.
/// </summary>
public static class NfsHandles
{
    public const int MaxSize = 64;
    public const int PrefixSize = 9;
    private const byte Version = 1;

    public static byte[] Create(ulong exportId, ReadOnlySpan<byte> payload)
    {
        if (PrefixSize + payload.Length > MaxSize)
        {
            throw new ArgumentException($"A handle has at most {MaxSize - PrefixSize} bytes of payload.", nameof(payload));
        }

        var handle = new byte[PrefixSize + payload.Length];
        handle[0] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(handle.AsSpan(1), exportId);
        payload.CopyTo(handle.AsSpan(PrefixSize));
        return handle;
    }

    public static bool TryGetExportId(ReadOnlySpan<byte> handle, out ulong exportId)
    {
        exportId = 0;
        if (handle.Length < PrefixSize || handle[0] != Version)
        {
            return false;
        }

        exportId = BinaryPrimitives.ReadUInt64BigEndian(handle[1..]);
        return true;
    }

    public static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> handle) => handle[PrefixSize..];
}

/// <summary>One export: its path for MOUNT, its id in the handles, its file system and the hosts allowed to use it.</summary>
/// <param name="AllowedClients">Addresses allowed to mount and use the export; null allows any (tests only).</param>
public sealed record NfsExport(string Path, ulong Id, INfsFileSystem FileSystem, IReadOnlySet<IPAddress>? AllowedClients)
{
    public bool Allows(IPAddress address) =>
        AllowedClients is null || AllowedClients.Contains(address) || (address.IsIPv4MappedToIPv6 && AllowedClients.Contains(address.MapToIPv4()));
}

/// <summary>The exports a server offers now (thread-safe; exports come and go with instant recovery sessions).</summary>
public sealed class NfsExportTable
{
    private readonly ConcurrentDictionary<ulong, NfsExport> _byId = new();

    /// <summary>Called when a host is refused (not in the allowlist), for diagnostics.</summary>
    public Action<IPAddress, string>? Refused { get; set; }

    public IReadOnlyCollection<NfsExport> All => [.. _byId.Values.OrderBy(e => e.Path, StringComparer.Ordinal)];

    public void Add(NfsExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        if (!_byId.TryAdd(export.Id, export))
        {
            throw new InvalidOperationException($"An export with id {export.Id:x16} exists already.");
        }
    }

    public bool Remove(ulong exportId) => _byId.TryRemove(exportId, out _);

    public NfsExport? FindByPath(string path) => _byId.Values.FirstOrDefault(e => string.Equals(e.Path, Normalize(path), StringComparison.Ordinal));

    public NfsExport? FindByHandle(ReadOnlySpan<byte> handle) => NfsHandles.TryGetExportId(handle, out var id) ? _byId.GetValueOrDefault(id) : null;

    private static string Normalize(string path) => path.Length > 1 ? path.TrimEnd('/') : path;
}
