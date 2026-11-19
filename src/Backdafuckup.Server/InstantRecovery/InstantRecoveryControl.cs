using System.Buffers.Binary;
using System.Collections.Concurrent;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.InstantRecovery;

/// <summary>
/// Asks a running instant recovery to stop or to migrate. The request is its state in the catalog (so it holds across a
/// restart of the server), and the session's runner is woken to act on it at once.
/// </summary>
public sealed class InstantRecoveryControl(
    IDbContextFactory<AppDbContext> dbFactory, SessionQueue queue, NfsServerHost nfs, IOptions<InstantRecoveryOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _wake = new();
    private readonly ConcurrentDictionary<Guid, LiveInstantRecovery> _live = new();

    /// <summary>The instant recoveries this process runs now (for metrics): state, age, overlay, last I/O, migration progress.</summary>
    public IReadOnlyCollection<LiveInstantRecovery> Live => [.. _live.Values];

    /// <summary>
    /// Queues an instant recovery: its session and its catalog row. The datastore (<c>bdfu-ir-&lt;id&gt;</c>), the export
    /// (<c>/bdfu/&lt;id&gt;</c>), its id in the file handles and the overlay directory come from the session's id.
    /// </summary>
    public async Task<Guid> StartAsync(InstantRecoveryRequest request, string triggeredBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Vms.Count == 0)
        {
            throw new ArgumentException("Choose at least one restore point to run.", nameof(request));
        }

        if (request.Vms.Count > Math.Max(1, options.Value.MaxVmsPerSession))
        {
            throw new ArgumentException($"An instant recovery runs at most {options.Value.MaxVmsPerSession} VMs (InstantRecovery:MaxVmsPerSession); {request.Vms.Count} were chosen.", nameof(request));
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var ids = request.Vms.Select(v => v.RestorePointId).ToList();
            var found = await db.RestorePoints.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (ids.Except(found).FirstOrDefault() is { } missing && missing != Guid.Empty)
            {
                throw new KeyNotFoundException($"Restore point {missing} does not exist.");
            }
        }

        var session = new SessionEntity
        {
            Kind = SessionKind.InstantRecovery,
            Name = $"Instant recovery {string.Join(", ", request.Vms.Select(v => v.Name))}",
            TriggeredBy = triggeredBy,
            State = SessionState.Queued,
            CreatedUtc = time.GetUtcNow(),
        };

        // The random end of the session's id (its start is a timestamp, the same for sessions a minute apart).
        var random = session.Id.ToByteArray(bigEndian: true).AsSpan(8);
        var tag = Convert.ToHexStringLower(random[4..]);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = time.GetUtcNow();
        var recovery = new InstantRecoveryEntity
        {
            ConnectionId = request.ConnectionId,
            HostId = request.HostId,
            DatastoreName = $"bdfu-ir-{tag}",
            ExportPath = $"/bdfu/{tag}",
            ExportId = BinaryPrimitives.ReadInt64BigEndian(random) & long.MaxValue,
            OverlayDirectory = Path.Combine(nfs.OverlayDirectory, tag),
            OverlayLimitBytes = request.OverlayLimitBytes,
            FolderId = request.FolderId,
            ResourcePoolId = request.ResourcePoolId,
            ConnectNics = request.ConnectNics,
            KeepDiskIdentity = request.KeepDiskIdentity,
            State = InstantRecoveryState.Starting,
            CreatedUtc = now,
            UpdatedUtc = now,
            Vms = [.. request.Vms.Select(v => new InstantRecoveryVmEntity
            {
                RestorePointId = v.RestorePointId,
                Name = v.Name,
                Directory = UniqueDirectory(v.Name, directories),
                NetworkMap = v.NetworkMap is null ? null : new Dictionary<string, string>(v.NetworkMap, StringComparer.Ordinal),
            })],
        };
        return await queue.EnqueueInstantRecoveryAsync(session, recovery, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A VM's directory in the datastore: its name without characters datastore paths do not allow, unique in the session.</summary>
    private static string UniqueDirectory(string name, HashSet<string> taken)
    {
        var chars = name.Trim().Select(c => c is '/' or '\\' or '[' or ']' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c).ToArray();
        var baseName = chars.Length == 0 ? "vm" : new string(chars);
        baseName = baseName.Length > 60 ? baseName[..60] : baseName;
        var directory = baseName;
        for (var i = 1; !taken.Add(directory); i++)
        {
            directory = $"{baseName}_{i}";
        }

        return directory;
    }

    /// <summary>Running → Stopping. False if the instant recovery is not running (starting, migrating or over).</summary>
    public async Task<bool> RequestStopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var changed = await db.InstantRecoveries.Where(r => r.SessionId == sessionId && r.State == InstantRecoveryState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, InstantRecoveryState.Stopping).SetProperty(r => r.UpdatedUtc, now), cancellationToken).ConfigureAwait(false);
        Wake(sessionId);
        return changed > 0;
    }

    /// <summary>Running → Migrating to <paramref name="target"/>. False if the instant recovery is not running.</summary>
    public async Task<bool> RequestMigrationAsync(Guid sessionId, InstantRecoveryMigration target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var changed = await db.InstantRecoveries.Where(r => r.SessionId == sessionId && r.State == InstantRecoveryState.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, InstantRecoveryState.Migrating).SetProperty(r => r.MigrationTarget, target)
                .SetProperty(r => r.MigrateTaskId, (string?)null).SetProperty(r => r.UpdatedUtc, now), cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            return false;
        }

        Wake(sessionId);
        return true;
    }

    /// <summary>Lets the session's runner look at its state now instead of at its next interval.</summary>
    public void Wake(Guid sessionId)
    {
        var signal = _wake.GetOrAdd(sessionId, _ => new SemaphoreSlim(0, 1));
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Woken already.
        }
    }

    /// <summary>Waits <paramref name="interval"/>, or less when the session is woken.</summary>
    internal async Task WaitAsync(Guid sessionId, TimeSpan interval, CancellationToken cancellationToken)
    {
        var signal = _wake.GetOrAdd(sessionId, _ => new SemaphoreSlim(0, 1));
        await signal.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The session ended: its wake signal and its live state go.</summary>
    internal void Forget(Guid sessionId)
    {
        _live.TryRemove(sessionId, out _);
        if (_wake.TryRemove(sessionId, out var signal))
        {
            signal.Dispose();
        }
    }

    /// <summary>Records (or refreshes) a running instant recovery's state from the catalog.</summary>
    internal LiveInstantRecovery Track(InstantRecoveryEntity recovery)
    {
        var live = _live.GetOrAdd(recovery.SessionId, id => new LiveInstantRecovery(id));
        live.Vms = string.Join(", ", recovery.Vms.Select(v => v.Name));
        live.Host = recovery.HostName ?? recovery.HostId;
        live.CreatedUtc = recovery.CreatedUtc;
        live.OverlayLimitBytes = recovery.OverlayLimitBytes;
        live.State = recovery.State.ToString();
        if (recovery.State != InstantRecoveryState.Migrating)
        {
            live.MigrationProgress = null;
        }

        return live;
    }

    internal LiveInstantRecovery? Find(Guid sessionId) => _live.GetValueOrDefault(sessionId);
}

/// <summary>An instant recovery to start: the vCenter and ESXi host, the restore points to run, where the VMs go in vCenter.</summary>
public sealed record InstantRecoveryRequest(Guid ConnectionId, string HostId, IReadOnlyList<InstantRecoveryVmRequest> Vms)
{
    public string? FolderId { get; init; }

    public string? ResourcePoolId { get; init; }

    public bool ConnectNics { get; init; }

    public bool KeepDiskIdentity { get; init; }

    public long? OverlayLimitBytes { get; init; }
}

/// <summary>A VM of an instant recovery: its restore point, its name in vCenter, its NICs' networks (NIC device key → network).</summary>
public sealed record InstantRecoveryVmRequest(Guid RestorePointId, string Name, IReadOnlyDictionary<string, string>? NetworkMap = null);

/// <summary>What the metrics show of an instant recovery this process runs.</summary>
public sealed class LiveInstantRecovery(Guid sessionId)
{
    public Guid SessionId { get; } = sessionId;

    public string Vms { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public long? OverlayLimitBytes { get; set; }

    public string State { get; set; } = nameof(InstantRecoveryState.Starting);

    /// <summary>The served file system (overlay size, last I/O); null while it is not served.</summary>
    public Backdafuckup.InstantRecovery.InstantRecoveryFileSystem? FileSystem { get; set; }

    /// <summary>0–1 while a Storage vMotion runs.</summary>
    public double? MigrationProgress { get; set; }
}
