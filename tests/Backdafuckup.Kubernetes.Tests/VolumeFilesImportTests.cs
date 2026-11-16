using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>A volume restored as files: what tells that a class cannot share a volume, and how its size is written.</summary>
public sealed class VolumeFilesImportTests
{
    [Theory]
    [InlineData("failed to provision volume with StorageClass \"standard\": NodePath only supports ReadWriteOnce and ReadWriteOncePod (1.22+) access modes", true)]
    [InlineData("rpc error: code = InvalidArgument desc = volume capabilities MULTI_NODE_MULTI_WRITER not supported", true)]
    [InlineData("failed to provision volume with StorageClass \"thin\": ReadWriteMany volumes are not supported for block volumes", true)]
    [InlineData("failed to provision volume with StorageClass \"nfs-client\": mkdir /persistentvolumes/x: permission denied", false)]
    [InlineData("waiting for first consumer to be created before binding", false)]
    [InlineData(null, false)]
    public void A_provisioner_that_refuses_the_access_modes_is_told_from_other_failures(string? message, bool refuses) =>
        Assert.Equal(refuses, VolumeFilesImport.RefusesAccessModes(message));

    [Theory]
    [InlineData(1L << 30, "1Gi")]
    [InlineData(3L << 30, "3Gi")]
    [InlineData(1536L << 20, "1536Mi")]
    [InlineData(1193745944L, "1193745944")]
    public void Sizes_are_written_in_gi_or_mi_when_they_are_whole(long bytes, string expected) =>
        Assert.Equal(expected, VolumeSnapshotFlow.Quantity(bytes).ToString());

    [Fact]
    public async Task A_hand_over_the_server_did_not_finish_gives_the_pv_its_policy_back_and_deletes_it_only_without_its_pvc()
    {
        var api = new FakeKubernetesApi();
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("web"));
        api.Add("api/v1", "persistentvolumeclaims", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "data", ["namespace"] = "web" }, ["spec"] = new JsonObject() });
        api.Add("api/v1", "persistentvolumes", Pv("pvc-bound", "Bound", "data"));
        api.Add("api/v1", "persistentvolumes", Pv("pvc-reserved", "Available", "data"));
        api.Add("api/v1", "persistentvolumes", Pv("pvc-orphan", "Available", "gone"));
        using var client = KubernetesClients.Create(Settings(), null, api.CreateHandler());
        var log = new ListLog();
        var flow = new VolumeSnapshotFlow(client, "rke2-test", log, new InMemoryArtifactLedger(), new KubernetesTimings());
        const string Recorded = """{"reclaimPolicy":"Delete"}""";

        foreach (var volume in new[] { "pvc-bound", "pvc-reserved", "pvc-orphan", "pvc-gone-already" })
        {
            await VolumeFilesImport.GiveBackAsync(flow, volume, Recorded, TestContext.Current.CancellationToken);
        }

        // Bound, or reserved for its PVC that exists: the PVC's volume, with its policy back. Without its PVC: deleted.
        Assert.Equal("Delete", (string?)api.Find("persistentvolumes", null, "pvc-bound")!["spec"]!["persistentVolumeReclaimPolicy"]);
        Assert.Equal("Delete", (string?)api.Find("persistentvolumes", null, "pvc-reserved")!["spec"]!["persistentVolumeReclaimPolicy"]);
        Assert.Null(api.Find("persistentvolumes", null, "pvc-orphan"));
        Assert.Contains("Info: The restored volume pvc-orphan did not reach its PVC web/gone before its restore stopped; it is deleted (cluster rke2-test).", log.Lines);
        Assert.Contains("Info: The restored volume pvc-bound of PVC web/data has its reclaim policy Delete back (cluster rke2-test; its restore stopped during the hand-over).", log.Lines);

        static JsonObject Pv(string name, string phase, string claim) => new()
        {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject
            {
                ["persistentVolumeReclaimPolicy"] = "Retain", ["capacity"] = new JsonObject { ["storage"] = "1Gi" },
                ["claimRef"] = new JsonObject { ["kind"] = "PersistentVolumeClaim", ["namespace"] = "web", ["name"] = claim },
            },
            ["status"] = new JsonObject { ["phase"] = phase },
        };
    }

    private static ConnectionSettings Settings() => new(
        Guid.Empty, HypervisorKind.Kubernetes, "10.26.1.10", 6443, new NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate("10.26.1.10", "spki", "thumb", string.Empty)], new Dictionary<string, string>());

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
}
