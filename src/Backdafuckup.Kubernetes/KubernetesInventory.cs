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

    /// <summary>A namespace's PVCs whose data is read file by file (NFS, hostPath, local).</summary>
    public const string FileVolumes = "fileVolumes";

    /// <summary>A namespace's labels, <c>key=value</c> joined with commas (jobs select namespaces by them).</summary>
    public const string Labels = "labels";
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

    /// <summary>The data in a PVC, a pod volume or a container's file system, from the usage numbers; empty: not known.</summary>
    public const string UsedBytes = "usedBytes";

    /// <summary>A namespace's pod volumes that are not PVCs, without those of platforms.</summary>
    public const string PodVolumes = "podVolumes";

    /// <summary>A namespace's data the usage numbers found outside PVCs (pod volumes, containers' file systems), without platforms'.</summary>
    public const string UnsavedBytes = "unsavedBytes";

    /// <summary>Pod volume: the pods' controller ("Deployment web").</summary>
    public const string Workload = "workload";

    /// <summary>Pod volume: <see cref="PodVolumeKinds"/>.</summary>
    public const string Kind = "kind";

    /// <summary>Pod volume: the hostPath's path, the NFS export, the CSI driver.</summary>
    public const string Source = "source";

    /// <summary>Pod volume: in how many running pods.</summary>
    public const string Pods = "pods";

    public const string SizeLimit = "sizeLimitBytes";

    /// <summary>Pod volume: <c>annotation</c> when a pod names it in <c>backup.velero.io/backup-volumes</c>.</summary>
    public const string OptIn = "optIn";

    /// <summary>Pod volume: the platform that puts it into pods, or whose namespace it is in.</summary>
    public const string Platform = "platform";

    /// <summary>Pod volume: why a backup does not hold its data.</summary>
    public const string Reason = "reason";

    /// <summary>Container: how fast its pods write to local disks (bytes per second, over an hour).</summary>
    public const string WriteBytesPerSecond = "writeBytesPerSecond";
}

/// <summary>How a volume's data is backed up.</summary>
public static class DataPaths
{
    /// <summary>A CSI snapshot, copied into the repository.</summary>
    public const string Snapshot = "snapshot";

    /// <summary>Read file by file (a volume without CSI snapshots: NFS, hostPath, local), into a file tree.</summary>
    public const string Files = "files";

    /// <summary>Only the volume's objects (PVC, PV); its data is not backed up (yet).</summary>
    public const string ObjectsOnly = "objects-only";
}

/// <summary>A PVC of a namespace and how its data is backed up (<see cref="DataPaths"/>).</summary>
/// <param name="OnNodeOfConsumer">A hostPath PV without node affinity: its data is on the node of the pods that use it.</param>
/// <param name="SharedFileSystem">An NFS or hostPath PV: the kubelet's numbers for it are those of the whole export or node disk.</param>
internal sealed record NamespaceVolume(
    string Claim, string DataPath, string? Reason, string? StorageClass, string Driver, string? SnapshotClass, long SizeBytes, string VolumeMode, string? VolumeName,
    bool OnNodeOfConsumer = false, bool SharedFileSystem = false)
{
    /// <summary>What the usage numbers say the PVC holds, when they can be about it alone.</summary>
    public long? UsedBytes(KubernetesUsage? usage, string ns) => KubernetesInventory.ClaimUsage(usage, ns, Claim, SizeBytes, SharedFileSystem);
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
        return new PlatformInfo(kubeSystem.Metadata.Uid, settings.ClusterName(), settings.Address, "Kubernetes", version.GitVersion);
    }

    /// <param name="usage">The cluster's usage numbers (sizes of PVCs, pod volumes and containers' file systems); null: none.</param>
    public static async Task<Inventory> GetAsync(IKubernetes client, ConnectionSettings settings, KubernetesUsage? usage, CancellationToken cancellationToken)
    {
        var platform = await GetPlatformAsync(client, settings, cancellationToken).ConfigureAwait(false);
        var namespaces = await client.CoreV1.ListNamespaceAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var claims = await client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var volumes = (await client.CoreV1.ListPersistentVolumeAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(v => v.Metadata.Name, StringComparer.Ordinal);
        var classes = (await client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(c => c.Metadata.Name, StringComparer.Ordinal);
        var snapshotClasses = await SnapshotClassesAsync(client, cancellationToken).ConfigureAwait(false);
        var pods = (await client.CoreV1.ListPodForAllNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items;
        var usedBy = UsedBy(pods);

        var items = new List<InventoryItem>();
        var perNamespace = new Dictionary<string, (int Claims, int Snapshot, int Files)>(StringComparer.Ordinal);
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
                [KubernetesInventoryProperties.UsedBytes] = ClaimUsage(usage, ns, claim.Metadata.Name, size?.ToInt64() ?? 0, volume?.Spec.Nfs is not null || volume?.Spec.HostPath is not null)
                    ?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            };
            items.Add(new InventoryItem(InventoryKind.VolumeClaim, $"{ns}/{claim.Metadata.Name}", claim.Metadata.Name, ns, properties));
            var counts = perNamespace.GetValueOrDefault(ns);
            perNamespace[ns] = (counts.Claims + 1, counts.Snapshot + (path == DataPaths.Snapshot ? 1 : 0), counts.Files + (path == DataPaths.Files ? 1 : 0));
        }

        // The cluster's platforms (from what was read already, plus API groups, nodes and CSI drivers); the product name
        // says which they are, e.g. "RKE2 · Rancher · Fleet".
        var facts = await KubernetesPlatformDetection.ReadFactsAsync(
            client, platform.ProductVersion, [.. namespaces.Items.Select(n => n.Metadata.Name)], [.. classes.Values.Select(c => c.Provisioner)], cancellationToken).ConfigureAwait(false);
        var detected = KubernetesPlatformDetection.Detect(facts);
        var selection = KubernetesPlatformDetection.Select(detected, settings);

        // Pod volumes that are not PVCs and containers' own file systems, per workload: what backups do not hold unless asked for.
        var podVolumes = new List<(string Namespace, Core.Repository.KubernetesPodVolumeRecord Volume)>();
        var writes = new List<(string Namespace, Core.Repository.KubernetesContainerWriteRecord Write)>();
        foreach (var group in pods.GroupBy(p => p.Metadata.NamespaceProperty, StringComparer.Ordinal))
        {
            podVolumes.AddRange(PodVolumes.Find(group, selection.Set, all: false, usage).Select(v => (group.Key, v)));
            writes.AddRange(PodVolumes.Writes(group, usage).Select(w => (group.Key, w)));
        }

        items.AddRange(PodVolumeItems(podVolumes, writes, selection.Set));

        foreach (var ns in namespaces.Items)
        {
            var name = ns.Metadata.Name;
            var (claimCount, snapshotCount, fileCount) = perNamespace.GetValueOrDefault(name);
            var own = podVolumes.Where(v => v.Namespace == name && v.Volume.Platform is null).Select(v => v.Volume).ToList();
            var layers = writes.Where(w => w.Namespace == name && w.Write.LayerBytes is not null).Select(w => w.Write.LayerBytes!.Value).ToList();
            var known = own.Any(v => v.UsedBytes is not null) || layers.Count > 0;
            items.Add(new InventoryItem(InventoryKind.Namespace, name, name, platform.PlatformId, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [KubernetesInventoryProperties.PodVolumes] = own.Count.ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.UnsavedBytes] = known ? (own.Sum(v => v.UsedBytes ?? 0) + layers.Sum()).ToString(CultureInfo.InvariantCulture) : string.Empty,
                [KubernetesInventoryProperties.Phase] = ns.Status?.Phase ?? string.Empty,
                [KubernetesInventoryProperties.VolumeClaims] = claimCount.ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.SnapshotVolumes] = snapshotCount.ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.FileVolumes] = fileCount.ToString(CultureInfo.InvariantCulture),
                [KubernetesInventoryProperties.Labels] = string.Join(',', (ns.Metadata.Labels ?? new Dictionary<string, string>()).OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}")),
            }));
        }

        return new Inventory(platform with { ProductName = selection.Set.Display }, items) { Platforms = detected };
    }

    /// <summary>
    /// A volume of a CSI driver with a VolumeSnapshotClass is backed up through a snapshot (the class marked default for the
    /// driver, else the only one); an NFS, hostPath or local volume file by file through a read-only copy of its PV; anything
    /// else only by its objects, with the reason.
    /// </summary>
    internal static (string Path, string? Reason, string? SnapshotClass) DataPathOf(V1PersistentVolume? volume, string driver, IReadOnlyList<(string Name, string Driver, bool IsDefault)> snapshotClasses)
    {
        if (volume is null)
        {
            return (DataPaths.ObjectsOnly, "not bound to a volume", null);
        }

        if (volume.Spec.Csi is null)
        {
            return volume.Spec switch
            {
                { Nfs: { } nfs } => (DataPaths.Files, $"NFS volume ({nfs.Server}:{nfs.Path}): read file by file through a read-only copy of its PV", null),
                { HostPath: { } host } => (DataPaths.Files, $"hostPath volume ({host.Path}): read file by file through a read-only copy of its PV", null),
                { Local: { } local } => (DataPaths.Files, $"local volume ({local.Path}): read file by file through a read-only copy of its PV", null),
                _ => (DataPaths.ObjectsOnly, "not a CSI volume, and not NFS, hostPath or local: its data cannot be read", null),
            };
        }

        var candidates = snapshotClasses.Where(c => string.Equals(c.Driver, driver, StringComparison.Ordinal)).ToList();
        var chosen = candidates.FirstOrDefault(c => c.IsDefault).Name ?? (candidates.Count == 1 ? candidates[0].Name : null);
        return chosen is not null
            ? (DataPaths.Snapshot, null, chosen)
            : (DataPaths.ObjectsOnly, candidates.Count == 0
                ? $"no VolumeSnapshotClass for the CSI driver {driver}"
                : $"{candidates.Count} VolumeSnapshotClasses for the CSI driver {driver} and none marked default ({DefaultSnapshotClassAnnotation})", null);
    }

    /// <summary>The PVCs of one namespace with their data path (as the inventory shows them), for its backup.</summary>
    internal static async Task<IReadOnlyList<NamespaceVolume>> VolumesAsync(IKubernetes client, string ns, CancellationToken cancellationToken)
    {
        var claims = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: cancellationToken).ConfigureAwait(false);
        var classes = (await client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(c => c.Metadata.Name, StringComparer.Ordinal);
        var snapshotClasses = await SnapshotClassesAsync(client, cancellationToken).ConfigureAwait(false);
        var result = new List<NamespaceVolume>();
        foreach (var claim in claims.Items.OrderBy(c => c.Metadata.Name, StringComparer.Ordinal))
        {
            V1PersistentVolume? volume = null;
            if (claim.Spec.VolumeName is { Length: > 0 } pv)
            {
                try
                {
                    volume = await client.CoreV1.ReadPersistentVolumeAsync(pv, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Not bound (any more).
                }
            }

            var className = claim.Spec.StorageClassName ?? volume?.Spec.StorageClassName;
            var driver = volume?.Spec.Csi?.Driver ?? (volume?.Spec.Nfs is not null ? "nfs" : className is null ? string.Empty : classes.GetValueOrDefault(className)?.Provisioner ?? string.Empty);
            var (path, reason, snapshotClass) = DataPathOf(volume, driver, snapshotClasses);
            var size = claim.Status?.Capacity?.TryGetValue("storage", out var capacity) == true ? capacity.ToInt64()
                : claim.Spec.Resources?.Requests?.TryGetValue("storage", out var requested) == true ? requested.ToInt64() : 0;
            result.Add(new NamespaceVolume(
                claim.Metadata.Name, path, reason, className, driver, snapshotClass, size, claim.Spec.VolumeMode ?? "Filesystem", volume?.Metadata.Name,
                volume?.Spec.HostPath is not null && volume.Spec.NodeAffinity is null, volume?.Spec.Nfs is not null || volume?.Spec.HostPath is not null));
        }

        return result;
    }

    /// <summary>
    /// A PVC's data from the usage numbers. The kubelet measures the file system a volume is on: for an NFS or hostPath PV
    /// that is the whole export or node disk, so those give none; neither does a number larger than the PVC.
    /// </summary>
    internal static long? ClaimUsage(KubernetesUsage? usage, string ns, string claim, long sizeBytes, bool sharedFileSystem) =>
        !sharedFileSystem && usage?.Claims.TryGetValue((ns, claim), out var used) == true && (sizeBytes <= 0 || used <= sizeBytes) ? used : null;

    internal static async Task<IReadOnlyList<(string Name, string Driver, bool IsDefault)>> SnapshotClassesAsync(IKubernetes client, CancellationToken cancellationToken)
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
    private static Dictionary<(string Namespace, string Claim), SortedSet<string>> UsedBy(IEnumerable<V1Pod> pods)
    {
        var result = new Dictionary<(string, string), SortedSet<string>>();
        foreach (var pod in pods)
        {
            var workload = PodVolumes.WorkloadOf(pod);
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

    /// <summary>
    /// Inventory items of pod volumes per workload and volume (the pods of a Deployment share their volumes' names), and of
    /// containers' own file systems per workload and container when the usage numbers know them.
    /// </summary>
    private static IEnumerable<InventoryItem> PodVolumeItems(
        IReadOnlyList<(string Namespace, Core.Repository.KubernetesPodVolumeRecord Volume)> volumes,
        IReadOnlyList<(string Namespace, Core.Repository.KubernetesContainerWriteRecord Write)> writes, KubernetesPlatformSet platforms)
    {
        foreach (var group in volumes.GroupBy(v => (v.Namespace, v.Volume.Workload, v.Volume.Volume)))
        {
            var first = group.First().Volume;
            var used = group.Where(v => v.Volume.UsedBytes is not null).Select(v => v.Volume.UsedBytes!.Value).ToList();
            yield return new InventoryItem(InventoryKind.PodVolume, $"{group.Key.Namespace}/{group.Key.Workload}/{group.Key.Volume}", group.Key.Volume, group.Key.Namespace,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesInventoryProperties.Namespace] = group.Key.Namespace,
                    [KubernetesInventoryProperties.Workload] = group.Key.Workload,
                    [KubernetesInventoryProperties.Kind] = first.Kind,
                    [KubernetesInventoryProperties.Source] = first.Source ?? string.Empty,
                    [KubernetesInventoryProperties.Pods] = group.Count().ToString(CultureInfo.InvariantCulture),
                    [KubernetesInventoryProperties.UsedBytes] = used.Count == 0 ? string.Empty : used.Sum().ToString(CultureInfo.InvariantCulture),
                    [KubernetesInventoryProperties.SizeLimit] = first.SizeLimitBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    [KubernetesInventoryProperties.OptIn] = group.Any(v => v.Volume.OptIn is not null) ? PodVolumeOptIns.Annotation : string.Empty,
                    [KubernetesInventoryProperties.Platform] = first.Platform ?? string.Empty,
                    [KubernetesInventoryProperties.Reason] = group.FirstOrDefault(v => v.Volume.OptIn is not null).Volume?.Reason ?? first.Reason ?? string.Empty,
                });
        }

        foreach (var group in writes.GroupBy(w => (w.Namespace, w.Write.Workload, w.Write.Container)))
        {
            var layers = group.Where(w => w.Write.LayerBytes is not null).Select(w => w.Write.LayerBytes!.Value).ToList();
            var rates = group.Where(w => w.Write.WriteBytesPerSecond is not null).Select(w => w.Write.WriteBytesPerSecond!.Value).ToList();
            yield return new InventoryItem(InventoryKind.PodVolume, $"{group.Key.Namespace}/{group.Key.Workload}/container:{group.Key.Container}", group.Key.Container, group.Key.Namespace,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [KubernetesInventoryProperties.Namespace] = group.Key.Namespace,
                    [KubernetesInventoryProperties.Workload] = group.Key.Workload,
                    [KubernetesInventoryProperties.Kind] = PodVolumeKinds.Container,
                    [KubernetesInventoryProperties.Pods] = group.Count().ToString(CultureInfo.InvariantCulture),
                    [KubernetesInventoryProperties.UsedBytes] = layers.Count == 0 ? string.Empty : layers.Sum().ToString(CultureInfo.InvariantCulture),
                    [KubernetesInventoryProperties.WriteBytesPerSecond] = rates.Count == 0 ? string.Empty : rates.Sum().ToString("0.###", CultureInfo.InvariantCulture),
                    [KubernetesInventoryProperties.Platform] = platforms.NamespaceOwner(group.Key.Namespace)?.Name ?? string.Empty,
                    [KubernetesInventoryProperties.Reason] = "a container's own file system: no backup reads it",
                });
        }
    }
}
