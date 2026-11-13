using System.Text.Json;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;

namespace Backdafuckup.Storage.Tests;

/// <summary>Manifests written by older versions, without the members added since, read with those empty.</summary>
public sealed class ManifestCompatibilityTests
{
    /// <summary>
    /// The JSON source generator sets a member missing from the JSON to null (it initialises every member of a type with
    /// required members); garbage collection failed on manifests from before file trees with a NullReferenceException.
    /// </summary>
    [Fact]
    public void A_manifest_without_the_members_added_later_reads_them_as_empty_lists()
    {
        var manifest = new RestorePointManifest
        {
            RestorePointId = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow, Kind = RestorePointKind.Full,
            Source = new ManifestSource(HypervisorKind.Kubernetes, "uid", "web", "web", null),
            Vm = new VmDescriptor { VmId = "web", Name = "web" },
            Chunking = new ManifestChunking(4 << 20, "sha256", "zstd"),
            Disks = [],
            Kubernetes = new KubernetesNamespaceDescriptor { Cluster = "10.26.1.10", Namespace = "web" },
        };
        var json = JsonSerializer.SerializeToNode(manifest, CoreJsonContext.Default.RestorePointManifest)!.AsObject();
        json.Remove("fileTrees");
        json.Remove("excludedDisks");
        json.Remove("consistency");
        json["vm"]!.AsObject().Remove("nics");
        foreach (var name in new[] { "volumes", "hooks", "podVolumes", "containerWrites" })
        {
            json["kubernetes"]!.AsObject().Remove(name);
        }

        var read = JsonSerializer.Deserialize(json.ToJsonString(), CoreJsonContext.Default.RestorePointManifest)!;

        Assert.NotNull(read.FileTrees);
        Assert.NotNull(read.ExcludedDisks);
        Assert.NotNull(read.Vm.Nics);
        Assert.Equal("crash", read.Consistency);
        Assert.NotNull(read.Kubernetes!.Volumes);
        Assert.NotNull(read.Kubernetes.Hooks);
        Assert.NotNull(read.Kubernetes.PodVolumes);
        Assert.NotNull(read.Kubernetes.ContainerWrites);
    }
}
