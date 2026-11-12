using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>The backup of a namespace against the fake API: objects, hooks, snapshots, volumes read through movers.</summary>
public sealed class NamespaceBackupTests
{
    private const string Ns = "bdfu-spike";
    private const string Snapshots = "apis/snapshot.storage.k8s.io/v1";
    private const string Image = "registry.test:8443/backdafuckup/mover@sha256:abc";

    private static readonly KubernetesTimings Fast = new()
    {
        Poll = TimeSpan.FromMilliseconds(5),
        SnapshotReady = TimeSpan.FromSeconds(5),
        Deleted = TimeSpan.FromSeconds(5),
        Progress = TimeSpan.FromHours(1),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_namespace_keeps_its_objects_runs_its_hooks_around_the_snapshots_and_reads_its_volumes_with_data()
    {
        var api = Cluster(out var channels, out var executor);
        var ledger = new InMemoryArtifactLedger();
        var log = new ListLog();
        await using var session = await new KubernetesProvider(api.CreateHandler, channels, new FixedImage(), Fast, executor).OpenAsync(Settings(), Ct);

        IVmBackupSession backup;
        await using ((backup = await session.BeginBackupAsync(new VmBackupRequest(Ns, Guid.NewGuid(), Guid.NewGuid(), Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000"), "x"), log, ledger, Ct))
            .ConfigureAwait(false))
        {
            // The namespace is the "VM"; only the PVC on a CSI driver with a snapshot class is a disk.
            Assert.Equal((Ns, Ns), (backup.Vm.VmId, backup.Vm.Name));
            var disk = Assert.Single(backup.Disks);
            Assert.Equal(("data-redis-test-0", $"{Ns}/data-redis-test-0", 1073741824L, "vsphere-csi"), (disk.Key, disk.SourceDiskId, disk.CapacityBytes, disk.StorageName));
            var descriptor = backup.Kubernetes!;
            Assert.Equal(("10.26.1.10", Ns, "uid-bdfu-spike", "v1.32.13+rke2r1"), (descriptor.Cluster, descriptor.Namespace, descriptor.NamespaceUid, descriptor.KubernetesVersion));
            // The NFS volume is read file by file (a file tree), after the snapshots.
            Assert.Equal(["data-redis-test-0:snapshot:data-redis-test-0:", "nfs-data:files::nfs-data"], descriptor.Volumes.Select(v => $"{v.Claim}:{v.DataPath}:{v.DiskKey}:{v.TreeKey}"));
            Assert.Contains("NFS", descriptor.Volumes[1].Reason, StringComparison.Ordinal);
            Assert.Contains(log.Lines, l => l.StartsWith("Info: PVC nfs-data: NFS volume", StringComparison.Ordinal));
            Assert.Equal(("nfs-data", "pvc", "shadow-pv"), (backup.FileTrees.Single().Key, backup.FileTrees.Single().Kind, backup.FileTrees.Single().Reader));

            // Velero's order: pre-hook, snapshot, and the post-hook only once the files of the NFS volume are read.
            Assert.Equal(["pre redis-test-0/redis redis-cli save"], executor.Calls);
            Assert.Contains(log.Lines, l => l == $"Info: The post-hooks of {Ns} run after the files of nfs-data are read.");
            await backup.FileTreesReadAsync(log, Ct);
            await backup.FileTreesReadAsync(log, Ct); // once only
            Assert.Equal(["pre redis-test-0/redis redis-cli save", "post redis-test-0/redis redis-cli config set appendonly yes"], executor.Calls);
            var order = api.Requests.Where(r => r.StartsWith("EXEC", StringComparison.Ordinal) || r.StartsWith($"POST /{Snapshots}/namespaces/{Ns}/volumesnapshots", StringComparison.Ordinal)).ToList();
            Assert.Equal(["EXEC pre", $"POST /{Snapshots}/namespaces/{Ns}/volumesnapshots", "EXEC post"], order);
            Assert.True(backup.GuestFreeze!.ApplicationConsistent);
            Assert.Equal(["pre", "post"], descriptor.Hooks.Select(h => h.Phase));
            Assert.All(descriptor.Hooks, h => Assert.Equal(0, h.ExitCode));

            // The objects: the namespace's, its custom resource with its CRD, and the PVs and StorageClass of its PVCs.
            Assert.Equal(KubernetesResources.Format, backup.Metadata.Format);
            var objects = KubernetesResources.Read(backup.Metadata.Content).ToList();
            var kinds = objects.Select(o => $"{o["kind"]}/{o["metadata"]!["name"]}").Order(StringComparer.Ordinal).ToList();
            Assert.Equal(
                [
                    "ConfigMap/redis-config", "CustomResourceDefinition/redisfailovers.databases.example.com", "Namespace/bdfu-spike", "PersistentVolume/nfs-1",
                    "PersistentVolume/pvc-1", "PersistentVolumeClaim/data-redis-test-0", "PersistentVolumeClaim/nfs-data", "Pod/redis-test-0", "RedisFailover/cache",
                    "Secret/redis-auth", "StatefulSet/redis-test", "StorageClass/vsphere-csi",
                ],
                kinds);
            Assert.Equal(descriptor.ObjectCount, objects.Count);
            Assert.All(objects, o => Assert.Null(o["metadata"]!["managedFields"]));
            Assert.Equal("apps/v1", (string?)objects.Single(o => (string?)o["kind"] == "StatefulSet")["apiVersion"]);
            Assert.Equal("databases.example.com/v1", (string?)objects.Single(o => (string?)o["kind"] == "RedisFailover")["apiVersion"]);

            // No change tracking: a full read without a warning.
            var changes = await backup.GetChangedExtentsAsync(disk, null, log, Ct);
            Assert.Equal((true, FullReadReasons.NotTracked), (changes.IsFull, changes.FullReasonCode));

            // The volume through its mover; once read, its snapshot is gone.
            var received = new List<string>();
            await using (var reader = (ISequentialDiskReader)await backup.OpenReaderAsync(disk, new DiskReaderOptions(), Ct))
            {
                Assert.Equal(("mover", 1073741824L), (reader.TransportName, reader.Capacity));
                await reader.StreamAsync(
                    (offset, data, _) =>
                    {
                        received.Add($"{offset}:{Convert.ToHexString(data.Span)}");
                        return ValueTask.CompletedTask;
                    },
                    Ct);
            }

            Assert.Equal(["0:CAFE"], received);
            Assert.Empty(api.All("volumesnapshots"));
            Assert.Empty(api.All("volumesnapshotcontents"));
            Assert.Equal(["bdfu-spike", "bdfu-spike"], api.All("persistentvolumeclaims").Select(p => (string?)p["metadata"]!["namespace"]));
            await backup.CommitAsync(log, Ct);
        }

        Assert.All(ledger.Items, i => Assert.Equal(ArtifactState.Done, i.State));
    }

    [Fact]
    public async Task A_failed_pre_hook_with_on_error_fail_stops_the_backup_before_any_snapshot_and_still_runs_the_post_hook()
    {
        var api = Cluster(out var channels, out var executor);
        executor.Fail = "pre";
        await using var session = await new KubernetesProvider(api.CreateHandler, channels, new FixedImage(), Fast, executor).OpenAsync(Settings(), Ct);

        var error = await Assert.ThrowsAsync<KubernetesOperationException>(() =>
            session.BeginBackupAsync(new VmBackupRequest(Ns, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x"), new ListLog(), new InMemoryArtifactLedger(), Ct));

        Assert.Equal($"The pre-hook in {Ns}/redis-test-0 (redis) failed: exit code 1: ERR disk full. Its on-error is Fail, so the namespace is not backed up.", error.Message);
        Assert.Equal(["pre redis-test-0/redis redis-cli save", "post redis-test-0/redis redis-cli config set appendonly yes"], executor.Calls);
        Assert.DoesNotContain(api.Requests, r => r.StartsWith($"POST /{Snapshots}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("2m", 120)]
    [InlineData("1m30s", 90)]
    [InlineData("1h", 3600)]
    public void Hook_timeouts_are_go_durations(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), KubernetesHooks.ParseDuration(text));

    [Fact]
    public void Hook_commands_are_a_json_array_or_one_word()
    {
        Assert.Equal(["/bin/sh", "-c", "redis-cli save"], KubernetesHooks.ParseCommand("""["/bin/sh", "-c", "redis-cli save"]"""));
        Assert.Equal(["fsfreeze"], KubernetesHooks.ParseCommand(" fsfreeze "));
        Assert.Null(KubernetesHooks.ParseDuration("soon"));
    }

    /// <summary>
    /// bdfu-spike with Redis: a StatefulSet, its pod with hooks, a ConfigMap, a Secret, a custom resource, a PVC on vSphere
    /// CSI (with a snapshot class) and one on NFS; the controllers make snapshots ready and run the mover pod.
    /// </summary>
    private static FakeKubernetesApi Cluster(out FakeChannels channels, out FakeExecutor executor)
    {
        var api = new FakeKubernetesApi();
        var movers = channels = new FakeChannels();
        executor = new FakeExecutor(api);
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(Ns));
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(KubernetesSetup.WorkNamespace));
        api.Add("api/v1", "configmaps", Object("redis-config", Ns));
        api.Add("api/v1", "secrets", Object("redis-auth", Ns));
        api.Add("apis/apps/v1", "statefulsets", Object("redis-test", Ns));
        var pod = Object("redis-test-0", Ns);
        pod["metadata"]!["annotations"] = new JsonObject
        {
            ["pre.hook.backup.velero.io/command"] = "[\"redis-cli\", \"save\"]",
            ["post.hook.backup.velero.io/command"] = "[\"redis-cli\", \"config\", \"set\", \"appendonly\", \"yes\"]",
        };
        pod["spec"] = new JsonObject { ["containers"] = new JsonArray(new JsonObject { ["name"] = "redis", ["image"] = "redis:7" }) };
        pod["status"] = new JsonObject { ["phase"] = "Running" };
        api.Add("api/v1", "pods", pod);
        api.Add("api/v1", "persistentvolumeclaims", Claim("data-redis-test-0", "pvc-1", "vsphere-csi"));
        api.Add("api/v1", "persistentvolumeclaims", Claim("nfs-data", "nfs-1", null));
        api.Add("api/v1", "persistentvolumes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "pvc-1" },
            ["spec"] = new JsonObject { ["storageClassName"] = "vsphere-csi", ["csi"] = new JsonObject { ["driver"] = "csi.vsphere.vmware.com", ["volumeHandle"] = "fcd-1" } },
        });
        api.Add("api/v1", "persistentvolumes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "nfs-1" },
            ["spec"] = new JsonObject { ["nfs"] = new JsonObject { ["server"] = "nfs.corp.example", ["path"] = "/export/data" } },
        });
        api.Add("apis/storage.k8s.io/v1", "storageclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "vsphere-csi" }, ["provisioner"] = "csi.vsphere.vmware.com" });
        api.Add(Snapshots, "volumesnapshotclasses", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "vsphere-snapshots" }, ["driver"] = "csi.vsphere.vmware.com" });
        api.Add("apis/apiextensions.k8s.io/v1", "customresourcedefinitions", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "redisfailovers.databases.example.com", ["managedFields"] = new JsonArray(new JsonObject { ["manager"] = "kubectl" }) },
            ["spec"] = new JsonObject
            {
                ["group"] = "databases.example.com",
                ["scope"] = "Namespaced",
                ["names"] = new JsonObject { ["plural"] = "redisfailovers", ["kind"] = "RedisFailover" },
                ["versions"] = new JsonArray(new JsonObject { ["name"] = "v1", ["served"] = true, ["storage"] = true }),
            },
        });
        api.Add("apis/databases.example.com/v1", "redisfailovers", Object("cache", Ns));

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
                        ["status"] = new JsonObject { ["snapshotHandle"] = "fcd-1+snap-1", ["restoreSize"] = 1073741824L, ["readyToUse"] = true },
                    });
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = $"snapcontent-{name}" });
                    break;
                case "volumesnapshots":
                    api.Mutate(() => item["status"] = new JsonObject { ["readyToUse"] = true, ["boundVolumeSnapshotContentName"] = (string?)item["spec"]!["source"]!["volumeSnapshotContentName"] });
                    break;
                case "pods":
                    api.Mutate(() => item["status"] = new JsonObject { ["phase"] = "Running" });
                    var token = (string?)api.Find("secrets", ns, $"{name[..^"-mover".Length]}-token")!["stringData"]!["token"];
                    var channel = movers.Opened.Single(c => c.Endpoint.Token == token);
                    _ = Task.Run(() => channel.SendAsync((0, [0xCA, 0xFE])));
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
        return api;
    }

    private static JsonObject Object(string name, string ns) => new()
    {
        ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = ns, ["managedFields"] = new JsonArray(new JsonObject { ["manager"] = "kubectl" }) },
    };

    private static JsonObject Claim(string name, string volume, string? storageClass) => new()
    {
        ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = Ns },
        ["spec"] = new JsonObject { ["volumeName"] = volume, ["storageClassName"] = storageClass, ["volumeMode"] = "Filesystem", ["accessModes"] = new JsonArray("ReadWriteOnce") },
        ["status"] = new JsonObject { ["phase"] = "Bound", ["capacity"] = new JsonObject { ["storage"] = "1Gi" } },
    };

    private static ConnectionSettings Settings() => new(
        Guid.Empty, HypervisorKind.Kubernetes, "10.26.1.10", 6443, new NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate("10.26.1.10", "spki", "thumb", string.Empty)], new Dictionary<string, string>());

    private sealed class FixedImage : IMoverImageSource
    {
        public Task<MoverDeployment> EnsurePushedAsync(Action<string>? log, CancellationToken cancellationToken) => Task.FromResult(new MoverDeployment(Image, null, null));
    }

    /// <summary>Records the hooks it runs (and an "EXEC" line among the API requests, for their order); fails the phase in <see cref="Fail"/>.</summary>
    private sealed class FakeExecutor(FakeKubernetesApi api) : IPodExecutor
    {
        public List<string> Calls { get; } = [];

        public string? Fail { get; set; }

        public Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, CancellationToken cancellationToken)
        {
            var phase = command.Contains("save") ? "pre" : "post";
            Calls.Add($"{phase} {pod}/{container} {string.Join(' ', command)}");
            api.Mutate(() => api.Requests.Add($"EXEC {phase}"));
            return Task.FromResult(phase == Fail ? (1, "ERR disk full") : (0, "OK"));
        }
    }

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
            var channel = new FakeBackupChannel(capacity);
            Opened.Add(channel);
            return Task.FromResult<IMoverBackupChannel>(channel);
        }

        public Task<IMoverRestoreChannel> OpenRestoreAsync(string description, IDiskImageSource image, CancellationToken cancellationToken, TimeSpan? connectTimeout = null, IProgress<long>? progress = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeBackupChannel(long capacity) : IMoverBackupChannel
    {
        private readonly TaskCompletionSource<DiskDataSink> _sink = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MoverEndpoint Endpoint { get; } = new(
            new Uri("https://backup.test:9555/api/agent/movers/1/data"), new Uri("https://backup.test:9555/api/agent/movers/1/result"), $"token-{Guid.NewGuid():N}", []);

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
            _done.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }
}
