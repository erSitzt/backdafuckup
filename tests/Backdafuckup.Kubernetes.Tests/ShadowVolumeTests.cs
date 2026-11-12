using k8s.Models;

namespace Backdafuckup.Kubernetes.Tests;

/// <summary>The read-only copy of a PV that a volume is read file by file through: what it copies, and what never.</summary>
public sealed class ShadowVolumeTests
{
    [Fact]
    public void A_copy_takes_only_the_source_and_how_to_mount_it_never_what_decides_the_sources_fate()
    {
        var pv = new V1PersistentVolume
        {
            Metadata = new V1ObjectMeta
            {
                Name = "pvc-1",
                Annotations = new Dictionary<string, string> { ["pv.kubernetes.io/provisioned-by"] = "cluster.local/nfs-subdir-external-provisioner" },
                Finalizers = ["kubernetes.io/pv-protection"],
            },
            Spec = new V1PersistentVolumeSpec
            {
                Capacity = new Dictionary<string, ResourceQuantity> { ["storage"] = new("5Gi") },
                AccessModes = ["ReadWriteMany"],
                PersistentVolumeReclaimPolicy = "Delete",
                StorageClassName = "nfs-client",
                MountOptions = ["nfsvers=4.1"],
                Nfs = new V1NFSVolumeSource { Server = "10.26.0.5", Path = "/export/prometheus" },
                ClaimRef = new V1ObjectReference { NamespaceProperty = "prometheus", Name = "data" },
            },
        };

        var (spec, describe) = ShadowVolume.CopySpec(pv, "bdfu-1a2b3c4d-r-0011aabb");

        Assert.Equal("NFS 10.26.0.5:/export/prometheus", describe);
        Assert.Equal(("10.26.0.5", "/export/prometheus", true), (spec.Nfs.Server, spec.Nfs.Path, spec.Nfs.ReadOnlyProperty));
        Assert.Equal(("Retain", string.Empty, "ReadOnlyMany"), (spec.PersistentVolumeReclaimPolicy, spec.StorageClassName, spec.AccessModes.Single()));
        Assert.Equal(["nfsvers=4.1"], spec.MountOptions);
        Assert.Equal(("backdafuckup-system", "bdfu-1a2b3c4d-r-0011aabb"), (spec.ClaimRef.NamespaceProperty, spec.ClaimRef.Name));

        // A hostPath copy only mounts a folder that is there: no empty folder made on the wrong node.
        var host = ShadowVolume.CopySpec(new V1PersistentVolume
        {
            Metadata = new V1ObjectMeta { Name = "pvc-2" },
            Spec = new V1PersistentVolumeSpec { HostPath = new V1HostPathVolumeSource { Path = "/var/lib/data", Type = "DirectoryOrCreate" } },
        }, "x").Spec;
        Assert.Equal(("/var/lib/data", "Directory"), (host.HostPath.Path, host.HostPath.Type));

        // Only NFS, hostPath and local are read this way.
        Assert.False(ShadowVolume.CanRead(new V1PersistentVolume { Spec = new V1PersistentVolumeSpec { Csi = new V1CSIPersistentVolumeSource { Driver = "x", VolumeHandle = "y" } } }));
    }
}
