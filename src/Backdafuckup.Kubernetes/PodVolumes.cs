using System.Globalization;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>Kinds of pod volumes (<see cref="KubernetesPodVolumeRecord.Kind"/>).</summary>
public static class PodVolumeKinds
{
    public const string EmptyDir = "emptyDir";

    /// <summary>An emptyDir in memory (tmpfs).</summary>
    public const string MemoryEmptyDir = "emptyDir-memory";

    public const string HostPath = "hostPath";
    public const string Nfs = "nfs";
    public const string Csi = "csi";

    /// <summary>A container's own file system (its writable layer), in the inventory.</summary>
    public const string Container = "container";

    public static string Label(string kind) => kind switch
    {
        MemoryEmptyDir => "emptyDir in memory",
        Container => "container file system",
        _ => kind,
    };
}

/// <summary>Why a pod volume's data was asked for (<see cref="KubernetesPodVolumeRecord.OptIn"/>).</summary>
public static class PodVolumeOptIns
{
    /// <summary>The job backs up every pod volume (Velero's opt-out mode), but those excluded by annotation.</summary>
    public const string Job = "job";

    /// <summary>Named in the pod's <c>backup.velero.io/backup-volumes</c>.</summary>
    public const string Annotation = "annotation";
}

/// <summary>
/// The volumes of a namespace's running pods that are not PVCs, and what the containers wrote into their own file systems:
/// what a backup of the namespace does not hold unless asked for (spec 2026-11-12-1952-kubernetes-file-level-volumes).
/// Pod volumes are opted in as with Velero: by the pod annotation <c>backup.velero.io/backup-volumes</c>, or all of them by
/// the job's switch but those in <c>backup.velero.io/backup-volumes-excludes</c>, hostPath volumes and what a platform puts
/// into pods.
/// </summary>
public static class PodVolumes
{
    public const string BackupAnnotation = "backup.velero.io/backup-volumes";
    public const string ExcludeAnnotation = "backup.velero.io/backup-volumes-excludes";

    /// <summary>Data that is not backed up from this size on is a warning in the backup's log.</summary>
    public const long WarningBytes = 100L * 1024 * 1024;

    /// <summary>The not-PVC volumes of the running pods.</summary>
    /// <param name="all">The job's switch: every pod volume, not only the annotated ones.</param>
    /// <param name="nodeAgent">Whether a node has a running node agent, which reads pod volumes there; null: no node agent.</param>
    /// <remarks>Volumes asked for that the node agent reads get their file tree's key (<see cref="TreeKey"/>) and no reason.</remarks>
    public static IReadOnlyList<KubernetesPodVolumeRecord> Find(
        IEnumerable<V1Pod> pods, KubernetesPlatformSet platforms, bool all, KubernetesUsage? usage, Func<string, bool>? nodeAgent = null)
    {
        ArgumentNullException.ThrowIfNull(pods);
        ArgumentNullException.ThrowIfNull(platforms);
        var result = new List<KubernetesPodVolumeRecord>();
        foreach (var pod in pods.Where(IsRunning).OrderBy(p => p.Metadata.Name, StringComparer.Ordinal))
        {
            var annotations = pod.Metadata.Annotations;
            var named = Names(annotations?.TryGetValue(BackupAnnotation, out var backup) == true ? backup : null);
            var excluded = Names(annotations?.TryGetValue(ExcludeAnnotation, out var exclude) == true ? exclude : null);
            var ns = pod.Metadata.NamespaceProperty;
            var owner = platforms.NamespaceOwner(ns);
            foreach (var volume in pod.Spec.Volumes ?? [])
            {
                if (Classify(volume) is not { } classified)
                {
                    continue;
                }

                var (kind, source, limit) = classified;
                if (kind == PodVolumeKinds.HostPath && OnlyRead(pod, volume.Name))
                {
                    continue; // The node's files for an agent to look at (/proc, /sys, …), not data the pod writes.
                }

                var platform = platforms.PodVolumeOf(volume.Name) ?? owner;
                var optIn = named.Contains(volume.Name) ? PodVolumeOptIns.Annotation
                    : all && !excluded.Contains(volume.Name) && platform is null && kind != PodVolumeKinds.HostPath ? PodVolumeOptIns.Job
                    : null;
                var used = usage?.PodVolumes.TryGetValue((ns, pod.Metadata.Name, volume.Name), out var bytes) == true ? bytes : (long?)null;
                var node = pod.Spec.NodeName ?? string.Empty;
                var readable = kind is PodVolumeKinds.EmptyDir or PodVolumeKinds.MemoryEmptyDir or PodVolumeKinds.Csi;
                var reads = optIn is not null && readable && nodeAgent?.Invoke(node) == true;
                var reason = optIn is null
                    ? platform is not null
                        ? platform == owner ? $"a volume of {platform.Name} in its own namespace" : $"put into the pod by {platform.Name}, not the workload's data"
                        : $"not asked for (the job's switch for pod volumes, or the pod annotation {BackupAnnotation})"
                    : kind switch
                    {
                        PodVolumeKinds.HostPath => "a pod's hostPath volume is not read: its data belongs to the node",
                        PodVolumeKinds.Nfs => $"the pod mounts the NFS export {source} itself, not through a PVC: give it a PV and PVC to back it up file by file",
                        _ when !readable => $"a {kind} volume mounted by the pod itself is not read",
                        _ when nodeAgent is null => "reading pod volumes needs the node agent, which the cluster does not have",
                        _ when !reads => $"the node agent has no running pod on node {node}",
                        _ => null,
                    };
                result.Add(new KubernetesPodVolumeRecord(
                    pod.Metadata.Name, WorkloadOf(pod), volume.Name, kind, source, optIn, platform?.Name, used, limit, reads ? TreeKeyOf(pod.Metadata.Name, volume.Name) : null, reason));
            }
        }

        return result;
    }

    /// <summary>
    /// The running containers that, by the usage numbers, hold data in their own file systems or write to local disks;
    /// empty without numbers.
    /// </summary>
    public static IReadOnlyList<KubernetesContainerWriteRecord> Writes(IEnumerable<V1Pod> pods, KubernetesUsage? usage)
    {
        ArgumentNullException.ThrowIfNull(pods);
        if (usage is null)
        {
            return [];
        }

        var result = new List<KubernetesContainerWriteRecord>();
        foreach (var pod in pods.Where(IsRunning).OrderBy(p => p.Metadata.Name, StringComparer.Ordinal))
        {
            var ns = pod.Metadata.NamespaceProperty;
            foreach (var container in pod.Spec.Containers ?? [])
            {
                var key = (ns, pod.Metadata.Name, container.Name);
                long? layer = usage.ContainerLayers.TryGetValue(key, out var bytes) ? bytes : null;
                double? rate = usage.ContainerWrites.TryGetValue(key, out var perSecond) ? perSecond : null;
                if (layer is > 0 || rate is > 0)
                {
                    result.Add(new KubernetesContainerWriteRecord(pod.Metadata.Name, WorkloadOf(pod), container.Name, layer, rate));
                }
            }
        }

        return result;
    }

    /// <summary>The key of a pod volume's file tree: '_' is in no pod's or volume's name, and stays in a file name.</summary>
    public static string TreeKeyOf(string pod, string volume) => $"pod_{pod}_{volume}";

    /// <summary>The pod's controller: "Deployment web" (a ReplicaSet's by its name), "StatefulSet db", or "Pod x" without one.</summary>
    public static string WorkloadOf(V1Pod pod)
    {
        ArgumentNullException.ThrowIfNull(pod);
        return pod.Metadata.OwnerReferences?.FirstOrDefault(o => o.Controller == true) switch
        {
            null => $"Pod {pod.Metadata.Name}",
            { Kind: "ReplicaSet" } rs when rs.Name.LastIndexOf('-') is > 0 and var dash => $"Deployment {rs.Name[..dash]}",
            { } o => $"{o.Kind} {o.Name}",
        };
    }

    /// <summary>
    /// The backup's log about pod volumes and containers' own file systems: what was not backed up, with sizes. Returns true
    /// when volumes asked for were not backed up (the run ends with a warning).
    /// </summary>
    public static bool Report(string ns, string cluster, IReadOnlyList<KubernetesPodVolumeRecord> volumes, IReadOnlyList<KubernetesContainerWriteRecord> writes, KubernetesUsage? usage, IOperationLog log)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(log);
        var asked = volumes.Where(v => v.OptIn is not null && v.TreeKey is null).ToList();
        var read = volumes.Where(v => v.TreeKey is not null).ToList();
        if (read.Count > 0)
        {
            log.Info($"Pod volumes read file by file by the node agent: {Describe(read, withHow: true)}.");
        }

        var notAsked = volumes.Where(v => v.OptIn is null && v.Platform is null).ToList();
        var platform = volumes.Where(v => v.OptIn is null && v.Platform is not null).ToList();
        if (asked.Count > 0)
        {
            foreach (var group in asked.GroupBy(v => v.Reason ?? "not read", StringComparer.Ordinal))
            {
                log.Warning($"Pod volumes asked for, not backed up: {Describe(group.ToList(), withHow: true)} – {group.Key}{(group.Key.Contains("node agent", StringComparison.Ordinal) ? $" (cluster {cluster})" : string.Empty)}.");
            }
        }

        if (notAsked.Count > 0)
        {
            var known = notAsked.Where(v => v.UsedBytes is not null).Sum(v => v.UsedBytes!.Value);
            var text = $"Pod volumes without a PVC, not backed up (not asked for: the job's switch for pod volumes, or the pod annotation {BackupAnnotation}): {Describe(notAsked, withHow: false)}.";
            if (known >= WarningBytes)
            {
                log.Warning(text);
            }
            else
            {
                log.Info(text);
            }
        }

        if (platform.Count > 0)
        {
            log.Info($"Pod volumes of platforms, not backed up: {string.Join(", ", platform.GroupBy(v => v.Platform!).Select(g => $"{g.Count()} of {g.Key} ({string.Join(", ", g.Select(v => v.Volume).Distinct(StringComparer.Ordinal))})"))}.");
        }

        var layers = writes.Where(w => w.LayerBytes is > 0).ToList();
        if (layers.Count > 0)
        {
            var total = layers.Sum(w => w.LayerBytes!.Value);
            var top = layers.GroupBy(w => (w.Workload, w.Container)).Select(g => (Name: $"{g.Key.Workload}/{g.Key.Container}", Bytes: g.Sum(w => w.LayerBytes!.Value), Pods: g.Count()))
                .OrderByDescending(t => t.Bytes).ToList();
            var text = $"Containers hold {Bytes(total)} in their own file systems, which no backup reads: " +
                       string.Join(", ", top.Take(5).Select(t => $"{t.Name} {Bytes(t.Bytes)}{(t.Pods > 1 ? $" in {t.Pods} pods" : string.Empty)}")) +
                       (top.Count > 5 ? $" and {top.Count - 5} more" : string.Empty) + ". Data worth keeping belongs in a PVC.";
            if (total >= WarningBytes)
            {
                log.Warning(text);
            }
            else
            {
                log.Info(text);
            }
        }

        // Who writes to local disks: the hint where data lives that no PVC holds (the user's way to find such workloads).
        var writers = writes.Where(w => w.WriteBytesPerSecond is > 0)
            .GroupBy(w => (w.Workload, w.Container)).Select(g => (Name: $"{g.Key.Workload}/{g.Key.Container}", Rate: g.Sum(w => w.WriteBytesPerSecond!.Value)))
            .OrderByDescending(w => w.Rate).ToList();
        if (writers.Count > 0)
        {
            log.Info($"Containers writing to local disks over the last hour (their own file systems, emptyDirs or block PVCs): " +
                     string.Join(", ", writers.Take(5).Select(w => $"{w.Name} {Bytes((long)(w.Rate * 3600))}/h")) + (writers.Count > 5 ? $" and {writers.Count - 5} more" : string.Empty) + ".");
        }

        if (usage is not null)
        {
            log.Info($"Usage numbers of {ns} from {usage.Source}: {usage.PodVolumes.Count} pod volume(s), {usage.ContainerLayers.Count} container file system(s), {usage.Claims.Count} PVC(s), {usage.ContainerWrites.Count} write rate(s).");
            foreach (var problem in usage.Problems)
            {
                log.Warning($"Usage numbers of {ns}: {problem}");
            }
        }

        return asked.Count > 0;
    }

    /// <summary>"Deployment web/cache (emptyDir, 2 pods, 120.0 MB)", grouped by workload and volume, the largest first.</summary>
    private static string Describe(IReadOnlyList<KubernetesPodVolumeRecord> volumes, bool withHow)
    {
        var groups = volumes.GroupBy(v => (v.Workload, v.Volume, v.Kind))
            .Select(g => (g.Key, Pods: g.Count(), Used: g.All(v => v.UsedBytes is null) ? (long?)null : g.Sum(v => v.UsedBytes ?? 0), OptIn: g.First().OptIn, Source: g.First().Source))
            .OrderByDescending(g => g.Used ?? -1).ThenBy(g => g.Key.Workload, StringComparer.Ordinal).ThenBy(g => g.Key.Volume, StringComparer.Ordinal).ToList();
        var parts = groups.Take(8).Select(g =>
        {
            var details = new List<string> { PodVolumeKinds.Label(g.Key.Kind) };
            if (g.Source is not null)
            {
                details.Add(g.Source);
            }

            if (g.Pods > 1)
            {
                details.Add($"{g.Pods} pods");
            }

            details.Add(g.Used is { } used ? Bytes(used) : "size unknown");
            if (withHow)
            {
                details.Add(g.OptIn == PodVolumeOptIns.Annotation ? "by its annotation" : "by the job");
            }

            return $"{g.Key.Workload}/{g.Key.Volume} ({string.Join(", ", details)})";
        });
        return string.Join(", ", parts) + (groups.Count > 8 ? $" and {groups.Count - 8} more" : string.Empty);
    }

    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
    };

    /// <summary>Whether every container that mounts the volume mounts it read-only.</summary>
    private static bool OnlyRead(V1Pod pod, string volume)
    {
        var mounts = (pod.Spec.Containers ?? []).Concat(pod.Spec.InitContainers ?? []).SelectMany(c => c.VolumeMounts ?? []).Where(m => m.Name == volume).ToList();
        return mounts.Count > 0 && mounts.All(m => m.ReadOnlyProperty == true);
    }

    private static bool IsRunning(V1Pod pod) => pod.Status?.Phase == "Running" && pod.Metadata.DeletionTimestamp is null;

    private static HashSet<string> Names(string? list) =>
        new((list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    /// <summary>Volumes whose data lives with the pod or is mounted by it directly; null for objects (ConfigMaps, Secrets, …) and PVCs.</summary>
    private static (string Kind, string? Source, long? Limit)? Classify(V1Volume volume) => volume switch
    {
        { PersistentVolumeClaim: not null } or { Ephemeral: not null } => null,
        { ConfigMap: not null } or { Secret: not null } or { Projected: not null } or { DownwardAPI: not null } or { GitRepo: not null } => null,
        { EmptyDir: { } e } => (string.Equals(e.Medium, "Memory", StringComparison.Ordinal) ? PodVolumeKinds.MemoryEmptyDir : PodVolumeKinds.EmptyDir, null, e.SizeLimit?.ToInt64()),
        { HostPath: { } h } => (PodVolumeKinds.HostPath, h.Path, null),
        { Nfs: { } n } => (PodVolumeKinds.Nfs, $"{n.Server}:{n.Path}", null),
        { Csi: { } c } => (PodVolumeKinds.Csi, c.Driver, null),
        { Iscsi: { } i } => ("iscsi", $"{i.TargetPortal} {i.Iqn}", null),
        { Cephfs: { } c } => ("cephfs", c.Path, null),
        { Rbd: { } r } => ("rbd", $"{r.Pool}/{r.Image}", null),
        { Fc: not null } => ("fc", null, null),
        { AzureFile: { } a } => ("azureFile", a.ShareName, null),
        { AzureDisk: { } a } => ("azureDisk", a.DiskName, null),
        { AwsElasticBlockStore: { } a } => ("awsElasticBlockStore", a.VolumeID, null),
        { GcePersistentDisk: { } g } => ("gcePersistentDisk", g.PdName, null),
        { VsphereVolume: { } v } => ("vsphereVolume", v.VolumePath, null),
        { Cinder: { } c } => ("cinder", c.VolumeID, null),
        { FlexVolume: { } f } => ("flexVolume", f.Driver, null),
        _ => null,
    };
}
