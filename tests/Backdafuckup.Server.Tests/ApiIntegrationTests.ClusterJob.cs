using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>A whole cluster backed up by one job and restored into another cluster (spec 2026-11-11-1343, phase 2).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_whole_cluster_is_backed_up_and_restored_into_another_cluster_cluster_scoped_objects_first()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "None", null, null, null), Ct));
        Directory.CreateDirectory(_root);
        var device = Path.Combine(_root, "ext4-volume.img");
        using (var image = File.OpenRead(FsFixtures.Image("ext4-gpt")))
        using (var copy = File.Create(device))
        {
            image.Position = 2048 * 512;
            image.CopyTo(copy);
        }

        // The source: bdfu-spike (Redis with its volume), a namespace "web" without volumes, and cluster-scoped objects.
        SetUpCluster(device, [], [], new FileInfo(device).Length);
        _cluster.Add("api/v1", "namespaces", Backdafuckup.Testing.Kubernetes.FakeKubernetesApi.Namespace("web"));
        _cluster.Add("api/v1", "configmaps", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "site", ["namespace"] = "web" }, ["data"] = new JsonObject { ["title"] = "hello" } });
        _cluster.Add("api/v1", "namespaces", Backdafuckup.Testing.Kubernetes.FakeKubernetesApi.Namespace("cattle-system"));
        _cluster.Add("apis/apiextensions.k8s.io/v1", "customresourcedefinitions", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "apps.catalog.cattle.io" },
            ["spec"] = new JsonObject
            {
                ["group"] = "catalog.cattle.io", ["scope"] = "Namespaced",
                ["names"] = new JsonObject { ["kind"] = "App", ["plural"] = "apps" },
                ["versions"] = new JsonArray(new JsonObject { ["name"] = "v1", ["served"] = true, ["storage"] = true }),
            },
        });
        _cluster.Add("apis/catalog.cattle.io/v1", "apps", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "site", ["namespace"] = "web" }, ["spec"] = new JsonObject() });

        // A HelmChart of RKE2's helm controller: a user object of an API that comes with RKE2, not from a backup.
        _cluster.Add("apis/apiextensions.k8s.io/v1", "customresourcedefinitions", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "helmcharts.helm.cattle.io" },
            ["spec"] = new JsonObject
            {
                ["group"] = "helm.cattle.io", ["scope"] = "Namespaced",
                ["names"] = new JsonObject { ["kind"] = "HelmChart", ["plural"] = "helmcharts" },
                ["versions"] = new JsonArray(new JsonObject { ["name"] = "v1", ["served"] = true, ["storage"] = true }),
            },
        });
        _cluster.Add("apis/helm.cattle.io/v1", "helmcharts", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "site-chart", ["namespace"] = "web" }, ["spec"] = new JsonObject() });
        _cluster.Add("apis/apiextensions.k8s.io/v1", "customresourcedefinitions", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "clusterissuers.cert-manager.io" },
            ["spec"] = new JsonObject
            {
                ["group"] = "cert-manager.io", ["scope"] = "Cluster",
                ["names"] = new JsonObject { ["kind"] = "ClusterIssuer", ["plural"] = "clusterissuers" },
                ["versions"] = new JsonArray(new JsonObject { ["name"] = "v1", ["served"] = true, ["storage"] = true }),
            },
        });
        _cluster.Add("apis/cert-manager.io/v1", "clusterissuers", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "letsencrypt" }, ["spec"] = new JsonObject() });
        _cluster.Add("apis/rbac.authorization.k8s.io/v1", "clusterroles", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "monitoring-reader" },
            ["rules"] = new JsonArray(new JsonObject { ["apiGroups"] = new JsonArray(""), ["resources"] = new JsonArray("pods"), ["verbs"] = new JsonArray("get") }),
        });
        _cluster.Add("apis/rbac.authorization.k8s.io/v1", "clusterroles", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "system:node" } });
        _cluster.Add("apis/scheduling.k8s.io/v1", "priorityclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "batch-low" }, ["value"] = 100 });
        _cluster.Add("apis/admissionregistration.k8s.io/v1", "validatingwebhookconfigurations", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "cert-manager-webhook" },
            ["webhooks"] = new JsonArray(new JsonObject { ["name"] = "w", ["clientConfig"] = new JsonObject { ["service"] = new JsonObject { ["namespace"] = "cert-manager", ["name"] = "cert-manager-webhook" } } }),
        });
        var source = await AddClusterAsync(client, "rke2-test", "10.26.1.10");
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));

        // A job of the whole cluster: a bad pattern is refused; the default excludes leave out kube-system and cattle-system.
        SaveJobDto Job(string id) => new()
        {
            Name = "Cluster", ConnectionId = source.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(id, "Whole cluster")], RetryCount = 0,
        };
        var refused = await client.PostAsJsonAsync("/api/jobs", Job(KubernetesClusterScope.JobObject(["Kube_System"])), Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", Job(KubernetesClusterScope.JobObject(KubernetesClusterScope.DefaultExcludes)), Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        var backupSession = await WaitForSessionAsync(client, backup.SessionId);
        var backupLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{backup.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(backupSession.Session.State == "Succeeded", backupLog);
        Assert.True(backupLog.Contains("Platform: RKE2 · Rancher + vSphere CSI (detected: rke2 by version v1.32.13+rke2r1; rancher by namespace cattle-system; vsphere-csi by CSI driver csi.vsphere.vmware.com).", StringComparison.Ordinal), backupLog);
        Assert.True(backupLog.Contains("Whole cluster without kube-system, kube-public, kube-node-lease, cattle-*, fleet-*, cluster-fleet-*, local, backdafuckup-system: 2 namespace(s) (bdfu-spike, web)", StringComparison.Ordinal), backupLog);
        Assert.Contains("Cluster-scoped objects of", backupLog, StringComparison.Ordinal);
        var points = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!;
        Assert.Equal(["@cluster", "bdfu-spike", "web"], points.Select(p => p.VmNativeId).Order(StringComparer.Ordinal));
        Assert.Equal(RestorePointObjectKinds.KubernetesCluster, points.Single(p => p.VmNativeId == "@cluster").ObjectKind);
        var clusterPoint = points.Single(p => p.VmNativeId == "@cluster");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/restores", new RestoreRequestDto { RestorePointId = clusterPoint.Id }, Ct)).StatusCode);

        var run = await client.GetFromJsonAsync<ClusterRunDto>($"/api/kubernetes/cluster-runs/{backup.SessionId}", Ct);
        Assert.Equal(["bdfu-spike", "web"], run!.Namespaces.Select(n => n.Namespace));
        Assert.Equal((1, 1, true), (run.Namespaces[0].Volumes, run.Namespaces[0].VolumesWithData, run.Namespaces[0].FromRun));
        Assert.Equal(["vsphere-csi"], run.Namespaces[0].StorageClasses);

        // The target: a cluster with only NFS. Its classes take the volumes; the run comes back there.
        var nfs = NfsCluster("uid-kube-system-dr", Path.Combine(_root, "dr-export"));
        _clusters["10.26.4.10"] = nfs;
        var target = await AddClusterAsync(client, "rke2-dr", "10.26.4.10");
        var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/kubernetes/cluster-restores", new ClusterRestoreRequestDto
        {
            BackupSessionId = backup.SessionId, TargetConnectionId = target.Id, StorageClassMap = new() { ["vsphere-csi"] = "nfs-client" },
        }, Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(session.Session.State is "Succeeded" or "Warning", log);
        Assert.Equal(["@cluster", "bdfu-spike", "web"], session.Tasks.OrderBy(t => t.StartedUtc).Select(t => t.ObjectNativeId));
        Assert.Contains("StorageClass vsphere-csi is not created: its volumes go into the StorageClass nfs-client of cluster", log, StringComparison.Ordinal);
        Assert.Contains("Webhook configurations are not restored", log, StringComparison.Ordinal);
        Assert.True(log.Contains("VolumeSnapshotClass vsphere-snapshots is not created: cluster 10.26.4.10 does not serve the snapshot API (snapshot.storage.k8s.io/v1)", StringComparison.Ordinal), log);
        Assert.True(log.Contains("1 made by Rancher left out", StringComparison.Ordinal), log);
        Assert.Equal("Succeeded", session.Tasks.Single(t => t.ObjectNativeId == "@cluster").State);
        Assert.Contains("ValidatingWebhookConfiguration/cert-manager-webhook (calls cert-manager/cert-manager-webhook)", log, StringComparison.Ordinal);

        // Cluster-scoped first: the CRD and its object, the role, the class; never the webhook, the system role or the mapped class.
        Assert.NotNull(nfs.Find("customresourcedefinitions", null, "clusterissuers.cert-manager.io"));
        Assert.NotNull(nfs.Find("clusterissuers", null, "letsencrypt"));
        Assert.NotNull(nfs.Find("clusterroles", null, "monitoring-reader"));
        Assert.NotNull(nfs.Find("priorityclasses", null, "batch-low"));
        Assert.Null(nfs.Find("clusterroles", null, "system:node"));
        Assert.Null(nfs.Find("validatingwebhookconfigurations", null, "cert-manager-webhook"));
        Assert.Null(nfs.Find("storageclasses", null, "vsphere-csi"));

        // Then the namespaces: bdfu-spike with its volume as files on NFS, web with its ConfigMap; nothing of kube-system or cattle-system.
        Assert.Equal("hello", (string?)nfs.Find("configmaps", "web", "site")!["data"]!["title"]);
        var claim = nfs.Find("persistentvolumeclaims", "bdfu-spike", "data-redis-test-0")!;
        var export = (string)nfs.Find("persistentvolumes", null, (string)claim["spec"]!["volumeName"]!)!["spec"]!["nfs"]!["path"]!;
        Assert.True(File.Exists(Path.Combine(export, "hello.txt")));
        Assert.Null(nfs.Find("namespaces", null, "cattle-system"));

        // Rancher's records of its Helm releases stay out of another cluster, and so does their CRD.
        Assert.Null(nfs.Find("apps", "web", "site"));
        Assert.Null(nfs.Find("customresourcedefinitions", null, "apps.catalog.cattle.io"));

        // The HelmChart needs RKE2's API, which the target lacks: it stays out, named, and its CRD is not created from the backup.
        Assert.True(log.Contains("1 object(s) are not restored (HelmChart/site-chart): their API helm.cattle.io comes with RKE2, which cluster 10.26.4.10 does not have.", StringComparison.Ordinal), log);
        Assert.Null(nfs.Find("customresourcedefinitions", null, "helmcharts.helm.cattle.io"));
        Assert.Null(nfs.Find("helmcharts", "web", "site-chart"));
    }
}
