using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Mover;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// The node agent (spec 2026-11-12-1952-kubernetes-file-level-volumes): paths only from fixed patterns, requests over its
/// socket, a pod volume read into a mover channel of its own server, and the kubelet's statistics.
/// </summary>
public sealed class NodeAgentTests : IDisposable
{
    private const string PodUid = "6f1c2e0a-8f43-4d6e-9a51-0c7a3b2d9e10";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-node-" + Guid.NewGuid().ToString("N")[..12]);

    public NodeAgentTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Kubelet => Path.Combine(_root, "kubelet");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_volume_s_path_comes_only_from_fixed_patterns()
    {
        var agent = new NodeAgent(Options(), TextWriter.Null);

        Assert.Equal(Path.Combine(Kubelet, "pods", PodUid, "volumes", "kubernetes.io~empty-dir", "cache"), agent.VolumePath(PodUid, "empty-dir", "cache"));
        Assert.Equal(Path.Combine(Kubelet, "pods", PodUid, "volumes", "kubernetes.io~csi", "pvc-0d1e.2", "mount"), agent.VolumePath(PodUid.ToUpperInvariant(), "csi", "pvc-0d1e.2"));
        Assert.Null(agent.VolumePath(PodUid, "empty-dir", "../../etc"));
        Assert.Null(agent.VolumePath(PodUid, "empty-dir", "a/b"));
        Assert.Null(agent.VolumePath(PodUid, "empty-dir", "Cache"));
        Assert.Null(agent.VolumePath(PodUid, "empty-dir", ".hidden"));
        Assert.Null(agent.VolumePath(PodUid, "host-path", "cache"));
        Assert.Null(agent.VolumePath("../" + PodUid, "empty-dir", "cache"));
        Assert.Null(agent.VolumePath("not-a-uid", "empty-dir", "cache"));
        Assert.Null(agent.VolumePath(null, "empty-dir", "cache"));
    }

    [Fact]
    public async Task A_request_over_the_socket_reads_a_pod_volume_into_a_channel_of_the_agent_s_own_server()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, "The mover reads volumes on linux-x64.");
        var volume = Path.Combine(Kubelet, "pods", PodUid, "volumes", "kubernetes.io~empty-dir", "cache");
        Directory.CreateDirectory(Path.Combine(volume, "sub"));
        await File.WriteAllTextAsync(Path.Combine(volume, "a.txt"), "hello", Ct);
        await File.WriteAllBytesAsync(Path.Combine(volume, "sub", "b.bin"), new byte[70_000], Ct);
        var server = new Channel();
        var options = Options();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var log = new StringWriter();
        var agent = new NodeAgent(options, TextWriter.Synchronized(log), _ => server);
        var running = agent.RunAsync(stop.Token);
        for (var i = 0; i < 100 && !File.Exists(options.Socket); i++)
        {
            await Task.Delay(20, Ct);
        }

        // Its own folder is there: the kubelet's directory is the node's.
        var (code, check) = await RequestAsync(options, new JsonObject { ["op"] = "check" });
        Assert.Equal((0, null), (code, (string?)check["problem"]));

        var channel = Guid.NewGuid();
        (code, var answer) = await RequestAsync(options, new JsonObject
        {
            ["op"] = "read", ["channel"] = channel.ToString(), ["token"] = "one-time", ["podUid"] = PodUid, ["plugin"] = "empty-dir", ["volume"] = "cache",
        });
        Assert.True(code == 0, answer.ToJsonString());
        Assert.Equal((true, volume), ((bool?)answer["accepted"], (string?)answer["path"]));

        // The files go to the server it was configured with, on the channel of the request, with its token.
        var sent = await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal($"https://backup.example:9555/api/agent/movers/{channel:D}/data", sent.Url);
        Assert.Equal("Bearer one-time", sent.Authorization);
        var names = new List<string>();
        var reader = new MoverTreeReader(new MemoryStream(sent.Body));
        await reader.ReadHeaderAsync(Ct);
        while (await reader.ReadAsync(Ct) is { } record and not TreeEnd)
        {
            if (record is TreeFileStart f)
            {
                names.Add(Encoding.UTF8.GetString(f.Name));
            }
        }

        Assert.Equal(["a.txt", "b.bin"], names);

        // What it cannot do is answered with the reason.
        (code, answer) = await RequestAsync(options, new JsonObject { ["op"] = "read", ["channel"] = channel.ToString(), ["token"] = "t", ["podUid"] = PodUid, ["plugin"] = "empty-dir", ["volume"] = "gone" });
        Assert.Equal(1, code);
        Assert.Contains("does not exist (the pod runs elsewhere, or was replaced)", (string?)answer["error"], StringComparison.Ordinal);
        (code, answer) = await RequestAsync(options, new JsonObject { ["op"] = "format" });
        Assert.Equal((1, "Unknown operation \"format\" (check, read or stats)."), (code, (string?)answer["error"]));

        await stop.CancelAsync();
        await running;
        Assert.Contains($"[{PodUid}/cache] reading {volume}.", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_its_socket_a_request_says_so()
    {
        var output = new StringWriter();
        Assert.Equal(1, await NodeRequest.RunAsync(new StringReader("""{ "op": "check" }"""), output, Path.Combine(_root, "none.sock"), Ct));
        Assert.StartsWith($"The node agent's socket {Path.Combine(_root, "none.sock")} does not answer", (string?)JsonNode.Parse(output.ToString())!["error"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_kubelet_s_statistics_are_cut_to_pod_volumes_PVCs_and_writable_layers()
    {
        var summary = JsonNode.Parse("""
            { "node": { "nodeName": "worker-1" },
              "pods": [ { "podRef": { "name": "web-1", "namespace": "web", "uid": "u1" },
                          "containers": [ { "name": "nginx", "rootfs": { "usedBytes": 4096, "capacityBytes": 1 } }, { "name": "sidecar" } ],
                          "volume": [ { "name": "cache", "usedBytes": 1048576 },
                                      { "name": "data", "usedBytes": 665000000000, "pvcRef": { "name": "data-web", "namespace": "web" } },
                                      { "name": "kube-api-access-x", "usedBytes": 12288 } ] } ] }
            """)!.AsObject();

        var cut = NodeAgent.Summarize(summary, "worker-1");

        Assert.Equal(
            """{"node":"worker-1","pods":[{"namespace":"web","name":"web-1","volumes":{"cache":1048576,"kube-api-access-x":12288},"claims":{"data-web":665000000000},"containers":{"nginx":4096}}]}""",
            cut.ToJsonString());
    }

    private NodeAgentOptions Options() => new(
        new Uri("https://backup.example:9555/"), [], Kubelet, Path.Combine(_root, "node.sock"), "worker-1", "10.26.3.4", PodUid, null);

    private static async Task<(int Code, JsonObject Answer)> RequestAsync(NodeAgentOptions options, JsonObject request)
    {
        var output = new StringWriter();
        var code = await NodeRequest.RunAsync(new StringReader(request.ToJsonString()), output, options.Socket, Ct);
        return (code, JsonNode.Parse(output.ToString())!.AsObject());
    }

    /// <summary>The server's mover channel: takes the files and answers as the server does.</summary>
    private sealed class Channel : HttpMessageHandler
    {
        public TaskCompletionSource<(string Url, string? Authorization, byte[] Body)> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Received.TrySetResult((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"total":0}""", Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
