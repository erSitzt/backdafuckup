using System.Runtime.CompilerServices;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// An open connection to a cluster. Inventory only so far: backing up and restoring namespaces follow (spec
/// 2026-11-11-1343-kubernetes-workload-backup, tasks 6–8).
/// </summary>
internal sealed class KubernetesSession(IKubernetes client, ConnectionSettings settings) : IHypervisorSession
{
    private PlatformInfo? _platform;

    public PlatformInfo Platform => _platform ?? new PlatformInfo(string.Empty, settings.Address, settings.Address, "Kubernetes", string.Empty);

    public async Task<Inventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var inventory = await KubernetesInventory.GetAsync(client, settings, cancellationToken).ConfigureAwait(false);
        _platform = inventory.Platform;
        return inventory;
    }

    /// <summary>A cluster has no storages of its own to report here (its volumes are in the inventory).</summary>
    public Task<StorageState> GetStorageStateAsync(CancellationToken cancellationToken) => Task.FromResult(new StorageState([], [], 0, 0));

    public Task<IReadOnlyList<string>> GetGuestAddressesAsync(string vmId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IVmBackupSession> BeginBackupAsync(VmBackupRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Backing up Kubernetes namespaces is not available yet (cluster {settings.Address}).");

    public Task<IVmRestoreSession> BeginRestoreAsync(VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Restoring into a Kubernetes cluster is not available yet (cluster {settings.Address}).");

    /// <summary>Deletes a leftover object of a backup or restore (the ledger's id: <c>namespace/name</c>, or <c>name</c> when cluster-scoped).</summary>
    public async Task CleanupArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var slash = artifact.NativeId.IndexOf('/', StringComparison.Ordinal);
        var (ns, name) = slash < 0 ? ((string?)null, artifact.NativeId) : (artifact.NativeId[..slash], artifact.NativeId[(slash + 1)..]);
        var flow = new VolumeSnapshotFlow(client, settings.Address, log, new InMemoryArtifactLedger(), new KubernetesTimings());
        await flow.DeleteAsync(artifact.Kind, ns, name, cancellationToken).ConfigureAwait(false);
        log.Info($"Leftover {artifact.Kind} {artifact.NativeId} deleted in cluster {settings.Address}.");
    }

    public Task CommitArtifactAsync(ProviderArtifact artifact, IOperationLog log, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"The cluster {settings.Address} has no artifacts of backdafuckup yet ({artifact.Kind} {artifact.NativeId}).");

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
