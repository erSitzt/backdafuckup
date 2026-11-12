using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>The objects backdafuckup creates in a cluster, as the artifact ledger names them (id: <c>namespace/name</c>, or <c>name</c> when cluster-scoped).</summary>
public static class KubernetesArtifactKinds
{
    public const string VolumeSnapshot = "k8s.VolumeSnapshot";
    public const string VolumeSnapshotContent = "k8s.VolumeSnapshotContent";
    public const string PersistentVolumeClaim = "k8s.PersistentVolumeClaim";
    public const string Pod = "k8s.Pod";
    public const string Secret = "k8s.Secret";

    /// <summary>A read-only copy of a PV for reading its volume file by file (cluster-scoped; id: its name).</summary>
    public const string PersistentVolume = "k8s.PersistentVolume";

    /// <summary>A workload scaled to 0 for a PVC swap (id <c>namespace/Kind/name</c>, data: its replicas); its cleanup scales it back.</summary>
    public const string ScaledWorkload = "k8s.ScaledWorkload";
}

/// <summary>Waits of the snapshot flow; tests shorten them.</summary>
public sealed record KubernetesTimings
{
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan SnapshotReady { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The mover's volume is a full copy of the snapshot (vSphere: a new disk from it), which takes long for large volumes.</summary>
    public TimeSpan MoverConnect { get; init; } = TimeSpan.FromHours(4);

    /// <summary>A mover that reads a volume file by file mounts an existing volume: no copy to wait for.</summary>
    public TimeSpan FilesMoverConnect { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan Deleted { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a restore waits for a target PVC to be bound: the storage copies the restored volume from its snapshot.</summary>
    public TimeSpan RestoreCopy { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How often a wait writes a line to the session log.</summary>
    public TimeSpan Progress { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a restore waits for a CRD it created to be established before it creates the CRD's objects.</summary>
    public TimeSpan CrdEstablished { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>A failure of the cluster in the snapshot flow (with the object and the cluster in its message).</summary>
public sealed class KubernetesOperationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A ready CSI snapshot of a PVC, taken in the PVC's namespace.</summary>
internal sealed record TakenSnapshot(
    string Namespace, string Claim, string Name, string ContentName, string Handle, string Driver, string SnapshotClass,
    string? StorageClass, string VolumeMode, long RestoreSize, Guid LedgerId)
{
    /// <summary>When the VolumeSnapshot was created (for its lifetime).</summary>
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Source => $"{Namespace}/{Claim}";
}

/// <summary>
/// The data path of a PVC backup (spec 2026-11-11-1343, spike S1/S2): a CSI snapshot of the PVC in its namespace; the
/// snapshot exposed in the work namespace (a static VolumeSnapshotContent with <c>Retain</c> and the volume-mode-change
/// annotation, a VolumeSnapshot on it, a block PVC from it); a mover pod that sends the device to the server. Every
/// object is in the artifact ledger before it is created, so a crash leaves nothing behind for long.
/// </summary>
internal sealed class VolumeSnapshotFlow(IKubernetes client, string cluster, IOperationLog log, IArtifactLedger ledger, KubernetesTimings timings)
{
    internal const string Group = "snapshot.storage.k8s.io";
    internal const string Version = "v1";
    internal const string ManagedByLabel = "app.kubernetes.io/managed-by";
    internal const string RunLabel = "backdafuckup.io/run";
    internal const string SourceAnnotation = "backdafuckup.io/source";
    internal const string VolumeModeChangeAnnotation = "snapshot.storage.kubernetes.io/allow-volume-mode-change";
    internal const string DevicePath = "/dev/bdfu-volume";

    public string Cluster => cluster;

    public IKubernetes Client => client;

    public IOperationLog Log => log;

    public IArtifactLedger Ledger => ledger;

    public KubernetesTimings Timings => timings;

    /// <summary>The server's mover channels, for restores (backups get them per export).</summary>
    public IMoverChannels? Channels { get; init; }

    /// <summary>The mover image, for restores.</summary>
    public MoverDeployment? Mover { get; set; }

    /// <summary>Takes a snapshot of the PVC and waits until it is ready to use.</summary>
    public async Task<TakenSnapshot> TakeAsync(string ns, string claim, string snapshotClass, string run, CancellationToken cancellationToken)
    {
        var pvc = await CallAsync($"reading the PVC {ns}/{claim}", () => client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(claim, ns, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var name = ObjectName(run, claim);
        var ledgerId = await ledger.RecordAsync(KubernetesArtifactKinds.VolumeSnapshot, $"{ns}/{name}", null, Source(ns, claim), cancellationToken).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["apiVersion"] = $"{Group}/{Version}",
            ["kind"] = "VolumeSnapshot",
            ["metadata"] = Metadata(name, run, ns, claim),
            ["spec"] = new JsonObject
            {
                ["volumeSnapshotClassName"] = snapshotClass,
                ["source"] = new JsonObject { ["persistentVolumeClaimName"] = claim },
            },
        };
        var created = DateTimeOffset.UtcNow;
        await CallAsync($"creating the snapshot {ns}/{name}", () => client.CustomObjects.CreateNamespacedCustomObjectAsync(body, Group, Version, ns, "volumesnapshots", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var started = DateTimeOffset.UtcNow;
        var (contentName, restoreSize) = await WaitReadyAsync(ns, name, $"the snapshot {ns}/{name} of the PVC {claim}", timings.SnapshotReady, cancellationToken).ConfigureAwait(false);
        var content = await CallAsync($"reading the snapshot content {contentName}", () => client.CustomObjects.GetClusterCustomObjectAsync(Group, Version, "volumesnapshotcontents", contentName, cancellationToken))
            .ConfigureAwait(false);
        var json = Element(content);
        var handle = Text(json, "status", "snapshotHandle") ?? throw new KubernetesOperationException($"The snapshot content {contentName} of {ns}/{name} has no snapshot handle (cluster {cluster}).");
        var size = Number(json, "status", "restoreSize") ?? restoreSize
            ?? (pvc.Status?.Capacity?.TryGetValue("storage", out var capacity) == true ? capacity.ToInt64() : (long?)null)
            ?? throw new KubernetesOperationException($"The size of the snapshot {ns}/{name} is unknown (no restoreSize, no capacity of {ns}/{claim}).");
        log.Info($"Snapshot {ns}/{name} of {claim} ready after {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s ({Bytes(size)}, handle {handle}).");
        var driver = Text(json, "spec", "driver") ?? string.Empty;
        KubernetesMetrics.RecordSnapshotReady(cluster, driver, DateTimeOffset.UtcNow - created);
        return new TakenSnapshot(ns, claim, name, contentName, handle, driver, snapshotClass, pvc.Spec.StorageClassName, pvc.Spec.VolumeMode ?? "Filesystem", size, ledgerId)
        {
            CreatedUtc = created,
        };
    }

    /// <summary>
    /// The pull secret of the mover image in the work namespace, when the server keeps the registry's credentials (pull
    /// secret mode "Create"): created, or updated when the credentials changed. Any data path calls it before its first
    /// mover pod, so a cluster that never ran a backup (a restore's target) gets it too.
    /// </summary>
    public async Task EnsurePullSecretAsync(MoverDeployment mover, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mover);
        if (mover.DockerConfigJson is not { } dockerConfig || mover.PullSecretName is not { } secretName)
        {
            return;
        }

        var client = Client;
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, Labels = new Dictionary<string, string> { [ManagedByLabel] = "backdafuckup" } },
            Type = "kubernetes.io/dockerconfigjson",
            Data = new Dictionary<string, byte[]> { [".dockerconfigjson"] = Encoding.UTF8.GetBytes(dockerConfig) },
        };
        V1Secret? existing = null;
        try
        {
            existing = await client.CoreV1.ReadNamespacedSecretAsync(secretName, KubernetesSetup.WorkNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Created below.
        }

        if (existing is null)
        {
            await CallAsync($"creating the pull secret {KubernetesSetup.WorkNamespace}/{secretName}", () => client.CoreV1.CreateNamespacedSecretAsync(
                secret, KubernetesSetup.WorkNamespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else if (existing.Data?.TryGetValue(".dockerconfigjson", out var current) != true || !current.AsSpan().SequenceEqual(secret.Data[".dockerconfigjson"]))
        {
            secret.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await CallAsync($"updating the pull secret {KubernetesSetup.WorkNamespace}/{secretName}", () => client.CoreV1.ReplaceNamespacedSecretAsync(
                secret, secretName, KubernetesSetup.WorkNamespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Exposes the snapshot to a mover pod that sends it through a channel; dispose the export to remove its objects.</summary>
    public async Task<VolumeExport> ExportAsync(TakenSnapshot snapshot, string run, IMoverChannels channels, MoverDeployment mover, CancellationToken cancellationToken)
    {
        var export = new VolumeExport(this, snapshot, ObjectName(run, Hash(snapshot.Source)), run);
        try
        {
            await export.StartAsync(channels, mover, cancellationToken).ConfigureAwait(false);
            return export;
        }
        catch
        {
            await export.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Deletes the snapshot (its class deletes the storage's snapshot with it) and waits until its content is gone.</summary>
    public async Task DeleteSnapshotAsync(TakenSnapshot snapshot, CancellationToken cancellationToken)
    {
        await DeleteAsync(KubernetesArtifactKinds.VolumeSnapshot, snapshot.Namespace, snapshot.Name, cancellationToken).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        while (await ExistsAsync(() => client.CustomObjects.GetClusterCustomObjectAsync(Group, Version, "volumesnapshotcontents", snapshot.ContentName, cancellationToken)).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow - started > timings.Deleted)
            {
                log.Warning($"The snapshot {snapshot.Namespace}/{snapshot.Name} is deleted, but its content {snapshot.ContentName} (handle {snapshot.Handle}) still exists after " +
                    $"{timings.Deleted.TotalMinutes:0} minutes in cluster {cluster}: the CSI driver {snapshot.Driver} did not delete the storage snapshot yet.");
                break;
            }

            await Task.Delay(timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        KubernetesMetrics.RecordSnapshotLifetime(cluster, snapshot.Driver, DateTimeOffset.UtcNow - snapshot.CreatedUtc);

        await ledger.UpdateAsync(snapshot.LedgerId, ArtifactState.Done, null, cancellationToken).ConfigureAwait(false);
        log.Info($"Snapshot {snapshot.Namespace}/{snapshot.Name} of {snapshot.Claim} deleted.");
    }

    /// <summary>Deletes an object of the flow; an object already gone counts as deleted.</summary>
    public async Task DeleteAsync(string kind, string? ns, string name, CancellationToken cancellationToken)
    {
        var what = ns is null ? $"{kind} {name}" : $"{kind} {ns}/{name}";
        try
        {
            switch (kind)
            {
                case KubernetesArtifactKinds.VolumeSnapshot:
                    await client.CustomObjects.DeleteNamespacedCustomObjectAsync(Group, Version, ns, "volumesnapshots", name, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case KubernetesArtifactKinds.VolumeSnapshotContent:
                    await client.CustomObjects.DeleteClusterCustomObjectAsync(Group, Version, "volumesnapshotcontents", name, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case KubernetesArtifactKinds.PersistentVolumeClaim:
                    await client.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(name, ns, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case KubernetesArtifactKinds.Pod:
                    await client.CoreV1.DeleteNamespacedPodAsync(name, ns, gracePeriodSeconds: 10, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case KubernetesArtifactKinds.Secret:
                    await client.CoreV1.DeleteNamespacedSecretAsync(name, ns, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                case KubernetesArtifactKinds.PersistentVolume:
                {
                    // The only PVs the product deletes: its own read-only copies, which keep the data whatever happens to them.
                    var pv = await client.CoreV1.ReadPersistentVolumeAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (pv.Metadata.Labels is not { } labels || !labels.TryGetValue(ManagedByLabel, out var managedBy) || managedBy != "backdafuckup"
                        || pv.Spec.PersistentVolumeReclaimPolicy != "Retain")
                    {
                        throw new KubernetesOperationException(
                            $"{what} in cluster {cluster} is not left to delete: it is not backdafuckup's read-only copy of a volume (label {ManagedByLabel}, reclaim policy Retain).");
                    }

                    await client.CoreV1.DeletePersistentVolumeAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new KubernetesOperationException($"Unknown kind of object {what} in the artifact ledger of cluster {cluster}.");
            }
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone already.
        }
        catch (HttpOperationException ex)
        {
            throw new KubernetesOperationException($"Deleting {what} in cluster {cluster} failed: {Problem(ex)}", ex);
        }
    }

    /// <summary>Waits until a VolumeSnapshot is ready to use; returns its content and size, fails with the snapshot's error.</summary>
    internal async Task<(string Content, long? RestoreSize)> WaitReadyAsync(string ns, string name, string what, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var reported = started;
        while (true)
        {
            var snapshot = Element(await CallAsync($"reading {what}", () => client.CustomObjects.GetNamespacedCustomObjectAsync(Group, Version, ns, "volumesnapshots", name, cancellationToken))
                .ConfigureAwait(false));
            if (Text(snapshot, "status", "error", "message") is { Length: > 0 } error)
            {
                throw new KubernetesOperationException($"{Capitalized(what)} failed in cluster {cluster}: {error}");
            }

            if (snapshot.TryGetProperty("status", out var status) && status.TryGetProperty("readyToUse", out var ready) && ready.ValueKind == JsonValueKind.True
                && Text(snapshot, "status", "boundVolumeSnapshotContentName") is { Length: > 0 } content)
            {
                var size = Text(snapshot, "status", "restoreSize") is { Length: > 0 } quantity ? new ResourceQuantity(quantity).ToInt64() : (long?)null;
                return (content, size);
            }

            var elapsed = DateTimeOffset.UtcNow - started;
            if (elapsed > timeout)
            {
                throw new KubernetesOperationException($"{Capitalized(what)} was not ready to use after {timeout.TotalMinutes:0} minutes (cluster {cluster}).");
            }

            if (DateTimeOffset.UtcNow - reported >= timings.Progress)
            {
                reported = DateTimeOffset.UtcNow;
                log.Info($"Waiting for {what} ({elapsed.TotalMinutes:0} min).");
            }

            await Task.Delay(timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Calls the API; an error names what was done, the cluster and the API's message.</summary>
    internal async Task<T> CallAsync<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw new KubernetesOperationException($"{Capitalized(what)} in cluster {cluster} failed: {Problem(ex)}", ex);
        }
    }

    internal static async Task<bool> ExistsAsync<T>(Func<Task<T>> read)
    {
        try
        {
            await read().ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    internal static string Problem(HttpOperationException ex)
    {
        var code = ex.Response?.StatusCode;
        var message = ex.Response?.Content;
        try
        {
            if (message is { Length: > 0 } && JsonDocument.Parse(message).RootElement is { ValueKind: JsonValueKind.Object } status && status.TryGetProperty("message", out var text))
            {
                message = text.GetString();
            }
        }
        catch (JsonException)
        {
            // Not a Status object.
        }

        return code switch
        {
            HttpStatusCode.Forbidden => $"403 Forbidden: the ServiceAccount lacks a permission (apply the current setup manifest). {message}",
            { } c => $"{(int)c} {c}: {message?.Trim()}",
            null => ex.Message,
        };
    }

    internal static JsonObject Metadata(string name, string run, string sourceNamespace, string claim, string? ns = null, JsonObject? annotations = null)
    {
        annotations ??= [];
        annotations[SourceAnnotation] = $"{sourceNamespace}/{claim}";
        var metadata = new JsonObject
        {
            ["name"] = name,
            ["labels"] = new JsonObject { [ManagedByLabel] = "backdafuckup", [RunLabel] = run },
            ["annotations"] = annotations,
        };
        if (ns is not null)
        {
            metadata["namespace"] = ns;
        }

        return metadata;
    }

    /// <summary><c>bdfu-&lt;run&gt;-&lt;suffix&gt;</c>, a DNS label of at most 63 characters (pods' host names).</summary>
    internal static string ObjectName(string run, string suffix)
    {
        var clean = new string([.. suffix.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        var name = $"bdfu-{run}-{clean}";
        if (name.Length > 63)
        {
            name = $"{name[..54].TrimEnd('-')}-{Hash(suffix)}";
        }

        return name;
    }

    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

    /// <summary>A size as Kubernetes shows it best: whole GiB or MiB as <c>Gi</c>/<c>Mi</c>, any other in bytes.</summary>
    internal static ResourceQuantity Quantity(long bytes) => new(
        bytes > 0 && bytes % (1L << 30) == 0 ? $"{bytes >> 30}Gi"
        : bytes > 0 && bytes % (1L << 20) == 0 ? $"{bytes >> 20}Mi"
        : bytes.ToString(CultureInfo.InvariantCulture));

    internal static JsonElement Element(object value) => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);

    internal static string? Text(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    internal static long? Number(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.Number ? element.GetInt64() : null;
    }

    internal static string Bytes(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GiB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB");

    private static string Source(string ns, string claim) => JsonSerializer.Serialize(new { source = $"{ns}/{claim}" });

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
