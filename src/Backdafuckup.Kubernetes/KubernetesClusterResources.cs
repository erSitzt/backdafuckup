using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The cluster-scoped objects of a whole-cluster backup (spec 2026-11-11-1343, phase 2), in the blob format of a
/// namespace's (<see cref="KubernetesResources.Format"/>): the CRDs and their cluster-scoped objects, the ClusterRoles and
/// ClusterRoleBindings people made, and the classes (storage, snapshots, ingress, priority, runtime); the webhook
/// configurations only for the record (a restore lists them, it never creates them). Left out is what the cluster's
/// platforms make themselves (<see cref="KubernetesPlatformSet"/>: system roles and defaults, a distribution's, manager's
/// or addon's roles, webhooks and CRDs), the snapshot API's CRDs, and backdafuckup's own objects.
/// </summary>
internal static class KubernetesClusterResources
{
    /// <summary>The cluster-scoped kinds besides CRDs, as the generic API serves them.</summary>
    private static readonly (string Group, string Version, string Plural, string Kind)[] Kinds =
    [
        ("rbac.authorization.k8s.io", "v1", "clusterroles", "ClusterRole"),
        ("rbac.authorization.k8s.io", "v1", "clusterrolebindings", "ClusterRoleBinding"),
        ("storage.k8s.io", "v1", "storageclasses", "StorageClass"),
        ("scheduling.k8s.io", "v1", "priorityclasses", "PriorityClass"),
        ("networking.k8s.io", "v1", "ingressclasses", "IngressClass"),
        ("node.k8s.io", "v1", "runtimeclasses", "RuntimeClass"),
        (VolumeSnapshotFlow.Group, "v1", "volumesnapshotclasses", "VolumeSnapshotClass"),
        ("admissionregistration.k8s.io", "v1", "validatingwebhookconfigurations", "ValidatingWebhookConfiguration"),
        ("admissionregistration.k8s.io", "v1", "mutatingwebhookconfigurations", "MutatingWebhookConfiguration"),
    ];

    public static async Task<KubernetesResources.Collected> CollectAsync(
        IKubernetes client, string cluster, KubernetesPlatformSet platforms, IOperationLog log, CancellationToken cancellationToken)
    {
        var lines = new List<JsonObject>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skip(string by) => skipped[by] = skipped.GetValueOrDefault(by) + 1;

        async Task<object?> TryAsync(string what, Func<Task<object>> call)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return null; // Not served in this cluster.
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Forbidden)
            {
                log.Warning($"The {what} of cluster {cluster} could not be read (403: the ServiceAccount may not list them); they are not in the restore point.");
                return null;
            }
            catch (HttpOperationException ex)
            {
                throw new KubernetesOperationException($"Reading the {what} of cluster {cluster} failed: {VolumeSnapshotFlow.Problem(ex)}", ex);
            }
        }

        void AddAll(string apiVersion, string kind, IEnumerable<JsonNode?> items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                // Matched as stored: items of lists of built-in kinds carry no apiVersion and kind until cleaned.
                var cleaned = KubernetesResources.Clean(item, apiVersion, kind);
                if (MadeBy(cleaned, platforms) is { } by)
                {
                    Skip(by);
                    continue;
                }

                lines.Add(cleaned);
            }
        }

        // CRDs, and the cluster-scoped objects of each.
        var definitions = await TryAsync("CustomResourceDefinitions", async () => await client.ApiextensionsV1.ListCustomResourceDefinitionAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false) as k8s.Models.V1CustomResourceDefinitionList;
        foreach (var crd in definitions?.Items ?? [])
        {
            var group = crd.Spec.Group;
            // The snapshot API's (the cluster's own machinery, never from a backup), and what the platforms install.
            if (group == VolumeSnapshotFlow.Group)
            {
                Skip("Kubernetes");
                continue;
            }

            if (platforms.CrdGroupOf(group) is { } platform)
            {
                Skip(platform.Name);
                continue;
            }

            AddAll("apiextensions.k8s.io/v1", "CustomResourceDefinition", [KubernetesResources.Node(crd)]);
            if (crd.Spec.Scope != "Cluster")
            {
                continue;
            }

            var version = crd.Spec.Versions.FirstOrDefault(v => v.Storage)?.Name ?? crd.Spec.Versions.FirstOrDefault(v => v.Served)?.Name;
            if (version is not null)
            {
                var list = await TryAsync(crd.Spec.Names.Plural, () => client.CustomObjects.ListClusterCustomObjectAsync(group, version, crd.Spec.Names.Plural, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                AddAll($"{group}/{version}", crd.Spec.Names.Kind, KubernetesResources.Items(list));
            }
        }

        foreach (var (group, version, plural, kind) in Kinds)
        {
            var list = await TryAsync($"{kind}s", () => client.CustomObjects.ListClusterCustomObjectAsync(group, version, plural, cancellationToken: cancellationToken)).ConfigureAwait(false);
            AddAll($"{group}/{version}", kind, KubernetesResources.Items(list));
        }

        var content = new MemoryStream();
        foreach (var line in lines)
        {
            content.Write(Encoding.UTF8.GetBytes(line.ToJsonString()));
            content.WriteByte((byte)'\n');
        }

        var perKind = lines.GroupBy(l => (string)l["kind"]!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        log.Info($"Cluster-scoped objects of {cluster}: {lines.Count} ({string.Join(", ", perKind.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Value} {k.Key}"))}); " +
                 $"{skipped.Values.Sum()} left out, made by the cluster's platforms ({platforms.Display}) or backdafuckup" +
                 (skipped.Count > 0 ? $": {string.Join(", ", skipped.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Value} {s.Key}"))}." : "."));
        return new KubernetesResources.Collected(content.ToArray(), lines.Count, perKind);
    }

    /// <summary>
    /// Who makes a cluster-scoped object itself (and makes it again): backdafuckup (its own roles and objects), or a platform
    /// of the cluster (the base's system roles and defaults, a distribution's or manager's roles, webhooks and records).
    /// </summary>
    internal static string? MadeBy(JsonObject item, KubernetesPlatformSet platforms)
    {
        if ((string?)item["metadata"]?["labels"]?[VolumeSnapshotFlow.ManagedByLabel] == "backdafuckup"
            || (KubernetesObjects.Kind(item) is "ClusterRole" or "ClusterRoleBinding" && KubernetesObjects.Name(item).StartsWith("backdafuckup-", StringComparison.Ordinal)))
        {
            return "backdafuckup";
        }

        return platforms.MadeBy(item)?.Profile.Name;
    }
}
