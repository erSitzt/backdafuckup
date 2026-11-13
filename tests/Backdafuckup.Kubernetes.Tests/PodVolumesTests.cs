using Backdafuckup.Core.Operations;
using Backdafuckup.Testing.Kubernetes;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>
/// Pod volumes that are not PVCs and containers' own file systems (spec 2026-11-12-1952-kubernetes-file-level-volumes):
/// which are asked for (Velero's annotations, the job's switch), which a platform put there, and the usage numbers.
/// </summary>
public sealed class PodVolumesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Pod_volumes_are_asked_for_as_with_velero_and_what_platforms_put_into_pods_is_named_as_theirs()
    {
        var web = Pod("web-7d9f-abc12", "web", "ReplicaSet", "web-7d9f", annotations: new() { [PodVolumes.BackupAnnotation] = "cache" },
            """{ "name": "cache", "emptyDir": { "sizeLimit": "1Gi" } }""",
            """{ "name": "scratch", "emptyDir": { "medium": "Memory" } }""",
            """{ "name": "node-logs", "hostPath": { "path": "/var/log" } }""",
            """{ "name": "istio-data", "emptyDir": {} }""",
            """{ "name": "shared", "nfs": { "server": "nfs.corp.example", "path": "/export/shared" } }""",
            """{ "name": "config", "configMap": { "name": "web" } }""",
            """{ "name": "data", "persistentVolumeClaim": { "claimName": "data-web" } }""");
        var worker = Pod("worker-0", "web", "StatefulSet", "worker", annotations: new() { [PodVolumes.ExcludeAnnotation] = "tmp" },
            """{ "name": "tmp", "emptyDir": {} }""",
            """{ "name": "queue", "emptyDir": {} }""");
        var done = Pod("job-xyz", "web", "Job", "job", annotations: null, """{ "name": "out", "emptyDir": {} }""");
        done.Status.Phase = "Succeeded";
        var istio = KubernetesPlatformSet.Of(["istio"]);

        // Without the job's switch only the annotated volume is asked for; the platform's sidecar volume is Istio's.
        var some = PodVolumes.Find([web, worker, done], istio, all: false, usage: null).ToDictionary(v => $"{v.Pod}/{v.Volume}");
        Assert.Equal(["web-7d9f-abc12/cache", "web-7d9f-abc12/istio-data", "web-7d9f-abc12/node-logs", "web-7d9f-abc12/scratch", "web-7d9f-abc12/shared", "worker-0/queue", "worker-0/tmp"],
            some.Keys.Order(StringComparer.Ordinal));
        var cache = some["web-7d9f-abc12/cache"];
        Assert.Equal(("Deployment web", PodVolumeKinds.EmptyDir, PodVolumeOptIns.Annotation, 1L << 30, (long?)null), (cache.Workload, cache.Kind, cache.OptIn, cache.SizeLimitBytes!.Value, cache.UsedBytes));
        Assert.Equal("reading pod volumes needs the node agent, which the cluster does not have", cache.Reason);
        Assert.Equal(PodVolumeKinds.MemoryEmptyDir, some["web-7d9f-abc12/scratch"].Kind);
        Assert.Equal(("Istio", (string?)null), (some["web-7d9f-abc12/istio-data"].Platform, some["web-7d9f-abc12/istio-data"].OptIn));
        Assert.Equal(("/var/log", (string?)null), (some["web-7d9f-abc12/node-logs"].Source, some["web-7d9f-abc12/node-logs"].OptIn));
        Assert.StartsWith("not asked for", some["worker-0/queue"].Reason, StringComparison.Ordinal);
        Assert.Equal("StatefulSet worker", some["worker-0/queue"].Workload);

        // With it every volume is, but those excluded by annotation, hostPath volumes and the platform's.
        var all = PodVolumes.Find([web, worker], istio, all: true, usage: null).ToDictionary(v => $"{v.Pod}/{v.Volume}");
        Assert.Equal(PodVolumeOptIns.Annotation, all["web-7d9f-abc12/cache"].OptIn);
        Assert.Equal(PodVolumeOptIns.Job, all["web-7d9f-abc12/scratch"].OptIn);
        Assert.Equal(PodVolumeOptIns.Job, all["worker-0/queue"].OptIn);
        Assert.Null(all["worker-0/tmp"].OptIn);
        Assert.Null(all["web-7d9f-abc12/node-logs"].OptIn);
        Assert.Null(all["web-7d9f-abc12/istio-data"].OptIn);
        Assert.Equal(PodVolumeOptIns.Job, all["web-7d9f-abc12/shared"].OptIn);
        Assert.Equal("the pod mounts the NFS export nfs.corp.example:/export/shared itself, not through a PVC: give it a PV and PVC to back it up file by file", all["web-7d9f-abc12/shared"].Reason);

        // Volumes in a platform's own namespace are the platform's.
        var calico = Pod("calico-node-x1", "calico-system", "DaemonSet", "calico-node", annotations: null, """{ "name": "var-run-calico", "hostPath": { "path": "/var/run/calico" } }""");
        var own = Assert.Single(PodVolumes.Find([calico], KubernetesPlatformSet.Of(["calico"]), all: true, usage: null));
        Assert.Equal(("Calico / Canal", "a volume of Calico / Canal in its own namespace"), (own.Platform, own.Reason));
    }

    [Fact]
    public void The_log_names_what_was_not_backed_up_with_sizes_and_warns_about_what_was_asked_for_or_is_large()
    {
        var web = Pod("web-1", "web", "ReplicaSet", "web-5c", annotations: new() { [PodVolumes.BackupAnnotation] = "cache" },
            """{ "name": "cache", "emptyDir": {} }""", """{ "name": "tmp", "emptyDir": {} }""");
        web.Spec.Containers = [new V1Container { Name = "nginx" }, new V1Container { Name = "exporter" }];
        var web2 = Pod("web-2", "web", "ReplicaSet", "web-5c", annotations: null, """{ "name": "tmp", "emptyDir": {} }""");
        web2.Spec.Containers = [new V1Container { Name = "nginx" }];
        var usage = new KubernetesUsage(
            "Prometheus https://prometheus.example",
            new Dictionary<(string, string, string), long> { [("web", "web-1", "cache")] = 120L << 20, [("web", "web-1", "tmp")] = 2L << 20 },
            new Dictionary<(string, string, string), long> { [("web", "web-1", "nginx")] = 150L << 20, [("web", "web-2", "nginx")] = 50L << 20 },
            new Dictionary<(string, string), long>(),
            new Dictionary<(string, string, string), double> { [("web", "web-1", "exporter")] = 2048 },
            ["Prometheus https://prometheus.example refused the query for PVCs (400 BadRequest): parse error."]);

        var volumes = PodVolumes.Find([web, web2], KubernetesPlatformSet.Base, all: false, usage);
        var writes = PodVolumes.Writes([web, web2], usage);
        Assert.Equal(120L << 20, volumes.Single(v => v.Volume == "cache").UsedBytes);
        Assert.Null(volumes.Single(v => v.Pod == "web-2").UsedBytes);
        Assert.Equal([("web-1", "nginx", 150L << 20, (double?)null), ("web-1", "exporter", (long?)null, 2048.0), ("web-2", "nginx", 50L << 20, null)],
            writes.Select(w => (w.Pod, w.Container, w.LayerBytes, w.WriteBytesPerSecond)));

        var log = new Lines();
        Assert.True(PodVolumes.Report("web", "rke2-test", volumes, writes, usage, log));
        Assert.Contains("Warning: Pod volumes asked for, not backed up: Deployment web/cache (emptyDir, 120.0 MB, by its annotation) – reading pod volumes needs the node agent, which the cluster does not have (cluster rke2-test).", log.All);
        Assert.Contains("Info: Pod volumes without a PVC, not backed up (not asked for: the job's switch for pod volumes, or the pod annotation backup.velero.io/backup-volumes): Deployment web/tmp (emptyDir, 2 pods, 2.0 MB).", log.All);
        Assert.Contains("Warning: Containers hold 200.0 MB in their own file systems, which no backup reads: Deployment web/nginx 200.0 MB in 2 pods. Data worth keeping belongs in a PVC.", log.All);
        Assert.Contains("Info: Usage numbers of web from Prometheus https://prometheus.example: 2 pod volume(s), 2 container file system(s), 0 PVC(s), 1 write rate(s).", log.All);
        Assert.Contains("Warning: Usage numbers of web: Prometheus https://prometheus.example refused the query for PVCs (400 BadRequest): parse error.", log.All);

        // Nothing asked for: no warning about it; without numbers, sizes are unknown.
        var quiet = new Lines();
        Assert.False(PodVolumes.Report("web", "rke2-test", PodVolumes.Find([web2], KubernetesPlatformSet.Base, all: false, null), [], null, quiet));
        Assert.Equal(["Info: Pod volumes without a PVC, not backed up (not asked for: the job's switch for pod volumes, or the pod annotation backup.velero.io/backup-volumes): Deployment web/tmp (emptyDir, size unknown)."], quiet.All);
    }

    [Fact]
    public async Task Prometheus_answers_each_query_with_the_labels_of_its_exporter_filtered_to_a_namespace_and_cluster()
    {
        var prometheus = new FakePrometheus();
        prometheus.Set("ephemeral_storage_container_volume_usage", 4096, ("pod_namespace", "web"), ("namespace", "monitoring"), ("pod_name", "web-1"), ("volume_name", "cache"));
        prometheus.Set("ephemeral_storage_container_rootfs_used_bytes", 8192, ("pod_namespace", "web"), ("pod_name", "web-1"), ("container", "nginx"));
        prometheus.Set("kubelet_volume_stats_used_bytes", 1e9, ("namespace", "web"), ("persistentvolumeclaim", "data-web"));
        prometheus.Set("container_fs_writes_bytes_total", 12.5, ("namespace", "web"), ("pod", "web-1"), ("container", "nginx"));
        var config = new KubernetesUsageConfig("https://prometheus.example/prom/", Filter: "cluster=\"rke2-test\"");
        using var source = new PrometheusUsageSource(config, "token-for-tests", prometheus.CreateHandler());

        var usage = await source.ReadAsync("web", Ct);

        Assert.Equal("Prometheus https://prometheus.example/prom", usage.Source);
        Assert.Equal(4096, usage.PodVolumes[("web", "web-1", "cache")]);
        Assert.Equal(8192, usage.ContainerLayers[("web", "web-1", "nginx")]);
        Assert.Equal(1_000_000_000, usage.Claims[("web", "data-web")]);
        Assert.Equal(12.5, usage.ContainerWrites[("web", "web-1", "nginx")]);
        Assert.Empty(usage.Problems);
        Assert.Equal("Bearer token-for-tests", prometheus.Authorization);
        Assert.Contains("max by (pod_namespace, pod_name, volume_name) (ephemeral_storage_container_volume_usage{pod_namespace=~\"web\",cluster=\"rke2-test\"})", prometheus.Queries);
        Assert.All(prometheus.Queries, q => Assert.Contains("cluster=\"rke2-test\"", q, StringComparison.Ordinal));

        // Basic authentication with a user name; a refused query is a problem, the others still count.
        prometheus.Refuse = ("kubelet_volume_stats_used_bytes", "parse error at char 12");
        using var basic = new PrometheusUsageSource(new KubernetesUsageConfig("https://prometheus.example", Username: "reader"), "secret", prometheus.CreateHandler());
        var partial = await basic.ReadAsync(null, Ct);
        Assert.Equal("Basic cmVhZGVyOnNlY3JldA==", prometheus.Authorization);
        Assert.Empty(partial.Claims);
        Assert.Single(partial.PodVolumes);
        var problem = Assert.Single(partial.Problems);
        Assert.StartsWith("Prometheus https://prometheus.example refused the query for PVCs (400 BadRequest): parse error at char 12. Query: max by (namespace, persistentvolumeclaim) (kubelet_volume_stats_used_bytes{namespace=~\".+\"})", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pvc_s_usage_counts_only_when_the_kubelet_measures_its_own_file_system()
    {
        var usage = new KubernetesUsage("Prometheus https://prometheus.example", new Dictionary<(string, string, string), long>(), new Dictionary<(string, string, string), long>(),
            new Dictionary<(string, string), long> { [("web", "data")] = 600L << 20, [("web", "nfs")] = 665L << 30 }, new Dictionary<(string, string, string), double>(), []);

        Assert.Equal(600L << 20, KubernetesInventory.ClaimUsage(usage, "web", "data", 1L << 30, sharedFileSystem: false));
        // An NFS or hostPath PV reports its export or node disk; a number above the PVC's size is not about the PVC.
        Assert.Null(KubernetesInventory.ClaimUsage(usage, "web", "nfs", 1L << 30, sharedFileSystem: true));
        Assert.Null(KubernetesInventory.ClaimUsage(usage, "web", "nfs", 1L << 30, sharedFileSystem: false));
        Assert.Null(KubernetesInventory.ClaimUsage(usage, "web", "other", 1L << 30, sharedFileSystem: false));
        Assert.Null(KubernetesInventory.ClaimUsage(null, "web", "data", 1L << 30, sharedFileSystem: false));
    }

    [Theory]
    [InlineData("prometheus.example:9090", "is not an http(s) URL")]
    [InlineData("https://prometheus.example/api/v1/query?query=up", "is not an http(s) URL without a query")]
    [InlineData("https://prometheus.example", "is a list of label matchers without braces", "{cluster=\"x\"}")]
    public void A_usage_source_is_checked_before_it_is_saved(string url, string problem, string? filter = null) =>
        Assert.Contains(problem, KubernetesUsageConfig.Problem(new KubernetesUsageConfig(url, Filter: filter)), StringComparison.Ordinal);

    private static V1Pod Pod(string name, string ns, string ownerKind, string owner, Dictionary<string, string>? annotations, params string[] volumes)
    {
        var pod = KubernetesJson.Deserialize<V1Pod>($$"""
            { "metadata": { "name": "{{name}}", "namespace": "{{ns}}", "ownerReferences": [ { "kind": "{{ownerKind}}", "name": "{{owner}}", "controller": true, "apiVersion": "apps/v1", "uid": "u" } ] },
              "spec": { "containers": [ { "name": "main" } ], "volumes": [ {{string.Join(",", volumes)}} ] },
              "status": { "phase": "Running" } }
            """);
        pod.Metadata.Annotations = annotations;
        return pod;
    }

    private sealed class Lines : IOperationLog
    {
        public List<string> All { get; } = [];

        public void Write(LogSeverity severity, string message, string? key = null) => All.Add($"{severity}: {message}");
    }
}
