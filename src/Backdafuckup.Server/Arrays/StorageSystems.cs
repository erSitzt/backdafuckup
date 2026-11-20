using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Proxies;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Arrays;

/// <summary>A request about storage systems the console or API got wrong; the message is shown as it is.</summary>
public sealed class StorageSystemRequestException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The storage arrays the server takes snapshots on: adding and testing them, which volume holds which datastore or SR
/// (by WWN, refreshed with the inventory), the objects this server left on them, and opening a session for the engine and
/// the cleanup. Every object the server creates on an array carries this server's stable id.
/// </summary>
public sealed class StorageSystems(
    IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, IEnumerable<IStorageArrayProvider> providers, ConnectionManager connections,
    TimeProvider time, ILogger<StorageSystems> logger, IProxyPool proxies)
{
    private const string ServerIdKey = "arrays.serverId";

    /// <summary>Our objects on an array that no open ledger row names are removed once they are this old.</summary>
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(2);

    private readonly SemaphoreSlim _serverIdGate = new(1, 1);
    private string? _serverId;

    public IStorageArrayProvider Provider(string kind) =>
        providers.FirstOrDefault(p => string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase))
        ?? throw new StorageSystemRequestException($"Unknown storage system kind '{kind}' ({string.Join(", ", providers.Select(p => p.Kind))}).");

    /// <summary>This server's id on the arrays: generated once, kept in the catalog, so it survives restarts and updates.</summary>
    public async Task<string> ServerIdAsync(CancellationToken cancellationToken)
    {
        if (_serverId is { } known)
        {
            return known;
        }

        await _serverIdGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var stored = await db.Settings.FirstOrDefaultAsync(s => s.Key == ServerIdKey, cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                stored = new AppSetting { Key = ServerIdKey, Value = Guid.NewGuid().ToString("N") };
                db.Settings.Add(stored);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return _serverId = stored.Value;
        }
        finally
        {
            _serverIdGate.Release();
        }
    }

    public async Task<IReadOnlyList<StorageSystemDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var systems = await db.StorageSystems.AsNoTracking().Include(s => s.Credential).OrderBy(s => s.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var volumes = await db.StorageVolumes.AsNoTracking().GroupBy(v => v.StorageSystemId)
            .Select(g => new { Id = g.Key, Count = g.Count(), Enabled = g.Count(v => v.Enabled) }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. systems.Select(s => ToDto(s, volumes.FirstOrDefault(v => v.Id == s.Id)?.Count ?? 0, volumes.FirstOrDefault(v => v.Id == s.Id)?.Enabled ?? 0))];
    }

    public async Task<StorageSystemDto> CreateAsync(CreateStorageSystemDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provider = Provider(request.Kind);
        var name = request.Name.Trim();
        if (name.Length is 0 or > 200 || string.IsNullOrWhiteSpace(request.Address) || string.IsNullOrWhiteSpace(request.UserName))
        {
            throw new StorageSystemRequestException("Name, address and user name are needed.");
        }

        var pins = request.TrustedCertificates.Select(c => c.SpkiSha256.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var trust = pins.Count > 0 ? TlsTrustMode.Pinned : TlsTrustMode.SystemCa;
        var settings = new ArrayConnectionSettings(request.Address.Trim(), request.Port, request.UserName.Trim(), request.Password, trust, pins, await ServerIdAsync(cancellationToken).ConfigureAwait(false));
        ArrayInfo info;
        try
        {
            info = await provider.TestConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
        {
            throw new StorageSystemRequestException($"Storage system {settings.Address}:{settings.Port}: {ex.Message}");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.StorageSystems.AnyAsync(s => s.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageSystemRequestException($"A storage system named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        var now = time.GetUtcNow();
        var credential = new Credential
        {
            Kind = CredentialKind.StorageArray, Username = settings.UserName, ProtectedSecret = protector.Protect(request.Password), Description = $"Storage system {name}",
            CreatedUtc = now, UpdatedUtc = now,
        };
        var system = new StorageSystemEntity
        {
            Kind = provider.Kind, Name = name, Address = settings.Address, Port = settings.Port, Credential = credential, TrustMode = trust, PinnedSpki = pins,
            Facts = info, Status = ConnectionStatus.Connected, LastCheckedUtc = now, CreatedUtc = now,
        };
        db.StorageSystems.Add(system);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Storage system {Name} ({Address}) added: {Model}, serial {Serial}, {Os}.", name, settings.Address, info.Model, info.Serial, info.OsVersion);

        try
        {
            await RefreshMappingAsync(system.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The volumes of storage system {Name} could not be mapped yet: {Error}", name, ex.Message);
        }

        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Single(s => s.Id == system.Id);
    }

    /// <summary>Signs in again and refreshes the facts (model, OS, licences, privileges).</summary>
    /// <summary>
    /// Changes name, account or address. Signs in with the new settings first and changes nothing if that fails or if
    /// they lead to another array (another serial number). Turned-on volumes, options and the ledger stay.
    /// </summary>
    public async Task<StorageSystemDto> UpdateAsync(Guid id, UpdateStorageSystemDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name.Trim();
        if (name.Length is 0 or > 200 || string.IsNullOrWhiteSpace(request.Address) || string.IsNullOrWhiteSpace(request.UserName) || request.Port is < 1 or > 65535)
        {
            throw new StorageSystemRequestException("Name, address, port and user name are needed.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var system = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        var address = request.Address.Trim();
        var moved = !string.Equals(address, system.Address, StringComparison.OrdinalIgnoreCase) || request.Port != system.Port;
        var pins = request.TrustedCertificates.Select(c => c.SpkiSha256.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (pins.Count == 0 && moved && system.TrustMode == TlsTrustMode.Pinned)
        {
            throw new StorageSystemRequestException($"The new address {address}:{request.Port} needs its certificate confirmed: the pinned key is that of {system.Address}:{system.Port}.");
        }

        var (trust, keys) = pins.Count > 0 ? (TlsTrustMode.Pinned, pins) : (system.TrustMode, system.PinnedSpki ?? []);
        var newPassword = !string.IsNullOrEmpty(request.Password);
        var password = newPassword ? request.Password! : protector.Unprotect(system.Credential.ProtectedSecret);
        var settings = new ArrayConnectionSettings(address, request.Port, request.UserName.Trim(), password, trust, keys, await ServerIdAsync(cancellationToken).ConfigureAwait(false));
        ArrayInfo info;
        try
        {
            info = await Provider(system.Kind).TestConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
        {
            throw new StorageSystemRequestException($"Nothing was changed: storage system {address}:{request.Port} with user {settings.UserName}: {ex.Message}");
        }

        if (system.Facts is { Serial.Length: > 0 } known && !string.Equals(known.Serial, info.Serial, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageSystemRequestException(
                $"Nothing was changed: {address}:{request.Port} is array {info.Name} (serial {info.Serial}), not {known.Name} (serial {known.Serial}). " +
                "Another array is a storage system of its own: add it.", StatusCodes.Status409Conflict);
        }

        if (await db.StorageSystems.AnyAsync(s => s.Id != id && s.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageSystemRequestException($"A storage system named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        List<string> changed = [];
        void Note(bool differs, string what)
        {
            if (differs)
            {
                changed.Add(what);
            }
        }

        Note(name != system.Name, $"name ({system.Name} → {name})");
        Note(moved, $"address ({system.Address}:{system.Port} → {address}:{request.Port})");
        Note(settings.UserName != system.Credential.Username, $"user ({system.Credential.Username} → {settings.UserName})");
        Note(newPassword, "password");
        Note(pins.Count > 0 && !pins.SequenceEqual(system.PinnedSpki ?? []), "trusted certificate");

        var now = time.GetUtcNow();
        system.Name = name;
        system.Address = address;
        system.Port = request.Port;
        system.TrustMode = trust;
        system.PinnedSpki = [.. keys];
        system.Credential.Username = settings.UserName;
        system.Credential.Description = $"Storage system {name}";
        if (newPassword)
        {
            system.Credential.ProtectedSecret = protector.Protect(password);
        }

        system.Credential.UpdatedUtc = now;
        system.Facts = info;
        system.Status = ConnectionStatus.Connected;
        system.LastError = null;
        system.LastCheckedUtc = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Storage system {Name} ({Address}) changed: {Changes}.", name, address, changed.Count == 0 ? "nothing" : string.Join(", ", changed));
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Single(s => s.Id == id);
    }

    public async Task<StorageSystemDto> TestAsync(Guid id, CancellationToken cancellationToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var system = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
            try
            {
                system.Facts = await Provider(system.Kind).TestConnectionAsync(await SettingsAsync(system, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                system.Status = ConnectionStatus.Connected;
                system.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
            {
                system.Status = ConnectionStatus.Error;
                system.LastError = ex.Message;
            }

            system.LastCheckedUtc = time.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Single(s => s.Id == id);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var system = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        var open = await db.Artifacts.CountAsync(a => a.StorageSystemId == id && a.State != ArtifactState.Done, cancellationToken).ConfigureAwait(false);
        if (open > 0)
        {
            throw new StorageSystemRequestException(
                $"Storage system {system.Name} still has {open} snapshot(s) or presentation(s) of this server to clean up; it can be removed once they are gone.", StatusCodes.Status409Conflict);
        }

        db.StorageSystems.Remove(system);
        db.Credentials.Remove(system.Credential);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StorageVolumeDto>> VolumesAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        var names = await db.Connections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.DisplayName, cancellationToken).ConfigureAwait(false);
        return
        [
            .. (await db.StorageVolumes.AsNoTracking().Where(v => v.StorageSystemId == id).OrderBy(v => v.VolumeName).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(v => new StorageVolumeDto(v.Id, v.VolumeUid, v.VolumeName, v.Wwn, v.SizeBytes, v.ConnectionId, v.ConnectionId is { } c ? names.GetValueOrDefault(c) : null,
                    v.StorageNativeId, v.StorageName, v.Layout.ToString(), v.Enabled, v.Refusal, v.UpdatedUtc, v.ReplicationRole)),
        ];
    }

    /// <summary>Turns backups from snapshots of one volume on or off (only for a volume without a refusal).</summary>
    public async Task SetVolumeAsync(Guid id, Guid volumeId, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var volume = await db.StorageVolumes.FirstOrDefaultAsync(v => v.Id == volumeId && v.StorageSystemId == id, cancellationToken).ConfigureAwait(false)
            ?? throw new StorageSystemRequestException($"Storage system {id} has no volume {volumeId}.", StatusCodes.Status404NotFound);
        if (enabled && volume.Refusal is { } refusal)
        {
            throw new StorageSystemRequestException($"Volume {volume.VolumeName} cannot be used: {refusal}");
        }

        volume.Enabled = enabled;
        volume.UpdatedUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How the server uses the array's snapshots: expiry, batches, experimental readers, verify runs (each within its range).</summary>
    public async Task<StorageSystemDto> SetOptionsAsync(Guid id, StorageSystemOptionsDto options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Check(options.ExpiryHours, 1, 72, "The expiry");
        Check(options.MaxVmsPerSnapshot, 1, 32, "VMs per snapshot");
        Check(options.BatchWindowSeconds, 0, 600, "The batch window");
        Check(options.ProbationRuns, 0, 50, "Verify runs");
        Check(options.MinSecondsAfterVmSnapshot, 0, 60, "The gap after the VM snapshot");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var system = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        system.Options = new StorageSystemOptions
        {
            ExpiryHours = options.ExpiryHours,
            MaxVmsPerSnapshot = options.MaxVmsPerSnapshot,
            BatchWindowSeconds = options.BatchWindowSeconds,
            ExperimentalReaders = options.ExperimentalReaders,
            ProbationRuns = options.ProbationRuns,
            MinSecondsAfterVmSnapshot = options.MinSecondsAfterVmSnapshot,
        };
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var volumes = await db.StorageVolumes.Where(v => v.StorageSystemId == id).Select(v => v.Enabled).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(system, volumes.Count, volumes.Count(e => e));

        static void Check(int value, int min, int max, string what)
        {
            if (value < min || value > max)
            {
                throw new StorageSystemRequestException($"{what} must be between {min} and {max} (is {value}).");
            }
        }
    }

    /// <summary>
    /// Which volume holds which datastore or SR: the LUN WWNs every pool and vCenter reports for its storages, matched with
    /// the array's volumes. A volume keeps whether it is turned on; volumes no storage lies on any more are dropped.
    /// </summary>
    public async Task<IReadOnlyList<StorageVolumeDto>> RefreshMappingAsync(Guid id, CancellationToken cancellationToken)
    {
        List<(Guid ConnectionId, StorageBacking Backing, string Name)> backings = [];
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
            var targets = await db.Connections.AsNoTracking().Where(c => c.Kind == HypervisorKind.XenServer || c.Kind == HypervisorKind.VSphere)
                .Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var connectionId in targets)
            {
                var storages = await db.Inventory.AsNoTracking().Where(i => i.ConnectionId == connectionId && i.Kind == InventoryKind.Storage)
                    .ToDictionaryAsync(i => i.NativeId, i => i.Name, cancellationToken).ConfigureAwait(false);
                if (storages.Count == 0)
                {
                    continue;
                }

                try
                {
                    await using var hypervisor = await connections.OpenAsync(connectionId, cancellationToken).ConfigureAwait(false);
                    if (hypervisor is IStorageBackingSource source)
                    {
                        backings.AddRange((await source.GetStorageBackingAsync(storages.Keys, cancellationToken).ConfigureAwait(false))
                            .Select(b => (connectionId, b, storages.GetValueOrDefault(b.StorageId, b.StorageId))));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Mapping storage system {Id}: connection {Connection} could not be read ({Error}); its storages keep their mapping.", id, connectionId, ex.Message);
                    return await VolumesAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var wwns = backings.SelectMany(b => b.Backing.LunWwns).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyList<ArrayVolume> volumes;
        await using (var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false))
        {
            volumes = wwns.Count == 0 ? [] : await array.FindVolumesByWwnAsync(wwns, cancellationToken).ConfigureAwait(false);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = await db.StorageVolumes.Where(v => v.StorageSystemId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var volume in volumes.Where(v => !v.IsSnapshot))
            {
                var wwn = Wwns.Normalize(volume.Wwn);
                var (connectionId, backing, storageName) = backings.FirstOrDefault(b => b.Backing.LunWwns.Contains(wwn, StringComparer.Ordinal));
                if (backing is null)
                {
                    continue;
                }

                seen.Add(volume.Uid);
                var row = existing.FirstOrDefault(v => v.VolumeUid == volume.Uid);
                if (row is null)
                {
                    row = new StorageVolumeMapping { StorageSystemId = id, VolumeUid = volume.Uid, VolumeName = volume.Name, Wwn = wwn };
                    db.StorageVolumes.Add(row);
                }

                row.VolumeName = volume.Name;
                row.Wwn = wwn;
                row.SizeBytes = volume.SizeBytes;
                row.ConnectionId = connectionId;
                row.StorageNativeId = backing.StorageId;
                row.StorageName = storageName;
                row.Layout = backing.Layout;
                row.Refusal = backing.Refusal ?? (backing.Layout == StorageLayout.Other ? $"{storageName} has no layout that can be read from array snapshots." : null);
                row.Enabled &= row.Refusal is null;
                row.ReplicationRole = volume.ReplicationRole;
                row.UpdatedUtc = now;
            }

            db.StorageVolumes.RemoveRange(existing.Where(v => !seen.Contains(v.VolumeUid)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return await VolumesAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>This server's snapshots and presentations on the array, and whether an open ledger row names each.</summary>
    public async Task<IReadOnlyList<StorageLeftoverDto>> LeftoversAsync(Guid id, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderArtifact> tagged;
        await using (var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false))
        {
            tagged = await array.FindTaggedAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var recorded = await db.Artifacts.AsNoTracking().Where(a => a.StorageSystemId == id && a.State != ArtifactState.Done).Select(a => a.NativeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. tagged.Select(t => new StorageLeftoverDto(t.Kind, t.NativeId, t.NativeRef ?? t.NativeId, t.Data, recorded.Contains(t.NativeId)))];
    }

    /// <summary>
    /// Removes this server's objects on every array that no open ledger row names and that are older than
    /// <see cref="OrphanAge"/> (left by a crash before their row was written, or after the database was restored).
    /// Presentations first, then snapshots. The array's expiry stays the last safety net.
    /// </summary>
    public async Task ScanOrphansAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            ids = await db.StorageSystems.AsNoTracking().Select(s => s.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var id in ids)
        {
            try
            {
                var leftovers = (await LeftoversAsync(id, cancellationToken).ConfigureAwait(false)).Where(l => !l.Recorded).ToList();
                if (leftovers.Count == 0)
                {
                    continue;
                }

                await using var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
                var log = new Log(logger);
                foreach (var leftover in leftovers.OrderBy(l => l.Kind == ArrayArtifactKinds.Snapshot ? 1 : 0))
                {
                    if (Created(leftover.Detail) is { } created && time.GetUtcNow() - created < OrphanAge)
                    {
                        continue;
                    }

                    await array.CleanupArtifactAsync(
                        new ProviderArtifact(Guid.Empty, leftover.Kind, leftover.NativeId, leftover.Name, null, null, ArtifactState.CleanupPending, leftover.Detail), log, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The orphan scan of storage system {Id} failed: {Error}", id, ex.Message);
            }
        }
    }

    /// <summary>A session to the array, for the engine and the cleanup.</summary>
    /// <summary>Whether privileges as the array named them ("edit in all domains") allow changes: the role edit or super somewhere.</summary>
    internal static bool MayChange(IEnumerable<string> privileges) => privileges.Any(p => p.Split(' ', 2)[0].ToLowerInvariant() is "edit" or "super");

    /// <summary>
    /// Per proxy, the host object on the array that lists its iSCSI initiator, the array's iSCSI addresses and the domain a
    /// new host object would go into. Reads the array; changes nothing.
    /// </summary>
    public async Task<StorageProxyAccessDto> ProxyAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        var (system, mappings, known) = await LoadForAccessAsync(id, cancellationToken).ConfigureAwait(false);
        var connected = proxies.Connected().ToDictionary(p => p.Id);
        var rows = known.Select(p => (p.Id, p.Name)).Concat(connected.Values.Where(c => known.All(k => k.Id != c.Id)).Select(c => (c.Id, c.Name))).ToList();
        var replicated = mappings.Any(m => m.ReplicationRole is not null);
        var refusal = CreateRefusal(system);
        try
        {
            await using var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
            var hosts = await array.ListHostsAsync(cancellationToken).ConfigureAwait(false);
            var portals = await array.GetIscsiPortalsAsync(cancellationToken).ConfigureAwait(false);
            var (domain, note) = await DomainAsync(array, mappings, cancellationToken).ConfigureAwait(false);

            // Each connected proxy with SAN access tries a TCP connection to the iSCSI addresses (seconds, no login).
            var addresses = portals.Select(p => p.Address).Distinct(StringComparer.Ordinal).ToList();
            var probes = await Task.WhenAll(connected.Values.Where(p => SanProblem(p) is null && addresses.Count > 0)
                .Select(async p => (p.Id, Reach: await proxies.ProbeIscsiAsync(p.Id, addresses, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
            var reach = probes.ToDictionary(p => p.Id, p => p.Reach);
            return new StorageProxyAccessDto(
                [
                    .. rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r =>
                    {
                        var live = connected.GetValueOrDefault(r.Id);
                        var host = live?.InitiatorName is { Length: > 0 } iqn ? hosts.FirstOrDefault(h => h.IscsiNames.Contains(iqn, StringComparer.OrdinalIgnoreCase)) : null;
                        var probe = reach.GetValueOrDefault(r.Id);
                        return new StorageProxyHostDto(r.Id, r.Name, live is not null, live?.InitiatorName, SanProblem(live), host?.Name, host?.Persona, host?.Domain is "-" ? null : host?.Domain,
                            probe?.Reached, probe?.Unreached, probe?.Addresses);
                    }),
                ],
                [.. portals.Select(p => $"{p.Address}:3260").Distinct(StringComparer.Ordinal)], domain, note, refusal, replicated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
        {
            return new StorageProxyAccessDto(
                [.. rows.Select(r => new StorageProxyHostDto(r.Id, r.Name, connected.ContainsKey(r.Id), connected.GetValueOrDefault(r.Id)?.InitiatorName, SanProblem(connected.GetValueOrDefault(r.Id)), null, null, null))],
                [], null, null, refusal, replicated, $"Storage system {system.Name} ({system.Address}:{system.Port}) could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// What creating a host object for a proxy would do: the checks on the array, and the change as API call and CLI line.
    /// Changes nothing.
    /// </summary>
    public async Task<StorageHostPlanDto> PlanHostAsync(Guid id, StorageHostRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (system, mappings, known) = await LoadForAccessAsync(id, cancellationToken).ConfigureAwait(false);
        var live = proxies.Connected().FirstOrDefault(p => p.Id == request.ProxyId);
        var proxyName = live?.Name ?? known.FirstOrDefault(p => p.Id == request.ProxyId).Name
            ?? throw new StorageSystemRequestException($"There is no backup proxy {request.ProxyId}.", StatusCodes.Status404NotFound);
        var name = string.IsNullOrWhiteSpace(request.Name) ? ArrayHostRequest.NameFor(proxyName) : request.Name.Trim();
        var runRefusal = CreateRefusal(system);
        if (live?.InitiatorName is not { Length: > 0 } iqn)
        {
            var why = live is null
                ? $"Proxy {proxyName} is not connected: its iSCSI initiator name is known once it is."
                : $"Proxy {proxyName} has no iSCSI initiator name yet{(SanProblem(live) is { } problem ? $" ({problem.Replace("\n", "; ", StringComparison.Ordinal)})" : string.Empty)}.";
            return new StorageHostPlanDto(proxyName, null, name, request.Domain, [], [], why, false, runRefusal);
        }

        try
        {
            await using var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
            var (proposed, note) = await DomainAsync(array, mappings, cancellationToken).ConfigureAwait(false);
            var domain = request.Domain is null ? proposed : request.Domain.Trim() is { Length: > 0 } chosen and not "-" ? chosen : null;
            var checks = new List<string>();
            string? refusal = null;
            var hosts = await array.ListHostsAsync(cancellationToken).ConfigureAwait(false);
            if (hosts.FirstOrDefault(h => h.IscsiNames.Contains(iqn, StringComparer.OrdinalIgnoreCase)) is { } listing)
            {
                refusal = $"Host object {listing.Name} lists the initiator {iqn} already: nothing to do. Backups present snapshots to {listing.Name}.";
            }
            else
            {
                checks.Add($"No host object on {system.Name} lists the initiator {iqn} yet.");
                if (hosts.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) is { } taken)
                {
                    refusal = $"A host object named {taken.Name} exists already (for {(taken.IscsiNames.Count == 0 ? "no iSCSI initiator" : string.Join(", ", taken.IscsiNames))}): choose another name.";
                }
                else
                {
                    checks.Add($"No host object is named {name}.");
                }
            }

            checks.Add(domain is null ? note ?? "The volumes are in no domain, so the host object goes into none." : $"Domain {domain}: {note ?? "as chosen."}");
            var hostRequest = new ArrayHostRequest(name, iqn, domain, $"backdafuckup proxy {Comment(proxyName)}", ProxyPlatforms.IsWindows(live.Platform));
            IReadOnlyList<ArrayChange> changes;
            try
            {
                changes = array.DescribeHostCreation(hostRequest);
            }
            catch (ArgumentException ex)
            {
                return new StorageHostPlanDto(proxyName, iqn, name, domain, checks, [], ex.Message.Split(" (Parameter", 2)[0], false, runRefusal);
            }

            return new StorageHostPlanDto(proxyName, iqn, name, domain, checks, [.. changes.Select(c => new StorageArrayChangeDto(c.Title, c.Method, c.Path, c.Body, c.Cli, c.UndoCli))],
                refusal, refusal is null && runRefusal is null, runRefusal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
        {
            throw new StorageSystemRequestException($"Storage system {system.Name} ({system.Address}:{system.Port}) could not be read: {ex.Message}", StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Creates the host object for a proxy as <see cref="PlanHostAsync"/> described it (same name and domain), when an
    /// administrator asks for it. The plan is made again first: anything changed on the array since is refused.
    /// </summary>
    public async Task<StorageProxyAccessDto> CreateHostAsync(Guid id, StorageHostRequestDto request, string? requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = await PlanHostAsync(id, request, cancellationToken).ConfigureAwait(false);
        if (!plan.CanRun)
        {
            throw new StorageSystemRequestException(plan.Refusal ?? plan.RunRefusal ?? "The host object cannot be created.", StatusCodes.Status409Conflict);
        }

        var system = (await ListAsync(cancellationToken).ConfigureAwait(false)).Single(s => s.Id == id);
        try
        {
            await using var array = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
            var windows = ProxyPlatforms.IsWindows(proxies.Connected().FirstOrDefault(p => p.Id == request.ProxyId)?.Platform);
            var host = await array.CreateHostAsync(new ArrayHostRequest(plan.Name, plan.InitiatorName!, plan.Domain, $"backdafuckup proxy {Comment(plan.ProxyName)}", windows), cancellationToken)
                .ConfigureAwait(false);
            logger.LogInformation("Created host object {Host} on storage system {System} ({Address}) for proxy {Proxy} ({Initiator}), domain {Domain}, asked for by {User}",
                host.Name, system.Name, system.Address, plan.ProxyName, plan.InitiatorName, plan.Domain ?? "none", requestedBy ?? "(unknown)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageSystemRequestException)
        {
            throw new StorageSystemRequestException(
                $"Host object {plan.Name} was not created on storage system {system.Name} ({system.Address}:{system.Port}): {ex.Message}", StatusCodes.Status502BadGateway);
        }

        return await ProxyAccessAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(StorageSystemDto System, List<StorageVolumeMapping> Mappings, List<(Guid Id, string Name)> Proxies)> LoadForAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        var mappings = await db.StorageVolumes.AsNoTracking().Where(v => v.StorageSystemId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var known = (await db.Proxies.AsNoTracking().Where(p => p.EnrolledUtc != null).Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(p => (p.Id, p.Name)).ToList();
        return (ToDto(entity, mappings.Count, mappings.Count(m => m.Enabled)), mappings, known);
    }

    /// <summary>The domain of the turned-on volumes (else of all mapped ones), with a sentence on how it was chosen.</summary>
    private static async Task<(string? Domain, string? Note)> DomainAsync(IStorageArraySession array, List<StorageVolumeMapping> mappings, CancellationToken cancellationToken)
    {
        var chosen = mappings.Where(m => m.Enabled).ToList() is { Count: > 0 } enabled ? enabled : mappings;
        if (chosen.Count == 0)
        {
            return (null, null);
        }

        var volumes = await array.FindVolumesByWwnAsync([.. chosen.Select(m => m.Wwn)], cancellationToken).ConfigureAwait(false);
        var domains = volumes.Select(v => v.Domain is { Length: > 0 } and not "-" ? v.Domain : null).Distinct().ToList();
        var which = chosen.Any(m => m.Enabled) ? "turned-on" : "mapped";
        return domains switch
        {
            [null] => (null, $"The {which} volumes are in no domain, so the host object goes into none."),
            [{ } one] => (one, $"the domain of the {which} volumes (a snapshot is presented only to a host object in its domain)."),
            [] => (null, null),
            _ => (null, $"The {which} volumes are in several domains ({string.Join(", ", domains.Select(d => d ?? "none"))}): a host object can be in one; choose it, " +
                        "or make one host object per domain."),
        };
    }

    private static string? CreateRefusal(StorageSystemDto system) =>
        system.Facts is not { } facts
            ? $"Storage system {system.Name} was not reached yet: test it first."
            : facts.Privileges.Count == 0 || MayChange(facts.Privileges)
                ? null // no rights named: the array decides
                : $"The account {system.UserName} may not change the array ({string.Join(", ", facts.Privileges)}). " +
                  "Run the CLI line as an array administrator, or give the account the role edit.";

    private static string? SanProblem(ProxyInfo? proxy) =>
        proxy is null ? null
        : proxy.Capabilities.Contains(ProxyAgentProtocol.CapabilitySan, StringComparer.Ordinal) ? null
        : proxy.SanRefusal ?? "no SAN access";

    /// <summary>Text for the host comment: no quotes, backslashes or line breaks.</summary>
    private static string Comment(string value) => new([.. value.Select(c => c is '"' or '\\' or '\n' or '\r' ? ' ' : c)]);

    public async Task<IStorageArraySession> OpenAsync(Guid id, CancellationToken cancellationToken)
    {
        StorageSystemEntity system;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            system = await FindAsync(db, id, cancellationToken).ConfigureAwait(false);
        }

        return await Provider(system.Kind).OpenAsync(await SettingsAsync(system, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArrayConnectionSettings> SettingsAsync(StorageSystemEntity system, CancellationToken cancellationToken) =>
        new(system.Address, system.Port, system.Credential.Username, protector.Unprotect(system.Credential.ProtectedSecret), system.TrustMode, system.PinnedSpki ?? [],
            await ServerIdAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<StorageSystemEntity> FindAsync(AppDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.StorageSystems.Include(s => s.Credential).FirstOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false)
        ?? throw new StorageSystemRequestException($"There is no storage system {id}.", StatusCodes.Status404NotFound);

    /// <summary>When an object was created, from the "created" of its recorded data (our snapshots carry it in their comment).</summary>
    private static DateTimeOffset? Created(string? data)
    {
        try
        {
            return data is not null && JsonNode.Parse(data)?["created"]?.GetValue<string>() is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static StorageSystemDto ToDto(StorageSystemEntity s, int volumes, int enabled) => new(
        s.Id, s.Kind, s.Name, s.Address, s.Port, s.Credential.Username, s.Status.ToString(), s.LastError, s.LastCheckedUtc,
        s.Facts is { } f
            ? new StorageSystemFactsDto(f.Name, f.Model, f.Serial, f.SystemWwn, f.OsVersion, f.ApiVersion, [.. f.Licenses.Select(l => new StorageLicenseDto(l.Name, l.ExpiresUtc))],
                f.Privileges, f.SessionTimeout is { } t ? (int)t.TotalSeconds : null, LicenseWarning(f))
            : null,
        new StorageSystemOptionsDto(
            s.Options.ExpiryHours, s.Options.MaxVmsPerSnapshot, s.Options.BatchWindowSeconds, s.Options.ExperimentalReaders, s.Options.ProbationRuns, s.Options.MinSecondsAfterVmSnapshot),
        s.PinnedSpki ?? [], volumes, enabled,
        [.. ArrayReaderStates.All.Select(r => ArrayReaderStates.ToDto(ArrayReaderStates.Find(s, r), s.Options))]);

    /// <summary>
    /// A warning when no snapshot feature (HPE "Virtual Copy", or "All features …" as the B10000 lists its licence) is
    /// licensed or it has expired; nothing is blocked.
    /// </summary>
    private static string? LicenseWarning(ArrayInfo facts)
    {
        var snapshot = facts.Licenses.FirstOrDefault(l => l.Name.Contains("Virtual Copy", StringComparison.OrdinalIgnoreCase) || l.Name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
                                                          || l.Name.StartsWith("All features", StringComparison.OrdinalIgnoreCase));
        return snapshot is null
            ? $"The array lists no snapshot licence ({(facts.Licenses.Count == 0 ? "no licences reported" : string.Join(", ", facts.Licenses.Select(l => l.Name)))}); snapshots may be refused."
            : snapshot.ExpiresUtc is { } expires && expires < DateTimeOffset.UtcNow ? $"The licence {snapshot.Name} expired on {expires:yyyy-MM-dd}." : null;
    }

    private sealed class Log(ILogger logger) : IOperationLog
    {
        public void Write(LogSeverity severity, string message, string? key = null) =>
            logger.Log(severity switch { LogSeverity.Error => LogLevel.Error, LogSeverity.Warning => LogLevel.Warning, _ => LogLevel.Information }, "{Message}", message);
    }
}
