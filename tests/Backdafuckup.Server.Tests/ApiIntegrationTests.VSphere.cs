using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.DataMover;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing.Vim;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>vSphere VMs backed up through a HotAdd proxy (vSphere spec, T6/T7), against the fake vSphere.</summary>
public sealed partial class ApiIntegrationTests
{
    private FakeVsphereLab? _vsphere;

    private FakeVsphereLab Vsphere => _vsphere ??= new FakeVsphereLab(Path.Combine(_root, "vsphere"));

    [Fact]
    public async Task A_vsphere_vm_is_backed_up_through_a_hotadd_proxy_full_then_only_its_changes()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (_, vmId, disks) = lab.AddVm("app01", 32L << 20, 8L << 20);
        var first = RandomNumberGenerator.GetBytes(3 << 20);
        lab.Write(disks[0], 1L << 20, first);
        lab.Write(disks[1], 0, RandomNumberGenerator.GetBytes(256 << 10));

        // The proxy: enrolled, connected, its devices are the lab's hot-added disks.
        var proxy = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy01"), Ct));
        var key = (await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(proxy.Proxy.Id, proxy.InstallToken, "proxy01"), Ct))).AgentKey;
        var agent = new ProxyAgentClient(
            new ProxyAgentSettings(_factory.Server.BaseAddress, proxy.Proxy.Id, key),
            new ProxyHostInfo("proxy01.corp.example", lab.ProxySmbiosUuid, null, ["10.26.16.91"], "test", null),
            lab.ProxyDevices, ConnectAgentSocketAsync, _ => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = agent.RunAsync(stop.Token);
        try
        {
            await WaitAsync(async () => (await client.GetFromJsonAsync<List<ProxyDto>>("/api/proxies", Ct))!.SingleOrDefault(p => p.Online));

            var vcenter = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
            {
                Kind = "VSphere", DisplayName = "Lab vCenter", Address = "vcenter.corp.example", UserName = lab.Vcenter.UserName, Password = lab.Vcenter.Password,
            }, Ct));
            var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto(vmId, "app01")], RetryCount = 0,
            }, Ct));

            // Full: CBT is switched on, only the allocated blocks are read, through the proxy.
            var full = await RunVsphereJobAsync(client, job.Id);
            Assert.Contains(full.Log, l => l.Contains("changed block tracking switched on", StringComparison.Ordinal));
            Assert.Contains(full.Log, l => l.Contains("hot-added to proxy proxy01 (VM proxy01 on esx01.corp.example, SCSI 1:0)", StringComparison.Ordinal));
            var fullPoint = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
            await AssertRestorePointMatchesAsync(fullPoint, disks);
            var fullRead = await ReadBytesAsync(client, fullPoint.Id);
            Assert.True(fullRead["scsi0:0"] <= 4L << 20, $"full read {fullRead["scsi0:0"]} bytes of disk 0");

            // Incremental: only what changed since.
            lab.Write(disks[0], 20L << 20, RandomNumberGenerator.GetBytes(128 << 10));
            var incremental = await RunVsphereJobAsync(client, job.Id);
            var points = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!;
            var latest = points.OrderByDescending(p => p.CreatedUtc).First();
            await AssertRestorePointMatchesAsync(latest, disks);
            var incrementalRead = await ReadBytesAsync(client, latest.Id);
            Assert.Equal((1L << 20, 0L), (incrementalRead["scsi0:0"], incrementalRead["scsi0:1"]));

            // Every snapshot is removed, every hot-added disk detached from the proxy VM and removed from the proxy's kernel.
            Assert.Empty(lab.Snapshots());
            Assert.Equal(2 * 4, lab.Events.Count(e => e.StartsWith("attach", StringComparison.Ordinal) || e.StartsWith("detach", StringComparison.Ordinal)));
            Assert.Equal(4, lab.ProxyDevices.Deleted.Count);
            Assert.Equal(["snapshot", "attach", "attach", "detach", "detach", "remove"], lab.Events.Take(6).Select(e => e.Split(' ')[0]).Select(e => e == "remove" ? "remove" : e));
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    private async Task<(string State, List<string> Log)> RunVsphereJobAsync(HttpClient client, Guid jobId)
    {
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{jobId}/run", new RunJobDto(), Ct));
        var detail = await WaitForSessionAsync(client, started.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}").ToList();
        Assert.True(detail.Session.State == "Succeeded", $"{detail.Session.State}: {string.Join(" | ", log)}");
        return (detail.Session.State, log);
    }

    /// <summary>Each disk of the restore point equals the lab disk's content now (nothing wrote since the backup).</summary>
    private async Task AssertRestorePointMatchesAsync(RestorePointDto point, IReadOnlyList<string> disks)
    {
        var repository = await _factory!.Services.GetRequiredService<Backdafuckup.Server.Engine.RepositoryManager>().GetAsync(point.RepositoryId, Ct);
        await using var reader = await repository.OpenRestorePointAsync(point.Id, Ct);
        for (var i = 0; i < disks.Count; i++)
        {
            var image = await RestorePointDiskImage.OpenAsync(reader, $"scsi0:{i}", Ct);
            var content = new byte[image.Capacity];
            await image.ReadAsync(0, content, Ct);
            Assert.True(content.AsSpan().SequenceEqual(Vsphere.Content(disks[i])), $"disk scsi0:{i} of restore point {point.Id} differs");
        }
    }

    /// <summary>Bytes read per disk key in a restore point's backup.</summary>
    private static async Task<Dictionary<string, long>> ReadBytesAsync(HttpClient client, Guid restorePointId)
    {
        var point = (await client.GetFromJsonAsync<RestorePointDto>($"/api/restore-points/{restorePointId}", Ct))!;
        return point.Disks.ToDictionary(d => d.Key, d => d.ReadBytes, StringComparer.Ordinal);
    }
}
