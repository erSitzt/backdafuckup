using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.Operations;

namespace Backdafuckup.Arrays.Alletra;

/// <summary>
/// A signed-in session to an HPE Alletra MP. Snapshots are named <c>bdfu-…</c> and commented with this server's id; a
/// snapshot or presentation is changed or deleted only when every proof says it is ours.
/// </summary>
internal sealed class AlletraSession(AlletraClient client, ArrayConnectionSettings settings, ArrayInfo info) : IStorageArraySession
{
    /// <summary>The prefix of our snapshot names (kept short: the array allows 31 characters until a spike shows more).</summary>
    public const string NamePrefix = "bdfu-";

    private const int MaxNameLength = 31;

    public ArrayInfo Info { get; } = info;

    private string ServerTag => $"server={settings.ServerInstanceId}";

    public async Task<IReadOnlyList<ArrayVolume>> FindVolumesByWwnAsync(IReadOnlyCollection<string> wwns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wwns);
        var wanted = wwns.Select(Wwns.Normalize).ToHashSet(StringComparer.Ordinal);
        return [.. (await VolumesAsync(cancellationToken).ConfigureAwait(false)).Where(v => wanted.Contains(Wwns.Normalize(v.Wwn)))];
    }

    public async Task<IReadOnlyList<ArrayHost>> ListHostsAsync(CancellationToken cancellationToken)
    {
        var hosts = await client.ListAsync("api/v3/hosts", cancellationToken).ConfigureAwait(false);
        var paths = await client.ListAsync("api/v3/hostpaths", cancellationToken).ConfigureAwait(false);
        return
        [
            .. hosts.Select(h =>
            {
                var name = h["name"]?.GetValue<string>() ?? string.Empty;
                var iqns = paths.Where(p => p["hostName"]?.GetValue<string>() == name).Select(p => p["address"]?.GetValue<string>())
                    .OfType<string>().Where(a => a.StartsWith("iqn.", StringComparison.OrdinalIgnoreCase) || a.StartsWith("eui.", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList(); // one host path per array port
                return new ArrayHost(h["uid"]?.GetValue<string>() ?? string.Empty, name, iqns, h["persona"]?["name"]?.GetValue<string>(), h["domain"]?.GetValue<string>());
            }),
        ];
    }

    /// <summary>
    /// The host persona of a backup proxy: Generic-ALUA (2), what HPE names for Linux hosts, also with Peer Persistence.
    /// The B10000 lists it as <c>{"id": 2, "name": "Generic-ALUA"}</c>.
    /// </summary>
    internal const int ProxyPersona = 2;

    public IReadOnlyList<ArrayChange> DescribeHostCreation(ArrayHostRequest request)
    {
        Check(request);
        var domain = Domain(request.Domain);
        return
        [
            new ArrayChange(
                $"Create host object {request.Name} for the iSCSI initiator {request.IscsiName}: persona Generic-ALUA (Linux), " +
                $"{(domain is null ? "in no domain" : $"in domain {domain}")}",
                "POST", "/api/v3/hosts", HostBody(request).ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                $"createhost -iscsi -persona {ProxyPersona}{(domain is null ? string.Empty : $" -domain {domain}")} -os Linux -comment \"{request.Comment}\" {request.Name} {request.IscsiName}",
                $"removehost {request.Name}"),
        ];
    }

    public async Task<ArrayHost> CreateHostAsync(ArrayHostRequest request, CancellationToken cancellationToken)
    {
        Check(request);
        var hosts = await ListHostsAsync(cancellationToken).ConfigureAwait(false);
        if (hosts.FirstOrDefault(h => h.IscsiNames.Contains(request.IscsiName, StringComparer.OrdinalIgnoreCase)) is { } listing)
        {
            throw new AlletraException($"Array {client.Endpoint}: host object {listing.Name} lists the iSCSI initiator {request.IscsiName} already; nothing was created.");
        }

        if (hosts.Any(h => string.Equals(h.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AlletraException($"Array {client.Endpoint}: a host object named {request.Name} exists already (without {request.IscsiName}); choose another name. Nothing was changed.");
        }

        try
        {
            await client.ChangeAsync(HttpMethod.Post, "api/v3/hosts", HostBody(request), cancellationToken).ConfigureAwait(false);
        }
        catch (AlletraException ex) when (ex.PermissionDenied)
        {
            throw Refused(ex, $"creating host object {request.Name}", request.Domain);
        }

        return (await ListHostsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(h => string.Equals(h.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new AlletraException($"Array {client.Endpoint}: host object {request.Name} was created, but the array does not list it.");
    }

    /// <summary>The body of <c>POST /api/v3/hosts</c> (host.CreateHost), shown before it is sent.</summary>
    private static JsonObject HostBody(ArrayHostRequest request)
    {
        var body = new JsonObject { ["name"] = request.Name, ["iscsiPaths"] = new JsonArray(request.IscsiName), ["persona"] = ProxyPersona };
        if (Domain(request.Domain) is { } domain)
        {
            body["domain"] = domain;
        }

        body["descriptors"] = new JsonObject { ["os"] = "Linux", ["comment"] = request.Comment };
        return body;
    }

    /// <summary>Names as the array takes them (31 characters), an initiator name, and a comment that fits between double quotes.</summary>
    private static void Check(ArrayHostRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AlletraNames.IsObjectName(request.Name))
        {
            throw new ArgumentException($"'{request.Name}' is no host name the array takes: 1 to 31 letters, digits, '.', '-' or '_', starting with a letter or digit.", nameof(request));
        }

        if (Domain(request.Domain) is { } domain && !AlletraNames.IsObjectName(domain))
        {
            throw new ArgumentException($"'{domain}' is no domain name the array takes.", nameof(request));
        }

        if (!AlletraNames.IsIscsiName(request.IscsiName))
        {
            throw new ArgumentException($"'{request.IscsiName}' is no iSCSI initiator name (iqn.yyyy-mm.… or eui.<16 hex digits>).", nameof(request));
        }

        if (request.Comment.IndexOfAny(['"', '\\', '\n', '\r']) >= 0 || request.Comment.Length > 255)
        {
            throw new ArgumentException("The host comment has a quote, a backslash or a line break, or is longer than 255 characters.", nameof(request));
        }
    }

    public async Task<IReadOnlyList<ArrayIscsiPortal>> GetIscsiPortalsAsync(CancellationToken cancellationToken) =>
    [
        .. (await client.ListAsync("api/v3/ports", cancellationToken).ConfigureAwait(false))
            .Where(p => p["iscsiData"]?["ipAddress"]?.GetValue<string>() is { Length: > 0 } ip && ip != "0.0.0.0")
            .Select(p => new ArrayIscsiPortal(p["iscsiData"]!["ipAddress"]!.GetValue<string>(), p["iscsiData"]?["iscsiName"]?.GetValue<string>(),
                p["name"]?.GetValue<string>() ?? p["uid"]?.GetValue<string>() ?? string.Empty)),
    ];

    public async Task<IReadOnlyList<ArraySnapshot>> CreateSnapshotsAsync(ArraySnapshotRequest request, IArtifactLedger ledger, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(log);
        var snapshots = new List<ArraySnapshot>();
        for (var i = 0; i < request.Volumes.Count; i++)
        {
            var source = request.Volumes[i];
            var name = SnapshotName(request.NameBase, i);
            var expiry = Math.Max(3600, (long)request.Expiry.TotalSeconds);
            var comment = $"backdafuckup {ServerTag} source={source.Uid} created={DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)} {request.Comment}".Trim();
            var artifact = await ledger.RecordAsync(ArrayArtifactKinds.Snapshot, string.Empty, name,
                JsonSerializer.Serialize(new { name, sourceUid = source.Uid, sourceId = source.Id, sourceName = source.Name }), cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            string? uid;
            try
            {
                uid = await client.ChangeAsync(HttpMethod.Post, $"api/v3/volumes/{Uri.EscapeDataString(source.Uid)}", new
                {
                    action = "CREATE_SNAPSHOT_VOLUME",
                    parameters = new { customName = name, namePattern = "CUSTOM", readOnly = request.ReadOnly, expireSecs = expiry, comment },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlletraException or OperationCanceledException)
            {
                // Whatever the array made of it: a snapshot with our name is recorded for the cleanup, else the row is closed.
                var left = (await VolumesAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(v => v.Name == name && v.IsSnapshot && Tagged(v.Comment));
                await ledger.UpdateAsync(artifact, left is null ? ArtifactState.Done : ArtifactState.CleanupPending, left?.Uid, CancellationToken.None).ConfigureAwait(false);
                if (ex is AlletraException { PermissionDenied: true } denied)
                {
                    throw Refused(denied, $"taking snapshot {name} of {source.Name}", source.Domain);
                }

                throw;
            }

            var volume = (uid is null ? null : await VolumeAsync(uid, cancellationToken).ConfigureAwait(false))
                ?? (await VolumesAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(v => v.Name == name)
                ?? throw new AlletraException($"Array {client.Endpoint}: snapshot {name} of {source.Name} was created, but the array does not show it.");
            var snapshot = new ArraySnapshot(volume.Uid, volume.Id, volume.Name, volume.Wwn, source.Uid, source.Id, source.Name, DateTimeOffset.UtcNow.AddSeconds(expiry));
            await ledger.UpdateAsync(artifact, ArtifactState.Active, volume.Uid, cancellationToken).ConfigureAwait(false);
            log.Info($"Array snapshot {name} of {source.Name} on {Info.Name} ({client.Endpoint}) in {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s " +
                     $"({(request.ReadOnly ? "read-only" : "read-write")}, expires {snapshot.ExpiresUtc:yyyy-MM-dd HH:mm} UTC unless extended).");
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    public async Task ExtendExpiryAsync(ArraySnapshot snapshot, TimeSpan expiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var volume = await ProveOursAsync(snapshot, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.ChangeAsync(HttpMethod.Patch, $"api/v3/volumes/{Uri.EscapeDataString(snapshot.Uid)}",
                new { expireSecs = Math.Max(3600, (long)expiry.TotalSeconds) }, cancellationToken).ConfigureAwait(false);
        }
        catch (AlletraException ex) when (ex.PermissionDenied)
        {
            throw Refused(ex, $"extending the expiry of snapshot {snapshot.Name}", volume?.Domain);
        }
    }

    public async Task<ArrayPresentation> PresentAsync(ArraySnapshot snapshot, string hostName, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ledger);
        if (string.IsNullOrWhiteSpace(hostName) || hostName.StartsWith("set:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"A snapshot is presented to exactly one host object, not '{hostName}'.", nameof(hostName));
        }

        var volume = await ProveOursAsync(snapshot, cancellationToken).ConfigureAwait(false)
            ?? throw new AlletraException($"Array {client.Endpoint}: snapshot {snapshot.Name} is gone; it cannot be presented to {hostName}.");
        var host = (await client.ListAsync("api/v3/hosts", cancellationToken).ConfigureAwait(false)).FirstOrDefault(h => h["name"]?.GetValue<string>() == hostName)
            ?? throw new AlletraException($"Array {client.Endpoint}: there is no host object {hostName} (create it with the proxy's initiator name, or map the proxy to an existing one).");
        var domains = await DomainsAsync(volume, hostName, host["domain"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
        var artifact = await ledger.RecordAsync(ArrayArtifactKinds.Presentation, string.Empty, snapshot.Name,
            JsonSerializer.Serialize(new { volumeName = snapshot.Name, hostName, snapshotUid = snapshot.Uid }), cancellationToken).ConfigureAwait(false);
        string? uid;
        try
        {
            uid = await client.ChangeAsync(HttpMethod.Post, "api/v3/vluns", new { volumeName = snapshot.Name, hostName, autoLun = true }, cancellationToken).ConfigureAwait(false);
        }
        catch (AlletraException ex)
        {
            // The ledger row stays open: the array may have made the VLUN all the same, and the cleanup looks for it.
            if (ex.PermissionDenied)
            {
                throw Refused(ex, $"presenting snapshot {snapshot.Name} to host object {hostName}", volume.Domain);
            }

            // Where the domains differ in a way the check could not settle, say so with the array's answer.
            throw domains is null ? ex : new AlletraException($"{ex.Message} ({domains})", ex.Status, ex) { ArrayMessage = ex.ArrayMessage };
        }

        var vlun = (uid is null ? null : await client.GetAsync($"api/v3/vluns/{Uri.EscapeDataString(uid)}", cancellationToken).ConfigureAwait(false) as JsonObject)
            ?? (await client.ListAsync("api/v3/vluns", cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(v => v["volumeName"]?.GetValue<string>() == snapshot.Name && v["hostName"]?.GetValue<string>() == hostName)
            ?? throw new AlletraException($"Array {client.Endpoint}: {snapshot.Name} was presented to {hostName}, but the array shows no such VLUN.");
        var presentation = new ArrayPresentation(vlun["uid"]?.GetValue<string>() ?? uid ?? string.Empty, (int)(AlletraClient.Long(vlun["lun"]) ?? -1),
            vlun["volumeWWN"]?.GetValue<string>() ?? snapshot.Wwn, hostName, snapshot.Name);
        await ledger.UpdateAsync(artifact, ArtifactState.Active, presentation.Uid, cancellationToken).ConfigureAwait(false);
        return presentation;
    }

    public async Task UnpresentAsync(ArrayPresentation presentation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (!presentation.VolumeName.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"VLUN {presentation.Uid} presents {presentation.VolumeName}, which is not one of our snapshots; it stays.");
        }

        // One presentation shows as a template (no port) and an active entry per array port (seen on a B10000): all of
        // them go, the template first (which usually takes the others along), then whatever is left.
        var entries = (await client.ListAsync("api/v3/vluns", cancellationToken).ConfigureAwait(false))
            .Where(v => v["volumeName"]?.GetValue<string>() == presentation.VolumeName
                        && (presentation.HostName.Length == 0 || v["hostName"]?.GetValue<string>() == presentation.HostName))
            .OrderBy(v => IsTemplate(v) ? 0 : 1)
            .Select(v => v["uid"]?.GetValue<string>()).OfType<string>().ToList();
        if (!entries.Contains(presentation.Uid, StringComparer.Ordinal) && presentation.Uid.Length > 0)
        {
            entries.Insert(0, presentation.Uid);
        }

        foreach (var uid in entries)
        {
            try
            {
                await client.ChangeAsync(HttpMethod.Delete, $"api/v3/vluns/{Uri.EscapeDataString(uid)}", null, cancellationToken).ConfigureAwait(false);
            }
            catch (AlletraException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
            {
                // Gone with its template.
            }
            catch (AlletraException ex) when (ex.PermissionDenied)
            {
                throw Refused(ex, $"removing the presentation of {presentation.VolumeName} to {presentation.HostName}", domain: null);
            }
        }
    }

    public async Task DeleteSnapshotAsync(ArraySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var volume = await ProveOursAsync(snapshot, cancellationToken).ConfigureAwait(false);
        if (volume is null)
        {
            return;
        }

        var presented = (await client.ListAsync("api/v3/vluns", cancellationToken).ConfigureAwait(false))
            .Where(v => v["volumeName"]?.GetValue<string>() == volume.Name).Select(v => v["hostName"]?.GetValue<string>()).Distinct().ToList();
        if (presented.Count > 0)
        {
            throw new InvalidOperationException($"Snapshot {volume.Name} on {client.Endpoint} is still presented to {string.Join(", ", presented)}; it is deleted once that is removed.");
        }

        try
        {
            await client.ChangeAsync(HttpMethod.Delete, $"api/v3/volumes/{Uri.EscapeDataString(volume.Uid)}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (AlletraException ex) when (ex.PermissionDenied)
        {
            throw Refused(ex, $"deleting snapshot {volume.Name}", volume.Domain);
        }
    }

    public async Task<IReadOnlyList<ProviderArtifact>> FindTaggedAsync(CancellationToken cancellationToken)
    {
        var ours = (await VolumesAsync(cancellationToken).ConfigureAwait(false))
            .Where(v => v.IsSnapshot && v.Name.StartsWith(NamePrefix, StringComparison.Ordinal) && Tagged(v.Comment)).ToList();
        var result = ours.Select(v => new ProviderArtifact(Guid.Empty, ArrayArtifactKinds.Snapshot, v.Uid, v.Name, null, null, ArtifactState.Active,
            JsonSerializer.Serialize(new { name = v.Name, sourceId = v.CopyOfId, created = Created(v.Comment) }))).ToList();
        var created = ours.ToDictionary(v => v.Name, v => Created(v.Comment), StringComparer.Ordinal);
        // One presentation per snapshot and host, by its template where there is one (the port entries go with it).
        var presentations = (await client.ListAsync("api/v3/vluns", cancellationToken).ConfigureAwait(false))
            .GroupBy(v => (Volume: v["volumeName"]?.GetValue<string>(), Host: v["hostName"]?.GetValue<string>()))
            .Select(g => g.OrderBy(v => IsTemplate(v) ? 0 : 1).First());
        foreach (var vlun in presentations)
        {
            if (vlun["volumeName"]?.GetValue<string>() is { } name && created.TryGetValue(name, out var when))
            {
                result.Insert(0, new ProviderArtifact(Guid.Empty, ArrayArtifactKinds.Presentation, vlun["uid"]?.GetValue<string>() ?? string.Empty, name, null, null, ArtifactState.Active,
                    JsonSerializer.Serialize(new { volumeName = name, hostName = vlun["hostName"]?.GetValue<string>(), created = when })));
            }
        }

        return result;
    }

    public async Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(log);
        switch (artifact.Kind)
        {
            case ArrayArtifactKinds.Presentation when artifact.NativeId is { Length: > 0 } uid:
                var vlun = await client.GetAsync($"api/v3/vluns/{Uri.EscapeDataString(uid)}", cancellationToken).ConfigureAwait(false);
                if (vlun?["volumeName"]?.GetValue<string>() is { } volumeName)
                {
                    await UnpresentAsync(new ArrayPresentation(uid, -1, string.Empty, vlun["hostName"]?.GetValue<string>() ?? string.Empty, volumeName), cancellationToken).ConfigureAwait(false);
                    log.Info($"Removed the presentation of {volumeName} to {vlun["hostName"]} on {client.Endpoint} that an interrupted session left.");
                }

                break;
            case ArrayArtifactKinds.Snapshot when artifact.NativeId is { Length: > 0 } uid:
                var volume = await VolumeAsync(uid, cancellationToken).ConfigureAwait(false);
                if (volume is null)
                {
                    break;
                }

                var recorded = artifact.Data is { } data ? JsonNode.Parse(data) : null;
                var sourceId = AlletraClient.Long(recorded?["sourceId"]) ?? -1;
                await DeleteSnapshotAsync(new ArraySnapshot(volume.Uid, volume.Id, volume.Name, volume.Wwn, string.Empty, sourceId, string.Empty, DateTimeOffset.MinValue), cancellationToken)
                    .ConfigureAwait(false);
                log.Info($"Deleted the array snapshot {volume.Name} on {client.Endpoint} that an interrupted session left.");
                break;
        }
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();

    /// <summary>
    /// A refusal for lack of rights, said with what was being done, the right it needs and the rights the account has.
    /// Creating, extending, presenting and deleting our snapshots needs the role edit in the volume's domain.
    /// </summary>
    private AlletraException Refused(AlletraException denied, string what, string? domain) => new(
        $"Array {client.Endpoint}: {what} was refused for lack of rights: the account {client.User} needs the role edit " +
        $"{(Domain(domain) is { } d ? $"in domain {d} (or in all domains)" : "in the snapshot's domain")}, and it has {client.PrivilegeSummary}. The array said {denied.ArrayMessage}.",
        denied.Status, denied)
    {
        PermissionDenied = true,
        ArrayMessage = denied.ArrayMessage,
    };

    /// <summary>
    /// Whether the snapshot can be presented to the host object as far as their domains go. A volume in a domain is
    /// presented only to a host in the same domain, or in a domain set that holds it: such a difference is refused here,
    /// before anything is made, naming both. Where only one of them is in a domain the rule is less clear, so the
    /// presentation is tried and this text (returned) is added to an error. Null when the domains match.
    /// </summary>
    private async Task<string?> DomainsAsync(ArrayVolume snapshot, string hostName, string? hostDomain, CancellationToken cancellationToken)
    {
        var (volumeDomain, ofHost) = (Domain(snapshot.Domain), Domain(hostDomain));
        if (volumeDomain == ofHost)
        {
            return null;
        }

        var said = $"snapshot {snapshot.Name} is {(volumeDomain is null ? "in no domain" : $"in domain {volumeDomain}")}, host object {hostName} " +
                   $"{(ofHost is null ? "in no domain" : $"in domain {ofHost}")}";
        if (volumeDomain is null || ofHost is null)
        {
            return said;
        }

        IReadOnlyList<JsonObject> sets;
        try
        {
            sets = await client.ListAsync("api/v3/domainsets", cancellationToken).ConfigureAwait(false);
        }
        catch (AlletraException)
        {
            return said;
        }

        if (sets.Any(s => s["name"]?.GetValue<string>() == ofHost && s["members"] is JsonArray members && members.Any(m => m?.GetValue<string>() == volumeDomain)))
        {
            return null;
        }

        throw new AlletraException(
            $"Array {client.Endpoint}: {said}, and the array presents a volume only to a host in the same domain: put host object {hostName} into domain {volumeDomain} " +
            $"(or a domain set with it), or map the proxy to a host object in {volumeDomain}. Nothing was presented.");
    }

    /// <summary>A presentation's template: the entry without an array port (the active entries per port hang off it).</summary>
    private static bool IsTemplate(JsonObject vlun) => vlun["portPos"] is null || vlun["portPos"] is JsonValue;

    /// <summary>A domain name, or null for none ("-" and empty mean none).</summary>
    private static string? Domain(string? domain) => domain is { Length: > 0 } and not "-" ? domain : null;

    /// <summary>
    /// The snapshot as the array shows it, if every proof holds: our name prefix, our server id in its comment, a
    /// snapshot, a copy of the recorded base. Null if it is gone already; throws if anything does not match.
    /// </summary>
    private async Task<ArrayVolume?> ProveOursAsync(ArraySnapshot snapshot, CancellationToken cancellationToken)
    {
        var volume = await VolumeAsync(snapshot.Uid, cancellationToken).ConfigureAwait(false);
        if (volume is null)
        {
            return null;
        }

        var problem = !volume.Name.StartsWith(NamePrefix, StringComparison.Ordinal) ? $"its name {volume.Name} is not one of ours ({NamePrefix}…)"
            : !Tagged(volume.Comment) ? $"its comment does not name this server ({ServerTag})"
            : !volume.IsSnapshot ? $"it is no snapshot ({volume.Type})"
            : volume.CopyOfId != snapshot.SourceId ? $"it is a copy of volume {volume.CopyOfId?.ToString(CultureInfo.InvariantCulture) ?? "none"}, not of {snapshot.SourceId.ToString(CultureInfo.InvariantCulture)}"
            : null;
        return problem is null
            ? volume
            : throw new InvalidOperationException($"Volume {snapshot.Uid} on {client.Endpoint} is left alone: {problem}.");
    }

    /// <summary>The "created=" of our comment (UTC, as written at the snapshot), or null.</summary>
    private static string? Created(string? comment) =>
        comment?.Split(' ').FirstOrDefault(p => p.StartsWith("created=", StringComparison.Ordinal))?["created=".Length..];

    private bool Tagged(string? comment) => comment is not null && comment.Split(' ').Contains(ServerTag, StringComparer.Ordinal);

    private static string SnapshotName(string nameBase, int index)
    {
        var suffix = "-" + (index + 1).ToString("00", CultureInfo.InvariantCulture);
        var head = nameBase.StartsWith(NamePrefix, StringComparison.Ordinal) ? nameBase : NamePrefix + nameBase;
        return (head.Length + suffix.Length > MaxNameLength ? head[..(MaxNameLength - suffix.Length)] : head) + suffix;
    }

    private async Task<IReadOnlyList<ArrayVolume>> VolumesAsync(CancellationToken cancellationToken) =>
        [.. (await client.ListAsync("api/v3/volumes", cancellationToken).ConfigureAwait(false)).Select(Volume)];

    private async Task<ArrayVolume?> VolumeAsync(string uid, CancellationToken cancellationToken) =>
        await client.GetAsync($"api/v3/volumes/{Uri.EscapeDataString(uid)}", cancellationToken).ConfigureAwait(false) is JsonObject volume ? Volume(volume) : null;

    internal static ArrayVolume Volume(JsonObject v) => new(
        v["uid"]?.GetValue<string>() ?? string.Empty,
        AlletraClient.Long(v["id"]) ?? -1,
        v["name"]?.GetValue<string>() ?? string.Empty,
        v["wwn"]?.GetValue<string>() ?? string.Empty,
        (long)((AlletraClient.Double(v["sizeMiB"]) ?? 0) * 1024 * 1024),
        (AlletraClient.Text(v["type"]) ?? string.Empty) switch
        {
            "VVTYPE_SNAPSHOT" => ArrayVolumeTypes.Snapshot,
            "VVTYPE_CLONE" => ArrayVolumeTypes.Clone,
            "VVTYPE_BASE" => ArrayVolumeTypes.Base,
            var other => other,
        },
        AlletraClient.Long(v["copyOfID"]),
        v["comment"]?.GetValue<string>(),
        AlletraClient.Bool(v["readOnly"]) ?? false,
        v["domain"]?.GetValue<string>(),
        // rcopyStatus: "primary", "secondary" or "none" (seen on a B10000, OS 10.5).
        AlletraClient.Text(v["rcopyStatus"])?.ToLowerInvariant() switch
        {
            "primary" => ArrayReplicationRoles.Primary,
            "secondary" => ArrayReplicationRoles.Secondary,
            _ => null,
        });
}
