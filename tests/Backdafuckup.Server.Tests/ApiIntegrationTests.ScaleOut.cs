using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Xen.Xapi;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Scale-out repositories: converting, placement by room, moving chains with their ids, sealing, evacuating, removing.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_converted_scale_out_places_chains_by_room_moves_them_with_their_ids_and_empties_an_extent()
    {
        var client = await SignedInClientAsync();
        var image = RandomNumberGenerator.GetBytes(4 << 20);
        var first = await BackUpImageAsync(client, "vm1", image);
        var jobId = first.JobId!.Value;
        var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
        var vm1 = first.VmNativeId;

        async Task<SessionDetailDto> RunAsync(Guid job) => await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{job}/run", new RunJobDto(), Ct))).SessionId);
        async Task<List<RestorePointDto>> PointsAsync(Guid job) => [.. (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={job}", Ct))!.OrderBy(p => p.CreatedUtc)];
        async Task<List<string>> LogAsync(Guid session) => [.. (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{session}/log", Ct))!.Select(l => l.Message)];
        async Task<SessionDetailDto> MoveAsync(Guid scaleOutId, StartMovesDto request) =>
            await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/repositories/{scaleOutId}/moves", request, Ct))).SessionId);

        // Converting needs a name for the scale-out repository; then Local is extent 1 and its job writes to the scale-out.
        var extentPath = Path.Combine(_root, "extent2");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = extentPath }, Ct)).StatusCode);
        var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = extentPath, ScaleOutName = "Scale" }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        var repositories = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!;
        var scale = repositories.Single(r => r.Name == "Scale");
        var e2 = repositories.Single(r => r.Name == "E2");
        Assert.Equal(("ScaleOut", 2, 1), (scale.Kind, scale.Extents, scale.RestorePoints));
        Assert.Equal((scale.Id, scale.Id, "Active"), (repositories.Single(r => r.Name == "Local").ScaleOutId, e2.ScaleOutId, e2.ExtentState));
        Assert.Equal(scale.Id, (await client.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}", Ct))!.RepositoryId);

        // An extent is no job target; the chain stays on Local (its next run is incremental there).
        var second = await RunAsync(jobId);
        Assert.Equal("Succeeded", second.Session.State);
        Assert.Contains(await LogAsync(second.Session.Id), l => l.Contains("Extent Local of Scale", StringComparison.Ordinal));
        var points = await PointsAsync(jobId);
        Assert.Equal([("Full", local.Id), ("Incremental", local.Id)], points.Select(p => (p.Kind, p.RepositoryId)).ToArray());

        // A new chain goes where there is most room: E2.
        _volumes.Set(Path.Combine(_root, "repo"), 200L << 30, 20L << 30);
        _volumes.Set(extentPath, 200L << 30, 150L << 30);
        var (vm2, disks2) = _pool.AddVm("vm2", 4 << 20);
        _pool.WriteDisk(disks2[0], 0, RandomNumberGenerator.GetBytes(4 << 20));
        var job = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{jobId}", Ct);
        var other = await Read<IdOnly>(client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "Second", ConnectionId = job!.ConnectionId, RepositoryId = scale.Id, RetentionPoints = 7, Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vm2, "vm2")], RetryCount = 0,
        }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/jobs", new SaveJobDto
        {
            Name = "To an extent", ConnectionId = job.ConnectionId, RepositoryId = e2.Id, RetentionPoints = 7, Schedule = new ScheduleDto("Daily", "22:00", [], "Europe/Berlin"),
            Objects = [new JobObjectDto(vm2, "vm2")], RetryCount = 0,
        }, Ct)).StatusCode);
        Assert.Equal("Succeeded", (await RunAsync(other.Id)).Session.State);
        Assert.Equal(e2.Id, Assert.Single(await PointsAsync(other.Id)).RepositoryId);

        // vm1's chain moves to E2 with its ids; the source copies go, and the next run continues incrementally on E2.
        var before = points.Select(p => p.Id).ToList();
        var move = await MoveAsync(scale.Id, new StartMovesDto([new ChainMoveDto(jobId, vm1, local.Id, e2.Id)]));
        Assert.True(move.Session.State == "Succeeded", move.Session.Summary + "\n" + string.Join("\n", await LogAsync(move.Session.Id)));
        Assert.Contains("1 chain(s) moved", move.Session.Summary, StringComparison.Ordinal);
        points = await PointsAsync(jobId);
        Assert.Equal(before, points.Select(p => p.Id).ToList());
        Assert.All(points, p => Assert.Equal(e2.Id, p.RepositoryId));
        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "repo", "restore-points"), "manifest.json", SearchOption.AllDirectories).Any());
        var chains = (await client.GetFromJsonAsync<ChainPageDto>($"/api/repositories/{scale.Id}/chains", Ct))!;
        var moved = chains.Items.Single(c => c.ObjectNativeId == vm1);
        Assert.Equal((e2.Id, "moved"), (moved.PlacedExtentId!.Value, moved.Reason!));
        foreach (var point in points)
        {
            Assert.True((await Read<VerifyResultDto>(client.PostAsync($"/api/restore-points/{point.Id}/verify?full=true", null, Ct))).Ok);
        }

        var changed = (byte[])image.Clone();
        RandomNumberGenerator.Fill(changed.AsSpan(1 << 20, 64 << 10));
        _pool.WriteDisk(DiskOf(vm1, changed.LongLength), 0, changed);
        var third = await RunAsync(jobId);
        Assert.Equal("Succeeded", third.Session.State);
        var newest = (await PointsAsync(jobId))[^1];
        Assert.Equal(("Incremental", e2.Id), (newest.Kind, newest.RepositoryId));
        var restore = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync("/api/restores", new RestoreRequestDto
        {
            RestorePointId = newest.Id, Mode = RestoreModeDto.AlternateLocation, VmName = "vm1-copy",
        }, Ct))).SessionId);
        Assert.Equal("Succeeded", restore.Session.State);
        var copy = _pool.Records("VM").Single(v => XapiRecord.Str(v, "name_label") == "vm1-copy");
        Assert.Equal(changed, _pool.DiskContent(DiskOf(XapiRecord.Str(copy, "uuid"), changed.LongLength)));

        // Sealed: no new chains there, and vm1 starts anew on Local with a full backup (a warning says why).
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/repositories/{e2.Id}/extent", new ExtentStateDto(Sealed: true), Ct)).StatusCode);
        var fourth = await RunAsync(jobId);
        Assert.True(fourth.Session.State == "Warning", fourth.Session.Summary + "\n" + string.Join("\n", await LogAsync(fourth.Session.Id)));
        Assert.Contains(await LogAsync(fourth.Session.Id), l => l.Contains("starts a new chain on extent Local of Scale with a full backup: its extent E2 is sealed", StringComparison.Ordinal));
        newest = (await PointsAsync(jobId))[^1];
        Assert.Equal(("Full", local.Id), (newest.Kind, newest.RepositoryId));

        // Evacuating E2 moves what is left there (vm1's older points, vm2's chain) to Local; then E2 can go, Local cannot.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/repositories/{e2.Id}", Ct)).StatusCode);
        var evacuation = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsync($"/api/repositories/{e2.Id}/evacuate", null, Ct))).SessionId);
        Assert.True(evacuation.Session.State == "Succeeded", evacuation.Session.Summary + "\n" + string.Join("\n", await LogAsync(evacuation.Session.Id)));
        Assert.Contains("E2 is evacuated", evacuation.Session.Summary, StringComparison.Ordinal);
        Assert.All(await PointsAsync(jobId), p => Assert.Equal(local.Id, p.RepositoryId));
        Assert.Equal(local.Id, Assert.Single(await PointsAsync(other.Id)).RepositoryId);
        Assert.Equal("Sealed", (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Id == e2.Id).ExtentState);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/repositories/{e2.Id}", Ct)).StatusCode);
        var last = await client.DeleteAsync($"/api/repositories/{local.Id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Equal("Succeeded", (await RunAsync(other.Id)).Session.State);

        // Rescanning finds nothing new, though ids moved between directories.
        var rescan = await client.PostAsync($"/api/repositories/{scale.Id}/rescan", null, Ct);
        Assert.Contains("\"imported\":0", await rescan.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    /// <summary>The disk of a VM of the fake pool with the given size.</summary>
    private string DiskOf(string vmUuid, long size) =>
        _pool.Records("VBD").Where(b => _pool.Uuid("VM", XapiRecord.Str(b, "VM")) == vmUuid)
            .Select(b => _pool.Records("VDI").Single(v => XapiRecord.Str(v, "uuid") == _pool.Uuid("VDI", XapiRecord.Str(b, "VDI"))))
            .Where(v => XapiRecord.Long(v, "virtual_size") == size)
            .Select(v => XapiRecord.Str(v, "uuid")).First();

    [Fact]
    public async Task An_extent_in_maintenance_is_closed_and_its_chains_start_anew_elsewhere_or_wait_as_set()
    {
        var client = await SignedInClientAsync();
        var image = RandomNumberGenerator.GetBytes(2 << 20);
        var point = await BackUpImageAsync(client, "vm1", image);
        var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
        var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = Path.Combine(_root, "extent2"), ScaleOutName = "Scale" }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        var scale = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Scale");
        async Task<SessionDetailDto> RunAsync() => await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/jobs/{point.JobId}/run", new RunJobDto(), Ct))).SessionId);

        // Maintenance: reads refused with the reason; the chain waits when the scale-out keeps chains in place.
        var settings = scale.Settings! with { FullWhenExtentUnavailable = false };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/repositories/{scale.Id}/settings", settings, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/repositories/{local.Id}/extent", new ExtentStateDto(Maintenance: true), Ct)).StatusCode);
        var verify = await client.PostAsync($"/api/restore-points/{point.Id}/verify", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, verify.StatusCode);
        Assert.Contains("The extent Local", await verify.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var skipped = await RunAsync();
        Assert.True(skipped.Session.State == "Warning", skipped.Session.Summary + "\n" + string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{skipped.Session.Id}/log", Ct))!.Select(l => l.Message)));
        Assert.Contains("which is in maintenance", Assert.Single(skipped.Tasks).Error, StringComparison.Ordinal);

        // With the default (a new full elsewhere) the chain starts on E2.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/repositories/{scale.Id}/settings", settings with { FullWhenExtentUnavailable = true }, Ct)).StatusCode);
        var anew = await RunAsync();
        Assert.Equal("Warning", anew.Session.State);
        var newest = (await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={point.JobId}", Ct))!.OrderBy(p => p.CreatedUtc).Last();
        Assert.Equal("Full", newest.Kind);
        Assert.NotEqual(local.Id, newest.RepositoryId);

        // Maintenance off: the old point reads again.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/repositories/{local.Id}/extent", new ExtentStateDto(Maintenance: false), Ct)).StatusCode);
        Assert.True((await Read<VerifyResultDto>(client.PostAsync($"/api/restore-points/{point.Id}/verify?full=true", null, Ct))).Ok);
    }

    [Fact]
    public async Task A_restore_point_deleted_while_its_chain_moves_is_not_switched_and_its_copy_is_removed()
    {
        var client = await SignedInClientAsync();
        var point = await BackUpImageAsync(client, "vm1", RandomNumberGenerator.GetBytes(2 << 20));
        var local = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!.Single(r => r.Name == "Local");
        var added = await client.PostAsJsonAsync($"/api/repositories/{local.Id}/extents", new AddExtentDto { Name = "E2", Path = Path.Combine(_root, "extent2"), ScaleOutName = "Scale" }, Ct);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync(Ct));
        var repositories = (await client.GetFromJsonAsync<List<RepositoryDto>>("/api/repositories", Ct))!;
        var (scale, e2) = (repositories.Single(r => r.Name == "Scale"), repositories.Single(r => r.Name == "E2"));

        // Right before the switch the point is deleted (as retention would): compare-and-set keeps it from switching.
        var mover = _factory!.Services.GetRequiredService<Backdafuckup.Server.ScaleOut.ChainMover>();
        mover.BeforeSwitch = async _ =>
        {
            var deleted = await client.DeleteAsync($"/api/restore-points/{point.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        };
        try
        {
            var move = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/repositories/{scale.Id}/moves",
                new StartMovesDto([new ChainMoveDto(point.JobId, point.VmNativeId, local.Id, e2.Id)]), Ct))).SessionId);
            Assert.True(move.Session.State == "Succeeded", move.Session.Summary);
            Assert.Contains("0 restore point(s)", string.Join("\n", (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{move.Session.Id}/log", Ct))!.Select(l => l.Message)), StringComparison.Ordinal);
        }
        finally
        {
            mover.BeforeSwitch = null;
        }

        Assert.Empty((await client.GetFromJsonAsync<List<RestorePointDto>>($"/api/restore-points?jobId={point.JobId}", Ct))!);
        await _factory.Services.GetRequiredService<Backdafuckup.Server.ScaleOut.ScaleOutBalancer>().TickAsync(Ct);
        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "extent2"), "manifest.json", SearchOption.AllDirectories).Any());
        Assert.False(Directory.EnumerateFiles(Path.Combine(_root, "repo", "restore-points"), "manifest.json", SearchOption.AllDirectories).Any());
    }
}
