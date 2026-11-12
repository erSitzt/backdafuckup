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

        // Nothing left in the cluster; the export's PV untouched (its policy and the provisioner's annotation).
        Assert.DoesNotContain(nfs.All("persistentvolumes"), p => p["metadata"]?["labels"]?["backdafuckup.io/shadow-of"] is not null);
        Assert.Empty(nfs.All("pods"));
        Assert.DoesNotContain(nfs.All("persistentvolumeclaims"), c => (string?)c["metadata"]!["namespace"] == KubernetesSetup.WorkNamespace);
        var source = nfs.Find("persistentvolumes", null, "pv-data-web")!;
        Assert.Equal(("Delete", "cluster.local/nfs-subdir-external-provisioner"),
            ((string?)source["spec"]!["persistentVolumeReclaimPolicy"], (string?)source["metadata"]!["annotations"]!["pv.kubernetes.io/provisioned-by"]));
    }
}
