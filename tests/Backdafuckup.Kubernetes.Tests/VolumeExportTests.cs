using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>The data path of a PVC backup against the fake API, whose hooks play the cluster's controllers.</summary>
public sealed class VolumeExportTests
{
    private const string Work = "backdafuckup-system";
    private const string Snapshots = "apis/snapshot.storage.k8s.io/v1";
    private const string Image = "registry.test:8443/backdafuckup/mover@sha256:abc";

    private static readonly KubernetesTimings Fast = new()
    {
        Poll = TimeSpan.FromMilliseconds(5),
        SnapshotReady = TimeSpan.FromSeconds(5),
        Deleted = TimeSpan.FromSeconds(5),
        MoverConnect = TimeSpan.FromMinutes(42),
        Progress = TimeSpan.FromHours(1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pvc_snapshot_reaches_a_mover_pod_as_a_block_volume_and_everything_is_removed_newest_first()
    {
        var (api, channels) = Cluster(pod => pod.Channel.SendAsync((0, [1, 2, 3]), (4096, [4, 5])));
        var ledger = new InMemoryArtifactLedger();
        var log = new ListLog();
        using var client = Client(api);
        var flow = new VolumeSnapshotFlow(client, "rke2-test", log, ledger, Fast);

        var snapshot = await flow.TakeAsync("bdfu-spike", "data-redis-test-0", "vsphere-snapshots", "1a2b3c4d", Ct);
        Assert.Equal(("bdfu-1a2b3c4d-data-redis-test-0", "snapcontent-bdfu-1a2b3c4d-data-redis-test-0", "fcd-1+snap-1", "csi.vsphere.vmware.com"),
            (snapshot.Name, snapshot.ContentName, snapshot.Handle, snapshot.Driver));
        Assert.Equal((1073741824L, "vsphere-csi", "Filesystem"), (snapshot.RestoreSize, snapshot.StorageClass, snapshot.VolumeMode));
        var original = api.Find("volumesnapshots", "bdfu-spike", snapshot.Name)!;
        Assert.Equal("data-redis-test-0", (string?)original["spec"]!["source"]!["persistentVolumeClaimName"]);
        Assert.Equal("backdafuckup", (string?)original["metadata"]!["labels"]!["app.kubernetes.io/managed-by"]);

        string name;
        await using (var export = await flow.ExportAsync(snapshot, "1a2b3c4d", channels, new MoverDeployment(Image, "backdafuckup-registry", """{"auths":{}}"""), Ct))
        {
            name = export.PodName[..^"-mover".Length];
            var received = new List<string>();
            await export.Channel.StreamAsync(
                (offset, data, _) =>
                {
                    received.Add($"{offset}:{Convert.ToHexString(data.Span)}");
                    return ValueTask.CompletedTask;
                },
                Ct);
            Assert.Equal(["0:010203", "4096:0405"], received);

            var channel = Assert.Single(channels.Opened);
            Assert.Equal(("bdfu-spike/data-redis-test-0 (cluster rke2-test)", 1073741824L, Fast.MoverConnect), (channel.Description, channel.Capacity, channel.ConnectTimeout));

            // The snapshot again in the work namespace: Retain, the mode change allowed, on the same storage snapshot.
            var content = api.Find("volumesnapshotcontents", null, name)!;
            Assert.Equal(("Retain", "fcd-1+snap-1", "Filesystem", Work, name), (
                (string?)content["spec"]!["deletionPolicy"], (string?)content["spec"]!["source"]!["snapshotHandle"], (string?)content["spec"]!["sourceVolumeMode"],
                (string?)content["spec"]!["volumeSnapshotRef"]!["namespace"], (string?)content["spec"]!["volumeSnapshotRef"]!["name"]));
            Assert.Equal("true", (string?)content["metadata"]!["annotations"]!["snapshot.storage.kubernetes.io/allow-volume-mode-change"]);
            Assert.Equal(name, (string?)api.Find("volumesnapshots", Work, name)!["spec"]!["source"]!["volumeSnapshotContentName"]);

            // The mover's volume: a block PVC from it, of the snapshot's size, on the original storage class.
            var pvc = api.Find("persistentvolumeclaims", Work, name)!;
            Assert.Equal(("Block", "vsphere-csi", "VolumeSnapshot", name, "1073741824"), (
                (string?)pvc["spec"]!["volumeMode"], (string?)pvc["spec"]!["storageClassName"], (string?)pvc["spec"]!["dataSource"]!["kind"],
                (string?)pvc["spec"]!["dataSource"]!["name"], (string?)pvc["spec"]!["resources"]!["requests"]!["storage"]));

            // The pod: the image by digest with its pull secret, the device, the token from a secret, root without privileges.
            var pod = api.Find("pods", Work, export.PodName)!;
            var container = pod["spec"]!["containers"]![0]!;
            Assert.Equal((Image, "backdafuckup-registry", "/dev/bdfu-volume", name), (
                (string?)container["image"], (string?)pod["spec"]!["imagePullSecrets"]![0]!["name"],
                (string?)container["volumeDevices"]![0]!["devicePath"], (string?)pod["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]));
            var env = container["env"]!.AsArray().ToDictionary(e => (string)e!["name"]!, e => e!);
            Assert.Equal(("backup", "1073741824", "pin-1"), ((string?)env["BDFU_MOVER_MODE"]["value"], (string?)env["BDFU_MOVER_BYTES"]["value"], (string?)env["BDFU_MOVER_SERVER_PINS"]["value"]));
            Assert.Null(env["BDFU_MOVER_TOKEN"]["value"]);
            Assert.Equal($"{name}-token", (string?)env["BDFU_MOVER_TOKEN"]["valueFrom"]!["secretKeyRef"]!["name"]);
            Assert.Equal(channel.Endpoint.Token, (string?)api.Find("secrets", Work, $"{name}-token")!["stringData"]!["token"]);
            Assert.Equal((0, false, false), ((int?)pod["spec"]!["securityContext"]!["runAsUser"], (bool?)pod["spec"]!["automountServiceAccountToken"],
                (bool?)container["securityContext"]!["allowPrivilegeEscalation"]));
            Assert.Equal("kubernetes.io/dockerconfigjson", (string?)api.Find("secrets", Work, "backdafuckup-registry")!["type"]);

            // Everything in the ledger before it was created.
            Assert.Equal(
                [KubernetesArtifactKinds.VolumeSnapshot, KubernetesArtifactKinds.VolumeSnapshotContent, KubernetesArtifactKinds.VolumeSnapshot,
                    KubernetesArtifactKinds.PersistentVolumeClaim, KubernetesArtifactKinds.Secret, KubernetesArtifactKinds.Pod],
                ledger.Items.Select(i => i.Kind));
            Assert.All(ledger.Items, i => Assert.Equal(ArtifactState.Active, i.State));
        }

        // Removed newest first; the pull secret stays for the next run; the original snapshot until it is deleted.
        Assert.Equal(
            [$"DELETE /api/v1/namespaces/{Work}/pods/{name}-mover", $"DELETE /api/v1/namespaces/{Work}/secrets/{name}-token",
                $"DELETE /api/v1/namespaces/{Work}/persistentvolumeclaims/{name}", $"DELETE /{Snapshots}/namespaces/{Work}/volumesnapshots/{name}",
                $"DELETE /{Snapshots}/volumesnapshotcontents/{name}"],
            api.Requests.Where(r => r.StartsWith("DELETE", StringComparison.Ordinal)));
        Assert.Empty(api.All("pods"));
        Assert.Single(api.All("secrets"));
        Assert.Equal(["bdfu-spike"], api.All("persistentvolumeclaims").Select(p => (string?)p["metadata"]!["namespace"]));
        Assert.True(channels.Opened[0].Disposed);

        await flow.DeleteSnapshotAsync(snapshot, Ct);
        Assert.Empty(api.All("volumesnapshots"));
        Assert.Empty(api.All("volumesnapshotcontents"));
        Assert.All(ledger.Items, i => Assert.Equal(ArtifactState.Done, i.State));
        Assert.Contains(log.Lines, l => l.Contains($"Mover pod {Work}/{name}-mover started for bdfu-spike/data-redis-test-0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mover_pod_that_cannot_pull_its_image_fails_the_channel_at_once_and_is_removed()
    {
        var (api, channels) = Cluster(pod =>
        {
            SetWaiting(pod, "ImagePullBackOff", "Back-off pulling image \"registry.test:8443/backdafuckup/mover@sha256:abc\"");
            return Task.CompletedTask;
        });
        using var client = Client(api);
        var flow = new VolumeSnapshotFlow(client, "rke2-test", new ListLog(), new InMemoryArtifactLedger(), Fast);
        var snapshot = await flow.TakeAsync("bdfu-spike", "data-redis-test-0", "vsphere-snapshots", "1a2b3c4d", Ct);

        await using (var export = await flow.ExportAsync(snapshot, "1a2b3c4d", channels, new MoverDeployment(Image, null, null), Ct))
        {
            var error = await Assert.ThrowsAsync<KubernetesOperationException>(() => export.Channel.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct));
            Assert.Contains($"The mover pod {Work}/{export.PodName} cannot start (ImagePullBackOff)", error.Message, StringComparison.Ordinal);
            Assert.Contains($"image {Image}", error.Message, StringComparison.Ordinal);
            Assert.Null(api.Find("pods", Work, export.PodName)!["spec"]!["imagePullSecrets"]);
        }

        Assert.Empty(api.All("pods"));
        Assert.Equal(["bdfu-spike"], api.All("persistentvolumeclaims").Select(p => (string?)p["metadata"]!["namespace"]));
    }

    [Fact]
    public async Task A_failed_mover_pod_fails_the_channel_with_the_end_of_its_log()
    {
        FakeKubernetesApi? cluster = null;
        var (api, channels) = Cluster(pod =>
        {
            cluster!.Mutate(() =>
            {
                cluster.PodLogs[$"{Work}/{pod.Name}"] = "backdafuckup mover 0.3.0\nFailed: The volume's device /dev/bdfu-volume cannot be opened for reading: permission denied\n";
                pod.Object["status"] = new JsonObject
                {
                    ["phase"] = "Failed",
                    ["containerStatuses"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "mover", ["image"] = Image, ["state"] = new JsonObject { ["terminated"] = new JsonObject { ["reason"] = "Error", ["exitCode"] = 1 } },
                    }),
                };
            });
            return Task.CompletedTask;
        });
        cluster = api;
        using var client = Client(api);
        var flow = new VolumeSnapshotFlow(client, "rke2-test", new ListLog(), new InMemoryArtifactLedger(), Fast);
        var snapshot = await flow.TakeAsync("bdfu-spike", "data-redis-test-0", "vsphere-snapshots", "1a2b3c4d", Ct);

        await using var export = await flow.ExportAsync(snapshot, "1a2b3c4d", channels, new MoverDeployment(Image, null, null), Ct);
        var error = await Assert.ThrowsAsync<KubernetesOperationException>(() => export.Channel.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct));
        Assert.Contains("failed (Error, exit code 1)", error.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be opened for reading: permission denied", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mover_pod_being_deleted_fails_the_channel_as_deleted()
    {
        // Deleted while it copies: the pod is still there, terminating, while its mover already stopped on SIGTERM.
        var (api, channels) = Cluster(pod =>
        {
            pod.Api.Mutate(() =>
            {
                pod.Object["metadata"]!["deletionTimestamp"] = "2026-11-12T08:53:10Z";
                pod.Object["status"] = new JsonObject { ["phase"] = "Running" };
            });
            return Task.CompletedTask;
        });
        using var client = Client(api);
        var flow = new VolumeSnapshotFlow(client, "rke2-test", new ListLog(), new InMemoryArtifactLedger(), Fast);
        var snapshot = await flow.TakeAsync("bdfu-spike", "data-redis-test-0", "vsphere-snapshots", "1a2b3c4d", Ct);

        await using (var export = await flow.ExportAsync(snapshot, "1a2b3c4d", channels, new MoverDeployment(Image, null, null), Ct))
        {
            var error = await Assert.ThrowsAsync<KubernetesOperationException>(() => export.Channel.StreamAsync((_, _, _) => ValueTask.CompletedTask, Ct));
            Assert.Equal(
                $"The mover pod {Work}/{export.PodName} for bdfu-spike/data-redis-test-0 was deleted (cluster rke2-test): by someone, an eviction or the drain of its node.",
                error.Message);
        }

        Assert.Empty(api.All("pods"));
    }

    [Fact]
    public async Task A_snapshot_error_of_the_driver_is_reported_with_the_snapshot_and_the_cluster()
    {
        var api = new FakeKubernetesApi();
        AddClaim(api);
        api.Created = (plural, item) => api.Mutate(() => item["status"] = new JsonObject
        {
            ["readyToUse"] = false,
            ["error"] = new JsonObject { ["message"] = "Failed to check and update snapshot content: the volume has already 3 snapshots" },
        });
        using var client = Client(api);
        var flow = new VolumeSnapshotFlow(client, "rke2-test", new ListLog(), new InMemoryArtifactLedger(), Fast);

        var error = await Assert.ThrowsAsync<KubernetesOperationException>(() => flow.TakeAsync("bdfu-spike", "data-redis-test-0", "vsphere-snapshots", "1a2b3c4d", Ct));
        Assert.Equal(
            "The snapshot bdfu-spike/bdfu-1a2b3c4d-data-redis-test-0 of the PVC data-redis-test-0 failed in cluster rke2-test: Failed to check and update snapshot content: the volume has already 3 snapshots",
            error.Message);
    }

    [Fact]
    public async Task Leftovers_in_the_ledger_are_deleted_by_the_session_and_an_object_already_gone_counts_as_deleted()
    {
        var api = new FakeKubernetesApi();
        api.Add("api/v1", "pods", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "bdfu-1a2b3c4d-x-mover", ["namespace"] = Work } });
        api.Add(Snapshots, "volumesnapshotcontents", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "bdfu-1a2b3c4d-x" } });
        var provider = new KubernetesProvider(api.CreateHandler);
        await using var session = await provider.OpenAsync(Settings(), Ct);

        foreach (var (kind, id) in new[]
        {
            (KubernetesArtifactKinds.Pod, $"{Work}/bdfu-1a2b3c4d-x-mover"),
            (KubernetesArtifactKinds.PersistentVolumeClaim, $"{Work}/bdfu-1a2b3c4d-x"),
            (KubernetesArtifactKinds.VolumeSnapshotContent, "bdfu-1a2b3c4d-x"),
        })
        {
            await session.CleanupArtifactAsync(new ProviderArtifact(Guid.NewGuid(), kind, id, null, null, null, ArtifactState.Active, null), NullOperationLog.Instance, Ct);
        }

        Assert.Empty(api.All("pods"));
        Assert.Empty(api.All("volumesnapshotcontents"));
        Assert.Contains($"DELETE /api/v1/namespaces/{Work}/persistentvolumeclaims/bdfu-1a2b3c4d-x", api.Requests);
    }

    [Fact]
    public void Object_names_are_dns_labels_of_at_most_63_characters_and_stay_unique_when_shortened()
    {
        Assert.Equal("bdfu-1a2b3c4d-data-redis-test-0", VolumeSnapshotFlow.ObjectName("1a2b3c4d", "data-redis-test-0"));
        Assert.Equal("bdfu-1a2b3c4d-data-my-claim", VolumeSnapshotFlow.ObjectName("1a2b3c4d", "Data.My_Claim"));
        var a = VolumeSnapshotFlow.ObjectName("1a2b3c4d", new string('a', 70) + "-0");
        var b = VolumeSnapshotFlow.ObjectName("1a2b3c4d", new string('a', 70) + "-1");
        Assert.True(a.Length <= 63 && b.Length <= 63);
        Assert.NotEqual(a, b);
        Assert.Matches("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$", a);
    }

    /// <summary>A cluster with the PVC bdfu-spike/data-redis-test-0 and controllers: snapshots get ready, PVCs bound, pods run and act.</summary>
    private static (FakeKubernetesApi Api, FakeChannels Channels) Cluster(Func<RunningPod, Task> onPod)
    {
        var api = new FakeKubernetesApi();
        var channels = new FakeChannels();
        AddClaim(api);
        api.Created = (plural, item) =>
        {
            var ns = (string?)item["metadata"]!["namespace"];
            var name = (string)item["metadata"]!["name"]!;
            switch (plural)
            {
                case "volumesnapshots" when (string?)item["spec"]!["source"]!["persistentVolumeClaimName"] is not null:
                    // The snapshot controller and the CSI driver: a content with the storage snapshot.
                    api.Add(Snapshots, "volumesnapshotcontents", new JsonObject
                    {
                        ["metadata"] = new JsonObject { ["name"] = $"snapcontent-{name}" },
                        ["spec"] = new JsonObject { ["driver"] = "csi.vsphere.vmware.com", ["deletionPolicy"] = "Delete" },
                        ["status"] = new JsonObject { ["snapshotHandle"] = "fcd-1+snap-1", ["restoreSize"] = 1073741824L, ["readyToUse"] = true },
                    });
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = $"snapcontent-{name}", ["restoreSize"] = "1Gi" });
                    break;
                case "volumesnapshots":
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = (string?)item["spec"]!["source"]!["volumeSnapshotContentName"] });
                    break;
                case "persistentvolumeclaims":
                    api.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Bound" });
                    break;
                case "pods":
                    api.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Running" });
                    var token = (string?)api.Find("secrets", ns, $"{name[..^"-mover".Length]}-token")!["stringData"]!["token"];
                    var channel = channels.Opened.Single(c => c.Endpoint.Token == token);
                    _ = Task.Run(() => onPod(new RunningPod(name, item, channel, api)));
                    break;
            }
        };

        // The snapshot controller deletes the content (and the storage snapshot) of a deleted snapshot whose class says Delete.
        api.Deleted = (plural, item) =>
        {
            if (plural == "volumesnapshots" && (string?)item["status"]?["boundVolumeSnapshotContentName"] is { } content && content.StartsWith("snapcontent-", StringComparison.Ordinal))
            {
                api.Remove("volumesnapshotcontents", null, content);
            }
        };
        return (api, channels);
    }

    private static void SetWaiting(RunningPod pod, string reason, string message) => pod.Api.Mutate(() => pod.Object["status"] = new JsonObject
    {
        ["phase"] = "Pending",
        ["containerStatuses"] = new JsonArray(new JsonObject
        {
            ["name"] = "mover", ["image"] = Image, ["state"] = new JsonObject { ["waiting"] = new JsonObject { ["reason"] = reason, ["message"] = message } },
        }),
    });

    private static void AddClaim(FakeKubernetesApi api)
    {
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("bdfu-spike"));
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(Work));
        api.Add("api/v1", "persistentvolumeclaims", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "data-redis-test-0", ["namespace"] = "bdfu-spike" },
            ["spec"] = new JsonObject { ["storageClassName"] = "vsphere-csi", ["volumeMode"] = "Filesystem", ["volumeName"] = "pvc-1", ["accessModes"] = new JsonArray("ReadWriteOnce") },
            ["status"] = new JsonObject { ["phase"] = "Bound", ["capacity"] = new JsonObject { ["storage"] = "1Gi" } },
        });
    }

    private static ConnectionSettings Settings() => new(
        Guid.Empty, HypervisorKind.Kubernetes, "10.26.1.10", 6443, new NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate("10.26.1.10", "spki", "thumb", string.Empty)], new Dictionary<string, string>());

    private static k8s.IKubernetes Client(FakeKubernetesApi api) => KubernetesClients.Create(Settings(), null, api.CreateHandler());

    private sealed record RunningPod(string Name, JsonObject Object, FakeBackupChannel Channel, FakeKubernetesApi Api);

    private sealed class ListLog : IOperationLog
    {
        public List<string> Lines { get; } = [];

        public void Write(LogSeverity severity, string message, string? key = null)
        {
            lock (Lines)
            {
                Lines.Add($"{severity}: {message}");
            }
        }
    }

    private sealed class FakeChannels : IMoverChannels
    {
        public Task<IMoverFilesChannel> OpenFilesAsync(string description, CancellationToken cancellationToken, TimeSpan? connectTimeout = null) =>
            throw new NotSupportedException();

        public List<FakeBackupChannel> Opened { get; } = [];

        public Task<IMoverBackupChannel> OpenBackupAsync(string description, long capacity, CancellationToken cancellationToken, TimeSpan? connectTimeout = null)
        {
            var channel = new FakeBackupChannel(description, capacity, connectTimeout);
            Opened.Add(channel);
            return Task.FromResult<IMoverBackupChannel>(channel);
        }

        public Task<IMoverRestoreChannel> OpenRestoreAsync(string description, IDiskImageSource image, CancellationToken cancellationToken, TimeSpan? connectTimeout = null, IProgress<long>? progress = null) =>
            throw new NotSupportedException();
    }

    /// <summary>Stands in for the server's channel: the "pod" sends records, the reader gets them.</summary>
    private sealed class FakeBackupChannel(string description, long capacity, TimeSpan? connectTimeout) : IMoverBackupChannel
    {
        private readonly TaskCompletionSource<DiskDataSink> _sink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Description => description;

        public TimeSpan? ConnectTimeout => connectTimeout;

        public bool Disposed { get; private set; }

        public MoverEndpoint Endpoint { get; } = new(
            new Uri("https://backup.test:9555/api/agent/movers/1/data"), new Uri("https://backup.test:9555/api/agent/movers/1/result"), $"token-{Guid.NewGuid():N}", ["pin-1"]);

        public long Capacity => capacity;

        public string TransportName => "mover";

        public async Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken)
        {
            _sink.TrySetResult(sink);
            await _done.Task.WaitAsync(cancellationToken);
        }

        public async Task SendAsync(params (long Offset, byte[] Data)[] records)
        {
            var sink = await _sink.Task;
            foreach (var (offset, data) in records)
            {
                await sink(offset, data, CancellationToken.None);
            }

            _done.TrySetResult();
        }

        public void Abort(Exception error) => _done.TrySetException(error);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _done.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }
}
