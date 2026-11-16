using System.Net.Http.Json;
using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Testing.Tape;

namespace Backdafuckup.Server.Tests;

/// <summary>A standalone drive: the operator inserts cartridges when a session asks for them.</summary>
public sealed partial class ApiIntegrationTests
{
    [Fact]
    public async Task On_a_standalone_drive_sessions_ask_for_cartridges_and_refuse_wrong_ones()
    {
        var client = await SignedInClientAsync();
        var desk = new FakeTapeLibrary(drives: 1, slots: 1, mailSlots: 0) { SerialPrefix = "SA", BecomingReadyPolls = 0 };
        _tapeHost.Libraries.Add(desk);
        _tapeHost.WithoutChanger.Add(desk);
        var drive = desk.Drives[0];

        var image = RandomNumberGenerator.GetBytes(4 << 20);
        var point = await BackUpImageAsync(client, "app01", image);
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Desk", "local", null, [new("SADRV0001", null, null)]), Ct));
        var pool = await Read<MediaPoolDto>(client.PostAsJsonAsync("/api/tape/pools", new MediaPoolRequestDto("Desk pool", library.Id, NewSet: "Never"), Ct));
        var job = await Read<TapeJobDto>(client.PostAsJsonAsync("/api/tape/jobs", new TapeJobRequestDto("Desk job", pool.Id, [point.JobId!.Value]), Ct));
        async Task<TapeOperatorRequestDto> RequestAsync(string containing)
        {
            TapeOperatorRequestDto? found = null;
            await Until(async () => (found = (await client.GetFromJsonAsync<List<TapeOperatorRequestDto>>("/api/tape/requests", Ct))!.SingleOrDefault(r => r.Message.Contains(containing, StringComparison.Ordinal))) is not null);
            return found!;
        }

        // The run asks for a cartridge.
        var run = (await Read<StartedDto>(client.PostAsync($"/api/tape/jobs/{job.Id}/run", null, Ct))).SessionId;
        var asked = await RequestAsync("Insert a blank, free or expired cartridge for pool Desk pool into Drive 1 of Desk");
        Assert.Equal((run, "Desk", "Drive 1"), (asked.SessionId, asked.Library, asked.Drive));

        // Other data on it: ejected, and the request says so; then a blank one is taken.
        var foreign = new FakeCartridge("X1", 64L << 20);
        foreign.Records.Add(new FakeRecord(RandomNumberGenerator.GetBytes(4096), null));
        drive.OperatorInsert(foreign);
        await RequestAsync("holds data that is not backdafuckup's");
        drive.OperatorRemove();
        var blank = new FakeCartridge("B1", 64L << 20);
        drive.OperatorInsert(blank);
        var written = await WaitForSessionAsync(client, run);
        Assert.True(written.Session.State == "Succeeded", written.Session.Summary);
        Assert.Empty((await client.GetFromJsonAsync<List<TapeOperatorRequestDto>>("/api/tape/requests", Ct))!);
        var tape = (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!.Single(m => m.SetId is not null);
        Assert.StartsWith("Deskpool-", tape.Barcode, StringComparison.Ordinal);
        Assert.Contains(tape.Barcode, written.Session.Summary, StringComparison.Ordinal);
        Assert.Equal(("Offline", "Appendable"), (tape.Location, tape.State));
        Assert.StartsWith(tape.Barcode, System.Text.Encoding.ASCII.GetString(blank.HostAttributes[Backdafuckup.Tape.Scsi.MamAttributes.Barcode]), StringComparison.Ordinal);
        Assert.False(drive.Loaded);
        drive.OperatorRemove();

        // Bringing it back asks for that tape by name; another one is ejected and named.
        var target = await Read<RepositoryDto>(client.PostAsJsonAsync("/api/repositories", new CreateRepositoryDto { Name = "Desk restore", Path = Path.Combine(_root, "desk-restore") }, Ct));
        var copy = Assert.Single((await client.GetFromJsonAsync<List<TapeCopyDto>>("/api/tape/copies", Ct))!);
        var restore = (await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/copies/{copy.Id}/restore", new TapeRestoreRequestDto(target.Id), Ct))).SessionId;
        await RequestAsync($"Insert tape {tape.Barcode} (#1 of the set) into Drive 1 of Desk");
        drive.OperatorInsert(new FakeCartridge("B2", 64L << 20));
        await RequestAsync("The cartridge inserted is blank, not tape");
        drive.OperatorRemove();
        drive.OperatorInsert(blank);
        var back = await WaitForSessionAsync(client, restore);
        Assert.True(back.Session.State == "Succeeded", back.Session.Summary);
        Assert.Single((await client.GetFromJsonAsync<List<RestorePointDto>>("/api/restore-points", Ct))!, r => r.RepositoryId == target.Id);
    }
}
