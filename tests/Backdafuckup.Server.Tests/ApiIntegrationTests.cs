using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.FileRestore;
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
    private FakeShareConnector? _shares;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Ct);
        _shares = new FakeShareConnector(Path.Combine(_root, "shares"));
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
                services.RemoveAll<Backdafuckup.Smb.ISmbShareConnector>();
                services.AddSingleton<Backdafuckup.Smb.ISmbShareConnector>(_shares);
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/about", Ct)).StatusCode);
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

        // The version from Directory.Build.props, with the commit the SDK takes from Git.
        var about = (await client.GetFromJsonAsync<AboutDto>("/api/about", Ct))!;
        Assert.Matches(@"^\d+\.\d+\.\d+", about.Version);
        Assert.Equal(typeof(Backdafuckup.Server.Hosting.ProductInfo).Assembly.GetName().Version!.ToString(3), about.Version);
        Assert.NotNull(about.BuildUtc);

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
        var point = await BackUpFixtureAsync(client, "files01", "ntfs-gpt");

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

        // 32 MiB: more than the 4 MiB pipe buffer, so the reader thread has to wait for the response.
        var sparse = await client.GetByteArrayAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=sparse.bin", Ct);
        Assert.Equal(FsFixtures.Manifest("ntfs-gpt.p1").Single(l => l[7] == "sparse.bin")[1], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(sparse)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=nope.txt", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/file-restore/mounts/{mount.Id}/file?volume={Uri.EscapeDataString(volume.Id)}&path=folder/../hello.txt", Ct)).StatusCode);

        // ZIP download: selection → one-time link → archive with the folder tree; the link works only once.
        var volumeId = volume.Id;
        var ticket = await Read<FileDownloadTicketDto>(client.PostAsJsonAsync(
            $"/api/file-restore/mounts/{mount.Id}/downloads", new FileDownloadRequestDto(volumeId, ["folder", "pattern-3m.bin", "hidden.txt"], "Europe/Berlin"), Ct));
        Assert.StartsWith("files01_", ticket.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".zip", ticket.FileName, StringComparison.Ordinal);
        var zipResponse = await client.GetAsync("/" + ticket.Url, Ct);
        Assert.True(zipResponse.IsSuccessStatusCode, await zipResponse.Content.ReadAsStringAsync(Ct));
        Assert.Equal("application/zip", zipResponse.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ticket.FileName, zipResponse.Content.Headers.ContentDisposition!.FileNameStar);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync(Ct))))
        {
            Assert.Equal(["folder/", "folder/sub/", "folder/sub/deep/", "folder/sub/deep/nested.txt", "pattern-3m.bin", "hidden.txt"], zip.Entries.Select(e => e.FullName));
            await using var zipped = zip.GetEntry("pattern-3m.bin")!.Open();
            Assert.Equal(expected, Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(zipped, Ct)));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/" + ticket.Url, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/file-restore/downloads/not-a-token", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/file-restore/mounts/{mount.Id}/downloads", new FileDownloadRequestDto(volumeId, ["hello.txt", "folder/sub"], null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
            $"/api/file-restore/mounts/{mount.Id}/downloads", new FileDownloadRequestDto(volumeId, [], null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
            $"/api/file-restore/mounts/{mount.Id}/downloads", new FileDownloadRequestDto(volumeId, ["nope"], null), Ct)).StatusCode);

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
        Assert.Contains(log!, l => l.Message.StartsWith("admin downloaded folder, pattern-3m.bin, hidden.txt in ", StringComparison.Ordinal) && l.Message.Contains("as ZIP: 3 file(s), 3 folder(s)", StringComparison.Ordinal));
        Assert.Contains(log!, l => l.Message.StartsWith("File browser closed: closed by admin", StringComparison.Ordinal));
    }

    /// <summary>A VM whose disk holds a file system test image, backed up once.</summary>
    private async Task<RestorePointDto> BackUpFixtureAsync(HttpClient client, string vmName, string fixture) =>
        await BackUpImageAsync(client, vmName, await File.ReadAllBytesAsync(FsFixtures.Image(fixture), Ct));

    /// <summary>A VM whose disk holds <paramref name="image"/>, backed up once (with guest processing if an agent is set up).</summary>
    private async Task<RestorePointDto> BackUpImageAsync(HttpClient client, string vmName, byte[] image, Func<string, Task>? beforeBackup = null)
    {
        var (vmUuid, disks) = _pool.AddVm(vmName, image.LongLength);
        _pool.WriteDisk(disks[0], 0, image);
        if (beforeBackup is not null)
        {
            await beforeBackup(vmUuid);
        }
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto { DisplayName = "Lab pool", Address = "xcp1", UserName = "root", Password = "secret" }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Files",
            ConnectionId = connection.Id,
            RepositoryId = repository.Id,
            RetentionPoints = 7,
            Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vmUuid, vmName)],
            RetryCount = 0,
            GuestProcessing = beforeBackup is not null,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        return Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
    }

    [Fact]
    public async Task Sql_databases_of_a_restore_point_are_described_from_the_guest_metadata_and_the_backup()
    {
        var client = await SignedInClientAsync();
        var image = WindowsTestDisk.Create(
            [
                new WindowsTestDisk.Volume('C', 8, new Dictionary<string, byte[]>()),
                new WindowsTestDisk.Volume('D', 12, new Dictionary<string, byte[]>
                {
                    ["SQL/shop.mdf"] = WindowsTestDisk.SqlFile(1, 1 << 20, 0x11),
                    ["SQL/shop_2.ndf"] = WindowsTestDisk.SqlFile(3, 256 << 10, 0x33),
                    ["SQL/docs.mdf"] = WindowsTestDisk.SqlFile(1, 64 << 10, 0x44),
                }),
                new WindowsTestDisk.Volume('L', 8, new Dictionary<string, byte[]>
                {
                    ["Log/shop_log.ldf"] = WindowsTestDisk.SqlFile(2, 512 << 10, 0x22),
                    ["Log/docs_log.ldf"] = WindowsTestDisk.SqlFile(2, 64 << 10, 0x55),
                }),
            ],
            software: root =>
            {
                root.CreateSubKey(@"Microsoft\Microsoft SQL Server\Instance Names\SQL").SetValue("SQLEXPRESS", "MSSQL16.SQLEXPRESS");
                var setup = root.CreateSubKey(@"Microsoft\Microsoft SQL Server\MSSQL16.SQLEXPRESS\Setup");
                setup.SetValue("Version", "16.0.1000.6");
                setup.SetValue("Edition", "Express Edition (64-bit)");
                var server = root.CreateSubKey(@"Microsoft\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQLServer");
                server.SetValue("DefaultData", @"D:\SQL");
                server.SetValue("DefaultLog", @"L:\Log");
                server.SetValue("BackupDirectory", @"C:\Backup");
            },
            system: root => root.CreateSubKey(@"ControlSet001\Services\MSSQL$SQLEXPRESS").SetValue("ObjectName", @"NT Service\MSSQL$SQLEXPRESS"));
        static GuestSqlFile F(string path, string spec, bool recursive = false) => new() { Path = path, Spec = spec, Recursive = recursive };
        FakeGuestAgent? agent = null;
        try
        {
            var point = await BackUpImageAsync(client, "sql01", image, async vmUuid =>
            {
                agent = new FakeGuestAgent(_factory!.Services, vmUuid)
                {
                    SqlDatabases =
                    [
                        // The secondary file is listed first: the primary file is found by its page header.
                        new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "shop", DataFiles = [F(@"D:\SQL\", "shop_2.ndf"), F(@"D:\SQL\", "shop.mdf")], LogFiles = [F(@"L:\Log\", "shop_log.ldf")], InBackup = true },
                        new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "docs", DataFiles = [F(@"D:\SQL\", "docs.mdf"), F(@"D:\SQL\fs\", "*", recursive: true)], LogFiles = [F(@"L:\Log\", "docs_log.ldf")], InBackup = true },
                        new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "gone", DataFiles = [F(@"D:\SQL\", "gone.mdf")], LogFiles = [F(@"E:\Log\", "gone_log.ldf")], InBackup = true },
                        new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "master", DataFiles = [F(@"C:\Program Files\SQL\", "master.mdf")], LogFiles = [F(@"C:\Program Files\SQL\", "mastlog.ldf")], InBackup = true },
                        // Guest metadata from an older agent: a flat file list.
                        new GuestSqlDatabase { Instance = "SQLEXPRESS", Database = "legacy", Files = [@"D:\SQL\shop.mdf", @"L:\Log\shop_log.ldf"] },
                    ],
                };
                await agent.StartAsync(Ct);
            });
            Assert.Equal(5, point.SqlDatabaseCount);

            var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
            Assert.Equal(["C:", "D:", "L:"], mount.Volumes.Where(v => v.Readable).Select(v => v.MountPoint).Order().ToList());
            var sql = (await client.GetFromJsonAsync<SqlMountDto>($"/api/file-restore/mounts/{mount.Id}/sql", Ct))!;
            Assert.Empty(sql.Warnings);

            var instance = Assert.Single(sql.Instances);
            Assert.Equal(new SqlInstanceDto("SQLEXPRESS", "MSSQL16.SQLEXPRESS", "16.0.1000.6", "SQL Server 2022", "Express Edition (64-bit)", "MSSQL$SQLEXPRESS",
                @"NT Service\MSSQL$SQLEXPRESS", @"D:\SQL", @"L:\Log", @"C:\Backup"), instance);

            var shop = sql.Databases.Single(d => d.Name == "shop");
            Assert.True(shop.Restorable, string.Join("; ", shop.Problems));
            Assert.Equal("application", shop.Consistency);
            Assert.Equal([@"D:\SQL\shop.mdf", @"D:\SQL\shop_2.ndf", @"L:\Log\shop_log.ldf"], shop.Files.Select(f => f.Path).ToList());
            Assert.True(shop.Files[0].Primary);
            Assert.Equal(["data", "data", "log"], shop.Files.Select(f => f.Kind).ToList());
            Assert.Equal([1L << 20, 256L << 10, 512L << 10], shop.Files.Select(f => f.SizeBytes!.Value).ToList());
            Assert.Equal((1L << 20) + (256L << 10) + (512L << 10), shop.TotalBytes);
            Assert.Equal("/SQL/shop.mdf", shop.Files[0].GuestPath);

            var docs = sql.Databases.Single(d => d.Name == "docs");
            Assert.False(docs.Restorable);
            Assert.Contains(docs.Problems, p => p.Contains("FILESTREAM", StringComparison.Ordinal));

            var gone = sql.Databases.Single(d => d.Name == "gone");
            Assert.Contains(gone.Problems, p => p.Contains("gone.mdf: it does not exist in the restore point", StringComparison.Ordinal));
            Assert.Contains(gone.Problems, p => p.Contains("no volume with drive letter E:", StringComparison.Ordinal));

            Assert.Contains(sql.Databases.Single(d => d.Name == "master").Problems, p => p.Contains("system database", StringComparison.Ordinal));

            var legacy = sql.Databases.Single(d => d.Name == "legacy");
            Assert.True(legacy.Restorable, string.Join("; ", legacy.Problems));
            Assert.Equal(["data", "log"], legacy.Files.Select(f => f.Kind).ToList());

            // Export the files of "shop" as a ZIP: primary file first, content as in the backup, plus an attach script.
            var ticket = await Read<FileDownloadTicketDto>(client.PostAsJsonAsync($"/api/file-restore/mounts/{mount.Id}/sql/downloads", new SqlDownloadRequestDto("SQLEXPRESS", "shop", "UTC"), Ct));
            Assert.EndsWith("_SQLEXPRESS_shop.zip", ticket.FileName, StringComparison.Ordinal);
            var zipResponse = await client.GetAsync("/" + ticket.Url, Ct);
            Assert.True(zipResponse.IsSuccessStatusCode, await zipResponse.Content.ReadAsStringAsync(Ct));
            using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await zipResponse.Content.ReadAsByteArrayAsync(Ct))))
            {
                Assert.Equal(["shop.mdf", "shop_2.ndf", "shop_log.ldf", "shop-attach.sql"], zip.Entries.Select(e => e.FullName).ToList());
                byte[] Content(string name)
                {
                    using var stream = zip.GetEntry(name)!.Open();
                    using var copy = new MemoryStream();
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }

                Assert.Equal(WindowsTestDisk.SqlFile(1, 1 << 20, 0x11), Content("shop.mdf"));
                Assert.Equal(WindowsTestDisk.SqlFile(2, 512 << 10, 0x22), Content("shop_log.ldf"));
                var script = System.Text.Encoding.UTF8.GetString(Content("shop-attach.sql"));
                Assert.Contains("CREATE DATABASE [shop] ON\r\n    (FILENAME = N'D:\\Restore\\shop.mdf'),\r\n    (FILENAME = N'D:\\Restore\\shop_2.ndf'),\r\n    (FILENAME = N'D:\\Restore\\shop_log.ldf')\r\nFOR ATTACH;", script, StringComparison.Ordinal);
            }

            // A database with a missing file cannot be exported; the audit log names the download.
            var refused = await client.PostAsJsonAsync($"/api/file-restore/mounts/{mount.Id}/sql/downloads", new SqlDownloadRequestDto("SQLEXPRESS", "gone", null), Ct);
            Assert.False(refused.IsSuccessStatusCode);
            Assert.Contains("gone.mdf: it does not exist in the restore point", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/file-restore/mounts/{mount.Id}", Ct)).StatusCode);
            var browseLog = await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{mount.SessionId}/log", Ct);
            Assert.Contains(browseLog!, l => l.Message.Contains(@"downloaded the files of SQL database SQLEXPRESS\shop (3 file(s)) as ZIP", StringComparison.Ordinal));

            // For checking the console by hand: BDFU_UI_FIXTURE=<folder> keeps a copy of this repository (add it as a repository).
            if (Environment.GetEnvironmentVariable("BDFU_UI_FIXTURE") is { Length: > 0 } keep)
            {
                CopyDirectory(Path.Combine(_root, "repo"), keep);
            }
        }
        finally
        {
            if (agent is not null)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task File_level_restore_copies_files_to_a_share()
    {
        var client = await SignedInClientAsync();
        var point = await BackUpFixtureAsync(client, "files02", "ntfs-gpt");
        var credential = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"CORP\restore", FakeShareConnector.Password, "Restore share"), Ct));
        var wrong = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"CORP\other", "wrong", null), Ct));
        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = mount.Volumes.Single(v => v.Readable).Id;
        FileRestoreRequestDto Request(FileRestoreModeDto mode, Guid credentialId, string share = @"\\fs01\restore\files02\today", string[]? paths = null) => new()
        {
            RestorePointId = point.Id,
            Volume = volume,
            Paths = paths ?? ["folder", "pattern-3m.bin", "hidden.txt"],
            Mode = mode,
            Target = new FileRestoreTargetDto { Kind = FileRestoreTargetKindDto.Share, SharePath = share, CredentialId = credentialId },
            MountId = mount.Id,
        };

        // Overwrite into a folder that does not exist yet.
        var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, credential.Id), Ct));
        var detail = await WaitForSessionAsync(client, started.SessionId);
        Assert.Equal("FileRestore", detail.Session.Kind);
        Assert.True(detail.Session.State == "Succeeded", detail.Session.Summary);
        var folder = Path.Combine(_root, "shares", "fs01", "restore", "files02", "today");
        var manifest = FsFixtures.Manifest("ntfs-gpt.p1").ToDictionary(l => l[7], l => l[1]);
        Assert.Equal(manifest["pattern-3m.bin"], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, "pattern-3m.bin"), Ct))));
        Assert.Equal(manifest["folder/sub/deep/nested.txt"], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, "folder", "sub", "deep", "nested.txt"), Ct))));
        Assert.Contains(_shares!.Connections, c => c.User == @"CORP\restore" && c.Password == FakeShareConnector.Password);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!;
        var text = string.Join("\n", log.Select(l => l.Message));
        Assert.True(log.Any(l => l.Message == @"files02: Connected to \\fs01\restore on fs01 (192.0.2.10):445 as CORP\restore."), text);
        Assert.True(log.Any(l => l.Message == "files02: Selection: 3 file(s) in 3 folder(s), 3.0 MB."), text);
        Assert.True(log.Any(l => l.Message.StartsWith("files02: File restore finished", StringComparison.Ordinal)), text);

        // Keep: the existing files stay, the restored ones are RESTORED- copies.
        File.WriteAllText(Path.Combine(folder, "hidden.txt"), "changed in the meantime");
        var kept = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Keep, credential.Id), Ct))).SessionId);
        Assert.True(kept.Session.State == "Succeeded", kept.Session.Summary);
        Assert.Contains("RESTORED- copies", kept.Session.Summary, StringComparison.Ordinal);
        Assert.Equal("changed in the meantime", File.ReadAllText(Path.Combine(folder, "hidden.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "RESTORED-hidden.txt")));
        Assert.True(File.Exists(Path.Combine(folder, "folder", "sub", "deep", "RESTORED-nested.txt")));

        // A wrong password fails the session with the server named.
        var failed = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, wrong.Id), Ct))).SessionId);
        Assert.Equal("Failed", failed.Session.State);
        Assert.Contains("fs01 (192.0.2.10):445", failed.Session.Summary, StringComparison.Ordinal);

        // Rejected up front: no share path, unknown credential, items from two folders, missing item.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, credential.Id, share: @"C:\temp"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, Guid.NewGuid()), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, credential.Id, paths: ["hello.txt", "folder/sub"]), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, credential.Id, paths: ["nope.txt"]), Ct)).StatusCode);

        // The browser's audit log records the restores.
        var browseLog = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{mount.SessionId}/log", Ct))!;
        Assert.Equal(3, browseLog.Count(l => l.Message.StartsWith("admin started restoring folder, pattern-3m.bin, hidden.txt from ", StringComparison.Ordinal) && l.Message.Contains(@"to \\fs01\restore\files02\today", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task File_level_restore_writes_into_the_vm_through_the_real_agent()
    {
        // The real agent loop over HTTP with the file system mapped into a sandbox folder (C:\x → {sandbox}/C/x).
        var client = await SignedInClientAsync();
        var point = await BackUpFixtureAsync(client, "files03", "ntfs-gpt");
        var directory = Path.Combine(_root, "agent-restore");
        Directory.CreateDirectory(directory);
        var sandbox = Path.Combine(_root, "guest");
        var paths = new AgentPaths(directory);
        var secret = await _factory!.Services.GetRequiredService<AgentSettingsStore>().GetEnrollmentSecretAsync(Ct);
        await File.WriteAllTextAsync(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(
            new AgentConfig { ServerUrl = "https://localhost/", EnrollmentSecret = secret }, Backdafuckup.Agent.Core.AgentJson.Options), Ct);
        var runner = new AgentRunner(paths, new SimulatedHost(point.VmNativeId), new SimulatedVss(), new AgentLog(paths.LogFile), _ => _factory.Server.CreateHandler(), TimeSpan.FromMilliseconds(200), new SandboxRestoreFileSystem(sandbox));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = Task.Run(() => runner.RunAsync(stop.Token), Ct);
        var agent = await _factory.Services.GetRequiredService<AgentService>().WaitForVmAsync(point.VmNativeId, TimeSpan.FromSeconds(30), Ct)
            ?? throw new TimeoutException("The agent did not come online.");
        var registry = _factory.Services.GetRequiredService<AgentRegistry>();
        for (var i = 0; i < 100 && !registry.HasCapability(agent.Id, AgentProtocol.CapabilityRestoreFiles); i++)
        {
            await Task.Delay(100, Ct);
        }

        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = mount.Volumes.Single(v => v.Readable).Id;
        FileRestoreRequestDto Request(FileRestoreModeDto mode, bool permissions, string? folder = @"D:\Restore") => new()
        {
            RestorePointId = point.Id,
            Volume = volume,
            Paths = ["folder", "pattern-3m.bin", "hidden.txt"],
            Mode = mode,
            Target = new FileRestoreTargetDto { Kind = FileRestoreTargetKindDto.Agent, Folder = folder, RestorePermissions = permissions },
            MountId = mount.Id,
        };

        // The test image has no drive letters, so there is no original location.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, false, folder: null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, false, folder: @"..\x"), Ct)).StatusCode);

        // Into D:\Restore: content and folder structure arrive, every file checked by SHA-256 against what was sent.
        var first = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, false), Ct))).SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{first.Session.Id}/log", Ct))!.Select(l => l.Message));
        Assert.True(first.Session.State == "Succeeded", first.Session.Summary + "\n" + log);
        var restored = Path.Combine(sandbox, "D", "Restore");
        var manifest = FsFixtures.Manifest("ntfs-gpt.p1").ToDictionary(l => l[7], l => l[1]);
        Assert.Equal(manifest["pattern-3m.bin"], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restored, "pattern-3m.bin"), Ct))));
        Assert.Equal(manifest["folder/sub/deep/nested.txt"], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(restored, "folder", "sub", "deep", "nested.txt"), Ct))));
        Assert.Contains("3 file(s) in 3 folder(s) restored", first.Session.Summary, StringComparison.Ordinal);

        // A link on the target is never written through; keep mode writes RESTORED- copies; permissions cannot be set
        // in the sandbox, so those files are reported as incomplete.
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.Delete(Path.Combine(restored, "folder", "sub"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(restored, "folder", "sub"), outside);
        var second = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Keep, true), Ct))).SessionId);
        var secondLog = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{second.Session.Id}/log", Ct))!.Select(l => l.Message).ToList();
        Assert.True(second.Session.State == "Warning", second.Session.Summary + "\n" + string.Join("\n", secondLog));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        Assert.Contains(secondLog, l => l.Contains(@"D:\Restore\folder\sub: a link or junction with this name exists", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(restored, "RESTORED-pattern-3m.bin")));
        Assert.Contains(secondLog, l => l.Contains("permissions not restored", StringComparison.Ordinal));

        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task Agent_transfers_files_from_several_volumes_keeps_existing_files_and_holds_its_work_slot()
    {
        // Data on D:, log on L: (as SQL databases often are), restored into one folder of the guest.
        var client = await SignedInClientAsync();
        var data = WindowsTestDisk.SqlFile(1, 1 << 20, 0x11);
        var logFile = WindowsTestDisk.SqlFile(2, 256 << 10, 0x22);
        var image = WindowsTestDisk.Create(
        [
            new WindowsTestDisk.Volume('C', 8, new Dictionary<string, byte[]>()),
            new WindowsTestDisk.Volume('D', 8, new Dictionary<string, byte[]> { ["SQL/shop.mdf"] = data }),
            new WindowsTestDisk.Volume('L', 8, new Dictionary<string, byte[]> { ["Log/shop_log.ldf"] = logFile }),
        ]);
        var point = await BackUpImageAsync(client, "sql02", image);
        var sandbox = Path.Combine(_root, "guest-volumes");
        var gate = new ManualResetEventSlim(false);
        var (agent, stop, running) = await StartAgentAsync(point.VmNativeId, new GatedSandbox(sandbox, gate));
        var registry = _factory!.Services.GetRequiredService<AgentRegistry>();
        var transfers = _factory.Services.GetRequiredService<AgentTransfers>();

        // A file that exists at the target stays as it is when the transfer says "fail".
        Directory.CreateDirectory(Path.Combine(sandbox, "E", "db"));
        await File.WriteAllTextAsync(Path.Combine(sandbox, "E", "db", "shop_log.ldf"), "existing", Ct);

        var repository = await _factory.Services.GetRequiredService<Backdafuckup.Server.Engine.RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using var reader = await repository.OpenRestorePointAsync(point.Id, Ct);
        var disks = await FileRestoreMounts.OpenDisksAsync(reader, _factory.Services.GetRequiredService<Backdafuckup.FileLevel.ChunkCache>(), Ct);
        AgentTransferPlan plan;
        string dataVolume;
        using (var guestImage = Backdafuckup.FileLevel.GuestImage.Open(disks))
        {
            var d = guestImage.Volumes.Single(v => v.MountPoint == "D:");
            var l = guestImage.Volumes.Single(v => v.MountPoint == "L:");
            dataVolume = d.Id;
            plan = AgentFilePlan.Plan(
            [
                (d.Id, guestImage.OpenFileSystem(d.Id), Backdafuckup.FileLevel.GuestPath.Parse("SQL/shop.mdf"), @"E:\db\shop.mdf", @"D:\SQL\shop.mdf"),
                (l.Id, guestImage.OpenFileSystem(l.Id), Backdafuckup.FileLevel.GuestPath.Parse("Log/shop_log.ldf"), @"E:\db\shop_log.ldf", @"L:\Log\shop_log.ldf"),
            ]);
        }

        var info = new FileTransferInfo { TransferId = Guid.NewGuid(), Roots = [@"E:\db"], IfExists = FileTransferIfExists.Fail, ItemCount = plan.Items.Count, TotalBytes = plan.Bytes };
        var observer = new RecordingObserver();
        using var transfer = new AgentTransfer(agent.Id, info, plan, disks, dataVolume, observer, TimeProvider.System);
        transfers.Add(transfer);
        var reservation = registry.TryReserveWork(agent.Id);
        Assert.NotNull(reservation);
        Assert.Null(registry.TryReserveWork(agent.Id));
        var accepted = await registry.SendAsync(agent.Id, new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.RestoreFiles, TransferId = info.TransferId }, TimeSpan.FromSeconds(30), Ct);
        Assert.True(accepted.Success, accepted.Error);

        // While the transfer waits at the gate, the agent reports its work slot and refuses a second restore.
        for (var i = 0; i < 100 && !registry.WorkText(agent.Id).Contains(info.TransferId.ToString(), StringComparison.Ordinal); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Contains($"file restore {info.TransferId}", registry.WorkText(agent.Id), StringComparison.Ordinal);
        var refused = await registry.SendAsync(agent.Id, new AgentCommand { Id = Guid.NewGuid(), Kind = AgentCommandKinds.RestoreFiles, TransferId = Guid.NewGuid() }, TimeSpan.FromSeconds(30), Ct);
        Assert.False(refused.Success);
        Assert.Contains("busy", refused.Error, StringComparison.Ordinal);

        gate.Set();
        var report = await transfer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Null(report.Error);
        reservation!.Dispose();
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(sandbox, "E", "db", "shop.mdf"), Ct));
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(sandbox, "E", "db", "shop_log.ldf"), Ct));
        Assert.Equal(1, transfer.RestoredFiles);
        Assert.Contains(observer.Problems, p => p.Contains(@"L:\Log\shop_log.ldf", StringComparison.Ordinal) && p.Contains("a file with this name exists; it is kept", StringComparison.Ordinal));

        // The finished work frees the slot even though the agent's last report (from the start of its poll) still names it.
        using (var next = registry.TryReserveWork(agent.Id))
        {
            Assert.NotNull(next);
        }

        await stop.CancelAsync();
        await running;
    }

    /// <summary>The real agent loop over HTTP with its file system mapped into a sandbox folder (C:\x → {sandbox}/C/x).</summary>
    private async Task<(Backdafuckup.Server.Data.AgentEntity Agent, CancellationTokenSource Stop, Task Running)> StartAgentAsync(string vmNativeId, IRestoreFileSystem? fs, ISqlEnvironment? sql = null)
    {
        var directory = Path.Combine(_root, "agent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var paths = new AgentPaths(directory);
        var secret = await _factory!.Services.GetRequiredService<AgentSettingsStore>().GetEnrollmentSecretAsync(Ct);
        await File.WriteAllTextAsync(paths.ConfigFile, System.Text.Json.JsonSerializer.Serialize(
            new AgentConfig { ServerUrl = "https://localhost/", EnrollmentSecret = secret }, Backdafuckup.Agent.Core.AgentJson.Options), Ct);
        var runner = new AgentRunner(paths, new SimulatedHost(vmNativeId), new SimulatedVss(), new AgentLog(paths.LogFile), _ => _factory.Server.CreateHandler(), TimeSpan.FromMilliseconds(200), fs, sql);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = Task.Run(() => runner.RunAsync(stop.Token), Ct);
        var agent = await _factory.Services.GetRequiredService<AgentService>().WaitForVmAsync(vmNativeId, TimeSpan.FromSeconds(30), Ct)
            ?? throw new TimeoutException("The agent did not come online.");
        var registry = _factory.Services.GetRequiredService<AgentRegistry>();
        var capability = fs is null ? AgentProtocol.CapabilitySqlRestore : AgentProtocol.CapabilityRestoreFilesV2;
        for (var i = 0; i < 100 && !registry.HasCapability(agent.Id, capability); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(registry.HasCapability(agent.Id, capability));
        return (agent, stop, running);
    }

    [Fact]
    public async Task Sql_inventory_runs_in_the_agent_with_the_chosen_account()
    {
        var client = await SignedInClientAsync();
        var vm = Guid.NewGuid().ToString();
        var sql = new FakeSqlEnvironment();
        var (_, stop, running) = await StartAgentAsync(vm, null, sql);
        Assert.Contains((await client.GetFromJsonAsync<List<AgentDto>>("/api/agents", Ct))!, a => a.VmNativeId == vm && a.CanRunSql && !a.CanRestoreFiles);

        var login = await Read<SqlCredentialDto>(client.PostAsJsonAsync("/api/credentials/sql", new SaveSqlCredentialDto("bdfu_restore", "Secret-1'\"x", "restores"), Ct));
        Assert.Equal("bdfu_restore", Assert.Single((await client.GetFromJsonAsync<List<SqlCredentialDto>>("/api/credentials/sql", Ct))!).UserName);
        var windows = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"CORP\sqladmin", "Windows-2", null), Ct));

        // Without an account: the instances from the registry and the service manager only.
        var plain = await Read<SqlTargetDto>(client.PostAsJsonAsync("/api/sql/inventory", new SqlInventoryRequestDto(vm, null, null), Ct));
        Assert.Equal("SQL01", plain.Host);
        Assert.Null(plain.Account);
        Assert.Equal(["OLD", "SQLEXPRESS"], plain.Instances.Select(i => i.Name).Order().ToList());
        Assert.All(plain.Instances, i => Assert.False(i.Connected));
        Assert.Empty(sql.Accounts);
        Assert.Equal(10L << 30, Assert.Single(plain.Volumes).FreeBytes);

        // With the SQL login: the agent gets the password; a stopped instance is not tried.
        var withLogin = await Read<SqlTargetDto>(client.PostAsJsonAsync("/api/sql/inventory", new SqlInventoryRequestDto(vm, login.Id, null), Ct));
        Assert.Equal("bdfu_restore (SQL login)", withLogin.Account);
        var express = withLogin.Instances.Single(i => i.Name == "SQLEXPRESS");
        Assert.Equal("SQL Server 2022", express.Product);
        Assert.Contains("error 18456", express.ConnectError, StringComparison.Ordinal);
        Assert.Equal("the service MSSQL$OLD is stopped", withLogin.Instances.Single(i => i.Name == "OLD").ConnectError);
        var account = Assert.Single(sql.Accounts);
        Assert.Equal((SqlAccountKinds.Sql, "bdfu_restore", "Secret-1'\"x"), (account.Kind, account.UserName, account.Password));

        // With a Windows credential: the agent logs on with it; one instance only.
        var withWindows = await Read<SqlTargetDto>(client.PostAsJsonAsync("/api/sql/inventory", new SqlInventoryRequestDto(vm, windows.Id, "SQLEXPRESS"), Ct));
        Assert.Equal(@"CORP\sqladmin (Windows)", withWindows.Account);
        Assert.Equal((SqlAccountKinds.Windows, @"CORP\sqladmin", "Windows-2"), (sql.Accounts[^1].Kind, sql.Accounts[^1].UserName, sql.Accounts[^1].Password));
        Assert.Equal(2, sql.Accounts.Count);

        // A VM without an agent, and an unknown account, are refused with a reason.
        var noAgent = await client.PostAsJsonAsync("/api/sql/inventory", new SqlInventoryRequestDto(Guid.NewGuid().ToString(), null, null), Ct);
        Assert.Equal(HttpStatusCode.Conflict, noAgent.StatusCode);
        Assert.Contains("No backdafuckup agent", await noAgent.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/sql/inventory", new SqlInventoryRequestDto(vm, Guid.NewGuid(), null), Ct)).StatusCode);

        // The passwords never appear in the agent's log.
        var agentLog = string.Join("\n", Directory.GetFiles(_root, "agent.log", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("SQL job", agentLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret-1", agentLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows-2", agentLog, StringComparison.Ordinal);

        await stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task File_level_restore_writes_into_a_linux_vm_over_ssh()
    {
        // A real OpenSSH with sudo (Debian container); the account alice needs her password for sudo.
        await using var guest = await SshTestServer.StartAsync(Ct);
        var client = await SignedInClientAsync();
        var point = await BackUpFixtureAsync(client, "linux01", "ext4-gpt");
        var credential = await Read<LinuxCredentialDto>(client.PostAsJsonAsync("/api/credentials/linux", new SaveLinuxCredentialDto("alice", "alicepw", null, null, "alicepw", "test guest"), Ct));
        Assert.True(credential.HasSudoPassword);
        var mount = await Read<FileMountDto>(client.PostAsJsonAsync("/api/file-restore/mounts", new OpenFileMountRequestDto(point.Id), Ct));
        var volume = mount.Volumes.Single(v => v.Readable).Id;
        FileRestoreRequestDto Request(FileRestoreModeDto mode, string? folder = "/srv/restore") => new()
        {
            RestorePointId = point.Id,
            Volume = volume,
            Paths = ["folder", "hello.txt", "link-to-hello", "owned.txt", "secret.txt", "pattern-3m.bin"],
            Mode = mode,
            Target = new FileRestoreTargetDto { Kind = FileRestoreTargetKindDto.Ssh, Address = guest.Host, Port = guest.Port, CredentialId = credential.Id, Folder = folder, RestorePermissions = true },
            MountId = mount.Id,
        };

        // No fstab in the test image, so no original location; relative folders are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, folder: null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite, folder: "srv/../etc"), Ct)).StatusCode);

        var first = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Overwrite), Ct))).SessionId);
        var log = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{first.Session.Id}/log", Ct))!.Select(l => l.Message));
        Assert.True(first.Session.State == "Succeeded", first.Session.Summary + "\n" + log);
        Assert.Contains("trusted on first use", log, StringComparison.Ordinal);
        Assert.Contains("put in place as root through sudo", log, StringComparison.Ordinal);
        var manifest = FsFixtures.Manifest("ext4-gpt.p1").ToDictionary(l => l[7], l => l[1]);
        var sums = await guest.ExecAsync(Ct, "sh", "-c", "cd /srv/restore && sha256sum hello.txt pattern-3m.bin folder/sub/deep/nested.txt");
        Assert.Contains($"{manifest["hello.txt"]}  hello.txt", sums, StringComparison.Ordinal);
        Assert.Contains($"{manifest["pattern-3m.bin"]}  pattern-3m.bin", sums, StringComparison.Ordinal);
        Assert.Contains($"{manifest["folder/sub/deep/nested.txt"]}  folder/sub/deep/nested.txt", sums, StringComparison.Ordinal);
        var stat = await guest.ExecAsync(Ct, "stat", "-c", "%n %u:%g %a %Y", "/srv/restore/secret.txt", "/srv/restore/owned.txt", "/srv/restore/hello.txt");
        Assert.Contains("/srv/restore/secret.txt 0:0 640", stat, StringComparison.Ordinal);
        Assert.Contains("/srv/restore/owned.txt 1000:1000 644", stat, StringComparison.Ordinal);
        Assert.Contains("/srv/restore/hello.txt 0:0 644 981173106", stat, StringComparison.Ordinal);
        Assert.Equal("hello.txt\n", await guest.ExecAsync(Ct, "readlink", "/srv/restore/link-to-hello"));

        // Keep: existing files stay, restored ones become RESTORED- copies; a planted link is not written through.
        await guest.ExecAsync(Ct, "sh", "-c", "echo changed > /srv/restore/hello.txt && rm -rf /srv/restore/folder && ln -s /etc /srv/restore/folder");
        var kept = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/file-restore/restores", Request(FileRestoreModeDto.Keep), Ct))).SessionId);
        var keptLog = string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{kept.Session.Id}/log", Ct))!.Select(l => l.Message));
        Assert.True(kept.Session.State == "Warning", kept.Session.Summary + "\n" + keptLog);
        Assert.Equal("changed\n", await guest.ExecAsync(Ct, "cat", "/srv/restore/hello.txt"));
        Assert.Equal($"{manifest["hello.txt"]}  /srv/restore/RESTORED-hello.txt\n", await guest.ExecAsync(Ct, "sha256sum", "/srv/restore/RESTORED-hello.txt"));
        Assert.Contains("/srv/restore/folder is a symbolic link; nothing is written through it", keptLog, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await guest.ExecAsync(Ct, "sh", "-c", "ls /etc | grep -x sub || true"));
        Assert.Equal(string.Empty, await guest.ExecAsync(Ct, "sh", "-c", "ls -A /var/tmp | grep bdfu- || true"));

        // The host key was trusted on first use and is listed.
        var keys = await client.GetFromJsonAsync<List<SshHostKeyDto>>("/api/ssh-host-keys", Ct);
        Assert.Contains(keys!, k => k.Host == guest.Host && k.Port == guest.Port);
    }

    /// <summary>Windows paths of the agent in a local folder: <c>D:\a\b</c> is <c>{root}/D/a/b</c>.</summary>
    private sealed class SandboxRestoreFileSystem(string root) : PortableRestoreFileSystem
    {
        public override string? CheckRoot(string path) => null;

        protected override string Map(string windowsPath) =>
            Path.Combine([root, windowsPath[..1], .. windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
    }

    /// <summary>A sandbox whose first temporary file waits for <paramref name="gate"/>: keeps a transfer running.</summary>
    private sealed class GatedSandbox(string root, ManualResetEventSlim gate) : PortableRestoreFileSystem
    {
        public override string? CheckRoot(string path) => null;

        protected override string Map(string windowsPath)
        {
            if (windowsPath.Contains(".bdfu-restore-", StringComparison.Ordinal))
            {
                gate.Wait(TimeSpan.FromSeconds(60));
            }

            return Path.Combine([root, windowsPath[..1], .. windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
        }
    }

    /// <summary>Two instances (one stopped) and a volume; every connection fails like a wrong login.</summary>
    private sealed class FakeSqlEnvironment : ISqlEnvironment
    {
        public List<SqlAccount> Accounts { get; } = [];

        public List<SqlInstanceInfo> Instances() =>
        [
            new() { Name = "SQLEXPRESS", InstanceId = "MSSQL16.SQLEXPRESS", SetupVersion = "16.0.1000.6", ServiceName = "MSSQL$SQLEXPRESS", ServiceState = "Running", ServiceAccount = @"NT Service\MSSQL$SQLEXPRESS", DefaultDataFolder = @"D:\SQL", DefaultLogFolder = @"L:\Log" },
            new() { Name = "OLD", ServiceName = "MSSQL$OLD", ServiceState = "Stopped" },
        ];

        public List<SqlVolumeInfo> Volumes() => [new() { Root = @"D:\", Label = "SQLDATA", FreeBytes = 10L << 30, TotalBytes = 20L << 30 }];

        public System.Data.Common.DbConnection Open(string instance, SqlAccount account)
        {
            lock (Accounts)
            {
                Accounts.Add(account);
            }

            throw new InvalidOperationException($"{account.UserName} could not connect to SQL Server instance {instance} on SQL01: Login failed (error 18456).");
        }
    }

    private sealed class RecordingObserver : Backdafuckup.FileLevel.Restore.IFileRestoreObserver
    {
        public List<string> Problems { get; } = [];

        public void Counting(long files, long bytes)
        {
        }

        public void Planned(long files, long directories, long bytes)
        {
        }

        public void Copied(long bytes)
        {
        }

        public void FileRestored(long files)
        {
        }

        public void Renamed(string source, string target)
        {
        }

        public void Problem(Backdafuckup.FileLevel.Restore.FileRestoreProblem problem)
        {
            lock (Problems)
            {
                Problems.Add(problem.ToString());
            }
        }
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

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != ".owner.lock"))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static async Task<T> Read<T>(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
