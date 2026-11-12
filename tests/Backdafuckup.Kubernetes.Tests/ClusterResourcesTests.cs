using System.Text.Json.Nodes;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>The cluster-scoped objects of a whole-cluster backup: what people made, without what the cluster makes itself.</summary>
public sealed class ClusterResourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cluster_scoped_objects_people_made_are_collected_and_the_clusters_own_are_left_out()
    {
        var api = new FakeKubernetesApi();
        const string Rbac = "apis/rbac.authorization.k8s.io/v1";
        Crd(api, "certificates.cert-manager.io", "cert-manager.io", "Certificate", "certificates", "Namespaced");
        Crd(api, "clusterissuers.cert-manager.io", "cert-manager.io", "ClusterIssuer", "clusterissuers", "Cluster");
        Crd(api, "clusters.management.cattle.io", "management.cattle.io", "Cluster", "clusters", "Cluster");
        Crd(api, "volumesnapshots.snapshot.storage.k8s.io", "snapshot.storage.k8s.io", "VolumeSnapshot", "volumesnapshots", "Namespaced");
        api.Add("apis/cert-manager.io/v1", "clusterissuers", new JsonObject { ["metadata"] = new JsonObject { ["name"] = "letsencrypt" }, ["spec"] = new JsonObject { ["acme"] = new JsonObject() } });
        api.Add(Rbac, "clusterroles", Named("system:controller:job-controller"));
        api.Add(Rbac, "clusterroles", Named("admin"));
        api.Add(Rbac, "clusterroles", Named("backdafuckup-restore"));
        api.Add(Rbac, "clusterroles", Named("project-member-viewer", labels: new JsonObject { ["cattle.io/creator"] = "norman" }));
        api.Add(Rbac, "clusterroles", Named("rbac-default", labels: new JsonObject { ["kubernetes.io/bootstrapping"] = "rbac-defaults" }));
        api.Add(Rbac, "clusterroles", Named("monitoring-reader"));
        api.Add(Rbac, "clusterrolebindings", Named("monitoring-reader"));
        api.Add(Rbac, "clusterrolebindings", Named("cluster-admin"));
        api.Add("apis/scheduling.k8s.io/v1", "priorityclasses", Named("system-node-critical"));
        api.Add("apis/scheduling.k8s.io/v1", "priorityclasses", Named("batch-low"));
        api.Add("apis/storage.k8s.io/v1", "storageclasses", Named("vsphere-csi"));
        api.Add("apis/networking.k8s.io/v1", "ingressclasses", Named("nginx"));
        api.Add("apis/admissionregistration.k8s.io/v1", "validatingwebhookconfigurations", Named("cert-manager-webhook"));
        api.Add("apis/admissionregistration.k8s.io/v1", "validatingwebhookconfigurations", Named("rancher.cattle.io"));
        using var client = KubernetesClients.Create(Settings(), null, api.CreateHandler());

        var log = new Lines();
        var collected = await KubernetesClusterResources.CollectAsync(client, "rke2-test", log, Ct);

        var keys = KubernetesResources.Read(collected.Content).Select(KubernetesObjects.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "ClusterIssuer/letsencrypt", "ClusterRole/monitoring-reader", "ClusterRoleBinding/monitoring-reader",
                "CustomResourceDefinition/certificates.cert-manager.io", "CustomResourceDefinition/clusterissuers.cert-manager.io",
                "IngressClass/nginx", "PriorityClass/batch-low", "StorageClass/vsphere-csi", "ValidatingWebhookConfiguration/cert-manager-webhook",
            ],
            keys);
        Assert.Contains(log.Written, l => l.StartsWith("Cluster-scoped objects of rke2-test: 9 (", StringComparison.Ordinal)
                                          && l.Contains("10 of the cluster itself, Rancher, Fleet or RKE2 left out", StringComparison.Ordinal));
    }

    [Fact]
    public void The_member_of_the_cluster_scoped_objects_is_no_namespace_name() =>
        Assert.Equal("@cluster", KubernetesClusterBackup.Member);

    private static void Crd(FakeKubernetesApi api, string name, string group, string kind, string plural, string scope) =>
        api.Add("apis/apiextensions.k8s.io/v1", "customresourcedefinitions", new JsonObject
        {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject
            {
                ["group"] = group,
                ["scope"] = scope,
                ["names"] = new JsonObject { ["kind"] = kind, ["plural"] = plural },
                ["versions"] = new JsonArray(new JsonObject { ["name"] = "v1", ["served"] = true, ["storage"] = true }),
            },
        });

    private static JsonObject Named(string name, JsonObject? labels = null) =>
        new() { ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = labels } };

    private static ConnectionSettings Settings() => new(
        Guid.Empty, HypervisorKind.Kubernetes, "10.26.1.10", 6443, new System.Net.NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate("10.26.1.10", "spki", "thumb", string.Empty)], new Dictionary<string, string>());

    private sealed class Lines : IOperationLog
    {
        public List<string> Written { get; } = [];

        public void Write(LogSeverity severity, string message, string? key = null) => Written.Add(message);
    }
}
