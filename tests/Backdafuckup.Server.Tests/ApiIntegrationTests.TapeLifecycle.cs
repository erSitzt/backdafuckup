using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Tests;

/// <summary>Media sets and tapes over time: export, expiry and reuse, free and damaged marks, a new set on demand.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task Sets_close_and_expire_their_tapes_go_out_and_are_reused_and_marks_need_the_barcode()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.BecomingReadyPolls = 1;
        for (var i = 0; i < 4; i++)
        {
            fake.AddCartridge($"BDF00{i}L9", 12L << 20);
        }

        var point = await BackUpImageAsync(client, "db01", RandomNumberGenerator.GetBytes(2 << 20));
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null)]), Ct));
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Daily", library.Id, NewSet: "EveryRun", RetentionDays: 0, Export: "AfterEveryRun"), Ct));
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("To tape", pool.Id, [point.JobId!.Value]), Ct));
        async Task<SessionSummaryDto> RunAsync() =>
            (await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct))).SessionId)).Session;
        async Task<TapeMediumDto> MediumAsync(string barcode) => (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!.Single(m => m.Barcode == barcode);
        async Task ImportAsync() =>
            Assert.Equal("Succeeded", (await WaitForSessionAsync(client, (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("import"), Ct))).SessionId)).Session.State);

        // Run 1: a new set on BDF000L9, which then goes out into a mail slot.
        var first = await RunAsync();
        Assert.True(first.State == "Succeeded", first.Summary);
        Assert.Equal((ushort?)768, fake.LocationOf("BDF000L9"));
        Assert.Equal(("Appendable", "MailSlot"), ((await MediumAsync("BDF000L9")).State, (await MediumAsync("BDF000L9")).Location));

        // Run 2: every run a new set, so the first closes (kept 0 days) and the copy goes on a new tape, also exported.
        var second = await RunAsync();
        Assert.True(second.State == "Succeeded", second.Summary);
        Assert.Contains("BDF001L9", second.Summary, StringComparison.Ordinal);
        Assert.Equal("Full", (await MediumAsync("BDF000L9")).State);

        // Back into the library: run 3 expires the first set and labels its tape again for the third.
        await ImportAsync();
        var third = await RunAsync();
        Assert.True(third.State == "Succeeded", third.Summary);
        Assert.Contains("BDF000L9", third.Summary, StringComparison.Ordinal);
        var sets = (await client.GetFromJsonAsync<List<MediaSetDto>>($"/api/tape/sets?pool={pool.Id}", Ct))!;
        Assert.Equal(["Closed", "Expired", "Open"], sets.Select(s => s.State).Order().ToArray());
        Assert.Equal(("Appendable", 1), ((await MediumAsync("BDF000L9")).State, (await MediumAsync("BDF000L9")).Sequence!.Value));

        // Marks: the barcode typed; a tape of a kept set is not freed; damaged ones are never written to.
        var kept = await MediumAsync("BDF001L9");
        var refused = await client.PostAsJsonAsync($"/api/tape/media/{kept.Id}/state", new TapeMediumStateDto("Free", "BDF001L9"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("set that is kept", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var blank = await MediumAsync("BDF002L9");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tape/media/{blank.Id}/state", new TapeMediumStateDto("Damaged", "BDF003L9"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/tape/media/{blank.Id}/state", new TapeMediumStateDto("Damaged", "BDF002L9"), Ct)).StatusCode);
        Assert.Equal("Damaged", (await MediumAsync("BDF002L9")).State);

        // A new set on demand: the open set closes now.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/tape/pools/{pool.Id}/new-set", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/tape/pools/{pool.Id}/new-set", null, Ct)).StatusCode);
        Assert.Null((await client.GetFromJsonAsync<List<MediaPoolDto>>("/api/tape/pools", Ct))!.Single().OpenSet);

        // The next run needs a tape: both earlier sets expired meanwhile (kept 0 days), so an expired tape comes first,
        // before the blank BDF003L9; the damaged one is never taken.
        await ImportAsync();
        var fourth = await RunAsync();
        Assert.True(fourth.State == "Succeeded", fourth.Summary);
        Assert.Contains("BDF000L9", fourth.Summary, StringComparison.Ordinal);
        Assert.Equal("Expired", (await MediumAsync("BDF001L9")).State);
        Assert.Equal("Unknown", (await MediumAsync("BDF003L9")).State);
        Assert.Equal("Damaged", (await MediumAsync("BDF002L9")).State);
    }
}
