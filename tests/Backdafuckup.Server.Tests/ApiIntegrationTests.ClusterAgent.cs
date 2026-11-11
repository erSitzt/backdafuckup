using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Mover;
using Backdafuckup.Server.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// A cluster the server reaches through its agent: the agent (the mover program in agent mode) dials out over WebSockets
/// and carries the client's TLS connections to the API server, here a TLS server in front of the fake API whose
/// certificate names only kubernetes.default.svc.
/// </summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_cluster_behind_its_agent_is_read_and_backed_up_through_the_agents_tunnel()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "Create", null, null, null), Ct));
        var volume = new byte[8 * 1024 * 1024];
        new Random(11).NextBytes(volume.AsSpan(0, 64 * 1024));
        var device = Path.Combine(_root, "edge-volume.img");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(device, volume, Ct);
        var movers = new List<string>();
        SetUpCluster(device, movers, []);

        // The API server: TLS with a certificate of the cluster CA for kubernetes.default.svc only, the agent's token required.
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=rke2-server-ca", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        await using var api = await StartTlsApiAsync(ca, caKey);
        _cluster.RequiredToken = "sa-token-1";
        _clusterOverNetwork = true;

        // Added through an agent: nothing to test yet, the manifest brings the agent with the connect token.
        var created = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "Kubernetes", Access = "Agent", DisplayName = "rke2-edge", Address = string.Empty, UserName = string.Empty, Password = string.Empty,
        }, Ct));
        Assert.Equal((false, "Unknown"), (created.Agent!.Online, created.Status));
        var manifest = await client.GetStringAsync($"/api/kubernetes/setup.yaml?connectionId={created.Id}", Ct);
        Assert.Contains("kind: Deployment", manifest, StringComparison.Ordinal);
        Assert.Contains("runAsNonRoot: true", manifest, StringComparison.Ordinal);
        Assert.Contains($"value: \"{created.Id:D}\"", manifest, StringComparison.Ordinal);
        Assert.Matches("image: \"registry.test:8443/backdafuckup/mover@sha256:[0-9a-f]{64}\"", manifest);
        Assert.Contains("type: kubernetes.io/dockerconfigjson", manifest, StringComparison.Ordinal);
        var token = Regex.Match(manifest, "  token: \"([A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        // A wrong connect token is refused.
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => ConnectAgentSocketAsync(new Uri($"wss://localhost/{ClusterTunnel.ControlPath(created.Id)}"), "not-the-token", Ct));
        Assert.Contains("401", refused.Message, StringComparison.Ordinal);

        // The agent, with the ServiceAccount's token and CA where the kubelet puts them.
        var account = Path.Combine(_root, "serviceaccount");
        Directory.CreateDirectory(account);
        await File.WriteAllTextAsync(Path.Combine(account, "token"), "sa-token-1", Ct);
        await File.WriteAllTextAsync(Path.Combine(account, "ca.crt"), ca.ExportCertificatePem(), Ct);
        var log = new StringWriter();
        var options = new ClusterAgentOptions(new Uri("https://localhost/"), created.Id, token, [], "127.0.0.1", api.Port, account, "backdafuckup-agent-7c9f", "agent-1");
        using var stopAgent = new CancellationTokenSource();
        var agent = new ClusterAgent(
            options,
            (uri, ct) => ConnectAgentSocketAsync(uri, token, ct),
            async ct =>
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, api.Port, ct);
                return new TcpStream(tcp);
            },
            TextWriter.Synchronized(log)) { TokenCheck = TimeSpan.FromMilliseconds(200) };
        var running = agent.RunAsync(stopAgent.Token);

        // Connected: the server reads the cluster through the tunnel (platform and inventory) with the agent's token.
        var connected = await WaitAsync(async () => await client.GetFromJsonAsync<ConnectionDto>($"/api/connections/{created.Id}", Ct) is { Agent.Online: true, Status: "Connected" } c ? c : null);
        Assert.Equal(("backdafuckup-agent-7c9f", "agent-1", $"127.0.0.1:{api.Port}"), (connected.Agent!.Pod, connected.Agent.Node, connected.Agent.ApiServer));
        var inventory = await WaitAsync(async () => await client.GetFromJsonAsync<List<InventoryItemDto>>($"/api/connections/{created.Id}/inventory", Ct) is { Count: > 0 } items ? items : null);
        Assert.Contains(inventory, i => i.Kind == "Namespace" && i.NativeId == "bdfu-spike" && i.Properties["snapshotVolumes"] == "1");
        Assert.True(api.Requests > 0, "the requests went over TLS to the API server");

        // A backup through the tunnel; the mover pods send the volume to the server directly.
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Edge namespaces", ConnectionId = created.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("bdfu-spike", "bdfu-spike")], RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        var session = await WaitForSessionAsync(client, backup.SessionId);
        var sessionLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{backup.SessionId}/log", Ct))!.Select(l => l.Message));
        Assert.True(session.Session.State == "Succeeded", session.Session.Summary + "\n" + sessionLog + "\n" + log);
        Assert.Single(movers);
        AssertClusterClean();

        // A renewed ServiceAccount token reaches the server; the old one no longer works in the cluster.
        await File.WriteAllTextAsync(Path.Combine(account, "token"), "sa-token-2", Ct);
        _cluster.RequiredToken = "sa-token-2";
        await WaitAsync(async () => (await client.PostAsync($"/api/connections/{created.Id}/rescan", null, Ct)).IsSuccessStatusCode ? true : (bool?)null, TimeSpan.FromSeconds(20));

        // The agent stops: the cluster cannot be opened, and says why.
        await stopAgent.CancelAsync();
        await running;
        var offline = await WaitAsync(async () => await client.GetFromJsonAsync<ConnectionDto>($"/api/connections/{created.Id}", Ct) is { Agent.Online: false } c ? c : null);
        Assert.NotNull(offline.Agent!.LastContactUtc);
        var rescan = await client.PostAsync($"/api/connections/{created.Id}/rescan", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, rescan.StatusCode);
        Assert.Contains("The agent of cluster rke2-edge is not connected to this server", await rescan.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        _clusterOverNetwork = false;
    }

    [Fact]
    public async Task A_direct_cluster_can_be_switched_to_its_agent_and_keeps_its_jobs()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        await Ok(client.PutAsJsonAsync("/api/kubernetes/mover", new SaveMoverImageDto("registry.test:8443", "backdafuckup/mover", "bdfu-push", "secret-1", "None", null, null, null), Ct));
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=rke2-server-ca", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var cluster = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "Kubernetes", DisplayName = "rke2-test", Address = "10.26.1.10", Port = 6443, UserName = "backdafuckup", Password = "token-for-tests", CaCertificatePem = ca.ExportCertificatePem(),
        }, Ct));
        Assert.Null(cluster.Agent);
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Namespaces", ConnectionId = cluster.Id, RepositoryId = repository.Id, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto("kube-system", "kube-system")], RetryCount = 0,
        }, Ct));

        var switched = await Read<ConnectionDto>(client.PostAsync($"/api/connections/{cluster.Id}/agent", null, Ct));
        Assert.Equal((cluster.Id, false, "kubernetes.default.svc", "agent"), (switched.Id, switched.Agent!.Online, switched.Address, switched.UserName));
        Assert.Empty(switched.Hosts);
        var manifest = await client.GetStringAsync($"/api/kubernetes/setup.yaml?connectionId={cluster.Id}", Ct);
        Assert.Contains("name: backdafuckup-agent", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("dockerconfigjson", manifest, StringComparison.Ordinal); // pull secret mode None
        Assert.Contains((await client.GetFromJsonAsync<List<JobDto>>("/api/jobs", Ct))!, j => j.Id == job.Id && j.ConnectionId == cluster.Id);

        // Without its agent the cluster cannot be read, and the message says where the agent should be.
        var rescan = await client.PostAsync($"/api/connections/{cluster.Id}/rescan", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, rescan.StatusCode);
        Assert.Contains("is not connected to this server yet", await rescan.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private async Task<WebSocket> ConnectAgentSocketAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        var sockets = _factory!.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        return await sockets.ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, cancellationToken);
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, TimeSpan? limit = null)
        where T : class
    {
        var until = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(200, Ct);
        }
    }

    private static async Task<bool> WaitAsync(Func<Task<bool?>> probe, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            if (await probe() == true)
            {
                return true;
            }

            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(500, Ct);
        }
    }

    /// <summary>The fake API behind TLS on a loopback port, with a server certificate of <paramref name="ca"/> for kubernetes.default.svc.</summary>
    private async Task<TlsApi> StartTlsApiAsync(X509Certificate2 ca, ECDsa caKey)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=kube-apiserver", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(ClusterTunnel.ApiServerName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = request.Create(ca.SubjectName, X509SignatureGenerator.CreateForECDsa(caKey), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3, 4]);
        using var withKey = signed.CopyWithPrivateKey(key);
        var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var forward = new HttpMessageInvoker(_cluster.CreateHandler());
        var counter = new TlsApi(app, certificate);
        app.Run(async http =>
        {
            Interlocked.Increment(ref counter.RequestCount);
            using var message = new HttpRequestMessage(new HttpMethod(http.Request.Method), $"https://{ClusterTunnel.ApiServerName}{http.Request.Path}{http.Request.QueryString}");
            if (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0)
            {
                var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body);
                message.Content = new ByteArrayContent(body.ToArray());
                if (http.Request.ContentType is { } type)
                {
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                }
            }

            foreach (var header in http.Request.Headers.Where(h => h.Key is "Authorization" or "Accept"))
            {
                message.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value!);
            }

            using var response = await forward.SendAsync(message, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                http.Response.ContentType = contentType.ToString();
            }

            await response.Content.CopyToAsync(http.Response.Body);
        });
        await app.StartAsync(Ct);
        counter.Port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
        return counter;
    }

    private sealed class TlsApi(WebApplication app, X509Certificate2 certificate) : IAsyncDisposable
    {
        public int RequestCount;

        public int Port { get; set; }

        public int Requests => Volatile.Read(ref RequestCount);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }

    /// <summary>A TCP connection's stream that closes its client with it.</summary>
    private sealed class TcpStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
