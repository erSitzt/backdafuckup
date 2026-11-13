using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The node agents of a cluster (spec 2026-11-12-1952-kubernetes-file-level-volumes): the running pods of the DaemonSet in
/// <see cref="KubernetesSetup.NodeNamespace"/> by node, and requests to them by exec of <c>node-request</c> with the request
/// on standard input (its token never in the command, which the API server's audit log keeps).
/// </summary>
public sealed class NodeAgents
{
    /// <summary>What the server execs in an agent's pod.</summary>
    public static readonly IReadOnlyList<string> Command = ["/app/backdafuckup-mover", "node-request"];

    private readonly IPodExecutor _executor;
    private readonly string _cluster;

    private NodeAgents(IPodExecutor executor, string cluster, IReadOnlyDictionary<string, string> pods)
    {
        _executor = executor;
        _cluster = cluster;
        Pods = pods;
    }

    /// <summary>The agent pod on each node that has a running one.</summary>
    public IReadOnlyDictionary<string, string> Pods { get; }

    public bool Any => Pods.Count > 0;

    /// <summary>The cluster's running node agents; none when it has no node agent (or its namespace cannot be read).</summary>
    public static async Task<NodeAgents> FindAsync(IKubernetes client, IPodExecutor executor, string cluster, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var pods = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var list = await client.CoreV1.ListNamespacedPodAsync(
                KubernetesSetup.NodeNamespace, labelSelector: $"app.kubernetes.io/name={KubernetesSetup.NodeAgentName}", cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var pod in list.Items.Where(p => p.Status?.Phase == "Running" && p.Metadata.DeletionTimestamp is null && !string.IsNullOrEmpty(p.Spec.NodeName)))
            {
                pods.TryAdd(pod.Spec.NodeName, pod.Metadata.Name);
            }
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            // No node agent.
        }

        return new NodeAgents(executor, cluster, pods);
    }

    /// <summary>Sends a request to the agent on <paramref name="node"/>; its answer, or the reason as an exception.</summary>
    public async Task<JsonObject> RequestAsync(string node, JsonObject request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Pods.TryGetValue(node, out var pod))
        {
            throw new KubernetesOperationException($"The node agent has no running pod on node {node} of cluster {_cluster}.");
        }

        var (code, output) = await _executor.ExecAsync(KubernetesSetup.NodeNamespace, pod, "node", Command, request.ToJsonString(), cancellationToken).ConfigureAwait(false);
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        JsonObject? answer = null;
        try
        {
            answer = line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // Not an answer: the output says what went wrong.
        }

        if (answer is null)
        {
            throw new KubernetesOperationException(
                $"The node agent {KubernetesSetup.NodeNamespace}/{pod} on node {node} of cluster {_cluster} did not answer (exit code {code}): {Short(output)}");
        }

        return (string?)answer["error"] is { } error
            ? throw new KubernetesOperationException($"The node agent {KubernetesSetup.NodeNamespace}/{pod} on node {node} of cluster {_cluster}: {error}")
            : answer;
    }

    /// <summary>
    /// The kubelets' usage numbers through every agent (or those of <paramref name="nodes"/>): pod volumes and containers'
    /// writable layers. An agent that fails is a problem; the others still count.
    /// </summary>
    public async Task<KubernetesUsage> UsageAsync(IReadOnlyCollection<string>? nodes, CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<(string, string, string), long>();
        var layers = new Dictionary<(string, string, string), long>();
        var problems = new List<string>();
        foreach (var node in Pods.Keys.Where(n => nodes is null || nodes.Contains(n)).Order(StringComparer.Ordinal))
        {
            JsonObject stats;
            try
            {
                stats = await RequestAsync(node, new JsonObject { ["op"] = "stats" }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KubernetesOperationException or HttpOperationException or IOException or System.Net.WebSockets.WebSocketException)
            {
                problems.Add(ex.Message);
                continue;
            }

            foreach (var pod in stats["pods"]?.AsArray() ?? [])
            {
                if ((string?)pod?["namespace"] is not { } ns || (string?)pod["name"] is not { } name)
                {
                    continue;
                }

                foreach (var (volume, bytes) in pod["volumes"]?.AsObject() ?? [])
                {
                    if (bytes is JsonValue value && value.TryGetValue<long>(out var used))
                    {
                        volumes[(ns, name, volume)] = used;
                    }
                }

                foreach (var (container, bytes) in pod["containers"]?.AsObject() ?? [])
                {
                    if (bytes is JsonValue value && value.TryGetValue<long>(out var used))
                    {
                        layers[(ns, name, container)] = used;
                    }
                }
            }
        }

        return new KubernetesUsage(
            "the node agents (the kubelets' statistics)", volumes, layers, new Dictionary<(string, string), long>(), new Dictionary<(string, string, string), double>(), problems);
    }

    private static string Short(string text) => text.Length > 400 ? text[..400] + "…" : text;
}

/// <summary>
/// A pod volume read by the node agent on its node: a tree channel of the server, and a request to the agent to read the
/// volume's folder into it. Disposing closes the channel; the agent's read fails with it.
/// </summary>
internal sealed class NodeVolume : IFileTreeReader
{
    private IMoverTreeChannel? _channel;

    private NodeVolume(string transport) => TransportName = transport;

    public string TransportName { get; }

    /// <param name="plugin"><c>empty-dir</c> or <c>csi</c>.</param>
    /// <param name="volume">The volume's name (an emptyDir), or its PV's (a CSI volume).</param>
    public static async Task<NodeVolume> StartAsync(
        NodeAgents agents, string node, string podUid, string plugin, string volume, string description, IMoverChannels channels, TimeSpan connectTimeout,
        IPreviousFileTree? previous, IOperationLog log, CancellationToken cancellationToken)
    {
        var pod = agents.Pods.GetValueOrDefault(node) ?? throw new KubernetesOperationException($"{description}: the node agent has no running pod on node {node}.");
        var reader = new NodeVolume($"the node agent {KubernetesSetup.NodeNamespace}/{pod} on node {node}");
        try
        {
            reader._channel = await channels.OpenTreeBackupAsync(description, cancellationToken, connectTimeout, previous).ConfigureAwait(false);
            var endpoint = reader._channel.Endpoint;
            var answer = await agents.RequestAsync(node, new JsonObject
            {
                ["op"] = "read",
                ["channel"] = ChannelId(endpoint),
                ["token"] = endpoint.Token,
                ["podUid"] = podUid,
                ["plugin"] = plugin,
                ["volume"] = volume,
                ["previous"] = endpoint.PreviousUrl is not null,
            }, cancellationToken).ConfigureAwait(false);
            log.Info($"{description}: read file by file by {reader.TransportName} from {(string?)answer["path"]}" +
                     ((bool?)answer["queued"] == true ? " (after the reads it is busy with)." : "."));
            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ReadAsync(Func<Stream, CancellationToken, Task> consume, CancellationToken cancellationToken) =>
        await (_channel ?? throw new InvalidOperationException("The node agent has not started.")).ConsumeAsync(consume, cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _channel, null) is { } channel)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The channel's id: the segment before <c>/data</c> in its URL (<c>…/api/agent/movers/{id}/data</c>).</summary>
    private static string ChannelId(MoverEndpoint endpoint) => endpoint.DataUrl.Segments[^2].TrimEnd('/');
}
