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
    /// <summary>Pod states that do not get better by waiting.</summary>
    private static readonly HashSet<string> FatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "ImagePullBackOff", "ErrImageNeverPull", "InvalidImageName", "CreateContainerConfigError", "CreateContainerError",
    };

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

        if (mover.DockerConfigJson is { } dockerConfig && mover.PullSecretName is { } pullSecret)
        {
            await EnsurePullSecretAsync(pullSecret, dockerConfig, cancellationToken).ConfigureAwait(false);
        }

        _channel = await channels.OpenBackupAsync($"{source} (cluster {flow.Cluster})", snapshot.RestoreSize, cancellationToken, flow.Timings.MoverConnect).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        var tokenSecret = $"{name}-token";
        await CreateAsync(KubernetesArtifactKinds.Secret, Work, tokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(tokenSecret), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);

        await CreateAsync(KubernetesArtifactKinds.Pod, Work, PodName, () => client.CoreV1.CreateNamespacedPodAsync(
            Pod(mover, endpoint, tokenSecret, size),
            Work,
            cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
        flow.Log.Info($"Mover pod {Work}/{PodName} started for {source}: block PVC {name} ({VolumeSnapshotFlow.Bytes(snapshot.RestoreSize)}) from the snapshot, image {mover.Image}.");
        _watching = WatchAsync(_channel, _stopWatching.Token);
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

    private async Task EnsurePullSecretAsync(string secretName, string dockerConfig, CancellationToken cancellationToken)
    {
        var client = flow.Client;
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta { Name = secretName, Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup" } },
            Type = "kubernetes.io/dockerconfigjson",
            Data = new Dictionary<string, byte[]> { [".dockerconfigjson"] = Encoding.UTF8.GetBytes(dockerConfig) },
        };
        V1Secret? existing = null;
        try
        {
            existing = await client.CoreV1.ReadNamespacedSecretAsync(secretName, Work, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Created below.
        }

        if (existing is null)
        {
            await flow.CallAsync($"creating the pull secret {Work}/{secretName}", () => client.CoreV1.CreateNamespacedSecretAsync(secret, Work, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        else if (existing.Data?.TryGetValue(".dockerconfigjson", out var current) != true || !current.AsSpan().SequenceEqual(secret.Data[".dockerconfigjson"]))
        {
            secret.Metadata.ResourceVersion = existing.Metadata.ResourceVersion;
            await flow.CallAsync($"updating the pull secret {Work}/{secretName}", () => client.CoreV1.ReplaceNamespacedSecretAsync(secret, secretName, Work, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    private V1Pod Pod(MoverDeployment mover, MoverEndpoint endpoint, string tokenSecret, string size) => new()
    {
        Metadata = Meta(PodName),
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            ImagePullSecrets = mover.PullSecretName is { } pull ? [new V1LocalObjectReference { Name = pull }] : null,
            SecurityContext = new V1PodSecurityContext { RunAsUser = 0, SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" } },
            Volumes = [new V1Volume { Name = "volume", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = name } }],
            Containers =
            [
                new V1Container
                {
                    Name = "mover",
                    Image = mover.Image,
                    ImagePullPolicy = "IfNotPresent",
                    Env =
                    [
                        new V1EnvVar { Name = "BDFU_MOVER_MODE", Value = "backup" },
                        new V1EnvVar { Name = "BDFU_MOVER_DATA_URL", Value = endpoint.DataUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_RESULT_URL", Value = endpoint.ResultUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_SERVER_PINS", Value = string.Join(',', endpoint.ServerPins) },
                        new V1EnvVar { Name = "BDFU_MOVER_DEVICE", Value = VolumeSnapshotFlow.DevicePath },
                        new V1EnvVar { Name = "BDFU_MOVER_BYTES", Value = size },
                        // The root file system is read-only: no diagnostics socket in /tmp.
                        new V1EnvVar { Name = "DOTNET_EnableDiagnostics", Value = "0" },
                        new V1EnvVar
                        {
                            Name = "BDFU_MOVER_TOKEN",
                            ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = tokenSecret, Key = "token" } },
                        },
                    ],
                    VolumeDevices = [new V1VolumeDevice { Name = "volume", DevicePath = VolumeSnapshotFlow.DevicePath }],
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") },
                        Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("256Mi") },
                    },
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        ReadOnlyRootFilesystem = true,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                },
            ],
        },
    };

    private V1ObjectMeta Meta(string? objectName = null) => new()
    {
        Name = objectName ?? name,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = snapshot.Source },
    };

    /// <summary>
    /// Watches the mover pod while the channel waits: fails the channel at once when the pod cannot start or failed (with
    /// its reason and the end of its log), and writes a line now and then while the volume is still being copied.
    /// </summary>
    private async Task WatchAsync(IMoverBackupChannel channel, CancellationToken cancellationToken)
    {
        var client = flow.Client;
        var started = DateTimeOffset.UtcNow;
        var reported = started;
        var running = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(flow.Timings.Poll, cancellationToken).ConfigureAwait(false);
            V1Pod pod;
            try
            {
                pod = await client.CoreV1.ReadNamespacedPodAsync(PodName, Work, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                channel.Abort(new KubernetesOperationException($"The mover pod {Work}/{PodName} for {snapshot.Source} was deleted (cluster {flow.Cluster})."));
                return;
            }
            catch (HttpOperationException ex)
            {
                flow.Log.Warning($"The mover pod {Work}/{PodName} could not be read (cluster {flow.Cluster}): {VolumeSnapshotFlow.Problem(ex)}");
                continue;
            }

            var container = pod.Status?.ContainerStatuses?.FirstOrDefault();
            if (container?.State?.Waiting is { Reason: { } reason } waiting && FatalWaitingReasons.Contains(reason))
            {
                channel.Abort(new KubernetesOperationException(
                    $"The mover pod {Work}/{PodName} cannot start ({reason}): {waiting.Message} — image {container.Image}; is it in the registry, and may the cluster pull it (pull secret)?"));
                return;
            }

            switch (pod.Status?.Phase)
            {
                case "Failed":
                    channel.Abort(new KubernetesOperationException(
                        $"The mover pod {Work}/{PodName} for {snapshot.Source} failed ({container?.State?.Terminated?.Reason ?? pod.Status.Reason}, exit code {container?.State?.Terminated?.ExitCode}): {await LogTailAsync(cancellationToken).ConfigureAwait(false)}"));
                    return;
                case "Succeeded":
                    return;
                case "Running" when !running:
                    running = true;
                    flow.Log.Info($"Mover pod {Work}/{PodName} running{(pod.Spec?.NodeName is { Length: > 0 } node ? $" on node {node}" : string.Empty)} after {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s.");
                    break;
                case "Pending" when DateTimeOffset.UtcNow - reported >= flow.Timings.Progress:
                    reported = DateTimeOffset.UtcNow;
                    var unschedulable = pod.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
                    flow.Log.Info(unschedulable is not null
                        ? $"Mover pod {Work}/{PodName} not scheduled yet ({(DateTimeOffset.UtcNow - started).TotalMinutes:0} min): {unschedulable.Message}"
                        : $"Mover pod {Work}/{PodName} waiting for its volume, a copy of the snapshot of {snapshot.Source} ({(DateTimeOffset.UtcNow - started).TotalMinutes:0} min).");
                    break;
            }
        }
    }

    private async Task<string> LogTailAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await flow.Client.CoreV1.ReadNamespacedPodLogAsync(PodName, Work, container: "mover", tailLines: 10, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var text = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
            return text.Length > 0 ? text.ReplaceLineEndings(" | ") : "(no log)";
        }
        catch (HttpOperationException ex)
        {
            return $"(its log could not be read: {VolumeSnapshotFlow.Problem(ex)})";
        }
    }
}
