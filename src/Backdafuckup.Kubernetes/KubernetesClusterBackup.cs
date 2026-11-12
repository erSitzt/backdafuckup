using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using k8s;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The backup of a cluster's cluster-scoped objects (the member <see cref="Member"/> of a whole-cluster job), shaped like
/// a VM's for the engine: no disks, the objects are the metadata. A cluster restore creates them before the namespaces of
/// the same run.
/// </summary>
internal sealed class KubernetesClusterBackup : IVmBackupSession
{
    /// <summary>The job member and object id of the cluster-scoped objects (the same as <c>Contracts.KubernetesClusterScope.Member</c>).</summary>
    public const string Member = "@cluster";

    private KubernetesClusterBackup(string run, VmDescriptor vm, VmMetadataBlob metadata, KubernetesNamespaceDescriptor descriptor)
    {
        SnapshotId = $"bdfu-{run}";
        Vm = vm;
        Metadata = metadata;
        Kubernetes = descriptor;
    }

    public VmDescriptor Vm { get; }

    public IReadOnlyList<SnapshotDisk> Disks => [];

    public VmMetadataBlob Metadata { get; }

    public string SnapshotId { get; }

    public GuestFreezeResult? GuestFreeze => null;

    public SnapshotFacts? Snapshot => null;

    public KubernetesNamespaceDescriptor Kubernetes { get; }

    public static async Task<KubernetesClusterBackup> StartAsync(
        IKubernetes client, ConnectionSettings settings, string kubernetesVersion, VmBackupRequest request, IOperationLog log, CancellationToken cancellationToken)
    {
        var cluster = settings.ClusterName();
        var objects = await KubernetesClusterResources.CollectAsync(client, cluster, log, cancellationToken).ConfigureAwait(false);
        var descriptor = new KubernetesNamespaceDescriptor
        {
            Cluster = cluster,
            Namespace = Member,
            ClusterScope = true,
            KubernetesVersion = kubernetesVersion,
            ObjectCount = objects.Count,
        };
        var vm = new VmDescriptor
        {
            VmId = Member,
            Name = "Cluster-scoped objects",
            Description = $"The cluster-scoped objects of cluster {cluster}: {objects.Count} (CRDs, ClusterRoles and their bindings, classes).",
        };
        return new KubernetesClusterBackup(request.RestorePointId.ToString("N")[..8], vm, new VmMetadataBlob(KubernetesResources.Format, objects.Content), descriptor);
    }

    public Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The cluster-scoped objects have no disks.");

    public Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The cluster-scoped objects have no disks.");

    public ChangeToken? GetNewChangeToken(SnapshotDisk disk) => null;

    public Task CommitAsync(IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
