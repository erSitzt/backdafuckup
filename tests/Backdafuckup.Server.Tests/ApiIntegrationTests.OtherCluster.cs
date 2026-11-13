using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing;
using Backdafuckup.Testing.Kubernetes;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>A namespace restored into another cluster (spec 2026-11-11-1343, phase 2): StorageClasses mapped, the target's own snapshot class.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_namespace_goes_into_another_cluster_with_its_storage_classes_mapped()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct));
        var volume = new byte[8 * 1024 * 1024];
        new Random(11).NextBytes(volume.AsSpan(0, 96 * 1024));
        new Random(12).NextBytes(volume.AsSpan(7 * 1024 * 1024, 4096));
        var device = Path.Combine(_root, "redis-volume.img");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(device, volume, Ct);
        SetUpCluster(device, [], []);
        var source = await AddClusterAsync(client, "rke2-test", "10.26.1.10");
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Namespaces", ConnectionId = source.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")], RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!);

        // The other cluster: another CSI driver with its own snapshot class, an NFS class, and no bdfu-spike.
        var other = new FakeKubernetesApi("uid-kube-system-target");
        other.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(KubernetesSetup.WorkNamespace));
        other.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "fast-csi" }, ["provisioner"] = "csi.other.example" });
        other.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "nfs-client" }, ["provisioner"] = "cluster.local/nfs-subdir-external-provisioner",
        });
        other.Add(Snapshots, "volumesnapshotclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "other-snaps" }, ["driver"] = "csi.other.example" });
        var restoredDevices = new List<string>();
        SetUpControllers(other, device, [], restoredDevices, driver: "csi.other.example");
        var snapshotClasses = new List<string?>();
        var controllers = other.Created!;
        other.Created = (plural, item) =>
        {
            if (plural == "volumesnapshots" && item["spec"]?["source"]?["persistentVolumeClaimName"] is not null)
            {
                lock (snapshotClasses)
                {
                    snapshotClasses.Add((string?)item["spec"]!["volumeSnapshotClassName"]);
                }
            }

            controllers(plural, item);
        };
        _clusters["10.26.2.10"] = other;
        var target = await AddClusterAsync(client, "rke2-target", "10.26.2.10");

        async Task<(string State, string Log)> RestoreAsync(KubernetesRestoreDto options, Guid? into = null)
        {
            var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
            {
                RestorePointId = point.Id, TargetConnectionId = into ?? target.Id, Mode = RestoreModeDto.AlternateLocation, Kubernetes = options,
            }, Ct));
            var session = await WaitForSessionAsync(client, started.SessionId);
            var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
            return (session.Session.State, string.Join("\n", log.Select(l => $"{l.Severity}: {l.Message}")));
        }

        // The preview: without a map the class is missing; mapped, the data path the restore will take, with what to look at.
        async Task<KubernetesRestorePlanDto> PlanAsync(Dictionary<string, string> map) => await Read<KubernetesRestorePlanDto>(client.PostAsJsonAsync(
            $"/api/restore-points/{point.Id}/kubernetes/restore-plan", new KubernetesRestorePlanRequestDto { TargetConnectionId = target.Id, StorageClassMap = map }, Ct));
        var plan = await PlanAsync([]);
        Assert.Equal((true, false, "bdfu-spike"), (plan.OtherCluster, plan.NamespaceExists, plan.TargetNamespace));
        Assert.Equal(["fast-csi", "nfs-client"], plan.StorageClasses);
        Assert.Equal(("missing", "vsphere-csi"), (Assert.Single(plan.Volumes).Path, plan.Volumes[0].TargetStorageClass));
        Assert.Contains(plan.Errors, e => e.Contains("map vsphere-csi to one of them", StringComparison.Ordinal));
        plan = await PlanAsync(new() { ["vsphere-csi"] = "fast-csi" });
        Assert.Equal(("block", "fast-csi"), (Assert.Single(plan.Volumes).Path, plan.Volumes[0].TargetStorageClass));
        Assert.Empty(plan.Errors);
        plan = await PlanAsync(new() { ["vsphere-csi"] = "nfs-client" });
        Assert.Equal("files", Assert.Single(plan.Volumes).Path);
        Assert.Contains(plan.Warnings, w => w.StartsWith("PVC data-redis-test-0 goes onto NFS", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Volumes that go in as files are read from their file system", StringComparison.Ordinal));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/restore-points/{point.Id}/kubernetes/restore-plan", new KubernetesRestorePlanRequestDto { TargetConnectionId = Guid.NewGuid() }, Ct)).StatusCode);

        // Without a map the target lacks the class: the restore names the classes it has, and nothing is created.
        var (state, log) = await RestoreAsync(new KubernetesRestoreDto());
        Assert.True(state == "Failed", log);
        Assert.Contains("PVC data-redis-test-0: the StorageClass vsphere-csi does not exist in cluster", log, StringComparison.Ordinal);
        Assert.Contains("(it has fast-csi, nfs-client); map vsphere-csi to one of them", log, StringComparison.Ordinal);
        Assert.Null(other.Find("namespaces", null, "bdfu-spike"));

        // Mapped to the CSI class: the volume goes in as a block device, snapshotted with the target's own class.
        (state, log) = await RestoreAsync(new KubernetesRestoreDto { StorageClassMap = new() { ["vsphere-csi"] = "fast-csi" } });
        Assert.True(state == "Succeeded", log);
        Assert.Contains("(cluster 10.26.1.10) into namespace bdfu-spike of cluster", log, StringComparison.Ordinal);
        Assert.Contains("PVC data-redis-test-0: its data goes into the StorageClass fast-csi", log, StringComparison.Ordinal);
        Assert.Contains("which has the VolumeSnapshotClass other-snaps", log, StringComparison.Ordinal);
        var claim = other.Find("persistentvolumeclaims", "bdfu-spike", "data-redis-test-0")!;
        Assert.Equal(("fast-csi", "VolumeSnapshot"), ((string?)claim["spec"]!["storageClassName"], (string?)claim["spec"]!["dataSource"]!["kind"]));
        Assert.All(snapshotClasses, c => Assert.Equal("other-snaps", c));
        Assert.NotEmpty(snapshotClasses);
        var written = await File.ReadAllBytesAsync(Assert.Single(restoredDevices), Ct);
        Assert.Equal(volume, written[..volume.Length]);
        Assert.NotNull(other.Find("configmaps", "bdfu-spike", "redis-config"));
        Assert.NotNull(other.Find("secrets", KubernetesSetup.WorkNamespace, "backdafuckup-registry")); // the pull secret: this cluster never ran a backup
        Assert.DoesNotContain(other.All("persistentvolumeclaims"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(other.All("pods"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(other.All("volumesnapshots"), v => (string?)v["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);

        // Into a new namespace name, the PVC and objects are the restore point's; the original cluster is untouched.
        Assert.Null(_cluster.Find("namespaces", null, "bdfu-spike-copy"));
        other.Remove("namespaces", null, "bdfu-spike");
        foreach (var item in other.All("persistentvolumeclaims").Where(p => (string?)p["metadata"]!["namespace"] == "bdfu-spike").ToList())
        {
            other.Remove("persistentvolumeclaims", "bdfu-spike", (string)item["metadata"]!["name"]!);
        }

        // The same namespace name in both clusters: two rows in the overview, each with its own restore points.
        var targetJob = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Target", ConnectionId = target.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")], RetryCount = 0,
        }, Ct));
        other.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("bdfu-spike"));
        var targetBackup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{targetJob.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, targetBackup.SessionId)).Session.State);
        var rows = (await client.GetFromJsonAsync<List<BackupVmDto>>("/api/restore-points/vms", Ct))!.Where(v => v.VmNativeId == "bdfu-spike").ToList();
        Assert.Equal(["uid-kube-system", "uid-kube-system-target"], rows.Select(r => r.PlatformId).Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(1, r.PointCount));
        var ofTarget = (await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike&platformId=uid-kube-system-target", Ct))!;
        Assert.Equal(targetJob.Id, Assert.Single(ofTarget).JobId);

        // A target that is no cluster is refused.
        var refused = await client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id, TargetConnectionId = Guid.NewGuid(), Mode = RestoreModeDto.AlternateLocation, Kubernetes = new KubernetesRestoreDto(),
        }, Ct);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("only be restored into a Kubernetes cluster", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_volume_goes_into_a_cluster_without_snapshots_as_files_and_is_handed_over_to_its_pvc()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "None", null, null, null), Ct));

        // The PVC's volume in the source cluster: an ext4 file system on the whole device.
        Directory.CreateDirectory(_root);
        var device = Path.Combine(_root, "ext4-volume.img");
        using (var image = File.OpenRead(FsFixtures.Image("ext4-gpt")))
        using (var copy = File.Create(device))
        {
            image.Position = 2048 * 512; // partition 1 starts at sector 2048
            image.CopyTo(copy);
        }

        SetUpCluster(device, [], [], new FileInfo(device).Length);
        var source = await AddClusterAsync(client, "rke2-test", "10.26.1.10");
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Namespaces", ConnectionId = source.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")], RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!);

        // A cluster with only an NFS class (nfs-subdir): a folder on the export per PVC, PVs that can change their PVC.
        var exports = Path.Combine(_root, "nfs-export");
        var nfs = NfsCluster("uid-kube-system-nfs", exports);
        _clusters["10.26.3.10"] = nfs;
        var target = await AddClusterAsync(client, "rke2-nfs", "10.26.3.10");

        var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id, TargetConnectionId = target.Id, Mode = RestoreModeDto.AlternateLocation,
            Kubernetes = new KubernetesRestoreDto { StorageClassMap = new() { ["vsphere-csi"] = "nfs-client" } },
        }, Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(session.Session.State is "Succeeded" or "Warning", log);
        Assert.Contains("PVC data-redis-test-0: its data goes into the StorageClass nfs-client of cluster", log, StringComparison.Ordinal);
        Assert.Contains("as files (StorageClass nfs-client of cluster.local/nfs-subdir-external-provisioner, which has no VolumeSnapshotClass)", log, StringComparison.Ordinal);

        // The PVC is bound to the volume the files went into, which has its reclaim policy back; the work objects are gone.
        var restoredClaim = nfs.Find("persistentvolumeclaims", "bdfu-spike", "data-redis-test-0")!;
        Assert.Equal("nfs-client", (string?)restoredClaim["spec"]!["storageClassName"]);
        var restoredVolume = nfs.Find("persistentvolumes", null, (string)restoredClaim["spec"]!["volumeName"]!)!;
        Assert.Equal(("Bound", "Delete", "data-redis-test-0", "bdfu-spike"), (
            (string?)restoredVolume["status"]!["phase"], (string?)restoredVolume["spec"]!["persistentVolumeReclaimPolicy"],
            (string?)restoredVolume["spec"]!["claimRef"]!["name"], (string?)restoredVolume["spec"]!["claimRef"]!["namespace"]));
        Assert.Contains("PVC bdfu-spike/data-redis-test-0: bound to its restored volume", log, StringComparison.Ordinal);
        Assert.DoesNotContain(nfs.All("persistentvolumeclaims"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(nfs.All("pods"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        Assert.DoesNotContain(nfs.All("secrets"), p => (string?)p["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);

        // The files of the volume are in its folder on the export, without lost+found; the root has the backed-up mode.
        var export = (string)restoredVolume["spec"]!["nfs"]!["path"]!;
        var expected = FsFixtures.Manifest("ext4-gpt.p1").ToDictionary(l => l[7], l => l[1]);
        Assert.Equal(expected["hello.txt"], Sha256(Path.Combine(export, "hello.txt")));
        Assert.Equal(expected["pattern-3m.bin"], Sha256(Path.Combine(export, "pattern-3m.bin")));
        Assert.Equal(expected["folder/sub/deep/nested.txt"], Sha256(Path.Combine(export, "folder/sub/deep/nested.txt")));
        Assert.Equal("hello.txt", new FileInfo(Path.Combine(export, "link-to-hello")).LinkTarget);
        Assert.False(Directory.Exists(Path.Combine(export, "lost+found")));
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Contains("restored, but owners are not kept", log, StringComparison.Ordinal); // the test runs without root, like an export with root_squash
        }

        static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }

    /// <summary>
    /// A cluster with only the NFS class <c>nfs-client</c> (nfs-subdir, the default): a folder in <paramref name="exports"/>
    /// per new PVC, PVs that change their PVC when reserved for another, a files mover pod that is the real mover program on
    /// its PVC's folder, CRDs established at once.
    /// </summary>
    private FakeKubernetesApi NfsCluster(string kubeSystemUid, string exports)
    {
        var nfs = new FakeKubernetesApi(kubeSystemUid);
        nfs.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(KubernetesSetup.WorkNamespace));
        nfs.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "nfs-client", ["annotations"] = new JsonObject { ["storageclass.kubernetes.io/is-default-class"] = "true" } },
            ["provisioner"] = "cluster.local/nfs-subdir-external-provisioner",
        });
        Directory.CreateDirectory(exports);
        nfs.Created = (plural, item) =>
        {
            var ns = (string?)item["metadata"]!["namespace"];
            var name = (string)item["metadata"]!["name"]!;
            switch (plural)
            {
                case "customresourcedefinitions":
                    // The API server: a new CRD is established at once.
                    nfs.Mutate(() => item["status"] = new JsonObject
                    {
                        ["conditions"] = new JsonArray(new JsonObject { ["type"] = "Established", ["status"] = "True" }),
                    });
                    break;
                case "persistentvolumeclaims" when item["spec"] is JsonObject claimSpec && claimSpec["volumeName"] is null:
                {
                    // The provisioner: a folder on the export.
                    var folder = Path.Combine(exports, $"{ns}-{name}");
                    Directory.CreateDirectory(folder);
                    nfs.Add("api/v1", "persistentvolumes", new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = $"pv-{name}", ["annotations"] = new JsonObject { ["pv.kubernetes.io/provisioned-by"] = "cluster.local/nfs-subdir-external-provisioner" } },
                        ["spec"] = new JsonObject
                        {
                            ["nfs"] = new JsonObject { ["server"] = "10.26.9.9", ["path"] = folder },
                            ["storageClassName"] = (string?)item["spec"]!["storageClassName"],
                            ["persistentVolumeReclaimPolicy"] = "Delete",
                            ["claimRef"] = new JsonObject { ["namespace"] = ns, ["name"] = name, ["uid"] = $"uid-{name}" },
                        },
                        ["status"] = new JsonObject { ["phase"] = "Bound" },
                    });
                    nfs.Mutate(() =>
                    {
                        item["spec"]!["volumeName"] = $"pv-{name}";
                        item["status"] = new JsonObject { ["phase"] = "Bound" };
                    });
                    break;
                }

                case "persistentvolumeclaims":
                {
                    // A PVC naming its PV: bound once the PV is reserved for it.
                    var pv = nfs.Find("persistentvolumes", null, (string)item["spec"]!["volumeName"]!)!;
                    if ((string?)pv["spec"]!["claimRef"]?["name"] == name && (string?)pv["spec"]!["claimRef"]?["namespace"] == ns)
                    {
                        nfs.Mutate(() =>
                        {
                            pv["spec"]!["claimRef"]!["uid"] = $"uid-{ns}-{name}";
                            pv["status"] = new JsonObject { ["phase"] = "Bound" };
                            item["status"] = new JsonObject { ["phase"] = "Bound" };
                        });
                    }

                    break;
                }

                case "pods" when ns == KubernetesSetup.WorkNamespace:
                {
                    // The kubelet: the files mover (or the tree mover reading a volume) with its PVC's folder on the export mounted.
                    var env = item["spec"]!["containers"]![0]!["env"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => (string?)e!["value"]);
                    var token = (string)nfs.Find("secrets", ns, (string)item["spec"]!["containers"]![0]!["env"]!.AsArray()
                        .Single(e => (string?)e!["name"] == "BDFU_MOVER_TOKEN")!["valueFrom"]!["secretKeyRef"]!["name"]!)!["stringData"]!["token"]!;
                    var claim = (string)item["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]!;
                    var volume = (string)nfs.Find("persistentvolumeclaims", ns, claim)!["spec"]!["volumeName"]!;
                    var folder = (string)nfs.Find("persistentvolumes", null, volume)!["spec"]!["nfs"]!["path"]!;
                    var options = new Backdafuckup.Mover.MoverOptions(
                        env["BDFU_MOVER_MODE"] == "tree" ? Backdafuckup.Mover.MoverMode.Tree : Backdafuckup.Mover.MoverMode.Files,
                        new Uri(env["BDFU_MOVER_DATA_URL"]!), new Uri(env["BDFU_MOVER_RESULT_URL"]!), token, [], string.Empty,
                        Root: folder, Keep: env.GetValueOrDefault("BDFU_MOVER_IF_EXISTS") == "keep", Owners: env.GetValueOrDefault("BDFU_MOVER_OWNERS") == "1",
                        PreviousUrl: env.GetValueOrDefault("BDFU_MOVER_PREVIOUS_URL") is { } previous ? new Uri(previous) : null,
                        Racy: TimeSpan.Zero); // files written just before count as settled: the test's next run is moments later
                    nfs.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Running" });
                    _ = Task.Run(async () =>
                    {
                        var exit = await Backdafuckup.Mover.MoverRunner.RunAsync(options, _factory!.Server.CreateHandler(), new StringWriter(), CancellationToken.None);
                        nfs.Mutate(() => item["status"] = new JsonObject { ["phase"] = exit == 0 ? "Succeeded" : "Failed" });
                    });
                    break;
                }
            }
        };
        nfs.Deleted = (plural, item) =>
        {
            if (plural == "persistentvolumeclaims" && (string?)item["spec"]?["volumeName"] is { } volume && nfs.Find("persistentvolumes", null, volume) is { } pv)
            {
                nfs.Mutate(() => pv["status"] = new JsonObject { ["phase"] = "Released" });
            }
        };
        nfs.Patched = (plural, item) =>
        {
            if (plural == "persistentvolumes" && item["spec"]?["claimRef"] is JsonObject claimRef && claimRef["uid"] is null && (string?)item["status"]?["phase"] == "Released")
            {
                nfs.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Available" });
            }
        };
        return nfs;
    }

    /// <summary>Adds a Kubernetes cluster (through its CA) at <paramref name="address"/>; a fake in <see cref="_clusters"/> answers for it.</summary>
    private async Task<ConnectionDto> AddClusterAsync(HttpClient client, string name, string address)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest($"CN={name}-server-ca", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "Kubernetes", DisplayName = name, Address = address, Port = 6443, UserName = "backdafuckup", Password = "token-for-tests", CaCertificatePem = ca.ExportCertificatePem(),
        }, Ct));
    }
}
