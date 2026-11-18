using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>A third connection kind (vCenter) next to pools and clusters: lists by kind, and restores stay on their kind of platform.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task Connections_are_listed_by_several_kinds_and_a_vm_is_not_restored_to_another_kind_of_platform()
    {
        var client = await SignedInClientAsync();
        var point = await BackUpFixtureAsync(client, "kinds01", "ntfs-gpt");

        // A vCenter as the catalog would hold it (its provider comes with the vSphere spec; the listing needs only the row).
        Guid vcenterId;
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var now = DateTimeOffset.UtcNow;
            var vcenter = new HypervisorConnection
            {
                Kind = Core.Hypervisor.HypervisorKind.VSphere,
                DisplayName = "Lab vCenter",
                Address = "vcenter.corp.example",
                PlatformId = "vc-instance-1",
                Credential = new Credential { Username = "backup@vsphere.local", ProtectedSecret = "x", CreatedUtc = now, UpdatedUtc = now },
            };
            db.Connections.Add(vcenter);
            await db.SaveChangesAsync(Ct);
            vcenterId = vcenter.Id;
        }

        var both = (await client.GetFromJsonAsync<List<ConnectionDto>>("/api/connections?kind=XenServer,VSphere", Ct))!;
        Assert.Equal(["Lab pool", "Lab vCenter"], both.Select(c => c.DisplayName));
        Assert.Equal(["Lab vCenter"], (await client.GetFromJsonAsync<List<ConnectionDto>>("/api/connections?kind=vsphere", Ct))!.Select(c => c.DisplayName));
        var unknown = await client.GetAsync("/api/connections?kind=XenServer,Proxmox", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("Unknown connection kind 'Proxmox' (XenServer, Kubernetes or VSphere)", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A VM of the pool cannot go to the vCenter: its XAPI metadata means nothing there.
        var refused = await client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = point.Id, TargetConnectionId = vcenterId, Mode = RestoreModeDto.AlternateLocation,
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("kinds01 was backed up from XCP-ng ('Lab pool'): it cannot be restored to 'Lab vCenter', a vSphere vCenter.", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
