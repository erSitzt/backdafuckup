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
    private async Task<RestorePointDto> BackUpFixtureAsync(HttpClient client, string vmName, string fixture)
    {
        var image = await File.ReadAllBytesAsync(FsFixtures.Image(fixture), Ct);
        var (vmUuid, disks) = _pool.AddVm(vmName, image.LongLength);
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
            Objects = [new JobObjectDto(vmUuid, vmName)],
            RetryCount = 0,
        }, Ct));
        var backup = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job.Id}/run", new RunJobDto(), Ct));
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, backup.SessionId)).Session.State);
        return Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
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

    /// <summary>Windows paths of the agent in a local folder: <c>D:\a\b</c> is <c>{root}/D/a/b</c>.</summary>
    private sealed class SandboxRestoreFileSystem(string root) : PortableRestoreFileSystem
    {
        public override string? CheckRoot(string path) => null;

        protected override string Map(string windowsPath) =>
            Path.Combine([root, windowsPath[..1], .. windowsPath[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries)]);
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
