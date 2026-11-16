using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>What the restore of a cluster's cluster-scoped objects did.</summary>
public sealed record KubernetesClusterRestoreResult(int Restored, int Existing, int Skipped, int Failed, IReadOnlyList<string> Webhooks);

/// <summary>Restores the cluster-scoped objects of a whole-cluster backup into a cluster (before the namespaces of the run).</summary>
public interface IKubernetesClusterRestorer
{
    /// <summary>What the account lacks to restore roles and bindings with more rights than its own (bind, escalate); null if nothing.</summary>
    Task<string?> MissingRestoreRightsAsync(CancellationToken cancellationToken);

    /// <param name="sourcePlatforms">The platforms recorded in the restore point (null: from before they were recorded).</param>
    Task<KubernetesClusterRestoreResult> RestoreClusterScopeAsync(
        byte[] objects, IReadOnlyList<string>? sourcePlatforms, IReadOnlyDictionary<string, string> storageClassMap, bool replaceExisting, IOperationLog log,
        CancellationToken cancellationToken);
}

/// <summary>
/// The cluster-scoped objects of a whole-cluster restore (spec 2026-11-11-1343, phase 2), in order: CRDs (waited for
/// until established), ClusterRoles (aggregated ones without their rules, which the cluster fills in), ClusterRoleBindings,
/// the classes, then the cluster-scoped objects of the CRDs. What exists is left as it is (or replaced). Never created:
/// webhook configurations (a stale CA with failurePolicy Fail would block the whole cluster: they are listed, their
/// operators make them again), StorageClasses mapped to the target's, and a second default class.
/// </summary>
internal static class KubernetesClusterRestore
{
    private const string DefaultStorageClass = "storageclass.kubernetes.io/is-default-class";
    private const string DefaultIngressClass = "ingressclass.kubernetes.io/is-default-class";
    private const string DefaultSnapshotClass = "snapshot.storage.kubernetes.io/is-default-class";

    public static async Task<string?> MissingRightsAsync(IKubernetes client, string cluster, CancellationToken cancellationToken)
    {
        foreach (var verb in new[] { "escalate", "bind" })
        {
            V1SelfSubjectAccessReview review;
            try
            {
                review = await client.AuthorizationV1.CreateSelfSubjectAccessReviewAsync(
                    new V1SelfSubjectAccessReview
                    {
                        Spec = new V1SelfSubjectAccessReviewSpec { ResourceAttributes = new V1ResourceAttributes { Group = "rbac.authorization.k8s.io", Resource = "clusterroles", Verb = verb } },
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException ex)
            {
                return $"The rights of the account in cluster {cluster} could not be checked: {VolumeSnapshotFlow.Problem(ex)}";
            }

            if (review.Status is { Allowed: false })
            {
                return $"The account may not {verb} ClusterRoles in cluster {cluster}: apply the setup manifest of this version again (its restore role has bind and escalate), " +
                       "or restore the namespaces one by one.";
            }
        }

        return null;
    }

    public static async Task<KubernetesClusterRestoreResult> RestoreAsync(
        IKubernetes client, string cluster, IReadOnlyList<JsonObject> objects, KubernetesPlatformSet source, IReadOnlyDictionary<string, string> storageClassMap,
        bool replaceExisting, IOperationLog log, KubernetesTimings timings, CancellationToken cancellationToken)
    {
        var plurals = KubernetesApplier.BuiltInPlurals();
        foreach (var crd in objects.Where(o => KubernetesObjects.Kind(o) == "CustomResourceDefinition"))
        {
            if ((string?)crd["spec"]?["names"]?["kind"] is { } kind && (string?)crd["spec"]?["names"]?["plural"] is { } plural)
            {
                plurals[kind] = plural;
            }
        }

        var applier = new KubernetesApplier(client, plurals);
        var rewrite = new ObjectRewrite(new Dictionary<string, string>(StringComparer.Ordinal), OtherCluster: true, Source: source);
        var restored = 0;
        var existing = 0;
        var skipped = 0;
        var failed = 0;
        var webhooks = new List<string>();
        var defaults = await DefaultsAsync(client, cancellationToken).ConfigureAwait(false);
        var servesSnapshots = !objects.Any(o => KubernetesObjects.Kind(o) == "VolumeSnapshotClass") || await ServesSnapshotsAsync(client, cancellationToken).ConfigureAwait(false);

        async Task<bool> PutAsync(JsonObject original)
        {
            var key = KubernetesObjects.Key(original);
            var prepared = KubernetesObjects.Prepare(original, string.Empty, string.Empty, rewrite);
            if (KubernetesObjects.Kind(prepared) == "ClusterRole" && prepared["aggregationRule"] is not null)
            {
                prepared.Remove("rules"); // the cluster aggregates them from the labelled roles
            }

            try
            {
                var live = await applier.ReadAsync(prepared, cancellationToken).ConfigureAwait(false);
                if (live is null)
                {
                    await applier.CreateAsync(prepared, cancellationToken).ConfigureAwait(false);
                    restored++;
                    return true;
                }

                if (replaceExisting && (string?)live["metadata"]?["resourceVersion"] is { } version)
                {
                    await applier.ReplaceAsync(prepared, version, cancellationToken).ConfigureAwait(false);
                    restored++;
                    return true;
                }

                existing++;
                return false;
            }
            catch (Exception ex) when (ex is HttpOperationException or KubernetesOperationException)
            {
                failed++;
                log.Warning($"{key} could not be restored in cluster {cluster}: {(ex is HttpOperationException http ? VolumeSnapshotFlow.Problem(http) : ex.Message)}");
                return false;
            }
        }

        // CRDs first, and established before their objects come.
        var definitions = new List<JsonObject>();
        foreach (var definition in objects.Where(o => KubernetesObjects.Kind(o) == "CustomResourceDefinition"))
        {
            if (await PutAsync(definition).ConfigureAwait(false))
            {
                definitions.Add(definition);
            }
        }

        await WaitEstablishedAsync(applier, cluster, definitions, log, timings, cancellationToken).ConfigureAwait(false);
        foreach (var kind in new[] { "ClusterRole", "ClusterRoleBinding", "StorageClass", "VolumeSnapshotClass", "PriorityClass", "IngressClass", "RuntimeClass" })
        {
            foreach (var item in objects.Where(o => KubernetesObjects.Kind(o) == kind))
            {
                var name = KubernetesObjects.Name(item);
                if (kind == "StorageClass" && storageClassMap.TryGetValue(name, out var mapped) && !string.IsNullOrWhiteSpace(mapped) && mapped != name)
                {
                    skipped++;
                    log.Info($"StorageClass {name} is not created: its volumes go into the StorageClass {mapped} of cluster {cluster}.");
                    continue;
                }

                if (kind == "VolumeSnapshotClass" && !servesSnapshots)
                {
                    skipped++;
                    log.Info($"VolumeSnapshotClass {name} is not created: cluster {cluster} does not serve the snapshot API ({VolumeSnapshotFlow.Group}/v1), so its volumes are restored as files.");
                    continue;
                }

                if (SecondDefault(item, defaults) is { } other)
                {
                    skipped++;
                    log.Warning($"{KubernetesObjects.Key(item)} is not created: it is marked as the default, and cluster {cluster} has its default {kind} {other} already.");
                    continue;
                }

                if (await PutAsync(item).ConfigureAwait(false) && DefaultKey(item) is { } marked)
                {
                    defaults.TryAdd(marked, name); // the backup's second default of this kind stays out as well
                }
            }
        }

        // The cluster-scoped objects of the CRDs, after everything they may refer to.
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "CustomResourceDefinition", "ClusterRole", "ClusterRoleBinding", "StorageClass", "VolumeSnapshotClass", "PriorityClass", "IngressClass", "RuntimeClass",
            "ValidatingWebhookConfiguration", "MutatingWebhookConfiguration",
        };
        foreach (var item in objects.Where(o => !known.Contains(KubernetesObjects.Kind(o))))
        {
            await PutAsync(item).ConfigureAwait(false);
        }

        foreach (var hook in objects.Where(o => KubernetesObjects.Kind(o) is "ValidatingWebhookConfiguration" or "MutatingWebhookConfiguration"))
        {
            var services = (hook["webhooks"] as JsonArray)?.OfType<JsonObject>()
                .Select(w => w["clientConfig"]?["service"] is JsonObject service ? $"{(string?)service["namespace"]}/{(string?)service["name"]}" : (string?)w["clientConfig"]?["url"])
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList() ?? [];
            webhooks.Add($"{KubernetesObjects.Key(hook)}{(services.Count > 0 ? $" (calls {string.Join(", ", services)})" : string.Empty)}");
        }

        if (webhooks.Count > 0)
        {
            log.Info($"Webhook configurations are not restored (with a stale CA they would block the whole cluster); their operators make them again: {string.Join("; ", webhooks)}.");
        }

        log.Info($"Cluster-scoped objects in cluster {cluster}: {restored} restored, {existing} existed and were left as they are, {skipped} not created, {failed} failed.");
        return new KubernetesClusterRestoreResult(restored, existing, skipped, failed, webhooks);
    }

    /// <summary>Whether the target serves the snapshot API (its CRDs come with the snapshot controller, never from a backup).</summary>
    private static async Task<bool> ServesSnapshotsAsync(IKubernetes client, CancellationToken cancellationToken)
    {
        try
        {
            await client.CustomObjects.ListClusterCustomObjectAsync(VolumeSnapshotFlow.Group, "v1", "volumesnapshotclasses", limit: 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (HttpOperationException)
        {
            return true; // Served, but not to be listed: creating the class says what is wrong.
        }
    }

    /// <summary>The default classes the target has: storage, ingress, priority (global default), snapshot per driver.</summary>
    private static async Task<Dictionary<string, string>> DefaultsAsync(IKubernetes client, CancellationToken cancellationToken)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task ReadAsync(Func<Task> read)
        {
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                // Not served, or not to be read: no default known.
            }
        }

        await ReadAsync(async () =>
        {
            foreach (var c in (await client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Where(c => IsMarked(c.Metadata.Annotations, DefaultStorageClass)))
            {
                defaults["StorageClass"] = c.Metadata.Name;
            }
        }).ConfigureAwait(false);
        await ReadAsync(async () =>
        {
            foreach (var c in (await client.NetworkingV1.ListIngressClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Where(c => IsMarked(c.Metadata.Annotations, DefaultIngressClass)))
            {
                defaults["IngressClass"] = c.Metadata.Name;
            }
        }).ConfigureAwait(false);
        await ReadAsync(async () =>
        {
            foreach (var c in (await client.SchedulingV1.ListPriorityClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Where(c => c.GlobalDefault == true))
            {
                defaults["PriorityClass"] = c.Metadata.Name;
            }
        }).ConfigureAwait(false);
        foreach (var (name, driver, isDefault) in await KubernetesInventory.SnapshotClassesAsync(client, cancellationToken).ConfigureAwait(false))
        {
            if (isDefault)
            {
                defaults[$"VolumeSnapshotClass/{driver}"] = name;
            }
        }

        return defaults;
    }

    /// <summary>The target's other default of the same kind, when <paramref name="item"/> is marked as default too.</summary>
    private static string? SecondDefault(JsonObject item, Dictionary<string, string> defaults) =>
        DefaultKey(item) is { } key && defaults.TryGetValue(key, out var other) && other != KubernetesObjects.Name(item) ? other : null;

    /// <summary>The default <paramref name="item"/> is marked as (a snapshot class per driver), or null when it is none.</summary>
    private static string? DefaultKey(JsonObject item)
    {
        var annotations = item["metadata"]?["annotations"] as JsonObject;
        var (marked, key) = KubernetesObjects.Kind(item) switch
        {
            "StorageClass" => (IsMarked(annotations, DefaultStorageClass), "StorageClass"),
            "IngressClass" => (IsMarked(annotations, DefaultIngressClass), "IngressClass"),
            "PriorityClass" => ((bool?)item["globalDefault"] == true, "PriorityClass"),
            "VolumeSnapshotClass" => (IsMarked(annotations, DefaultSnapshotClass), $"VolumeSnapshotClass/{(string?)item["driver"]}"),
            _ => (false, string.Empty),
        };
        return marked ? key : null;
    }

    private static bool IsMarked(IDictionary<string, string>? annotations, string key) =>
        annotations?.TryGetValue(key, out var value) == true && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsMarked(JsonObject? annotations, string key) =>
        string.Equals((string?)annotations?[key], "true", StringComparison.OrdinalIgnoreCase);

    private static async Task WaitEstablishedAsync(
        KubernetesApplier applier, string cluster, IReadOnlyList<JsonObject> definitions, IOperationLog log, KubernetesTimings timings, CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                var live = await applier.ReadAsync(definition, cancellationToken).ConfigureAwait(false);
                if ((live?["status"]?["conditions"] as JsonArray)?.OfType<JsonObject>()
                    .Any(c => (string?)c["type"] == "Established" && (string?)c["status"] == "True") == true)
                {
                    break;
                }

                if (DateTimeOffset.UtcNow - started > timings.CrdEstablished)
                {
                    log.Warning($"The CRD {KubernetesObjects.Name(definition)} is not established in cluster {cluster} after {timings.CrdEstablished.TotalSeconds:0} s; its objects may fail.");
                    break;
                }

                await Task.Delay(timings.Poll, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
