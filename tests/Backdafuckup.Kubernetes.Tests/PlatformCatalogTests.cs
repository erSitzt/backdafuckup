using System.Text;
using System.Text.Json.Nodes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>The platform catalog (spec 2026-11-12-1952-kubernetes-platform-profiles): its checks and the rules it carries.</summary>
public sealed class PlatformCatalogTests
{
    [Fact]
    public void The_built_in_catalog_loads_starts_with_the_base_and_names_sources_for_what_was_not_tested()
    {
        var catalog = KubernetesPlatformCatalog.Builtin;

        Assert.Equal("kubernetes", catalog.Profiles[0].Id);
        Assert.True(catalog.Profiles[0].Always);
        Assert.Equal(catalog.Profiles.Count, catalog.Profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.Profiles.Where(p => !p.Tested), p => Assert.NotEmpty(p.Sources));
        Assert.All(catalog.Profiles.Where(p => !p.Always), p => Assert.NotEmpty(p.Detect));
        Assert.All(catalog.Profiles.Where(p => p.KubeletRootDir is not null || p.ContainerRuntimeRoot is not null),
            p => Assert.Contains(p.Category, new[] { PlatformCategory.Base, PlatformCategory.Distribution }));

        // "Tested" is what the lab ran, nothing more.
        Assert.Equal(["calico", "fleet", "istio", "kind", "kubernetes", "rancher", "rke2", "vsphere-csi"],
            catalog.Profiles.Where(p => p.Tested).Select(p => p.Id).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true, "colour": "blue" } ] }""", "colour")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "core", "tested": true, "always": true } ] }""", "category \"core\"")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true }, { "id": "x", "category": "addon", "detect": [ { "namespace": "x" } ] } ] }""", "not tested and names no sources")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true }, { "id": "x", "category": "addon", "tested": true, "detect": [ { "namespace": "x", "apiGroup": "y" } ] } ] }""", "exactly one of")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true }, { "id": "kubernetes", "category": "addon", "tested": true, "detect": [ { "namespace": "x" } ] } ] }""", "more than once")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true }, { "id": "x", "category": "addon", "tested": true, "detect": [ { "namespace": "x" } ], "kubeletRootDir": "/k" } ] }""", "only distributions")]
    [InlineData("""{ "schema": 1, "profiles": [ { "id": "kubernetes", "category": "base", "tested": true, "always": true, "made": [ { "always": true } ] } ] }""", "matches nothing in particular")]
    [InlineData("""{ "schema": 2, "profiles": [] }""", "schema 2")]
    public void A_catalog_with_a_mistake_is_refused_naming_it(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => KubernetesPlatformCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_has_the_base_keeps_catalog_order_names_unknown_ids_and_takes_paths_from_its_distribution()
    {
        var set = KubernetesPlatformSet.Of(["fleet", "rke2", "rancher", "from-a-newer-version"]);

        Assert.Equal(["kubernetes", "rke2", "rancher", "fleet"], set.Ids);
        Assert.Equal(["from-a-newer-version"], set.Unknown);
        Assert.Equal("RKE2 · Rancher · Fleet", set.Display);
        Assert.Equal(("/var/lib/kubelet", "/var/lib/rancher/rke2/agent/containerd"), (set.KubeletRootDir, set.ContainerRuntimeRoot));
        Assert.Contains(set.NamespacePatterns, n => n.Profile.Id == "rancher" && n.Pattern == "cattle-*");

        Assert.Equal("Kubernetes", KubernetesPlatformSet.Base.Display);
        Assert.Equal("/var/lib/containerd", KubernetesPlatformSet.Base.ContainerRuntimeRoot);
    }

    /// <summary>
    /// What the rules in code left out before the catalog, the legacy set leaves out too (restore points without recorded
    /// platforms must behave as they did). Additions on purpose are listed in the second part.
    /// </summary>
    [Fact]
    public void The_legacy_set_leaves_out_at_least_what_the_rules_in_code_did()
    {
        var legacy = KubernetesPlatformSet.Legacy;

        // Cluster-scoped objects of the cluster itself, RKE2, Rancher and Fleet (the old IsManaged).
        foreach (var name in new[] { "system:node", "admin", "edit", "view", "cluster-admin", "rke2-coredns", "cattle-admin", "rancher-webhook", "fleet-agent", "cluster-owner", "project-owner-promoted" })
        {
            Assert.True(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRole", name)) is not null, name);
            Assert.True(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRoleBinding", name)) is not null, name);
        }

        Assert.NotNull(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRole", "anything", labels: new() { ["kubernetes.io/bootstrapping"] = "rbac-defaults" })));
        Assert.NotNull(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRole", "anything", labels: new() { ["management.cattle.io/project"] = "p-1" })));
        Assert.NotNull(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRoleBinding", "anything", annotations: new() { ["field.cattle.io/creatorId"] = "u-1" })));
        Assert.NotNull(legacy.MadeBy(Obj("scheduling.k8s.io/v1", "PriorityClass", "system-node-critical")));
        Assert.NotNull(legacy.MadeBy(Obj("admissionregistration.k8s.io/v1", "ValidatingWebhookConfiguration", "rancher.cattle.io")));
        Assert.NotNull(legacy.MadeBy(Obj("admissionregistration.k8s.io/v1", "MutatingWebhookConfiguration", "rke2-ingress-nginx-admission")));

        // CRD groups of Rancher, k3s/RKE2, the CNIs and the vSphere CSI driver (the old SkippedCrdGroups).
        foreach (var group in new[] { "cattle.io", "management.cattle.io", "k3s.cattle.io", "crd.projectcalico.org", "projectcalico.org", "operator.tigera.io", "cilium.io", "cns.vmware.com" })
        {
            Assert.True(legacy.IsCrdGroup(group), group);
        }

        // Rancher's namespaced objects (the old IsRancherMade) and fields (the old IsRancherField).
        Assert.Equal("rancher", legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "RoleBinding", "rb-1", labels: new() { ["authz.cluster.cattle.io/rtb-owner"] = "x" }))?.Profile.Id);
        Assert.Equal("rancher", legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "Role", "r-1", labels: new() { ["cattle.io/creator"] = "norman" }))?.Profile.Id);
        Assert.Equal("rancher", legacy.MadeBy(Obj("catalog.cattle.io/v1", "App", "redis"))?.Profile.Id);
        Assert.All(new[] { "field.cattle.io/projectId", "lifecycle.cattle.io/create.x", "cattle.io/status", "authz.management.cattle.io/x", "management.cattle.io/system" },
            key => Assert.True(legacy.IsStripped(key), key));

        // What people made stays.
        Assert.Null(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRole", "monitoring-reader")));
        Assert.Null(legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "RoleBinding", "redis", labels: new() { ["cattle.io/creator"] = "someone" })));
        Assert.Null(legacy.MadeBy(Obj("cert-manager.io/v1", "Certificate", "web")));
        Assert.Null(legacy.MadeBy(Obj("notcattle.io/v1", "Thing", "x")));
        Assert.False(legacy.IsCrdGroup("cert-manager.io"));
        Assert.False(legacy.IsStripped("app.kubernetes.io/name"));

        // Additions on purpose: what the addon manager makes, Calico's own roles, and Cilium's endpoints in every namespace.
        Assert.Equal("kubernetes", legacy.MadeBy(Obj("v1", "ConfigMap", "coredns", labels: new() { ["addonmanager.kubernetes.io/mode"] = "Reconcile" }))?.Profile.Id);
        Assert.Equal("calico", legacy.MadeBy(Obj("rbac.authorization.k8s.io/v1", "ClusterRole", "calico-node"))?.Profile.Id);
        Assert.Equal(new PlatformMade(KubernetesPlatformCatalog.Builtin.Find("cilium")!, true), legacy.MadeBy(Obj("cilium.io/v2", "CiliumEndpoint", "web-1")));
    }

    [Fact]
    public void A_kind_whose_objects_a_platform_makes_by_their_group_is_made_as_a_whole_and_per_object_rules_need_the_object()
    {
        var set = KubernetesPlatformSet.Of(["rancher", "istio"]);

        Assert.Equal("rancher", set.MakesKind("catalog.cattle.io", "v1", "App")?.Profile.Id);
        Assert.Null(set.MakesKind("cert-manager.io", "v1", "Certificate"));

        // Istio's root CA ConfigMap is made in every namespace; other ConfigMaps are not Istio's.
        Assert.Equal(new PlatformMade(KubernetesPlatformCatalog.Builtin.Find("istio")!, true), set.MadeBy(Obj("v1", "ConfigMap", "istio-ca-root-cert")));
        Assert.Null(set.MadeBy(Obj("v1", "ConfigMap", "site")));
    }

    private static JsonObject Obj(string apiVersion, string kind, string name, Dictionary<string, string>? labels = null, Dictionary<string, string>? annotations = null)
    {
        static JsonObject? Map(Dictionary<string, string>? map) =>
            map is null ? null : new JsonObject(map.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        return new JsonObject
        {
            ["apiVersion"] = apiVersion,
            ["kind"] = kind,
            ["metadata"] = new JsonObject { ["name"] = name, ["labels"] = Map(labels), ["annotations"] = Map(annotations) },
        };
    }
}
