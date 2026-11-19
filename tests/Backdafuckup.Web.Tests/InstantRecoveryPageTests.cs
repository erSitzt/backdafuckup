using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Web.Pages;
using Backdafuckup.Web.Services;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Backdafuckup.Web.Tests;

/// <summary>
/// The instant recovery page: a start from a vSphere restore point (a new name, NICs disconnected, the host chosen), the
/// running list with a typed confirmation for Stop and a Migrate to production storage, and a clear word when it is off.
/// </summary>
public sealed class InstantRecoveryPageTests : BunitContext
{
    private static readonly Guid PointId = Guid.Parse("01a1f31e-0000-7000-8000-000000000001");
    private static readonly Guid VcenterId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000a1");
    private static readonly Guid SessionId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000c1");
    private readonly FakeApi _api = new();

    public InstantRecoveryPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new ApiClient(new HttpClient(_api) { BaseAddress = new Uri("https://backup.test/") }));
        _api.Connections = [new(VcenterId, "VSphere", "Lab vCenter", "vcenter.corp.example", 443, null, null, null, null, "Connected", null, null, "backup", false, [])];
        _api.Inventory =
        [
            Item("Vm", "5003ab12-original", "app-template", new() { ["folder"] = "group-v1001" }),
            Item("Folder", "group-v1001", "backdafuckup-test", new() { ["path"] = "DC/vm/backdafuckup-test" }),
            Item("Host", "host-31", "esx01.corp.example"),
            Item("Host", "host-32", "esx02.corp.example"),
            Item("Storage", "datastore-12", "alletra-ds1", new() { ["type"] = "VMFS" }),
            Item("Storage", "datastore-90", "bdfu-ir-1a2b3c4d", new() { ["type"] = "NFS" }),
            Item("Network", "network-101", "VM Network"),
        ];
        _api.Point = new RestorePointDto(
            PointId, Guid.NewGuid(), Guid.NewGuid(), "Template backup", VcenterId, "5003ab12-original", "app-template", DateTimeOffset.Parse("2026-11-19T08:53:00Z"), "Full", "Available",
            100L << 30, 70L << 30,
            [new RestorePointDiskDto("scsi0:0", "Hard disk 1", 100L << 30, "datastore-12", "alletra-ds1", "Full", null, 0, 0)],
            [new RestorePointNicDto("4000", "00:50:56:aa:bb:01", "network-101", "VM Network")],
            2, 8L << 30, "efi");
    }

    [Fact]
    public void A_start_from_a_restore_point_needs_a_host_and_sends_the_new_name_with_nics_disconnected()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/instant-recovery?point={PointId}");
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("Run app-template now", cut.Markup, StringComparison.Ordinal));

        // Two hosts: one has to be chosen; the NICs start disconnected, the name says what it is.
        Assert.Contains("Choose the ESXi host that runs the VM.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("The NICs start disconnected (new MAC addresses)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("app-template (instant)", cut.FindAll("input").Select(i => i.GetAttribute("value")));
        Assert.True(StartButton(cut).HasAttribute("disabled"));

        var host = cut.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "ESXi host");
        cut.InvokeAsync(() => host.Instance.ValueChanged.InvokeAsync("host-32"));
        cut.WaitForAssertion(() => Assert.False(StartButton(cut).HasAttribute("disabled")));
        Assert.Contains("<b>app-template (instant)</b> starts on esx02.corp.example", cut.Markup, StringComparison.Ordinal);
        StartButton(cut).Click();

        cut.WaitForAssertion(() => Assert.NotNull(_api.Started));
        var body = _api.Started!.RootElement;
        Assert.Equal(VcenterId.ToString(), body.GetProperty("connectionId").GetString());
        Assert.Equal("host-32", body.GetProperty("hostId").GetString());
        Assert.Equal("group-v1001", body.GetProperty("folderId").GetString());
        Assert.False(body.GetProperty("connectNics").GetBoolean());
        var vm = Assert.Single(body.GetProperty("vms").EnumerateArray());
        Assert.Equal(PointId.ToString(), vm.GetProperty("restorePointId").GetString());
        Assert.Equal("app-template (instant)", vm.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, vm.GetProperty("networkMap").ValueKind);
        cut.WaitForAssertion(() => Assert.EndsWith($"/sessions/{SessionId}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_needs_the_vm_name_typed_and_migrate_offers_production_datastores_only()
    {
        _api.Recoveries = [Running()];
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("bdfu-ir-1a2b3c4d", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("app-template, restore point of", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Fmt.Bytes(12L << 30), cut.Markup, StringComparison.Ordinal);

        // Stop: what the VM wrote is lost, so its name has to be typed.
        cut.Find("button[aria-label=Stop]").Click();
        cut.WaitForAssertion(() => Assert.Contains("What the VM wrote since it started is lost", cut.Markup, StringComparison.Ordinal));
        Assert.True(Button(cut, "Stop").HasAttribute("disabled"));
        cut.FindAll("input").Last().Input("app-template");
        Assert.True(Button(cut, "Stop").HasAttribute("disabled"));
        cut.FindAll("input").Last().Input("app-template (instant)");
        cut.WaitForAssertion(() => Assert.False(Button(cut, "Stop").HasAttribute("disabled")));
        Button(cut, "Stop").Click();
        cut.WaitForAssertion(() => Assert.Contains($"POST api/instant-recoveries/{SessionId}/stop", _api.Calls));

        // Migrate: the instant recovery's own datastores are not offered; thin by default.
        cut.Find("button[aria-label=Migrate]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Migrate app-template (instant) to production storage", cut.Markup, StringComparison.Ordinal));
        var datastore = cut.FindComponents<MudSelect<string>>().Single(s => s.Instance.Label == "Datastore");
        Assert.DoesNotContain("bdfu-ir-", string.Join(" ", datastore.FindComponents<MudSelectItem<string>>().Select(i => i.Instance.Value)), StringComparison.Ordinal);
        cut.InvokeAsync(() => datastore.Instance.ValueChanged.InvokeAsync("datastore-12"));
        cut.WaitForAssertion(() => Assert.False(Button(cut, "Migrate").HasAttribute("disabled")));
        Button(cut, "Migrate").Click();
        cut.WaitForAssertion(() => Assert.NotNull(_api.Migrated));
        Assert.Equal("datastore-12", _api.Migrated!.RootElement.GetProperty("datastoreId").GetString());
        Assert.True(_api.Migrated.RootElement.GetProperty("thin").GetBoolean());
    }

    [Fact]
    public void When_instant_recovery_is_off_the_page_says_how_to_switch_it_on_and_nothing_starts()
    {
        _api.Server = _api.Server with { Enabled = false, Listening = false };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/instant-recovery?point={PointId}");
        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.Contains("Instant recovery is off on this backup server.", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("TCP/UDP 111, TCP 2049 and TCP/UDP 20048", cut.Markup, StringComparison.Ordinal);
        Assert.True(StartButton(cut).HasAttribute("disabled"));
    }

    private IRenderedComponent<IComponent> RenderPage() => Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudSnackbarProvider>(1);
        builder.CloseComponent();
        builder.OpenComponent<InstantRecovery>(2);
        builder.CloseComponent();
    });

    private static AngleSharp.Dom.IElement StartButton(IRenderedComponent<IComponent> cut) => Button(cut, "Start instant recovery");

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<IComponent> cut, string text) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == text);

    private static InstantRecoveryDto Running() => new(
        SessionId, "Running", VcenterId, "Lab vCenter", "host-31", "esx01.corp.example", "bdfu-ir-1a2b3c4d", "192.0.2.10", "/bdfu/1a2b3c4d",
        DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow, null, 12L << 30, null,
        [new InstantRecoveryVmDto(PointId, "app-template (instant)", "vm-801", "app-template", DateTimeOffset.Parse("2026-11-19T08:53:00Z"))], null);

    private static InventoryItemDto Item(string kind, string id, string name, Dictionary<string, string>? properties = null) =>
        new(kind, id, name, null, properties ?? []);

    /// <summary>The API as the page asks it.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public InstantRecoveryServerDto Server { get; set; } = new(true, true, "0.0.0.0", null, 111, 2049, 20048, "own", null, 0, 4, 8);

        public List<InstantRecoveryDto> Recoveries { get; set; } = [];

        public RestorePointDto? Point { get; set; }

        public List<ConnectionDto> Connections { get; set; } = [];

        public List<InventoryItemDto> Inventory { get; set; } = [];

        public List<string> Calls { get; } = [];

        public JsonDocument? Started { get; private set; }

        public JsonDocument? Migrated { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (Calls)
            {
                Calls.Add($"{request.Method} {path}");
            }

            if (request.Method == HttpMethod.Post)
            {
                var body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                switch (path)
                {
                    case "api/instant-recoveries":
                        Started = body;
                        return Json(new StartedDto(SessionId));
                    case var p when p.EndsWith("/migrate", StringComparison.Ordinal):
                        Migrated = body;
                        return new HttpResponseMessage(HttpStatusCode.Accepted);
                    default:
                        return new HttpResponseMessage(HttpStatusCode.Accepted);
                }
            }

            object? result = path switch
            {
                "api/instant-recoveries/server" => Server,
                "api/instant-recoveries" => Recoveries,
                "api/connections" => Connections,
                _ when path == $"api/restore-points/{PointId}" => Point,
                _ when path.StartsWith("api/connections/", StringComparison.Ordinal) && path.EndsWith("/inventory", StringComparison.Ordinal) => Inventory,
                _ => null,
            };
            return result is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(result);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, value.GetType()) };
    }
}
