using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Web.Pages;
using Backdafuckup.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Backdafuckup.Web.Tests;

/// <summary>
/// The inventory (one wording for both platforms' power states, the newest backup per VM, a filter for VMs without one) and
/// the backup proxies (short version, missing SAN settings in a row of their own instead of inside a column).
/// </summary>
public sealed class InventoryAndProxiesPageTests : BunitContext
{
    private static readonly Guid PoolId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000b1");
    private static readonly Guid VcenterId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000b2");
    private readonly FakeApi _api = new();

    public InventoryAndProxiesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new ApiClient(new HttpClient(_api) { BaseAddress = new Uri("https://backup.test/") }));
    }

    [Fact]
    public void The_inventory_says_running_or_off_for_both_platforms_and_shows_each_vms_newest_backup()
    {
        _api.Answers["api/connections"] = new List<ConnectionDto>
        {
            new(PoolId, "XenServer", "Lab pool", "xcp1.corp.example", 443, "192.0.2.11", null, "XCP-ng", "8.3.0", "Connected", null, null, "root", false, []),
            new(VcenterId, "VSphere", "Lab vCenter", "vcenter.corp.example", 443, null, null, "VMware vCenter Server", "7.0.3", "Connected", null, null, "backup", false, []),
        };
        _api.Answers[$"api/connections/{PoolId}/inventory"] = new List<InventoryItemDto>
        {
            Vm("1f0c-app01", "app01", "Running", "xcp-host1", cbt: true),
            Vm("1f0c-old01", "old01", "Halted", "xcp-host1", cbt: false),
            new("Host", "xcp-host1", "xcp1", null, new Dictionary<string, string> { ["address"] = "192.0.2.11" }),
        };
        _api.Answers[$"api/connections/{VcenterId}/inventory"] = new List<InventoryItemDto>
        {
            Vm("5003-vdi01", "vdi01", "poweredOff", "host-31", cbt: true, guestOs: "Microsoft Windows 11 (64-bit)"),
        };
        _api.Answers["api/restore-points/vms"] = new List<BackupVmDto>
        {
            new("1f0c-app01", "app01", PoolId, [new BackupVmJobDto(Guid.NewGuid(), "Nightly", true, true, null, 3, 1L << 30, [], null, "ok", null)], 3, 1L << 30,
                new BackupVmPointDto(Guid.NewGuid(), null, DateTimeOffset.UtcNow.AddHours(-2), "Incremental", "crash-consistent", null, 10L << 30, 0, false, null), "ok"),
        };

        Render<MudPopoverProvider>();
        var cut = Render<Inventory>();
        cut.WaitForAssertion(() => Assert.Contains("Lab vCenter", cut.Markup, StringComparison.Ordinal));

        // One wording: XAPI's Running/Halted and vSphere's poweredOff.
        Assert.Equal(["Running", "Off", "Off"], PowerCells(cut));
        Assert.DoesNotContain("poweredOff", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Halted", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Microsoft Windows 11 (64-bit)", cut.Markup, StringComparison.Ordinal);

        // The newest backup with its job; none for the others.
        Assert.Matches(@"[12] h [0-9]+ min ago", cut.Markup);
        Assert.Contains("Nightly", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("2 VMs · 1 running · 1 with backup", cut.Markup, StringComparison.Ordinal);

        // Only the VMs without a backup.
        var show = cut.FindComponent<MudToggleGroup<string>>();
        cut.InvokeAsync(() => show.Instance.ValueChanged.InvokeAsync("unprotected"));
        cut.WaitForAssertion(() => Assert.Equal(["Off", "Off"], PowerCells(cut)));
        Assert.Contains("VMs (1 of 2)", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_shows_a_short_version_and_its_missing_san_settings_below_its_row()
    {
        _api.Answers["api/proxies"] = new List<ProxyDto>
        {
            new(Guid.NewGuid(), "proxy01", true, true, "bdfu-vs-proxy01", "0.3.0+65d7e6243262a1ec54a7e1030bcf8338fe3bff10", "420bba8e-6f4d-f008-67cd-a1a7c07d7961",
                ["192.0.2.91"], ["hotadd-v1", "nbd-v1"], 1, DateTimeOffset.UtcNow, "192.0.2.91:51086", DateTimeOffset.UtcNow.AddDays(-1), null, null,
                "open-iscsi is installed, but has no initiator name yet\nmultipathd runs"),
            new(Guid.NewGuid(), "proxy02", true, true, "bdfu-vs-proxy02", "0.3.0+65d7e62", null, ["192.0.2.92"], ["hotadd-v1", "nbd-v1", "san-v1"], 0, DateTimeOffset.UtcNow,
                null, DateTimeOffset.UtcNow.AddDays(-1), null, "iqn.2004-10.com.ubuntu:01:9f8e7d6c5b4a", null),
        };
        _api.Answers["api/proxies/package"] = new ProxyPackageDto("0.3.0", [new ProxyPackageFileDto("backdafuckup-proxy", 4)]);

        Render<MudPopoverProvider>();
        var cut = Render<Proxies>();
        cut.WaitForAssertion(() => Assert.Contains("proxy02", cut.Markup, StringComparison.Ordinal));

        Assert.Contains(">0.3.0 (65d7e62)<", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("SAN: 2 settings missing", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["open-iscsi is installed, but has no initiator name yet", "multipathd runs"], cut.FindAll("li").Select(l => l.TextContent.Trim()));
        // The missing settings span the table, not one column.
        Assert.Contains(cut.FindAll("td"), td => td.GetAttribute("colspan") == "8" && td.TextContent.Contains("multipathd runs", StringComparison.Ordinal));
        Assert.Contains("iqn.2004-10.com.ubuntu:01:9f8e7d6c5b4a", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>The power cell's text of each VM row, in the order shown.</summary>
    private static List<string> PowerCells(IRenderedComponent<Inventory> cut) =>
        [.. cut.FindAll("td.nowrap").Select(td => td.TextContent.Trim()).Where(t => t is "Running" or "Off" or "Suspended" or "Paused")];

    private static InventoryItemDto Vm(string id, string name, string power, string host, bool cbt, string? guestOs = null)
    {
        var properties = new Dictionary<string, string>
        {
            ["powerState"] = power, ["vcpus"] = "2", ["memoryBytes"] = (4L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture), ["diskCount"] = "1",
            ["diskBytes"] = (40L << 30).ToString(System.Globalization.CultureInfo.InvariantCulture), ["cbtEnabled"] = cbt ? "True" : "False",
        };
        if (guestOs is not null)
        {
            properties["guestOs"] = guestOs;
        }

        return new InventoryItemDto("Vm", id, name, host, properties);
    }

    /// <summary>Answers GET requests from a table of paths (without the query); everything else is 404.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public Dictionary<string, object> Answers { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(Answers.TryGetValue(path, out var answer)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer, answer.GetType()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
