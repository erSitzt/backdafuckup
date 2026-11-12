using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>Recognising a cluster's platforms from what it shows (spec 2026-11-12-1952-kubernetes-platform-profiles).</summary>
public sealed class PlatformDetectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Fixtures() => [.. Directory.GetFiles(FixtureDirectory(), "*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>Each fixture (captured from a cluster, or written after a platform's documentation) is detected exactly as expected.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_cluster_is_detected_exactly_as_its_fixture_expects(string fixture)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), fixture)))!.AsObject();
        var facts = Facts(json["facts"]!.AsObject());

        var detected = KubernetesPlatformDetection.Detect(facts);

        Assert.Equal(json["expect"]!.AsArray().Select(e => (string)e!), detected.Select(d => d.Id));
        Assert.True(detected.Count(d => KubernetesPlatformCatalog.Builtin.Find(d.Id)!.Category == PlatformCategory.Distribution) <= 1, "at most one distribution");
        Assert.All(detected, d => Assert.False(string.IsNullOrWhiteSpace(d.Evidence)));
    }

    [Fact]
    public async Task The_facts_are_read_from_the_cluster_and_a_platform_set_by_hand_wins_over_the_detected_one()
    {
        var api = new FakeKubernetesApi { GitVersion = "v1.32.13+rke2r1" };
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("cattle-system"));
        api.Add("api/v1", "nodes", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = "agent-1", ["labels"] = new JsonObject { ["kubernetes.io/os"] = "linux" }, ["annotations"] = new JsonObject { ["rke2.io/node-args"] = "[]" } },
            ["spec"] = new JsonObject { ["providerID"] = "rke2://agent-1" },
        });
        api.Add("apis/storage.k8s.io/v1", "csidrivers", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "csi.vsphere.vmware.com" } });
        api.ExtraApiGroups.Add("management.cattle.io");
        using var client = KubernetesClients.Create(Settings(), null, api.CreateHandler());

        var facts = await KubernetesPlatformDetection.ReadFactsAsync(client, cancellationToken: Ct);

        Assert.Equal("v1.32.13+rke2r1", facts.GitVersion);
        Assert.Contains("management.cattle.io", facts.ApiGroups);
        Assert.Equal(("agent-1", "rke2://agent-1"), (facts.Nodes.Single().Name, facts.Nodes.Single().ProviderId));
        Assert.Contains("csi.vsphere.vmware.com", facts.CsiDrivers);
        var detected = KubernetesPlatformDetection.Detect(facts);
        Assert.Equal(["rke2", "rancher", "vsphere-csi"], detected.Select(d => d.Id));
        Assert.Equal(new DetectedPlatform("rke2", "version v1.32.13+rke2r1"), detected[0]);

        var selection = KubernetesPlatformDetection.Select(detected, Settings());
        Assert.False(selection.Overridden);
        Assert.Equal("RKE2 · Rancher", selection.Set.Display);
        Assert.Equal("Platform: RKE2 · Rancher + vSphere CSI (detected: rke2 by version v1.32.13+rke2r1; rancher by namespace cattle-system; vsphere-csi by CSI driver csi.vsphere.vmware.com).",
            selection.Describe());

        // Set by hand for the connection: those, whatever was detected.
        var manual = KubernetesPlatformDetection.Select(detected, Settings(new() { [KubernetesConnectionOptions.Platforms] = "kind, istio" }));
        Assert.True(manual.Overridden);
        Assert.Equal(["kubernetes", "kind", "istio"], manual.Set.Ids);
        Assert.EndsWith("(set by hand).", manual.Describe(), StringComparison.Ordinal);
    }

    private static ClusterFacts Facts(JsonObject facts)
    {
        static IReadOnlyList<string> Strings(JsonNode? node) => node is JsonArray array ? [.. array.Select(e => (string)e!)] : [];
        static IReadOnlyDictionary<string, string> Map(JsonNode? node) =>
            node is JsonObject map ? map.ToDictionary(kv => kv.Key, kv => (string?)kv.Value ?? string.Empty, StringComparer.Ordinal) : new Dictionary<string, string>();
        return new ClusterFacts(
            (string?)facts["gitVersion"],
            Strings(facts["apiGroups"]),
            Strings(facts["namespaces"]),
            [.. (facts["nodes"] as JsonArray ?? []).OfType<JsonObject>().Select(n => new ClusterNodeFacts((string)n["name"]!, (string?)n["providerId"], Map(n["labels"]), Map(n["annotations"])))],
            Strings(facts["csiDrivers"]));
    }

    private static ConnectionSettings Settings(Dictionary<string, string>? options = null) => new(
        Guid.Empty, HypervisorKind.Kubernetes, "10.26.1.10", 6443, new System.Net.NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate("10.26.1.10", "spki", "thumb", string.Empty)], options ?? new Dictionary<string, string>());

    /// <summary>tests/fixtures/k8s-platforms above the test binaries.</summary>
    private static string FixtureDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "k8s-platforms");
            if (File.Exists(Path.Combine(dir.FullName, "backdafuckup.slnx")) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"tests/fixtures/k8s-platforms not found above {AppContext.BaseDirectory}");
    }
}
