using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Engine;
using Backdafuckup.Testing.Kubernetes;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Pod volumes that are not PVCs and the usage numbers of a cluster (spec 2026-11-12-1952-kubernetes-file-level-volumes).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task Pod_volumes_and_container_file_systems_are_named_with_their_sizes_and_those_asked_for_make_a_warning()
    {
        var client = await SignedInClientAsync();
        var api = new FakeKubernetesApi("uid-kube-system-pods");
        _clusters["10.26.7.10"] = api;
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("web"));
        api.Add("api/v1", "pods", Pod("web-5c-a1", "web-5c", new JsonObject { [PodVolumes.BackupAnnotation] = "cache" }, "cache", "tmp"));
        api.Add("api/v1", "pods", Pod("web-5c-b2", "web-5c", new JsonObject { [PodVolumes.ExcludeAnnotation] = "tmp" }, "cache", "tmp"));
        _prometheus.Set("ephemeral_storage_container_volume_usage", 120 << 20, ("pod_namespace", "web"), ("pod_name", "web-5c-a1"), ("volume_name", "cache"));
        _prometheus.Set("ephemeral_storage_container_volume_usage", 3 << 20, ("pod_namespace", "web"), ("pod_name", "web-5c-b2"), ("volume_name", "tmp"));
        _prometheus.Set("ephemeral_storage_container_rootfs_used_bytes", 150 << 20, ("pod_namespace", "web"), ("pod_name", "web-5c-a1"), ("container", "nginx"));
        _prometheus.Set("container_fs_writes_bytes_total", 4096, ("namespace", "web"), ("pod", "web-5c-a1"), ("container", "nginx"));
        var cluster = await AddClusterAsync(client, "rke2-pods", "10.26.7.10");

        // The usage source: checked, tested (nothing saved), then saved; its token is never returned.
        var bad = await client.PutAsJsonAsync($"/api/connections/{cluster.Id}/usage-source", new SaveKubernetesUsageSourceDto("prometheus.example:9090"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var source = new SaveKubernetesUsageSourceDto("https://prometheus.example", Secret: "prom-token", Filter: "cluster=\"rke2-pods\"");
        var test = await Read<KubernetesUsageTestDto>(client.PostAsJsonAsync($"/api/connections/{cluster.Id}/usage-source/test", source, Ct));
        Assert.Equal((2, 1, 0, 1), (test.PodVolumes, test.ContainerLayers, test.Claims, test.ContainerWrites));
        Assert.Empty(test.Problems);
        Assert.Equal("Bearer prom-token", _prometheus.Authorization);
        Assert.Null((await client.GetFromJsonAsync<List<ConnectionDto>>("/api/connections", Ct))!.Single(c => c.Id == cluster.Id).UsageSource);
        var saved = await Read<ConnectionDto>(client.PutAsJsonAsync($"/api/connections/{cluster.Id}/usage-source", source, Ct));
        Assert.Equal(("https://prometheus.example", true, "cluster=\"rke2-pods\""), (saved.UsageSource!.Url, saved.UsageSource.HasSecret, saved.UsageSource.Filter));
        Assert.DoesNotContain("prom-token", await client.GetStringAsync("/api/connections", Ct), StringComparison.Ordinal);

        // The inventory: pod volumes per workload with their data, the containers' file systems, the namespace's total.
        await Ok(client.PostAsync($"/api/connections/{cluster.Id}/rescan", null, Ct));
        var inventory = (await client.GetFromJsonAsync<List<InventoryItemDto>>($"/api/connections/{cluster.Id}/inventory", Ct))!;
        var cache = inventory.Single(i => i.Kind == "PodVolume" && i.Name == "cache");
        Assert.Equal(("web/Deployment web/cache", "Deployment web", "emptyDir", "2", (120L << 20).ToString(), "annotation"),
            (cache.NativeId, cache.Properties["workload"], cache.Properties["kind"], cache.Properties["pods"], cache.Properties["usedBytes"], cache.Properties["optIn"]));
        var nginx = inventory.Single(i => i.Kind == "PodVolume" && i.Name == "nginx");
        Assert.Equal(("container", (150L << 20).ToString(), "4096"), (nginx.Properties["kind"], nginx.Properties["usedBytes"], nginx.Properties["writeBytesPerSecond"]));
        var ns = inventory.Single(i => i.Kind == "Namespace" && i.Name == "web");
        Assert.Equal(("4", ((120L << 20) + (3L << 20) + (150L << 20)).ToString()), (ns.Properties["podVolumes"], ns.Properties["unsavedBytes"]));

        // A backup names what it did not back up; the volume a pod asked for makes the run a warning.
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var request = new SaveJobDto
        {
            Name = "Web", ConnectionId = cluster.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("web", "web")], RetryCount = 0,
        };
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", request, Ct));
        var (state, log) = await RunAsync(client, job.Id);
        Assert.True(state == "Warning", log);
        Assert.Contains("Warning: web: Pod volumes asked for, not backed up: Deployment web/cache (emptyDir, 120.0 MB, by its annotation) – reading pod volumes needs the node agent, which the cluster does not have (cluster 10.26.7.10).", log, StringComparison.Ordinal);
        Assert.Contains("Pod volumes without a PVC, not backed up (not asked for: the job's switch for pod volumes, or the pod annotation backup.velero.io/backup-volumes): Deployment web/tmp (emptyDir, 2 pods, 3.0 MB), Deployment web/cache (emptyDir, size unknown).", log, StringComparison.Ordinal);
        Assert.Contains("Warning: web: Containers hold 150.0 MB in their own file systems, which no backup reads: Deployment web/nginx 150.0 MB. Data worth keeping belongs in a PVC.", log, StringComparison.Ordinal);
        Assert.Contains("web: Usage numbers of web from Prometheus https://prometheus.example: 2 pod volume(s), 1 container file system(s), 0 PVC(s), 1 write rate(s).", log, StringComparison.Ordinal);
        Assert.Contains(_prometheus.Queries, q => q.Contains("pod_namespace=~\"web\",cluster=\"rke2-pods\"", StringComparison.Ordinal));

        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
        var store = await _factory!.Services.GetRequiredService<RepositoryManager>().GetAsync(repository.Id, Ct);
        await using (var reader = await store.OpenRestorePointAsync(point.Id, Ct))
        {
            var descriptor = reader.Manifest.Kubernetes!;
            Assert.Equal("Prometheus https://prometheus.example", descriptor.UsageSource);
            Assert.Equal(["web-5c-a1/cache annotation", "web-5c-a1/tmp -", "web-5c-b2/cache -", "web-5c-b2/tmp -"],
                descriptor.PodVolumes.Select(v => $"{v.Pod}/{v.Volume} {v.OptIn ?? "-"}"));
            Assert.Equal(("nginx", 150L << 20, 4096.0), (descriptor.ContainerWrites[0].Container, descriptor.ContainerWrites[0].LayerBytes!.Value, descriptor.ContainerWrites[0].WriteBytesPerSecond!.Value));
        }

        var details = (await client.GetFromJsonAsync<KubernetesRestorePointDto>($"/api/restore-points/{point.Id}/kubernetes", Ct))!;
        Assert.Equal(("cache", "annotation", false, 120L << 20), (details.PodVolumes![0].Volume, details.PodVolumes[0].OptIn, details.PodVolumes[0].BackedUp, details.PodVolumes[0].UsedBytes!.Value));
        Assert.Equal(("nginx", 150L << 20), (details.ContainerWrites![0].Container, details.ContainerWrites[0].LayerBytes!.Value));

        // The job's switch asks for every pod volume but those a pod excludes.
        var updated = await client.PutAsJsonAsync($"/api/jobs/{job.Id}", request with { KubernetesPodVolumes = true }, Ct);
        updated.EnsureSuccessStatusCode();
        Assert.True((await client.GetFromJsonAsync<JobDto>($"/api/jobs/{job.Id}", Ct))!.KubernetesPodVolumes);
        (state, log) = await RunAsync(client, job.Id);
        Assert.True(state == "Warning", log);
        Assert.Contains("Pod volumes asked for, not backed up: Deployment web/cache (emptyDir, 2 pods, 120.0 MB, by its annotation), Deployment web/tmp (emptyDir, size unknown, by the job) – reading pod volumes needs the node agent", log, StringComparison.Ordinal);

        // Without a usage source the sizes are unknown.
        await Ok(client.DeleteAsync($"/api/connections/{cluster.Id}/usage-source", Ct));
        (_, log) = await RunAsync(client, job.Id);
        Assert.Contains("(emptyDir, 2 pods, size unknown, by", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage numbers", log, StringComparison.Ordinal);
    }

    private static async Task<(string State, string Log)> RunAsync(HttpClient client, Guid jobId)
    {
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{jobId}/run", new RunJobDto(), Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        return (session.Session.State, log);
    }

    private static JsonObject Pod(string name, string replicaSet, JsonObject annotations, params string[] emptyDirs) => new()
    {
        ["metadata"] = new JsonObject
        {
            ["name"] = name, ["namespace"] = "web", ["annotations"] = annotations,
            ["ownerReferences"] = new JsonArray(new JsonObject { ["apiVersion"] = "apps/v1", ["kind"] = "ReplicaSet", ["name"] = replicaSet, ["uid"] = "uid-rs", ["controller"] = true }),
        },
        ["spec"] = new JsonObject
        {
            ["containers"] = new JsonArray(new JsonObject { ["name"] = "nginx", ["image"] = "nginx:1.27" }),
            ["volumes"] = new JsonArray([.. emptyDirs.Select(v => (JsonNode)new JsonObject { ["name"] = v, ["emptyDir"] = new JsonObject() })]),
        },
        ["status"] = new JsonObject { ["phase"] = "Running" },
    };
}
