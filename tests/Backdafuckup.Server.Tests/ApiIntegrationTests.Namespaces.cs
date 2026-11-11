using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.DataMover;
using Backdafuckup.Kubernetes;
using Backdafuckup.Mover;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// A namespace backed up end to end: job, engine, snapshot in the (fake) cluster, a mover pod that is the real mover
/// program reading a file, the server's channel, the repository.
/// </summary>
public sealed partial class ApiIntegrationTests
{
    private const string Snapshots = "apis/snapshot.storage.k8s.io/v1";

    [Fact]
    public async Task A_namespace_job_backs_up_the_objects_and_the_pvc_data_through_a_mover_pod_and_leaves_nothing_in_the_cluster()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct));

        // The volume's device: 8 MiB with data in two places.
        var volume = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(volume.AsSpan(0, 64 * 1024));
        new Random(43).NextBytes(volume.AsSpan(5 * 1024 * 1024, 4096));
        var device = Path.Combine(_root, "redis-volume.img");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(device, volume, Ct);
        var movers = new List<string>();
        SetUpCluster(device, movers, []);

        // The cluster, through its CA.
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=rke2-server-ca", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var cluster = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "Kubernetes",
            DisplayName = "rke2-test",
            Address = "10.26.1.10",
            Port = 6443,
            UserName = "backdafuckup",
            Password = "token-for-tests",
            CaCertificatePem = ca.ExportCertificatePem(),
        }, Ct));
        var inventory = await client.GetFromJsonAsync<List<InventoryItemDto>>($"/api/connections/{cluster.Id}/inventory", Ct);
        Assert.Contains(inventory!, i => i.Kind == "Namespace" && i.NativeId == "bdfu-spike" && i.Properties["snapshotVolumes"] == "1");

        // A job with guest processing is refused for a cluster; one with the namespace is saved.
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var save = new SaveJobDto
        {
            Name = "Namespaces",
            ConnectionId = cluster.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")],
            RetryCount = 0,
        };
        var refused = await client.PostAsJsonAsync("/api/jobs", save with { GuestProcessing = true }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("no guest processing", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", save, Ct));

        // First run: the image is pushed, the hooks run, the volume is read by its mover pod.
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
        var logText = string.Join("\n", log.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(session.Session.State == "Succeeded", logText);
        Assert.Contains(log, l => l.Message.StartsWith("Job started (incremental), 1 namespace(s)", StringComparison.Ordinal));
        Assert.Contains("bdfu-spike: Pre-hook in bdfu-spike/redis-test-0 (redis): redis-cli save – done", logText, StringComparison.Ordinal);
        Assert.Contains("bdfu-spike: Volume data-redis-test-0: full backup (Kubernetes volumes have no change tracking", logText, StringComparison.Ordinal);
        Assert.Contains("bdfu-spike: Volume data-redis-test-0: Full via mover", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(log, l => l.Severity is "Warning" or "Error");
        Assert.True(_registry.Manifests.Count == 1, "the mover image is pushed by the first backup");
        Assert.Single(movers);

        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!);
        Assert.Equal((RestorePointObjectKinds.KubernetesNamespace, "application", "bdfu-spike"), (point.ObjectKind, point.Consistency, point.VmName));
        var disk = Assert.Single(point.Disks);
        Assert.Equal(("data-redis-test-0", 8L * 1024 * 1024), (disk.Key, disk.CapacityBytes));

        // The repository holds the volume as it was, and the namespace's objects with the descriptor.
        var store = await _factory.Services.GetRequiredService<RepositoryManager>().GetAsync(repository.Id, Ct);
        await using (var reader = await store.OpenRestorePointAsync(point.Id, Ct))
        {
            var image = await RestorePointDiskImage.OpenAsync(reader, "data-redis-test-0", Ct);
            var restored = new byte[volume.Length];
            await image.ReadAsync(0, restored, Ct);
            Assert.Equal(volume, restored);
            var manifest = reader.Manifest;
            Assert.Equal(("bdfu-spike", "10.26.1.10", 2), (manifest.Kubernetes!.Namespace, manifest.Kubernetes.Cluster, manifest.Kubernetes.Hooks.Count));
            await using var objects = await reader.OpenBlobAsync(manifest.Metadata!, Ct);
            var lines = new StreamReader(objects).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(lines, l => l.Contains("\"kind\":\"ConfigMap\"", StringComparison.Ordinal) && l.Contains("redis-config", StringComparison.Ordinal));
            Assert.Equal(manifest.Kubernetes.ObjectCount, lines.Length);
        }

        // Nothing left: no snapshot, no work PVC, no mover pod; every ledger entry done.
        AssertClusterClean();
        await using (var db = await _factory.Services.GetRequiredService<IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var artifacts = await db.Artifacts.ToListAsync(Ct);
            Assert.Equal(6, artifacts.Count); // snapshot, content, exposed snapshot, PVC, token secret, pod (the pull secret stays: no artifact)
            Assert.All(artifacts, a => Assert.Equal(ArtifactState.Done, a.State));
        }

        // Second run: read in full again without a warning, the repository stores (almost) nothing new, the image is not pushed again.
        var again = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        var second = await WaitForSessionAsync(client, again.SessionId);
        Assert.Equal("Succeeded", second.Session.State);
        Assert.Equal(2, movers.Count);
        Assert.Single(_registry.Manifests);
        var points = (await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!;
        Assert.Equal(2, points.Count);
        Assert.True(points.OrderBy(p => p.CreatedUtc).Last().NewStoredBytes < 64 * 1024, "unchanged chunks are not stored again");
        AssertClusterClean();
    }

    [Fact]
    public async Task A_namespace_comes_back_into_another_namespace_with_its_volume_and_a_pvc_next_to_the_original()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "None", null, null, null), Ct));
        var volume = new byte[8 * 1024 * 1024];
        new Random(7).NextBytes(volume.AsSpan(0, 100 * 1024));
        new Random(8).NextBytes(volume.AsSpan(6 * 1024 * 1024, 8192));
        var device = Path.Combine(_root, "redis-volume.img");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(device, volume, Ct);
        var restoredDevices = new List<string>();
        SetUpCluster(device, [], restoredDevices);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=rke2-server-ca", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var cluster = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "Kubernetes", DisplayName = "rke2-test", Address = "10.26.1.10", Port = 6443, UserName = "backdafuckup", Password = "token-for-tests", CaCertificatePem = ca.ExportCertificatePem(),
        }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Namespaces", ConnectionId = cluster.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")], RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!);

        // What the restore point holds, for the console: volumes, objects, and what the pod needs.
        var details = (await client.GetFromJsonAsync<KubernetesRestorePointDto>($"/api/restore-points/{point.Id}/kubernetes", Ct))!;
        Assert.Equal(("bdfu-spike", "snapshot"), (details.Namespace, Assert.Single(details.Volumes).DataPath));
        var pod = details.Objects.Single(o => o.Kind == "Pod");
        Assert.Equal(["PersistentVolumeClaim/data-redis-test-0"], pod.Needs);
        Assert.Contains(details.Objects, o => o.Kind == "ConfigMap" && o.Name == "redis-config");

        async Task<(SessionDetailDto Session, string Log)> RestoreAsync(KubernetesRestoreDto options)
        {
            var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
            {
                RestorePointId = point.Id, TargetConnectionId = cluster.Id, Mode = RestoreModeDto.OriginalLocation, Kubernetes = options,
            }, Ct));
            var session = await WaitForSessionAsync(client, started.SessionId);
            var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
            return (session, string.Join("\n", log.Select(l => $"{l.Severity}: {l.Message}")));
        }

        // Into the original namespace: refused, its PVC with data exists.
        var (refused, refusedLog) = await RestoreAsync(new KubernetesRestoreDto());
        Assert.Equal("Failed", refused.Session.State);
        Assert.Contains("The PVC bdfu-spike/data-redis-test-0 exists in cluster 10.26.1.10", refusedLog, StringComparison.Ordinal);
        Assert.Empty(restoredDevices);

        // Into a new namespace: the volume written by a mover, the PVC bound to it through a static PV, the objects in order.
        var (copy, copyLog) = await RestoreAsync(new KubernetesRestoreDto { TargetNamespace = "bdfu-spike-copy" });
        Assert.True(copy.Session.State == "Succeeded", copyLog);
        Assert.NotNull(_cluster.Find("namespaces", null, "bdfu-spike-copy"));
        // The PVC is made from a snapshot of the restored volume (an ordinary volume of the driver); the snapshots are gone.
        var restoredClaim = _cluster.Find("persistentvolumeclaims", "bdfu-spike-copy", "data-redis-test-0")!;
        Assert.Equal(("VolumeSnapshot", "snapshot.storage.k8s.io"), ((string?)restoredClaim["spec"]!["dataSource"]!["kind"], (string?)restoredClaim["spec"]!["dataSource"]!["apiGroup"]));
        Assert.Null(restoredClaim["spec"]!["volumeName"]);
        var device1 = Assert.Single(restoredDevices);
        Assert.Contains($"PVC bdfu-spike-copy/data-redis-test-0: created from the snapshot of its restored volume", copyLog, StringComparison.Ordinal);
        Assert.NotNull(_cluster.Find("configmaps", "bdfu-spike-copy", "redis-config"));
        Assert.NotNull(_cluster.Find("pods", "bdfu-spike-copy", "redis-test-0"));
        var written = await File.ReadAllBytesAsync(device1, Ct);
        Assert.Equal(volume, written[..volume.Length]);
        Assert.Contains("Volume data-redis-test-0: restored", copyLog, StringComparison.Ordinal);
        AssertClusterClean();

        // One PVC next to the original, under a new name; nothing else is touched.
        var configMaps = _cluster.All("configmaps").Count;
        var (single, singleLog) = await RestoreAsync(new KubernetesRestoreDto
        {
            OnlyClaims = ["data-redis-test-0"], ClaimNames = new() { ["data-redis-test-0"] = "data-redis-test-0-restored" },
        });
        Assert.True(single.Session.State == "Succeeded", singleLog);
        Assert.NotNull(_cluster.Find("persistentvolumeclaims", "bdfu-spike", "data-redis-test-0-restored"));
        Assert.Equal(configMaps, _cluster.All("configmaps").Count);
        Assert.Equal(2, restoredDevices.Count);
        Assert.Equal(volume, (await File.ReadAllBytesAsync(restoredDevices[1], Ct))[..volume.Length]);
        AssertClusterClean();
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct);
        Assert.All(await db.Artifacts.ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
    }

    private void AssertClusterClean()
    {
        Assert.Empty(_cluster.All("volumesnapshots"));
        Assert.Empty(_cluster.All("volumesnapshotcontents"));
        Assert.DoesNotContain(_cluster.All("pods"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(_cluster.All("persistentvolumeclaims"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(_cluster.All("secrets"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace
            && ((string?)p["metadata"]!["name"])!.EndsWith("-token", StringComparison.Ordinal));
    }

    /// <summary>
    /// bdfu-spike with a Redis pod (hooks), a ConfigMap and its PVC on a CSI driver with a snapshot class. The controllers
    /// make snapshots ready, and a mover pod runs the real mover program with <paramref name="device"/> as its volume.
    /// </summary>
    private void SetUpCluster(string device, List<string> movers, List<string> restoredDevices)
    {
        var api = _cluster;
        api.Add("api/v1", "namespaces", Backdafuckup.Testing.Kubernetes.FakeKubernetesApi.Namespace("bdfu-spike"));
        api.Add("api/v1", "namespaces", Backdafuckup.Testing.Kubernetes.FakeKubernetesApi.Namespace(KubernetesSetup.WorkNamespace));
        api.Add("api/v1", "configmaps", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "redis-config", ["namespace"] = "bdfu-spike" } });
        api.Add("api/v1", "pods", new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["name"] = "redis-test-0",
                ["namespace"] = "bdfu-spike",
                ["annotations"] = new JsonObject { ["pre.hook.backup.velero.io/command"] = "[\"redis-cli\", \"save\"]", ["post.hook.backup.velero.io/command"] = "[\"true\"]" },
            },
            ["spec"] = new JsonObject
            {
                ["containers"] = new JsonArray(new JsonObject { ["name"] = "redis", ["image"] = "redis:7" }),
                ["volumes"] = new JsonArray(new JsonObject { ["name"] = "data", ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = "data-redis-test-0" } }),
            },
            ["status"] = new JsonObject { ["phase"] = "Running" },
        });
        api.Add("api/v1", "persistentvolumeclaims", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "data-redis-test-0", ["namespace"] = "bdfu-spike" },
            ["spec"] = new JsonObject { ["volumeName"] = "pvc-1", ["storageClassName"] = "vsphere-csi", ["volumeMode"] = "Filesystem", ["accessModes"] = new JsonArray("ReadWriteOnce") },
            ["status"] = new JsonObject { ["phase"] = "Bound", ["capacity"] = new JsonObject { ["storage"] = "8Mi" } },
        });
        api.Add("api/v1", "persistentvolumes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "pvc-1" },
            ["spec"] = new JsonObject { ["storageClassName"] = "vsphere-csi", ["csi"] = new JsonObject { ["driver"] = "csi.vsphere.vmware.com", ["volumeHandle"] = "fcd-1" } },
        });
        api.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "vsphere-csi" }, ["provisioner"] = "csi.vsphere.vmware.com" });
        api.Add(Snapshots, "volumesnapshotclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "vsphere-snapshots" }, ["driver"] = "csi.vsphere.vmware.com" });

        api.Created = (plural, item) =>
        {
            var ns = (string?)item["metadata"]!["namespace"];
            var name = (string)item["metadata"]!["name"]!;
            switch (plural)
            {
                case "volumesnapshots" when (string?)item["spec"]!["source"]!["persistentVolumeClaimName"] is not null:
                    api.Add(Snapshots, "volumesnapshotcontents", new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = $"snapcontent-{name}" },
                        ["spec"] = new JsonObject { ["driver"] = "csi.vsphere.vmware.com", ["deletionPolicy"] = "Delete" },
                        ["status"] = new JsonObject { ["snapshotHandle"] = "fcd-1+snap-1", ["restoreSize"] = 8L * 1024 * 1024, ["readyToUse"] = true },
                    });
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = $"snapcontent-{name}" });
                    break;
                case "volumesnapshots":
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = (string?)item["spec"]!["source"]!["volumeSnapshotContentName"] });
                    break;
                case "persistentvolumeclaims" when ns == KubernetesSetup.WorkNamespace && (item["spec"] as JsonObject)?.ContainsKey("dataSource") != true:
                    // The CSI driver: a new volume for a restore's work PVC.
                    api.Add("api/v1", "persistentvolumes", new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = $"pv-{name}" },
                        ["spec"] = new JsonObject
                        {
                            ["capacity"] = new JsonObject { ["storage"] = (string?)item["spec"]!["resources"]!["requests"]!["storage"] },
                            ["csi"] = new JsonObject { ["driver"] = "csi.vsphere.vmware.com", ["volumeHandle"] = $"fcd-{name}" },
                            ["persistentVolumeReclaimPolicy"] = "Delete",
                            ["volumeMode"] = "Block",
                        },
                    });
                    api.Mutate(() =>
                    {
                        item["spec"]!["volumeName"] = $"pv-{name}";
                        item["status"] = new JsonObject { ["phase"] = "Bound" };
                    });
                    break;
                case "persistentvolumeclaims":
                    api.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Bound" });
                    break;
                case "pods" when ns == KubernetesSetup.WorkNamespace:
                    // The kubelet: the pod runs the mover with the volume's device.
                    var env = item["spec"]!["containers"]![0]!["env"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => (string?)e!["value"]);
                    var token = (string)api.Find("secrets", ns, (string)item["spec"]!["containers"]![0]!["env"]!.AsArray()
                        .Single(e => (string?)e!["name"] == "BDFU_MOVER_TOKEN")!["valueFrom"]!["secretKeyRef"]!["name"]!)!["stringData"]!["token"]!;
                    var restore = env["BDFU_MOVER_MODE"] == "restore";
                    var target = device;
                    if (restore)
                    {
                        // The new volume: a file of the work PVC's size.
                        var claim = (string)item["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]!;
                        var size = long.Parse((string)api.Find("persistentvolumeclaims", ns, claim)!["spec"]!["resources"]!["requests"]!["storage"]!, System.Globalization.CultureInfo.InvariantCulture);
                        target = Path.Combine(_root, $"{claim}.img");
                        using (var file = File.Create(target))
                        {
                            file.SetLength(size);
                        }

                        lock (restoredDevices)
                        {
                            restoredDevices.Add(target);
                        }
                    }

                    var options = new MoverOptions(
                        restore ? MoverMode.Restore : MoverMode.Backup, new Uri(env["BDFU_MOVER_DATA_URL"]!), new Uri(env["BDFU_MOVER_RESULT_URL"]!), token, [], target, 4096,
                        restore ? null : long.Parse(env["BDFU_MOVER_BYTES"]!, System.Globalization.CultureInfo.InvariantCulture));
                    api.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Running" });
                    lock (movers)
                    {
                        movers.Add(name);
                    }

                    _ = Task.Run(async () =>
                    {
                        var exit = await MoverRunner.RunAsync(options, _factory!.Server.CreateHandler(), new StringWriter(), CancellationToken.None);
                        api.Mutate(() => item["status"] = new JsonObject { ["phase"] = exit == 0 ? "Succeeded" : "Failed" });
                    });
                    break;
            }
        };
        api.Deleted = (plural, item) =>
        {
            if (plural == "volumesnapshots" && (string?)item["status"]?["boundVolumeSnapshotContentName"] is { } content && content.StartsWith("snapcontent-", StringComparison.Ordinal))
            {
                api.Remove("volumesnapshotcontents", null, content);
            }
        };
    }

    /// <summary>Hooks in the fake cluster succeed.</summary>
    private sealed class HookRecorder : IPodExecutor
    {
        public Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, CancellationToken cancellationToken) =>
            Task.FromResult((0, "OK"));
    }
}
