using System.Net;
using System.Text;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>A file restore into a live PVC of the cluster.</summary>
/// <param name="Keep">Existing files stay and the restored ones get the RESTORED- prefix; otherwise they are replaced.</param>
/// <param name="Owners">Owner and group from the backup; otherwise those of the folder an item goes into.</param>
/// <param name="SessionId">The restore session (names and labels of the objects it creates).</param>
public sealed record PvcFileRestoreRequest(string Namespace, string Claim, bool Keep, bool Owners, Guid SessionId);

/// <summary>Puts files into a live PVC: a mover pod mounts it next to the workload and takes the files from a channel.</summary>
public interface IKubernetesFileRestore
{
    Task<IPvcFileRestore> BeginFileRestoreAsync(PvcFileRestoreRequest request, IOperationLog log, IArtifactLedger ledger, CancellationToken cancellationToken);
}

/// <summary>A running file restore into a PVC; disposing it removes its pod and secrets.</summary>
public interface IPvcFileRestore : IAsyncDisposable
{
    /// <summary>For messages: <c>PVC namespace/name (cluster …)</c>.</summary>
    string Description { get; }

    /// <summary>Where the server writes the files (<see cref="MoverFileWriter"/>).</summary>
    IMoverFilesChannel Channel { get; }

    /// <summary>Waits for the mover's report of what it put in place, then removes its pod and secrets.</summary>
    Task<MoverFileReport> FinishAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The mover pod of a file restore and what it needs, in the PVC's own namespace: a secret with the channel's token, a
/// copy of the image pull secret when there is one, and the pod on the node of the pods that use the PVC. Everything is
/// in the artifact ledger, so a crash still removes it.
/// </summary>
internal sealed class PvcFileImport : IPvcFileRestore
{
    private const string EnforceLabel = "pod-security.kubernetes.io/enforce";

    private readonly VolumeSnapshotFlow _flow;
    private readonly PvcFileRestoreRequest _request;
    private readonly string _run;
    private readonly string _name;
    private readonly List<(string Kind, string Name, Guid LedgerId)> _created = [];
    private readonly CancellationTokenSource _stopWatching = new();
    private Task _watching = Task.CompletedTask;
    private IMoverFilesChannel? _channel;
    private int _disposed;

    private PvcFileImport(VolumeSnapshotFlow flow, PvcFileRestoreRequest request)
    {
        _flow = flow;
        _request = request;
        _run = request.SessionId.ToString("N")[..8];
        _name = VolumeSnapshotFlow.ObjectName(_run, $"files-{VolumeSnapshotFlow.Hash($"{request.Namespace}/{request.Claim}")}");
        Description = $"PVC {request.Namespace}/{request.Claim} (cluster {flow.Cluster})";
    }

    public string Description { get; }

    public IMoverFilesChannel Channel => _channel ?? throw new InvalidOperationException("The file restore has not started.");

    private string Ns => _request.Namespace;

    private string PodName => $"{_name}-mover";

    public static async Task<PvcFileImport> StartAsync(
        VolumeSnapshotFlow flow, IMoverChannels? channels, IMoverImageSource? images, PvcFileRestoreRequest request, CancellationToken cancellationToken)
    {
        var import = new PvcFileImport(flow, request);
        try
        {
            await import.StartAsync(channels, images, cancellationToken).ConfigureAwait(false);
            return import;
        }
        catch
        {
            await import.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MoverFileReport> FinishAsync(CancellationToken cancellationToken)
    {
        var report = await Channel.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        await StopWatchingAsync().ConfigureAwait(false);
        await DeleteCreatedAsync(cancellationToken).ConfigureAwait(false);
        return report;
    }

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

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await DeleteCreatedAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KubernetesOperationException or HttpOperationException or HttpRequestException or OperationCanceledException)
        {
            _flow.Log.Warning($"The mover pod {Ns}/{PodName} or its secrets could not be removed (cluster {_flow.Cluster}): {ex.Message}. The server removes them later.");
        }

        _stopWatching.Dispose();
    }

    private async Task StartAsync(IMoverChannels? channels, IMoverImageSource? images, CancellationToken cancellationToken)
    {
        var client = _flow.Client;
        var space = await ReadAsync($"the namespace {Ns}", () => client.CoreV1.ReadNamespaceAsync(Ns, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new KubernetesOperationException($"The namespace {Ns} does not exist in cluster {_flow.Cluster} (any more). Restore the namespace first, or download the files as ZIP.");
        if (space.Metadata?.Labels?.TryGetValue(EnforceLabel, out var level) == true && level == "restricted")
        {
            throw new KubernetesOperationException(
                $"The namespace {Ns} enforces the Pod Security level \"restricted\" ({EnforceLabel}), which admits no pod running as root; the mover needs root to write files with their owners. Download the files as ZIP, or allow \"baseline\" for the namespace during the restore.");
        }

        var claim = await ReadAsync($"the PVC {Ns}/{_request.Claim}", () => client.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(_request.Claim, Ns, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new KubernetesOperationException(
                $"The PVC {Ns}/{_request.Claim} does not exist in cluster {_flow.Cluster} (any more). Restore the PVC first (Namespace, apps or PVCs), or download the files as ZIP.");
        if (claim.Spec?.VolumeMode == "Block")
        {
            throw new KubernetesOperationException($"The PVC {Ns}/{_request.Claim} is a block volume (volumeMode Block); it has no file system to restore files into.");
        }

        if (claim.Status?.Phase != "Bound")
        {
            throw new KubernetesOperationException($"The PVC {Ns}/{_request.Claim} is {claim.Status?.Phase ?? "not bound"} in cluster {_flow.Cluster}; files can be restored into a bound PVC only.");
        }

        var pods = await _flow.CallAsync($"listing the pods of {Ns}", () => client.CoreV1.ListNamespacedPodAsync(Ns, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var users = pods.Items
            .Where(p => p.Metadata.DeletionTimestamp is null && p.Status?.Phase is not ("Succeeded" or "Failed"))
            .Where(p => p.Spec?.Volumes?.Any(v => v.PersistentVolumeClaim?.ClaimName == _request.Claim) == true)
            .ToList();
        var modes = claim.Spec?.AccessModes ?? [];
        if (modes.Contains("ReadWriteOncePod") && users.Count > 0)
        {
            throw new KubernetesOperationException(
                $"The PVC {Ns}/{_request.Claim} is ReadWriteOncePod and used by {Names(users)}: no second pod may mount it. Stop the workload (scale it to 0) for the restore, or download the files as ZIP.");
        }

        var node = modes.Contains("ReadWriteMany") ? null : users.Select(p => p.Spec?.NodeName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        _flow.Log.Info(users.Count == 0
            ? $"The PVC {Ns}/{_request.Claim} is not in use; the mover pod mounts it where the scheduler puts it."
            : node is null
                ? $"The PVC {Ns}/{_request.Claim} is used by {Names(users)}; the mover pod mounts it as well ({string.Join(", ", modes)})."
                : $"The PVC {Ns}/{_request.Claim} is used by {Names(users)} on node {node}; the mover pod mounts it there too.");

        if (channels is null || images is null)
        {
            throw new KubernetesOperationException($"Files cannot be restored into cluster {_flow.Cluster}: this server has no mover channel or image.");
        }

        var mover = await images.EnsurePushedAsync(line => _flow.Log.Info(line), cancellationToken).ConfigureAwait(false);
        _channel = await channels.OpenFilesAsync(Description, cancellationToken, _flow.Timings.MoverConnect).ConfigureAwait(false);
        var endpoint = _channel.Endpoint;
        var tokenSecret = $"{_name}-token";
        await CreateAsync(KubernetesArtifactKinds.Secret, tokenSecret, () => client.CoreV1.CreateNamespacedSecretAsync(
            new V1Secret { Metadata = Meta(tokenSecret), Type = "Opaque", StringData = new Dictionary<string, string> { ["token"] = endpoint.Token } },
            Ns,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // The pull secret lives in the work namespace; the pod in the PVC's namespace needs a copy of it.
        if (mover.PullSecretName is { } pull)
        {
            byte[]? config = null;
            if (mover.DockerConfigJson is { } json)
            {
                config = Encoding.UTF8.GetBytes(json);
            }
            else
            {
                var source = await _flow.CallAsync(
                    $"reading the pull secret {KubernetesSetup.WorkNamespace}/{pull}", () => client.CoreV1.ReadNamespacedSecretAsync(pull, KubernetesSetup.WorkNamespace, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                source.Data?.TryGetValue(".dockerconfigjson", out config);
            }

            if (config is null)
            {
                throw new KubernetesOperationException($"The pull secret {KubernetesSetup.WorkNamespace}/{pull} has no .dockerconfigjson (cluster {_flow.Cluster}).");
            }

            var copy = $"{_name}-pull";
            await CreateAsync(KubernetesArtifactKinds.Secret, copy, () => client.CoreV1.CreateNamespacedSecretAsync(
                new V1Secret { Metadata = Meta(copy), Type = "kubernetes.io/dockerconfigjson", Data = new Dictionary<string, byte[]> { [".dockerconfigjson"] = config } },
                Ns,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            mover = mover with { PullSecretName = copy };
        }

        var pod = MoverPods.BuildFiles(_request.Claim, Meta(PodName), mover, endpoint, tokenSecret, node, _request.Keep, _request.Owners);
        try
        {
            await CreateAsync(KubernetesArtifactKinds.Pod, PodName, () => client.CoreV1.CreateNamespacedPodAsync(pod, Ns, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (KubernetesOperationException ex) when (ex.InnerException is HttpOperationException { Response.StatusCode: HttpStatusCode.Forbidden } http
                                                      && http.Response.Content?.Contains("PodSecurity", StringComparison.Ordinal) == true)
        {
            throw new KubernetesOperationException(
                $"The namespace {Ns} does not admit the mover pod (Pod Security): {VolumeSnapshotFlow.Problem(http)}. The mover runs as root with the capabilities CHOWN, DAC_OVERRIDE, FOWNER and FSETID (level \"baseline\"). Download the files as ZIP instead.",
                ex);
        }

        _flow.Log.Info($"Mover pod {Ns}/{PodName} started for the files{(node is null ? string.Empty : $" on node {node}")}, image {mover.Image}.");
        _watching = MoverPods.WatchAsync(_flow, PodName, Description, _channel.Abort, _stopWatching.Token, Ns, $"waiting for {_request.Claim} to be mounted");
    }

    private async Task<T?> ReadAsync<T>(string what, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpOperationException ex)
        {
            throw new KubernetesOperationException($"Reading {what} in cluster {_flow.Cluster} failed: {VolumeSnapshotFlow.Problem(ex)}", ex);
        }
    }

    private static string Names(IEnumerable<V1Pod> pods)
    {
        var names = pods.Select(p => p.Metadata.Name).ToList();
        return names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
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

    /// <summary>Deletes the pod first (it holds the volume and the token), then the secrets.</summary>
    private async Task DeleteCreatedAsync(CancellationToken cancellationToken)
    {
        foreach (var (kind, name, ledgerId) in _created.OrderBy(c => c.Kind == KubernetesArtifactKinds.Pod ? 0 : 1).ToList())
        {
            await _flow.DeleteAsync(kind, Ns, name, cancellationToken).ConfigureAwait(false);
            await _flow.Ledger.UpdateAsync(ledgerId, ArtifactState.Done, null, CancellationToken.None).ConfigureAwait(false);
            _created.RemoveAll(c => c.LedgerId == ledgerId);
        }
    }

    private async Task CreateAsync<T>(string kind, string objectName, Func<Task<T>> create)
    {
        var ledgerId = await _flow.Ledger.RecordAsync(kind, $"{Ns}/{objectName}", null, null, CancellationToken.None).ConfigureAwait(false);
        _created.Add((kind, objectName, ledgerId));
        await _flow.CallAsync($"creating {kind} {Ns}/{objectName}", create).ConfigureAwait(false);
    }

    private V1ObjectMeta Meta(string objectName) => new()
    {
        Name = objectName,
        Labels = new Dictionary<string, string> { [VolumeSnapshotFlow.ManagedByLabel] = "backdafuckup", [VolumeSnapshotFlow.RunLabel] = _run },
        Annotations = new Dictionary<string, string> { [VolumeSnapshotFlow.SourceAnnotation] = $"{Ns}/{_request.Claim}" },
    };
}
