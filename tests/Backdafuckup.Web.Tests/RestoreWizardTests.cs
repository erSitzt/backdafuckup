using System.Net;
using System.Net.Http.Json;
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
/// The restore wizard for a vSphere restore point: only vCenters as targets, names instead of ids, what the original
/// location means, and no "Next" while something would make the restore fail.
/// </summary>
public sealed class RestoreWizardTests : BunitContext
{
    private static readonly Guid PointId = Guid.Parse("01a1f31e-0000-7000-8000-000000000001");
    private static readonly Guid VcenterId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000a1");
    private static readonly Guid OtherVcenterId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000a2");
    private static readonly Guid PoolId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000b1");
    private readonly FakeApi _api = new();

    public RestoreWizardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new ApiClient(new HttpClient(_api) { BaseAddress = new Uri("https://backup.test/") }));
        _api.Connections = [Connection(VcenterId, "VSphere", "Lab vCenter"), Connection(OtherVcenterId, "VSphere", "DR vCenter"), Connection(PoolId, "XenServer", "Lab pool")];
        _api.Inventory[VcenterId] =
        [
            Item("Vm", "5003ab12-original", "app-template", new() { ["folder"] = "group-v1001", ["resourcePool"] = "resgroup-1002" }),
            Item("Folder", "group-v1001", "backdafuckup-test", new() { ["path"] = "DC/vm/backdafuckup-test" }),
            Item("ResourcePool", "resgroup-1002", "backdafuckup-test", new() { ["path"] = "Cluster/backdafuckup-test" }),
            Item("Host", "host-31", "esx01.corp.example"),
            Item("Storage", "datastore-12", "alletra-ds1", new() { ["type"] = "VMFS" }),
            Item("Network", "network-101", "VM Network"),
        ];
        _api.Inventory[OtherVcenterId] =
        [
            Item("Storage", "datastore-77", "dr-ds1", new() { ["type"] = "VMFS" }),
            Item("Network", "network-9", "Isolated"),
        ];
    }

    [Fact]
    public void A_vsphere_vm_that_still_exists_needs_replace_rollback_or_a_new_location()
    {
        var cut = RenderWizard();
        cut.WaitForAssertion(() => Assert.Contains("app-template", cut.Markup, StringComparison.Ordinal));

        // The default is a new location: a new name, the original untouched.
        Assert.Contains("A new VM, by default named <b>app-template_restored</b>", cut.Markup, StringComparison.Ordinal);
        Assert.False(NextButton(cut).HasAttribute("disabled"));

        // The original location: says where and that the VM still exists, and does not go on until that is decided.
        cut.FindAll("input[type=radio]")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("The VM comes back as <b>app-template</b> in Lab vCenter", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("in its original folder and resource pool, its disks on their original datastores", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("(alletra-ds1).", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("<b>The VM still exists there.</b>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("app-template still exists in Lab vCenter: tick 'Replace the existing VM', use quick rollback, or restore to a new location.", cut.Markup, StringComparison.Ordinal);
        Assert.True(NextButton(cut).HasAttribute("disabled"));

        cut.FindAll("input[type=checkbox]").Last().Change(true);
        cut.WaitForAssertion(() => Assert.False(NextButton(cut).HasAttribute("disabled")));
    }

    [Fact]
    public void Targets_are_vcenters_only_and_every_choice_shows_its_name()
    {
        var cut = RenderWizard();
        cut.WaitForAssertion(() => Assert.Contains("app-template", cut.Markup, StringComparison.Ordinal));
        NextButton(cut).Click();

        // The target shows names, not ids: the vCenter, the original folder, resource pool and datastore.
        cut.WaitForAssertion(() => Assert.Contains("Only vCenters: a VM goes back to the kind of platform it was backed up from.", cut.Markup, StringComparison.Ordinal));
        var values = cut.FindAll("input").Select(i => i.GetAttribute("value")).ToList();
        Assert.Contains("Lab vCenter", values);
        Assert.Contains("DC/vm/backdafuckup-test", values);
        Assert.Contains("Cluster/backdafuckup-test", values);
        Assert.Contains("alletra-ds1 (VMFS)", values);
        Assert.DoesNotContain(values, v => v is not null && (v.StartsWith("datastore-", StringComparison.Ordinal) || v.StartsWith("group-", StringComparison.Ordinal) || Guid.TryParse(v, out _)));
        Assert.Contains("Was on alletra-ds1", cut.Markup, StringComparison.Ordinal);

        // The network step: the NIC's network by its name.
        NextButton(cut).Click();
        cut.WaitForAssertion(() => Assert.Contains("NIC 4000 (was VM Network, 00:50:56:aa:bb:01)", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("VM Network", cut.FindAll("input").Select(i => i.GetAttribute("value")));
        Assert.DoesNotContain("network-101", cut.FindAll("input").Select(i => i.GetAttribute("value")));
    }

    [Fact]
    public async Task Another_vcenter_without_the_datastore_or_network_asks_for_them_before_going_on()
    {
        var cut = RenderWizard();
        cut.WaitForAssertion(() => Assert.Contains("app-template", cut.Markup, StringComparison.Ordinal));
        NextButton(cut).Click();
        cut.WaitForAssertion(() => Assert.Contains("Lab vCenter", cut.FindAll("input").Select(i => i.GetAttribute("value"))));

        // Choose the other vCenter: the original datastore is not there, so one has to be chosen.
        var wizard = cut.FindComponent<RestoreWizard>();
        var select = cut.FindComponents<MudSelect<Guid>>().Single();
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(OtherVcenterId));
        cut.WaitForAssertion(() => Assert.Contains("Choose a datastore for Hard disk 1.", cut.Markup, StringComparison.Ordinal));
        Assert.True(NextButton(cut).HasAttribute("disabled"));
        Assert.NotNull(wizard);
    }

    private IRenderedComponent<IComponent> RenderWizard()
    {
        _api.Point = new RestorePointDto(
            PointId, Guid.NewGuid(), Guid.NewGuid(), "Template backup", VcenterId, "5003ab12-original", "app-template", DateTimeOffset.Parse("2026-11-19T08:53:00Z"), "Full", "Available",
            100L << 30, 70L << 30,
            [new RestorePointDiskDto("scsi0:0", "Hard disk 1", 100L << 30, "datastore-12", "alletra-ds1", "Full", null, 0, 0)],
            [new RestorePointNicDto("4000", "00:50:56:aa:bb:01", "network-101", "VM Network")],
            2, 8L << 30, "efi");
        return Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<RestoreWizard>(1);
            builder.AddAttribute(2, nameof(RestoreWizard.Id), PointId);
            builder.CloseComponent();
        });
    }

    private static AngleSharp.Dom.IElement NextButton(IRenderedComponent<IComponent> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Next");

    private static ConnectionDto Connection(Guid id, string kind, string name) =>
        new(id, kind, name, $"{name.ToLowerInvariant().Replace(' ', '-')}.corp.example", 443, null, null, null, null, "Connected", null, null, "backup", false, []);

    private static InventoryItemDto Item(string kind, string id, string name, Dictionary<string, string>? properties = null) =>
        new(kind, id, name, null, properties ?? []);

    /// <summary>The API as the wizard asks it.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public RestorePointDto? Point { get; set; }

        public List<ConnectionDto> Connections { get; set; } = [];

        public Dictionary<Guid, List<InventoryItemDto>> Inventory { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            object? body = path switch
            {
                _ when path == $"api/restore-points/{PointId}" => Point,
                "api/connections" => Connections,
                _ when path.StartsWith("api/connections/", StringComparison.Ordinal) && path.EndsWith("/inventory", StringComparison.Ordinal) =>
                    Inventory.GetValueOrDefault(Guid.Parse(path.Split('/')[2])) ?? [],
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body, body.GetType()) });
        }
    }
}
