using System.Net;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The restore of a namespace's restore point into the same cluster, shaped like a VM restore for the engine: each volume
/// with data is written into a new volume and snapshotted (<see cref="VolumeImport"/>), then the objects are created in
/// Velero's order — the PVCs with data from those snapshots, as ordinary volumes of the CSI driver. The whole namespace, the
/// objects of an app or workload with what they need, or single PVCs (also under new names). Objects that exist are left
/// as they are or replaced; a PVC with data never is. Disposing before <see cref="CompleteAsync"/> removes what was created.
/// </summary>
internal sealed class KubernetesNamespaceRestore : IVmRestoreSession
{
    private const string ProvisionedBy = "pv.kubernetes.io/provisioned-by";

    private readonly IKubernetes _client;
    private readonly ConnectionSettings _settings;
    private readonly VmRestorePlan _plan;
    private readonly KubernetesRestoreOptions _options;
    private readonly KubernetesNamespaceDescriptor _descriptor;
    private readonly IMoverImageSource? _images;
    private readonly VolumeSnapshotFlow _flow;
    private readonly KubernetesApplier _applier;
    private readonly List<JsonObject> _objects;
    private readonly HashSet<string> _selected;
    private readonly string _run;
    private readonly ObjectRewrite _rewrite;
    private readonly Dictionary<string, TargetVolume> _targets = new(StringComparer.Ordinal);
    private readonly List<VolumeImport> _imports = [];
    private readonly List<VolumeFilesImport> _fileImports = [];
    private readonly List<JsonObject> _created = [];
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, IReadOnlyList<(string Kind, string Name)>> _swaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PausedWorkload> _paused = new(StringComparer.Ordinal);
    private bool _namespaceCreated;
    private bool _completed;

    public KubernetesNamespaceRestore(
        IKubernetes client, ConnectionSettings settings, VmRestorePlan plan, IOperationLog log, IArtifactLedger ledger,
        IMoverChannels? channels, IMoverImageSource? images, KubernetesTimings timings)
    {
        _client = client;
        _settings = settings;
        _plan = plan;
        _options = plan.Kubernetes ?? throw new ArgumentException("A namespace restore needs its options.", nameof(plan));
        _descriptor = plan.Manifest.Kubernetes ?? throw new InvalidDataException("The restore point is not one of a Kubernetes namespace.");
        _images = images;
        _run = (plan.SessionId == Guid.Empty ? Guid.NewGuid() : plan.SessionId).ToString("N")[..8];
        _flow = new VolumeSnapshotFlow(client, settings.ClusterName(), log, ledger, timings) { Channels = channels };
        _objects = [.. KubernetesResources.Read(plan.Metadata.Content)];
        _rewrite = new ObjectRewrite(_options.StorageClassMap, _options.OtherCluster, KubernetesPlatformSet.Recorded(_descriptor.Platforms));

        var plurals = KubernetesApplier.BuiltInPlurals();
        foreach (var crd in _objects.Where(o => KubernetesObjects.Kind(o) == "CustomResourceDefinition"))
        {
            if ((string?)crd["spec"]?["names"]?["kind"] is { } kind && (string?)crd["spec"]?["names"]?["plural"] is { } plural)
            {
                plurals[kind] = plural;
            }
        }

        _applier = new KubernetesApplier(client, plurals);
        var claims = _objects.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim").Select(KubernetesObjects.Key);
        _selected = _options.OnlyClaims is { } only
            ? [.. only.Select(c => $"PersistentVolumeClaim/{c}")]
            : _options.Objects is { } chosen
                ? _options.ExactObjects ? chosen.ToHashSet(StringComparer.Ordinal) : KubernetesObjects.Expand(chosen, _objects)
                : [.. _objects.Where(o => KubernetesObjects.Namespace(o) is not null).Select(KubernetesObjects.Key)];
    }

    private IOperationLog Log => _flow.Log;

    private string Target => _options.TargetNamespace;

    private string Source => _descriptor.Namespace;

    /// <summary>The PVCs with data in the restore point (a disk, or a file tree) that this restore brings back.</summary>
    private IEnumerable<KubernetesVolumeRecord> DataVolumes =>
        _descriptor.Volumes.Where(v => v.HasData && _selected.Contains($"PersistentVolumeClaim/{v.Claim}"));

    public bool RestoresDisk(string diskKey) => DataVolumes.Any(v => v.DiskKey == diskKey || v.TreeKey == diskKey);

    public async Task<RestoreValidation> ValidateAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (Target.Length is 0 or > 63 || !Target.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') || Target.StartsWith('-') || Target.EndsWith('-'))
        {
            errors.Add($"'{Target}' is not a namespace name (lower-case letters, digits and '-', at most 63).");
            return new RestoreValidation(errors, warnings);
        }

        if (_options.Objects is { } chosen && chosen.FirstOrDefault(k => !_objects.Any(o => KubernetesObjects.Key(o) == k)) is { } missing)
        {
            errors.Add($"The restore point of {Source} has no object {missing}.");
        }

        if (_selected.Count == 0)
        {
            errors.Add("Nothing to restore: the selection holds no object of the restore point.");
        }

        if (_options.ReplaceClaims && _options.OnlyClaims is null)
        {
            errors.Add("Replacing PVCs is a restore of single PVCs.");
        }

        await CheckStorageAsync(errors, cancellationToken).ConfigureAwait(false);

        foreach (var volume in DataVolumes)
        {
            var name = _options.ClaimName(volume.Claim);
            if (!await ExistsAsync(() => _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, Target, cancellationToken: cancellationToken)).ConfigureAwait(false))
            {
                continue;
            }

            if (!_options.ReplaceClaims)
            {
                errors.Add($"The PVC {Target}/{name} exists in cluster {_settings.ClusterName()}: delete it first, restore into another namespace, restore the PVC under a new name, or replace it.");
                continue;
            }

            var (workloads, problem) = await WorkloadPause.FindAsync(_client, Target, name, cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                errors.Add(problem);
                continue;
            }

            _swaps[name] = workloads;
            Log.Info($"PVC {Target}/{name} is replaced: its current data is deleted" +
                         (workloads.Count > 0 ? $"; {string.Join(", ", workloads.Select(w => $"{w.Kind} {w.Name}"))} is scaled to 0 for the swap and back afterwards." : "."));
        }

        foreach (var volume in _descriptor.Volumes.Where(v => !v.HasData && _selected.Contains($"PersistentVolumeClaim/{v.Claim}")))
        {
            warnings.Add($"PVC {volume.Claim}: only its object is restored; its data is not in the restore point ({volume.Reason}).");
        }

        if (errors.Count == 0 && DataVolumes.Any())
        {
            if (_flow.Channels is null || _images is null)
            {
                errors.Add($"Volume data cannot be restored to cluster {_settings.ClusterName()}: this server has no mover channel.");
            }
            else
            {
                _flow.Mover = await _images.EnsurePushedAsync(line => Log.Info(line), cancellationToken).ConfigureAwait(false);
            }
        }

        if (errors.Count == 0)
        {
            var what = _options.OnlyClaims is { } claims
                ? $"PVC(s) {string.Join(", ", claims.Select(c => _options.ClaimName(c) == c ? c : $"{c} as {_options.ClaimName(c)}"))}"
                : _options.Objects is not null ? $"{_selected.Count} object(s) of the selection and what they need" : "the namespace";
            var from = _options.OtherCluster ? $" (cluster {_descriptor.Cluster})" : string.Empty;
            Log.Info($"Restoring {what} of {Source}{from} into namespace {Target} of cluster {_settings.ClusterName()} ({DataVolumes.Count()} volume(s) with data; existing objects: {(_options.ReplaceExisting ? "replaced" : "left as they are")}).");
        }

        return new RestoreValidation(errors, warnings);
    }

    public async Task<IDiskWriter> CreateDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var volume = DataVolumes.First(v => v.DiskKey == target.DiskKey || v.TreeKey == target.DiskKey);
        var way = _targets.GetValueOrDefault(volume.Claim) ?? throw new InvalidOperationException($"The volume of PVC {volume.Claim} was not validated.");
        if (way.Path == VolumeDataPath.Files)
        {
            var modes = (_objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && KubernetesObjects.Name(o) == volume.Claim)?["spec"]?["accessModes"] as JsonArray)
                ?.Select(m => (string?)m).OfType<string>().ToList() ?? [];
            var files = await VolumeFilesImport.CreateAsync(_flow, _run, target, way.StorageClass, modes, $"{Target}/{_options.ClaimName(volume.Claim)}", cancellationToken)
                .ConfigureAwait(false);
            _fileImports.Add(files);
            return files;
        }

        var import = await VolumeImport.CreateAsync(
            _flow, _run, target, way.StorageClass, way.SnapshotClass, Target, $"{Target}/{_options.ClaimName(volume.Claim)}", cancellationToken).ConfigureAwait(false);
        _imports.Add(import);
        return import;
    }

    public Task<string> CreateEmptyDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken) =>
        throw new NotSupportedException("A namespace has no volumes that were left out of its backup.");

    /// <summary>Creates the objects; the volumes restored before are bound to their PVCs here.</summary>
    public async Task<RestoredVm> CreateVmAsync(IReadOnlyDictionary<string, string> sourceToNewDiskId, CancellationToken cancellationToken)
    {
        var restored = new List<string>();
        var existing = new List<string>();
        var replaced = new List<string>();
        var controlled = 0;
        var platformMade = new Dictionary<string, int>(StringComparer.Ordinal);

        // The namespace first (as in the restore point, renamed when it goes elsewhere).
        if (await _applier.ReadAsync(new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace", ["metadata"] = new JsonObject { ["name"] = Target } }, cancellationToken).ConfigureAwait(false) is null)
        {
            var space = _objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "Namespace") is { } original
                ? KubernetesObjects.Prepare(original, Source, Target, _rewrite)
                : new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace", ["metadata"] = new JsonObject { ["name"] = Target } };
            await CallAsync($"creating the namespace {Target}", () => _applier.CreateAsync(space, cancellationToken)).ConfigureAwait(false);
            _namespaceCreated = true;
            Log.Info($"Namespace {Target} created.");
        }

        // The snapshots of the restored volumes in the namespace, for their PVCs to come from.
        foreach (var import in _imports)
        {
            await import.ExposeAsync(cancellationToken).ConfigureAwait(false);
        }

        // What the selected objects need from the cluster's scope, when missing: their CRDs, and in the same cluster the
        // StorageClasses they keep (another cluster has its own classes: the map names them).
        var kinds = _objects.Where(o => _selected.Contains(KubernetesObjects.Key(o)) && LeftOut(o) is null)
            .Select(KubernetesObjects.Kind).ToHashSet(StringComparer.Ordinal);
        var classes = _options.OtherCluster
            ? []
            : _objects.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && _selected.Contains(KubernetesObjects.Key(o)))
                .Select(o => (string?)o["spec"]?["storageClassName"]).Where(c => !string.IsNullOrEmpty(c)).OfType<string>().Where(c => _rewrite.MapClass(c) == c)
                .ToHashSet(StringComparer.Ordinal); // "" is no class: older backups hold a nameless StorageClass for it, which stays out

        var definitions = new List<JsonObject>();
        var unserved = new Dictionary<string, (string Group, KubernetesPlatformProfile Platform)>(StringComparer.Ordinal);
        var notServed = new List<JsonObject>();
        foreach (var definition in _objects.Where(o =>
                     (KubernetesObjects.Kind(o) == "CustomResourceDefinition" && kinds.Contains((string?)o["spec"]?["names"]?["kind"] ?? string.Empty))
                     || (KubernetesObjects.Kind(o) == "StorageClass" && classes.Contains(KubernetesObjects.Name(o)))))
        {
            var prepared = KubernetesObjects.Prepare(definition, Source, Target, _rewrite);
            var missing = await _applier.ReadAsync(definition, cancellationToken).ConfigureAwait(false) is null;

            // A platform's CRDs come with the platform, never from a backup: without them here, their objects stay out.
            if (KubernetesObjects.Kind(definition) == "CustomResourceDefinition" && (string?)definition["spec"]?["group"] is { } group
                && _rewrite.Platforms.CrdGroupOf(group) is { } platform)
            {
                if (missing)
                {
                    unserved[(string)definition["spec"]!["names"]!["kind"]!] = (group, platform);
                }

                continue;
            }

            if (missing && await TryCreateAsync(prepared, restored, cancellationToken).ConfigureAwait(false)
                && KubernetesObjects.Kind(definition) == "CustomResourceDefinition")
            {
                definitions.Add(prepared);
            }
        }

        await WaitEstablishedAsync(definitions, cancellationToken).ConfigureAwait(false);

        // The namespaced objects in order; PVCs with data take their restored volumes.
        var volumes = _imports.ToDictionary(i => i.DiskId, StringComparer.Ordinal);
        var fileVolumes = _fileImports.ToDictionary(i => i.DiskId, StringComparer.Ordinal);
        foreach (var item in _objects
                     .Where(o => KubernetesObjects.Namespace(o) is not null && _selected.Contains(KubernetesObjects.Key(o)))
                     .OrderBy(o => KubernetesObjects.Rank(KubernetesObjects.Kind(o))).ThenBy(KubernetesObjects.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (KubernetesObjects.IsControlled(item))
            {
                controlled++;
                continue;
            }

            if (KubernetesObjects.IsClusterMade(item))
            {
                continue;
            }

            if (LeftOut(item) is { } made)
            {
                platformMade[made.Profile.Name] = platformMade.GetValueOrDefault(made.Profile.Name) + 1;
                continue;
            }

            if (unserved.ContainsKey(KubernetesObjects.Kind(item)))
            {
                notServed.Add(item);
                continue;
            }

            if (KubernetesObjects.Kind(item) == "PersistentVolumeClaim")
            {
                await RestoreClaimAsync(item, sourceToNewDiskId, volumes, fileVolumes, restored, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var prepared = KubernetesObjects.Prepare(item, Source, Target, _rewrite);
            var live = await ReadQuietlyAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                await TryCreateAsync(prepared, restored, cancellationToken).ConfigureAwait(false);
            }
            else if (_options.ReplaceExisting && (string?)live["metadata"]?["resourceVersion"] is { } version)
            {
                try
                {
                    await _applier.ReplaceAsync(prepared, version, cancellationToken).ConfigureAwait(false);
                    replaced.Add(KubernetesObjects.Key(item));
                }
                catch (Exception ex) when (ex is HttpOperationException or KubernetesOperationException)
                {
                    Warn($"{KubernetesObjects.Key(item)} could not be replaced: {Problem(ex)}");
                }
            }
            else
            {
                existing.Add(KubernetesObjects.Key(item));
            }
        }

        foreach (var group in notServed.GroupBy(o => unserved[KubernetesObjects.Kind(o)].Group))
        {
            var platform = unserved[KubernetesObjects.Kind(group.First())].Platform.Name;
            Warn($"{group.Count()} object(s) are not restored ({string.Join(", ", group.Select(KubernetesObjects.Key))}): their API {group.Key} comes with {platform}, " +
                 $"which cluster {_settings.ClusterName()} does not have.");
        }

        await WaitBoundAsync(cancellationToken).ConfigureAwait(false);
        await ResumeAsync(cancellationToken).ConfigureAwait(false);
        Log.Info($"{restored.Count} object(s) restored{(restored.Count > 0 ? $" ({Summary(restored)})" : string.Empty)}" +
                 (replaced.Count > 0 ? $"; {replaced.Count} replaced ({Summary(replaced)})" : string.Empty) +
                 (existing.Count > 0 ? $"; {existing.Count} existed and were left as they are ({Summary(existing)})" : string.Empty) +
                 (controlled > 0 ? $"; {controlled} made by their controllers (they make them again)" : string.Empty) +
                 (platformMade.Count > 0
                     ? $"; {string.Join(", ", platformMade.Select(kv => $"{kv.Value} made by {kv.Key}"))} left out (the platform makes them again)"
                     : string.Empty) + ".");
        return new RestoredVm(Target, Target, Warnings: _warnings);
    }

    /// <summary>
    /// An object the source cluster's platform made, left out: in another cluster all of them (its platform makes its own
    /// there), in the same cluster those it makes in every namespace.
    /// </summary>
    private PlatformMade? LeftOut(JsonObject o) => _rewrite.Platforms.MadeBy(o) is { } made && (_options.OtherCluster || made.Always) ? made : null;

    public Task<RestoredVm> CompleteAsync(CancellationToken cancellationToken)
    {
        _completed = true;
        return Task.FromResult(new RestoredVm(Target, Target, Warnings: _warnings));
    }

    /// <summary>A restore that did not complete takes back what it created: a namespace it created with everything in it, else each object.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var import in _imports)
        {
            await import.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var files in _fileImports)
        {
            await files.DisposeAsync().ConfigureAwait(false);
        }

        if (!_completed)
        {
            using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
            await ResumeAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                if (_namespaceCreated)
                {
                    await _applier.DeleteAsync(new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace", ["metadata"] = new JsonObject { ["name"] = Target } }, timeout.Token)
                        .ConfigureAwait(false);
                    Log.Warning($"The restore did not complete: the namespace {Target} it created is deleted again, with everything in it.");
                }
                else
                {
                    foreach (var created in Enumerable.Reverse(_created))
                    {
                        await _applier.DeleteAsync(created, timeout.Token).ConfigureAwait(false);
                    }

                    if (_created.Count > 0)
                    {
                        Log.Warning($"The restore did not complete: the {_created.Count} object(s) it created in {Target} are deleted again.");
                    }
                }
            }
            catch (Exception ex) when (ex is HttpOperationException or KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                Log.Warning($"Taking back the incomplete restore in {Target} failed: {ex.Message}");
            }
        }

        // Volumes written as files that never reached their PVC (a completed restore handed them over).
        foreach (var files in _fileImports)
        {
            using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
            try
            {
                await files.ReleaseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                Log.Warning($"A restored volume of {files.Description} could not be removed (retried later): {ex.Message}");
            }
        }

        // The snapshots and work volumes of the restored volumes (a completed restore released them already).
        foreach (var import in _imports)
        {
            using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
            try
            {
                await import.ReleaseAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or OperationCanceledException or HttpRequestException)
            {
                Log.Warning($"The snapshots of a restored volume could not all be deleted (retried later): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A PVC: with data, from the snapshot of its restored volume; without data, created on its StorageClass (empty), or
    /// bound again to its static PV (e.g. an NFS export).
    /// </summary>
    private async Task RestoreClaimAsync(
        JsonObject claim, IReadOnlyDictionary<string, string> sourceToNewDiskId, Dictionary<string, VolumeImport> volumes, Dictionary<string, VolumeFilesImport> fileVolumes,
        List<string> restored, CancellationToken cancellationToken)
    {
        var originalName = KubernetesObjects.Name(claim);
        var name = _options.ClaimName(originalName);
        var prepared = KubernetesObjects.Prepare(claim, Source, Target, _rewrite);
        prepared["metadata"]!["name"] = name;
        var spec = (JsonObject)prepared["spec"]!;
        var backupVolume = (string?)claim["spec"]?["volumeName"] is { } pvName ? _objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "PersistentVolume" && KubernetesObjects.Name(o) == pvName) : null;

        if (sourceToNewDiskId.TryGetValue($"{Source}/{originalName}", out var fileDiskId) && fileVolumes.TryGetValue(fileDiskId, out var files))
        {
            // The volume written as files: handed over to the PVC, which names it as its volume.
            if (_swaps.TryGetValue(name, out var swapped))
            {
                await SwapOutAsync(name, swapped, cancellationToken).ConfigureAwait(false);
            }

            spec["volumeName"] = await files.HandOverAsync(Target, name, cancellationToken).ConfigureAwait(false);
            spec["storageClassName"] = files.StorageClass;
            spec["accessModes"] = new JsonArray([.. files.AccessModes.Select(m => (JsonNode)m)]); // the volume's: fewer when the class could not share it
            await CallAsync($"creating the PVC {Target}/{name}", () => _applier.CreateAsync(prepared, cancellationToken)).ConfigureAwait(false);
            _created.Add(prepared);
            restored.Add($"PersistentVolumeClaim/{name}");
            await files.CompleteHandOverAsync(Target, name, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (sourceToNewDiskId.TryGetValue($"{Source}/{originalName}", out var diskId) && volumes.TryGetValue(diskId, out var import) && import.Snapshot is { } snapshot)
        {
            // A Filesystem PVC from the snapshot of the restored volume: an ordinary volume of the driver, a copy of it.
            spec.Remove("volumeName");
            spec["dataSource"] = new JsonObject { ["apiGroup"] = VolumeSnapshotFlow.Group, ["kind"] = "VolumeSnapshot", ["name"] = snapshot };
            if (import.StorageClass is { } storageClass)
            {
                spec["storageClassName"] = storageClass; // the class the volume was written to (a choice for this claim, or the default)
            }
            if (await WaitsForConsumerAsync((string?)spec["storageClassName"], cancellationToken).ConfigureAwait(false) && import.Node is { } node)
            {
                // Provisioned now, not when a pod comes: the snapshots go once the copy is made.
                prepared["metadata"]!["annotations"] ??= new JsonObject();
                prepared["metadata"]!["annotations"]!["volume.kubernetes.io/selected-node"] = node;
            }

            if (_swaps.TryGetValue(name, out var users))
            {
                await SwapOutAsync(name, users, cancellationToken).ConfigureAwait(false);
            }

            await CallAsync($"creating the PVC {Target}/{name}", () => _applier.CreateAsync(prepared, cancellationToken)).ConfigureAwait(false);
            _created.Add(prepared);
            restored.Add($"PersistentVolumeClaim/{name}");
            Log.Info($"PVC {Target}/{name}: created from the snapshot of its restored volume ({Target}/{snapshot}).");
            return;
        }

        if (await ReadQuietlyAsync(prepared, cancellationToken).ConfigureAwait(false) is not null)
        {
            Warn($"PVC {Target}/{name} exists and is left as it is (its data is not in the restore point).");
            return;
        }

        // Objects only: a static PV (no provisioner) that is gone comes back and binds; otherwise the PVC gets a new volume.
        // Not in another cluster: the PV names storage of the old one (e.g. an NFS export), so both would share the data.
        spec.Remove("volumeName");
        var staticVolume = backupVolume is not null && (string?)backupVolume["metadata"]?["annotations"]?[ProvisionedBy] is null;
        if (staticVolume && _options.OtherCluster)
        {
            Warn($"PVC {Target}/{name}: its static PV {KubernetesObjects.Name(backupVolume!)} is not created in another cluster (it names storage of cluster {_descriptor.Cluster}); the PVC gets a new, empty volume.");
        }
        else if (staticVolume)
        {
            var live = await _applier.ReadAsync(backupVolume!, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                var pv = KubernetesObjects.Prepare(backupVolume!, Source, Target, _rewrite);
                pv["spec"]!["claimRef"] = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["namespace"] = Target, ["name"] = name };
                if (await TryCreateAsync(pv, restored, cancellationToken).ConfigureAwait(false))
                {
                    spec["volumeName"] = KubernetesObjects.Name(pv);
                }
            }
            else if ((string?)live["status"]?["phase"] == "Available")
            {
                spec["volumeName"] = KubernetesObjects.Name(backupVolume!);
            }
        }

        if (spec["volumeName"] is null && !(staticVolume && _options.OtherCluster))
        {
            Warn($"PVC {Target}/{name}: created without its data (not in the restore point); it gets a new, empty volume.");
        }

        await TryCreateAsync(prepared, restored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the workloads using the PVC (once each), then deletes the PVC and waits until it is gone.</summary>
    private async Task SwapOutAsync(string claim, IReadOnlyList<(string Kind, string Name)> users, CancellationToken cancellationToken)
    {
        var toPause = users.Where(w => !_paused.ContainsKey($"{w.Kind}/{w.Name}")).ToList();
        foreach (var workload in await WorkloadPause.PauseAsync(_flow, Target, claim, toPause, cancellationToken).ConfigureAwait(false))
        {
            _paused[workload.Key] = workload;
        }

        var existing = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["metadata"] = new JsonObject { ["name"] = claim, ["namespace"] = Target } };
        await CallAsync($"deleting the PVC {Target}/{claim}", () => _applier.DeleteAsync(existing, cancellationToken)).ConfigureAwait(false);
        var started = DateTimeOffset.UtcNow;
        while (await ReadQuietlyAsync(existing, cancellationToken).ConfigureAwait(false) is not null)
        {
            if (DateTimeOffset.UtcNow - started > _flow.Timings.Deleted)
            {
                throw new KubernetesOperationException($"The PVC {Target}/{claim} is still there {_flow.Timings.Deleted.TotalMinutes:0} minutes after its deletion (cluster {_settings.ClusterName()}).");
            }

            await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
        }

        Log.Info($"PVC {Target}/{claim} with its former data deleted.");
    }

    /// <summary>Scales the stopped workloads back (also when the restore did not complete).</summary>
    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var workload in _paused.Values.ToList())
        {
            try
            {
                await WorkloadPause.ResumeAsync(_flow, workload, cancellationToken).ConfigureAwait(false);
                _paused.Remove(workload.Key);
            }
            catch (KubernetesOperationException ex)
            {
                Warn($"{workload.Kind} {workload.Namespace}/{workload.Name} could not be scaled back to {workload.Replicas} (retried later): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Waits until the PVCs made from restored volumes are bound (the storage copies each volume from its snapshot), then
    /// removes the snapshots and work volumes. One that is not bound in time is a warning; its snapshots stay for it.
    /// </summary>
    private async Task WaitBoundAsync(CancellationToken cancellationToken)
    {
        var claims = _created.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && o["spec"]?["dataSource"] is not null).ToList();
        var started = DateTimeOffset.UtcNow;
        var reported = started;
        foreach (var claim in claims)
        {
            var snapshot = (string?)claim["spec"]?["dataSource"]?["name"];
            while (true)
            {
                var live = await ReadQuietlyAsync(claim, cancellationToken).ConfigureAwait(false);
                if ((string?)live?["status"]?["phase"] == "Bound")
                {
                    if (_imports.FirstOrDefault(i => i.Snapshot == snapshot) is { } import)
                    {
                        await import.ReleaseAsync(cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                var waited = DateTimeOffset.UtcNow - started;
                if (waited > _flow.Timings.RestoreCopy)
                {
                    Warn($"PVC {Target}/{KubernetesObjects.Name(claim)} is not bound after {waited.TotalMinutes:0} minutes ({(string?)live?["status"]?["phase"] ?? "gone"}); " +
                         $"the snapshot {Target}/{snapshot} it is made from stays until it is.");
                    break;
                }

                if (DateTimeOffset.UtcNow - reported >= _flow.Timings.Progress)
                {
                    reported = DateTimeOffset.UtcNow;
                    Log.Info($"Waiting for PVC {Target}/{KubernetesObjects.Name(claim)}: the storage copies the restored volume ({waited.TotalMinutes:0} min).");
                }

                await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The StorageClass of each restored PVC in the target (mapped, or chosen for its disk) and, for one with data, the way
    /// its data goes in. A class the target lacks is an error naming the classes it has; in the same cluster a missing class
    /// the restore point holds is created from it (for PVCs without data).
    /// </summary>
    private async Task CheckStorageAsync(List<string> errors, CancellationToken cancellationToken)
    {
        KubernetesTargetStorage storage;
        try
        {
            storage = await KubernetesTargetStorage.ReadAsync(_client, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            errors.Add($"The StorageClasses of cluster {_settings.ClusterName()} could not be read: {VolumeSnapshotFlow.Problem(ex)}");
            return;
        }

        var available = storage.ClassNames.Count > 0 ? string.Join(", ", storage.ClassNames) : "none";
        foreach (var claim in _objects.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && _selected.Contains(KubernetesObjects.Key(o))))
        {
            var name = KubernetesObjects.Name(claim);
            var source = (string?)claim["spec"]?["storageClassName"];
            var volume = DataVolumes.FirstOrDefault(v => v.Claim == name);
            var chosen = (volume?.DiskKey ?? volume?.TreeKey) is { } key && _plan.DiskStorage.GetValueOrDefault(key) is { Length: > 0 } picked ? picked : null;
            var target = chosen ?? _rewrite.MapClass(source);
            if (volume is null)
            {
                var fromBackup = !_options.OtherCluster && target == source
                    && _objects.Any(o => KubernetesObjects.Kind(o) == "StorageClass" && KubernetesObjects.Name(o) == target);
                if (!string.IsNullOrEmpty(target) && !storage.Has(target) && !fromBackup)
                {
                    errors.Add($"PVC {name}: the StorageClass {target} does not exist in cluster {_settings.ClusterName()} (it has {available}); map {source} to one of them.");
                }

                continue;
            }

            if (storage.Decide(target) is not { } way)
            {
                errors.Add(string.IsNullOrEmpty(target)
                    ? $"PVC {name} has no StorageClass, and cluster {_settings.ClusterName()} has no default class (it has {available}): map it to one of them."
                    : $"PVC {name}: the StorageClass {target} does not exist in cluster {_settings.ClusterName()} (it has {available}); map {source} to one of them.");
                continue;
            }

            // A volume backed up file by file goes back as files, into whatever class (a snapshot class does not matter).
            if (volume.TreeKey is not null)
            {
                way = way with { Path = VolumeDataPath.Files, SnapshotClass = null, Reason = "it was backed up file by file" };
            }

            _targets[name] = way;
            Log.Info(volume.TreeKey is not null
                ? $"PVC {name}: its files go into a new volume of the StorageClass {way.StorageClass} of cluster {_settings.ClusterName()}, from its file tree with owners, modes and times; hard links become copies, sparse files are written in full."
                : way.Path == VolumeDataPath.Block
                ? $"PVC {name}: its data goes into the StorageClass {way.StorageClass} of cluster {_settings.ClusterName()} as a block volume ({way.Reason})."
                : $"PVC {name}: its data goes into the StorageClass {way.StorageClass} of cluster {_settings.ClusterName()} as files ({way.Reason}): read from the file system " +
                  "of its image (ext or XFS) with owners, modes and times; hard links become copies, sparse files are written in full, extended attributes and ACLs stay behind.");
        }
    }

    /// <summary>Waits until the CRDs the restore created are established, so that their objects can be created.</summary>
    private async Task WaitEstablishedAsync(IReadOnlyList<JsonObject> definitions, CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            var started = DateTimeOffset.UtcNow;
            while (true)
            {
                var live = await ReadQuietlyAsync(definition, cancellationToken).ConfigureAwait(false);
                if ((live?["status"]?["conditions"] as JsonArray)?.OfType<JsonObject>()
                    .Any(c => (string?)c["type"] == "Established" && (string?)c["status"] == "True") == true)
                {
                    break;
                }

                if (DateTimeOffset.UtcNow - started > _flow.Timings.CrdEstablished)
                {
                    Warn($"The CRD {KubernetesObjects.Name(definition)} is not established in cluster {_settings.ClusterName()} after {_flow.Timings.CrdEstablished.TotalSeconds:0} s; its objects may fail.");
                    break;
                }

                await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether volumes of the StorageClass are provisioned only for a pod (WaitForFirstConsumer).</summary>
    private async Task<bool> WaitsForConsumerAsync(string? storageClass, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(storageClass))
        {
            return false;
        }

        var live = await ReadQuietlyAsync(new JsonObject { ["apiVersion"] = "storage.k8s.io/v1", ["kind"] = "StorageClass", ["metadata"] = new JsonObject { ["name"] = storageClass } }, cancellationToken)
            .ConfigureAwait(false);
        return (string?)live?["volumeBindingMode"] == "WaitForFirstConsumer";
    }

    private async Task<bool> TryCreateAsync(JsonObject prepared, List<string> restored, CancellationToken cancellationToken)
    {
        try
        {
            await _applier.CreateAsync(prepared, cancellationToken).ConfigureAwait(false);
            _created.Add(prepared);
            restored.Add(KubernetesObjects.Key(prepared));
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            return false; // Made meanwhile (by a controller of an object restored before).
        }
        catch (Exception ex) when (ex is HttpOperationException or KubernetesOperationException)
        {
            Warn($"{KubernetesObjects.Key(prepared)} could not be restored: {Problem(ex)}");
            return false;
        }
    }

    private async Task<JsonObject?> ReadQuietlyAsync(JsonObject o, CancellationToken cancellationToken)
    {
        try
        {
            return await _applier.ReadAsync(o, cancellationToken).ConfigureAwait(false);
        }
        catch (KubernetesOperationException)
        {
            return null; // An unknown kind: its create reports it.
        }
    }

    private void Warn(string message)
    {
        _warnings.Add(message);
        Log.Warning(message);
    }

    private Task<T> CallAsync<T>(string what, Func<Task<T>> call) => _flow.CallAsync(what, call);

    private Task CallAsync(string what, Func<Task> call) => _flow.CallAsync(what, async () =>
    {
        await call().ConfigureAwait(false);
        return true;
    });

    private static async Task<bool> ExistsAsync<T>(Func<Task<T>> read) => await VolumeSnapshotFlow.ExistsAsync(read).ConfigureAwait(false);

    private static string Problem(Exception ex) => ex is HttpOperationException http ? VolumeSnapshotFlow.Problem(http) : ex.Message;

    private static string Summary(IEnumerable<string> keys) =>
        string.Join(", ", keys.GroupBy(k => k[..k.IndexOf('/', StringComparison.Ordinal)]).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
}
