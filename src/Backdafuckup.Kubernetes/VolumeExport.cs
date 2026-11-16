using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// A snapshot exposed to a mover in the work namespace: the static VolumeSnapshotContent and VolumeSnapshot, the block
/// PVC from it, the mover's token secret and its pod. <see cref="Channel"/> is the volume as a sequential disk for the
/// backup pipeline; disposing removes the objects, newest first.
/// </summary>
internal sealed class VolumeExport(VolumeSnapshotFlow flow, TakenSnapshot snapshot, string name, string run) : IAsyncDisposable
{
    private readonly List<(string Kind, string? Namespace, string Name, Guid LedgerId)> _created = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverBackupChannel? _channel;
    private int _disposed;

    private static string Work => KubernetesSetup.WorkNamespace;

    public IMoverBackupChannel Channel => _channel ?? throw new InvalidOperationException("The export has not started.");

    public string PodName => $"{name}-mover";

    public async Task StartAsync(IMoverChannels channels, MoverDeployment mover, CancellationToken cancellationToken)
    {
        var client = flow.Client;
        var source = snapshot.Source;

        // The snapshot again, in the work namespace: a content of our own on the same storage snapshot. Retain: deleting it
        // leaves the storage snapshot to the original one. The annotation lets a block PVC come from a filesystem snapshot.
        await CreateAsync(KubernetesArtifactKinds.VolumeSnapshotContent, null, name, () => client.CustomObjects.CreateClusterCustomObjectAsync(
            new JsonObject
            {
                ["apiVersion"] = $"{VolumeSnapshotFlow.Group}/{VolumeSnapshotFlow.Version}",
                ["kind"] = "VolumeSnapshotContent",
                ["metadata"] = VolumeSnapshotFlow.Metadata(name, run, snapshot.Namespace, snapshot.Claim, annotations: new JsonObject { [VolumeSnapshotFlow.VolumeModeChangeAnnotation] = "true" }),
                ["spec"] = new JsonObject
                {
                    ["deletionPolicy"] = "Retain",
                    ["driver"] = snapshot.Driver,
                    ["source"] = new JsonObject { ["snapshotHandle"] = snapshot.Handle },
                    ["sourceVolumeMode"] = snapshot.VolumeMode,
                    ["volumeSnapshotClassName"] = snapshot.SnapshotClass,
                    ["volumeSnapshotRef"] = new JsonObject { ["name"] = name, ["namespace"] = Work },
                },
            },
            VolumeSnapshotFlow.Group,
            VolumeSnapshotFlow.Version,
            "volumesnapshotcontents",
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        await CreateAsync(KubernetesArtifactKinds.VolumeSnapshot, Work, name, () => client.CustomObjects.CreateNamespacedCustomObjectAsync(
            new JsonObject
            {
                ["apiVersion"] = $"{VolumeSnapshotFlow.Group}/{VolumeSnapshotFlow.Version}",
                ["kind"] = "VolumeSnapshot",
                ["metadata"] = VolumeSnapshotFlow.Metadata(name, run, snapshot.Namespace, snapshot.Claim),
                ["spec"] = new JsonObject
                {
                    ["volumeSnapshotClassName"] = snapshot.SnapshotClass,
                    ["source"] = new JsonObject { ["volumeSnapshotContentName"] = name },
                },
            },
            VolumeSnapshotFlow.Group,
            VolumeSnapshotFlow.Version,
            Work,
            "volumesnapshots",
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await flow.WaitReadyAsync(Work, name, $"the snapshot {Work}/{name} (of {source})", flow.Timings.SnapshotReady, cancellationToken).ConfigureAwait(false);

        // The volume the mover reads: a raw block device, a copy of the snapshot on the same storage.
        var size = snapshot.RestoreSize.ToString(CultureInfo.InvariantCulture);
        await CreateAsync(KubernetesArtifactKinds.PersistentVolumeClaim, Work, name, () => client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim
            {
                Metadata = Meta(),
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = ["ReadWriteOnce"],
                    VolumeMode = "Block",
                    StorageClassName = snapshot.StorageClass,
                    DataSource = new V1TypedLocalObjectReference { ApiGroup = VolumeSnapshotFlow.Group, Kind = "VolumeSnapshot", Name = name },
                    Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) } },
                },
            },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        await flow.EnsurePullSecretAsync(mover, cancellationToken).ConfigureAwait(false);

        _channel = await channels.OpenBackupAsync($"{source} (cluster {flow.Cluster})", snapshot.RestoreSize, cancellationToken, flow.Timings.MoverConnect).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        var tokenSecret = $"{name}-token";
        await CreateAsync(KubernetesArtifactKinds.Secret, Work, tokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(tokenSecret), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        await CreateAsync(KubernetesArtifactKinds.Pod, Work, PodName, () => client.CoreV1.CreateNamespacedPodAsync(
            MoverPods.Build(PodName, name, Meta(PodName), mover, endpoint, tokenSecret, "backup", size),
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        flow.Log.Info($"Mover pod {Work}/{PodName} started for {source}: block PVC {name} ({VolumeSnapshotFlow.Bytes(snapshot.RestoreSize)}) from the snapshot, image {mover.Image}.");
        var channel = _channel;
        _watching = MoverPods.WatchAsync(flow, PodName, snapshot.Source, channel.Abort, _stopWatching.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopWatching.CancelAsync().ConfigureAwait(false);
        try
        {
            await _watching.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        // Newest first: the pod releases the PVC, the PVC the exposed snapshot, which releases its content. Each one left
        // (a failed delete) stays in the ledger for the cleanup that retries later.
        using var timeout = new CancellationTokenSource(flow.Timings.Deleted);
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            var (kind, ns, objectName, ledgerId) = _created[i];
            try
            {
                await flow.DeleteAsync(kind, ns, objectName, timeout.Token).ConfigureAwait(false);
                await flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                flow.Log.Warning($"{kind} {(ns is null ? objectName : $"{ns}/{objectName}")} could not be deleted (retried later): {ex.Message}");
            }
        }

        _stopWatching.Dispose();
    }

    /// <summary>Records the object in the ledger, then creates it; an object that already exists (a retried run) is an error.</summary>
    private async Task CreateAsync<T>(string kind, string? ns, string objectName, Func<Task<T>> create, CancellationToken cancellationToken)
    {
        var id = ns is null ? objectName : $"{ns}/{objectName}";
        var ledgerId = await flow.Ledger.RecordAsync(kind, id, null, null, cancellationToken).ConfigureAwait(false);
        _created.Add((kind, ns, objectName, ledgerId));
        await flow.CallAsync($"creating {kind} {id}", create).ConfigureAwait(false);
    }

    private V1ObjectMeta Meta(string? objectName = null) => new()
    {
        Name = objectName ?? name,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = snapshot.Source },
    };
}
