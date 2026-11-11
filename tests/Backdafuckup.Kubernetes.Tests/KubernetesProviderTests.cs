using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Kubernetes.Tests;

public sealed class KubernetesProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConnectionSettings Settings(string address = "10.26.1.10", Dictionary<string, string>? options = null) => new(
        Guid.Empty, HypervisorKind.Kubernetes, address, 6443, new NetworkCredential("backdafuckup", "token-for-tests"), TlsTrustMode.Pinned,
        [new PinnedCertificate(address, "spki", "thumb", string.Empty)], options ?? new Dictionary<string, string>());

    [Fact]
    public async Task A_cluster_is_identified_by_its_kube_system_namespace_and_reports_its_version()
    {
        var api = new FakeKubernetesApi();
        var provider = new KubernetesProvider(api.CreateHandler);

        var result = await provider.TestConnectionAsync(Settings(), Ct);

        Assert.True(result.Success, result.Error);
        Assert.Equal(("uid-kube-system", "Kubernetes", "v1.32.13+rke2r1", "https://10.26.1.10:6443"),
            (result.Platform!.PlatformId, result.Platform.ProductName, result.Platform.ProductVersion, result.Platform.MasterAddress));
        Assert.Contains("GET /version", api.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the token (401 Unauthorized)")]
    [InlineData(HttpStatusCode.Forbidden, "403 Forbidden): the ServiceAccount lacks a permission")]
    public async Task A_refused_token_names_the_cluster_and_the_reason(HttpStatusCode status, string expected)
    {
        var api = new FakeKubernetesApi { FailWith = status };

        var result = await new KubernetesProvider(api.CreateHandler).TestConnectionAsync(Settings(), Ct);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
        Assert.Contains("https://10.26.1.10:6443", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_api_url_takes_a_path_prefix_and_bracketed_ipv6()
    {
        Assert.Equal("https://10.26.1.10:6443/k8s/clusters/c-m-abc12",
            KubernetesClients.BaseUrl(Settings(options: new() { [KubernetesConnectionOptions.BasePath] = "/k8s/clusters/c-m-abc12/" })));
        Assert.Equal("https://[fd00::10]:6443", KubernetesClients.BaseUrl(Settings("fd00::10")));
    }

    [Fact]
    public async Task The_inventory_says_per_volume_whether_its_data_goes_through_a_snapshot_and_why_not()
    {
        var api = new FakeKubernetesApi();
        foreach (var ns in new[] { "redis", "portal", "legacy", "empty" })
        {
            api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace(ns));
        }

        api.Add("apis/storage.k8s.io/v1", "storageclasses", StorageClass("vsphere-csi-block-rwo", "csi.vsphere.vmware.com"));
        api.Add("apis/storage.k8s.io/v1", "storageclasses", StorageClass("nfs-client", "cluster.local/nfs-subdir-external-provisioner"));
        api.Add("apis/storage.k8s.io/v1", "storageclasses", StorageClass("other-csi", "csi.example.com"));
        api.Add("apis/snapshot.storage.k8s.io/v1", "volumesnapshotclasses", SnapshotClass("vsphere-csi-snapclass", "csi.vsphere.vmware.com", isDefault: false));

        api.Add("api/v1", "persistentvolumeclaims", Claim("redis", "data-redis-0", "vsphere-csi-block-rwo", "pv-redis", "8Gi"));
        api.Add("api/v1", "persistentvolumes", CsiVolume("pv-redis", "vsphere-csi-block-rwo", "csi.vsphere.vmware.com"));
        api.Add("api/v1", "persistentvolumeclaims", Claim("portal", "uploads", null, "pv-uploads", "5Gi"));
        api.Add("api/v1", "persistentvolumes", NfsVolume("pv-uploads"));
        api.Add("api/v1", "persistentvolumeclaims", Claim("legacy", "data", "other-csi", "pv-legacy", "1Gi"));
        api.Add("api/v1", "persistentvolumes", CsiVolume("pv-legacy", "other-csi", "csi.example.com"));
        api.Add("api/v1", "persistentvolumeclaims", Claim("legacy", "pending", "other-csi", null, "1Gi"));

        api.Add("api/v1", "pods", Pod("redis", "redis-0", "StatefulSet", "redis", "data-redis-0"));
        api.Add("api/v1", "pods", Pod("portal", "web-7d9f8c6b5-x2k4p", "ReplicaSet", "web-7d9f8c6b5", "uploads"));

        await using var session = await new KubernetesProvider(api.CreateHandler).OpenAsync(Settings(), Ct);
        var inventory = await session.GetInventoryAsync(Ct);

        var redis = Item(inventory, InventoryKind.VolumeClaim, "redis/data-redis-0");
        Assert.Equal((DataPaths.Snapshot, "vsphere-csi-snapclass", "csi.vsphere.vmware.com", "8589934592", "StatefulSet redis", "redis"),
            (redis.Properties[KubernetesInventoryProperties.DataPath], redis.Properties[KubernetesInventoryProperties.SnapshotClass],
             redis.Properties[KubernetesInventoryProperties.Driver], redis.Properties[KubernetesInventoryProperties.SizeBytes],
             redis.Properties[KubernetesInventoryProperties.UsedBy], redis.ParentNativeId));

        var uploads = Item(inventory, InventoryKind.VolumeClaim, "portal/uploads");
        Assert.Equal(DataPaths.ObjectsOnly, uploads.Properties[KubernetesInventoryProperties.DataPath]);
        Assert.StartsWith("NFS volume", uploads.Properties[KubernetesInventoryProperties.DataPathReason], StringComparison.Ordinal);
        Assert.Equal(("nfs", "Deployment web"), (uploads.Properties[KubernetesInventoryProperties.Driver], uploads.Properties[KubernetesInventoryProperties.UsedBy]));

        var legacy = Item(inventory, InventoryKind.VolumeClaim, "legacy/data");
        Assert.Equal("no VolumeSnapshotClass for the CSI driver csi.example.com", legacy.Properties[KubernetesInventoryProperties.DataPathReason]);
        Assert.Equal("not bound to a volume", Item(inventory, InventoryKind.VolumeClaim, "legacy/pending").Properties[KubernetesInventoryProperties.DataPathReason]);

        Assert.Equal(("1", "1"), Counts(Item(inventory, InventoryKind.Namespace, "redis")));
        Assert.Equal(("2", "0"), Counts(Item(inventory, InventoryKind.Namespace, "legacy")));
        Assert.Equal(("0", "0"), Counts(Item(inventory, InventoryKind.Namespace, "empty")));
        Assert.Equal("uid-kube-system", Item(inventory, InventoryKind.Namespace, "redis").ParentNativeId);
    }

    [Fact]
    public void Of_several_snapshot_classes_for_a_driver_the_default_one_is_taken_and_without_one_none()
    {
        var volume = new k8s.Models.V1PersistentVolume { Spec = new k8s.Models.V1PersistentVolumeSpec { Csi = new k8s.Models.V1CSIPersistentVolumeSource { Driver = "csi.example.com", VolumeHandle = "h" } } };

        Assert.Equal((DataPaths.Snapshot, (string?)null, "b"),
            KubernetesInventory.DataPathOf(volume, "csi.example.com", [("a", "csi.example.com", false), ("b", "csi.example.com", true)]));
        var (path, reason, _) = KubernetesInventory.DataPathOf(volume, "csi.example.com", [("a", "csi.example.com", false), ("b", "csi.example.com", false)]);
        Assert.Equal(DataPaths.ObjectsOnly, path);
        Assert.Contains("2 VolumeSnapshotClasses for the CSI driver csi.example.com and none marked default", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cluster_without_snapshot_crds_backs_up_objects_only()
    {
        var api = new FakeKubernetesApi();
        api.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("redis"));
        api.Add("api/v1", "persistentvolumeclaims", Claim("redis", "data", "vsphere", "pv", "8Gi"));
        api.Add("api/v1", "persistentvolumes", CsiVolume("pv", "vsphere", "csi.vsphere.vmware.com"));

        await using var session = await new KubernetesProvider(api.CreateHandler).OpenAsync(Settings(), Ct);
        var inventory = await session.GetInventoryAsync(Ct);

        Assert.Equal("no VolumeSnapshotClass for the CSI driver csi.vsphere.vmware.com",
            Item(inventory, InventoryKind.VolumeClaim, "redis/data").Properties[KubernetesInventoryProperties.DataPathReason]);
    }

    private static InventoryItem Item(Inventory inventory, InventoryKind kind, string nativeId) =>
        Assert.Single(inventory.Items, i => i.Kind == kind && i.NativeId == nativeId);

    private static (string, string) Counts(InventoryItem ns) =>
        (ns.Properties[KubernetesInventoryProperties.VolumeClaims], ns.Properties[KubernetesInventoryProperties.SnapshotVolumes]);

    private static JsonObject StorageClass(string name, string provisioner) => new()
    {
        ["apiVersion"] = "storage.k8s.io/v1", ["kind"] = "StorageClass", ["metadata"] = new JsonObject { ["name"] = name }, ["provisioner"] = provisioner,
    };

    private static JsonObject SnapshotClass(string name, string driver, bool isDefault) => new()
    {
        ["apiVersion"] = "snapshot.storage.k8s.io/v1", ["kind"] = "VolumeSnapshotClass", ["driver"] = driver, ["deletionPolicy"] = "Delete",
        ["metadata"] = new JsonObject
        {
            ["name"] = name,
            ["annotations"] = new JsonObject { ["snapshot.storage.kubernetes.io/is-default-class"] = isDefault ? "true" : "false" },
        },
    };

    private static JsonObject Claim(string ns, string name, string? storageClass, string? volume, string size)
    {
        var spec = new JsonObject
        {
            ["accessModes"] = new JsonArray("ReadWriteOnce"),
            ["volumeMode"] = "Filesystem",
            ["resources"] = new JsonObject { ["requests"] = new JsonObject { ["storage"] = size } },
        };
        if (storageClass is not null)
        {
            spec["storageClassName"] = storageClass;
        }

        if (volume is not null)
        {
            spec["volumeName"] = volume;
        }

        return new JsonObject
        {
            ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim",
            ["metadata"] = new JsonObject { ["name"] = name, ["namespace"] = ns, ["uid"] = $"uid-{ns}-{name}" },
            ["spec"] = spec,
            ["status"] = new JsonObject { ["phase"] = volume is null ? "Pending" : "Bound", ["capacity"] = new JsonObject { ["storage"] = size } },
        };
    }

    private static JsonObject CsiVolume(string name, string storageClass, string driver) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "PersistentVolume", ["metadata"] = new JsonObject { ["name"] = name },
        ["spec"] = new JsonObject { ["storageClassName"] = storageClass, ["csi"] = new JsonObject { ["driver"] = driver, ["volumeHandle"] = $"handle-{name}" } },
    };

    private static JsonObject NfsVolume(string name) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "PersistentVolume", ["metadata"] = new JsonObject { ["name"] = name },
        ["spec"] = new JsonObject { ["nfs"] = new JsonObject { ["server"] = "10.26.1.30", ["path"] = "/srv/nfs/uploads" } },
    };

    private static JsonObject Pod(string ns, string name, string ownerKind, string ownerName, string claim) => new()
    {
        ["apiVersion"] = "v1", ["kind"] = "Pod",
        ["metadata"] = new JsonObject
        {
            ["name"] = name, ["namespace"] = ns,
            ["ownerReferences"] = new JsonArray(new JsonObject { ["apiVersion"] = "apps/v1", ["kind"] = ownerKind, ["name"] = ownerName, ["uid"] = "o", ["controller"] = true }),
        },
        ["spec"] = new JsonObject
        {
            ["containers"] = new JsonArray(new JsonObject { ["name"] = "c", ["image"] = "busybox" }),
            ["volumes"] = new JsonArray(new JsonObject { ["name"] = "data", ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = claim } }),
        },
    };
}

public sealed class KubernetesSetupTests
{
    [Fact]
    public void The_setup_manifest_parses_into_the_namespace_account_token_and_roles()
    {
        var objects = k8s.KubernetesYaml.LoadAllFromString(KubernetesSetup.Manifest());

        Assert.Equal(
            ["Namespace", "ServiceAccount", "Secret", "ClusterRole", "ClusterRoleBinding", "Role", "RoleBinding", "ClusterRole", "ClusterRoleBinding"],
            objects.Select(o => ((k8s.IKubernetesObject)o).Kind));
        var token = Assert.IsType<k8s.Models.V1Secret>(objects[2]);
        Assert.Equal(("kubernetes.io/service-account-token", "backdafuckup"), (token.Type, token.Metadata.Annotations["kubernetes.io/service-account.name"]));
        var backup = Assert.IsType<k8s.Models.V1ClusterRole>(objects[3]);
        Assert.Contains(backup.Rules, r => r.Resources.Contains("volumesnapshotcontents") && r.Verbs.Contains("create"));
        Assert.Contains(backup.Rules, r => r.Resources.Contains("pods/exec"));

        // Without restore: the same up to the work namespace's role, no rights to create objects anywhere.
        var backupOnly = k8s.KubernetesYaml.LoadAllFromString(KubernetesSetup.Manifest(includeRestore: false));
        Assert.Equal(7, backupOnly.Count);
        Assert.DoesNotContain(backupOnly, o => o is k8s.Models.V1ClusterRole { Metadata.Name: "backdafuckup-restore" });
    }
}
