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
/// with data is written into a new volume (<see cref="VolumeImport"/>), then the objects are created in Velero's order —
/// the PVCs with data bound to their new volumes through static Filesystem PVs (spike S3). The whole namespace, the
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
    private readonly List<VolumeImport> _imports = [];
    private readonly List<JsonObject> _created = [];
    private readonly List<string> _warnings = [];
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
        _flow = new VolumeSnapshotFlow(client, settings.Address, log, ledger, timings) { Channels = channels };
        _objects = [.. KubernetesResources.Read(plan.Metadata.Content)];

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
                ? KubernetesObjects.Expand(chosen, _objects)
                : [.. _objects.Where(o => KubernetesObjects.Namespace(o) is not null).Select(KubernetesObjects.Key)];
    }

    private IOperationLog Log => _flow.Log;

    private string Target => _options.TargetNamespace;

    private string Source => _descriptor.Namespace;

    /// <summary>The PVCs with data in the restore point that this restore brings back (claim names).</summary>
    private IEnumerable<KubernetesVolumeRecord> DataVolumes =>
        _descriptor.Volumes.Where(v => v.DiskKey is not null && _selected.Contains($"PersistentVolumeClaim/{v.Claim}"));

    public bool RestoresDisk(string diskKey) => DataVolumes.Any(v => v.DiskKey == diskKey);

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

        foreach (var volume in DataVolumes)
        {
            var name = _options.ClaimName(volume.Claim);
            if (await ExistsAsync(() => _client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(name, Target, cancellationToken: cancellationToken)).ConfigureAwait(false))
            {
                errors.Add($"The PVC {Target}/{name} exists in cluster {_settings.Address}: delete it first, restore into another namespace, or restore the PVC under a new name.");
            }
        }

        foreach (var volume in _descriptor.Volumes.Where(v => v.DiskKey is null && _selected.Contains($"PersistentVolumeClaim/{v.Claim}")))
        {
            warnings.Add($"PVC {volume.Claim}: only its object is restored; its data is not in the restore point ({volume.Reason}).");
        }

        if (errors.Count == 0 && DataVolumes.Any())
        {
            if (_flow.Channels is null || _images is null)
            {
                errors.Add($"Volume data cannot be restored to cluster {_settings.Address}: this server has no mover channel.");
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
            Log.Info($"Restoring {what} of {Source} into namespace {Target} of cluster {_settings.Address} ({DataVolumes.Count()} volume(s) with data; existing objects: {(_options.ReplaceExisting ? "replaced" : "left as they are")}).");
        }

        return new RestoreValidation(errors, warnings);
    }

    public async Task<IDiskWriter> CreateDiskAsync(RestoreDiskTarget target, CancellationToken cancellationToken)
    {
        var volume = DataVolumes.First(v => v.DiskKey == target.DiskKey);
        var storageClass = string.IsNullOrEmpty(target.StorageId) ? volume.StorageClass : target.StorageId;
        var import = await VolumeImport.CreateAsync(_flow, _run, target, storageClass, $"{Target}/{_options.ClaimName(volume.Claim)}", cancellationToken).ConfigureAwait(false);
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

        // The namespace first (as in the restore point, renamed when it goes elsewhere).
        if (await _applier.ReadAsync(new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace", ["metadata"] = new JsonObject { ["name"] = Target } }, cancellationToken).ConfigureAwait(false) is null)
        {
            var space = _objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "Namespace") is { } original
                ? KubernetesObjects.Prepare(original, Source, Target)
                : new JsonObject { ["apiVersion"] = "v1", ["kind"] = "Namespace", ["metadata"] = new JsonObject { ["name"] = Target } };
            await CallAsync($"creating the namespace {Target}", () => _applier.CreateAsync(space, cancellationToken)).ConfigureAwait(false);
            _namespaceCreated = true;
            Log.Info($"Namespace {Target} created.");
        }

        // What the selected objects need from the cluster's scope: their CRDs and StorageClasses, when missing.
        var kinds = _objects.Where(o => _selected.Contains(KubernetesObjects.Key(o))).Select(KubernetesObjects.Kind).ToHashSet(StringComparer.Ordinal);
        var classes = _objects.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && _selected.Contains(KubernetesObjects.Key(o)))
            .Select(o => (string?)o["spec"]?["storageClassName"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var definition in _objects.Where(o =>
                     (KubernetesObjects.Kind(o) == "CustomResourceDefinition" && kinds.Contains((string?)o["spec"]?["names"]?["kind"] ?? string.Empty))
                     || (KubernetesObjects.Kind(o) == "StorageClass" && classes.Contains(KubernetesObjects.Name(o)))))
        {
            if (await _applier.ReadAsync(definition, cancellationToken).ConfigureAwait(false) is null)
            {
                await TryCreateAsync(KubernetesObjects.Prepare(definition, Source, Target), restored, cancellationToken).ConfigureAwait(false);
            }
        }

        // The namespaced objects in order; PVCs with data take their restored volumes.
        var volumes = _imports.ToDictionary(i => i.DiskId, StringComparer.Ordinal);
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

            if (KubernetesObjects.Kind(item) == "PersistentVolumeClaim")
            {
                await RestoreClaimAsync(item, sourceToNewDiskId, volumes, restored, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var prepared = KubernetesObjects.Prepare(item, Source, Target);
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

        await WaitBoundAsync(cancellationToken).ConfigureAwait(false);
        Log.Info($"{restored.Count} object(s) restored{(restored.Count > 0 ? $" ({Summary(restored)})" : string.Empty)}" +
                 (replaced.Count > 0 ? $"; {replaced.Count} replaced ({Summary(replaced)})" : string.Empty) +
                 (existing.Count > 0 ? $"; {existing.Count} existed and were left as they are ({Summary(existing)})" : string.Empty) +
                 (controlled > 0 ? $"; {controlled} made by their controllers (they make them again)" : string.Empty) + ".");
        return new RestoredVm(Target, Target, Warnings: _warnings);
    }

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

        if (!_completed)
        {
            using var timeout = new CancellationTokenSource(_flow.Timings.Deleted);
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
                    // PVCs before their PVs: a PV whose PVC is gone is deleted with its volume by the cluster (Delete).
                    foreach (var created in Enumerable.Reverse(_created).Where(o => KubernetesObjects.Kind(o) != "PersistentVolume"))
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

        // Restored volumes that never reached their PVC: the cluster deletes them (their ledger entries say how).
        foreach (var import in _imports.Where(i => i.RetainedLedgerId is not null))
        {
            var bound = _completed || _created.Any(o => KubernetesObjects.Kind(o) == "PersistentVolume" && (string?)o["metadata"]?["annotations"]?["backdafuckup.io/restored-from"] == import.RetainedVolume);
            if (bound)
            {
                await _flow.Ledger.UpdateAsync(import.RetainedLedgerId!.Value, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await _flow.DeleteAsync(VolumeImport.RetainedVolumeKind, null, import.RetainedVolume!, CancellationToken.None).ConfigureAwait(false);
                    await _flow.Ledger.UpdateAsync(import.RetainedLedgerId!.Value, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (KubernetesOperationException ex)
                {
                    Log.Warning($"The restored volume {import.RetainedVolume} could not be released (retried later): {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// A PVC: with data, its new volume becomes a static Filesystem PV for it (the work PV object goes, the volume stays);
    /// without data, it is created on its StorageClass (empty), or bound again to its static PV (e.g. an NFS export).
    /// </summary>
    private async Task RestoreClaimAsync(
        JsonObject claim, IReadOnlyDictionary<string, string> sourceToNewDiskId, Dictionary<string, VolumeImport> volumes, List<string> restored, CancellationToken cancellationToken)
    {
        var originalName = KubernetesObjects.Name(claim);
        var name = _options.ClaimName(originalName);
        var prepared = KubernetesObjects.Prepare(claim, Source, Target);
        prepared["metadata"]!["name"] = name;
        var spec = (JsonObject)prepared["spec"]!;
        var backupVolume = (string?)claim["spec"]?["volumeName"] is { } pvName ? _objects.FirstOrDefault(o => KubernetesObjects.Kind(o) == "PersistentVolume" && KubernetesObjects.Name(o) == pvName) : null;

        if (sourceToNewDiskId.TryGetValue($"{Source}/{originalName}", out var retained) && volumes.TryGetValue(retained, out _))
        {
            var work = await CallAsync($"reading the restored volume {retained}", () => _client.CoreV1.ReadPersistentVolumeAsync(retained, cancellationToken: cancellationToken)).ConfigureAwait(false);
            var csi = work.Spec.Csi ?? throw new KubernetesOperationException($"The restored volume {retained} is not a CSI volume.");
            var pv = StaticVolume(backupVolume, claim, name, retained, csi.Driver, csi.VolumeHandle, work.Spec.Capacity["storage"].ToString());

            // The work PV object goes (Retain: the volume stays), the static Filesystem PV takes the volume for the PVC.
            await CallAsync($"removing the work PV object {retained}", () => _client.CoreV1.DeletePersistentVolumeAsync(retained, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await CallAsync($"creating the PV {KubernetesObjects.Name(pv)} for {Target}/{name}", () => _applier.CreateAsync(pv, cancellationToken)).ConfigureAwait(false);
            _created.Add(pv);
            spec["volumeName"] = KubernetesObjects.Name(pv);
            await CallAsync($"creating the PVC {Target}/{name}", () => _applier.CreateAsync(prepared, cancellationToken)).ConfigureAwait(false);
            _created.Add(prepared);
            restored.Add($"PersistentVolumeClaim/{name}");
            Log.Info($"PVC {Target}/{name}: bound to its restored volume (PV {KubernetesObjects.Name(pv)}, {csi.Driver} {csi.VolumeHandle}).");
            return;
        }

        if (await ReadQuietlyAsync(prepared, cancellationToken).ConfigureAwait(false) is not null)
        {
            Warn($"PVC {Target}/{name} exists and is left as it is (its data is not in the restore point).");
            return;
        }

        // Objects only: a static PV (no provisioner) that is gone comes back and binds; otherwise the PVC gets a new volume.
        spec.Remove("volumeName");
        if (backupVolume is not null && (string?)backupVolume["metadata"]?["annotations"]?[ProvisionedBy] is null)
        {
            var live = await _applier.ReadAsync(backupVolume, cancellationToken).ConfigureAwait(false);
            if (live is null)
            {
                var pv = KubernetesObjects.Prepare(backupVolume, Source, Target);
                pv["spec"]!["claimRef"] = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["namespace"] = Target, ["name"] = name };
                if (await TryCreateAsync(pv, restored, cancellationToken).ConfigureAwait(false))
                {
                    spec["volumeName"] = KubernetesObjects.Name(pv);
                }
            }
            else if ((string?)live["status"]?["phase"] == "Available")
            {
                spec["volumeName"] = KubernetesObjects.Name(backupVolume);
            }
        }

        if (spec["volumeName"] is null)
        {
            Warn($"PVC {Target}/{name}: created without its data (not in the restore point); it gets a new, empty volume.");
        }

        await TryCreateAsync(prepared, restored, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The PV for a restored volume: the driver's attributes and the PV's settings from the restore point, the new volume, reserved for its PVC.</summary>
    private JsonObject StaticVolume(JsonObject? backupVolume, JsonObject claim, string claimName, string retained, string driver, string handle, string capacity)
    {
        var name = $"pvc-bdfu-{_run}-{VolumeSnapshotFlow.Hash($"{Target}/{claimName}")}";
        var original = backupVolume?["spec"] as JsonObject;
        var csi = new JsonObject
        {
            ["driver"] = driver,
            ["volumeHandle"] = handle,
            ["fsType"] = (string?)original?["csi"]?["fsType"] ?? "ext4",
        };
        if (original?["csi"]?["volumeAttributes"] is JsonObject attributes)
        {
            csi["volumeAttributes"] = attributes.DeepClone();
        }

        var spec = new JsonObject
        {
            ["capacity"] = new JsonObject { ["storage"] = capacity },
            ["accessModes"] = claim["spec"]?["accessModes"]?.DeepClone() ?? new JsonArray("ReadWriteOnce"),
            ["persistentVolumeReclaimPolicy"] = (string?)original?["persistentVolumeReclaimPolicy"] ?? "Delete",
            ["volumeMode"] = (string?)claim["spec"]?["volumeMode"] ?? "Filesystem",
            ["csi"] = csi,
            ["claimRef"] = new JsonObject { ["apiVersion"] = "v1", ["kind"] = "PersistentVolumeClaim", ["namespace"] = Target, ["name"] = claimName },
        };
        if ((string?)claim["spec"]?["storageClassName"] is { } storageClass)
        {
            spec["storageClassName"] = storageClass;
        }

        foreach (var field in new[] { "mountOptions", "nodeAffinity" })
        {
            if (original?[field] is { } value)
            {
                spec[field] = value.DeepClone();
            }
        }

        return new JsonObject
        {
            ["apiVersion"] = "v1",
            ["kind"] = "PersistentVolume",
            ["metadata"] = new JsonObject
            {
                ["name"] = name,
                ["labels"] = new JsonObject { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup-restore" },

                // The driver's provisioner deletes the volume with its PV (Delete) only when it is named as the provisioner.
                ["annotations"] = new JsonObject { [ProvisionedBy] = driver, ["backdafuckup.io/restored-from"] = retained },
            },
            ["spec"] = spec,
        };
    }

    /// <summary>Waits briefly for the PVCs with data to be bound; one that is not is a warning (the pods using it wait).</summary>
    private async Task WaitBoundAsync(CancellationToken cancellationToken)
    {
        var claims = _created.Where(o => KubernetesObjects.Kind(o) == "PersistentVolumeClaim" && o["spec"]?["volumeName"] is not null).ToList();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(2).Ticks, _flow.Timings.Deleted.Ticks));
        foreach (var claim in claims)
        {
            while (true)
            {
                var live = await ReadQuietlyAsync(claim, cancellationToken).ConfigureAwait(false);
                if ((string?)live?["status"]?["phase"] == "Bound")
                {
                    break;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    Warn($"PVC {Target}/{KubernetesObjects.Name(claim)} is not bound yet ({(string?)live?["status"]?["phase"] ?? "gone"}): check the PV {(string?)claim["spec"]?["volumeName"]}.");
                    break;
                }

                await Task.Delay(_flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
            }
        }
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
