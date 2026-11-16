using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Agents;
using Backdafuckup.Tape.Agent;
using Backdafuckup.Testing.Tape;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Tape agents (spec 2026-11-16-1804-tape-device-layer): a library on another host, reached through its agent.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task A_tape_agent_forwards_its_devices_and_its_library_works_like_a_local_one()
    {
        var client = await SignedInClientAsync();

        // An agent needs the address under which agents reach this server.
        var early = await client.PostAsJsonAsync("/api/tape/agents", new CreateTapeAgentDto("lab-01"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("server address", await early.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);

        // Created: its settings file is shown once; the list never has the token.
        var created = await Read<TapeAgentSecretDto>(client.PostAsJsonAsync("/api/tape/agents", new CreateTapeAgentDto("lab-01"), Ct));
        var env = Settings(created.EnvironmentFile);
        var agentId = created.Agent.Id;
        Assert.Equal(("https://localhost/", agentId.ToString("D")), (env["BDFU_SERVER_URL"], env["BDFU_TAPE_AGENT_ID"]));
        Assert.StartsWith("bdfuta_", env["BDFU_TAPE_AGENT_TOKEN"], StringComparison.Ordinal);
        Assert.True(env.ContainsKey("BDFU_SERVER_PINS")); // Empty here: the test host has no certificate.
        Assert.False(created.Agent.Online);
        Assert.DoesNotContain(env["BDFU_TAPE_AGENT_TOKEN"], await client.GetStringAsync("/api/tape/agents", Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/tape/agents", new CreateTapeAgentDto("lab-01"), Ct)).StatusCode);

        // A wrong token is refused.
        var control = new Uri(new Uri("http://localhost/"), TapeAgentProtocol.ControlPath(agentId));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAgentSocketAsync(control, "bdfuta_wrong", Ct));

        // The agent's host has its own library: one drive, four slots, a mail slot.
        var remote = new FakeTapeLibrary(drives: 1, slots: 4, mailSlots: 1) { SerialPrefix = "AG", BecomingReadyPolls = 1 };
        remote.AddCartridge("AGT000L9", 2 * TapeGiB);
        var agentHost = new FakeTapeHost(remote);
        var log = new ConcurrentQueue<string>();
        TapeAgentClient Agent(string token) => new(agentHost, ct => ConnectAgentSocketAsync(control, token, ct), "the test server", "test", log.Enqueue) { MaxPause = TimeSpan.FromMilliseconds(300) };
        async Task<TapeHostDto> Host() => (await client.GetFromJsonAsync<List<TapeHostDto>>("/api/tape/hosts", Ct))!.Single(h => h.Id == agentId.ToString("D"));

        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var running = Agent(env["BDFU_TAPE_AGENT_TOKEN"]).RunAsync(stop.Token);
            await Until(async () => (await Host()).Online);
            Assert.Equal("tape agent lab-01", (await Host()).Name);
            Assert.Contains(log, l => l.StartsWith("Connected to the backup server the test server; reported 2 device(s)", StringComparison.Ordinal));

            // Scanned through the agent: only its devices, mapped by the changer.
            var scan = await Read<TapeScanDto>(client.PostAsync($"/api/tape/hosts/{agentId:D}/scan", null, Ct));
            var changer = Assert.Single(scan.Changers);
            Assert.Equal(("AGLIB0001", "fake:changerAG"), (changer.Changer.Serial, changer.Changer.Path)); // As the agent's host names it.
            Assert.Equal([(256, "AGDRV0001", "dvcid")], changer.Drives.Select(d => (d.Element, d.Serial, d.MappedBy)).ToArray());

            var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync(
                "/api/tape/libraries", new CreateTapeLibraryDto("Remote", agentId.ToString("D"), "AGLIB0001", [new("AGDRV0001", 256, null)]), Ct));
            Assert.Equal(("tape agent lab-01", 4, 1, 1, (string?)null), (library.HostName, library.Slots, library.MailSlots, library.Media, library.LastError));

            // Load and unload run through the agent like on a local library.
            var load = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("load", "AGT000L9"), Ct))).SessionId);
            Assert.True(load.Session.State == "Succeeded", load.Session.Summary);
            Assert.Contains("AGT000L9 is ready in Drive 1", load.Session.Summary, StringComparison.Ordinal);
            Assert.Equal((ushort?)256, remote.LocationOf("AGT000L9"));
            var unload = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("unload"), Ct))).SessionId);
            Assert.True(unload.Session.State == "Succeeded", unload.Session.Summary);
            Assert.Equal((ushort?)1024, remote.LocationOf("AGT000L9"));
            Assert.False(agentHost.IsOpen("AGLIB0001") || agentHost.IsOpen("AGDRV0001"));

            var listed = Assert.Single((await client.GetFromJsonAsync<List<TapeAgentDto>>("/api/tape/agents", Ct))!);
            Assert.Equal((true, Environment.MachineName, "test", 2, "Remote"), (listed.Online, listed.HostName!, listed.Version!, listed.Devices, listed.Libraries.Single()));

            // The agent stops: the library says which agent is missing and since when.
            await stop.CancelAsync();
            await running;
        }

        await Until(async () => !(await Host()).Online);
        var offline = await client.PostAsync($"/api/tape/libraries/{(await client.GetFromJsonAsync<List<TapeLibraryDto>>("/api/tape/libraries", Ct))!.Single().Id}/inventory", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, offline.StatusCode);
        Assert.Contains("The tape agent lab-01 is not connected (last seen ", await offline.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Back again; then a new token disconnects it, and the old one is refused.
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var running = Agent(env["BDFU_TAPE_AGENT_TOKEN"]).RunAsync(stop.Token);
            await Until(async () => (await Host()).Online);
            var libraryId = (await client.GetFromJsonAsync<List<TapeLibraryDto>>("/api/tape/libraries", Ct))!.Single().Id;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/tape/libraries/{libraryId}/inventory", null, Ct)).StatusCode);

            var renewed = await Read<TapeAgentSecretDto>(client.PostAsync($"/api/tape/agents/{agentId}/token", null, Ct));
            Assert.NotEqual(env["BDFU_TAPE_AGENT_TOKEN"], Settings(renewed.EnvironmentFile)["BDFU_TAPE_AGENT_TOKEN"]);
            await Until(async () => !(await Host()).Online);
            await Until(() => Task.FromResult(log.Any(l => l.StartsWith("Cannot connect to the backup server", StringComparison.Ordinal))));
            Assert.False((await Host()).Online);
            await stop.CancelAsync();
            await running;
        }

        // Removed only without libraries.
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/tape/agents/{agentId}", Ct)).StatusCode);
        var only = (await client.GetFromJsonAsync<List<TapeLibraryDto>>("/api/tape/libraries", Ct))!.Single();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tape/libraries/{only.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tape/agents/{agentId}", Ct)).StatusCode);
        Assert.Equal(["local"], (await client.GetFromJsonAsync<List<TapeHostDto>>("/api/tape/hosts", Ct))!.Select(h => h.Id).ToArray());
    }

    [Fact]
    public async Task A_tape_job_and_bringing_back_run_through_an_agent_with_posted_writes_and_reads_ahead()
    {
        var client = await SignedInClientAsync();
        await _factory!.Services.GetRequiredService<AgentSettingsStore>().SetServerUrlAsync("https://localhost/", Ct);
        var created = await Read<TapeAgentSecretDto>(client.PostAsJsonAsync("/api/tape/agents", new CreateTapeAgentDto("lab-02"), Ct));
        var agentId = created.Agent.Id;
        var control = new Uri(new Uri("http://localhost/"), TapeAgentProtocol.ControlPath(agentId));
        var remote = new FakeTapeLibrary(drives: 1, slots: 4, mailSlots: 1) { SerialPrefix = "PU", BecomingReadyPolls = 1, HoloQuirks = true };
        remote.AddCartridge("PUT000L9", 2 * TapeGiB);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = new TapeAgentClient(new FakeTapeHost(remote), ct => ConnectAgentSocketAsync(control, Settings(created.EnvironmentFile)["BDFU_TAPE_AGENT_TOKEN"], ct), "the test server", "test", _ => { })
            .RunAsync(stop.Token);
        await Until(async () => (await client.GetFromJsonAsync<List<TapeHostDto>>("/api/tape/hosts", Ct))!.Any(h => h.Id == agentId.ToString("D") && h.Online));
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync(
            "/api/tape/libraries", new CreateTapeLibraryDto("Remote", agentId.ToString("D"), "PULIB0001", [new("PUDRV0001", 256, null)]), Ct));

        // Written and read back through the agent (as Holo: no residual, no early warning), byte for byte.
        var image = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6 << 20);
        var point = await BackUpImageAsync(client, "remote01", image);
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Remote pool", library.Id), Ct));
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("To the remote tape", pool.Id, [point.JobId!.Value]), Ct));
        var written = await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct))).SessionId);
        Assert.True(written.Session.State == "Succeeded", written.Session.Summary);
        var copy = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);
        var started = await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/copies/{copy.Id}/restore-vm", new RestoreRequestDto
        {
            RestorePointId = copy.Id,
            Mode = RestoreModeDto.AlternateLocation,
            VmName = "remote01-from-tape",
        }, Ct));
        var back = await WaitForSessionAsync(client, started.SessionId);
        Assert.True(back.Session.State == "Succeeded", back.Session.Summary);
        var vm = _pool.Records("VM").Single(v => v.Str("name_label") == "remote01-from-tape");
        var disk = _pool.Records("VBD").Where(b => _pool.Uuid("VM", b.Str("VM")) == vm.Str("uuid")).Select(b => b.Str("VDI"))
            .Select(r => _pool.Records("VDI").Single(v => v.Str("uuid") == _pool.Uuid("VDI", r)))
            .Single(v => v.Long("virtual_size") == image.LongLength);
        Assert.Equal(image, _pool.DiskContent(disk.Str("uuid")));

        // A library its pools still use is not removed.
        var refused = await client.DeleteAsync($"/api/tape/libraries/{library.Id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Media pool(s) Remote pool use Remote", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        await stop.CancelAsync();
        await running;
    }

    private static Dictionary<string, string> Settings(string environmentFile) =>
        environmentFile.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#') && l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
}
