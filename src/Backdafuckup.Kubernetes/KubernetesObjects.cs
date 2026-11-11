using System.Text.Json.Nodes;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The objects of a namespace's restore point: which belong together (a workload and what it needs, an app), in which
/// order they are restored, and how each is cleaned of what only the old cluster state means (uid, status, cluster IPs).
/// </summary>
internal static class KubernetesObjects
{
    private static readonly HashSet<string> Workloads = new(StringComparer.Ordinal)
    {
        "Deployment", "StatefulSet", "DaemonSet", "ReplicaSet", "ReplicationController", "Job", "CronJob", "Pod",
    };

    public static string Kind(JsonObject o) => (string?)o["kind"] ?? string.Empty;

    public static string Name(JsonObject o) => (string?)o["metadata"]?["name"] ?? string.Empty;

    public static string? Namespace(JsonObject o) => (string?)o["metadata"]?["namespace"];

    public static string Key(JsonObject o) => $"{Kind(o)}/{Name(o)}";

    public static bool IsWorkload(JsonObject o) => Workloads.Contains(Kind(o));

    /// <summary>Made by a controller (a Deployment's ReplicaSet, a StatefulSet's pod, an operator's object): its owner recreates it.</summary>
    public static bool IsControlled(JsonObject o) =>
        o["metadata"]?["ownerReferences"] is JsonArray owners && owners.OfType<JsonObject>().Any(r => (bool?)r["controller"] == true);

    /// <summary>The app an object belongs to: label app.kubernetes.io/instance, else its Helm release, else app.kubernetes.io/name.</summary>
    public static string? App(JsonObject o) =>
        Text(o["metadata"]?["labels"]?["app.kubernetes.io/instance"])
        ?? Text(o["metadata"]?["annotations"]?["meta.helm.sh/release-name"])
        ?? Text(o["metadata"]?["labels"]?["app.kubernetes.io/name"]);

    /// <summary>Objects the cluster makes itself, never restored: ServiceAccount tokens, the root CA ConfigMap, the default ServiceAccount.</summary>
    public static bool IsClusterMade(JsonObject o) => Kind(o) switch
    {
        "Secret" => (string?)o["type"] == "kubernetes.io/service-account-token",
        "ConfigMap" => Name(o) == "kube-root-ca.crt",
        "ServiceAccount" => Name(o) == "default",
        _ => false,
    };

    /// <summary>The order of a restore (as Velero's): definitions and the namespace, storage, configuration, access, services, workloads, pods last.</summary>
    public static int Rank(string kind) => kind switch
    {
        "CustomResourceDefinition" => 0,
        "Namespace" => 1,
        "StorageClass" => 2,
        "PersistentVolume" => 3,
        "PersistentVolumeClaim" => 4,
        "Secret" => 5,
        "ConfigMap" => 6,
        "ServiceAccount" => 7,
        "LimitRange" => 8,
        "ResourceQuota" => 9,
        "Role" => 10,
        "RoleBinding" => 11,
        "Service" => 12,
        "NetworkPolicy" => 13,
        "PodDisruptionBudget" => 14,
        "Deployment" or "StatefulSet" or "DaemonSet" or "ReplicaSet" or "ReplicationController" or "Job" or "CronJob" or "PodTemplate" => 30,
        "HorizontalPodAutoscaler" => 31,
        "Ingress" => 32,
        "Pod" => 40,
        _ => 20, // custom resources: after the configuration they use, before the workloads their operators may make
    };

    /// <summary>
    /// What a workload needs to run (keys <c>Kind/name</c>, only those in <paramref name="all"/>): ConfigMaps and Secrets of
    /// its volumes and environment, pull secrets, its ServiceAccount with the RoleBindings and Roles that name it, its PVCs
    /// (a StatefulSet's from its claim templates), the Services that select its pods and the Ingresses to them, its
    /// HorizontalPodAutoscaler and PodDisruptionBudgets.
    /// </summary>
    public static IReadOnlyList<string> Needs(JsonObject workload, IReadOnlyList<JsonObject> all)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        // What the cluster makes itself (the default ServiceAccount, token secrets) is never restored, so never needed.
        var present = all.Where(o => !IsClusterMade(o)).Select(Key).ToHashSet(StringComparer.Ordinal);
        void Add(string kind, string? name)
        {
            if (!string.IsNullOrEmpty(name) && present.Contains($"{kind}/{name}"))
            {
                keys.Add($"{kind}/{name}");
            }
        }

        var kind = Kind(workload);
        var template = kind switch
        {
            "Pod" => workload,
            "CronJob" => workload["spec"]?["jobTemplate"]?["spec"]?["template"] as JsonObject,
            _ => workload["spec"]?["template"] as JsonObject,
        };
        var pod = template?["spec"] as JsonObject;
        var podLabels = (template?["metadata"]?["labels"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => Text(kv.Value) ?? string.Empty, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var volume in (pod?["volumes"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            Add("ConfigMap", Text(volume["configMap"]?["name"]));
            Add("Secret", Text(volume["secret"]?["secretName"]));
            Add("PersistentVolumeClaim", Text(volume["persistentVolumeClaim"]?["claimName"]));
            foreach (var source in (volume["projected"]?["sources"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                Add("ConfigMap", Text(source["configMap"]?["name"]));
                Add("Secret", Text(source["secret"]?["name"]));
            }
        }

        var containers = ((pod?["containers"] as JsonArray)?.OfType<JsonObject>() ?? []).Concat((pod?["initContainers"] as JsonArray)?.OfType<JsonObject>() ?? []);
        foreach (var container in containers)
        {
            foreach (var from in (container["envFrom"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                Add("ConfigMap", Text(from["configMapRef"]?["name"]));
                Add("Secret", Text(from["secretRef"]?["name"]));
            }

            foreach (var variable in (container["env"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                Add("ConfigMap", Text(variable["valueFrom"]?["configMapKeyRef"]?["name"]));
                Add("Secret", Text(variable["valueFrom"]?["secretKeyRef"]?["name"]));
            }
        }

        foreach (var pull in (pod?["imagePullSecrets"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            Add("Secret", Text(pull["name"]));
        }

        var account = Text(pod?["serviceAccountName"]) ?? "default";
        Add("ServiceAccount", account);
        foreach (var binding in all.Where(o => Kind(o) == "RoleBinding"))
        {
            var subjects = (binding["subjects"] as JsonArray)?.OfType<JsonObject>() ?? [];
            if (subjects.Any(s => (string?)s["kind"] == "ServiceAccount" && (string?)s["name"] == account))
            {
                Add("RoleBinding", Name(binding));
                if ((string?)binding["roleRef"]?["kind"] == "Role")
                {
                    Add("Role", Text(binding["roleRef"]?["name"]));
                }
            }
        }

        // A StatefulSet's PVCs: <template>-<statefulset>-<ordinal>.
        if (kind == "StatefulSet")
        {
            foreach (var claimTemplate in (workload["spec"]?["volumeClaimTemplates"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var prefix = $"{Name(claimTemplate)}-{Name(workload)}-";
                foreach (var claim in all.Where(o => Kind(o) == "PersistentVolumeClaim" && Name(o).StartsWith(prefix, StringComparison.Ordinal)
                                                     && Name(o)[prefix.Length..].All(char.IsAsciiDigit) && Name(o).Length > prefix.Length))
                {
                    keys.Add(Key(claim));
                }
            }
        }

        // Services selecting its pods, Ingresses to them, its autoscaler and disruption budgets.
        var services = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in all.Where(o => Kind(o) == "Service"))
        {
            if (service["spec"]?["selector"] is JsonObject selector && selector.Count > 0 && Selects(selector, podLabels))
            {
                keys.Add(Key(service));
                services.Add(Name(service));
            }
        }

        foreach (var ingress in all.Where(o => Kind(o) == "Ingress"))
        {
            var backends = (ingress["spec"]?["rules"] as JsonArray)?.OfType<JsonObject>()
                .SelectMany(r => (r["http"]?["paths"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Select(p => Text(p["backend"]?["service"]?["name"]))
                .Append(Text(ingress["spec"]?["defaultBackend"]?["service"]?["name"])) ?? [];
            if (backends.Any(b => b is not null && services.Contains(b)))
            {
                keys.Add(Key(ingress));
            }
        }

        foreach (var scaler in all.Where(o => Kind(o) == "HorizontalPodAutoscaler"))
        {
            if ((string?)scaler["spec"]?["scaleTargetRef"]?["kind"] == kind && (string?)scaler["spec"]?["scaleTargetRef"]?["name"] == Name(workload))
            {
                keys.Add(Key(scaler));
            }
        }

        foreach (var budget in all.Where(o => Kind(o) == "PodDisruptionBudget"))
        {
            if (budget["spec"]?["selector"]?["matchLabels"] is JsonObject match && match.Count > 0 && Selects(match, podLabels))
            {
                keys.Add(Key(budget));
            }
        }

        keys.Remove(Key(workload));
        return [.. keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>The selected objects and everything their workloads need.</summary>
    public static HashSet<string> Expand(IEnumerable<string> selected, IReadOnlyList<JsonObject> all)
    {
        var byKey = all.GroupBy(Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in selected)
        {
            if (byKey.TryGetValue(key, out var item))
            {
                result.Add(key);
                if (IsWorkload(item))
                {
                    result.UnionWith(Needs(item, all));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The object as it is created again: without what belongs to its old life in the cluster (uid, resourceVersion,
    /// timestamps, owners, status, managed fields) and with its namespace changed to <paramref name="targetNamespace"/>.
    /// Services lose their cluster IPs and node ports, ServiceAccounts their token secrets, Jobs their generated selector,
    /// pods their node; RoleBindings to ServiceAccounts of the old namespace name the new one.
    /// </summary>
    public static JsonObject Prepare(JsonObject original, string sourceNamespace, string targetNamespace)
    {
        var o = (JsonObject)original.DeepClone();
        o.Remove("status");
        if (o["metadata"] is JsonObject metadata)
        {
            foreach (var field in new[] { "uid", "resourceVersion", "creationTimestamp", "deletionTimestamp", "deletionGracePeriodSeconds", "generation", "managedFields", "selfLink", "ownerReferences" })
            {
                metadata.Remove(field);
            }

            if (metadata["namespace"] is not null)
            {
                metadata["namespace"] = targetNamespace;
            }

            if (Kind(o) == "Namespace")
            {
                metadata["name"] = targetNamespace;
                (metadata["labels"] as JsonObject)?.Remove("kubernetes.io/metadata.name");
            }
        }

        switch (Kind(o))
        {
            case "Service" when o["spec"] is JsonObject spec:
                if ((string?)spec["clusterIP"] != "None")
                {
                    spec.Remove("clusterIP");
                    spec.Remove("clusterIPs");
                }

                spec.Remove("healthCheckNodePort");
                foreach (var port in (spec["ports"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    port.Remove("nodePort");
                }

                break;
            case "ServiceAccount":
                o.Remove("secrets");
                break;
            case "RoleBinding":
                foreach (var subject in (o["subjects"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    if ((string?)subject["namespace"] == sourceNamespace)
                    {
                        subject["namespace"] = targetNamespace;
                    }
                }

                break;
            case "Job" when o["spec"] is JsonObject spec:
                // The selector and its labels are generated per Job: a new Job gets new ones.
                spec.Remove("selector");
                foreach (var labels in new[] { o["metadata"]?["labels"], spec["template"]?["metadata"]?["labels"] }.OfType<JsonObject>())
                {
                    labels.Remove("controller-uid");
                    labels.Remove("batch.kubernetes.io/controller-uid");
                }

                break;
            case "Pod" when o["spec"] is JsonObject spec:
                spec.Remove("nodeName");
                break;
            case "PersistentVolumeClaim" when o["spec"] is JsonObject spec:
                spec.Remove("dataSource");
                spec.Remove("dataSourceRef");
                if (o["metadata"]?["annotations"] is JsonObject annotations)
                {
                    foreach (var key in annotations.Select(kv => kv.Key).Where(k => k.StartsWith("pv.kubernetes.io/", StringComparison.Ordinal)
                                 || k is "volume.beta.kubernetes.io/storage-provisioner" or "volume.kubernetes.io/storage-provisioner" or "volume.kubernetes.io/selected-node").ToList())
                    {
                        annotations.Remove(key);
                    }
                }

                break;
        }

        return o;
    }

    /// <summary>Whether a label selector (match labels) selects pods with <paramref name="labels"/>.</summary>
    private static bool Selects(JsonObject selector, IReadOnlyDictionary<string, string> labels) =>
        selector.All(kv => labels.TryGetValue(kv.Key, out var value) && value == Text(kv.Value));

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

/// <summary>An object of a namespace's restore point as the console lists it.</summary>
/// <param name="Needs">For a workload: what a restore of it brings along (<c>Kind/name</c>).</param>
public sealed record KubernetesObjectInfo(string ApiVersion, string Kind, string Name, string? Namespace, bool Controlled, string? App, IReadOnlyList<string>? Needs);

/// <summary>Reads the objects of a namespace's restore point (its metadata blob) for the console.</summary>
public static class KubernetesRestorePoints
{
    public static IReadOnlyList<KubernetesObjectInfo> Objects(byte[] content)
    {
        var all = KubernetesResources.Read(content).ToList();
        return [.. all
            .Where(o => !KubernetesObjects.IsClusterMade(o))
            .OrderBy(o => KubernetesObjects.Rank(KubernetesObjects.Kind(o))).ThenBy(KubernetesObjects.Key, StringComparer.Ordinal)
            .Select(o => new KubernetesObjectInfo(
                (string?)o["apiVersion"] ?? string.Empty,
                KubernetesObjects.Kind(o),
                KubernetesObjects.Name(o),
                KubernetesObjects.Namespace(o),
                KubernetesObjects.IsControlled(o),
                KubernetesObjects.App(o),
                KubernetesObjects.IsWorkload(o) && !KubernetesObjects.IsControlled(o) ? KubernetesObjects.Needs(o, all) : null))];
    }
}
