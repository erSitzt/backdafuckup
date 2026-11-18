using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Agent.Core;
using Backdafuckup.Contracts;
using Backdafuckup.DataMover;
using Backdafuckup.Proxy;
using Backdafuckup.Server.Agents;
using Backdafuckup.Testing.Vim;
using Microsoft.EntityFrameworkCore;
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

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
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

    [Fact]
    public async Task Without_our_agent_vmware_tools_quiesce_the_snapshot_and_an_agent_is_found_by_the_vms_bios_uuid()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (vmRef, vmId, disks) = lab.AddVm("db01", 16L << 20);
        lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(1 << 20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            // A Windows agent reports the BIOS serial vSphere gives the VM; it is matched by the VM's BIOS UUID to the VM's id.
            var biosUuid = Guid.Parse(lab.Vcenter.Get(vmRef, "config.uuid")!.Value);
            var hex = biosUuid.ToString("N");
            var serial = "VMware-" + string.Join(' ', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 2, 2))) + "-" + string.Join(' ', Enumerable.Range(8, 8).Select(i => hex.Substring(i * 2, 2)));
            var agentHttp = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var host = new Backdafuckup.Agent.Protocol.AgentHostInfo { HostName = "DB01", BiosSerial = serial, Build = Backdafuckup.Agent.Protocol.AgentProtocol.BuildNet10 };
            var secret = await _factory.Services.GetRequiredService<AgentSettingsStore>().GetEnrollmentSecretAsync(Ct);
            await Read<Backdafuckup.Agent.Protocol.AgentEnrollResponse>(agentHttp.PostAsJsonAsync("/api/agent/enroll", new Backdafuckup.Agent.Protocol.AgentEnrollRequest { EnrollmentSecret = secret, Host = host }, Ct));
            Assert.Equal(vmId, Assert.Single((await client.GetFromJsonAsync<List<AgentDto>>("/api/agents", Ct))!).VmNativeId);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/agents/{(await client.GetFromJsonAsync<List<AgentDto>>("/api/agents", Ct))![0].Id}", Ct)).StatusCode);

            // No agent and no credentials to install one: VMware Tools quiesce the snapshot (a Linux guest: file systems frozen).
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere tools", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto(vmId, "db01")], RetryCount = 0, GuestProcessing = true,
            }, Ct));
            var quiesced = await RunVsphereJobAsync(client, job.Id, "Warning");
            Assert.Contains(quiesced.Log, l => l.Contains("VMware Tools quiesce the snapshot instead", StringComparison.Ordinal));
            Assert.Contains(quiesced.Log, l => l.Contains("snapshot quiesced by VMware Tools: the file systems were frozen (fsfreeze)", StringComparison.Ordinal));
            Assert.Contains(lab.Events, e => e.EndsWith("db01 (quiesced)", StringComparison.Ordinal));

            // VMware Tools fail: the snapshot is taken again without quiescing, with a warning.
            lab.QuiesceFails = true;
            var crash = await RunVsphereJobAsync(client, job.Id, "Warning");
            Assert.Contains(crash.Log, l => l.Contains("VMware Tools could not quiesce the snapshot (An error occurred while quiescing the virtual machine.); it is taken without quiescing", StringComparison.Ordinal));
            Assert.Empty(lab.Snapshots());
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task A_vm_without_an_address_gets_our_agent_through_vmware_tools_and_the_agent_quiesces_its_backup()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (vmRef, vmId, disks) = lab.AddVm("win01", 16L << 20);
        lab.Vcenter.Set(vmRef, "config.guestId", Val.Str("windows2019srv_64Guest"));
        lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(1 << 20));
        var build = Path.Combine(_root, "agent", "net10");
        Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(Path.Combine(build, "backdafuckup-agent.exe"), "agent net10", Ct);
        await File.WriteAllTextAsync(Path.Combine(build, "bdfu-vss.dll"), "vss", Ct);

        // The service sc.exe starts is the agent, with the config the installation copied; it reports the VM's BIOS serial.
        const string folder = @"C:\Program Files\backdafuckup Agent";
        var hex = Guid.Parse(lab.Vcenter.Get(vmRef, "config.uuid")!.Value).ToString("N");
        var serial = "VMware-" + string.Join(' ', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 2, 2))) + "-" + string.Join(' ', Enumerable.Range(8, 8).Select(i => hex.Substring(i * 2, 2)));
        using var agentStop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task? agentRun = null;
        lab.OnServiceStarted = (vm, _) => agentRun ??= Task.Run(async () =>
        {
            var directory = Path.Combine(_root, "agent-" + vm);
            Directory.CreateDirectory(directory);
            var paths = new AgentPaths(directory);
            await File.WriteAllBytesAsync(paths.ConfigFile, lab.GuestFiles[$@"{vm}:{folder}\agent.json"], Ct);
            // A domain controller: its restore point may not be rolled back in place (see below).
            var runner = new AgentRunner(paths, new SimulatedHost(serial, domainController: true), new SimulatedVss(domainController: true), new AgentLog(paths.LogFile),
                _ => _factory.Server.CreateHandler(), TimeSpan.FromMilliseconds(200));
            await runner.RunAsync(agentStop.Token);
        }, Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var account = await Read<WindowsCredentialDto>(client.PostAsJsonAsync("/api/credentials", new SaveWindowsCredentialDto(@"CORP\backup-admin", "secret", null), Ct));
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere agent", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto(vmId, "win01")], RetryCount = 0, GuestProcessing = true, GuestCredentialId = account.Id,
            }, Ct));

            var backup = await RunVsphereJobAsync(client, job.Id);

            Assert.Contains(backup.Log, l => l.Contains(@"VM win01 reports no IP address to vCenter; installing through VMware Tools (guest operations) as CORP\backup-admin", StringComparison.Ordinal));
            Assert.Contains(backup.Log, l => l.Contains("The agent service runs in VM win01 (net10 build)", StringComparison.Ordinal));
            Assert.Equal(["agent.json", "backdafuckup-agent.exe", "bdfu-vss.dll"], lab.GuestFiles.Keys.Select(k => k[(k.LastIndexOf('\\') + 1)..]).Order(StringComparer.Ordinal));
            Assert.Equal("\"\\\"C:\\Program Files\\backdafuckup Agent\\backdafuckup-agent.exe\\\" run\"", lab.GuestServices["win01:BackdafuckupAgent"].BinaryPath);
            Assert.Equal(vmId, Assert.Single((await client.GetFromJsonAsync<List<AgentDto>>("/api/agents", Ct))!).VmNativeId);

            // Our agent froze the guest: VMware Tools did not quiesce the snapshot, and the restore point is application-consistent.
            Assert.Contains(lab.Events, e => e.EndsWith("of win01", StringComparison.Ordinal));
            Assert.DoesNotContain(lab.Events, e => e.EndsWith("win01 (quiesced)", StringComparison.Ordinal));
            var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
            Assert.Equal("application", point.Consistency);
            await AssertRestorePointMatchesAsync(point, disks);

            // A domain controller is not rolled back in place: it would keep its VM-Generation-ID (USN rollback).
            var rollback = await RestoreVsphereAsync(client, new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, QuickRollback = true }, "Failed");
            Assert.Contains(rollback.Log, l => l.Contains("win01 was a domain controller at the restore point. A quick rollback keeps its VM-Generation-ID", StringComparison.Ordinal));
            Assert.Empty(lab.Snapshots());
        }
        finally
        {
            await agentStop.CancelAsync();
            await stop.CancelAsync();
            await running;
            if (agentRun is not null)
            {
                await agentRun.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    [Fact]
    public async Task A_vsphere_restore_point_comes_back_as_a_new_vm_and_in_place_of_the_original_and_a_failed_restore_leaves_nothing()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (vmRef, vmId, disks) = lab.AddVm("app02", 24L << 20, 8L << 20);
        lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(2 << 20));
        lab.Write(disks[0], 20L << 20, RandomNumberGenerator.GetBytes(512 << 10));
        lab.Write(disks[1], 1L << 20, RandomNumberGenerator.GetBytes(1 << 20));
        var backedUp = disks.Select(lab.Content).ToList();
        var originalBios = lab.Vcenter.Get(vmRef, "config.uuid")!.Value;
        var originalMac = Assert.Single(lab.VmNics(vmRef)).Mac;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere restore", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto(vmId, "app02")], RetryCount = 0,
            }, Ct));
            await RunVsphereJobAsync(client, job.Id);
            var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
            lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(4096)); // after the backup: the restore brings the old content back
            var deviceCount = lab.Vcenter.Get(lab.ProxyVm, "config.hardware.device")!.Elements().Count();

            // As a new VM into another folder: the disks written through the proxy (only their data), new MACs, a new BIOS UUID.
            var copy = await RestoreVsphereAsync(client, new RestoreRequestDto
            {
                RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "app02-copy", RegenerateMacs = true, PowerOn = true,
                TargetFolderId = lab.TestFolder.Value, TargetResourcePoolId = lab.ResourcePool.Value,
            });
            Assert.Contains(copy.Log, l => l.Contains("Created disk Hard disk 1 as [ds-shared-01] app02-copy/app02-copy.vmdk (24 MiB, thin) on proxy proxy01", StringComparison.Ordinal));
            Assert.Contains(copy.Log, l => l.Contains("Created VM 'app02-copy'", StringComparison.Ordinal) && l.Contains("in folder 'backdafuckup-test' and resource pool 'Resources'", StringComparison.Ordinal));
            var copyVm = lab.FindVm("app02-copy")!.Value;
            Assert.Equal(lab.TestFolder.Value, lab.Vcenter.Get(copyVm, "parent")!.Value);
            Assert.Equal("poweredOn", lab.Vcenter.Get(copyVm, "runtime.powerState")!.Value);
            Assert.NotEqual(originalBios, lab.Vcenter.Get(copyVm, "config.uuid")!.Value);
            Assert.Equal("efi", lab.Vcenter.Get(copyVm, "config.firmware")!.Value);
            var copyDisks = lab.VmDisks(copyVm);
            Assert.Equal([(0, 0, "[ds-shared-01] app02-copy/app02-copy.vmdk"), (0, 1, "[ds-shared-01] app02-copy/app02-copy_1.vmdk")],
                copyDisks.Select(d => (d.Bus, d.Unit, d.FileName)).Order());
            Assert.True(lab.Content(copyDisks.Single(d => d.Unit == 0).Uuid).AsSpan().SequenceEqual(backedUp[0]), "disk 0 of the copy differs");
            Assert.True(lab.Content(copyDisks.Single(d => d.Unit == 1).Uuid).AsSpan().SequenceEqual(backedUp[1]), "disk 1 of the copy differs");
            var copyNic = Assert.Single(lab.VmNics(copyVm));
            Assert.Equal(("VirtualVmxnet3", lab.Network.Value, (int?)192, false), (copyNic.Type, copyNic.Network, copyNic.PciSlot, copyNic.StartConnected));
            Assert.NotEqual(originalMac, copyNic.Mac);
            Assert.Contains(copy.Log, l => l.StartsWith("Info: app02: The NICs of app02-copy start disconnected: the original VM app02 still exists", StringComparison.Ordinal));
            Assert.Equal(deviceCount, lab.Vcenter.Get(lab.ProxyVm, "config.hardware.device")!.Elements().Count());
            Assert.Contains(lab.Events, e => e.StartsWith("create [ds-shared-01] app02-copy/app02-copy.vmdk on proxy01", StringComparison.Ordinal));
            Assert.Contains(lab.Events, e => e == "detach [ds-shared-01] app02-copy/app02-copy.vmdk from proxy01");

            // CreateVM fails: the disk files this restore made are deleted again, nothing else is touched.
            lab.CreateVmFails = true;
            var failed = await RestoreVsphereAsync(client, new RestoreRequestDto
            {
                RestorePointId = point.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "app02-fail", TargetFolderId = lab.TestFolder.Value, TargetResourcePoolId = lab.ResourcePool.Value,
            }, "Failed");
            lab.CreateVmFails = false;
            Assert.Contains(failed.Log, l => l.Contains("Removed the partially restored VM and disks (the folder app02-fail stays, empty)", StringComparison.Ordinal));
            Assert.Null(lab.FindVm("app02-fail"));
            Assert.DoesNotContain(lab.DiskFiles.Keys, f => f.Contains("app02-fail", StringComparison.Ordinal));
            Assert.Equal(["delete [ds-shared-01] app02-fail/app02-fail.vmdk", "delete [ds-shared-01] app02-fail/app02-fail_1.vmdk"],
                lab.Events.Where(e => e.StartsWith("delete ", StringComparison.Ordinal)));
            Assert.Contains(lab.DiskFiles.Keys, f => f == "[ds-shared-01] app02/app02.vmdk");

            // To the original place: the original is shut down and renamed, the restored VM gets its name, BIOS UUID and MAC.
            var back = await RestoreVsphereAsync(client, new RestoreRequestDto
            {
                RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, ReplaceExisting = true, RegenerateMacs = false,
            });
            Assert.Contains(back.Log, l => l.Contains("The original VM is renamed to 'app02 (replaced ", StringComparison.Ordinal));
            Assert.StartsWith("app02 (replaced ", lab.Vcenter.Name(vmRef), StringComparison.Ordinal);
            Assert.Equal("poweredOff", lab.Vcenter.Get(vmRef, "runtime.powerState")!.Value);
            var restored = lab.FindVm("app02")!.Value;
            Assert.NotEqual(vmRef, restored);
            Assert.Equal(originalBios, lab.Vcenter.Get(restored, "config.uuid")!.Value);
            Assert.Equal(lab.VmFolder.Value, lab.Vcenter.Get(restored, "parent")!.Value);
            var restoredNic = Assert.Single(lab.VmNics(restored));
            Assert.Equal((originalMac, (int?)192, true), (restoredNic.Mac, restoredNic.PciSlot, restoredNic.StartConnected));
            Assert.Equal("poweredOff", lab.Vcenter.Get(restored, "runtime.powerState")!.Value);
            var restoredDisks = lab.VmDisks(restored);
            Assert.Equal("[ds-shared-01] app02_1/app02.vmdk", restoredDisks.Single(d => d.Unit == 0).FileName);
            Assert.True(lab.Content(restoredDisks.Single(d => d.Unit == 0).Uuid).AsSpan().SequenceEqual(backedUp[0]), "disk 0 of the restored VM differs");
            Assert.False(lab.Content(disks[0]).AsSpan().SequenceEqual(backedUp[0]), "the replaced original was written to");

            // Nothing a restore created is left open in the ledger.
            await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<Backdafuckup.Server.Data.AppDbContext>>().CreateDbContextAsync(Ct);
            Assert.Empty(await db.Artifacts.Where(a => a.Kind.StartsWith("vsphere.") && a.State != Backdafuckup.Core.Operations.ArtifactState.Done).ToListAsync(Ct));
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task A_quick_rollback_writes_back_only_what_changed_since_the_restore_point_and_a_failed_one_reverts_the_vm()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (vmRef, vmId, disks) = lab.AddVm("app03", 32L << 20);
        lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(6 << 20));
        lab.Write(disks[0], 28L << 20, RandomNumberGenerator.GetBytes(1 << 20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = "vSphere rollback", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
                Objects = [new JobObjectDto(vmId, "app03")], RetryCount = 0,
            }, Ct));
            await RunVsphereJobAsync(client, job.Id);
            var point = Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
            var backedUp = lab.Content(disks[0]);

            // After the backup: data overwritten, and data zeroed where the restore point has some.
            lab.Write(disks[0], 4L << 20, RandomNumberGenerator.GetBytes(256 << 10));
            lab.Write(disks[0], 28L << 20, new byte[64 << 10]);
            var rollback = await RestoreVsphereAsync(client, new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, QuickRollback = true });

            Assert.True(lab.Content(disks[0]).AsSpan().SequenceEqual(backedUp), "the disk is not as at the restore point");
            Assert.Contains(rollback.Log, l => l.Contains("Hard disk 1 of app03: 0.3 MiB changed since the restore point (2 area(s)); writing them back.", StringComparison.Ordinal));
            Assert.Contains(rollback.Log, l => l.Contains("Quick rollback of app03: 0.3 MiB changed since the restore point, 0.3 MiB written back.", StringComparison.Ordinal));
            Assert.Equal(vmRef, lab.FindVm("app03")!.Value);
            Assert.Equal("poweredOn", lab.Vcenter.Get(vmRef, "runtime.powerState")!.Value);
            Assert.Empty(lab.Snapshots());
            Assert.Contains(lab.Events, e => e == "snapshot backdafuckup rollback safety snapshot of app03");
            Assert.Contains(lab.Events, e => e.StartsWith("attach [ds-shared-01] app03/app03.vmdk to proxy01", StringComparison.Ordinal) && e.EndsWith(" persistent", StringComparison.Ordinal));
            Assert.Contains(lab.Events, e => e == "cbt off app03");

            // Change tracking was reset: the next backup reads the disk fully.
            var next = await RunVsphereJobAsync(client, job.Id, "Warning");
            Assert.Contains(next.Log, l => l.Contains("changed block tracking of the disk was reset since the last backup", StringComparison.Ordinal));

            // A rollback that cannot know the changes (tracking reset since that restore point) fails, and the VM is reverted.
            lab.Write(disks[0], 1L << 20, RandomNumberGenerator.GetBytes(128 << 10));
            var before = lab.Content(disks[0]);
            var failed = await RestoreVsphereAsync(client, new RestoreRequestDto { RestorePointId = point.Id, Mode = RestoreModeDto.OriginalLocation, QuickRollback = true }, "Failed");
            Assert.Contains(failed.Log, l => l.Contains("vCenter cannot list what changed on Hard disk 1 of app03 since the restore point", StringComparison.Ordinal));
            Assert.Contains(failed.Log, l => l.Contains("app03 is back as it was before it (reverted to the safety snapshot, which is removed). It was running before: start it again.", StringComparison.Ordinal));
            Assert.True(lab.Content(disks[0]).AsSpan().SequenceEqual(before), "the failed rollback changed the disk");
            Assert.Empty(lab.Snapshots());
            Assert.Contains(lab.Events, e => e == "revert app03");
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    private async Task<(string State, List<string> Log)> RestoreVsphereAsync(HttpClient client, RestoreRequestDto request, string expected = "Succeeded")
    {
        var started = await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", request, Ct));
        var detail = await WaitForSessionAsync(client, started.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}").ToList();
        Assert.True(detail.Session.State == expected, $"{detail.Session.State}: {string.Join(" | ", log)}");
        return (detail.Session.State, log);
    }

    /// <summary>The proxy enrolled and connected (its devices are the lab's hot-added disks), the vCenter added, a repository.</summary>
    private async Task<(ConnectionDto Vcenter, RepositoryDto Repository, Task Running)> StartVsphereAsync(HttpClient client, CancellationToken stop)
    {
        var lab = Vsphere;
        var proxy = await Read<ProxyInstallDto>(client.PostAsJsonAsync("/api/proxies", new CreateProxyDto("proxy01"), Ct));
        var key = (await Read<EnrollProxyResponseDto>(client.PostAsJsonAsync("/api/agent/proxies/enroll", new EnrollProxyRequestDto(proxy.Proxy.Id, proxy.InstallToken, "proxy01"), Ct))).AgentKey;
        var agent = new ProxyAgentClient(
            new ProxyAgentSettings(_factory!.Server.BaseAddress, proxy.Proxy.Id, key),
            new ProxyHostInfo("proxy01.corp.example", lab.ProxySmbiosUuid, null, ["10.26.16.91"], "test", null),
            lab.ProxyDevices, ConnectAgentSocketAsync, _ => { });
        var running = agent.RunAsync(stop);
        await WaitAsync(async () => (await client.GetFromJsonAsync<List<ProxyDto>>("/api/proxies", Ct))!.SingleOrDefault(p => p.Online));
        var vcenter = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            Kind = "VSphere", DisplayName = "Lab vCenter", Address = "vcenter.corp.example", UserName = lab.Vcenter.UserName, Password = lab.Vcenter.Password,
        }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        return (vcenter, repository, running);
    }

    private async Task<(string State, List<string> Log)> RunVsphereJobAsync(HttpClient client, Guid jobId, string expected = "Succeeded")
    {
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{jobId}/run", new RunJobDto(), Ct));
        var detail = await WaitForSessionAsync(client, started.SessionId);
        var log = (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{started.SessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}").ToList();
        Assert.True(detail.Session.State == expected, $"{detail.Session.State}: {string.Join(" | ", log)}");
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
