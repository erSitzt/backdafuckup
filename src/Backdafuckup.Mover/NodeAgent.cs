using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Backdafuckup.Core.Tls;

namespace Backdafuckup.Mover;

/// <summary>
/// The settings of the node agent (<c>BDFU_MOVER_MODE=node</c>), from the environment of its DaemonSet:
/// <list type="bullet">
/// <item><c>BDFU_NODE_SERVER_URL</c>: the backup server, e.g. <c>https://backup01:9555/</c>; reads go to its mover channels.</item>
/// <item><c>BDFU_MOVER_SERVER_PINS</c>: SHA-256 SPKI pins of the server certificate; empty: the system's CA store.</item>
/// <item><c>BDFU_NODE_KUBELET_ROOT</c>: where the kubelet's directory is mounted (read-only); default <see cref="NodeAgentOptions.DefaultKubeletRoot"/>.</item>
/// <item><c>BDFU_NODE_KUBELET_CERT</c>: the kubelet's own serving certificate under that mount (kubeadm, kind: self-signed),
/// trusted for its statistics when the cluster CA did not sign the one it presents; default <c>pki/kubelet.crt</c> there.</item>
/// <item><c>BDFU_NODE_SOCKET</c>: the socket <c>node-request</c> talks to; default <see cref="NodeAgentOptions.DefaultSocket"/>.</item>
/// <item><c>NODE_NAME</c>, <c>HOST_IP</c>, <c>POD_UID</c>: from the downward API.</item>
/// </list>
/// </summary>
internal sealed record NodeAgentOptions(
    Uri Server, IReadOnlyList<string> Pins, string KubeletRoot, string Socket, string? NodeName, string? HostIp, string? PodUid, string? KubeletCert,
    string AccountDirectory = NodeAgentOptions.DefaultAccountDirectory, int MaxReads = 2, TimeSpan? Racy = null)
{
    public const string DefaultKubeletRoot = "/host/kubelet";
    public const string DefaultSocket = "/run/bdfu/node.sock";
    public const string DefaultAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    public static NodeAgentOptions FromEnvironment(Func<string, string?> get)
    {
        var url = get("BDFU_NODE_SERVER_URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || server.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException($"BDFU_NODE_SERVER_URL is not an http(s) URL: {url}");
        }

        var pins = (get("BDFU_MOVER_SERVER_PINS") ?? string.Empty).Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new NodeAgentOptions(
            server, pins,
            get("BDFU_NODE_KUBELET_ROOT") is { Length: > 0 } root ? root.TrimEnd('/') : DefaultKubeletRoot,
            get("BDFU_NODE_SOCKET") is { Length: > 0 } socket ? socket : DefaultSocket,
            get("NODE_NAME"), get("HOST_IP"), get("POD_UID"),
            get("BDFU_NODE_KUBELET_CERT") is { Length: > 0 } cert ? cert : null);
    }

    /// <summary>The kubelet's own certificate: the one named, or <c>pki/kubelet.crt</c> in its directory.</summary>
    public string KubeletCertPath => KubeletCert ?? Path.Combine(KubeletRoot, "pki", "kubelet.crt");
}

/// <summary>
/// The node agent (spec 2026-11-12-1952-kubernetes-file-level-volumes): a DaemonSet pod on every node that reads pod
/// volumes (emptyDirs, CSI volumes mounted by a pod) file by file from the kubelet's directory, and the kubelet's usage
/// numbers. The server never sends it a URL or a path: it execs <c>node-request</c> in the agent's pod with an operation,
/// a mover channel and its token, a pod's uid and a volume's name; the agent builds the path from fixed patterns and sends
/// the files to its own server's channel. At most <see cref="NodeAgentOptions.MaxReads"/> reads run at once.
/// </summary>
internal sealed class NodeAgent(NodeAgentOptions options, TextWriter log, Func<MoverOptions, HttpMessageHandler?>? transport = null)
{
    private readonly SemaphoreSlim _reads = new(options.MaxReads, options.MaxReads);
    private int _running;
    private int _waiting;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        log.WriteLine($"Node agent on {options.NodeName ?? "?"}: kubelet directory {options.KubeletRoot}, server {options.Server.GetLeftPart(UriPartial.Authority)}, socket {options.Socket}.");
        if (SelfCheck() is { } problem)
        {
            log.WriteLine(problem);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.Socket)!);
        File.Delete(options.Socket);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(options.Socket));
        listener.Listen(16);
        using var stop = cancellationToken.Register(listener.Dispose);
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException && cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = ServeAsync(client, cancellationToken);
        }
    }

    /// <summary>
    /// The agent sees the kubelet's directory when its own pod's folder is there (the mount and its propagation work);
    /// null when it does, else what is wrong.
    /// </summary>
    internal string? SelfCheck()
    {
        if (!Directory.Exists(Path.Combine(options.KubeletRoot, "pods")))
        {
            return $"The kubelet's directory is not at {options.KubeletRoot} (no pods folder): pod volumes cannot be read on {options.NodeName ?? "this node"}. Is the DaemonSet's kubelet path the node's?";
        }

        return options.PodUid is { Length: > 0 } uid && !Directory.Exists(Path.Combine(options.KubeletRoot, "pods", uid))
            ? $"The kubelet's directory {options.KubeletRoot} does not have this pod's folder ({uid}): it is not the kubelet of {options.NodeName ?? "this node"}."
            : null;
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            JsonObject response;
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                response = await HandleAsync(JsonNode.Parse(line ?? "{}") as JsonObject ?? [], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                response = Error($"The request is not one the node agent understands: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Every request gets an answer: the server must not read silence as success.
                log.WriteLine($"A request failed: {ex}");
                response = Error($"The node agent on {options.NodeName ?? "this node"} failed on the request ({ex.GetType().Name}): {ex.Message}");
            }

            await writer.WriteLineAsync(response.ToJsonString(NodeRequest.Json)).ConfigureAwait(false);
        }
    }

    internal async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var op = (string?)request["op"];
        return op switch
        {
            "check" => new JsonObject
            {
                ["node"] = options.NodeName,
                ["problem"] = SelfCheck(),
                ["running"] = _running,
                ["waiting"] = _waiting,
            },
            "read" => StartRead(request, cancellationToken),
            "stats" => await StatsAsync(cancellationToken).ConfigureAwait(false),
            _ => Error($"Unknown operation \"{op}\" (check, read or stats)."),
        };
    }

    /// <summary>
    /// Starts reading a pod volume into a mover channel and answers at once (<c>accepted</c>, and whether it waits for
    /// other reads): the server's channel waits for the files, not the exec.
    /// </summary>
    private JsonObject StartRead(JsonObject request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse((string?)request["channel"], out var channel) || (string?)request["token"] is not { Length: > 0 } token)
        {
            return Error("A read needs the mover channel and its token.");
        }

        if (VolumePath((string?)request["podUid"], (string?)request["plugin"], (string?)request["volume"]) is not { } path)
        {
            return Error($"No such pod volume: pod {(string?)request["podUid"]}, {(string?)request["plugin"]} volume {(string?)request["volume"]} (pod uid, plugin empty-dir or csi, and the volume's name).");
        }

        if (!Directory.Exists(path))
        {
            return Error($"The volume is not on {options.NodeName ?? "this node"}: {path} does not exist (the pod runs elsewhere, or was replaced).");
        }

        var data = new Uri(options.Server, $"api/agent/movers/{channel:D}/data");
        var mover = new MoverOptions(
            MoverMode.Tree, data, new Uri(options.Server, $"api/agent/movers/{channel:D}/result"), token, options.Pins, string.Empty, Root: path,
            PreviousUrl: (bool?)request["previous"] == true ? new Uri(options.Server, $"api/agent/movers/{channel:D}/previous") : null, Racy: options.Racy);
        var queued = Volatile.Read(ref _running) >= options.MaxReads;
        _ = ReadAsync(mover, $"{(string?)request["podUid"]}/{(string?)request["volume"]}", cancellationToken);
        return new JsonObject { ["accepted"] = true, ["path"] = path, ["queued"] = queued };
    }

    private async Task ReadAsync(MoverOptions mover, string what, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _waiting);
        await _reads.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref _waiting);
        Interlocked.Increment(ref _running);
        try
        {
            log.WriteLine($"[{what}] reading {mover.Root}.");
            await MoverRunner.RunAsync(mover, transport?.Invoke(mover), new PrefixedWriter(log, $"[{what}] "), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _reads.Release();
        }
    }

    /// <summary>
    /// The folder of a pod volume under the kubelet's directory, from fixed patterns: an emptyDir's
    /// <c>pods/&lt;uid&gt;/volumes/kubernetes.io~empty-dir/&lt;name&gt;</c>, a CSI volume's
    /// <c>pods/&lt;uid&gt;/volumes/kubernetes.io~csi/&lt;pv&gt;/mount</c>; null for anything else.
    /// </summary>
    internal string? VolumePath(string? podUid, string? plugin, string? volume)
    {
        if (podUid is null || !Guid.TryParseExact(podUid, "D", out _) || volume is null || !IsVolumeName(volume))
        {
            return null;
        }

        var pod = Path.Combine(options.KubeletRoot, "pods", podUid.ToLowerInvariant(), "volumes");
        return plugin switch
        {
            "empty-dir" => Path.Combine(pod, "kubernetes.io~empty-dir", volume),
            "csi" => Path.Combine(pod, "kubernetes.io~csi", volume, "mount"),
            _ => null,
        };
    }

    /// <summary>
    /// A volume's or PV's name: lower-case letters, digits, '-' and '.', starting and ending alphanumeric, at most 253
    /// characters. Checked without regular expressions: the trimmed image leaves their assembly out.
    /// </summary>
    internal static bool IsVolumeName(string name) =>
        name.Length is > 0 and <= 253 && IsAlphanumeric(name[0]) && IsAlphanumeric(name[^1]) && name.All(c => IsAlphanumeric(c) || c is '-' or '.');

    private static bool IsAlphanumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    /// <summary>
    /// The kubelet's <c>/stats/summary</c>, cut to what the server uses: per pod its volumes' and containers' bytes. TLS
    /// is verified: against the kubelet's own certificate when the DaemonSet names it, else against the cluster CA.
    /// </summary>
    private async Task<JsonObject> StatsAsync(CancellationToken cancellationToken)
    {
        if (options.HostIp is not { Length: > 0 } host)
        {
            return Error("The node agent does not know its node's address (HOST_IP).");
        }

        var url = new Uri($"https://{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:10250/stats/summary?only_cpu_and_memory=false");
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(Path.Combine(options.AccountDirectory, "token"), cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (IOException ex)
        {
            return Error($"The node agent's ServiceAccount token cannot be read: {ex.Message}");
        }

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = KubeletValidator() },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Error($"The kubelet {url.Authority} answered {(int)response.StatusCode} {response.StatusCode} for its statistics: {(body.Length > 300 ? body[..300] : body)}");
            }

            return Summarize(JsonNode.Parse(body) as JsonObject ?? [], options.NodeName);
        }
        catch (HttpRequestException ex)
        {
            return Error($"The kubelet {url.Authority} cannot be reached, or its certificate is neither signed by the cluster CA for {host} nor its own {options.KubeletCertPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// The kubelet's certificate is trusted when the cluster CA signed it for the node's address (RKE2, kubelets with
    /// serving certificates from the cluster), else when it is the kubelet's own certificate in its directory (kubeadm and
    /// kind sign their own). Never without either.
    /// </summary>
    private RemoteCertificateValidationCallback KubeletValidator()
    {
        var caPath = Path.Combine(options.AccountDirectory, "ca.crt");
        var ownPath = options.KubeletCertPath;
        return (_, certificate, _, errors) =>
        {
            if (certificate is null)
            {
                return false;
            }

            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && File.Exists(caPath))
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                var authorities = new X509Certificate2Collection();
                authorities.ImportFromPemFile(caPath);
                chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
                using var leaf = new X509Certificate2(certificate);
                if (chain.Build(leaf))
                {
                    return true;
                }
            }

            if (!File.Exists(ownPath))
            {
                return false;
            }

            using var own = X509Certificate2.CreateFromPem(File.ReadAllText(ownPath));
            return string.Equals(CertificatePinning.SpkiSha256(own), CertificatePinning.SpkiSha256(certificate), StringComparison.Ordinal);
        };
    }

    /// <summary>Per pod (namespace, name): its volumes' used bytes, its PVCs' (by claim) and its containers' writable layers.</summary>
    internal static JsonObject Summarize(JsonObject summary, string? node)
    {
        var pods = new JsonArray();
        foreach (var pod in summary["pods"]?.AsArray() ?? [])
        {
            var volumes = new JsonObject();
            var claims = new JsonObject();
            foreach (var volume in pod?["volume"]?.AsArray() ?? [])
            {
                if ((string?)volume?["name"] is not { } name || volume["usedBytes"] is not JsonValue used || !used.TryGetValue<long>(out var bytes))
                {
                    continue;
                }

                if (volume["pvcRef"] is null)
                {
                    volumes[name] = bytes;
                }
                else if ((string?)volume["pvcRef"]?["name"] is { } claim)
                {
                    claims[claim] = bytes;
                }
            }

            var containers = new JsonObject();
            foreach (var container in pod?["containers"]?.AsArray() ?? [])
            {
                if ((string?)container?["name"] is { } name && container?["rootfs"]?["usedBytes"] is JsonValue used && used.TryGetValue<long>(out var bytes))
                {
                    containers[name] = bytes;
                }
            }

            pods.Add((JsonNode)new JsonObject
            {
                ["namespace"] = (string?)pod?["podRef"]?["namespace"],
                ["name"] = (string?)pod?["podRef"]?["name"],
                ["volumes"] = volumes,
                ["claims"] = claims,
                ["containers"] = containers,
            });
        }

        return new JsonObject { ["node"] = node, ["pods"] = pods };
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    /// <summary>Each line of a read's log with the volume it is about.</summary>
    private sealed class PrefixedWriter(TextWriter inner, string prefix) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value) => inner.WriteLine(prefix + value);

        public override void Write(char value) => inner.Write(value);
    }
}

/// <summary>
/// <c>backdafuckup-mover node-request</c>: what the server execs in a node agent's pod. Reads one JSON request (one line)
/// from standard input – never from its arguments, which the API server's audit log keeps –, hands it to the agent over its socket and prints the agent's JSON answer; exit code 1 when it has an
/// error.
/// </summary>
internal static class NodeRequest
{
    /// <summary>Answers as they are read in logs: no escaped apostrophes.</summary>
    internal static readonly System.Text.Json.JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(TextReader input, TextWriter output, string socket, CancellationToken cancellationToken)
    {
        // One line: an exec through the API server cannot always close standard input.
        var request = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty).Trim();
        JsonObject answer;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), cancellationToken).ConfigureAwait(false);
            await using var stream = new NetworkStream(client, ownsSocket: false);
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                await writer.WriteLineAsync(request).ConfigureAwait(false);
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            answer = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line
                ? JsonNode.Parse(line) as JsonObject ?? new JsonObject { ["error"] = $"The node agent answered something else than a JSON object: {line}" }
                : new JsonObject { ["error"] = "The node agent closed the connection without an answer." };
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Text.Json.JsonException)
        {
            answer = new JsonObject { ["error"] = $"The node agent's socket {socket} does not answer: {ex.Message}" };
        }

        await output.WriteLineAsync(answer.ToJsonString(Json)).ConfigureAwait(false);
        return answer["error"] is null ? 0 : 1;
    }
}
