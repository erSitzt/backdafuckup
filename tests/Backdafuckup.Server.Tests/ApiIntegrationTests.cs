using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Testing;
using Backdafuckup.Testing.Xapi;
using Backdafuckup.Xen;
using Backdafuckup.Xen.Xapi;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace Backdafuckup.Server.Tests;

/// <summary>The REST API end to end: setup, login, CSRF, pool, repository, job, backup and restore.</summary>
[Trait("Category", "Integration")]
public sealed class ApiIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bdfu-api-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeXenPool _pool = new();
    private WebApplicationFactory<Program>? _factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Ct);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Catalog", _postgres.GetConnectionString());
            builder.UseSetting("Paths:ConfigDirectory", _root);
            builder.UseSetting("Paths:DataDirectory", Path.Combine(_root, "data"));
            builder.UseSetting("Paths:LogDirectory", Path.Combine(_root, "logs"));
            builder.UseSetting("Agent:PollHold", "00:00:01");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHypervisorProvider>();
                services.AddSingleton<IHypervisorProvider>(new XenProvider(
                    new XenOptions { CbtBitOrder = CbtBitOrder.MostSignificantBitFirst, XapiRetryAttempts = 1, XapiRetryDelay = TimeSpan.FromMilliseconds(10) },
                    (_, host) => new XapiTransport(new Uri($"https://{host}/"), _pool.CreateHandler())));
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _pool.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Complete_workflow_through_the_api()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });

        // Anonymous access is limited to setup/login.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/jobs", Ct)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<SetupStatusDto>("/api/setup/status", Ct))!.SetupRequired);

        // Setup with the token, then login. Requests without the CSRF header are rejected.
        var token = _factory.Services.GetRequiredService<SetupTokenService>().EnsureToken();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin", "correct horse battery"), Ct)).StatusCode);
        client.DefaultRequestHeaders.Add(ApiHeaders.Csrf, "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/setup", new SetupRequestDto("wrong", "admin", "correct horse battery"), Ct)).StatusCode);

        // A rejected password must not burn the token.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin", "too short"), Ct)).StatusCode);
        await Ok(client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin", "correct horse battery"), Ct));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin2", "correct horse battery"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", "wrong password!"), Ct)).StatusCode);
        await Ok(client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", "correct horse battery"), Ct));
        Assert.Equal("admin", (await client.GetFromJsonAsync<CurrentUserDto>("/api/auth/me", Ct))!.UserName);

        // Pool, inventory, repository.
        var (vmUuid, disks) = _pool.AddVm("mail01", 4L * 1024 * 1024);
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            DisplayName = "Lab pool",
            Address = "xcp1",
            UserName = "root",
            Password = "secret",
        }, Ct));
        Assert.Equal("lab-pool", connection.PlatformName);
        var inventory = await client.GetFromJsonAsync<List<InventoryItemDto>>($"/api/connections/{connection.Id}/inventory", Ct);
        Assert.Contains(inventory!, i => i.Kind == "Vm" && i.NativeId == vmUuid);

        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));

        // Job, run, wait.
        var created = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Daily",
            ConnectionId = connection.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vmUuid, "mail01")],
            RetryCount = 0,
        }, Ct));
        var job = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{created.Id}", Ct);
        Assert.NotNull(job!.NextRunUtc);

        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{created.Id}/run", new RunJobDto(), Ct));
        var session = await WaitForSessionAsync(client, started.SessionId);
        Assert.Equal("Succeeded", session.Session.State);
        Assert.Single(session.Tasks);
        var log = await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct);
        Assert.Contains(log!, l => l.Message.Contains("Backup finished", StringComparison.Ordinal) && l.Message.Contains("dedupe", StringComparison.Ordinal));

        // Transfer statistics: per disk, per VM and for the session; the categories add up to the processed data.
        var taskMetrics = Assert.IsType<TransferMetricsDto>(session.Tasks[0].Metrics);
        var diskMetrics = Assert.Single(taskMetrics.Disks!);
        Assert.Equal("Done", diskMetrics.State);
        Assert.Equal("nbd", diskMetrics.Transport);
        Assert.Equal(4L * 1024 * 1024, diskMetrics.Metrics.ProcessedBytes);
        Assert.Equal(
            diskMetrics.Metrics.ProcessedBytes,
            diskMetrics.Metrics.UnchangedBytes + diskMetrics.Metrics.ZeroBytes + diskMetrics.Metrics.DedupedBytes + diskMetrics.Metrics.NewDataBytes);
        Assert.True(diskMetrics.Metrics.StoredBytes > 0 && diskMetrics.Metrics.Seconds > 0);
        var sessionMetrics = Assert.IsType<TransferMetricsDto>(session.Session.Metrics);
        Assert.Equal(taskMetrics.ProcessedBytes, sessionMetrics.ProcessedBytes);
        Assert.Equal(taskMetrics.StoredBytes, sessionMetrics.StoredBytes);
        Assert.Equal(0, sessionMetrics.CurrentBytesPerSecond);
        Assert.NotNull(sessionMetrics.CompressionRatio);

        var points = await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={created.Id}", Ct);
        var point = Assert.Single(points!);
        Assert.True((await Read<VerifyResultDto>(client.PostAsync($"/api/restore-points/{point.Id}/verify?full=true", null, Ct))).Ok);

        // Restore to an alternate location.
        var restore = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id,
            Mode = RestoreModeDto.AlternateLocation,
            VmName = "mail01-copy",
        }, Ct));
        var restored = await WaitForSessionAsync(client, restore.SessionId);
        Assert.Equal("Succeeded", restored.Session.State);
        Assert.Equal(4L * 1024 * 1024, restored.Session.Metrics?.ProcessedBytes);
        Assert.Contains(_pool.Records("VM"), v => XapiRecord.Str(v, "name_label") == "mail01-copy");

        var dashboard = await client.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct);
        Assert.Equal(1, dashboard!.JobsSucceeded24h);
        Assert.Equal(1, dashboard.ProtectedVms);
        _ = disks;
    }

    [Fact]
    public async Task Agents_enroll_with_the_secret_and_receive_commands_through_the_long_poll()
    {
        // The agent interface needs neither a sign-in nor the CSRF header.
        var agentHttp = _factory!.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var secret = await _factory.Services.GetRequiredService<AgentSettingsStore>().GetEnrollmentSecretAsync(Ct);
        var host = new AgentHostInfo { HostName = "SQL01", BiosSerial = Guid.NewGuid().ToString(), Build = AgentProtocol.BuildNet10 };
        Assert.Equal(HttpStatusCode.Forbidden, (await agentHttp.PostAsJsonAsync("/api/agent/enroll", new AgentEnrollRequest { EnrollmentSecret = "wrong", Host = host }, Ct)).StatusCode);
        var enrolled = await Read<AgentEnrollResponse>(agentHttp.PostAsJsonAsync("/api/agent/enroll", new AgentEnrollRequest { EnrollmentSecret = secret, Host = host }, Ct));
        Assert.Equal(64, enrolled.AgentKey.Length);

        Assert.Equal(HttpStatusCode.Unauthorized, (await agentHttp.PostAsJsonAsync("/api/agent/poll", new AgentPollRequest { Host = host }, Ct)).StatusCode);
        agentHttp.DefaultRequestHeaders.Add(AgentProtocol.KeyHeader, enrolled.AgentKey);
        Assert.Equal(HttpStatusCode.NoContent, (await agentHttp.PostAsJsonAsync("/api/agent/poll", new AgentPollRequest { Host = host }, Ct)).StatusCode);

        // A command of the server arrives with the next poll; the agent's result reaches the waiting caller.
        var registry = _factory.Services.GetRequiredService<AgentRegistry>();
        var sent = registry.SendAsync(enrolled.AgentId, new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.GetInfo }, TimeSpan.FromSeconds(30), Ct);
        var poll = await Read<AgentPollResponse>(agentHttp.PostAsJsonAsync("/api/agent/poll", new AgentPollRequest { Host = host }, Ct));
        Assert.Equal(AgentCommandKinds.GetInfo, poll.Command!.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await agentHttp.PostAsJsonAsync("/api/agent/result", new AgentCommandResult { CommandId = poll.Command.Id, Success = true, Info = host }, Ct)).StatusCode);
        Assert.True((await sent).Success);
        Assert.True(_factory.Services.GetRequiredService<AgentService>().IsOnline(enrolled.AgentId));

        // The console sees the agent, mapped to the VM by its BIOS serial number.
        var console = await SignedInClientAsync();
        var agent = Assert.Single((await console.GetFromJsonAsync<List<AgentDto>>("/api/agents", Ct))!);
        Assert.Equal("SQL01", agent.HostName);
        Assert.Equal(host.BiosSerial, agent.VmNativeId);
        Assert.True(agent.Online);
        Assert.Equal(HttpStatusCode.NoContent, (await console.DeleteAsync($"/api/agents/{agent.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentHttp.PostAsJsonAsync("/api/agent/poll", new AgentPollRequest { Host = host }, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_agent_runner_freezes_and_thaws_through_the_real_server_over_http()
    {
        // The real agent loop (enroll, long poll, commands in the background, results) against the real endpoints;
        // only VSS is simulated. JSON goes through both serializers.
        var directory = Path.Combine(_root, "agent");
        Directory.CreateDirectory(directory);
        var paths = new AgentPaths(directory);
        var secret = await _factory!.Services.GetRequiredService<AgentSettingsStore>().GetEnrollmentSecretAsync(Ct);
        var vmUuid = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(
            new AgentConfig { ServerUrl = "https://localhost/", EnrollmentSecret = secret }, Backdafuckup.Agent.Core.AgentJson.Options), Ct);
        var vss = new SimulatedVss();
        var runner = new AgentRunner(paths, new SimulatedHost(vmUuid), vss, new AgentLog(paths.LogFile), _ => _factory.Server.CreateHandler(), TimeSpan.FromMilliseconds(200));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = Task.Run(() => runner.RunAsync(stop.Token), Ct);

        var agents = _factory.Services.GetRequiredService<AgentService>();
        var agent = await agents.WaitForVmAsync(vmUuid, TimeSpan.FromSeconds(30), Ct) ?? throw new TimeoutException("The agent did not come online.");
        var quiescer = new AgentQuiescer(_factory.Services.GetRequiredService<AgentRegistry>(), agents.Options, agent.Id, "agent on SQL01 (10.0.0.5)");
        var log = new List<string>();

        await using (var freeze = await quiescer.FreezeAsync(new ListLog(log), Ct))
        {
            Assert.True(vss.Frozen);
            var result = await freeze.ThawAsync(Ct);
            Assert.True(result.ApplicationConsistent);
            Assert.Equal(TimeSpan.FromMilliseconds(1200), result.HeldTime);
        }

        Assert.False(vss.Frozen);
        Assert.Equal(["prepare", "freeze", "thaw"], vss.Calls);
        Assert.Equal("shop", Assert.Single(quiescer.Prepared!.SqlDatabases).Database);
        Assert.Contains(log, l => l.Contains("1 SQL database(s) in SQLEXPRESS", StringComparison.Ordinal));

        await stop.CancelAsync();
        Assert.Equal(0, await running);
    }

    [Fact]
    public async Task File_level_restore_browses_a_restore_point_and_downloads_files()
    {
        var client = await SignedInClientAsync();

        // A VM whose disk holds the NTFS test image, backed up once.
        var image = await File.ReadAllBytesAsync(FsFixtures.Image("ntfs-gpt"), Ct);
        var (vmUuid, disks) = _pool.AddVm("files01", image.LongLength);
        _pool.WriteDisk(disks[0], 0, image);
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Files",
            ConnectionId = connection.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vmUuid, "files01")],
            RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);

        // Open the browser: one NTFS volume; opening again reuses it.
        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = Assert.Single(mount.Volumes, v => v.Readable);
        Assert.Equal("NTFS", volume.FileSystem);
        Assert.Equal("windows", volume.Family);
        Assert.Equal(mount.Id, (await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct))).Id);

        // Folders first, hidden files included; paging.
        var root = await client.GetFromJsonAsync<GuestEntryPageDto>($"/api/file-restore/mounts/{mount.Id}/entries?volume={Uri.EscapeDataString(volume.Id)}", Ct);
        Assert.Equal("directory", root!.Entries[0].Kind);
        Assert.Contains(root.Entries, e => e.Name == "hidden.txt" && e.Attributes!.Contains("Hidden", StringComparison.Ordinal));
        var many = await client.GetFromJsonAsync<GuestEntryPageDto>($"/api/file-restore/mounts/{mount.Id}/entries?volume={Uri.EscapeDataString(volume.Id)}&path=many&offset=2990&limit=50", Ct);
        Assert.Equal(3000, many!.Total);
        Assert.Equal(10, many.Entries.Count);

        // Downloads: content and name; a missing file and a path with ".." are rejected.
        var file = await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path={Uri.EscapeDataString("Ümlaut äöü ß.txt")}", Ct);
        Assert.True(file.IsSuccessStatusCode, await file.Content.ReadAsStringAsync(Ct));
        Assert.Equal("umlaut\n", await file.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Ümlaut äöü ß.txt", file.Content.Headers.ContentDisposition!.FileNameStar);
        var expected = FsFixtures.Manifest("ntfs-gpt.p1").Single(l => l[7] == "pattern-3m.bin")[1];
        var pattern = await client.GetByteArrayAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=pattern-3m.bin", Ct);
        Assert.Equal(expected, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pattern)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=nope.txt", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=folder/../hello.txt", Ct)).StatusCode);

        // While the browser is open, the restore point cannot be deleted.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/restore-points/{point.Id}", Ct)).StatusCode);

        // Closing ends the audit session; the browser is gone.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/file-restore/mounts/{mount.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/entries?volume={Uri.EscapeDataString(volume.Id)}", Ct)).StatusCode);
        var session = await WaitForSessionAsync(client, mount.SessionId);
        Assert.Equal("FileBrowse", session.Session.Kind);
        Assert.Equal("Succeeded", session.Session.State);
        var log = await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{mount.SessionId}/log", Ct);
        Assert.Contains(log!, l => l.Message.StartsWith("Opened by admin", StringComparison.Ordinal));
        Assert.Contains(log!, l => l.Message.Contains("admin downloaded", StringComparison.Ordinal) && l.Message.Contains("pattern-3m.bin", StringComparison.Ordinal));
        Assert.Contains(log!, l => l.Message.StartsWith("File browser closed: closed by admin", StringComparison.Ordinal));
    }

    private sealed class SimulatedVss : IVssBridge
    {
        public List<string> Calls { get; } = [];

        public bool Frozen { get; private set; }

        public string ProviderState => "registered";

        public GuestBackupInfo Prepare(bool copyOnly)
        {
            Calls.Add("prepare");
            Assert.True(copyOnly);
            return new GuestBackupInfo
            {
                Writers = [new GuestWriter { Name = "SqlServerWriter", State = "Stable" }],
                SqlDatabases = [new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "shop", Files = [@"D:\SQL\shop.mdf"] }],
            };
        }

        public FreezeReport Freeze(int holdTimeoutMs)
        {
            Calls.Add("freeze");
            Frozen = true;
            return new FreezeReport { FreezeMs = 300 };
        }

        public ThawReport Thaw()
        {
            Calls.Add("thaw");
            Frozen = false;
            return new ThawReport { Consistent = true, HeldMs = 1200, Writers = [new GuestWriter { Name = "SqlServerWriter", State = "Stable" }] };
        }

        public void Abort()
        {
            Calls.Add("abort");
            Frozen = false;
        }
    }

    private sealed class SimulatedHost(string vmUuid) : IHostInfoSource
    {
        public AgentHostInfo Collect() => new() { HostName = "SQL01", BiosSerial = vmUuid, Build = AgentProtocol.BuildNet10, AgentVersion = "test", IpAddresses = ["10.0.0.5"] };
    }

    private sealed class ListLog(List<string> lines) : Backdafuckup.Core.Operations.IOperationLog
    {
        public void Write(Backdafuckup.Core.Operations.LogSeverity severity, string message, string? key = null) => lines.Add(message);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add(ApiHeaders.Csrf, "1");
        var token = _factory.Services.GetRequiredService<SetupTokenService>().EnsureToken();
        await Ok(client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin", "correct horse battery"), Ct));
        await Ok(client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto("admin", "correct horse battery"), Ct));
        return client;
    }

    private sealed record IdOnly(Guid Id);

    private static async Task<SessionDetailDto> WaitForSessionAsync(HttpClient client, Guid sessionId)
    {
        for (var i = 0; i < 300; i++)
        {
            var detail = await client.GetFromJsonAsync<SessionDetailDto>($"/api/sessions/{sessionId}", Ct);
            if (detail!.Session.State is not ("Queued" or "Running" or "Stopping"))
            {
                return detail;
            }

            await Task.Delay(200, Ct);
        }

        throw new TimeoutException("Session did not finish.");
    }

    private static async Task Ok(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static async Task<T> Read<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
