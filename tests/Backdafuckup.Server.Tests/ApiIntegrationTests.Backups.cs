using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task The_backups_overview_has_one_row_per_vm_with_its_jobs_their_state_and_filters()
    {
        var client = await SignedInClientAsync();
        var connection = await Read<ConnectionDto>(client.PostAsJsonAsync("/api/connections", new CreateConnectionDto
        {
            DisplayName = "Lab pool",
            Address = "xcp1",
            UserName = "root",
            Password = "secret",
        }, Ct));
        var repository = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Local", Path = Path.Combine(_root, "repo") }, Ct));
        var berlin = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin");
        async Task<Guid> JobAsync(string name, ScheduleDto schedule, params (string Id, string Name)[] vms) =>
            (await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
            {
                Name = name,
                ConnectionId = connection.Id,
                RepositoryId = repository.Id,
                RetentionPoints = 7,
                Schedule = schedule,
                Objects = [.. vms.Select(v => new JobObjectDto(v.Id, v.Name))],
                RetryCount = 0,
            }, Ct))).Id;

        var nightly = await JobAsync("Nightly", berlin, ("vm-a", "app01"), ("vm-b", "app02"), ("vm-c", "app03"));
        var archive = await JobAsync("Archive", new ScheduleDto("Weekly", "02:00", Enum.GetValues<DayOfWeek>(), "UTC"), ("vm-a", "app01"), ("vm-d", "db01"));
        var paused = await JobAsync("Paused", berlin, ("vm-f", "old01"));
        await Ok(client.PostAsync($"/api/jobs/{paused}/enabled/false", null, Ct));
        var manual = await JobAsync("Manual", new ScheduleDto("None", "22:00", [], "UTC"), ("vm-g", "man01"));
        var retired = await JobAsync("Retired", berlin, ("vm-e", "gone01"));

        var now = DateTimeOffset.UtcNow;
        RestorePointEntity Point(Guid job, string jobName, string vm, string name, DateTimeOffset created) => new()
        {
            Id = Guid.CreateVersion7(),
            RepositoryId = repository.Id,
            JobId = job,
            JobName = jobName,
            ConnectionId = connection.Id,
            PlatformId = "lab-pool",
            VmNativeId = vm,
            VmName = name,
            CreatedUtc = created,
            Kind = RestorePointKind.Incremental,
            State = RestorePointState.Available,
            LogicalBytes = 1L << 30,
            NewStoredBytes = 1L << 20,
            Consistency = "application",
            SqlDatabaseCount = 0,
            DomainController = false,
            SqlBackupSetsRecorded = true,
        };
        await using (var db = await _factory!.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync(Ct))
        {
            var dc = Point(nightly, "Nightly", "vm-c", "app03", now.AddHours(-2));
            dc.Consistency = "crash";
            dc.ConsistencyReason = "Guest processing failed: the agent on app03 (10.0.0.3) did not answer within 60 s.";
            dc.DomainController = true;
            dc.AdDomain = "corp.local";
            var sql = Point(archive, "Archive", "vm-a", "app01", now.AddHours(-3));
            sql.SqlDatabaseCount = 3;
            db.RestorePoints.AddRange(
                Point(nightly, "Nightly", "vm-a", "app01", now.AddHours(-1)),
                Point(nightly, "Nightly", "vm-a", "app01", now.AddHours(-25)),
                sql,
                Point(nightly, "Nightly", "vm-b", "app02", now.AddDays(-4)),
                dc,
                Point(paused, "Paused", "vm-f", "old01", now.AddDays(-10)),
                Point(manual, "Manual", "vm-g", "man01", now.AddDays(-10)),
                Point(retired, "Retired", "vm-e", "gone01", now.AddDays(-5)));
            await db.SaveChangesAsync(Ct);
        }

        // A deleted job leaves its restore points without a job.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/jobs/{retired}", Ct)).StatusCode);

        async Task<List<BackupVmDto>> VmsAsync(string query = "") => (await client.GetFromJsonAsync<List<BackupVmDto>>($"/api/restore-points/vms{query}", Ct))!;
        var vms = await VmsAsync();
        Assert.Equal(["app01", "app02", "app03", "db01", "gone01", "man01", "old01"], vms.Select(v => v.VmName));
        var vm = vms.ToDictionary(v => v.VmName);

        // A VM in two jobs: both, each with its points; the VM's newest point is the nightly one.
        Assert.Equal(BackupVmStates.Ok, vm["app01"].State);
        Assert.Equal(3, vm["app01"].PointCount);
        Assert.Equal(3L << 20, vm["app01"].StoredBytes);
        Assert.Equal(["Archive", "Nightly"], vm["app01"].Jobs.Select(j => j.JobName));
        Assert.Equal([1, 2], vm["app01"].Jobs.Select(j => j.PointCount));
        Assert.All(vm["app01"].Jobs, j => Assert.Equal(BackupVmStates.Ok, j.State));
        Assert.Equal(nightly, vm["app01"].Latest!.JobId);
        Assert.Equal(3, vm["app01"].Jobs[0].Latest!.SqlDatabaseCount);
        Assert.Equal("Daily", vm["app01"].Jobs[1].Schedule!.Kind);
        Assert.Equal([repository.Id], vm["app01"].Jobs[1].RepositoryIds);

        // Missed the last nightly run that started over 12 hours ago.
        var stale = Assert.Single(vm["app02"].Jobs);
        Assert.Equal(BackupVmStates.Stale, vm["app02"].State);
        Assert.Equal(BackupVmStates.Stale, stale.State);
        Assert.True(stale.MissedRunUtc > stale.Latest!.CreatedUtc && stale.MissedRunUtc <= now.AddHours(-12), $"{stale.MissedRunUtc}");

        // A job member without a restore point, with the name the job knows.
        var none = Assert.Single(vm["db01"].Jobs);
        Assert.Equal((BackupVmStates.NoPoint, 0, archive, (BackupVmPointDto?)null), (vm["db01"].State, vm["db01"].PointCount, none.JobId!.Value, none.Latest));
        Assert.Equal(BackupVmStates.NoPoint, none.State);

        // The deleted job: no id, its name from the restore points.
        var gone = Assert.Single(vm["gone01"].Jobs);
        Assert.Equal((null, "Retired", false, BackupVmStates.NoJob), (gone.JobId, gone.JobName, gone.JobExists, gone.State));
        Assert.Equal(BackupVmStates.Ok, vm["gone01"].State);
        Assert.Equal(BackupVmStates.Disabled, Assert.Single(vm["old01"].Jobs).State);
        Assert.Equal(BackupVmStates.Manual, Assert.Single(vm["man01"].Jobs).State);
        Assert.Equal((true, "corp.local", "crash"), (vm["app03"].Latest!.DomainController, vm["app03"].Latest!.AdDomain, vm["app03"].Latest!.Consistency));

        // Filters: a VM keeps only the jobs that match.
        var archived = await VmsAsync("?q=ARCH");
        Assert.Equal(["app01", "db01"], archived.Select(v => v.VmName));
        Assert.Equal(["Archive"], archived[0].Jobs.Select(j => j.JobName));
        Assert.Equal(1, archived[0].PointCount);
        Assert.Equal(["Nightly"], (await VmsAsync("?q=app02"))[0].Jobs.Select(j => j.JobName));
        Assert.Equal(["app01", "app02", "app03"], (await VmsAsync($"?jobId={nightly}")).Select(v => v.VmName));
        Assert.Equal(["app01", "app02", "app03", "db01"], (await VmsAsync($"?jobId={nightly}&jobId={archive}")).Select(v => v.VmName));
        Assert.Equal(["gone01"], (await VmsAsync($"?jobId={Guid.Empty}")).Select(v => v.VmName));
        Assert.Equal(["app03"], (await VmsAsync("?content=ad")).Select(v => v.VmName));
        Assert.Equal(["app01"], (await VmsAsync("?content=sql")).Select(v => v.VmName));
        Assert.Equal(["app01", "app03"], (await VmsAsync("?content=ad&content=sql")).Select(v => v.VmName));
        Assert.Equal(["app03"], (await VmsAsync("?consistency=warning")).Select(v => v.VmName));
        Assert.Empty(await VmsAsync("?consistency=crash"));
        Assert.Equal(["app02"], (await VmsAsync("?state=stale")).Select(v => v.VmName));
        Assert.Equal(["app02", "db01"], (await VmsAsync("?state=stale&state=nopoint")).Select(v => v.VmName));
        Assert.Equal(["app02", "db01", "gone01", "man01", "old01"], (await VmsAsync("?olderThanDays=3")).Select(v => v.VmName));
        Assert.Equal(7, (await VmsAsync($"?repositoryId={repository.Id}")).Count);
        Assert.Empty(await VmsAsync($"?repositoryId={Guid.NewGuid()}"));
        var refused = await client.GetAsync("/api/restore-points/vms?content=dns", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Unknown content 'dns': use ad, sql.", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The points of one VM in one job, as before.
        Assert.Equal(2, (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?vmId=vm-a&jobId={nightly}", Ct))!.Count);

        // JSON is compressed when the client accepts it, but never where a response can carry a secret.
        using var compressed = new HttpRequestMessage(HttpMethod.Get, "/api/restore-points/vms");
        compressed.Headers.AcceptEncoding.ParseAdd("br");
        var response = await client.SendAsync(compressed, Ct);
        Assert.Equal(["br"], response.Content.Headers.ContentEncoding);
        await using (var brotli = new BrotliStream(await response.Content.ReadAsStreamAsync(Ct), CompressionMode.Decompress))
        {
            Assert.Equal(7, (await JsonSerializer.DeserializeAsync<List<BackupVmDto>>(brotli, JsonSerializerOptions.Web, Ct))!.Count);
        }

        using var token = new HttpRequestMessage(HttpMethod.Post, "/api/tokens") { Content = JsonContent.Create(new CreateApiTokenDto("overview", 1)) };
        token.Headers.AcceptEncoding.ParseAdd("br");
        var created = await client.SendAsync(token, Ct);
        Assert.True(created.IsSuccessStatusCode, $"{(int)created.StatusCode}");
        Assert.Empty(created.Content.Headers.ContentEncoding);
    }
}
