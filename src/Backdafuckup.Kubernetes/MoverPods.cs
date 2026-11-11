using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>The mover pods of backups and restores: how they look, and the watch that fails a channel when its pod cannot run.</summary>
internal static class MoverPods
{
    /// <summary>Pod states that do not get better by waiting.</summary>
    private static readonly HashSet<string> FatalWaitingReasons = new(StringComparer.Ordinal)
    {
        "ImagePullBackOff", "ErrImageNeverPull", "InvalidImageName", "CreateContainerConfigError", "CreateContainerError",
    };

    /// <summary>The mover pod for <paramref name="claim"/> (a block PVC in the work namespace).</summary>
    /// <param name="mode"><c>backup</c> or <c>restore</c>.</param>
    /// <param name="size">Backup: the bytes to send; null for a restore.</param>
    public static V1Pod Build(string podName, string claim, V1ObjectMeta metadata, MoverDeployment mover, MoverEndpoint endpoint, string tokenSecret, string mode, string? size) => new()
    {
        Metadata = metadata,
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            ImagePullSecrets = mover.PullSecretName is { } pull ? [new V1LocalObjectReference { Name = pull }] : null,
            SecurityContext = new V1PodSecurityContext { RunAsUser = 0, SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" } },
            Volumes = [new V1Volume { Name = "volume", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim } }],
            Containers =
            [
                new V1Container
                {
                    Name = "mover",
                    Image = mover.Image,
                    ImagePullPolicy = "IfNotPresent",
                    Env =
                    [
                        new V1EnvVar { Name = "BDFU_MOVER_MODE", Value = mode },
                        new V1EnvVar { Name = "BDFU_MOVER_DATA_URL", Value = endpoint.DataUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_RESULT_URL", Value = endpoint.ResultUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_SERVER_PINS", Value = string.Join(',', endpoint.ServerPins) },
                        new V1EnvVar { Name = "BDFU_MOVER_DEVICE", Value = VolumeSnapshotFlow.DevicePath },
                        new V1EnvVar { Name = "BDFU_MOVER_BYTES", Value = size ?? string.Empty },
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

    /// <summary>The folder the live PVC is mounted at in a file restore's mover pod.</summary>
    public const string FilesRoot = "/bdfu-volume";

    /// <summary>
    /// The mover pod of a file restore: in the PVC's own namespace, the PVC mounted as a file system, on the node of the
    /// pods that use it (a ReadWriteOnce volume is attached to one node), tolerating that node's taints. It runs as root
    /// with the capabilities to set owners and write into folders of any owner (allowed by the Pod Security level
    /// "baseline", not by "restricted").
    /// </summary>
    public static V1Pod BuildFiles(string claim, V1ObjectMeta metadata, MoverDeployment mover, MoverEndpoint endpoint, string tokenSecret, string? node, bool keep, bool owners) => new()
    {
        Metadata = metadata,
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            NodeName = node,
            Tolerations = node is null ? null : [new V1Toleration { OperatorProperty = "Exists" }],
            ImagePullSecrets = mover.PullSecretName is { } pull ? [new V1LocalObjectReference { Name = pull }] : null,
            SecurityContext = new V1PodSecurityContext { RunAsUser = 0, SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" } },
            Volumes = [new V1Volume { Name = "volume", PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = claim } }],
            Containers =
            [
                new V1Container
                {
                    Name = "mover",
                    Image = mover.Image,
                    ImagePullPolicy = "IfNotPresent",
                    Env =
                    [
                        new V1EnvVar { Name = "BDFU_MOVER_MODE", Value = "files" },
                        new V1EnvVar { Name = "BDFU_MOVER_DATA_URL", Value = endpoint.DataUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_RESULT_URL", Value = endpoint.ResultUrl.ToString() },
                        new V1EnvVar { Name = "BDFU_MOVER_SERVER_PINS", Value = string.Join(',', endpoint.ServerPins) },
                        new V1EnvVar { Name = "BDFU_MOVER_ROOT", Value = FilesRoot },
                        new V1EnvVar { Name = "BDFU_MOVER_IF_EXISTS", Value = keep ? "keep" : "replace" },
                        new V1EnvVar { Name = "BDFU_MOVER_OWNERS", Value = owners ? "1" : "0" },
                        new V1EnvVar { Name = "DOTNET_EnableDiagnostics", Value = "0" },
                        new V1EnvVar
                        {
                            Name = "BDFU_MOVER_TOKEN",
                            ValueFrom = new V1EnvVarSource { SecretKeyRef = new V1SecretKeySelector { Name = tokenSecret, Key = "token" } },
                        },
                    ],
                    VolumeMounts = [new V1VolumeMount { Name = "volume", MountPath = FilesRoot }],
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new("64Mi") },
                        Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("256Mi") },
                    },
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        ReadOnlyRootFilesystem = true,
                        Capabilities = new V1Capabilities { Drop = ["ALL"], Add = ["CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID"] },
                    },
                },
            ],
        },
    };

    /// <summary>
    /// Watches the mover pod while the channel waits: fails the channel at once when the pod cannot start or failed (with
    /// its reason and the end of its log), and writes a line now and then while the volume is still being copied.
    /// </summary>
    /// <param name="ns">The pod's namespace (default: the work namespace).</param>
    /// <param name="pending">What a pending pod waits for, for the log (default: the copy of a snapshot).</param>
    public static async Task WatchAsync(
        VolumeSnapshotFlow flow, string podName, string source, Action<Exception> abort, CancellationToken cancellationToken, string? ns = null, string? pending = null, string mode = "backup")
    {
        var space = ns ?? KubernetesSetup.WorkNamespace;
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
                pod = await client.CoreV1.ReadNamespacedPodAsync(podName, space, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                abort(new KubernetesOperationException($"The mover pod {space}/{podName} for {source} was deleted (cluster {flow.Cluster})."));
                return;
            }
            catch (HttpOperationException ex)
            {
                flow.Log.Warning($"The mover pod {space}/{podName} could not be read (cluster {flow.Cluster}): {VolumeSnapshotFlow.Problem(ex)}");
                continue;
            }

            var container = pod.Status?.ContainerStatuses?.FirstOrDefault();
            if (container?.State?.Waiting is { Reason: { } reason } waiting && FatalWaitingReasons.Contains(reason))
            {
                abort(new KubernetesOperationException(
                    $"The mover pod {space}/{podName} cannot start ({reason}): {waiting.Message} — image {container.Image}; is it in the registry, and may the cluster pull it (pull secret)?"));
                return;
            }

            switch (pod.Status?.Phase)
            {
                case "Failed":
                    abort(new KubernetesOperationException(
                        $"The mover pod {space}/{podName} for {source} failed ({container?.State?.Terminated?.Reason ?? pod.Status.Reason}, exit code {container?.State?.Terminated?.ExitCode}): {await LogTailAsync(flow, space, podName, cancellationToken).ConfigureAwait(false)}"));
                    return;
                case "Succeeded":
                    return;
                case "Running" when !running:
                    running = true;
                    KubernetesMetrics.RecordMoverStart(flow.Cluster, mode, DateTimeOffset.UtcNow - started);
                    flow.Log.Info($"Mover pod {space}/{podName} running{(pod.Spec?.NodeName is { Length: > 0 } node ? $" on node {node}" : string.Empty)} after {(DateTimeOffset.UtcNow - started).TotalSeconds:0} s.");
                    break;
                case "Pending" when DateTimeOffset.UtcNow - reported >= flow.Timings.Progress:
                    reported = DateTimeOffset.UtcNow;
                    var unschedulable = pod.Status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
                    flow.Log.Info(unschedulable is not null
                        ? $"Mover pod {space}/{podName} not scheduled yet ({(DateTimeOffset.UtcNow - started).TotalMinutes:0} min): {unschedulable.Message}"
                        : $"Mover pod {space}/{podName} {pending ?? $"waiting for its volume, a copy of the snapshot of {source}"} ({(DateTimeOffset.UtcNow - started).TotalMinutes:0} min).");
                    break;
            }
        }
    }

    private static async Task<string> LogTailAsync(VolumeSnapshotFlow flow, string ns, string podName, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await flow.Client.CoreV1.ReadNamespacedPodLogAsync(podName, ns, container: "mover", tailLines: 10, cancellationToken: cancellationToken).ConfigureAwait(false);
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
