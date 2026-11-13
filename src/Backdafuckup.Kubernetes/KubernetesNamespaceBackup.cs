using System.Collections.Concurrent;
using System.Diagnostics;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The backup of a namespace, shaped like a VM's for the engine: the namespace is the "VM", its PVCs with CSI snapshots
/// are the disks (read through mover pods), its objects are the metadata. Pre-hooks run before the first snapshot, post-
/// hooks after the last. Each snapshot is deleted as soon as its volume is read; disposing deletes any left.
/// </summary>
internal sealed class KubernetesNamespaceBackup : IVmBackupSession
{
    private readonly VolumeSnapshotFlow _flow;
    private readonly IMoverChannels? _channels;
    private readonly MoverDeployment? _mover;
    private readonly string _run;
    private readonly Dictionary<string, TakenSnapshot> _snapshots;
    private readonly ConcurrentDictionary<string, bool> _deleted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (NamespaceVolume Volume, string? Node)> _files;
    private readonly DeferredHooks? _postHooks;

    private KubernetesNamespaceBackup(
        VolumeSnapshotFlow flow, IMoverChannels? channels, MoverDeployment? mover, string run, VmDescriptor vm, IReadOnlyList<SnapshotDisk> disks,
        Dictionary<string, TakenSnapshot> snapshots, VmMetadataBlob metadata, GuestFreezeResult? freeze, SnapshotFacts? facts, KubernetesNamespaceDescriptor descriptor,
        Dictionary<string, (NamespaceVolume Volume, string? Node)> files, DeferredHooks? postHooks)
    {
        _flow = flow;
        _channels = channels;
        _mover = mover;
        _run = run;
        _snapshots = snapshots;
        _files = files;
        _postHooks = postHooks;
        Vm = vm;
        Disks = disks;
        Metadata = metadata;
        GuestFreeze = freeze;
        Snapshot = facts;
        Kubernetes = descriptor;
        FileTrees = [.. files.Values.Select(f => new FileTreeSource(
            f.Volume.Claim, f.Volume.Claim, "pvc", "shadow-pv", f.Volume.SizeBytes, $"PVC {descriptor.Namespace}/{f.Volume.Claim} (cluster {descriptor.Cluster})"))];
    }

    public IReadOnlyList<FileTreeSource> FileTrees { get; }

    /// <summary>Post-hooks held back until the volumes read file by file are read (Velero's order: their files are read before the post-hook).</summary>
    internal sealed class DeferredHooks(IPodExecutor executor, string cluster, string ns, IReadOnlyList<PodHook> hooks, List<KubernetesHookRecord> records)
    {
        private int _ran;

        public async Task RunAsync(IOperationLog log, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                records.AddRange(await KubernetesHooks.RunAsync(executor, cluster, ns, hooks, log, cancellationToken).ConfigureAwait(false));
            }
        }
    }

    public VmDescriptor Vm { get; }

    public IReadOnlyList<SnapshotDisk> Disks { get; }

    public VmMetadataBlob Metadata { get; }

    public string SnapshotId => $"bdfu-{_run}";

    public GuestFreezeResult? GuestFreeze { get; }

    public SnapshotFacts? Snapshot { get; }

    public KubernetesNamespaceDescriptor Kubernetes { get; }

    public static async Task<KubernetesNamespaceBackup> StartAsync(
        IKubernetes client, ConnectionSettings settings, string kubernetesVersion, KubernetesPlatformSet platforms, VmBackupRequest request, IOperationLog log, IArtifactLedger ledger,
        IMoverChannels? channels, IMoverImageSource? images, IPodExecutor executor, KubernetesTimings timings, CancellationToken cancellationToken)
    {
        var cluster = settings.ClusterName();
        var ns = request.VmId;
        var run = request.RestorePointId.ToString("N")[..8];
        var flow = new VolumeSnapshotFlow(client, cluster, log, ledger, timings);
        k8s.Models.V1Namespace space;
        try
        {
            space = await client.CoreV1.ReadNamespaceAsync(ns, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new KubernetesOperationException($"The namespace {ns} does not exist in cluster {cluster} (any more).", ex);
        }
        catch (HttpOperationException ex)
        {
            throw new KubernetesOperationException($"Reading the namespace {ns} in cluster {cluster} failed: {VolumeSnapshotFlow.Problem(ex)}", ex);
        }

        var volumes = await flow.CallAsync($"listing the PVCs of {ns}", () => KubernetesInventory.VolumesAsync(client, ns, cancellationToken)).ConfigureAwait(false);
        var pods = (await flow.CallAsync($"listing the pods of {ns}", () => client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: cancellationToken)).ConfigureAwait(false)).Items;
        var withData = volumes.Where(v => v.DataPath == DataPaths.Snapshot).ToList();

        // Volumes without snapshots, read file by file after the snapshots. A hostPath PV without node affinity has its data
        // on the node of the pods that use it; with none running, that node is not known.
        var files = new Dictionary<string, (NamespaceVolume Volume, string? Node)>(StringComparer.Ordinal);
        foreach (var volume in volumes.Where(v => v.DataPath == DataPaths.Files))
        {
            var node = pods.Where(p => p.Spec.Volumes?.Any(v => v.PersistentVolumeClaim?.ClaimName == volume.Claim) == true)
                .Select(p => p.Spec.NodeName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            if (volume.OnNodeOfConsumer && node is null)
            {
                log.Info($"PVC {volume.Claim}: objects only, not its data – a hostPath volume without node affinity and no pod using it: the node of its data is not known.");
                continue;
            }

            files[volume.Claim] = (volume, volume.OnNodeOfConsumer ? node : null);
            log.Info($"PVC {volume.Claim}: {volume.Reason}.");
        }

        foreach (var volume in volumes.Where(v => v.DataPath == DataPaths.ObjectsOnly))
        {
            log.Info($"PVC {volume.Claim}: objects only, not its data – {volume.Reason}.");
        }

        // The mover image first: without it no volume can be read, so nothing is snapshotted in vain.
        MoverDeployment? mover = null;
        if (withData.Count > 0 || files.Count > 0)
        {
            if (channels is null || images is null)
            {
                throw new KubernetesOperationException($"Volume data cannot be backed up from cluster {cluster}: this server has no mover channel.");
            }

            mover = await images.EnsurePushedAsync(line => log.Info(line), cancellationToken).ConfigureAwait(false);
        }

        var objects = await KubernetesResources.CollectAsync(client, cluster, ns, volumes, log, cancellationToken).ConfigureAwait(false);
        log.Info($"Namespace {ns}: {objects.Count} objects ({string.Join(", ", objects.PerKind.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Value} {k.Key}"))}).");

        // Hooks around the snapshots: the post-hooks run even when a snapshot fails (they undo what the pre-hooks did). With
        // volumes read file by file they wait until those are read (Velero's order).
        var preHooks = KubernetesHooks.Find(pods, KubernetesHooks.Pre);
        var postHooks = KubernetesHooks.Find(pods, KubernetesHooks.Post);
        var hookRecords = new List<KubernetesHookRecord>();
        var snapshots = new Dictionary<string, TakenSnapshot>(StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        TimeSpan? firstSnapshot = null;
        TimeSpan? lastSnapshot = null;
        TimeSpan preTime = TimeSpan.Zero;
        TimeSpan heldTime = TimeSpan.Zero;
        var succeeded = false;
        DeferredHooks? deferred = null;
        try
        {
            hookRecords.AddRange(await KubernetesHooks.RunAsync(executor, cluster, ns, preHooks, log, cancellationToken).ConfigureAwait(false));
            preTime = Stopwatch.GetElapsedTime(started);
            var fatal = preHooks.Zip(hookRecords).FirstOrDefault(p => p.First.FailOnError && p.Second.Error is not null);
            if (fatal.First is not null)
            {
                throw new KubernetesOperationException(
                    $"The pre-hook in {ns}/{fatal.First.Pod} ({fatal.First.Container}) failed: {fatal.Second.Error}. Its on-error is Fail, so the namespace is not backed up.");
            }

            var held = Stopwatch.GetTimestamp();
            foreach (var volume in withData)
            {
                var at = Stopwatch.GetElapsedTime(started);
                firstSnapshot ??= at;
                lastSnapshot = at;
                snapshots[volume.Claim] = await flow.TakeAsync(ns, volume.Claim, volume.SnapshotClass!, run, cancellationToken).ConfigureAwait(false);
            }

            heldTime = Stopwatch.GetElapsedTime(held);
            succeeded = true;
        }
        finally
        {
            if (succeeded && files.Count > 0 && postHooks.Count > 0)
            {
                deferred = new DeferredHooks(executor, cluster, ns, postHooks, hookRecords);
                log.Info($"The post-hooks of {ns} run after the files of {string.Join(", ", files.Keys)} are read.");
            }
            else
            {
                try
                {
                    hookRecords.AddRange(await KubernetesHooks.RunAsync(executor, cluster, ns, postHooks, log, succeeded ? cancellationToken : CancellationToken.None).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!succeeded)
                {
                    // Stopped while unwinding: the snapshots go below.
                }
            }

            if (!succeeded)
            {
                foreach (var taken in snapshots.Values)
                {
                    await DeleteQuietlyAsync(flow, taken).ConfigureAwait(false);
                }
            }
        }

        var snapshotTime = Stopwatch.GetElapsedTime(started) - preTime;
        var disks = withData.Select(v => snapshots[v.Claim]).Select(s => new SnapshotDisk(
            s.Claim, s.Source, s.Name, s.Claim, s.RestoreSize, false, s.StorageClass ?? string.Empty, s.StorageClass ?? s.Driver,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["handle"] = s.Handle, ["driver"] = s.Driver, ["snapshot"] = $"{s.Namespace}/{s.Name}" })).ToList();
        var preResults = hookRecords.Where(h => h.Phase == KubernetesHooks.Pre).ToList();
        var freeze = preResults.Count == 0
            ? null
            : preResults.FirstOrDefault(h => h.Error is not null) is { } failed
                ? GuestFreezeResult.NotConsistent($"The pre-hook in {ns}/{failed.Pod} failed ({failed.Error}); the volumes are crash-consistent.")
                : new GuestFreezeResult(true, null, preTime, heldTime, [.. preResults.Select(h => new GuestWriterResult($"{h.Pod}/{h.Container}", "ok", null))]);
        var facts = snapshots.Count == 0 ? null : new SnapshotFacts(
            snapshotTime, TimeSpan.Zero, snapshots.Count, [.. snapshots.Values.Select(s => s.StorageClass ?? s.Driver).Distinct(StringComparer.Ordinal)],
            snapshots.Count > 1 && firstSnapshot is { } first && lastSnapshot is { } last ? last - first : null);
        var descriptor = new KubernetesNamespaceDescriptor
        {
            Cluster = cluster,
            Namespace = ns,
            NamespaceUid = space.Metadata.Uid,
            KubernetesVersion = kubernetesVersion,
            ObjectCount = objects.Count,
            Volumes = [.. volumes.Select(v => new KubernetesVolumeRecord(
                v.Claim, v.DataPath, v.Reason, v.StorageClass, v.Driver, v.SnapshotClass,
                snapshots.TryGetValue(v.Claim, out var taken) ? taken.RestoreSize : v.SizeBytes, v.VolumeMode, snapshots.ContainsKey(v.Claim) ? v.Claim : null,
                files.ContainsKey(v.Claim) ? v.Claim : null) with { DataPath = files.ContainsKey(v.Claim) || v.DataPath != DataPaths.Files ? v.DataPath : DataPaths.ObjectsOnly })],
            Hooks = hookRecords,
            Platforms = platforms.Ids,
        };
        var vm = new VmDescriptor
        {
            VmId = ns,
            Name = ns,
            Description = $"Kubernetes namespace {ns} in cluster {cluster}: {objects.Count} objects, {snapshots.Count + files.Count} of {volumes.Count} PVCs with data.",
        };
        return new KubernetesNamespaceBackup(
            flow, channels, mover, run, vm, disks, snapshots, new VmMetadataBlob(KubernetesResources.Format, objects.Content), freeze, facts, descriptor, files, deferred);
    }

    public Task<ChangedExtents> GetChangedExtentsAsync(SnapshotDisk disk, ChangeToken? since, IOperationLog log, CancellationToken cancellationToken) =>
        Task.FromResult(ChangedExtents.Full(
            "Kubernetes volumes have no change tracking: the whole volume is read, the repository stores only the chunks that changed", FullReadReasons.NotTracked));

    public async Task<IDiskSource> OpenReaderAsync(SnapshotDisk disk, DiskReaderOptions options, CancellationToken cancellationToken)
    {
        var snapshot = _snapshots[disk.Key];
        var export = await _flow.ExportAsync(snapshot, _run, _channels!, _mover!, cancellationToken).ConfigureAwait(false);
        return new ExportReader(this, export, snapshot);
    }

    public ChangeToken? GetNewChangeToken(SnapshotDisk disk) => null;

    public async Task<IFileTreeReader> OpenFileTreeAsync(FileTreeSource tree, IPreviousFileTree? previous, CancellationToken cancellationToken)
    {
        var (volume, node) = _files[tree.Key];
        return await ShadowVolume.StartAsync(_flow, Kubernetes.Namespace, volume.Claim, volume.VolumeName!, _run, node, _channels!, _mover!, previous, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task FileTreesReadAsync(IOperationLog log, CancellationToken cancellationToken)
    {
        if (_postHooks is not null)
        {
            await _postHooks.RunAsync(log, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Each snapshot was deleted after its volume was read; anything left goes now.</summary>
    public async Task CommitAsync(IOperationLog log, CancellationToken cancellationToken) => await DeleteRemainingAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        // Post-hooks held back for file reads that never came (the backup failed before): they undo what the pre-hooks did.
        if (_postHooks is not null)
        {
            try
            {
                await _postHooks.RunAsync(_flow.Log, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or HttpOperationException or IOException)
            {
                _flow.Log.Warning($"The post-hooks of {Kubernetes.Namespace} failed: {ex.Message}");
            }
        }

        await DeleteRemainingAsync().ConfigureAwait(false);
    }

    private async Task DeleteRemainingAsync()
    {
        foreach (var snapshot in _snapshots.Values)
        {
            if (_deleted.TryAdd(snapshot.Name, true))
            {
                await DeleteQuietlyAsync(_flow, snapshot).ConfigureAwait(false);
            }
        }
    }

    private async Task SnapshotReadAsync(TakenSnapshot snapshot)
    {
        if (_deleted.TryAdd(snapshot.Name, true))
        {
            await DeleteQuietlyAsync(_flow, snapshot).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a snapshot; a failure is logged and left to the ledger's cleanup.</summary>
    private static async Task DeleteQuietlyAsync(VolumeSnapshotFlow flow, TakenSnapshot snapshot)
    {
        try
        {
            await flow.DeleteSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KubernetesOperationException or HttpRequestException or IOException)
        {
            flow.Log.Warning($"The snapshot {snapshot.Namespace}/{snapshot.Name} of {snapshot.Claim} could not be deleted (retried later): {ex.Message}");
        }
    }

    /// <summary>A volume as a sequential disk: the mover's channel; disposing removes the export, then the snapshot.</summary>
    private sealed class ExportReader(KubernetesNamespaceBackup owner, VolumeExport export, TakenSnapshot snapshot) : ISequentialDiskReader
    {
        public long Capacity => export.Channel.Capacity;

        public string TransportName => export.Channel.TransportName;

        public Task StreamAsync(DiskDataSink sink, CancellationToken cancellationToken) => export.Channel.StreamAsync(sink, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await export.DisposeAsync().ConfigureAwait(false);
            await owner.SnapshotReadAsync(snapshot).ConfigureAwait(false);
        }
    }
}
