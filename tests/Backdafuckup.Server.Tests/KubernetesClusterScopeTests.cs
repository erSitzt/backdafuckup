using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Tests;

/// <summary>The whole-cluster job object: its exclude patterns and the namespaces they leave out.</summary>
public sealed class KubernetesClusterScopeTests
{
    [Fact]
    public void The_legacy_default_excludes_leave_out_the_system_rancher_and_fleet_namespaces_and_patterns_match_whole_names()
    {
        var excludes = KubernetesClusterScope.LegacyDefaultExcludes;
        foreach (var ns in new[] { "kube-system", "kube-public", "kube-node-lease", "cattle-system", "cattle-fleet-system", "fleet-default", "cluster-fleet-local-abc", "local", "backdafuckup-system" })
        {
            Assert.True(KubernetesClusterScope.IsExcluded(ns, excludes), ns);
        }

        foreach (var ns in new[] { "bdfu-spike", "default", "cert-manager", "istio-system", "my-cattle-app", "locale" })
        {
            Assert.False(KubernetesClusterScope.IsExcluded(ns, excludes), ns);
        }

        // The work namespace is never backed up, whatever the job says; an empty list leaves out nothing else.
        Assert.True(KubernetesClusterScope.IsExcluded("backdafuckup-system", []));
        Assert.False(KubernetesClusterScope.IsExcluded("kube-system", []));
    }

    [Fact]
    public void A_job_object_keeps_its_excludes_and_bad_patterns_are_named()
    {
        var id = KubernetesClusterScope.JobObject([" kube-system ", "cattle-*", "", "kube-system", "vmware-system-csi"]);
        Assert.Equal("cluster:kube-system,cattle-*,vmware-system-csi", id);
        Assert.True(KubernetesClusterScope.IsJobObject(id));
        Assert.False(KubernetesClusterScope.IsJobObject("bdfu-spike"));
        Assert.Equal(["kube-system", "cattle-*", "vmware-system-csi"], KubernetesClusterScope.Excludes(id));
        Assert.Equal([], KubernetesClusterScope.Excludes(KubernetesClusterScope.JobObject([])));
        Assert.Equal("Whole cluster without kube-system, cattle-*", KubernetesClusterScope.Describe(["kube-system", "cattle-*"]));

        Assert.Null(KubernetesClusterScope.Validate(KubernetesClusterScope.DefaultExcludes));
        Assert.Null(KubernetesClusterScope.Validate(KubernetesClusterScope.LegacyDefaultExcludes));
        Assert.Equal("'Kube_System' is no namespace name or pattern (lower-case letters, digits, '-' and '*').", KubernetesClusterScope.Validate(["Kube_System"]));
        Assert.NotNull(KubernetesClusterScope.Validate(["a/b"]));

        // The platform's token: first in the job object, the platform's patterns in its place at a run, other "@" items refused.
        Assert.Equal(["@platform"], KubernetesClusterScope.DefaultExcludes);
        var withPlatform = KubernetesClusterScope.JobObject(["vmware-system-csi", "@platform"]);
        Assert.Equal("cluster:@platform,vmware-system-csi", withPlatform);
        Assert.Equal("Whole cluster without the platform's namespaces and vmware-system-csi", KubernetesClusterScope.Describe(KubernetesClusterScope.Excludes(withPlatform)));
        Assert.Equal("Whole cluster without the platform's namespaces", KubernetesClusterScope.Describe(["@platform"]));
        Assert.Equal(["kube-*", "cattle-*", "vmware-system-csi"], KubernetesClusterScope.Expand(KubernetesClusterScope.Excludes(withPlatform), ["kube-*", "cattle-*"]));
        Assert.Equal(["kube-system"], KubernetesClusterScope.Expand(["kube-system"], ["cattle-*"]));
        Assert.False(KubernetesClusterScope.IsExcluded("kube-system", ["@platform"]));
        Assert.Equal("'@everything' is not known; '@platform' stands for the namespaces the cluster's platforms make.", KubernetesClusterScope.Validate(["@everything"]));

        // The member of the cluster-scoped objects is no namespace name and no job object.
        Assert.False(KubernetesClusterScope.IsJobObject(KubernetesClusterScope.Member));
        Assert.Equal("@cluster", KubernetesClusterScope.Member); // the same as the Kubernetes project's KubernetesClusterBackup.Member
    }
}
