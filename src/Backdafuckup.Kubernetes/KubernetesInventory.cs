using System.Globalization;
using System.Text.Json;
using Backdafuckup.Core.Hypervisor;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>Property keys of Kubernetes inventory items.</summary>
public static class KubernetesInventoryProperties
{
    public const string Namespace = "namespace";
    public const string Phase = "phase";
    public const string VolumeClaims = "volumeClaims";
    public const string SnapshotVolumes = "snapshotVolumes";
    public const string SizeBytes = "sizeBytes";
    public const string StorageClass = "storageClass";
    public const string Driver = "driver";
    public const string VolumeMode = "volumeMode";
    public const string AccessModes = "accessModes";
    public const string VolumeName = "volumeName";
    public const string UsedBy = "usedBy";

    /// <summary>How the volume's data is backed up: <see cref="DataPaths"/>.</summary>
    public const string DataPath = "dataPath";

    /// <summary>Why the data is not backed up through a snapshot (objects only).</summary>
    public const string DataPathReason = "dataPathReason";

    /// <summary>The VolumeSnapshotClass a snapshot would use.</summary>
    public const string SnapshotClass = "snapshotClass";
}

/// <summary>How a volume's data is backed up.</summary>
public static class DataPaths
{
    /// <summary>A CSI snapshot, copied into the repository.</summary>
    public const string Snapshot = "snapshot";

    /// <summary>Only the volume's objects (PVC, PV); its data is not backed up (yet).</summary>
    public const string ObjectsOnly = "objects-only";
}

internal static class KubernetesInventory
{
    private const string SnapshotGroup = "snapshot.storage.k8s.io";
    private const string DefaultSnapshotClassAnnotation = "snapshot.storage.kubernetes.io/is-default-class";

    /// <summary>
    /// The cluster's identity: the uid of the namespace kube-system, which lives as long as the cluster. The address is the
    /// one given (the server connects to the platform's master address next time), never the URL.
    /// </summary>
    public static async Task<PlatformInfo> GetPlatformAsync(IKubernetes client, ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var version = await client.Version.GetCodeAsync(cancellationToken).ConfigureAwait(false);
        var kubeSystem = await client.CoreV1.ReadNamespaceAsync("kube-system", cancellationToken: cancellationToken).ConfigureAwait(false);
        return new PlatformInfo(kubeSystem.Metadata.Uid, settings.Address, settings.Address, "Kubernetes", version.GitVersion);
    }

    public static async Task<Inventory> GetAsync(IKubernetes client, ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var platform = await GetPlatformAsync(client, settings, cancellationToken).ConfigureAwait(false);
        var namespaces = await client.CoreV1.ListNamespaceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var claims = await client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var volumes = (await client.CoreV1.ListPersistentVolumeAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(v => v.Metadata.Name, StringComparer.Ordinal);
        var classes = (await client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(c => c.Metadata.Name, StringComparer.Ordinal);
        var snapshotClasses = await SnapshotClassesAsync(client, cancellationToken).ConfigureAwait(false);
        var usedBy = await UsedByAsync(client, cancellationToken).ConfigureAwait(false);

        var items = new List<InventoryItem>();
        var perNamespace = new Dictionary<string, (int Claims, int Snapshot)>(StringComparer.Ordinal);
        foreach (var claim in claims.Items)
        {
            var ns = claim.Metadata.NamespaceProperty;
            var volume = claim.Spec.VolumeName is { Length: > 0 } pv ? volumes.GetValueOrDefault(pv) : null;
            var className = claim.Spec.StorageClassName ?? volume?.Spec.StorageClassName ?? string.Empty;
            var driver = volume?.Spec.Csi?.Driver ?? (volume?.Spec.Nfs is not null ? "nfs" : classes.GetValueOrDefault(className)?.Provisioner ?? string.Empty);
            var (path, reason, snapshotClass) = DataPathOf(volume, driver, snapshotClasses);
            var size = claim.Status?.Capacity?.TryGetValue("storage", out var capacity) == true ? capacity
                : claim.Spec.Resources?.Requests?.TryGetValue("storage", out var requested) == true ? requested : null;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesInventoryProperties.Namespace] = ns,
                [KubernetesInventoryProperties.Phase] = claim.Status?.Phase ?? string.Empty,
                [KubernetesInventoryProperties.SizeBytes] = size is null ? string.Empty : size.ToInt64().ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.StorageClass] = className,
                [KubernetesInventoryProperties.Driver] = driver,
                [KubernetesInventoryProperties.VolumeMode] = claim.Spec.VolumeMode ?? "Filesystem",
                [KubernetesInventoryProperties.AccessModes] = string.Join(",", claim.Spec.AccessModes ?? []),
                [KubernetesInventoryProperties.VolumeName] = claim.Spec.VolumeName ?? string.Empty,
                [KubernetesInventoryProperties.UsedBy] = string.Join(", ", usedBy.GetValueOrDefault((ns, claim.Metadata.Name)) ?? []),
                [KubernetesInventoryProperties.DataPath] = path,
                [KubernetesInventoryProperties.DataPathReason] = reason ?? string.Empty,
                [KubernetesInventoryProperties.SnapshotClass] = snapshotClass ?? string.Empty,
            };
            items.Add(new InventoryItem(InventoryKind.VolumeClaim, $"{ns}/{claim.Metadata.Name}", claim.Metadata.Name, ns, properties));
            var counts = perNamespace.GetValueOrDefault(ns);
            perNamespace[ns] = (counts.Claims + 1, counts.Snapshot + (path == DataPaths.Snapshot ? 1 : 0));
        }

        foreach (var ns in namespaces.Items)
        {
            var name = ns.Metadata.Name;
            var (claimCount, snapshotCount) = perNamespace.GetValueOrDefault(name);
            items.Add(new InventoryItem(InventoryKind.Namespace, name, name, platform.PlatformId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesInventoryProperties.Phase] = ns.Status?.Phase ?? string.Empty,
                [KubernetesInventoryProperties.VolumeClaims] = claimCount.ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.SnapshotVolumes] = snapshotCount.ToString(CultureInfo.InvariantCulture),
            }));
        }

        return new Inventory(platform, items);
    }

    /// <summary>
    /// A volume of a CSI driver with a VolumeSnapshotClass is backed up through a snapshot (the class marked default for the
    /// driver, else the only one); anything else only by its objects, with the reason.
    /// </summary>
    internal static (string Path, string? Reason, string? SnapshotClass) DataPathOf(V1PersistentVolume? volume, string driver, IReadOnlyList<(string Name, string Driver, bool IsDefault)> snapshotClasses)
    {
        if (volume is null)
        {
            return (DataPaths.ObjectsOnly, "not bound to a volume", null);
        }

        if (volume.Spec.Csi is null)
        {
            return (DataPaths.ObjectsOnly, volume.Spec.Nfs is not null ? "NFS volume: no CSI snapshots (file-level backup comes later)" : "not a CSI volume: no snapshots", null);
        }

        var candidates = snapshotClasses.Where(c => string.Equals(c.Driver, driver, StringComparison.Ordinal)).ToList();
        var chosen = candidates.FirstOrDefault(c => c.IsDefault).Name ?? (candidates.Count == 1 ? candidates[0].Name : null);
        return chosen is not null
            ? (DataPaths.Snapshot, null, chosen)
            : (DataPaths.ObjectsOnly, candidates.Count == 0
                ? $"no VolumeSnapshotClass for the CSI driver {driver}"
                : $"{candidates.Count} VolumeSnapshotClasses for the CSI driver {driver} and none marked default ({DefaultSnapshotClassAnnotation})", null);
    }

    private static async Task<IReadOnlyList<(string Name, string Driver, bool IsDefault)>> SnapshotClassesAsync(IKubernetes client, CancellationToken cancellationToken)
    {
        object list;
        try
        {
            list = await client.CustomObjects.ListClusterCustomObjectAsync(SnapshotGroup, "v1", "volumesnapshotclasses", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return []; // No snapshot CRDs in the cluster.
        }

        var json = list is JsonElement element ? element : JsonSerializer.SerializeToElement(list);
        var result = new List<(string, string, bool)>();
        if (json.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var meta = item.GetProperty("metadata");
                var isDefault = meta.TryGetProperty("annotations", out var annotations) && annotations.TryGetProperty(DefaultSnapshotClassAnnotation, out var value)
                    && string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase);
                result.Add((meta.GetProperty("name").GetString() ?? string.Empty, item.TryGetProperty("driver", out var d) ? d.GetString() ?? string.Empty : string.Empty, isDefault));
            }
        }

        return result;
    }

    /// <summary>Which workloads mount each PVC: the owner of each pod (a ReplicaSet's Deployment by its name prefix).</summary>
    private static async Task<Dictionary<(string Namespace, string Claim), SortedSet<string>>> UsedByAsync(IKubernetes client, CancellationToken cancellationToken)
    {
        var pods = await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<(string, string), SortedSet<string>>();
        foreach (var pod in pods.Items)
        {
            var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
            var workload = owner switch
            {
                null => $"Pod {pod.Metadata.Name}",
                { Kind: "ReplicaSet" } rs when rs.Name.LastIndexOf('-') is > 0 and var dash => $"Deployment {rs.Name[..dash]}",
                { } o => $"{o.Kind} {o.Name}",
            };
            foreach (var volume in pod.Spec.Volumes ?? [])
            {
                if (volume.PersistentVolumeClaim?.ClaimName is { } claim)
                {
                    var key = (pod.Metadata.NamespaceProperty, claim);
                    if (!result.TryGetValue(key, out var set))
                    {
                        result[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    set.Add(workload);
                }
            }
        }

        return result;
    }
}
