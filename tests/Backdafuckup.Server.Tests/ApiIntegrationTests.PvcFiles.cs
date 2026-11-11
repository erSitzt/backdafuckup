using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Files of a namespace's restore point go back into the live PVC through a mover pod next to the workload.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task Files_of_a_pvc_go_back_into_the_live_pvc_through_a_mover_pod_on_the_node_of_the_workload()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct));

        // The PVC's volume: an ext4 file system on the whole device. The live PVC: a folder the mover pod mounts.
        Directory.CreateDirectory(_root);
        var device = Path.Combine(_root, "ext4-volume.img");
        using (var source = File.OpenRead(FsFixtures.Image("ext4-gpt")))
        using (var copy = File.Create(device))
        {
            source.Position = 2048 * 512; // partition 1 starts at sector 2048
            source.CopyTo(copy);
        }

        var live = Path.Combine(_root, "live-pvc");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(outside);
        var movers = new List<string>();
        SetUpCluster(device, movers, [], new FileInfo(device).Length, live);
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
        var backupSession = (await WaitForSessionAsync(client, backup.SessionId)).Session;
        Assert.Equal(("Succeeded", RestorePointObjectKinds.KubernetesNamespace), (backupSession.State, backupSession.ObjectKind)); // the console marks it as a namespace's
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points?vmId=bdfu-spike", Ct))!);

        // The browser sees the PVC's file system; a namespace has no VM to restore into, and folders are absolute.
        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = Assert.Single(mount.Volumes, v => v.Readable);
        Assert.Equal(("ext", "data-redis-test-0", RestorePointObjectKinds.KubernetesNamespace), (volume.FileSystem, volume.DiskName, mount.ObjectKind));
        FileRestoreRequestDto Request(FileRestoreModeDto mode, FileRestoreTargetKindDto kind = FileRestoreTargetKindDto.Pvc, string? folder = null) => new()
        {
            RestorePointId = point.Id,
            Volume = volume.Id,
            Paths = ["folder", "hello.txt", "link-to-hello", "pattern-3m.bin"],
            Mode = mode,
            Target = new FileRestoreTargetDto { Kind = kind, Folder = folder, RestorePermissions = false },
            MountId = mount.Id,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, FileRestoreTargetKindDto.Ssh), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, folder: "srv/../etc"), Ct)).StatusCode);

        // Into the original location: the mover pod runs on the workload's node with the PVC mounted, and is gone afterwards.
        var first = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite), Ct))).SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{first.Session.Id}/log", Ct))!.Select(l => l.Message));
        Assert.True(first.Session.State == "Succeeded", first.Session.Summary + "\n" + log);
        Assert.Equal(RestorePointObjectKinds.KubernetesNamespace, first.Session.ObjectKind);
        Assert.Contains("is used by redis-test-0 on node node-1; the mover pod mounts it there too", log, StringComparison.Ordinal);
        Assert.Contains("to PVC bdfu-spike/data-redis-test-0, original location", first.Session.Summary, StringComparison.Ordinal);
        var manifest = FsFixtures.Manifest("ext4-gpt.p1").ToDictionary(l => l[7], l => l[1]);
        Assert.Equal(manifest["hello.txt"], Sha256(Path.Combine(live, "hello.txt")));
        Assert.Equal(manifest["pattern-3m.bin"], Sha256(Path.Combine(live, "pattern-3m.bin")));
        Assert.Equal(manifest["folder/sub/deep/nested.txt"], Sha256(Path.Combine(live, "folder/sub/deep/nested.txt")));
        Assert.Equal("hello.txt", new FileInfo(Path.Combine(live, "link-to-hello")).LinkTarget);
        Assert.Equal(981173106, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(live, "hello.txt"))).ToUnixTimeSeconds());
        var pod = Assert.Single(movers, m => m.Contains("-files-", StringComparison.Ordinal)); // next to the backup's mover
        Assert.Null(_cluster.Find("pods", "bdfu-spike", pod));
        Assert.DoesNotContain(_cluster.All("secrets"), s => (string?)s["metadata"]!["namespace"] == "bdfu-spike");

        // Keep: existing files stay and restored ones become RESTORED- copies; a planted link is not written through.
        await File.WriteAllTextAsync(Path.Combine(live, "hello.txt"), "changed\n", Ct);
        Directory.Delete(Path.Combine(live, "folder"), recursive: true);
        File.CreateSymbolicLink(Path.Combine(live, "folder"), outside);
        var kept = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Keep), Ct))).SessionId);
        var keptLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{kept.Session.Id}/log", Ct))!.Select(l => l.Message));
        Assert.True(kept.Session.State == "Warning", kept.Session.Summary + "\n" + keptLog);
        Assert.Equal("changed\n", await File.ReadAllTextAsync(Path.Combine(live, "hello.txt"), Ct));
        Assert.Equal(manifest["hello.txt"], Sha256(Path.Combine(live, "RESTORED-hello.txt")));
        Assert.Contains("PVC bdfu-spike/data-redis-test-0:/folder/sub: folder is a symbolic link; nothing is written through it", keptLog, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(live, ".bdfu-restore-*", SearchOption.AllDirectories));

        // A PVC that the namespace does not have (any more): no pod is started.
        var missing = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite) with
        {
            Target = new FileRestoreTargetDto { Kind = FileRestoreTargetKindDto.Pvc, Claim = "data-gone-0" },
        }, Ct))).SessionId);
        Assert.Equal("Failed", missing.Session.State);
        Assert.Contains("The PVC bdfu-spike/data-gone-0 does not exist", missing.Session.Summary, StringComparison.Ordinal);
        Assert.Equal(3, movers.Count);
        AssertClusterClean();

        static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }
}
