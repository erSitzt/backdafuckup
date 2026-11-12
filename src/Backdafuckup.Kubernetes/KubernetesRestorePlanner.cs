using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Repository;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>How a namespace's restore point would go into a target cluster, before anything is written.</summary>
/// <param name="Cluster">The target cluster's name.</param>
/// <param name="StorageClasses">The target's classes, for the mapping.</param>
public sealed record KubernetesRestorePlan(
    string Cluster,
    string TargetNamespace,
    bool OtherCluster,
    bool NamespaceExists,
    IReadOnlyList<string> StorageClasses,
    string? DefaultStorageClass,
    IReadOnlyList<KubernetesVolumePlan> Volumes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

/// <summary>One PVC of the restore point in the target.</summary>
/// <param name="Path"><c>block</c>, <c>files</c>, <c>objects</c> (no data in the restore point) or <c>missing</c> (no such class in the target).</param>
public sealed record KubernetesVolumePlan(string Claim, string? SourceStorageClass, string? TargetStorageClass, string Path, string Reason, long SizeBytes);

/// <summary>Plans a namespace restore into a cluster (read only): what a restore would check, and what to look at first.</summary>
public interface IKubernetesRestorePlanner
{
    Task<KubernetesRestorePlan> PlanRestoreAsync(
        KubernetesNamespaceDescriptor descriptor, byte[] objects, string targetNamespace, IReadOnlyDictionary<string, string> storageClassMap, bool otherCluster,
        CancellationToken cancellationToken);
}

/// <summary>
/// The preview of a namespace restore: for each PVC its StorageClass in the target and the way its data goes in (as the
/// restore decides it), and warnings about what may not work there — classes or CRDs it lacks, owners on NFS, what files
/// lose, priority and runtime classes it lacks, pods pinned to nodes, Ingress hosts that still point to the old cluster.
/// </summary>
internal static class KubernetesRestorePlanner
{
    private const string FsTypeParameter = "csi.storage.k8s.io/fstype";

    public static async Task<KubernetesRestorePlan> PlanAsync(
        IKubernetes client, string cluster, KubernetesNamespaceDescriptor descriptor, IReadOnlyList<JsonObject> objects, string targetNamespace,
        IReadOnlyDictionary<string, string> storageClassMap, bool otherCluster, CancellationToken cancellationToken)
    {
        var rewrite = new ObjectRewrite(storageClassMap, otherCluster, KubernetesPlatformSet.Recorded(descriptor.Platforms));
        var storage = await KubernetesTargetStorage.ReadAsync(client, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var errors = new List<string>();
        var exists = await ExistsAsync(() => client.CoreV1.ReadNamespaceAsync(targetNamespace, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists)
        {
            warnings.Add($"The namespace {targetNamespace} exists in cluster {cluster}: objects that exist there are left as they are (or replaced, if chosen); a PVC with data that exists stops the restore.");
        }

        var available = storage.ClassNames.Count > 0 ? string.Join(", ", storage.ClassNames) : "none";
        var volumes = new List<KubernetesVolumePlan>();
        foreach (var claim in objects.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && KubernetesObjects.Namespace(o) is not null))
        {
            var name = KubernetesObjects.Name(claim);
            var source = (string?)claim["spec"]?["storageClassName"];
            var target = rewrite.MapClass(source);
            var record = descriptor.Volumes.FirstOrDefault(v => v.Claim == name);
            var size = record?.SizeBytes ?? 0;
            if (record?.DiskKey is null)
            {
                var missing = !string.IsNullOrEmpty(target) && !storage.Has(target)
                    && (otherCluster || target != source || !objects.Any(o => KubernetesObjects.Kind(o) == "StorageClass" && KubernetesObjects.Name(o) == target));
                if (missing)
                {
                    errors.Add($"PVC {name}: the StorageClass {target} does not exist in cluster {cluster} (it has {available}); map {source} to one of them.");
                }

                volumes.Add(new KubernetesVolumePlan(name, source, target, missing ? "missing" : "objects", record?.Reason ?? "its data is not in the restore point", size));
                continue;
            }

            if (storage.Decide(target) is not { } way)
            {
                errors.Add(string.IsNullOrEmpty(target)
                    ? $"PVC {name} has no StorageClass, and cluster {cluster} has no default class (it has {available}): map it to one of them."
                    : $"PVC {name}: the StorageClass {target} does not exist in cluster {cluster} (it has {available}); map {source} to one of them.");
                volumes.Add(new KubernetesVolumePlan(name, source, target, "missing", $"no StorageClass {target} in cluster {cluster}", size));
                continue;
            }

            volumes.Add(new KubernetesVolumePlan(name, source, way.StorageClass, way.Path == VolumeDataPath.Block ? "block" : "files", way.Reason, size));
            var targetClass = storage.Find(way.StorageClass);
            if (way.Path == VolumeDataPath.Files && targetClass?.Provisioner?.Contains("nfs", StringComparison.OrdinalIgnoreCase) == true)
            {
                warnings.Add($"PVC {name} goes onto NFS ({targetClass.Provisioner}): owners are kept only if the export lets root give files to other users (no root_squash); otherwise the files belong to the export's user, and a workload running as another user may not read them.");
            }

            if (way.Path == VolumeDataPath.Block && FsType(objects, claim) is { } fsType
                && targetClass?.Parameters?.TryGetValue(FsTypeParameter, out var targetFsType) == true && !string.Equals(fsType, targetFsType, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"PVC {name}: its volume has {fsType}, the StorageClass {way.StorageClass} formats {targetFsType}; the block copy keeps {fsType}, which the driver may refuse to mount.");
            }
        }

        if (volumes.Any(v => v.Path == "files"))
        {
            warnings.Add("Volumes that go in as files are read from their file system (ext or XFS only): hard links become copies, sparse files are written in full, extended attributes and ACLs stay behind.");
        }

        await CheckWorkloadsAsync(client, cluster, objects, otherCluster, warnings, cancellationToken).ConfigureAwait(false);
        await CheckDefinitionsAsync(client, cluster, objects, rewrite, warnings, cancellationToken).ConfigureAwait(false);

        // What the source cluster's platforms made: left out of another cluster, and what they make in every namespace
        // out of any restore. One line per platform.
        var platforms = rewrite.Platforms;
        foreach (var group in objects
                     .Where(o => KubernetesObjects.Kind(o) != "CustomResourceDefinition" && KubernetesObjects.Namespace(o) is not null && !KubernetesObjects.IsControlled(o))
                     .Select(o => (Object: o, Made: platforms.MadeBy(o)))
                     .Where(m => m.Made is { } made && (otherCluster || made.Always))
                     .GroupBy(m => m.Made!.Profile))
        {
            var kinds = string.Join(", ", group.GroupBy(m => KubernetesObjects.Kind(m.Object)).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
            warnings.Add($"{group.Count()} object(s) made by {group.Key.Name} stay out ({kinds}): {group.Key.Name} makes them again{(otherCluster ? " where it runs" : string.Empty)}.");
        }

        return new KubernetesRestorePlan(cluster, targetNamespace, otherCluster, exists, storage.ClassNames, storage.DefaultClass, volumes, warnings, errors);
    }

    /// <summary>Priority and runtime classes the target lacks, pods pinned to nodes, Ingress hosts and classes, fixed load balancer addresses.</summary>
    private static async Task CheckWorkloadsAsync(IKubernetes client, string cluster, IReadOnlyList<JsonObject> objects, bool otherCluster, List<string> warnings, CancellationToken cancellationToken)
    {
        var priorities = await NamesAsync(async () => (await client.SchedulingV1.ListPriorityClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Select(c => c.Metadata.Name))
            .ConfigureAwait(false);
        var runtimes = await NamesAsync(async () => (await client.NodeV1.ListRuntimeClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Select(c => c.Metadata.Name))
            .ConfigureAwait(false);
        var ingressClasses = await NamesAsync(async () => (await client.NetworkingV1.ListIngressClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Select(c => c.Metadata.Name))
            .ConfigureAwait(false);
        foreach (var item in objects.Where(o => KubernetesObjects.Namespace(o) is not null && !KubernetesObjects.IsControlled(o)))
        {
            var key = KubernetesObjects.Key(item);
            if (PodSpec(item) is { } pod)
            {
                if ((string?)pod["priorityClassName"] is { Length: > 0 } priority && priorities is not null && !priorities.Contains(priority))
                {
                    warnings.Add($"{key} uses the PriorityClass {priority}, which cluster {cluster} does not have: its pods would be refused.");
                }

                if ((string?)pod["runtimeClassName"] is { Length: > 0 } runtime && runtimes is not null && !runtimes.Contains(runtime))
                {
                    warnings.Add($"{key} uses the RuntimeClass {runtime}, which cluster {cluster} does not have: its pods would be refused.");
                }

                if (otherCluster && PinnedNodes(pod) is { Count: > 0 } nodes)
                {
                    warnings.Add($"{key} is pinned to the node(s) {string.Join(", ", nodes)} of the old cluster (nodeSelector or affinity on kubernetes.io/hostname): its pods may not be scheduled.");
                }
            }

            if (KubernetesObjects.Kind(item) == "Ingress")
            {
                if ((string?)item["spec"]?["ingressClassName"] is { Length: > 0 } ingressClass && ingressClasses is not null && !ingressClasses.Contains(ingressClass))
                {
                    warnings.Add($"{key} uses the IngressClass {ingressClass}, which cluster {cluster} does not have.");
                }

                var hosts = (item["spec"]?["rules"] as JsonArray)?.Select(r => (string?)r?["host"]).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
                if (otherCluster && hosts.Count > 0)
                {
                    warnings.Add($"{key} serves {string.Join(", ", hosts)}: their DNS names still point to the old cluster.");
                }
            }

            if (otherCluster && KubernetesObjects.Kind(item) == "Service" && ((string?)item["spec"]?["loadBalancerIP"] is { Length: > 0 } || item["spec"]?["externalIPs"] is JsonArray { Count: > 0 }))
            {
                warnings.Add($"{key} has fixed addresses of the old cluster's network (loadBalancerIP, externalIPs); they are left out.");
            }
        }
    }

    /// <summary>CRDs the restore point brings that the target lacks: their objects come back, their operators do not.</summary>
    private static async Task CheckDefinitionsAsync(
        IKubernetes client, string cluster, IReadOnlyList<JsonObject> objects, ObjectRewrite rewrite, List<string> warnings, CancellationToken cancellationToken)
    {
        // CRDs whose objects all stay out (a platform's records, e.g. Rancher's) are not needed.
        foreach (var definition in objects.Where(o => KubernetesObjects.Kind(o) == "CustomResourceDefinition" && !LeftOutKind(o, rewrite)))
        {
            var name = KubernetesObjects.Name(definition);
            if (await ExistsAsync(() => client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(name, cancellationToken: cancellationToken)).ConfigureAwait(false))
            {
                continue;
            }

            // A platform's CRDs come with the platform, never from a backup.
            var group = (string?)definition["spec"]?["group"] ?? string.Empty;
            if (rewrite.Platforms.CrdGroupOf(group) is { } platform)
            {
                var kind = (string?)definition["spec"]?["names"]?["kind"] ?? string.Empty;
                var count = objects.Count(o => KubernetesObjects.Kind(o) == kind && KubernetesObjects.Namespace(o) is not null);
                if (count > 0)
                {
                    warnings.Add($"{count} {kind} object(s) stay out: their API {group} comes with {platform.Name}, which cluster {cluster} does not have.");
                }

                continue;
            }

            warnings.Add($"The CRD {name} is created in cluster {cluster} from the restore point; the operator or controller that acts on its objects is not part of it.");
        }
    }

    /// <summary>Whether every object of a CRD's kind stays out of this restore (made by a platform of the source cluster).</summary>
    internal static bool LeftOutKind(JsonObject definition, ObjectRewrite rewrite) =>
        (string?)definition["spec"]?["group"] is { } group && (string?)definition["spec"]?["names"]?["kind"] is { } kind
        && rewrite.Platforms.MakesKind(group, "v1", kind) is { } made && (rewrite.OtherCluster || made.Always);

    /// <summary>The pod spec of a workload (its template) or pod.</summary>
    private static JsonObject? PodSpec(JsonObject item) => KubernetesObjects.Kind(item) switch
    {
        "Pod" => item["spec"] as JsonObject,
        "CronJob" => item["spec"]?["jobTemplate"]?["spec"]?["template"]?["spec"] as JsonObject,
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "ReplicationController" or "Job" => item["spec"]?["template"]?["spec"] as JsonObject,
        _ => null,
    };

    /// <summary>Host names a pod spec is pinned to by nodeSelector or required node affinity.</summary>
    private static List<string> PinnedNodes(JsonObject pod)
    {
        var nodes = new List<string>();
        if ((string?)pod["nodeSelector"]?["kubernetes.io/hostname"] is { } selected)
        {
            nodes.Add(selected);
        }

        foreach (var term in (pod["affinity"]?["nodeAffinity"]?["requiredDuringSchedulingIgnoredDuringExecution"]?["nodeSelectorTerms"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            foreach (var expression in (term["matchExpressions"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if ((string?)expression["key"] == "kubernetes.io/hostname" && (string?)expression["operator"] == "In")
                {
                    nodes.AddRange((expression["values"] as JsonArray)?.Select(v => (string?)v).OfType<string>() ?? []);
                }
            }
        }

        return nodes;
    }

    /// <summary>The file system type of a PVC's volume as backed up (its PV's CSI fsType), if known.</summary>
    private static string? FsType(IReadOnlyList<JsonObject> objects, JsonObject claim) =>
        (string?)claim["spec"]?["volumeName"] is { } pv
            ? (string?)objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "PersistentVolume" && KubernetesObjects.Name(o) == pv)?["spec"]?["csi"]?["fsType"]
            : null;

    /// <summary>The names a list returns; null when the account may not list them or the API is not served.</summary>
    private static async Task<HashSet<string>?> NamesAsync(Func<Task<IEnumerable<string>>> list)
    {
        try
        {
            return [.. await list().ConfigureAwait(false)];
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Task<bool> ExistsAsync<T>(Func<Task<T>> read) => VolumeSnapshotFlow.ExistsAsync(read);
}
