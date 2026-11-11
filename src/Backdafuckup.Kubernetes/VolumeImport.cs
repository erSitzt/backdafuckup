using System.Globalization;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// A volume of a restore point written into a new volume (spike S3): a block PVC in the work namespace on the original
/// StorageClass, a mover pod that writes the image into it, then the volume kept (its PV set to Retain, the PVC removed)
/// for the restore to bind it to the target PVC as a Filesystem volume. <see cref="DiskId"/> is the kept PV.
/// </summary>
internal sealed class VolumeImport : IDiskWriter
{
    /// <summary>A PV that holds restored data until it is bound to its PVC; its cleanup lets the cluster delete the volume.</summary>
    public const string RetainedVolumeKind = "k8s.RetainedVolume";

    private readonly VolumeSnapshotFlow _flow;
    private readonly string _name;
    private readonly string _run;
    private readonly RestoreDiskTarget _target;
    private readonly string _source;
    private readonly List<(string Kind, string? Namespace, string Name, Guid LedgerId)> _created = [];
    private readonly HashSet<Guid> _deleted = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverRestoreChannel? _channel;
    private int _disposed;

    private VolumeImport(VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string source)
    {
        _flow = flow;
        _run = run;
        _target = target;
        _source = source;
        _name = VolumeSnapshotFlow.ObjectName(run, $"r-{VolumeSnapshotFlow.Hash(source)}");
    }

    private static string Work => KubernetesSetup.WorkNamespace;

    public string DiskId => RetainedVolume ?? string.Empty;

    /// <summary>The PV that holds the written data (Retain, no claim) once the write is done.</summary>
    public string? RetainedVolume { get; private set; }

    public Guid? RetainedLedgerId { get; private set; }

    public long Capacity => _target.CapacityBytes;

    public DiskWriterCapabilities Capabilities => DiskWriterCapabilities.Sequential | DiskWriterCapabilities.SkipsZeroRegions;

    public long TransferredBytes { get; private set; }

    public string PodName => $"{_name}-mover";

    /// <summary>Creates the empty block PVC the image is written to.</summary>
    public static async Task<VolumeImport> CreateAsync(VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string? storageClass, string source, CancellationToken cancellationToken)
    {
        var import = new VolumeImport(flow, run, target, source);
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
        _watching = MoverPods.WatchAsync(_flow, PodName, _source, channel.Abort, _stopWatching.Token);

        TransferredBytes = await channel.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await StopWatchingAsync().ConfigureAwait(false);

        // The mover is done: remove it, then keep the volume without its work PVC.
        await DeleteCreatedAsync(KubernetesArtifactKinds.Pod, cancellationToken).ConfigureAwait(false);
        await DeleteCreatedAsync(KubernetesArtifactKinds.Secret, cancellationToken).ConfigureAwait(false);
        var claim = await _flow.CallAsync($"reading the PVC {Work}/{_name}", () => client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(_name, Work, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var volume = claim.Spec.VolumeName is { Length: > 0 } bound
            ? bound
            : throw new KubernetesOperationException($"The PVC {Work}/{_name} is not bound to a volume after its mover finished (cluster {_flow.Cluster}).");
        RetainedLedgerId = await _flow.Ledger.RecordAsync(RetainedVolumeKind, volume, null, null, cancellationToken).ConfigureAwait(false);
        await _flow.CallAsync($"keeping the volume {volume} (Retain)", () => client.CoreV1.PatchPersistentVolumeAsync(
            new V1Patch("""{"spec":{"persistentVolumeReclaimPolicy":"Retain"}}""", V1Patch.PatchType.MergePatch), volume, cancellationToken: cancellationToken)).ConfigureAwait(false);
        RetainedVolume = volume;
        await DeleteCreatedAsync(KubernetesArtifactKinds.PersistentVolumeClaim, cancellationToken).ConfigureAwait(false);
        _flow.Log.Info($"{_source}: {VolumeSnapshotFlow.Bytes(TransferredBytes)} of data written to the new volume {volume}.");
    }

    /// <summary>Removes what is left of the work objects; the engine and the restore session may both call it.</summary>
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

        // Whatever is left of the work objects (a failed write): the PVC goes with its volume (it is not Retain yet).
        using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            var (kind, ns, name, ledgerId) = _created[i];
            if (_deleted.Contains(ledgerId))
            {
                continue;
            }

            try
            {
                await _flow.DeleteAsync(kind, ns, name, timeout.Token).ConfigureAwait(false);
                await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                _flow.Log.Warning($"{kind} {ns}/{name} could not be deleted (retried later): {ex.Message}");
            }
        }

        _stopWatching.Dispose();
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

    private async Task CreateAsync<T>(string kind, string ns, string objectName, Func<Task<T>> create, CancellationToken cancellationToken)
    {
        var ledgerId = await _flow.Ledger.RecordAsync(kind, $"{ns}/{objectName}", null, null, cancellationToken).ConfigureAwait(false);
        _created.Add((kind, ns, objectName, ledgerId));
        await _flow.CallAsync($"creating {kind} {ns}/{objectName}", create).ConfigureAwait(false);
    }

    private V1ObjectMeta Meta(string objectName) => new()
    {
        Name = objectName,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = _run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = _source },
    };
}
