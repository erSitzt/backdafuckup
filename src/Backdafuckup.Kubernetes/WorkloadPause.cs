using System.Globalization;
using System.Text.Json;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>A workload scaled to 0 for a PVC swap: what to scale back, and its ledger entry.</summary>
internal sealed record PausedWorkload(string Namespace, string Kind, string Name, int Replicas, Guid LedgerId)
{
    public string Key => $"{Kind}/{Name}";
}

/// <summary>
/// Stops what uses a PVC so it can be swapped: the controllers of the pods that mount it (Deployment, StatefulSet,
/// ReplicaSet, ReplicationController) are scaled to 0, their replicas kept in the artifact ledger, so a crash still
/// scales them back. Pods that no scalable controller owns (a DaemonSet's, a Job's, a bare pod) stop the swap.
/// </summary>
internal static class WorkloadPause
{
    private static readonly Dictionary<string, (string Group, string Version, string Plural)> Scalable = new(StringComparer.Ordinal)
    {
        ["Deployment"] = ("apps", "v1", "deployments"),
        ["StatefulSet"] = ("apps", "v1", "statefulsets"),
        ["ReplicaSet"] = ("apps", "v1", "replicasets"),
    };

    /// <summary>The workloads to scale for <paramref name="claim"/>, or the reason it cannot be swapped.</summary>
    public static async Task<(IReadOnlyList<(string Kind, string Name)> Workloads, string? Problem)> FindAsync(IKubernetes client, string ns, string claim, CancellationToken cancellationToken)
    {
        var workloads = new List<(string Kind, string Name)>();
        foreach (var pod in await UsersAsync(client, ns, claim, cancellationToken).ConfigureAwait(false))
        {
            var owner = pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true);
            switch (owner?.Kind)
            {
                case "ReplicaSet":
                    var replicaSet = await client.AppsV1.ReadNamespacedReplicaSetAsync(owner.Name, ns, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var deployment = replicaSet.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true && o.Kind == "Deployment");
                    workloads.Add(deployment is null ? ("ReplicaSet", owner.Name) : ("Deployment", deployment.Name));
                    break;
                case "StatefulSet" or "ReplicationController":
                    workloads.Add((owner.Kind, owner.Name));
                    break;
                case null:
                    return ([], $"The pod {ns}/{pod.Metadata.Name} uses the PVC {claim} and no controller owns it: stop it first.");
                default:
                    return ([], $"The pod {ns}/{pod.Metadata.Name} of the {owner.Kind} {owner.Name} uses the PVC {claim}, and a {owner.Kind} cannot be scaled to 0: stop it first.");
            }
        }

        return ([.. workloads.Distinct()], null);
    }

    /// <summary>Scales the workloads to 0 (each in the ledger first) and waits until no pod uses the PVC any more.</summary>
    public static async Task<IReadOnlyList<PausedWorkload>> PauseAsync(
        VolumeSnapshotFlow flow, string ns, string claim, IReadOnlyList<(string Kind, string Name)> workloads, CancellationToken cancellationToken)
    {
        var paused = new List<PausedWorkload>();
        foreach (var (kind, name) in workloads)
        {
            var replicas = await ReplicasAsync(flow.Client, ns, kind, name, cancellationToken).ConfigureAwait(false);
            var ledgerId = await flow.Ledger.RecordAsync(
                KubernetesArtifactKinds.ScaledWorkload, $"{ns}/{kind}/{name}", null, JsonSerializer.Serialize(new { replicas }), cancellationToken).ConfigureAwait(false);
            await ScaleAsync(flow, ns, kind, name, 0, cancellationToken).ConfigureAwait(false);
            paused.Add(new PausedWorkload(ns, kind, name, replicas, ledgerId));
            flow.Log.Info($"{kind} {ns}/{name} scaled from {replicas} to 0 for the swap of PVC {claim}.");
        }

        var started = DateTimeOffset.UtcNow;
        while ((await UsersAsync(flow.Client, ns, claim, cancellationToken).ConfigureAwait(false)) is { Count: > 0 } users)
        {
            if (DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(10))
            {
                throw new KubernetesOperationException($"The PVC {ns}/{claim} is still used by {string.Join(", ", users.Select(p => p.Metadata.Name))} after 10 minutes (cluster {flow.Cluster}).");
            }

            await Task.Delay(flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        return paused;
    }

    /// <summary>Scales a paused workload back to its replicas; its ledger entry is done.</summary>
    public static async Task ResumeAsync(VolumeSnapshotFlow flow, PausedWorkload workload, CancellationToken cancellationToken)
    {
        await ScaleAsync(flow, workload.Namespace, workload.Kind, workload.Name, workload.Replicas, cancellationToken).ConfigureAwait(false);
        await flow.Ledger.UpdateAsync(workload.LedgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        flow.Log.Info($"{workload.Kind} {workload.Namespace}/{workload.Name} scaled back to {workload.Replicas}.");
    }

    /// <summary>The cleanup of a ledger entry (<c>namespace/Kind/name</c>, data <c>{"replicas":n}</c>): scales the workload back.</summary>
    public static Task ResumeAsync(VolumeSnapshotFlow flow, string nativeId, string? data, CancellationToken cancellationToken)
    {
        var parts = nativeId.Split('/', 3);
        var replicas = data is null ? 1 : JsonDocument.Parse(data).RootElement.GetProperty("replicas").GetInt32();
        return ScaleAsync(flow, parts[0], parts[1], parts[2], replicas, cancellationToken);
    }

    private static async Task<IReadOnlyList<V1Pod>> UsersAsync(IKubernetes client, string ns, string claim, CancellationToken cancellationToken) =>
        [.. (await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .Where(p => p.Spec?.Volumes?.Any(v => v.PersistentVolumeClaim?.ClaimName == claim) == true)];

    private static async Task<int> ReplicasAsync(IKubernetes client, string ns, string kind, string name, CancellationToken cancellationToken)
    {
        if (kind == "ReplicationController")
        {
            return (await client.CoreV1.ReadNamespacedReplicationControllerAsync(name, ns, cancellationToken: cancellationToken).ConfigureAwait(false)).Spec.Replicas ?? 1;
        }

        var (group, version, plural) = Scalable[kind];
        var live = VolumeSnapshotFlow.Element(await client.CustomObjects.GetNamespacedCustomObjectAsync(group, version, ns, plural, name, cancellationToken).ConfigureAwait(false));
        return VolumeSnapshotFlow.Number(live, "spec", "replicas") is { } replicas ? (int)replicas : 1;
    }

    private static async Task ScaleAsync(VolumeSnapshotFlow flow, string ns, string kind, string name, int replicas, CancellationToken cancellationToken)
    {
        var patch = new V1Patch("{\"spec\":{\"replicas\":" + replicas.ToString(CultureInfo.InvariantCulture) + "}}", V1Patch.PatchType.MergePatch);
        if (kind == "ReplicationController")
        {
            await flow.CallAsync($"scaling {kind} {ns}/{name} to {replicas}", () => flow.Client.CoreV1.PatchNamespacedReplicationControllerAsync(patch, name, ns, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return;
        }

        var (group, version, plural) = Scalable[kind];
        await flow.CallAsync($"scaling {kind} {ns}/{name} to {replicas}", () => flow.Client.CustomObjects.PatchNamespacedCustomObjectAsync(patch, group, version, ns, plural, name, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
