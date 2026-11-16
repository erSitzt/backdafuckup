using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Repository;
using Backdafuckup.Kubernetes;
using Backdafuckup.Mover;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Engine;
using Backdafuckup.Testing.Kubernetes;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>The node agent reads pod volumes (spec 2026-11-12-1952-kubernetes-file-level-volumes).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task The_node_agent_reads_a_pod_volume_asked_for_and_a_mounted_CSI_volume_without_snapshots_and_its_kubelet_s_numbers_count()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        const string PodUid = "0b8f5f1e-3c2a-4b7d-9e61-5a4c3d2b1f00";
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);

        // A pod on worker-1 asks for its emptyDir; the node agent runs there, with the kubelet's directory of the test.
        var kubelet = Path.Combine(_root, "kubelet");
        var cache = Path.Combine(kubelet, "pods", PodUid, "volumes", "kubernetes.io~empty-dir", "cache");
        Directory.CreateDirectory(Path.Combine(cache, "pages"));
        await File.WriteAllTextAsync(Path.Combine(cache, "pages", "index.html"), "<h1>cached</h1>", Ct);
        await File.WriteAllBytesAsync(Path.Combine(cache, "blob.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(200_000), Ct);

        // Its PVC logs-web is a CSI volume without a VolumeSnapshotClass, mounted by its container.
        var logs = Path.Combine(kubelet, "pods", PodUid, "volumes", "kubernetes.io~csi", "pvc-7d1c", "mount");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "access.log"), "GET / 200\n", Ct);
        var api = new FakeKubernetesApi("uid-kube-system-node");
        _clusters["10.26.8.10"] = api;
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("web"));
        api.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "plain-csi" }, ["provisioner"] = "csi.plain.example" });
        api.Add("api/v1", "persistentvolumes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "pvc-7d1c", ["annotations"] = new JsonObject { ["pv.kubernetes.io/provisioned-by"] = "csi.plain.example" } },
            ["spec"] = new JsonObject
            {
                ["capacity"] = new JsonObject { ["storage"] = "1Gi" },
                ["csi"] = new JsonObject { ["driver"] = "csi.plain.example", ["volumeHandle"] = "vol-7d1c" },
                ["storageClassName"] = "plain-csi",
                ["claimRef"] = new JsonObject { ["namespace"] = "web", ["name"] = "logs-web" },
            },
            ["status"] = new JsonObject { ["phase"] = "Bound" },
        });
        api.Add("api/v1", "persistentvolumeclaims", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "logs-web", ["namespace"] = "web" },
            ["spec"] = new JsonObject { ["volumeName"] = "pvc-7d1c", ["storageClassName"] = "plain-csi", ["accessModes"] = new JsonArray("ReadWriteOnce") },
            ["status"] = new JsonObject { ["phase"] = "Bound", ["capacity"] = new JsonObject { ["storage"] = "1Gi" } },
        });
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(KubernetesSetup.NodeNamespace));
        api.Add("api/v1", "pods", new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["name"] = "web-0", ["namespace"] = "web", ["uid"] = PodUid, ["annotations"] = new JsonObject { [PodVolumes.BackupAnnotation] = "cache" },
                ["ownerReferences"] = new JsonArray(new JsonObject { ["apiVersion"] = "apps/v1", ["kind"] = "StatefulSet", ["name"] = "web", ["uid"] = "uid-sts", ["controller"] = true }),
            },
            ["spec"] = new JsonObject
            {
                ["nodeName"] = "worker-1",
                ["containers"] = new JsonArray(new JsonObject
                {
                    ["name"] = "nginx", ["image"] = "nginx:1.27",
                    ["volumeMounts"] = new JsonArray(new JsonObject { ["name"] = "logs", ["mountPath"] = "/var/log/nginx" }),
                }),
                ["volumes"] = new JsonArray(
                    new JsonObject { ["name"] = "cache", ["emptyDir"] = new JsonObject() },
                    new JsonObject { ["name"] = "logs", ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = "logs-web" } }),
            },
            ["status"] = new JsonObject { ["phase"] = "Running" },
        });
        api.Add("api/v1", "pods", new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["name"] = "backdafuckup-node-x7k2p", ["namespace"] = KubernetesSetup.NodeNamespace, ["uid"] = "uid-agent",
                ["labels"] = new JsonObject { ["app.kubernetes.io/name"] = KubernetesSetup.NodeAgentName },
            },
            ["spec"] = new JsonObject { ["nodeName"] = "worker-1", ["containers"] = new JsonArray(new JsonObject { ["name"] = "node", ["image"] = "mover" }) },
            ["status"] = new JsonObject { ["phase"] = "Running" },
        });

        // The agent on worker-2 is stopping (an update of the DaemonSet), its exec fails: its numbers are missing, nothing else.
        api.Add("api/v1", "pods", new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["name"] = "backdafuckup-node-q4m8z", ["namespace"] = KubernetesSetup.NodeNamespace, ["uid"] = "uid-agent-2",
                ["labels"] = new JsonObject { ["app.kubernetes.io/name"] = KubernetesSetup.NodeAgentName },
            },
            ["spec"] = new JsonObject { ["nodeName"] = "worker-2", ["containers"] = new JsonArray(new JsonObject { ["name"] = "node", ["image"] = "mover" }) },
            ["status"] = new JsonObject { ["phase"] = "Running" },
        });

        var socket = Path.Combine(_root, "node.sock");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var agent = new NodeAgent(
            new NodeAgentOptions(new Uri("https://localhost/"), [], kubelet, socket, "worker-1", null, null, null, Racy: TimeSpan.Zero), TextWriter.Null, _ => _factory.Server.CreateHandler());
        var running = agent.RunAsync(stop.Token);
        var requests = new List<string>();
        _nodeRequests = async (pod, input) =>
        {
            lock (requests)
            {
                requests.Add($"{pod} {(string?)JsonNode.Parse(input)!["op"]}");
            }

            if (pod == "backdafuckup-node-q4m8z")
            {
                throw new k8s.KubernetesException(new k8s.Models.V1Status
                {
                    Status = "Failure", Message = "Internal error occurred: error executing command in container: failed to exec in container: cannot exec in a stopped state",
                });
            }

            // The kubelet's statistics as the agent cuts them; everything else through the agent's socket.
            if ((string?)JsonNode.Parse(input)!["op"] == "stats")
            {
                return (0, """{"node":"worker-1","pods":[{"namespace":"web","name":"web-0","volumes":{"cache":204800},"claims":{"logs-web":4096},"containers":{"nginx":8192}}]}""");
            }

            var output = new StringWriter();
            var code = await NodeRequest.RunAsync(new StringReader(input), output, socket, Ct);
            return (code, output.ToString());
        };

        try
        {
            var cluster = await AddClusterAsync(client, "rke2-node", "10.26.8.10");

            // The inventory: the CSI volume is read by the node agent, its size from the kubelet.
            var inventory = (await client.GetFromJsonAsync<List<InventoryItemDto>>($"/api/connections/{cluster.Id}/inventory", Ct))!;
            var claim = inventory.Single(i => i.Kind == "VolumeClaim" && i.Name == "logs-web");
            Assert.Equal(
                ("files", "node-agent", "no VolumeSnapshotClass for the CSI driver csi.plain.example: read file by file by the node agent from the mount of pod web-0 on node worker-1", "4096"),
                (claim.Properties["dataPath"], claim.Properties["readBy"], claim.Properties["dataPathReason"], claim.Properties["usedBytes"]));
            var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "Web", ConnectionId = cluster.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto("web", "web")], RetryCount = 0,
            }, Ct));

            var (state, log) = await RunAsync(client, job.Id);
            Assert.True(state == "Succeeded", log);
            Assert.Contains("web: Pod volumes read file by file by the node agent: StatefulSet web/cache (emptyDir, 200.0 KB, by its annotation).", log, StringComparison.Ordinal);
            Assert.Contains("read file by file by the node agent backdafuckup-node/backdafuckup-node-x7k2p on node worker-1 from " + cache, log, StringComparison.Ordinal);
            Assert.Contains("web: Usage numbers of web from the node agents (the kubelets' statistics): 1 pod volume(s), 1 container file system(s), 1 PVC(s), 0 write rate(s).", log, StringComparison.Ordinal);
            Assert.Contains("web: Volume web-0/cache: 2 file(s) in 1 folder(s)", log, StringComparison.Ordinal);
            Assert.Contains(
                "web: PVC logs-web: no VolumeSnapshotClass for the CSI driver csi.plain.example: read file by file by the node agent from the mount of pod web-0 on node worker-1.",
                log, StringComparison.Ordinal);
            Assert.Contains("read file by file by the node agent backdafuckup-node/backdafuckup-node-x7k2p on node worker-1 from " + logs, log, StringComparison.Ordinal);
            Assert.Contains("web: Volume logs-web: 1 file(s) in 0 folder(s)", log, StringComparison.Ordinal);
            Assert.Equal(2, requests.Count(r => r == "backdafuckup-node-x7k2p read"));
            Assert.Equal(1, requests.Count(r => r == "backdafuckup-node-q4m8z stats"));

            var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
            var store = await _factory.Services.GetRequiredService<RepositoryManager>().GetAsync(repository.Id, Ct);
            await using (var reader = await store.OpenRestorePointAsync(point.Id, Ct))
            {
                Assert.Equal(
                    "logs-web logs-web pvc node-agent; pod_web-0_cache web-0/cache pod-volume node-agent",
                    string.Join("; ", reader.Manifest.FileTrees.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} {t.Name} {t.Kind} {t.Reader}")));
                var root = await reader.ReadFileTreeIndexAsync("pod_web-0_cache", Ct);
                var entries = FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, reader.Manifest.Chunking.ChunkSize).ToDictionary(e => e.Path, e => e.Entry);
                Assert.Equal("<h1>cached</h1>"u8.ToArray(), ChunkStreams.ReadAll(entries["pages/index.html"].Content!, reader.Chunks, reader.Manifest.Chunking.ChunkSize));
                Assert.Equal("pod_web-0_cache", Assert.Single(reader.Manifest.Kubernetes!.PodVolumes).TreeKey);
                var record = Assert.Single(reader.Manifest.Kubernetes.Volumes);
                Assert.Equal(("files", "logs-web"), (record.DataPath, record.TreeKey));
            }

            Assert.True((await store.VerifyAsync(point.Id, VerifyDepth.Full, null, Ct)).Ok);

            // A restore of the namespace starts the pod with the volume empty, and says where its files are.
            var plan = await Read<KubernetesRestorePlanDto>(client.PostAsJsonAsync($"/api/restore-points/{point.Id}/kubernetes/restore-plan",
                new KubernetesRestorePlanRequestDto { TargetNamespace = "web-copy" }, Ct));
            Assert.Contains("Pods start with empty volumes; the files of web-0/cache are in the restore point: restore them under Files (browse, download, into a PVC or onto a share).", plan.Warnings);
            Assert.Contains(plan.Volumes, v => v.Claim == "logs-web" && v.Path == "files");

            // The next run compares the volume with this one (the pod keeps its name): nothing read again.
            (state, log) = await RunAsync(client, job.Id);
            Assert.True(state == "Succeeded", log);
            Assert.Contains("Volume web-0/cache: compared with the restore point of", log, StringComparison.Ordinal);
            Assert.Contains("2 file(s) unchanged since the previous run", log, StringComparison.Ordinal);
            Assert.Contains("Volume logs-web: compared with the restore point of", log, StringComparison.Ordinal);
        }
        finally
        {
            _nodeRequests = null;
            await stop.CancelAsync();
            await running;
        }
    }
}
