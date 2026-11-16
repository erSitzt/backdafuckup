using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Testing.Kubernetes;

namespace Backdafuckup.Server.Tests;

/// <summary>A cluster's platforms: detected at the scan, set by hand, shown to the console (spec 2026-11-12-1952-kubernetes-platform-profiles).</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_clusters_platforms_are_detected_at_the_scan_and_can_be_set_by_hand()
    {
        var client = await SignedInClientAsync();
        var cluster = new FakeKubernetesApi("uid-kube-system-platforms") { GitVersion = "v1.32.13+rke2r1" };
        cluster.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("cattle-system"));
        cluster.Add("api/v1", "namespaces", FakeKubernetesApi.Namespace("cattle-fleet-system"));
        cluster.ExtraApiGroups.Add("crd.projectcalico.org");
        _clusters["10.26.5.10"] = cluster;

        // Detected when it is added: the product name says which platforms, the console gets them with their evidence.
        var added = await AddClusterAsync(client, "rke2-platforms", "10.26.5.10");
        var connection = (await client.GetFromJsonAsync<ConnectionDto>($"/api/connections/{added.Id}", Ct))!;
        Assert.Equal("RKE2 · Rancher · Fleet", connection.ProductName);
        var platforms = connection.Platforms!;
        Assert.Equal(["rke2", "rancher", "fleet", "calico"], platforms.Detected.Select(d => d.Id));
        Assert.Equal("version v1.32.13+rke2r1", platforms.Detected[0].Evidence);
        Assert.Null(platforms.Override);
        Assert.Equal(["kubernetes", "rke2", "rancher", "fleet", "calico"], platforms.Effective.Select(p => p.Id));
        Assert.Contains(platforms.Namespaces, n => n.Profile == "Rancher" && n.Patterns.Contains("cattle-*"));

        // Set by hand: the product name follows at once, and a rescan keeps the platforms set (still showing what it detects).
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/connections/{added.Id}/platforms", new SetConnectionPlatformsDto(["rke2", "kind"]), Ct)).StatusCode);
        var unknown = await client.PutAsJsonAsync($"/api/connections/{added.Id}/platforms", new SetConnectionPlatformsDto(["no-such-platform"]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("no-such-platform", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var manual = await Read<ConnectionDto>(client.PutAsJsonAsync($"/api/connections/{added.Id}/platforms", new SetConnectionPlatformsDto(["kind", "istio"]), Ct));
        Assert.Equal("kind", manual.ProductName);
        Assert.Equal(["kind", "istio"], manual.Platforms!.Override!);
        (await client.PostAsync($"/api/connections/{added.Id}/rescan", null, Ct)).EnsureSuccessStatusCode();
        connection = (await client.GetFromJsonAsync<ConnectionDto>($"/api/connections/{added.Id}", Ct))!;
        Assert.Equal("kind", connection.ProductName);
        Assert.Equal(["kubernetes", "kind", "istio"], connection.Platforms!.Effective.Select(p => p.Id));
        Assert.Equal("rke2", connection.Platforms.Detected[0].Id);

        // Back to detection.
        var detected = await Read<ConnectionDto>(client.PutAsJsonAsync($"/api/connections/{added.Id}/platforms", new SetConnectionPlatformsDto(null), Ct));
        Assert.Equal("RKE2 · Rancher · Fleet", detected.ProductName);
        Assert.Null(detected.Platforms!.Override);

        // The catalog for the dialog: every platform but the base, untested ones with their sources.
        var catalog = (await client.GetFromJsonAsync<List<KubernetesPlatformDto>>("/api/kubernetes/platforms", Ct))!;
        Assert.DoesNotContain(catalog, p => p.Id == "kubernetes");
        Assert.All(catalog.Where(p => !p.Tested), p => Assert.NotEmpty(p.Sources));
        Assert.Contains(catalog, p => p.Id == "rke2" && p.Category == "distribution" && p.Tested);
    }
}
