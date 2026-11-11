using System.Runtime.CompilerServices;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// An open connection to a cluster: inventory, backups of namespaces, and the cleanup of leftover objects. Restores follow
/// (spec 2026-11-11-1343-kubernetes-workload-backup, task 8).
/// </summary>
/// <summary>Reads a namespace of the live cluster the way a backup stores it (for comparing it with a restore point).</summary>
public interface IKubernetesNamespaceReader
{
    Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ReadObjectsAsync(string ns, CancellationToken cancellationToken);
}

internal sealed class KubernetesSession(
    IKubernetes client, ConnectionSettings settings, IMoverChannels? channels = null, IMoverImageSource? images = null, KubernetesTimings? timings = null, IPodExecutor? executor = null)
    : IHypervisorSession, IKubernetesNamespaceReader, IKubernetesFileRestore
{
    private PlatformInfo? _platform;

    public PlatformInfo Platform => _platform ?? new PlatformInfo(string.Empty, settings.ClusterName(), settings.Address, "Kubernetes", string.Empty);

    public async Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var inventory = await KubernetesInventory.GetAsync(client, settings, cancellationToken).ConfigureAwait(false);
        _platform = inventory.Platform;
        return inventory;
    }

    /// <summary>A cluster has no storages of its own to report here (its volumes are in the inventory).</summary>
    public Task<StorageState> GetStorageStateAsync(CancellationToken cancellationToken) => Task.FromResult(new StorageState([], [], 0, 0));

    public Task<IReadOnlyList<string>> GetGuestAddressesAsync(string vmId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Backs up the namespace <see cref="VmBackupRequest.VmId"/>: its objects, hooks, and the data of its PVCs with CSI snapshots.</summary>
    public async Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_platform is null)
        {
            try
            {
                _platform = await KubernetesInventory.GetPlatformAsync(client, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (k8s.Autorest.HttpOperationException ex)
            {
                throw new KubernetesOperationException($"Reading the version of cluster {settings.ClusterName()} failed: {VolumeSnapshotFlow.Problem(ex)}", ex);
            }
        }

        return await KubernetesNamespaceBackup.StartAsync(
            client, settings, _platform.ProductVersion, request, log, ledger, channels, images, executor ?? new ClientPodExecutor(client), timings ?? new KubernetesTimings(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> ReadObjectsAsync(string ns, CancellationToken cancellationToken)
    {
        var flow = new VolumeSnapshotFlow(client, settings.ClusterName(), NullOperationLog.Instance, new InMemoryArtifactLedger(), new KubernetesTimings());
        var volumes = await flow.CallAsync($"listing the PVCs of {ns}", () => KubernetesInventory.VolumesAsync(client, ns, cancellationToken)).ConfigureAwait(false);
        var collected = await KubernetesResources.CollectAsync(client, settings.ClusterName(), ns, volumes, NullOperationLog.Instance, cancellationToken).ConfigureAwait(false);
        return [.. KubernetesResources.Read(collected.Content)];
    }

    /// <summary>Restores a namespace's restore point (its objects and volumes) into this cluster.</summary>
    public Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Manifest.Kubernetes is null || plan.Kubernetes is null)
        {
            throw new NotSupportedException($"Only restore points of Kubernetes namespaces can be restored into cluster {settings.ClusterName()}.");
        }

        IVmRestoreSession restore = new KubernetesNamespaceRestore(client, settings, plan, log, ledger, channels, images, timings ?? new KubernetesTimings());
        return Task.FromResult(restore);
    }

    /// <summary>Starts a mover pod next to a live PVC that puts restored files into it.</summary>
    public async Task<IPvcFileRestore> BeginFileRestoreAsync(PvcFileRestoreRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var flow = new VolumeSnapshotFlow(client, settings.ClusterName(), log, ledger, timings ?? new KubernetesTimings()) { Channels = channels };
        return await PvcFileImport.StartAsync(flow, channels, images, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a leftover object of a backup or restore (the ledger's id: <c>namespace/name</c>, or <c>name</c> when cluster-scoped).</summary>
    public async Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var slash = artifact.NativeId.IndexOf('/', StringComparison.Ordinal);
        var (ns, name) = slash < 0 ? ((string?)null, artifact.NativeId) : (artifact.NativeId[..slash], artifact.NativeId[(slash + 1)..]);
        var flow = new VolumeSnapshotFlow(client, settings.ClusterName(), log, new InMemoryArtifactLedger(), new KubernetesTimings());
        if (artifact.Kind == KubernetesArtifactKinds.ScaledWorkload)
        {
            await WorkloadPause.ResumeAsync(flow, artifact.NativeId, artifact.Data, cancellationToken).ConfigureAwait(false);
            log.Info($"Workload {artifact.NativeId} scaled back in cluster {settings.ClusterName()} (a PVC swap did not finish).");
            return;
        }

        await flow.DeleteAsync(artifact.Kind, ns, name, cancellationToken).ConfigureAwait(false);
        log.Info($"Leftover {artifact.Kind} {artifact.NativeId} deleted in cluster {settings.ClusterName()}.");
    }

    public Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The cluster {settings.ClusterName()} has no artifacts of backdafuckup yet ({artifact.Kind} {artifact.NativeId}).");

    public Task ReleaseChangeTokensAsync(IEnumerable<ChangeToken> tokens, IOperationLog log, CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<ProviderArtifact> FindTaggedArtifactsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
