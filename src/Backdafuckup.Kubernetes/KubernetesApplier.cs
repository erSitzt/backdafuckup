using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// Reads, creates, replaces and deletes an object of any kind given as JSON: the core kinds through their typed calls, the
/// grouped kinds and custom resources through the generic API (their plural from <paramref name="plurals"/>).
/// </summary>
/// <param name="plurals">Kind → plural for grouped kinds (built-in and from the CRDs of the restore point).</param>
internal sealed class KubernetesApplier(IKubernetes client, IReadOnlyDictionary<string, string> plurals)
{
    private delegate Task<object> Create(IKubernetes c, string json, string? ns, CancellationToken ct);

    private delegate Task<object> Read(IKubernetes c, string? ns, string name, CancellationToken ct);

    private delegate Task<object> Replace(IKubernetes c, string json, string? ns, string name, CancellationToken ct);

    private delegate Task Delete(IKubernetes c, string? ns, string name, CancellationToken ct);

    private sealed record CoreKind(Create Create, Read Read, Replace Replace, Delete Delete);

    private static readonly Dictionary<string, CoreKind> Core = new(StringComparer.Ordinal)
    {
        ["Namespace"] = new(
            async (c, j, _, ct) => await c.CoreV1.CreateNamespaceAsync(D<V1Namespace>(j), cancellationToken: ct).ConfigureAwait(false),
            async (c, _, n, ct) => await c.CoreV1.ReadNamespaceAsync(n, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, _, n, ct) => await c.CoreV1.ReplaceNamespaceAsync(D<V1Namespace>(j), n, cancellationToken: ct).ConfigureAwait(false),
            (c, _, n, ct) => c.CoreV1.DeleteNamespaceAsync(n, cancellationToken: ct)),
        ["PersistentVolume"] = new(
            async (c, j, _, ct) => await c.CoreV1.CreatePersistentVolumeAsync(D<V1PersistentVolume>(j), cancellationToken: ct).ConfigureAwait(false),
            async (c, _, n, ct) => await c.CoreV1.ReadPersistentVolumeAsync(n, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, _, n, ct) => await c.CoreV1.ReplacePersistentVolumeAsync(D<V1PersistentVolume>(j), n, cancellationToken: ct).ConfigureAwait(false),
            (c, _, n, ct) => c.CoreV1.DeletePersistentVolumeAsync(n, cancellationToken: ct)),
        ["ConfigMap"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedConfigMapAsync(D<V1ConfigMap>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedConfigMapAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedConfigMapAsync(D<V1ConfigMap>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedConfigMapAsync(n, ns, cancellationToken: ct)),
        ["Secret"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedSecretAsync(D<V1Secret>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedSecretAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedSecretAsync(D<V1Secret>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedSecretAsync(n, ns, cancellationToken: ct)),
        ["ServiceAccount"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedServiceAccountAsync(D<V1ServiceAccount>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedServiceAccountAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedServiceAccountAsync(D<V1ServiceAccount>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedServiceAccountAsync(n, ns, cancellationToken: ct)),
        ["Service"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedServiceAsync(D<V1Service>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedServiceAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedServiceAsync(D<V1Service>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedServiceAsync(n, ns, cancellationToken: ct)),
        ["PersistentVolumeClaim"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(D<V1PersistentVolumeClaim>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedPersistentVolumeClaimAsync(D<V1PersistentVolumeClaim>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(n, ns, cancellationToken: ct)),
        ["Pod"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedPodAsync(D<V1Pod>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedPodAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedPodAsync(D<V1Pod>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedPodAsync(n, ns, cancellationToken: ct)),
        ["ReplicationController"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedReplicationControllerAsync(D<V1ReplicationController>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedReplicationControllerAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedReplicationControllerAsync(D<V1ReplicationController>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedReplicationControllerAsync(n, ns, cancellationToken: ct)),
        ["LimitRange"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedLimitRangeAsync(D<V1LimitRange>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedLimitRangeAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedLimitRangeAsync(D<V1LimitRange>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedLimitRangeAsync(n, ns, cancellationToken: ct)),
        ["ResourceQuota"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedResourceQuotaAsync(D<V1ResourceQuota>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedResourceQuotaAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedResourceQuotaAsync(D<V1ResourceQuota>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedResourceQuotaAsync(n, ns, cancellationToken: ct)),
        ["PodTemplate"] = new(
            async (c, j, ns, ct) => await c.CoreV1.CreateNamespacedPodTemplateAsync(D<V1PodTemplate>(j), ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, ns, n, ct) => await c.CoreV1.ReadNamespacedPodTemplateAsync(n, ns, cancellationToken: ct).ConfigureAwait(false),
            async (c, j, ns, n, ct) => await c.CoreV1.ReplaceNamespacedPodTemplateAsync(D<V1PodTemplate>(j), n, ns, cancellationToken: ct).ConfigureAwait(false),
            (c, ns, n, ct) => c.CoreV1.DeleteNamespacedPodTemplateAsync(n, ns, cancellationToken: ct)),
    };

    /// <summary>The built-in grouped kinds and their plurals (custom resources come from their CRDs).</summary>
    public static Dictionary<string, string> BuiltInPlurals() => new(StringComparer.Ordinal)
    {
        ["Deployment"] = "deployments",
        ["StatefulSet"] = "statefulsets",
        ["DaemonSet"] = "daemonsets",
        ["ReplicaSet"] = "replicasets",
        ["Job"] = "jobs",
        ["CronJob"] = "cronjobs",
        ["Ingress"] = "ingresses",
        ["NetworkPolicy"] = "networkpolicies",
        ["PodDisruptionBudget"] = "poddisruptionbudgets",
        ["Role"] = "roles",
        ["RoleBinding"] = "rolebindings",
        ["HorizontalPodAutoscaler"] = "horizontalpodautoscalers",
        ["StorageClass"] = "storageclasses",
        ["CustomResourceDefinition"] = "customresourcedefinitions",
        ["ClusterRole"] = "clusterroles",
        ["ClusterRoleBinding"] = "clusterrolebindings",
        ["PriorityClass"] = "priorityclasses",
        ["IngressClass"] = "ingressclasses",
        ["RuntimeClass"] = "runtimeclasses",
        ["VolumeSnapshotClass"] = "volumesnapshotclasses",
        ["ValidatingWebhookConfiguration"] = "validatingwebhookconfigurations",
        ["MutatingWebhookConfiguration"] = "mutatingwebhookconfigurations",
    };

    /// <summary>The object as the cluster has it; null when it does not exist.</summary>
    public async Task<JsonObject?> ReadAsync(JsonObject o, CancellationToken cancellationToken)
    {
        try
        {
            object live;
            if (Core.TryGetValue(KubernetesObjects.Kind(o), out var core) && Group(o).Length == 0)
            {
                live = await core.Read(client, KubernetesObjects.Namespace(o), KubernetesObjects.Name(o), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // By a list with a field selector, not a get: the account may list everything but get only what it reads by
                // name itself (KubernetesSetup.ReadByName), so that no rule grants the proxy subresources.
                var (group, version, plural, ns) = Grouped(o);
                var selector = $"metadata.name={KubernetesObjects.Name(o)}";
                var list = ns is null
                    ? await client.CustomObjects.ListClusterCustomObjectAsync(group, version, plural, fieldSelector: selector, cancellationToken: cancellationToken).ConfigureAwait(false)
                    : await client.CustomObjects.ListNamespacedCustomObjectAsync(group, version, ns, plural, fieldSelector: selector, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (KubernetesResources.Items(list).OfType<JsonObject>().FirstOrDefault() is not { } found)
                {
                    return null;
                }

                var item = (JsonObject)found.DeepClone();

                // Items of a list of built-in kinds carry no apiVersion and kind.
                item["apiVersion"] ??= (string?)o["apiVersion"];
                item["kind"] ??= KubernetesObjects.Kind(o);
                return item;
            }

            return JsonNode.Parse(live is JsonElement element ? element.GetRawText() : KubernetesJson.Serialize(live))!.AsObject();
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CreateAsync(JsonObject o, CancellationToken cancellationToken)
    {
        var ns = KubernetesObjects.Namespace(o);
        if (Core.TryGetValue(KubernetesObjects.Kind(o), out var core) && Group(o).Length == 0)
        {
            await core.Create(client, o.ToJsonString(), ns, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (group, version, plural, scope) = Grouped(o);
        _ = scope is null
            ? await client.CustomObjects.CreateClusterCustomObjectAsync(o, group, version, plural, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await client.CustomObjects.CreateNamespacedCustomObjectAsync(o, group, version, scope, plural, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the live object with <paramref name="o"/> (with the live resourceVersion, as an update requires).</summary>
    public async Task ReplaceAsync(JsonObject o, string resourceVersion, CancellationToken cancellationToken)
    {
        var copy = (JsonObject)o.DeepClone();
        copy["metadata"]!["resourceVersion"] = resourceVersion;
        var ns = KubernetesObjects.Namespace(copy);
        var name = KubernetesObjects.Name(copy);
        if (Core.TryGetValue(KubernetesObjects.Kind(copy), out var core) && Group(copy).Length == 0)
        {
            await core.Replace(client, copy.ToJsonString(), ns, name, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (group, version, plural, scope) = Grouped(copy);
        _ = scope is null
            ? await client.CustomObjects.ReplaceClusterCustomObjectAsync(copy, group, version, plural, name, cancellationToken: cancellationToken).ConfigureAwait(false)
            : await client.CustomObjects.ReplaceNamespacedCustomObjectAsync(copy, group, version, scope, plural, name, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the object; one already gone counts as deleted.</summary>
    public async Task DeleteAsync(JsonObject o, CancellationToken cancellationToken)
    {
        try
        {
            if (Core.TryGetValue(KubernetesObjects.Kind(o), out var core) && Group(o).Length == 0)
            {
                await core.Delete(client, KubernetesObjects.Namespace(o), KubernetesObjects.Name(o), cancellationToken).ConfigureAwait(false);
                return;
            }

            var (group, version, plural, ns) = Grouped(o);
            _ = ns is null
                ? await client.CustomObjects.DeleteClusterCustomObjectAsync(group, version, plural, KubernetesObjects.Name(o), cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.DeleteNamespacedCustomObjectAsync(group, version, ns, plural, KubernetesObjects.Name(o), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone already.
        }
    }

    private static string Group(JsonObject o) => ((string?)o["apiVersion"] ?? "v1") is var apiVersion && apiVersion.IndexOf('/', StringComparison.Ordinal) is > 0 and var slash
        ? apiVersion[..slash]
        : string.Empty;

    private (string Group, string Version, string Plural, string? Namespace) Grouped(JsonObject o)
    {
        var apiVersion = (string?)o["apiVersion"] ?? string.Empty;
        var slash = apiVersion.IndexOf('/', StringComparison.Ordinal);
        var kind = KubernetesObjects.Kind(o);
        if (slash <= 0 || !plurals.TryGetValue(kind, out var plural))
        {
            throw new KubernetesOperationException($"The kind {kind} ({apiVersion}) of {KubernetesObjects.Name(o)} is unknown to the restore (no CRD for it in the restore point or the cluster).");
        }

        return (apiVersion[..slash], apiVersion[(slash + 1)..], plural, KubernetesObjects.Namespace(o));
    }

    private static T D<T>(string json) => KubernetesJson.Deserialize<T>(json);
}
