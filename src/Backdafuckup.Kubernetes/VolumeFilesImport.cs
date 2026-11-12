using System.Net;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The restore of a volume as files (spec 2026-11-11-1343, phase 2), for a target StorageClass without snapshots (NFS,
/// local-path, a CSI driver without a snapshot class): a new Filesystem PVC of that class in the work namespace, a mover pod
/// that puts the files the server reads from the volume's image into it, with owners, modes and times. The volume is then
/// handed over to the target PVC: its PV is kept (Retain) while the work PVC is deleted, bound to the target PVC by its
/// claim reference, and given its reclaim policy back. Not used for drivers with snapshots: on vSphere a rebound volume
/// is lost to CNS (see <see cref="VolumeImport"/>). A class whose provisioner cannot make a volume the PVC's shared access
/// modes (ReadWriteMany on local-path) gets a second work PVC as ReadWriteOnce, and the PVC is restored that way.
/// </summary>
internal sealed class VolumeFilesImport : IFileLevelDiskWriter
{
    private readonly VolumeSnapshotFlow _flow;
    private readonly RestoreDiskTarget _target;
    private readonly string _name;
    private readonly string _run;
    private readonly string _source;
    private readonly List<(string Kind, string Name, Guid LedgerId)> _created = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverFilesChannel? _channel;
    private string _claim;
    private List<string> _modes = ["ReadWriteOnce"];
    private string? _reducedFrom;
    private string? _volume;
    private string? _reclaimPolicy;
    private bool _bound;
    private int _disposed;

    private VolumeFilesImport(VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string storageClass, string source)
    {
        _flow = flow;
        _run = run;
        _target = target;
        _source = source;
        StorageClass = storageClass;
        _name = VolumeSnapshotFlow.ObjectName(run, $"f-{VolumeSnapshotFlow.Hash(source)}");
        _claim = _name;
        Description = $"PVC {source} (cluster {flow.Cluster})";
    }

    private static string Work => KubernetesSetup.WorkNamespace;

    /// <summary>The volume's import: the work PVC the files are written to.</summary>
    public string DiskId => _name;

    /// <summary>The target cluster's StorageClass of the volume; the target PVC gets the same.</summary>
    public string StorageClass { get; }

    public string Description { get; }

    public long Capacity => _target.CapacityBytes;

    public DiskWriterCapabilities Capabilities => DiskWriterCapabilities.Sequential;

    public long TransferredBytes { get; private set; }

    /// <summary>The access modes of the volume the files went into, for the restored PVC: the backed-up PVC's, or fewer (see the class).</summary>
    public IReadOnlyList<string> AccessModes => _modes;

    private string PodName => $"{_claim}-mover";

    private string TokenSecret => $"{_name}-token";

    /// <summary>Creates the empty Filesystem PVC the files go into.</summary>
    /// <param name="accessModes">Those of the backed-up PVC (the volume is handed over to its restored copy).</param>
    public static async Task<VolumeFilesImport> CreateAsync(
        VolumeSnapshotFlow flow, string run, RestoreDiskTarget target, string storageClass, IReadOnlyList<string> accessModes, string source, CancellationToken cancellationToken)
    {
        var import = new VolumeFilesImport(flow, run, target, storageClass, source);
        if (accessModes.Count > 0)
        {
            import._modes = [.. accessModes];
        }

        await import.CreateClaimAsync(cancellationToken).ConfigureAwait(false);
        return import;
    }

    /// <summary>
    /// Whether a provisioner's warning says it cannot make a volume of the requested access modes (local-path: "NodePath only
    /// supports ReadWriteOnce and ReadWriteOncePod (1.22+) access modes"; a CSI driver: an unsupported volume capability).
    /// </summary>
    internal static bool RefusesAccessModes(string? message) =>
        message is not null
        && (message.Contains("access mode", StringComparison.OrdinalIgnoreCase) || message.Contains("accessmode", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ReadWriteMany", StringComparison.Ordinal) || message.Contains("ReadOnlyMany", StringComparison.Ordinal)
            || message.Contains("MULTI_NODE", StringComparison.Ordinal) || message.Contains("volume capabilit", StringComparison.OrdinalIgnoreCase));

    private static bool Shared(IEnumerable<string> modes) => modes.Any(m => m is "ReadWriteMany" or "ReadOnlyMany");

    private Task CreateClaimAsync(CancellationToken cancellationToken) =>
        CreateAsync(KubernetesArtifactKinds.PersistentVolumeClaim, _claim, () => _flow.Client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim
            {
                Metadata = Meta(_claim),
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = [.. _modes],
                    VolumeMode = "Filesystem",
                    StorageClassName = StorageClass,
                    Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = VolumeSnapshotFlow.Quantity(_target.CapacityBytes) } },
                },
            },
            Work,
            cancellationToken: cancellationToken));

    public Task WriteAsync(IDiskImageSource image, IProgress<long>? processed, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Description} is restored as files: the restore streams them through OpenFilesAsync.");

    public async Task<IMoverFilesChannel> OpenFilesAsync(CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        if (_flow.Channels is not { } channels || _flow.Mover is not { } mover)
        {
            throw new KubernetesOperationException($"Volume data cannot be restored to cluster {_flow.Cluster}: this server has no mover channel or image.");
        }

        await _flow.EnsurePullSecretAsync(mover, cancellationToken).ConfigureAwait(false);
        _channel = await channels.OpenFilesAsync(Description, cancellationToken, _flow.Timings.MoverConnect).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        await CreateAsync(KubernetesArtifactKinds.Secret, TokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(TokenSecret), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Work,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await CreatePodAsync(cancellationToken).ConfigureAwait(false);
        _watching = SuperviseAsync(_channel, _stopWatching.Token);
        return _channel;
    }

    private async Task CreatePodAsync(CancellationToken cancellationToken)
    {
        var mover = _flow.Mover!;
        var endpoint = _channel!.Endpoint;
        await CreateAsync(KubernetesArtifactKinds.Pod, PodName, () => _flow.Client.CoreV1.CreateNamespacedPodAsync(
            MoverPods.BuildFiles(_claim, Meta(PodName), mover, endpoint, TokenSecret, node: null, keep: false, owners: true),
            Work,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        _flow.Log.Info($"Mover pod {Work}/{PodName} started for {Description}: a new volume of the StorageClass {StorageClass} ({string.Join(", ", _modes)}), written as files; image {mover.Image}.");
    }

    /// <summary>
    /// Watches the mover pod; when the class's provisioner refuses the volume's shared access modes, the work PVC and its pod
    /// are made again as ReadWriteOnce (once), and watched again.
    /// </summary>
    private async Task SuperviseAsync(IMoverFilesChannel channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            string? refused = null;
            await MoverPods.WatchAsync(
                _flow, PodName, _source, channel.Abort, cancellationToken, pending: $"waiting for its new volume of the StorageClass {StorageClass}", mode: "restore", claim: _claim,
                claimProblem: warning =>
                {
                    if (_reducedFrom is not null || !Shared(_modes) || !RefusesAccessModes(warning.Message))
                    {
                        return false;
                    }

                    refused = warning.Message;
                    return true;
                }).ConfigureAwait(false);
            if (refused is null)
            {
                return;
            }

            try
            {
                await ReduceAccessModesAsync(refused, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or HttpOperationException or HttpRequestException)
            {
                channel.Abort(new KubernetesOperationException(
                    $"{Description}: the StorageClass {StorageClass} cannot make a volume that is {_reducedFrom} ({refused}), and a ReadWriteOnce volume could not be started instead: {ex.Message}",
                    ex));
                return;
            }
        }
    }

    /// <summary>The work PVC (never bound: its volume was refused) and its pod go; a ReadWriteOnce PVC and a new pod take their place.</summary>
    private async Task ReduceAccessModesAsync(string refused, CancellationToken cancellationToken)
    {
        _reducedFrom = string.Join(", ", _modes);
        foreach (var (kind, name, ledgerId) in _created.Where(c => c.Kind is KubernetesArtifactKinds.Pod or KubernetesArtifactKinds.PersistentVolumeClaim)
                     .OrderBy(c => c.Kind == KubernetesArtifactKinds.Pod ? 0 : 1).ToList())
        {
            await _flow.DeleteAsync(kind, Work, name, cancellationToken).ConfigureAwait(false);
            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _created.RemoveAll(c => c.LedgerId == ledgerId);
        }

        var single = _modes.Where(m => !Shared([m])).ToList();
        _modes = single.Count > 0 ? single : ["ReadWriteOnce"];
        _claim = $"{_name}-o"; // a new name: the old PVC and pod may still be on their way out
        _flow.Log.Warning(
            $"{Description}: the StorageClass {StorageClass} cannot make a volume that is {_reducedFrom} ({refused}); the volume is made {string.Join(", ", _modes)} instead, " +
            "and the PVC is restored that way: pods on different nodes cannot mount it at the same time.");
        await CreateClaimAsync(cancellationToken).ConfigureAwait(false);
        await CreatePodAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MoverFileReport> FinishFilesAsync(CancellationToken cancellationToken)
    {
        if (_channel is null)
        {
            throw new InvalidOperationException("The mover of the volume was not started.");
        }

        var report = await _channel.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        TransferredBytes = report.Bytes;
        await StopWatchingAsync().ConfigureAwait(false);
        foreach (var (kind, name, ledgerId) in _created.Where(c => c.Kind != KubernetesArtifactKinds.PersistentVolumeClaim).OrderBy(c => c.Kind == KubernetesArtifactKinds.Pod ? 0 : 1).ToList())
        {
            await _flow.DeleteAsync(kind, Work, name, cancellationToken).ConfigureAwait(false);
            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _created.RemoveAll(c => c.LedgerId == ledgerId);
        }

        return report;
    }

    /// <summary>
    /// Frees the written volume for the target PVC <paramref name="targetNamespace"/>/<paramref name="claim"/>: its PV is
    /// kept (Retain) while the work PVC is deleted, then reserved for the target PVC by its claim reference. Returns the PV,
    /// which the target PVC names as its volume; <see cref="CompleteHandOverAsync"/> waits until it is bound.
    /// </summary>
    public async Task<string> HandOverAsync(string targetNamespace, string claim, CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        var work = await _flow.CallAsync($"reading the PVC {Work}/{_claim}", () => client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(_claim, Work, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var volume = work.Spec?.VolumeName is { Length: > 0 } bound
            ? bound
            : throw new KubernetesOperationException($"The PVC {Work}/{_claim} the files of {_source} were written to is not bound to a volume (cluster {_flow.Cluster}).");
        var pv = await _flow.CallAsync($"reading the PV {volume}", () => client.CoreV1.ReadPersistentVolumeAsync(volume, cancellationToken: cancellationToken)).ConfigureAwait(false);
        _reclaimPolicy = pv.Spec?.PersistentVolumeReclaimPolicy ?? "Delete";
        _volume = volume;
        await PatchVolumeAsync($"keeping the PV {volume} (Retain) while it changes its PVC", ReclaimPolicy("Retain"), cancellationToken).ConfigureAwait(false);

        var ledger = _created.Single(c => c.Kind == KubernetesArtifactKinds.PersistentVolumeClaim);
        await _flow.DeleteAsync(KubernetesArtifactKinds.PersistentVolumeClaim, Work, _claim, cancellationToken).ConfigureAwait(false);
        await _flow.Ledger.UpdateAsync(ledger.LedgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
        _created.Remove(ledger);
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var current = await _flow.CallAsync($"reading the PV {volume}", () => client.CoreV1.ReadPersistentVolumeAsync(volume, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (current.Status?.Phase is "Released" or "Available" || current.Spec?.ClaimRef?.Name != _claim)
            {
                break;
            }

            if (DateTimeOffset.UtcNow - started > _flow.Timings.Deleted)
            {
                throw new KubernetesOperationException($"The PV {volume} is still bound to {Work}/{_claim} {_flow.Timings.Deleted.TotalMinutes:0} minutes after the PVC was deleted (cluster {_flow.Cluster}).");
            }

            await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        var claimRef = new System.Text.Json.Nodes.JsonObject
        {
            ["spec"] = new System.Text.Json.Nodes.JsonObject
            {
                ["claimRef"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["namespace"] = targetNamespace, ["name"] = claim, ["uid"] = null, ["resourceVersion"] = null,
                },
            },
        }.ToJsonString();
        await PatchVolumeAsync($"reserving the PV {volume} for the PVC {targetNamespace}/{claim}", claimRef, cancellationToken).ConfigureAwait(false);
        return volume;
    }

    /// <summary>Waits until the target PVC is bound to the volume, then gives the PV its reclaim policy back.</summary>
    public async Task CompleteHandOverAsync(string targetNamespace, string claim, CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        var volume = _volume ?? throw new InvalidOperationException("The volume was not handed over.");
        var started = DateTimeOffset.UtcNow;
        while (true)
        {
            var pv = await _flow.CallAsync($"reading the PV {volume}", () => client.CoreV1.ReadPersistentVolumeAsync(volume, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (pv.Status?.Phase == "Bound" && pv.Spec?.ClaimRef is { } bound && bound.Name == claim && bound.NamespaceProperty == targetNamespace && !string.IsNullOrEmpty(bound.Uid))
            {
                break;
            }

            if (DateTimeOffset.UtcNow - started > _flow.Timings.Deleted)
            {
                throw new KubernetesOperationException(
                    $"The PVC {targetNamespace}/{claim} is not bound to its restored volume {volume} after {_flow.Timings.Deleted.TotalMinutes:0} minutes (cluster {_flow.Cluster}; the PV is {pv.Status?.Phase}).");
            }

            await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        _bound = true;
        await PatchVolumeAsync($"giving the PV {volume} its reclaim policy {_reclaimPolicy} back", ReclaimPolicy(_reclaimPolicy!), cancellationToken).ConfigureAwait(false);
        _flow.Log.Info($"PVC {targetNamespace}/{claim}: bound to its restored volume {volume} (StorageClass {StorageClass}).");
    }

    /// <summary>Stops the mover: its pod and secret go; the volume stays for the target PVC (see <see cref="ReleaseAsync"/>).</summary>
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

        using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
        try
        {
            foreach (var (kind, name, ledgerId) in _created.Where(c => c.Kind != KubernetesArtifactKinds.PersistentVolumeClaim)
                         .OrderBy(c => c.Kind == KubernetesArtifactKinds.Pod ? 0 : 1).ToList())
            {
                await _flow.DeleteAsync(kind, Work, name, timeout.Token).ConfigureAwait(false);
                await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
                _created.RemoveAll(c => c.LedgerId == ledgerId);
            }
        }
        catch (Exception ex) when (ex is KubernetesOperationException or HttpOperationException or OperationCanceledException or HttpRequestException)
        {
            _flow.Log.Warning($"The mover pod of {Description} or its secret could not be removed (the server removes them later): {ex.Message}");
        }

        _stopWatching.Dispose();
    }

    /// <summary>
    /// After a restore that did not hand the volume over: the work PVC, or a PV that was freed for the target PVC but never
    /// bound (with its reclaim policy back, so its storage goes too). Nothing once the volume is its PVC's.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_bound)
        {
            return;
        }

        foreach (var (kind, name, ledgerId) in _created.Where(c => c.Kind == KubernetesArtifactKinds.PersistentVolumeClaim).ToList())
        {
            await _flow.DeleteAsync(kind, Work, name, cancellationToken).ConfigureAwait(false);
            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _created.RemoveAll(c => c.LedgerId == ledgerId);
        }

        if (_volume is { } volume)
        {
            await PatchVolumeAsync($"giving the PV {volume} its reclaim policy back", ReclaimPolicy(_reclaimPolicy ?? "Delete"), cancellationToken).ConfigureAwait(false);
            await _flow.CallAsync($"deleting the PV {volume}", () => _flow.Client.CoreV1.DeletePersistentVolumeAsync(volume, cancellationToken: cancellationToken)).ConfigureAwait(false);
            _flow.Log.Warning($"The restored volume {volume} of {_source} did not reach its PVC; it is deleted again (cluster {_flow.Cluster}).");
            _volume = null;
        }
    }

    private static string ReclaimPolicy(string policy) =>
        new System.Text.Json.Nodes.JsonObject { ["spec"] = new System.Text.Json.Nodes.JsonObject { ["persistentVolumeReclaimPolicy"] = policy } }.ToJsonString();

    private async Task PatchVolumeAsync(string what, string mergePatch, CancellationToken cancellationToken)
    {
        var volume = _volume ?? throw new InvalidOperationException("No volume yet.");
        await _flow.CallAsync(what, () => _flow.Client.CoreV1.PatchPersistentVolumeAsync(new V1Patch(mergePatch, V1Patch.PatchType.MergePatch), volume, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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

    private async Task CreateAsync<T>(string kind, string objectName, Func<Task<T>> create)
    {
        var ledgerId = await _flow.Ledger.RecordAsync(kind, $"{Work}/{objectName}", null, null, CancellationToken.None).ConfigureAwait(false);
        _created.Add((kind, objectName, ledgerId));
        await _flow.CallAsync($"creating {kind} {Work}/{objectName}", create).ConfigureAwait(false);
    }

    private V1ObjectMeta Meta(string objectName) => new()
    {
        Name = objectName,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = _run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = _source },
    };
}
