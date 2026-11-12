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
/// configurations only for the record (a restore lists them, it never creates them). Left out is what a cluster makes
/// itself or Rancher, Fleet and RKE2 put into it: system roles and defaults, their CRDs, the CNI's, the snapshot API's.
/// </summary>
internal static class KubernetesClusterResources
{
    /// <summary>CRD groups that belong to the cluster's own machinery.</summary>
    private static readonly string[] SkippedCrdGroups =
        [VolumeSnapshotFlow.Group, "crd.projectcalico.org", "projectcalico.org", "operator.tigera.io", "cilium.io", "k3s.cattle.io"];

    /// <summary>The default ClusterRoles of Kubernetes that are not system:-prefixed.</summary>
    private static readonly HashSet<string> DefaultRoles = new(StringComparer.Ordinal) { "admin", "edit", "view", "cluster-admin" };

    /// <summary>Name prefixes of what RKE2, Rancher, Fleet and backdafuckup itself make.</summary>
    private static readonly string[] ManagedPrefixes = ["system:", "rke2-", "cattle-", "fleet-", "rancher-", "backdafuckup-", "cluster-owner", "project-owner"];

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

    public static async Task<KubernetesResources.Collected> CollectAsync(IKubernetes client, string cluster, IOperationLog log, CancellationToken cancellationToken)
    {
        var lines = new List<JsonObject>();
        var skipped = 0;

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
                if (IsManaged(kind, item))
                {
                    skipped++;
                    continue;
                }

                lines.Add(KubernetesResources.Clean(item, apiVersion, kind));
            }
        }

        // CRDs, and the cluster-scoped objects of each.
        var definitions = await TryAsync("CustomResourceDefinitions", async () => await client.ApiextensionsV1.ListCustomResourceDefinitionAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false) as k8s.Models.V1CustomResourceDefinitionList;
        foreach (var crd in definitions?.Items ?? [])
        {
            var group = crd.Spec.Group;
            if (SkippedCrdGroups.Contains(group, StringComparer.Ordinal) || group.EndsWith(".cattle.io", StringComparison.Ordinal) || group == "cattle.io")
            {
                skipped++;
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
                 $"{skipped} of the cluster itself, Rancher, Fleet or RKE2 left out.");
        return new KubernetesResources.Collected(content.ToArray(), lines.Count, perKind);
    }

    /// <summary>What the cluster, RKE2, Rancher, Fleet or backdafuckup make themselves (and make again).</summary>
    internal static bool IsManaged(string kind, JsonObject item)
    {
        var name = (string?)item["metadata"]?["name"] ?? string.Empty;
        var labels = item["metadata"]?["labels"] as JsonObject;
        if ((string?)labels?[VolumeSnapshotFlow.ManagedByLabel] == "backdafuckup")
        {
            return true;
        }

        switch (kind)
        {
            case "ClusterRole" or "ClusterRoleBinding":
                return DefaultRoles.Contains(name)
                       || ManagedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))
                       || (string?)labels?["kubernetes.io/bootstrapping"] == "rbac-defaults"
                       || labels?.Any(kv => kv.Key.Contains("cattle.io", StringComparison.Ordinal)) == true
                       || item["metadata"]?["annotations"] is JsonObject annotations && annotations.Any(kv => kv.Key.Contains("cattle.io", StringComparison.Ordinal));
            case "PriorityClass":
                return name.StartsWith("system-", StringComparison.Ordinal);
            case "ValidatingWebhookConfiguration" or "MutatingWebhookConfiguration":
                return name.StartsWith("rancher.cattle.io", StringComparison.Ordinal) || name.StartsWith("rke2-", StringComparison.Ordinal);
            default:
                return false;
        }
    }
}
