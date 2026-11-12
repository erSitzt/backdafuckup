using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// A volume read file by file through a read-only copy of its PV (spec 2026-11-12-1952-kubernetes-file-level-volumes): for
/// NFS, hostPath and local PVs, whose data is reached by mounting the same source again. In the work namespace: the copy
/// of the PV (only its source, node affinity, mount options and capacity; read-only, <c>Retain</c>, no class, bound ahead
/// to its claim), the claim, the mover's token secret and its pod. Never copied: annotations (above all the provisioner's,
/// whose deletion would remove the export's folder), finalizers, the reclaim policy. Disposing removes them, the PV last.
/// </summary>
internal sealed class ShadowVolume : IFileTreeReader
{
    private readonly VolumeSnapshotFlow _flow;
    private readonly string _source;
    private readonly string _run;
    private readonly string _name;
    private readonly List<(string Kind, string? Namespace, string Name, Guid LedgerId)> _created = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverTreeChannel? _channel;
    private int _disposed;

    private ShadowVolume(VolumeSnapshotFlow flow, string source, string run, string name, string transport)
    {
        _flow = flow;
        _source = source;
        _run = run;
        _name = name;
        TransportName = transport;
    }

    public string TransportName { get; }

    private static string Work => KubernetesSetup.WorkNamespace;

    private string PodName => $"{_name}-mover";

    /// <summary>What can be read this way: a PV with an NFS, hostPath or local source.</summary>
    public static bool CanRead(V1PersistentVolume volume) => volume.Spec.Nfs is not null || volume.Spec.HostPath is not null || volume.Spec.Local is not null;

    /// <summary>
    /// Starts the mover for <paramref name="claim"/> in <paramref name="ns"/> (bound to <paramref name="volumeName"/>). A hostPath
    /// PV without node affinity is read on <paramref name="node"/>, the node of a pod that uses it.
    /// </summary>
    public static async Task<ShadowVolume> StartAsync(
        VolumeSnapshotFlow flow, string ns, string claim, string volumeName, string run, string? node, IMoverChannels channels, MoverDeployment mover,
        CancellationToken cancellationToken)
    {
        var source = $"PVC {ns}/{claim}";
        var pv = await flow.CallAsync($"reading the PV {volumeName} of {source}", () => flow.Client.CoreV1.ReadPersistentVolumeAsync(volumeName, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var name = $"bdfu-{run}-r-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{ns}/{claim}")))[..8]}";
        var (spec, describe) = CopySpec(pv, name);
        var shadow = new ShadowVolume(flow, source, run, name, $"a read-only copy of PV {volumeName} ({describe})");
        try
        {
            await shadow.StartAsync(pv, spec, node, channels, mover, cancellationToken).ConfigureAwait(false);
            return shadow;
        }
        catch
        {
            await shadow.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The copy's spec: the source and what is needed to mount it, never what decides the source's fate.</summary>
    internal static (V1PersistentVolumeSpec Spec, string Describe) CopySpec(V1PersistentVolume pv, string name)
    {
        var from = pv.Spec;
        var spec = new V1PersistentVolumeSpec
        {
            Capacity = from.Capacity is null ? null : new Dictionary<string, ResourceQuantity>(from.Capacity),
            AccessModes = ["ReadOnlyMany"],
            PersistentVolumeReclaimPolicy = "Retain",
            StorageClassName = string.Empty,
            MountOptions = from.MountOptions,
            NodeAffinity = from.NodeAffinity,
            VolumeMode = "Filesystem",
            ClaimRef = new V1ObjectReference { NamespaceProperty = KubernetesSetup.WorkNamespace, Name = name },
        };
        string describe;
        if (from.Nfs is { } nfs)
        {
            spec.Nfs = new V1NFSVolumeSource { Server = nfs.Server, Path = nfs.Path, ReadOnlyProperty = true };
            describe = $"NFS {nfs.Server}:{nfs.Path}";
        }
        else if (from.HostPath is { } hostPath)
        {
            // "Directory": an empty type or DirectoryOrCreate would let the node make an empty folder where the data is not.
            spec.HostPath = new V1HostPathVolumeSource { Path = hostPath.Path, Type = "Directory" };
            describe = $"hostPath {hostPath.Path}";
        }
        else if (from.Local is { } local)
        {
            spec.Local = new V1LocalVolumeSource { Path = local.Path, FsType = local.FsType };
            describe = $"local {local.Path}";
        }
        else
        {
            throw new KubernetesOperationException($"The PV {pv.Metadata.Name} is neither NFS nor hostPath nor local: it cannot be read file by file through a copy.");
        }

        return (spec, describe);
    }

    public async Task ReadAsync(Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken) =>
        await (_channel ?? throw new InvalidOperationException("The mover has not started.")).ConsumeAsync(consume, cancellationToken).ConfigureAwait(false);

    private async Task StartAsync(V1PersistentVolume pv, V1PersistentVolumeSpec spec, string? node, IMoverChannels channels, MoverDeployment mover, CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        var capacity = pv.Spec.Capacity is { } sizes && sizes.TryGetValue("storage", out var size) ? size : new ResourceQuantity("1Gi");
        await CreateAsync(KubernetesArtifactKinds.PersistentVolume, null, _name, () => client.CoreV1.CreatePersistentVolumeAsync(
            new V1PersistentVolume { Metadata = Meta(_name, pv.Metadata.Name), Spec = spec },
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await CreateAsync(KubernetesArtifactKinds.PersistentVolumeClaim, Work, _name, () => client.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(
            new V1PersistentVolumeClaim
            {
                Metadata = Meta(_name, pv.Metadata.Name),
                Spec = new V1PersistentVolumeClaimSpec
                {
                    // An explicit empty class: without it the default class would be set and the claim never bind to the copy.
                    StorageClassName = string.Empty,
                    VolumeName = _name,
                    AccessModes = ["ReadOnlyMany"],
                    Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string, ResourceQuantity> { ["storage"] = capacity } },
                },
            },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        await _flow.EnsurePullSecretAsync(mover, cancellationToken).ConfigureAwait(false);
        _channel = await channels.OpenTreeBackupAsync($"{_source} (cluster {_flow.Cluster})", cancellationToken, _flow.Timings.FilesMoverConnect).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        var tokenSecret = $"{_name}-token";
        await CreateAsync(KubernetesArtifactKinds.Secret, Work, tokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(tokenSecret, pv.Metadata.Name), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        await CreateAsync(KubernetesArtifactKinds.Pod, Work, PodName, () => client.CoreV1.CreateNamespacedPodAsync(
            MoverPods.BuildRead(_name, Meta(PodName, pv.Metadata.Name), mover, endpoint, tokenSecret, node),
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        _flow.Log.Info($"Mover pod {Work}/{PodName} started for {_source}: it reads the volume file by file through {TransportName}{(node is null ? string.Empty : $" on node {node}")}, image {mover.Image}.");
        var channel = _channel;
        _watching = MoverPods.WatchAsync(_flow, PodName, _source, channel.Abort, _stopWatching.Token, mode: "tree");
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

        // Newest first: the pod releases the claim, the claim the copy of the PV, which goes last. Each one left (a failed
        // delete) stays in the ledger for the cleanup that retries later.
        using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
        for (var i = _created.Count - 1; i >= 0; i--)
        {
            var (kind, ns, objectName, ledgerId) = _created[i];
            try
            {
                await _flow.DeleteAsync(kind, ns, objectName, timeout.Token).ConfigureAwait(false);
                await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                _flow.Log.Warning($"{kind} {(ns is null ? objectName : $"{ns}/{objectName}")} could not be deleted (retried later): {ex.Message}");
            }
        }

        _stopWatching.Dispose();
    }

    private async Task CreateAsync<T>(string kind, string? ns, string objectName, Func<Task<T>> create, CancellationToken cancellationToken)
    {
        var id = ns is null ? objectName : $"{ns}/{objectName}";
        var ledgerId = await _flow.Ledger.RecordAsync(kind, id, null, null, cancellationToken).ConfigureAwait(false);
        _created.Add((kind, ns, objectName, ledgerId));
        await _flow.CallAsync($"creating {kind} {id}", create).ConfigureAwait(false);
    }

    private V1ObjectMeta Meta(string objectName, string shadowOf) => new()
    {
        Name = objectName,
        Labels = new Dictionary<string, string>
        {
            [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup",
            [VolumeSnapshotFlow.RunLabel] = _run,
            [ShadowOfLabel] = shadowOf.Length <= 63 ? shadowOf : shadowOf[..63],
        },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = _source },
    };

    /// <summary>The PV a copy and its claim, secret and pod belong to.</summary>
    public const string ShadowOfLabel = "backdafuckup.io/shadow-of";

    internal static string Size(ResourceQuantity quantity) => quantity.ToInt64().ToString(CultureInfo.InvariantCulture);
}
