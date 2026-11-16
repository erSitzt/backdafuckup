using System.Text.Json.Nodes;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>What belongs to a workload, and how objects are prepared for a restore.</summary>
public sealed class KubernetesObjectsTests
{
    [Fact]
    public void A_statefulset_needs_its_configuration_account_claims_services_and_their_ingress()
    {
        var all = App();
        var redis = all.Single(o => KubernetesObjects.Key(o) == "StatefulSet/redis");

        Assert.Equal(
            [
                "ConfigMap/redis-config", "HorizontalPodAutoscaler/redis", "Ingress/redis", "PersistentVolumeClaim/data-redis-0", "PersistentVolumeClaim/data-redis-1",
                "PodDisruptionBudget/redis", "Role/redis", "RoleBinding/redis", "Secret/redis-auth", "Secret/registry-pull", "Service/redis", "ServiceAccount/redis",
            ],
            KubernetesObjects.Needs(redis, all));

        // Another app's objects stay out; the selection of the workload brings its needs.
        var expanded = KubernetesObjects.Expand(["StatefulSet/redis"], all);
        Assert.Contains("PersistentVolumeClaim/data-redis-1", expanded);
        Assert.DoesNotContain("ConfigMap/other", expanded);
        Assert.DoesNotContain("PersistentVolumeClaim/data-redis-other-0", expanded);
        Assert.Equal("cache", KubernetesObjects.App(redis));
    }

    [Fact]
    public void Prepared_objects_lose_their_cluster_state_and_move_to_the_target_namespace()
    {
        var service = KubernetesObjects.Prepare(Obj("Service", "redis", spec: new JsonObject
        {
            ["clusterIP"] = "10.43.0.12",
            ["clusterIPs"] = new JsonArray("10.43.0.12"),
            ["ports"] = new JsonArray(new JsonObject { ["port"] = 6379, ["nodePort"] = 30123 }),
        }), "shop", "shop-copy");
        Assert.Equal("shop-copy", (string?)service["metadata"]!["namespace"]);
        Assert.Null(service["metadata"]!["uid"]);
        Assert.Null(service["metadata"]!["resourceVersion"]);
        Assert.Null(service["metadata"]!["ownerReferences"]);
        Assert.Null(service["status"]);
        Assert.Null(service["spec"]!["clusterIP"]);
        Assert.Null(service["spec"]!["ports"]![0]!["nodePort"]);

        var headless = KubernetesObjects.Prepare(Obj("Service", "redis-headless", spec: new JsonObject { ["clusterIP"] = "None" }), "shop", "shop");
        Assert.Equal("None", (string?)headless["spec"]!["clusterIP"]);

        var binding = Obj("RoleBinding", "redis");
        binding["subjects"] = new JsonArray(
            new JsonObject { ["kind"] = "ServiceAccount", ["name"] = "redis", ["namespace"] = "shop" },
            new JsonObject { ["kind"] = "ServiceAccount", ["name"] = "monitor", ["namespace"] = "monitoring" });
        var moved = KubernetesObjects.Prepare(binding, "shop", "shop-copy");
        Assert.Equal(["shop-copy", "monitoring"], moved["subjects"]!.AsArray().Select(s => (string?)s!["namespace"]));

        var job = Obj("Job", "migrate", spec: new JsonObject
        {
            ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["batch.kubernetes.io/controller-uid"] = "abc" } },
            ["template"] = new JsonObject { ["metadata"] = new JsonObject { ["labels"] = new JsonObject { ["batch.kubernetes.io/controller-uid"] = "abc", ["app"] = "migrate" } } },
        });
        var preparedJob = KubernetesObjects.Prepare(job, "shop", "shop");
        Assert.Null(preparedJob["spec"]!["selector"]);
        Assert.Equal(["app"], preparedJob["spec"]!["template"]!["metadata"]!["labels"]!.AsObject().Select(kv => kv.Key));

        var space = KubernetesObjects.Prepare(new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Namespace",
            ["metadata"] = new JsonObject { ["name"] = "shop", ["labels"] = new JsonObject { ["kubernetes.io/metadata.name"] = "shop", ["team"] = "web" } },
        }, "shop", "shop-copy");
        Assert.Equal("shop-copy", (string?)space["metadata"]!["name"]);
        Assert.Equal(["team"], space["metadata"]!["labels"]!.AsObject().Select(kv => kv.Key));

        var claim = Obj("PersistentVolumeClaim", "data", spec: new JsonObject { ["dataSource"] = new JsonObject { ["kind"] = "VolumeSnapshot" }, ["volumeName"] = "pvc-1" });
        claim["metadata"]!["annotations"] = new JsonObject { ["pv.kubernetes.io/bind-completed"] = "yes", ["volume.kubernetes.io/storage-provisioner"] = "csi", ["team"] = "web" };
        var preparedClaim = KubernetesObjects.Prepare(claim, "shop", "shop");
        Assert.Null(preparedClaim["spec"]!["dataSource"]);
        Assert.Equal(["team"], preparedClaim["metadata"]!["annotations"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void Restores_follow_velero_order_and_skip_what_the_cluster_makes_itself()
    {
        string[] kinds = ["Pod", "Deployment", "Service", "Secret", "PersistentVolumeClaim", "Namespace", "CustomResourceDefinition", "RedisFailover", "Ingress"];
        Assert.Equal(
            ["CustomResourceDefinition", "Namespace", "PersistentVolumeClaim", "Secret", "Service", "RedisFailover", "Deployment", "Ingress", "Pod"],
            kinds.OrderBy(KubernetesObjects.Rank));

        var token = Obj("Secret", "redis-token");
        token["type"] = "kubernetes.io/service-account-token";
        Assert.True(KubernetesObjects.IsClusterMade(token));
        Assert.True(KubernetesObjects.IsClusterMade(Obj("ConfigMap", "kube-root-ca.crt")));
        Assert.True(KubernetesObjects.IsClusterMade(Obj("ServiceAccount", "default")));
        Assert.False(KubernetesObjects.IsClusterMade(Obj("ServiceAccount", "redis")));

        var owned = Obj("Pod", "redis-0");
        owned["metadata"]!["ownerReferences"] = new JsonArray(new JsonObject { ["kind"] = "StatefulSet", ["name"] = "redis", ["controller"] = true });
        Assert.True(KubernetesObjects.IsControlled(owned));
    }

    [Fact]
    public void A_restore_point_compared_with_the_cluster_tells_same_changed_deleted_and_new_and_never_shows_secret_values()
    {
        var config = Obj("ConfigMap", "redis-config");
        config["data"] = new JsonObject { ["maxmemory"] = "1gb" };
        var deployment = Obj("Deployment", "web", group: "apps/v1", spec: new JsonObject { ["replicas"] = 2 });
        deployment["metadata"]!["annotations"] = new JsonObject { ["deployment.kubernetes.io/revision"] = "3" };
        var secret = Obj("Secret", "redis-auth");
        secret["data"] = new JsonObject { ["password"] = Convert.ToBase64String("s3cret-value"u8.ToArray()) };
        var claim = Obj("PersistentVolumeClaim", "data", spec: new JsonObject { ["volumeName"] = "pvc-old" });
        var backup = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', new[] { config, deployment, secret, claim, Obj("Service", "gone") }.Select(o => o.ToJsonString())));

        // Live: the ConfigMap changed, the Deployment only got a new revision, the PVC another volume, the Service is gone, a Job is new.
        var liveConfig = (JsonObject)config.DeepClone();
        liveConfig["data"]!["maxmemory"] = "2gb";
        liveConfig["metadata"]!["resourceVersion"] = "99";
        var liveDeployment = (JsonObject)deployment.DeepClone();
        liveDeployment["metadata"]!["annotations"]!["deployment.kubernetes.io/revision"] = "4";
        var liveClaim = (JsonObject)claim.DeepClone();
        liveClaim["spec"]!["volumeName"] = "pvc-restored";
        var states = KubernetesCompare.Compare(backup, [liveConfig, liveDeployment, (JsonObject)secret.DeepClone(), liveClaim, Obj("Job", "migrate", group: "batch/v1")])
            .ToDictionary(c => $"{c.Kind}/{c.Name}", c => c.State);

        Assert.Equal(KubernetesObjectStates.Changed, states["ConfigMap/redis-config"]);
        Assert.Equal(KubernetesObjectStates.Same, states["Deployment/web"]);
        Assert.Equal(KubernetesObjectStates.Same, states["Secret/redis-auth"]);
        Assert.Equal(KubernetesObjectStates.Same, states["PersistentVolumeClaim/data"]);
        Assert.Equal(KubernetesObjectStates.Missing, states["Service/gone"]);
        Assert.Equal(KubernetesObjectStates.New, states["Job/migrate"]);

        var shown = KubernetesCompare.Show(backup, "Secret", "redis-auth")!;
        Assert.DoesNotContain(Convert.ToBase64String("s3cret-value"u8.ToArray()), shown, StringComparison.Ordinal);
        Assert.Contains("\"password\": \"<hidden, about 12 bytes>\"", shown, StringComparison.Ordinal);
        Assert.Contains("1gb", KubernetesCompare.Show(backup, "ConfigMap", "redis-config"), StringComparison.Ordinal);
        Assert.Null(KubernetesCompare.Show(backup, "ConfigMap", "nope"));
    }

    /// <summary>The app "cache": a StatefulSet with everything it uses; plus objects of another app.</summary>
    private static List<JsonObject> App()
    {
        var redis = Obj("StatefulSet", "redis", group: "apps/v1", spec: new JsonObject
        {
            ["template"] = new JsonObject
            {
                ["metadata"] = new JsonObject { ["labels"] = new JsonObject { ["app"] = "redis", ["tier"] = "cache" } },
                ["spec"] = new JsonObject
                {
                    ["serviceAccountName"] = "redis",
                    ["imagePullSecrets"] = new JsonArray(new JsonObject { ["name"] = "registry-pull" }),
                    ["volumes"] = new JsonArray(new JsonObject { ["name"] = "config", ["configMap"] = new JsonObject { ["name"] = "redis-config" } }),
                    ["containers"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "redis",
                        ["env"] = new JsonArray(new JsonObject
                        {
                            ["name"] = "REDIS_PASSWORD",
                            ["valueFrom"] = new JsonObject { ["secretKeyRef"] = new JsonObject { ["name"] = "redis-auth", ["key"] = "password" } },
                        }),
                    }),
                },
            },
            ["volumeClaimTemplates"] = new JsonArray(new JsonObject { ["metadata"] = new JsonObject { ["name"] = "data" } }),
        });
        redis["metadata"]!["labels"] = new JsonObject { ["app.kubernetes.io/instance"] = "cache" };
        var binding = Obj("RoleBinding", "redis", group: "rbac.authorization.k8s.io/v1");
        binding["subjects"] = new JsonArray(new JsonObject { ["kind"] = "ServiceAccount", ["name"] = "redis" });
        binding["roleRef"] = new JsonObject { ["kind"] = "Role", ["name"] = "redis" };
        var ingress = Obj("Ingress", "redis", group: "networking.k8s.io/v1", spec: new JsonObject
        {
            ["rules"] = new JsonArray(new JsonObject
            {
                ["http"] = new JsonObject { ["paths"] = new JsonArray(new JsonObject { ["backend"] = new JsonObject { ["service"] = new JsonObject { ["name"] = "redis" } } }) },
            }),
        });
        return
        [
            redis,
            Obj("ConfigMap", "redis-config"),
            Obj("ConfigMap", "other"),
            Obj("Secret", "redis-auth"),
            Obj("Secret", "registry-pull"),
            Obj("ServiceAccount", "redis"),
            binding,
            Obj("Role", "redis", group: "rbac.authorization.k8s.io/v1"),
            Obj("PersistentVolumeClaim", "data-redis-0"),
            Obj("PersistentVolumeClaim", "data-redis-1"),
            Obj("PersistentVolumeClaim", "data-redis-other-0"),
            Obj("Service", "redis", spec: new JsonObject { ["selector"] = new JsonObject { ["app"] = "redis" } }),
            Obj("Service", "web", spec: new JsonObject { ["selector"] = new JsonObject { ["app"] = "web" } }),
            ingress,
            Obj("HorizontalPodAutoscaler", "redis", group: "autoscaling/v2", spec: new JsonObject { ["scaleTargetRef"] = new JsonObject { ["kind"] = "StatefulSet", ["name"] = "redis" } }),
            Obj("PodDisruptionBudget", "redis", group: "policy/v1", spec: new JsonObject { ["selector"] = new JsonObject { ["matchLabels"] = new JsonObject { ["tier"] = "cache" } } }),
        ];
    }

    [Fact]
    public void Into_another_cluster_classes_are_mapped_and_what_only_the_old_cluster_means_is_left_out()
    {
        var rewrite = new ObjectRewrite(new Dictionary<string, string> { ["vsphere-csi"] = "nfs-client" }, OtherCluster: true);

        // A PVC and a StatefulSet's claim templates: the class in the spec and in the legacy annotation.
        var claim = Obj("PersistentVolumeClaim", "data", spec: new JsonObject { ["storageClassName"] = "vsphere-csi" });
        claim["metadata"]!["annotations"] = new JsonObject { ["volume.beta.kubernetes.io/storage-class"] = "vsphere-csi" };
        claim["metadata"]!["finalizers"] = new JsonArray("kubernetes.io/pvc-protection");
        var preparedClaim = KubernetesObjects.Prepare(claim, "shop", "shop", rewrite);
        Assert.Equal("nfs-client", (string?)preparedClaim["spec"]!["storageClassName"]);
        Assert.Equal("nfs-client", (string?)preparedClaim["metadata"]!["annotations"]!["volume.beta.kubernetes.io/storage-class"]);
        Assert.Null(preparedClaim["metadata"]!["finalizers"]);

        var set = Obj("StatefulSet", "redis", "apps/v1", new JsonObject
        {
            ["volumeClaimTemplates"] = new JsonArray(
                new JsonObject { ["metadata"] = new JsonObject { ["name"] = "data" }, ["spec"] = new JsonObject { ["storageClassName"] = "vsphere-csi" } },
                new JsonObject { ["metadata"] = new JsonObject { ["name"] = "logs" }, ["spec"] = new JsonObject { ["storageClassName"] = "fast" } }),
        });
        var preparedSet = KubernetesObjects.Prepare(set, "shop", "shop", rewrite);
        Assert.Equal(["nfs-client", "fast"], preparedSet["spec"]!["volumeClaimTemplates"]!.AsArray().Select(t => (string?)t!["spec"]!["storageClassName"]));

        // Rancher's project fields and a Service's addresses of the old network go; other labels stay.
        var space = new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "Namespace",
            ["metadata"] = new JsonObject
            {
                ["name"] = "shop",
                ["labels"] = new JsonObject { ["field.cattle.io/projectId"] = "p-abc", ["team"] = "web" },
                ["annotations"] = new JsonObject { ["cattle.io/status"] = "{}", ["lifecycle.cattle.io/create.namespace-auth"] = "true", ["note"] = "keep" },
                ["finalizers"] = new JsonArray("controller.cattle.io/namespace-auth"),
            },
        };
        var preparedSpace = KubernetesObjects.Prepare(space, "shop", "shop", rewrite);
        Assert.Equal(["team"], preparedSpace["metadata"]!["labels"]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(["note"], preparedSpace["metadata"]!["annotations"]!.AsObject().Select(kv => kv.Key));
        Assert.Null(preparedSpace["metadata"]!["finalizers"]);

        var balancer = KubernetesObjects.Prepare(
            Obj("Service", "web", spec: new JsonObject { ["type"] = "LoadBalancer", ["loadBalancerIP"] = "10.26.0.50", ["externalIPs"] = new JsonArray("10.26.0.51") }), "shop", "shop", rewrite);
        Assert.Null(balancer["spec"]!["loadBalancerIP"]);
        Assert.Null(balancer["spec"]!["externalIPs"]);

        // In the same cluster the finalizers and fields stay; an unmapped class keeps its name.
        var same = KubernetesObjects.Prepare(space, "shop", "shop", rewrite with { OtherCluster = false });
        Assert.NotNull(same["metadata"]!["finalizers"]);
        Assert.Equal("fast", (string?)KubernetesObjects.Prepare(set, "shop", "shop", rewrite with { OtherCluster = false })["spec"]!["volumeClaimTemplates"]![1]!["spec"]!["storageClassName"]);

        // Rancher's RoleBindings for its projects stay out of another cluster; one made by hand does not.
        var projectBinding = Obj("RoleBinding", "rb-abc", "rbac.authorization.k8s.io/v1");
        projectBinding["metadata"]!["labels"] = new JsonObject { ["authz.cluster.cattle.io/rtb-owner-updated"] = "p-abc" };
        var creatorBinding = Obj("RoleBinding", "u-xyz-admin", "rbac.authorization.k8s.io/v1");
        creatorBinding["metadata"]!["labels"] = new JsonObject { ["cattle.io/creator"] = "norman" };
        Assert.Equal("rancher", KubernetesPlatformSet.Legacy.MadeBy(projectBinding)?.Profile.Id);
        Assert.Equal("rancher", KubernetesPlatformSet.Legacy.MadeBy(creatorBinding)?.Profile.Id);
        Assert.Null(KubernetesPlatformSet.Legacy.MadeBy(Obj("RoleBinding", "redis", "rbac.authorization.k8s.io/v1")));

        // So do the objects of Rancher's API groups (the App records of its Helm releases); others' groups that end alike do not.
        Assert.Equal("rancher", KubernetesPlatformSet.Legacy.MadeBy(Obj("App", "redis", "catalog.cattle.io/v1"))?.Profile.Id);
        Assert.Equal("rancher", KubernetesPlatformSet.Legacy.MadeBy(Obj("Setting", "x", "cattle.io/v1"))?.Profile.Id);
        Assert.Null(KubernetesPlatformSet.Legacy.MadeBy(Obj("Thing", "x", "notcattle.io/v1")));
        Assert.Null(KubernetesPlatformSet.Legacy.MadeBy(Obj("Certificate", "x", "cert-manager.io/v1")));
    }

    private static JsonObject Obj(string kind, string name, string group = "v1", JsonObject? spec = null)
    {
        var o = new JsonObject
        {
            ["apiVersion"] = group,
            ["kind"] = kind,
            ["metadata"] = new JsonObject
            {
                ["name"] = name, ["namespace"] = "shop", ["uid"] = $"uid-{name}", ["resourceVersion"] = "42",
                ["ownerReferences"] = new JsonArray(new JsonObject { ["kind"] = "Thing", ["name"] = "x", ["controller"] = false }),
            },
            ["status"] = new JsonObject { ["phase"] = "Whatever" },
        };
        if (spec is not null)
        {
            o["spec"] = spec;
        }

        return o;
    }
}
