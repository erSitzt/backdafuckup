using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Tests;

public sealed class AgentCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-agent-tests", Guid.NewGuid().ToString("N"));

    public AgentCoreTests() => Directory.CreateDirectory(_root);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("rsa")]
    [InlineData("ecdsa")]
    public void The_pin_of_the_own_der_reader_equals_the_pin_of_the_framework(string kind)
    {
        using var certificate = kind == "rsa"
            ? new CertificateRequest("CN=backup.example.local", RSA.Create(3072), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5))
            : new CertificateRequest("CN=backup.example.local", ECDsa.Create(ECCurve.NamedCurves.nistP384), HashAlgorithmName.SHA384).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5));

        var expected = Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

        Assert.Equal(expected, ServerCertificatePin.SpkiSha256(certificate.RawData));
        Assert.Throws<CryptographicException>(() => ServerCertificatePin.SpkiSha256(certificate.RawData[..40]));
    }

    [Fact]
    public void Commands_must_come_in_order_and_a_failure_aborts_the_backup()
    {
        var vss = new FakeVss();
        var executor = new CommandExecutor(vss, new FakeHost(), new AgentLog(Path.Combine(_root, "agent.log")));
        var operation = Guid.NewGuid();

        // Freeze without prepare is refused; nothing is frozen.
        var early = executor.Execute(Command(AgentCommandKinds.Freeze, operation));
        Assert.False(early.Success);
        Assert.Contains("does not fit the state", early.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("freeze", vss.Calls);

        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        Assert.False(executor.Execute(Command(AgentCommandKinds.Thaw, operation)).Success); // thaw before freeze

        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        Assert.False(executor.Execute(Command(AgentCommandKinds.Freeze, Guid.NewGuid())).Success); // other operation
        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        Assert.True(executor.Execute(Command(AgentCommandKinds.Freeze, operation)).Success);
        var thaw = executor.Execute(Command(AgentCommandKinds.Thaw, operation));
        Assert.True(thaw.Success);
        Assert.True(thaw.Thaw!.Consistent);

        // A failing freeze reports the HRESULT and aborts the prepared backup.
        vss.FailFreeze = true;
        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        var failed = executor.Execute(Command(AgentCommandKinds.Freeze, operation));
        Assert.False(failed.Success);
        Assert.Equal(unchecked((int)0x80042314), failed.ErrorCode);
        Assert.Equal("abort", vss.Calls[^1]);
    }

    [Fact]
    public void Nothing_is_written_to_disk_while_the_guest_is_frozen()
    {
        // A write to the frozen volume would block until Windows releases the hold (10 s): the agent would freeze itself.
        var path = Path.Combine(_root, "agent.log");
        var log = new AgentLog(path);
        var vss = new FakeVss();
        var executor = new CommandExecutor(vss, new FakeHost(), log);
        var operation = Guid.NewGuid();
        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        var beforeFreeze = File.ReadAllText(path);

        Assert.True(executor.Execute(Command(AgentCommandKinds.Freeze, operation)).Success);
        Assert.True(log.IsPaused);
        Assert.Equal(beforeFreeze, File.ReadAllText(path));

        Assert.True(executor.Execute(Command(AgentCommandKinds.Thaw, operation)).Success);
        Assert.False(log.IsPaused);
        var afterThaw = File.ReadAllText(path);
        Assert.Contains("writes held after", afterThaw, StringComparison.Ordinal);
        Assert.Contains("thawed after", afterThaw, StringComparison.Ordinal);

        // A failed freeze writes again at once.
        vss.FailFreeze = true;
        Assert.True(executor.Execute(Command(AgentCommandKinds.PrepareBackup, operation)).Success);
        Assert.False(executor.Execute(Command(AgentCommandKinds.Freeze, operation)).Success);
        Assert.False(log.IsPaused);
        Assert.Contains("Freeze failed", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_key_and_log_are_not_program_files()
    {
        foreach (var name in new[] { "agent.json", "agent.key", "agent.log", "agent.log.1", "backdafuckup-agent.exe.old", "x.tmp", "version.txt" })
        {
            Assert.False(AgentFiles.IsProgramFile(name), name);
        }

        Assert.True(AgentFiles.IsProgramFile("backdafuckup-agent.exe"));
        Assert.True(AgentFiles.IsProgramFile("bdfu-vss.dll"));
    }

    [Fact]
    public async Task A_self_update_swaps_in_verified_files_and_requests_a_restart()
    {
        var paths = await InstalledAgentAsync();
        var newExe = Encoding.UTF8.GetBytes("new agent build");
        var server = new FakeServer
        {
            Files = { ["backdafuckup-agent.exe"] = newExe },
            Offer = new AgentUpdateOffer
            {
                Build = AgentProtocol.BuildNet10,
                Version = "0.2.0",
                Files = [new AgentFile { Name = "backdafuckup-agent.exe", Size = newExe.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(newExe)) }],
            },
        };
        server.Offer.FilesHash = AgentFiles.Hash(server.Offer.Files);
        var runner = new AgentRunner(paths, new FakeHost(), new FakeVss(), new AgentLog(paths.LogFile), _ => server, TimeSpan.FromMilliseconds(50));

        var code = await runner.RunAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(AgentProtocol.RestartExitCode, code);
        Assert.Equal(newExe, await File.ReadAllBytesAsync(Path.Combine(paths.BaseDirectory, "backdafuckup-agent.exe"), Ct));
        Assert.True(File.Exists(Path.Combine(paths.BaseDirectory, "backdafuckup-agent.exe.old")));
        Assert.Equal("agent-key-1", (await File.ReadAllTextAsync(paths.KeyFile, Ct)).Trim());
    }

    [Fact]
    public async Task An_update_with_a_wrong_checksum_is_not_installed()
    {
        var paths = await InstalledAgentAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = new FakeServer
        {
            Files = { ["backdafuckup-agent.exe"] = Encoding.UTF8.GetBytes("tampered") },
            Offer = new AgentUpdateOffer { Build = AgentProtocol.BuildNet10, FilesHash = "x", Files = [new AgentFile { Name = "backdafuckup-agent.exe", Size = 8, Sha256 = new string('0', 64) }] },
            AfterPolls = (count) => { if (count >= 3) stop.Cancel(); },
        };
        var runner = new AgentRunner(paths, new FakeHost(), new FakeVss(), new AgentLog(paths.LogFile), _ => server, TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, await runner.RunAsync(stop.Token));

        Assert.Equal("old agent build", await File.ReadAllTextAsync(Path.Combine(paths.BaseDirectory, "backdafuckup-agent.exe"), Ct));
        Assert.Contains("does not match its checksum", await File.ReadAllTextAsync(paths.LogFile, Ct), StringComparison.Ordinal);
    }

    private async Task<AgentPaths> InstalledAgentAsync()
    {
        var paths = new AgentPaths(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "backdafuckup-agent.exe"), "old agent build", Ct);
        await File.WriteAllTextAsync(paths.ConfigFile, JsonSerializer.Serialize(new AgentConfig { ServerUrl = "https://backup.example.local:9555", EnrollmentSecret = "secret" }, AgentJson.Options), Ct);
        return paths;
    }

    private static AgentCommand Command(string kind, Guid operation) => new() { Id = Guid.NewGuid(), Kind = kind, OperationId = operation, TimeoutMs = 30_000 };

    private sealed class FakeVss : IVssBridge
    {
        public List<string> Calls { get; } = [];

        public bool FailFreeze { get; set; }

        public string ProviderState => "registered";

        public GuestBackupInfo Prepare(bool copyOnly)
        {
            Calls.Add("prepare");
            return new GuestBackupInfo();
        }

        public FreezeReport Freeze(int holdTimeoutMs)
        {
            Calls.Add("freeze");
            return FailFreeze ? throw new VssException("Windows released the writes", unchecked((int)0x80042314)) : new FreezeReport { FreezeMs = 10 };
        }

        public ThawReport Thaw()
        {
            Calls.Add("thaw");
            return new ThawReport { Consistent = true, HeldMs = 20 };
        }

        public void Abort() => Calls.Add("abort");
    }

    private sealed class FakeHost : IHostInfoSource
    {
        public AgentHostInfo Collect() => new() { HostName = "SQL01", Build = AgentProtocol.BuildNet10, FilesHash = "old" };
    }

    /// <summary>The server side of enroll, poll and file download, enough for the self-update.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private int _polls;

        public Dictionary<string, byte[]> Files { get; } = [];

        public AgentUpdateOffer Offer { get; set; } = new();

        public Action<int>? AfterPolls { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            HttpResponseMessage response;
            if (path == AgentProtocol.EnrollPath)
            {
                response = Json(new AgentEnrollResponse { AgentId = Guid.NewGuid(), AgentKey = "agent-key-1" });
            }
            else if (path == AgentProtocol.PollPath)
            {
                Assert.Equal("agent-key-1", request.Headers.GetValues(AgentProtocol.KeyHeader).Single());
                AfterPolls?.Invoke(Interlocked.Increment(ref _polls));
                response = Json(new AgentPollResponse { Update = Offer });
            }
            else if (path.StartsWith(AgentProtocol.FilesPath + "/", StringComparison.Ordinal))
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[Path.GetFileName(path)]) };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, value.GetType(), AgentJson.Options), Encoding.UTF8, "application/json") };
    }
}
