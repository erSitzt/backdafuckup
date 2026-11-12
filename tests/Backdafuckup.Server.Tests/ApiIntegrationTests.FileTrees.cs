using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Repository;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Engine;
using Backdafuckup.Testing.Kubernetes;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Volumes without CSI snapshots backed up file by file (spec 2026-11-12-1952-kubernetes-file-level-volumes).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task An_nfs_volume_is_backed_up_file_by_file_through_a_read_only_copy_of_its_pv_and_nothing_is_left()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "None", null, null, null), Ct));

        // An NFS cluster with a namespace whose PVC lives in a folder on the export.
        var exports = Path.Combine(_root, "exports");
        var nfs = NfsCluster("uid-kube-system-files", exports);
        _clusters["10.26.6.10"] = nfs;
        var folder = Path.Combine(exports, "web-data-web");
        Directory.CreateDirectory(Path.Combine(folder, "site"));
        var page = RandomNumberGenerator.GetBytes(300_000);
        await File.WriteAllBytesAsync(Path.Combine(folder, "site", "index.bin"), page, Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "config.txt"), "port=8080", Ct);
        nfs.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("web"));
        nfs.Add("api/v1", "persistentvolumes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "pv-data-web", ["annotations"] = new JsonObject { ["pv.kubernetes.io/provisioned-by"] = "cluster.local/nfs-subdir-external-provisioner" } },
            ["spec"] = new JsonObject
            {
                ["capacity"] = new JsonObject { ["storage"] = "1Gi" },
                ["nfs"] = new JsonObject { ["server"] = "10.26.9.9", ["path"] = folder },
                ["storageClassName"] = "nfs-client",
                ["persistentVolumeReclaimPolicy"] = "Delete",
                ["claimRef"] = new JsonObject { ["namespace"] = "web", ["name"] = "data-web", ["uid"] = "uid-data-web" },
            },
            ["status"] = new JsonObject { ["phase"] = "Bound" },
        });
        nfs.Add("api/v1", "persistentvolumeclaims", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "data-web", ["namespace"] = "web" },
            ["spec"] = new JsonObject { ["volumeName"] = "pv-data-web", ["storageClassName"] = "nfs-client", ["accessModes"] = new JsonArray("ReadWriteMany") },
            ["status"] = new JsonObject { ["phase"] = "Bound", ["capacity"] = new JsonObject { ["storage"] = "1Gi" } },
        });
        var cluster = await AddClusterAsync(client, "rke2-files", "10.26.6.10");
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Web", ConnectionId = cluster.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("web", "web")], RetryCount = 0,
        }, Ct));

        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(session.Session.State == "Succeeded", log);
        Assert.Contains("PVC data-web: NFS volume (10.26.9.9:", log, StringComparison.Ordinal);
        Assert.Contains("Volume data-web: 2 file(s) in 1 folder(s)", log, StringComparison.Ordinal);
        Assert.Contains("read file by file through a read-only copy of PV pv-data-web (NFS 10.26.9.9:", log, StringComparison.Ordinal);

        // The restore point holds the tree: the same files, the same bytes.
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
        var store = await _factory.Services.GetRequiredService<RepositoryManager>().GetAsync(repository.Id, Ct);
        await using (var reader = await store.OpenRestorePointAsync(point.Id, Ct))
        {
            var tree = Assert.Single(reader.Manifest.FileTrees);
            Assert.Equal(("data-web", "shadow-pv", 2L), (tree.Key, tree.Reader, tree.Stats.Files));
            Assert.Equal("data-web", reader.Manifest.Kubernetes!.Volumes.Single().TreeKey);
            var root = await reader.ReadFileTreeIndexAsync("data-web", Ct);
            var entries = FileTreeWalker.Enumerate(ChunkStreamRef.OfIds(root.DiskSize, root.Entries), reader.Chunks, reader.Manifest.Chunking.ChunkSize).ToDictionary(e => e.Path, e => e.Entry);
            Assert.Equal(["config.txt", "site", "site/index.bin"], entries.Keys.Order(StringComparer.Ordinal));
            Assert.Equal(page, ChunkStreams.ReadAll(entries["site/index.bin"].Content!, reader.Chunks, reader.Manifest.Chunking.ChunkSize));
        }

        // Browsing: the tree is a Unix volume named after its PVC; folders list, files download as they were.
        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = Assert.Single(mount.Volumes);
        Assert.Equal(("data-web:files", "files", "unix", true), (volume.Id, volume.FileSystem, volume.Family, volume.Readable));
        var top = (await client.GetFromJsonAsync<GuestEntryPageDto>($"/api/file-restore/mounts/{mount.Id}/entries?volume={Uri.EscapeDataString(volume.Id)}&path=/", Ct))!;
        Assert.Equal([("config.txt", "file"), ("site", "directory")], top.Entries.Select(e => (e.Name, e.Kind)).Order());
        var download = await client.GetByteArrayAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=/site/index.bin", Ct);
        Assert.Equal(page, download);
        (await client.DeleteAsync($"/api/file-restore/mounts/{mount.Id}", Ct)).EnsureSuccessStatusCode();

        // The namespace restored into a new one: the PVC gets a new volume (the provisioner's), filled with its files.
        var restored = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, Kubernetes = new KubernetesRestoreDto { TargetNamespace = "web-restore" },
        }, Ct));
        var restoreSession = await WaitForSessionAsync(client, restored.SessionId);
        var restoreLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{restored.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(restoreSession.Session.State == (Environment.IsPrivilegedProcess ? "Succeeded" : "Warning"), restoreLog); // without root: owners not kept
        Assert.Contains("PVC data-web: its files go into a new volume of the StorageClass nfs-client", restoreLog, StringComparison.Ordinal);
        var claim = nfs.Find("persistentvolumeclaims", "web-restore", "data-web")!;
        var target = (string)nfs.Find("persistentvolumes", null, (string)claim["spec"]!["volumeName"]!)!["spec"]!["nfs"]!["path"]!;
        Assert.NotEqual(folder, target);
        Assert.Equal(page, await File.ReadAllBytesAsync(Path.Combine(target, "site", "index.bin"), Ct));
        Assert.Equal("port=8080", await File.ReadAllTextAsync(Path.Combine(target, "config.txt"), Ct));

        // Nothing left in the cluster; the export's PV untouched (its policy and the provisioner's annotation).
        Assert.DoesNotContain(nfs.All("persistentvolumes"), p => p["metadata"]?["labels"]?["backdafuckup.io/shadow-of"] is not null);
        Assert.Empty(nfs.All("pods"));
        Assert.DoesNotContain(nfs.All("persistentvolumeclaims"), c => (string?)c["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        var source = nfs.Find("persistentvolumes", null, "pv-data-web")!;
        Assert.Equal(("Delete", "cluster.local/nfs-subdir-external-provisioner"),
            ((string?)source["spec"]!["persistentVolumeReclaimPolicy"], (string?)source["metadata"]!["annotations"]!["pv.kubernetes.io/provisioned-by"]));

        // Into a class that cannot share a volume (local-path refuses ReadWriteMany with a warning on the PVC, as the real one
        // does): the preview says so; the restore makes the volume ReadWriteOnce instead, and the PVC is restored that way.
        RefuseSharedVolumes(nfs, "local-path");
        var plan = await Read<KubernetesRestorePlanDto>(client.PostAsJsonAsync($"/api/restore-points/{point.Id}/kubernetes/restore-plan", new KubernetesRestorePlanRequestDto
        {
            TargetNamespace = "web-local", StorageClassMap = new Dictionary<string, string> { ["nfs-client"] = "local-path" },
        }, Ct));
        Assert.Contains(plan.Warnings, w => w.StartsWith("PVC data-web is ReadWriteMany: should the StorageClass local-path (rancher.io/local-path) not provide that", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Volumes backed up file by file go back as files", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("ext or XFS only", StringComparison.Ordinal));
        var local = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation,
            Kubernetes = new KubernetesRestoreDto { TargetNamespace = "web-local", StorageClassMap = new Dictionary<string, string> { ["nfs-client"] = "local-path" } },
        }, Ct));
        var localSession = await WaitForSessionAsync(client, local.SessionId);
        var localLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{local.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}"));
        Assert.True(localSession.Session.State == "Warning", localLog);
        Assert.True(localLog.Contains("Warning: web: PVC web-local/data-web (cluster 10.26.6.10): the StorageClass local-path cannot make a volume that is ReadWriteMany (failed to provision volume", StringComparison.Ordinal), localLog);
        Assert.Contains("the volume is made ReadWriteOnce instead", localLog, StringComparison.Ordinal);
        var localClaim = nfs.Find("persistentvolumeclaims", "web-local", "data-web")!;
        Assert.Equal(["ReadWriteOnce"], localClaim["spec"]!["accessModes"]!.AsArray().Select(m => (string?)m));
        var localFolder = (string)nfs.Find("persistentvolumes", null, (string)localClaim["spec"]!["volumeName"]!)!["spec"]!["nfs"]!["path"]!;
        Assert.Equal(page, await File.ReadAllBytesAsync(Path.Combine(localFolder, "site", "index.bin"), Ct));
        Assert.Empty(nfs.All("pods"));
        Assert.DoesNotContain(nfs.All("persistentvolumeclaims"), c => (string?)c["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
    }

    /// <summary>
    /// Adds the class <paramref name="storageClass"/> (rancher.io/local-path) to an <see cref="NfsCluster"/>: a PVC of it that
    /// asks for ReadWriteMany is never provisioned, only warned about (ProvisioningFailed), and a pod on it stays pending;
    /// a ReadWriteOnce PVC gets a folder on the export like any other.
    /// </summary>
    private static void RefuseSharedVolumes(FakeKubernetesApi cluster, string storageClass)
    {
        cluster.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = storageClass }, ["provisioner"] = "rancher.io/local-path", ["volumeBindingMode"] = "WaitForFirstConsumer",
        });
        var provision = cluster.Created!;
        cluster.Created = (plural, item) =>
        {
            var ns = (string?)item["metadata"]!["namespace"];
            var name = (string)item["metadata"]!["name"]!;
            if (plural == "persistentvolumeclaims" && (string?)item["spec"]?["storageClassName"] == storageClass
                && item["spec"]?["accessModes"]?.AsArray().Any(m => (string?)m == "ReadWriteMany") == true)
            {
                cluster.Add("api/v1", "events", new JsonObject
                {
                    ["metadata"] = new JsonObject { ["name"] = $"{name}.provisioning", ["namespace"] = ns },
                    ["involvedObject"] = new JsonObject { ["kind"] = "PersistentVolumeClaim", ["name"] = name, ["namespace"] = ns },
                    ["type"] = "Warning",
                    ["reason"] = "ProvisioningFailed",
                    ["message"] = $"failed to provision volume with StorageClass \"{storageClass}\": NodePath only supports ReadWriteOnce and ReadWriteOncePod (1.22+) access modes",
                });
                return;
            }

            if (plural == "pods" && item["spec"]?["volumes"]?[0]?["persistentVolumeClaim"]?["claimName"] is { } claim
                && cluster.Find("persistentvolumeclaims", ns, (string)claim!) is { } bound && bound["spec"]?["volumeName"] is null)
            {
                cluster.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Pending" });
                return;
            }

            provision(plural, item);
        };
    }
}
