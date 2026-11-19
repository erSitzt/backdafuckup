using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Operations;
using Backdafuckup.Nfs;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.InstantRecovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// Instant recovery of vSphere VMs (spec 2, task 7) against the fake vSphere: start, the export ESXi reads, migration,
/// stop, a start that fails, and VMs that are removed by hand.
/// </summary>
public sealed partial class ApiIntegrationTests
{
    private static readonly NfsCaller EsxCaller = new(new AuthSys(0, "esx01", 0, 0, []), new IPEndPoint(IPAddress.Parse("192.0.2.21"), 900));

    [Fact]
    public async Task A_vsphere_vm_runs_from_its_restore_point_over_nfs_and_is_migrated_to_production_storage()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        var (_, _, disks) = lab.AddVm("app03", 24L << 20, 8L << 20);
        lab.Write(disks[0], 0, RandomNumberGenerator.GetBytes(2 << 20));
        lab.Write(disks[1], 1L << 20, RandomNumberGenerator.GetBytes(1 << 20));
        var content = disks.Select(lab.Content).ToList();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var point = await BackUpVsphereAsync(client, vcenter, repository, "app03");
            var server = await client.GetFromJsonAsync<InstantRecoveryServerDto>("/api/instant-recoveries/server", Ct);
            Assert.True(server is { Enabled: true, Listening: true, Running: 0 }, $"{server}");
            var check = await client.GetFromJsonAsync<InstantRecoveryHostDto>($"/api/instant-recoveries/host?connectionId={vcenter.Id}&hostId={lab.Host.Value}", Ct);
            Assert.Equal(("esx01.corp.example", "127.0.0.1", 0, 8), (check!.Name, check.MountFrom, check.NfsMounts, check.NfsMountLimit));
            Assert.Equal(["192.0.2.21", "198.51.100.21"], check.Addresses);
            Assert.Empty(check.Problems);
            var sessionId = (await Read<StartedDto>(client.PostAsJsonAsync("/api/instant-recoveries", new StartInstantRecoveryDto
            {
                ConnectionId = vcenter.Id, HostId = lab.Host.Value, Vms = [new InstantRecoveryVmRequestDto(point.Id)], FolderId = lab.TestFolder.Value,
            }, Ct))).SessionId;
            var recovery = await WaitForRecoveryAsync(sessionId, InstantRecoveryState.Running);

            // vCenter: our export mounted from the listen address, the VM registered around its files there and powered on.
            Assert.Contains($"mount 127.0.0.1:{recovery.ExportPath} as {recovery.DatastoreName}", lab.Events);
            var vm = lab.FindVm("app03 (instant)")!.Value;
            Assert.Equal([$"[{recovery.DatastoreName}] app03 (instant)/disk0.vmdk", $"[{recovery.DatastoreName}] app03 (instant)/disk1.vmdk"], lab.VmDisks(vm).Select(d => d.FileName));
            Assert.Equal("poweredOn", lab.Vcenter.Get(vm, "runtime.powerState")?.Value);
            Assert.Equal(["192.0.2.21", "198.51.100.21"], recovery.AllowedClients);
            Assert.Equal("esx01.corp.example", recovery.HostName);

            // The export: the flat files are the restore point's disks, the descriptors name them; what ESXi writes stays in the overlay.
            var nfs = _factory.Services.GetRequiredService<NfsServerHost>();
            var export = nfs.Exports.FindByPath(recovery.ExportPath)!;
            var descriptor = Encoding.UTF8.GetString(await ReadFileAsync(export, "app03 (instant)", "disk0.vmdk"));
            Assert.Contains($"RW {(24L << 20) / 512} VMFS \"disk0-flat.vmdk\"", descriptor, StringComparison.Ordinal);
            var flat0 = await ReadFileAsync(export, "app03 (instant)", "disk0-flat.vmdk");
            var flat1 = await ReadFileAsync(export, "app03 (instant)", "disk1-flat.vmdk");
            Assert.True(content[0].AsSpan().SequenceEqual(flat0), "disk0-flat.vmdk is the restore point's first disk");
            Assert.True(content[1].AsSpan().SequenceEqual(flat1), "disk1-flat.vmdk is the restore point's second disk");
            var flat = await LookupPathAsync(export, "app03 (instant)", "disk0-flat.vmdk");
            await export.FileSystem.WriteAsync(EsxCaller, flat, 4096, new byte[4096], NfsStable.FileSync, Ct);
            Assert.True(Directory.Exists(recovery.OverlayDirectory));

            // The list shows it running, with what ESXi wrote.
            var listed = Assert.Single((await client.GetFromJsonAsync<List<InstantRecoveryDto>>("/api/instant-recoveries", Ct))!, r => r.SessionId == sessionId);
            Assert.Equal(("Running", "esx01.corp.example", "app03 (instant)", "app03"), (listed.State, listed.HostName, listed.Vms[0].Name, listed.Vms[0].SourceVmName));
            Assert.True(listed.OverlayBytes >= 4096, $"overlay {listed.OverlayBytes}");

            // Migrate: not onto its own datastore; Storage vMotion to the production datastore, then the instant recovery's datastore goes.
            var own = await client.PostAsJsonAsync($"/api/instant-recoveries/{sessionId}/migrate", new MigrateInstantRecoveryDto(recovery.DatastoreId!), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/api/instant-recoveries/{sessionId}/migrate", new MigrateInstantRecoveryDto(lab.Datastore.Value), Ct)).StatusCode);
            var detail = await WaitForSessionAsync(client, sessionId);
            var log = await SessionLogAsync(client, sessionId);
            Assert.True(detail.Session.State == "Succeeded", $"{detail.Session.State}: {string.Join(" | ", log)}");
            Assert.Contains(log, l => l.Contains("Moving VM app03 (instant) to datastore ds-shared-01 (thin disks) by Storage vMotion", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("VM app03 (instant) now runs from datastore ds-shared-01.", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains($"Migrated: app03 (instant) runs from production storage; datastore {recovery.DatastoreName} is gone", StringComparison.Ordinal));
            Assert.Equal(["relocate app03 (instant) to ds-shared-01", $"unmount {recovery.DatastoreName}"],
                lab.Events.SkipWhile(e => !e.StartsWith("relocate", StringComparison.Ordinal)));
            Assert.True(lab.Vcenter.Exists(vm));

            // Nothing of the instant recovery is left: export, overlay, open ledger rows; its restore point is free again.
            Assert.Null(nfs.Exports.FindByPath(recovery.ExportPath));
            Assert.False(Directory.Exists(recovery.OverlayDirectory));
            await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct);
            Assert.Equal(InstantRecoveryState.Migrated, (await db.InstantRecoveries.SingleAsync(r => r.SessionId == sessionId, Ct)).State);
            Assert.All(await db.Artifacts.Where(a => a.SessionId == sessionId).ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
            Assert.Null(await InstantRecoveryCatalog.FindUseAsync(db, point.Id, Ct));
            var again = await client.PostAsync($"/api/instant-recoveries/{sessionId}/stop", null, Ct);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("The instant recovery is Migrated; only a running one can be stopped.", await again.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task A_start_that_fails_removes_what_it_made_and_a_stop_of_the_session_powers_off_and_unregisters_only()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        lab.AddVm("app04", 16L << 20);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var point = await BackUpVsphereAsync(client, vcenter, repository, "app04");
            var control = _factory!.Services.GetRequiredService<InstantRecoveryControl>();
            var request = new InstantRecoveryRequest(vcenter.Id, lab.Host.Value, [new InstantRecoveryVmRequest(point.Id, "app04 (instant)")]) { FolderId = lab.TestFolder.Value };

            // The host cannot mount: the session fails naming host, server, path and ports, and what the NFS server saw of the host.
            lab.NasMountFails = "NFS mount 127.0.0.1:/bdfu failed: Unable to connect to NFS server.";
            var failed = await control.StartAsync(request, "test", Ct);
            var detail = await WaitForSessionAsync(client, failed);
            Assert.Equal("Failed", detail.Session.State);
            Assert.Contains("ESXi host esx01.corp.example could not mount 127.0.0.1:/bdfu/", detail.Session.Summary, StringComparison.Ordinal);
            Assert.Contains("192.0.2.21 has not reached this server on any of", detail.Session.Summary, StringComparison.Ordinal);
            Assert.Contains("(portmapper)", detail.Session.Summary, StringComparison.Ordinal);
            var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                var row = await db.InstantRecoveries.SingleAsync(r => r.SessionId == failed, Ct);
                Assert.Equal(InstantRecoveryState.Failed, row.State);
                Assert.False(Directory.Exists(row.OverlayDirectory));
                Assert.Null(_factory.Services.GetRequiredService<NfsServerHost>().Exports.FindByPath(row.ExportPath));
                Assert.All(await db.Artifacts.Where(a => a.SessionId == failed).ToListAsync(Ct), a => Assert.Equal(ArtifactState.Done, a.State));
            }

            // Started again, then the session is stopped in the console: the VM is powered off and unregistered, the datastore unmounted.
            lab.NasMountFails = null;
            var sessionId = await control.StartAsync(request, "test", Ct);
            var recovery = await WaitForRecoveryAsync(sessionId, InstantRecoveryState.Running);
            await Ok(client.PostAsync($"/api/sessions/{sessionId}/stop", null, Ct));
            detail = await WaitForSessionAsync(client, sessionId);
            var log = await SessionLogAsync(client, sessionId);
            Assert.True(detail.Session.State == "Stopped", $"{detail.Session.State}: {string.Join(" | ", log)}");
            Assert.Contains(log, l => l.Contains("The session was stopped: the instant recovery stops", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains($"Stopped: app04 (instant) removed, datastore {recovery.DatastoreName} unmounted", StringComparison.Ordinal));
            Assert.Equal(["power off app04 (instant)", "unregister app04 (instant)", $"unmount {recovery.DatastoreName}"],
                lab.Events.SkipWhile(e => e != "power off app04 (instant)"));
            Assert.DoesNotContain(lab.Events, e => e.StartsWith("destroy", StringComparison.Ordinal) || e.StartsWith("delete", StringComparison.Ordinal));
            Assert.Null(lab.FindVm("app04 (instant)"));
            Assert.True(lab.FindVm("app04").HasValue);
            Assert.False(Directory.Exists(recovery.OverlayDirectory));
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task A_refused_stop_keeps_the_vm_running_and_a_vm_removed_by_hand_ends_the_instant_recovery()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var lab = Vsphere;
        lab.AddVm("app05", 16L << 20);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (vcenter, repository, running) = await StartVsphereAsync(client, stop.Token);
        try
        {
            var point = await BackUpVsphereAsync(client, vcenter, repository, "app05");
            var control = _factory!.Services.GetRequiredService<InstantRecoveryControl>();
            var sessionId = await control.StartAsync(
                new InstantRecoveryRequest(vcenter.Id, lab.Host.Value, [new InstantRecoveryVmRequest(point.Id, "app05 (instant)")]) { FolderId = lab.TestFolder.Value }, "test", Ct);
            var recovery = await WaitForRecoveryAsync(sessionId, InstantRecoveryState.Running);

            // A disk on production storage was added by hand: the stop is refused, the VM keeps running, the session too.
            var vm = lab.FindVm("app05 (instant)")!.Value;
            lab.AddForeignDisk(vm, "[ds-shared-01] data/data.vmdk", 1L << 20);
            Assert.True(await control.RequestStopAsync(sessionId, Ct));
            recovery = await WaitForRecoveryAsync(sessionId, InstantRecoveryState.Running, r => r.Message is not null);
            Assert.Contains("has files on other datastores than", recovery.Message, StringComparison.Ordinal);
            Assert.Equal("poweredOn", lab.Vcenter.Get(vm, "runtime.powerState")?.Value);
            Assert.Equal("Running", (await client.GetFromJsonAsync<SessionDetailDto>($"/api/sessions/{sessionId}", Ct))!.Session.State);

            // The administrator removes the VM in vCenter: the instant recovery notices and ends; its datastore goes.
            lab.RemoveVmByHand(vm);
            var detail = await WaitForSessionAsync(client, sessionId);
            var log = await SessionLogAsync(client, sessionId);
            Assert.True(detail.Session.State == "Succeeded", $"{detail.Session.State}: {string.Join(" | ", log)}");
            Assert.Contains(log, l => l.Contains($"VM app05 (instant) ({vm.Value}) was removed from vCenter outside the backup server.", StringComparison.Ordinal));
            Assert.Contains($"unmount {recovery.DatastoreName}", lab.Events);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }

    /// <summary>A job for the VM, run once; its restore point.</summary>
    private async Task<RestorePointDto> BackUpVsphereAsync(HttpClient client, ConnectionDto vcenter, RepositoryDto repository, string vmName)
    {
        var vm = Vsphere.FindVm(vmName)!.Value;
        var job = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = $"IR {vmName}", ConnectionId = vcenter.Id, RepositoryId = repository.Id, RetentionPoints = 7, Schedule = new ScheduleDto("None", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(Vsphere.Vcenter.Get(vm, "config.instanceUuid")!.Value, vmName)], RetryCount = 0,
        }, Ct));
        await RunVsphereJobAsync(client, job.Id);
        return Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job.Id}", Ct))!);
    }

    private async Task<InstantRecoveryEntity> WaitForRecoveryAsync(Guid sessionId, InstantRecoveryState state, Func<InstantRecoveryEntity, bool>? also = null)
    {
        var dbFactory = _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InstantRecoveryEntity? last = null;
        for (var i = 0; i < 300; i++)
        {
            await using (var db = await dbFactory.CreateDbContextAsync(Ct))
            {
                last = await db.InstantRecoveries.AsNoTracking().Include(r => r.Vms).SingleOrDefaultAsync(r => r.SessionId == sessionId, Ct);
                if (last?.State == state && (also is null || also(last)))
                {
                    return last;
                }

                if (last?.State == InstantRecoveryState.Failed && state != InstantRecoveryState.Failed)
                {
                    var log = await db.SessionLog.Where(l => l.SessionId == sessionId).Select(l => l.Message).ToListAsync(Ct);
                    Assert.Fail($"The instant recovery failed: {last.Message} | {string.Join(" | ", log)}");
                }
            }

            await Task.Delay(100, Ct);
        }

        throw new TimeoutException($"The instant recovery did not reach {state}; it is {last?.State} ({last?.Message}).");
    }

    private static async Task<List<string>> SessionLogAsync(HttpClient client, Guid sessionId) =>
        (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{sessionId}/log", Ct))!.Select(l => $"{l.Severity}: {l.Message}").ToList();

    private static async Task<ReadOnlyMemory<byte>> LookupPathAsync(NfsExport export, params string[] path)
    {
        var handle = export.FileSystem.RootHandle;
        foreach (var name in path)
        {
            handle = (await export.FileSystem.LookupAsync(EsxCaller, handle, name, Ct)).Handle;
        }

        return handle;
    }

    private static async Task<byte[]> ReadFileAsync(NfsExport export, params string[] path)
    {
        var handle = await LookupPathAsync(export, path);
        var size = (long)(await export.FileSystem.GetAttributesAsync(EsxCaller, handle, Ct)).Size;
        var data = new byte[size];
        for (long offset = 0; offset < size;)
        {
            var read = await export.FileSystem.ReadAsync(EsxCaller, handle, offset, (int)Math.Min(1 << 20, size - offset), Ct);
            if (read.Data.Length == 0)
            {
                break;
            }

            read.Data.CopyTo(data.AsMemory((int)offset));
            offset += read.Data.Length;
        }

        return data;
    }
}
