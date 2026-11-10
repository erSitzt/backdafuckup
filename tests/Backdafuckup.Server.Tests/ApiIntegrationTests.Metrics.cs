using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Backdafuckup.Server.Tests;

public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task Metrics_need_a_sign_in_or_a_token_and_a_metrics_token_reads_nothing_else()
    {
        var anonymous = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/metrics", Ct)).StatusCode);

        // A backup first, so that the snapshot histograms and the session counters have values.
        var console = await SignedInClientAsync();
        await BackUpImageAsync(console, "metrics-vm", new byte[4 << 20]);
        var text = await console.GetStringAsync("/metrics", Ct);
        Assert.Contains("bdfu_snapshot_duration_seconds_bucket{", text);
        Assert.Contains("le=\"10\"", text);
        Assert.Contains("bdfu_backup_disk_bytes_total{", text);
        Assert.Contains("bdfu_sessions_total{", text);
        Assert.Contains("bdfu_sessions_running{", text);
        Assert.DoesNotContain("_total_total", text);
        Assert.DoesNotContain("_seconds_seconds", text);
        Assert.DoesNotContain("_bytes_bytes", text);

        // The scrape token: /metrics only.
        var created = await Read<CreatedApiTokenDto>(console.PostAsJsonAsync("/api/tokens", new CreateApiTokenDto("prometheus", null, ApiTokenScopes.Metrics), Ct));
        Assert.Equal(ApiTokenScopes.Metrics, created.Info.Scope);
        var scraper = Bearer(created.Token);
        var scraped = await scraper.GetAsync("/metrics", Ct);
        Assert.Equal(HttpStatusCode.OK, scraped.StatusCode);
        Assert.Contains("bdfu_", await scraped.Content.ReadAsStringAsync(Ct));

        var refused = await scraper.GetAsync("/api/jobs", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("scope 'metrics'", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await scraper.GetAsync("/api/about", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scraper.PostAsJsonAsync("/api/tokens", new CreateApiTokenDto("more", 1), Ct)).StatusCode);

        // A full token reads /metrics too; an unknown scope is refused.
        var full = await Read<CreatedApiTokenDto>(console.PostAsJsonAsync("/api/tokens", new CreateApiTokenDto("scripts", 1), Ct));
        Assert.Equal(ApiTokenScopes.Full, full.Info.Scope);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(full.Token).GetAsync("/metrics", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Bearer(full.Token).GetAsync("/api/jobs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await console.PostAsJsonAsync("/api/tokens", new CreateApiTokenDto("odd", 1, "admin"), Ct)).StatusCode);
        Assert.Contains((await console.GetFromJsonAsync<List<ApiTokenDto>>("/api/tokens", Ct))!, t => t.Name == "prometheus" && t.Scope == ApiTokenScopes.Metrics);

        HttpClient Bearer(string token)
        {
            var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }
    }
}
