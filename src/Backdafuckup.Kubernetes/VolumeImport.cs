using System.Globalization;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// A volume of a restore point written into the cluster: a block PVC in the work namespace on the original StorageClass,
/// a mover pod that writes the image into it, then a snapshot of it, exposed in the target namespace (a static
/// VolumeSnapshotContent with the volume-mode-change annotation and a VolumeSnapshot on it). The target PVC is created
/// from that snapshot (<see cref="DiskId"/>) as a Filesystem PVC: an ordinary volume of the CSI driver. Rebinding the
/// written volume itself through a static PV does not hold on vSphere: deleting the work PV, even with Retain, removes the
/// volume from CNS, after which it can neither be detached nor deleted.
/// </summary>
internal sealed class VolumeImport : IDiskWriter
{
    private readonly VolumeSnapshotFlow _flow;
    private readonly string _name;
    private readonly string _run;
    private readonly RestoreDiskTarget _target;
    private readonly string _targetNamespace;
    private readonly string? _snapshotClass;
    private readonly string _source;
    private readonly List<(string Kind, string? Namespace, string Name, Guid LedgerId)> _created = [];
    private readonly HashSet<Guid> _deleted = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverRestoreChannel? _channel;
    private int _disposed;
    private bool _handedOver;
    private string? _handle;
    private string _driver = string.Empty;

    private VolumeImport(VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string targetNamespace, string? snapshotClass, string source)
    {
        _flow = flow;
        _run = run;
        _target = target;
        _targetNamespace = targetNamespace;
        _snapshotClass = snapshotClass;
        _source = source;
        _name = VolumeSnapshotFlow.ObjectName(run, $"r-{VolumeSnapshotFlow.Hash(source)}");
    }

    private static string Work => KubernetesSetup.WorkNamespace;

    /// <summary>The volume's import: the work PVC it is written to (also the name of its snapshots).</summary>
    public string DiskId => _name;

    /// <summary>The VolumeSnapshot in the target namespace the target PVC is created from (once exposed).</summary>
    public string? Snapshot { get; private set; }

    /// <summary>The node the mover ran on: a target PVC on a StorageClass with WaitForFirstConsumer is provisioned there.</summary>
    public string? Node { get; private set; }

    public long Capacity => _target.CapacityBytes;

    public DiskWriterCapabilities Capabilities => DiskWriterCapabilities.Sequential | DiskWriterCapabilities.SkipsZeroRegions;

    public long TransferredBytes { get; private set; }

    public string PodName => $"{_name}-mover";

    /// <summary>Creates the empty block PVC the image is written to.</summary>
    public static async Task<VolumeImport> CreateAsync(
        VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string? storageClass, string? snapshotClass, string targetNamespace, string source, CancellationToken cancellationToken)
    {
        var import = new VolumeImport(flow, run, target, targetNamespace, snapshotClass, source);
        var size = target.CapacityBytes.ToString(CultureInfo.InvariantCulture);
        await import.CreateAsync(KubernetesArtifactKinds.PersistentVolumeClaim, Work, import._name, () => flow.Client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim
            {
                Metadata = import.Meta(import._name),
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = ["ReadWriteOnce"],
                    VolumeMode = "Block",
                    StorageClassName = string.IsNullOrEmpty(storageClass) ? null : storageClass,
                    Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = new(size) } },
                },
            },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        return import;
    }

    public async Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var client = _flow.Client;
        if (_flow.Channels is not { } channels || _flow.Mover is not { } mover)
        {
            throw new KubernetesOperationException($"Volume data cannot be restored to cluster {_flow.Cluster}: this server has no mover channel or image.");
        }

        _channel = await channels.OpenRestoreAsync($"{_source} (cluster {_flow.Cluster})", image, cancellationToken, _flow.Timings.MoverConnect, processed).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        var tokenSecret = $"{_name}-token";
        await CreateAsync(KubernetesArtifactKinds.Secret, Work, tokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(tokenSecret), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await CreateAsync(KubernetesArtifactKinds.Pod, Work, PodName, () => client.CoreV1.CreateNamespacedPodAsync(
            MoverPods.Build(PodName, _name, Meta(PodName), mover, endpoint, tokenSecret, "restore", null),
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        _flow.Log.Info($"Mover pod {Work}/{PodName} started to restore {_source}: new block PVC {_name} ({VolumeSnapshotFlow.Bytes(_target.CapacityBytes)}), image {mover.Image}.");
        var channel = _channel;
        _watching = MoverPods.WatchAsync(_flow, PodName, _source, channel.Abort, _stopWatching.Token, mode: "restore");

        TransferredBytes = await channel.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await StopWatchingAsync().ConfigureAwait(false);
        try
        {
            Node = (await client.CoreV1.ReadNamespacedPodAsync(PodName, Work, cancellationToken: cancellationToken).ConfigureAwait(false)).Spec?.NodeName;
        }
        catch (k8s.Autorest.HttpOperationException)
        {
            // Only a hint for WaitForFirstConsumer.
        }

        await DeleteCreatedAsync(KubernetesArtifactKinds.Pod, cancellationToken).ConfigureAwait(false);
        await DeleteCreatedAsync(KubernetesArtifactKinds.Secret, cancellationToken).ConfigureAwait(false);
        _flow.Log.Info($"{_source}: {VolumeSnapshotFlow.Bytes(TransferredBytes)} of data written; taking a snapshot of it for the target PVC.");

        // A snapshot of the written volume …
        await CreateAsync(KubernetesArtifactKinds.VolumeSnapshot, Work, _name, () => client.CustomObjects.CreateNamespacedCustomObjectAsync(
            new JsonObject
            {
                ["apiVersion"] = $"{VolumeSnapshotFlow.Group}/{VolumeSnapshotFlow.Version}",
                ["kind"] = "VolumeSnapshot",
                ["metadata"] = Metadata(_name),
                ["spec"] = new JsonObject
                {
                    ["volumeSnapshotClassName"] = _snapshotClass,
                    ["source"] = new JsonObject { ["persistentVolumeClaimName"] = _name },
                },
            },
            VolumeSnapshotFlow.Group,
            VolumeSnapshotFlow.Version,
            Work,
            "volumesnapshots",
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        var (contentName, _) = await _flow.WaitReadyAsync(Work, _name, $"the snapshot {Work}/{_name} of the restored volume", _flow.Timings.SnapshotReady, cancellationToken).ConfigureAwait(false);
        var content = VolumeSnapshotFlow.Element(await _flow.CallAsync($"reading the snapshot content {contentName}", () =>
            client.CustomObjects.GetClusterCustomObjectAsync(VolumeSnapshotFlow.Group, VolumeSnapshotFlow.Version, "volumesnapshotcontents", contentName, cancellationToken)).ConfigureAwait(false));
        _handle = VolumeSnapshotFlow.Text(content, "status", "snapshotHandle")
            ?? throw new KubernetesOperationException($"The snapshot content {contentName} of the restored volume has no snapshot handle (cluster {_flow.Cluster}).");
        _driver = VolumeSnapshotFlow.Text(content, "spec", "driver") ?? string.Empty;

        // The snapshot, the work PVC and what follows stay until the target PVC is made from them (ReleaseAsync).
        _handedOver = true;
    }

    /// <summary>
    /// Exposes the snapshot of the written volume in the target namespace (which must exist by now), where a Filesystem PVC
    /// may come from it: a static VolumeSnapshotContent with the volume-mode-change annotation, and a VolumeSnapshot on it.
    /// </summary>
    public async Task ExposeAsync(CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        var exposed = $"{_name}-x";
        await CreateAsync(KubernetesArtifactKinds.VolumeSnapshotContent, null, exposed, () => client.CustomObjects.CreateClusterCustomObjectAsync(
            new JsonObject
            {
                ["apiVersion"] = $"{VolumeSnapshotFlow.Group}/{VolumeSnapshotFlow.Version}",
                ["kind"] = "VolumeSnapshotContent",
                ["metadata"] = Metadata(exposed, new JsonObject { [VolumeSnapshotFlow.VolumeModeChangeAnnotation] = "true" }),
                ["spec"] = new JsonObject
                {
                    ["deletionPolicy"] = "Retain",
                    ["driver"] = _driver,
                    ["source"] = new JsonObject { ["snapshotHandle"] = _handle },
                    ["sourceVolumeMode"] = "Block",
                    ["volumeSnapshotClassName"] = _snapshotClass,
                    ["volumeSnapshotRef"] = new JsonObject { ["name"] = _name, ["namespace"] = _targetNamespace },
                },
            },
            VolumeSnapshotFlow.Group,
            VolumeSnapshotFlow.Version,
            "volumesnapshotcontents",
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await CreateAsync(KubernetesArtifactKinds.VolumeSnapshot, _targetNamespace, _name, () => client.CustomObjects.CreateNamespacedCustomObjectAsync(
            new JsonObject
            {
                ["apiVersion"] = $"{VolumeSnapshotFlow.Group}/{VolumeSnapshotFlow.Version}",
                ["kind"] = "VolumeSnapshot",
                ["metadata"] = Metadata(_name),
                ["spec"] = new JsonObject
                {
                    ["volumeSnapshotClassName"] = _snapshotClass,
                    ["source"] = new JsonObject { ["volumeSnapshotContentName"] = exposed },
                },
            },
            VolumeSnapshotFlow.Group,
            VolumeSnapshotFlow.Version,
            _targetNamespace,
            "volumesnapshots",
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await _flow.WaitReadyAsync(_targetNamespace, _name, $"the snapshot {_targetNamespace}/{_name} of the restored volume", _flow.Timings.SnapshotReady, cancellationToken).ConfigureAwait(false);
        Snapshot = _name;
    }

    /// <summary>
    /// Once the target PVC is bound (its volume copied from the snapshot): removes the snapshots, newest first, then the work
    /// PVC with its volume (a volume with snapshots cannot be deleted on vSphere, so the snapshot must be gone first).
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        foreach (var (kind, ns, name, ledgerId) in Enumerable.Reverse(_created).Where(c => !_deleted.Contains(c.LedgerId)).ToList())
        {
            await _flow.DeleteAsync(kind, ns, name, cancellationToken).ConfigureAwait(false);
            if (kind == KubernetesArtifactKinds.VolumeSnapshot && ns == Work)
            {
                await WaitGoneAsync(name, cancellationToken).ConfigureAwait(false);
            }

            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _deleted.Add(ledgerId);
        }
    }

    /// <summary>Removes the work pod and token; everything else goes in <see cref="ReleaseAsync"/>, or here when the write failed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopWatchingAsync().ConfigureAwait(false);
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (!_handedOver)
        {
            using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
            try
            {
                await ReleaseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                _flow.Log.Warning($"The work objects of the restore of {_source} could not all be deleted (retried later): {ex.Message}");
            }
        }

        _stopWatching.Dispose();
    }

    private async Task WaitGoneAsync(string snapshot, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        while (await VolumeSnapshotFlow.ExistsAsync(() => _flow.Client.CustomObjects.GetNamespacedCustomObjectAsync(
                   VolumeSnapshotFlow.Group, VolumeSnapshotFlow.Version, Work, "volumesnapshots", snapshot, cancellationToken)).ConfigureAwait(false)
               && DateTimeOffset.UtcNow - started < _flow.Timings.Deleted)
        {
            await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StopWatchingAsync()
    {
        await _stopWatching.CancelAsync().ConfigureAwait(false);
        try
        {
            await _watching.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private async Task DeleteCreatedAsync(string kind, CancellationToken cancellationToken)
    {
        foreach (var (createdKind, ns, name, ledgerId) in _created.Where(c => c.Kind == kind && !_deleted.Contains(c.LedgerId)).ToList())
        {
            await _flow.DeleteAsync(createdKind, ns, name, cancellationToken).ConfigureAwait(false);
            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _deleted.Add(ledgerId);
        }
    }

    private async Task CreateAsync<T>(string kind, string? ns, string objectName, Func<Task<T>> create, CancellationToken cancellationToken)
    {
        var id = ns is null ? objectName : $"{ns}/{objectName}";
        var ledgerId = await _flow.Ledger.RecordAsync(kind, id, null, null, cancellationToken).ConfigureAwait(false);
        _created.Add((kind, ns, objectName, ledgerId));
        await _flow.CallAsync($"creating {kind} {id}", create).ConfigureAwait(false);
    }

    private JsonObject Metadata(string objectName, JsonObject? annotations = null)
    {
        annotations ??= [];
        annotations[VolumeSnapshotFlow.SourceAnnotation] = _source;
        return new JsonObject
        {
            ["name"] = objectName,
            ["labels"] = new JsonObject { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = _run },
            ["annotations"] = annotations,
        };
    }

    private V1ObjectMeta Meta(string objectName) => new()
    {
        Name = objectName,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = _run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = _source },
    };
}
