using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The objects of a namespace as a restore point keeps them: every object of the built-in kinds and of the custom
/// resources (CRDs) in the namespace, plus the cluster-wide objects it needs (the namespace itself, the PVs of its PVCs,
/// their StorageClasses and the CRDs of its custom resources). One JSON object per line, with apiVersion and kind;
/// <c>metadata.managedFields</c> is left out. Secrets are included.
/// </summary>
internal static class KubernetesResources
{
    public const string Format = "k8s-resources-jsonl-v1";

    /// <summary>Namespaced kinds of the built-in API groups (events, endpoints and leases are left out: the cluster makes them).</summary>
    private static readonly (string Group, string Version, string Plural, string Kind)[] GroupKinds =
    [
        ("apps", "v1", "deployments", "Deployment"),
        ("apps", "v1", "statefulsets", "StatefulSet"),
        ("apps", "v1", "daemonsets", "DaemonSet"),
        ("apps", "v1", "replicasets", "ReplicaSet"),
        ("batch", "v1", "jobs", "Job"),
        ("batch", "v1", "cronjobs", "CronJob"),
        ("networking.k8s.io", "v1", "ingresses", "Ingress"),
        ("networking.k8s.io", "v1", "networkpolicies", "NetworkPolicy"),
        ("policy", "v1", "poddisruptionbudgets", "PodDisruptionBudget"),
        ("rbac.authorization.k8s.io", "v1", "roles", "Role"),
        ("rbac.authorization.k8s.io", "v1", "rolebindings", "RoleBinding"),
        ("autoscaling", "v2", "horizontalpodautoscalers", "HorizontalPodAutoscaler"),
    ];

    /// <summary>Custom resources of these groups belong to the cluster's machinery, not to the application.</summary>
    private static readonly HashSet<string> SkippedGroups = new(StringComparer.Ordinal) { VolumeSnapshotFlow.Group, "metrics.k8s.io" };

    public sealed record Collected(byte[] Content, int Count, IReadOnlyDictionary<string, int> PerKind);

    public static async Task<Collected> CollectAsync(IKubernetes client, string cluster, string ns, IReadOnlyList<NamespaceVolume> volumes, IOperationLog log, CancellationToken cancellationToken)
    {
        var lines = new List<JsonObject>();

        void AddAll(string apiVersion, string kind, IEnumerable<JsonNode?> items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                if ((string?)item["metadata"]?["labels"]?[VolumeSnapshotFlow.ManagedByLabel] == "backdafuckup")
                {
                    continue; // Our own work objects.
                }

                lines.Add(Clean(item, apiVersion, kind));
            }
        }

        async Task<T?> TryAsync<T>(string what, Func<Task<T>> call)
            where T : class
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // The kind is not served in this cluster.
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
            {
                log.Warning($"The {what} of namespace {ns} could not be read in cluster {cluster} (403: the ServiceAccount may not list them); they are not in the restore point.");
                return null;
            }
            catch (HttpOperationException ex)
            {
                throw new KubernetesOperationException($"Reading the {what} of namespace {ns} in cluster {cluster} failed: {VolumeSnapshotFlow.Problem(ex)}", ex);
            }
        }

        // The namespace itself and the core kinds.
        AddAll("v1", "Namespace", [Node(await TryAsync("Namespace object", () => client.CoreV1.ReadNamespaceAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false))]);
        AddAll("v1", "ConfigMap", Items(await TryAsync("ConfigMaps", () => client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "Secret", Items(await TryAsync("Secrets", () => client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "ServiceAccount", Items(await TryAsync("ServiceAccounts", () => client.CoreV1.ListNamespacedServiceAccountAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "Service", Items(await TryAsync("Services", () => client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "PersistentVolumeClaim", Items(await TryAsync("PVCs", () => client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "Pod", Items(await TryAsync("Pods", () => client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "ReplicationController", Items(await TryAsync("ReplicationControllers", () => client.CoreV1.ListNamespacedReplicationControllerAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "LimitRange", Items(await TryAsync("LimitRanges", () => client.CoreV1.ListNamespacedLimitRangeAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "ResourceQuota", Items(await TryAsync("ResourceQuotas", () => client.CoreV1.ListNamespacedResourceQuotaAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));
        AddAll("v1", "PodTemplate", Items(await TryAsync("PodTemplates", () => client.CoreV1.ListNamespacedPodTemplateAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)));

        // The built-in groups, through the generic API (the objects as the server has them).
        foreach (var (group, version, plural, kind) in GroupKinds)
        {
            var list = await TryAsync($"{kind}s", () => client.CustomObjects.ListNamespacedCustomObjectAsync(group, version, ns, plural, cancellationToken: cancellationToken)).ConfigureAwait(false);
            AddAll($"{group}/{version}", kind, Items(list));
        }

        // Custom resources: every namespaced CRD in its storage version; the CRDs with objects here go along.
        var definitions = await TryAsync("CustomResourceDefinitions", () => client.ApiextensionsV1.ListCustomResourceDefinitionAsync(cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var crd in definitions?.Items ?? [])
        {
            if (crd.Spec.Scope != "Namespaced" || SkippedGroups.Contains(crd.Spec.Group))
            {
                continue;
            }

            var version = crd.Spec.Versions.FirstOrDefault(v => v.Storage)?.Name ?? crd.Spec.Versions.FirstOrDefault(v => v.Served)?.Name;
            if (version is null)
            {
                continue;
            }

            var before = lines.Count;
            var list = await TryAsync(crd.Spec.Names.Plural, () => client.CustomObjects.ListNamespacedCustomObjectAsync(crd.Spec.Group, version, ns, crd.Spec.Names.Plural, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            AddAll($"{crd.Spec.Group}/{version}", crd.Spec.Names.Kind, Items(list));
            if (lines.Count > before)
            {
                AddAll("apiextensions.k8s.io/v1", "CustomResourceDefinition", [Node(crd)]);
            }
        }

        // The cluster-wide objects of the volumes: their PVs and StorageClasses.
        foreach (var name in volumes.Select(v => v.VolumeName).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            AddAll("v1", "PersistentVolume", [Node(await TryAsync($"PersistentVolume {name}", () => client.CoreV1.ReadPersistentVolumeAsync(name, cancellationToken: cancellationToken)).ConfigureAwait(false))]);
        }

        // A PVC of a static PV names the class "" on purpose (no class); reading "" would read the list of all classes.
        foreach (var name in volumes.Select(v => v.StorageClass).Where(c => !string.IsNullOrEmpty(c)).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            AddAll("storage.k8s.io/v1", "StorageClass", [Node(await TryAsync($"StorageClass {name}", () => client.StorageV1.ReadStorageClassAsync(name, cancellationToken: cancellationToken)).ConfigureAwait(false))]);
        }

        var content = new MemoryStream();
        foreach (var line in lines)
        {
            content.Write(Encoding.UTF8.GetBytes(line.ToJsonString()));
            content.WriteByte((byte)'\n');
        }

        var perKind = lines.GroupBy(l => (string)l["kind"]!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new Collected(content.ToArray(), lines.Count, perKind);
    }

    /// <summary>The objects of a stored blob, line by line.</summary>
    public static IEnumerable<JsonObject> Read(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                yield return JsonNode.Parse(line)!.AsObject();
            }
        }
    }

    internal static JsonObject Clean(JsonObject item, string apiVersion, string kind)
    {
        var copy = new JsonObject { ["apiVersion"] = apiVersion, ["kind"] = kind };
        foreach (var (key, value) in item)
        {
            if (key is not ("apiVersion" or "kind"))
            {
                copy[key] = value?.DeepClone();
            }
        }

        (copy["metadata"] as JsonObject)?.Remove("managedFields");
        return copy;
    }

    internal static JsonNode? Node(object? value) => value is null ? null : JsonNode.Parse(KubernetesJson.Serialize(value));

    internal static IEnumerable<JsonNode?> Items(object? list) => list switch
    {
        null => [],
        JsonElement element => element.TryGetProperty("items", out var items) ? JsonNode.Parse(items.GetRawText())!.AsArray() : [],
        _ => JsonNode.Parse(KubernetesJson.Serialize(list))?["items"]?.AsArray() ?? [],
    };
}
