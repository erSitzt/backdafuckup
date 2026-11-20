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

/// <summary>The storage systems page says whether backups use an array, and why not: readers off, nothing turned on, an account that may only look.</summary>
public sealed class StorageSystemsPageTests : BunitContext
{
    private static readonly Guid SystemId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000d1");
    private readonly FakeApi _api = new();

    public StorageSystemsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new ApiClient(new HttpClient(_api) { BaseAddress = new Uri("https://backup.test/") }));
    }

    [Theory]
    [InlineData(false, 2, "edit in domain bdfu-test", "Not used by backups: the experimental readers are off", false)]
    [InlineData(true, 0, "edit in domain bdfu-test", "Not used by backups: no volume is turned on.", false)]
    [InlineData(true, 2, "browse in all domains", "Not used by backups: the account backup-ro may not take snapshots", true)]
    [InlineData(true, 2, "edit in domain bdfu-test", "Used by backups: jobs with 'Backup from storage snapshots' read VMs whose disks all lie on the 2 turned-on volume(s)", false)]
    public void The_page_says_whether_backups_use_the_array_and_why_not(bool experimental, int turnedOn, string privilege, string usage, bool rightsWarning)
    {
        _api.Answers["api/storage-systems"] = new List<StorageSystemDto>
        {
            new(SystemId, "AlletraMp", "array01", "array01.corp.example", 443, "backup-ro", "Connected", null, DateTimeOffset.UtcNow,
                new StorageSystemFactsDto("array01", "HPE Alletra Storage MP", "CZ0000TEST", "2FF70002AC000001", "10.5.51", "WSAPI v3", [], [privilege], 900, null),
                new StorageSystemOptionsDto(6, 8, 60, experimental, 5, 2), [], 4, turnedOn),
        };
        _api.Answers[$"api/storage-systems/{SystemId}/volumes"] = new List<StorageVolumeDto>();

        Render<MudPopoverProvider>();
        var cut = Render<StorageSystems>();
        cut.WaitForAssertion(() => Assert.Contains("array01", cut.Markup, StringComparison.Ordinal));

        Assert.Contains(usage, cut.Markup, StringComparison.Ordinal);
        Assert.Equal(rightsWarning, cut.Markup.Contains("can read the\n                    array, but not take", StringComparison.Ordinal) || cut.Markup.Contains("but not take, present or delete snapshots", StringComparison.Ordinal));
        Assert.Contains(", WSAPI v3", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("WSAPI WSAPI", cut.Markup, StringComparison.Ordinal);
        Assert.Contains($"rights: {privilege}", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_missing_host_object_is_explained_and_the_dialog_shows_the_rest_call_and_cli_line_before_anything_is_created(bool mayChange)
    {
        var proxyId = Guid.Parse("01a1f31e-0000-7000-8000-0000000000e1");
        const string iqn = "iqn.2004-10.com.ubuntu:01:0a1b2c3d4e5";
        _api.Answers["api/storage-systems"] = new List<StorageSystemDto>
        {
            new(SystemId, "AlletraMp", "array01", "array01.corp.example", 443, "bdfu-backup", "Connected", null, DateTimeOffset.UtcNow,
                new StorageSystemFactsDto("array01", "HPE Alletra Storage MP", "CZ0000TEST", "2FF70002AC000001", "10.5.51", "WSAPI v3", [], ["edit in all domains"], 900, null),
                new StorageSystemOptionsDto(6, 8, 60, true, 5, 2), [], 2, 1),
        };
        _api.Answers[$"api/storage-systems/{SystemId}/volumes"] = new List<StorageVolumeDto>();
        _api.Answers[$"api/storage-systems/{SystemId}/proxy-access"] = new StorageProxyAccessDto(
            [new StorageProxyHostDto(proxyId, "proxy01", true, iqn, null, null, null, null, [], ["192.0.2.10:3260 (no TCP connection within 3 s)", "192.0.2.11:3260 (no TCP connection within 3 s)"],
                ["198.51.100.91/24 (ens192)"])],
            ["192.0.2.10:3260", "192.0.2.11:3260"], null, null, null, Replicated: true);
        const string cli = $"createhost -iscsi -persona 2 -os Linux -comment \"backdafuckup proxy proxy01\" bdfu-proxy01 {iqn}";
        _api.Answers[$"api/storage-systems/{SystemId}/proxy-access/plan"] = new StorageHostPlanDto(
            "proxy01", iqn, "bdfu-proxy01", null, [$"No host object on array01 lists the initiator {iqn} yet.", "No host object is named bdfu-proxy01."],
            [new StorageArrayChangeDto("Create host object bdfu-proxy01 for the iSCSI initiator …", "POST", "/api/v3/hosts", "{\n  \"name\": \"bdfu-proxy01\"\n}", cli, "removehost bdfu-proxy01")],
            null, mayChange, mayChange ? null : "The account bdfu-backup may not change the array (browse in all domains). Run the CLI line as an array administrator, or give the account the role edit.");

        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<StorageSystems>();
        cut.WaitForAssertion(() => Assert.Contains("Create host object…", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("none lists its initiator: backups cannot present snapshots to this proxy", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("192.0.2.10:3260, 192.0.2.11:3260", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("give each proxy a host object on both arrays", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Reaches none of the 2 iSCSI addresses", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Its own addresses: 198.51.100.91/24 (ens192).", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").First(b => b.TextContent.Contains("Create host object", StringComparison.Ordinal)).Click();
        dialogs.WaitForAssertion(() => Assert.Contains("removehost bdfu-proxy01", dialogs.Markup, StringComparison.Ordinal));

        Assert.Contains("POST /api/v3/hosts", dialogs.Markup, StringComparison.Ordinal);
        Assert.Contains(cli, dialogs.FindAll("pre").Select(pre => pre.TextContent.Trim()));
        Assert.Contains("Nothing is changed on the array until you click", dialogs.Markup, StringComparison.Ordinal);
        var create = dialogs.FindAll("button").Single(b => b.TextContent.Contains("Create on the array", StringComparison.Ordinal));
        Assert.Equal(!mayChange, create.HasAttribute("disabled"));
        Assert.Equal(!mayChange, dialogs.Markup.Contains("Run the CLI line as an array administrator", StringComparison.Ordinal));
    }

    [Fact]
    public void Editing_opens_with_the_stored_settings_and_saving_without_a_password_keeps_the_stored_one()
    {
        var system = new StorageSystemDto(SystemId, "AlletraMp", "array01", "array01.corp.example", 443, "backup-ro", "Connected", null, DateTimeOffset.UtcNow,
            new StorageSystemFactsDto("array01", "HPE Alletra Storage MP", "CZ0000TEST", "2FF70002AC000001", "10.5.51", "WSAPI v3", [], ["browse in all domains"], 900, null),
            new StorageSystemOptionsDto(6, 8, 60, false, 5, 2), [], 0, 0);
        _api.Answers["api/storage-systems"] = new List<StorageSystemDto> { system };
        _api.Answers[$"api/storage-systems/{SystemId}"] = system;
        _api.Answers[$"api/storage-systems/{SystemId}/volumes"] = new List<StorageVolumeDto>();
        _api.Answers[$"api/storage-systems/{SystemId}/proxy-access"] = new StorageProxyAccessDto([], [], null, null, null, false);

        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<StorageSystems>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label=Edit]")));
        cut.Find("button[aria-label=Edit]").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Empty: the stored password stays.", dialogs.Markup, StringComparison.Ordinal));

        Assert.Contains(dialogs.FindAll("input"), i => i.GetAttribute("value") == "array01.corp.example");
        Assert.DoesNotContain("Show certificate", dialogs.Markup, StringComparison.Ordinal); // same address: no new certificate
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();

        dialogs.WaitForAssertion(() => Assert.Contains(_api.Changes, c => c.StartsWith($"PUT api/storage-systems/{SystemId} ", StringComparison.Ordinal)));
        var body = System.Text.Json.Nodes.JsonNode.Parse(_api.Changes.Single(c => c.StartsWith("PUT ", StringComparison.Ordinal)).Split(' ', 3)[2])!;
        Assert.Equal(("array01", "array01.corp.example", "backup-ro"), (body["name"]!.GetValue<string>(), body["address"]!.GetValue<string>(), body["userName"]!.GetValue<string>()));
        Assert.Null(body["password"]);
        Assert.Empty(body["trustedCertificates"]!.AsArray());
    }

    /// <summary>Answers GET requests from a table of paths (without the query); everything else is 404.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public Dictionary<string, object> Answers { get; } = new(StringComparer.Ordinal);

        /// <summary>"METHOD path body" of every request that is not a GET.</summary>
        public List<string> Changes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method != HttpMethod.Get)
            {
                lock (Changes)
                {
                    Changes.Add($"{request.Method} {path} {request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()}");
                }
            }

            return Task.FromResult(Answers.TryGetValue(path, out var answer)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(answer, answer.GetType()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
