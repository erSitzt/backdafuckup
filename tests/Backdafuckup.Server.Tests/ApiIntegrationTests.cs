using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Extents;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Server.Api;
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
        await Ok(client.PostAsJsonAsync("/api/setup", new SetupRequestDto(token, "admin", "correct horse battery"), Ct));
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
        Assert.Contains(log!, l => l.Message.Contains("Backup finished", StringComparison.Ordinal));

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
        Assert.Contains(_pool.Records("VM"), v => XapiRecord.Str(v, "name_label") == "mail01-copy");

        var dashboard = await client.GetFromJsonAsync<DashboardDto>("/api/dashboard", Ct);
        Assert.Equal(1, dashboard!.JobsSucceeded24h);
        Assert.Equal(1, dashboard.ProtectedVms);
        _ = disks;
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
