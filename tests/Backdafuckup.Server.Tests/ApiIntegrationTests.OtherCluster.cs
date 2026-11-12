using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Agents;
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

        // Into a class without a snapshot class: no block path (the files path comes with phase 2's files restore).
        other.Remove("namespaces", null, "bdfu-spike");
        foreach (var item in other.All("persistentvolumeclaims").Where(p => (string?)p["metadata"]!["namespace"] == "bdfu-spike").ToList())
        {
            other.Remove("persistentvolumeclaims", "bdfu-spike", (string)item["metadata"]!["name"]!);
        }

        (state, log) = await RestoreAsync(new KubernetesRestoreDto { StorageClassMap = new() { ["vsphere-csi"] = "nfs-client" } });
        Assert.True(state == "Failed", log);
        Assert.Contains("StorageClass nfs-client of cluster.local/nfs-subdir-external-provisioner, which has no VolumeSnapshotClass", log, StringComparison.Ordinal);

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
