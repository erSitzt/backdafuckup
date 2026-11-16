using System.Net;
using System.Net.Http.Json;
using System.Text;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Tape;
using Backdafuckup.Tape.Scsi;
using Backdafuckup.Testing.Tape;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>Tape libraries on the server (spec 2026-11-16-1804-tape-device-layer): find, add, inventory, and library actions.</summary>
public sealed partial class ApiIntegrationTests
{
    private const long TapeGiB = 1L << 30;

    [Fact]
    public async Task Tape_libraries_are_found_by_serial_added_with_their_drives_and_inventoried()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.AddCartridge("BDF000L9", 2 * TapeGiB);
        fake.AddCartridge("BDF001L9", 2 * TapeGiB);
        fake.AddCartridge("WRM000LZ", 2 * TapeGiB, worm: true);
        fake.AddCartridge("LONGBARCODE01", 2 * TapeGiB, slot: 1030);
        fake.AddCartridge("CLN001CU", 2 * TapeGiB, cleaning: true, slot: 1031);

        var host = Assert.Single((await client.GetFromJsonAsync<List<TapeHostDto>>("/api/tape/hosts", Ct))!);
        Assert.Equal(("local", "fake host", true), (host.Id, host.Name, host.Online));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/tape/hosts/nowhere/scan", null, Ct)).StatusCode);

        // The changer names its drives (DVCID): both are mapped without guessing.
        var scan = await Read<TapeScanDto>(client.PostAsync("/api/tape/hosts/local/scan", null, Ct));
        var changer = Assert.Single(scan.Changers);
        Assert.Equal(("changer", "LIB0001", 8, 2, (string?)null), (changer.Changer.Kind, changer.Changer.Serial, changer.Slots, changer.MailSlots, changer.Changer.Library));
        Assert.Equal([(256, "DRV0001", "dvcid"), (257, "DRV0002", "dvcid")], changer.Drives.Select(d => (d.Element, d.Serial, d.MappedBy)).ToArray());
        Assert.Empty(scan.StandaloneDrives);
        Assert.Empty(scan.Problems);

        // Requests that cannot be right are refused with the reason.
        async Task Refused(CreateTapeLibraryDto request, HttpStatusCode status, string reason)
        {
            var response = await client.PostAsJsonAsync("/api/tape/libraries", request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
            Assert.Contains(reason, body, StringComparison.Ordinal);
        }

        await Refused(new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null), new("DRV0002", 300, null)]), HttpStatusCode.BadRequest, "has no drive element 300; its drive elements are 256, 257");
        await Refused(new CreateTapeLibraryDto("Lab", "local", "NOPE", [new("DRV0001", 256, null)]), HttpStatusCode.BadRequest, "No changer with serial NOPE is attached to fake host");
        await Refused(new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null), new("DRV0002", 256, null)]), HttpStatusCode.BadRequest, "its own drive element");
        await Refused(new CreateTapeLibraryDto("Lab", "local", null, [new("DRV0001", null, null), new("DRV0002", null, null)]), HttpStatusCode.BadRequest, "choose one drive");
        await Refused(new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("NODRIVE", 256, null)]), HttpStatusCode.BadRequest, "No tape drive with serial NODRIVE is attached to fake host");

        var created = await Read<TapeLibraryDto>(client.PostAsJsonAsync(
            "/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0002", 257, null), new("DRV0001", 256, "Upper")]), Ct));
        Assert.Equal(("Lab", "fake host", "HPE", "MSL6480", 8, 2, 5, 0, (string?)null), (created.Name, created.HostName, created.Vendor, created.Product, created.Slots, created.MailSlots, created.Media, created.MailSlotsFull, created.LastError));
        Assert.Equal([("Upper", 256, "DRV0001"), ("Drive 2", 257, "DRV0002")], created.Drives.Select(d => (d.Name, d.Element ?? 0, d.Serial)).ToArray());
        Assert.All(created.Drives, d => Assert.Null(d.Holder));
        await Refused(new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null)]), HttpStatusCode.Conflict, "named 'Lab' exists already");
        await Refused(new CreateTapeLibraryDto("Lab 2", "local", "LIB0001", [new("DRV0001", 256, null)]), HttpStatusCode.Conflict, "belongs to the library 'Lab' already");

        // The cartridges with their kind (from the barcode until they were in a drive) and their place.
        var media = (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!;
        Assert.Equal(
            [("BDF000L9", "Data", "Slot", 1024), ("BDF001L9", "Data", "Slot", 1025), ("CLN001CU", "Cleaning", "Slot", 1031), ("LONGBARCODE01", "Data", "Slot", 1030), ("WRM000LZ", "Worm", "Slot", 1026)],
            media.Select(m => (m.Barcode, m.Kind, m.Location, m.Address ?? 0)).ToArray());
        Assert.All(media, m => Assert.Equal(("Lab", m.Address), (m.Library, m.HomeSlot)));

        // The slot map: drives, mail slots, slots, numbered as people count them.
        var elements = (await client.GetFromJsonAsync<List<TapeElementDto>>($"/api/tape/libraries/{created.Id}/elements", Ct))!;
        Assert.Equal(2 + 2 + 8, elements.Count);
        Assert.Equal(("Slot", 7, "LONGBARCODE01", "Data"), elements.Where(e => e.Address == 1030).Select(e => (e.Type, e.Number, e.Barcode, e.MediumKind)).Single());
        Assert.Equal(("Drive", 1, "Upper", (string?)null), elements.Where(e => e.Address == 256).Select(e => (e.Type, e.Number, e.DriveName!, e.Barcode)).Single());
        Assert.Equal(["MailSlot", "MailSlot"], elements.Where(e => e.Address is 768 or 769).Select(e => e.Type).ToArray());

        // A cartridge put in through the library's own interface is found by the next inventory.
        fake.AddCartridge("BDF002L9", 2 * TapeGiB, slot: 1029);
        var inventoried = await Read<TapeLibraryDto>(client.PostAsync($"/api/tape/libraries/{created.Id}/inventory", null, Ct));
        Assert.Equal(6, inventoried.Media);

        // A scan names the library a device belongs to, and devices it could not ask.
        _tapeHost.Busy.Add("DRV0002");
        var again = await Read<TapeScanDto>(client.PostAsync("/api/tape/hosts/local/scan", null, Ct));
        Assert.Equal("Lab", again.Changers.Single().Changer.Library);
        Assert.Contains(again.Problems, p => p.Contains("fake:drive2 (HPE Ultrium 9-SCSI, fake connection) on fake host could not be asked for its serial", StringComparison.Ordinal));
        _tapeHost.Busy.Clear();

        var renamed = await Read<TapeLibraryDto>(client.PutAsJsonAsync($"/api/tape/libraries/{created.Id}", new RenameTapeLibraryDto("Lab A"), Ct));
        Assert.Equal("Lab A", renamed.Name);

        // Removed: the cartridges stay known, without a library.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tape/libraries/{created.Id}", Ct)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<TapeLibraryDto>>("/api/tape/libraries", Ct))!);
        Assert.All((await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!, m => Assert.Equal(((Guid?)null, "Offline"), (m.LibraryId, m.Location)));
        Assert.False(_tapeHost.IsOpen("LIB0001"));
    }

    [Fact]
    public async Task Library_actions_run_as_tape_sessions_that_wait_for_their_drive()
    {
        var client = await SignedInClientAsync();
        var fake = _tapeHost.Libraries[0];
        fake.BecomingReadyPolls = 1;
        fake.AddCartridge("BDF000L9", 2 * TapeGiB);
        fake.AddCartridge("BDF001L9", 2 * TapeGiB);
        fake.AddCartridge("CLN001CU", 2 * TapeGiB, cleaning: true);
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync(
            "/api/tape/libraries", new CreateTapeLibraryDto("Lab", "local", "LIB0001", [new("DRV0001", 256, null), new("DRV0002", 257, null)]), Ct));
        var drive1 = library.Drives[0];

        async Task<Guid> Start(TapeActionDto action)
        {
            var response = await client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", action, Ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<StartedDto>(Ct))!.SessionId;
        }

        async Task<List<string>> Log(Guid session) => (await client.GetFromJsonAsync<List<LogEntryDto>>($"/api/sessions/{session}/log", Ct))!.Select(l => l.Message).ToList();
        async Task<(SessionSummaryDto Session, List<string> Log)> Run(TapeActionDto action)
        {
            var id = await Start(action);
            return ((await WaitForSessionAsync(client, id)).Session, await Log(id));
        }

        TapeMediumDto Medium(List<TapeMediumDto> all, string barcode) => all.Single(m => m.Barcode == barcode);
        async Task<List<TapeMediumDto>> Media() => (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!;

        // Requests that cannot be right do not become sessions.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("fly"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("load"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("unload"), Ct)).StatusCode);

        // Load into a chosen drive: moved, ready, the cartridge memory read.
        var (load, _) = await Run(new TapeActionDto("load", "BDF000L9", drive1.Id));
        Assert.True(load.State == "Succeeded", load.Summary);
        Assert.Contains("BDF000L9 is ready in Drive 1", load.Summary, StringComparison.Ordinal);
        Assert.Contains("data cartridge", load.Summary, StringComparison.Ordinal);
        Assert.Contains("cartridge serial MSBDF000L9", load.Summary, StringComparison.Ordinal);
        Assert.Equal((ushort?)256, fake.LocationOf("BDF000L9"));
        var loaded = Medium(await Media(), "BDF000L9");
        Assert.Equal(("Drive", "Drive 1", 1024, "MSBDF000L9", 2 * TapeGiB), (loaded.Location, loaded.Drive!, loaded.HomeSlot ?? 0, loaded.MediumSerial!, loaded.CapacityBytes ?? 0));

        // Two loads for one drive: the second waits (the console names the holder), then swaps the cartridges.
        fake.BecomingReadyPolls = 100;
        var first = await Start(new TapeActionDto("load", "BDF001L9", drive1.Id));
        await Until(async () => (await Log(first)).Any(l => l.StartsWith("Waiting for Drive 1 to be ready", StringComparison.Ordinal)));
        var second = await Start(new TapeActionDto("load", "BDF000L9", drive1.Id));
        await Until(async () => (await client.GetFromJsonAsync<TapeLibraryDto>($"/api/tape/libraries/{library.Id}", Ct))!.Drives[0].Waiting.Count == 1);
        var busy = (await client.GetFromJsonAsync<TapeLibraryDto>($"/api/tape/libraries/{library.Id}", Ct))!.Drives[0];
        Assert.Equal(("Load BDF001L9", "Load BDF000L9"), (busy.Holder, busy.Waiting.Single()));
        Assert.NotNull(busy.HolderSinceUtc);
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, first)).Session.State);
        Assert.Equal("Succeeded", (await WaitForSessionAsync(client, second)).Session.State);
        Assert.Contains("Waiting for Drive 1: 'Load BDF001L9' uses it.", await Log(second));
        Assert.Contains("Drive 1 holds BDF001L9; it goes back to a slot first.", await Log(second));
        Assert.Equal(((ushort?)256, (ushort?)1025), (fake.LocationOf("BDF000L9"), fake.LocationOf("BDF001L9")));
        fake.BecomingReadyPolls = 1;
        var free = (await client.GetFromJsonAsync<TapeLibraryDto>($"/api/tape/libraries/{library.Id}", Ct))!.Drives[0];
        Assert.Equal(((string?)null, 0, "BDF000L9"), (free.Holder, free.Waiting.Count, free.Barcode!));

        // A cleaning cartridge is not loaded for data.
        var (cleaning, _) = await Run(new TapeActionDto("load", "CLN001CU"));
        Assert.Equal("Failed", cleaning.State);
        Assert.Contains("CLN001CU is a cleaning cartridge", cleaning.Summary, StringComparison.Ordinal);

        // Unload: back into the slot it came from.
        var (unload, _) = await Run(new TapeActionDto("unload", null, drive1.Id));
        Assert.True(unload.State == "Succeeded", unload.Summary);
        Assert.Equal("BDF000L9 unloaded from Drive 1 into slot 1 (element 1024).", unload.Summary);
        Assert.Equal((ushort?)1024, fake.LocationOf("BDF000L9"));
        Assert.Equal("Drive 1 is empty.", (await Run(new TapeActionDto("unload", null, drive1.Id))).Session.Summary);

        // Export into a mail slot; the operator takes it out, and the mail slot watcher notices.
        var (export, _) = await Run(new TapeActionDto("export", "BDF001L9"));
        Assert.True(export.State == "Succeeded", export.Summary);
        Assert.Equal("BDF001L9 is in mail slot 1 (element 768) of Lab; take it out there.", export.Summary);
        Assert.Equal(1, (await client.GetFromJsonAsync<TapeLibraryDto>($"/api/tape/libraries/{library.Id}", Ct))!.MailSlotsFull);
        fake.OperatorRemove(768);
        var watcher = _factory!.Services.GetRequiredService<TapeMailSlotWatcher>();
        await watcher.CheckAsync(Ct);
        Assert.Equal("Offline", Medium(await Media(), "BDF001L9").Location);

        // It comes back with a new cartridge: the console shows both, the import puts the known one into its old slot.
        fake.OperatorInsert("BDF001L9", 2 * TapeGiB);
        fake.OperatorInsert("NEW000L9", 2 * TapeGiB);
        await watcher.CheckAsync(Ct);
        Assert.Equal(2, (await client.GetFromJsonAsync<TapeLibraryDto>($"/api/tape/libraries/{library.Id}", Ct))!.MailSlotsFull);
        var (import, importLog) = await Run(new TapeActionDto("import"));
        Assert.True(import.State == "Succeeded", import.Summary);
        Assert.Equal("2 of 2 cartridge(s) imported into Lab.", import.Summary);
        Assert.Contains("BDF001L9 from mail slot 1 (element 768) into slot 2 (element 1025).", importLog);
        Assert.Equal(((ushort?)1025, (ushort?)1027), (fake.LocationOf("BDF001L9"), fake.LocationOf("NEW000L9")));
        Assert.Equal("No cartridges in the mail slots of Lab.", (await Run(new TapeActionDto("import"))).Session.Summary);

        // A full scan: the robot checks every element first.
        var (scan, scanLog) = await Run(new TapeActionDto("scan"));
        Assert.True(scan.State == "Succeeded", scan.Summary);
        Assert.Equal("Inventory of Lab: 4 cartridge(s), 0 of them in mail slots.", scan.Summary);
        Assert.Contains(scanLog, l => l.Contains("INITIALIZE ELEMENT STATUS", StringComparison.Ordinal));
        Assert.Contains(fake.CommandLog, c => c.Contains("INITIALIZE ELEMENT STATUS", StringComparison.Ordinal));

        // Nothing is left open; the sessions are listed as tape sessions.
        Assert.False(_tapeHost.IsOpen("LIB0001") || _tapeHost.IsOpen("DRV0001") || _tapeHost.IsOpen("DRV0002"));
        var sessions = (await client.GetFromJsonAsync<List<SessionSummaryDto>>("/api/sessions?kind=TapeMaintenance", Ct))!;
        Assert.Equal(10, sessions.Count);
        Assert.All(sessions, s => Assert.Equal("TapeMaintenance", s.Kind));
    }

    [Fact]
    public async Task A_standalone_drive_is_asked_which_cartridge_it_holds()
    {
        var client = await SignedInClientAsync();
        var desk = new FakeTapeLibrary(drives: 1, slots: 1, mailSlots: 0) { SerialPrefix = "SA", BecomingReadyPolls = 0 };
        _tapeHost.Libraries.Add(desk);
        _tapeHost.WithoutChanger.Add(desk);

        var scan = await Read<TapeScanDto>(client.PostAsync("/api/tape/hosts/local/scan", null, Ct));
        Assert.Equal("SADRV0001", Assert.Single(scan.StandaloneDrives).Serial);
        var library = await Read<TapeLibraryDto>(client.PostAsJsonAsync("/api/tape/libraries", new CreateTapeLibraryDto("Desk", "local", null, [new("SADRV0001", null, null)]), Ct));
        Assert.Equal(((string?)null, 0, 0, "Drive 1", 0), (library.ChangerSerial, library.Slots, library.Media, library.Drives.Single().Name, library.MailSlots));

        var load = await client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("load", "SA0001L9"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, load.StatusCode);
        Assert.Contains("standalone drive without a robot", await load.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Put in by hand; the library wrote its barcode into the cartridge memory.
        var cartridge = new FakeCartridge("SA0001L9", 2 * TapeGiB);
        cartridge.HostAttributes[MamAttributes.Barcode] = Encoding.ASCII.GetBytes("SA0001L9".PadRight(32));
        desk.Drives[0].OperatorInsert(cartridge);
        var inventoried = await Read<TapeLibraryDto>(client.PostAsync($"/api/tape/libraries/{library.Id}/inventory", null, Ct));
        Assert.Equal((1, "SA0001L9", (string?)null), (inventoried.Media, inventoried.Drives.Single().Barcode!, inventoried.LastError));
        var medium = (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!.Single();
        Assert.Equal(("Drive", "Drive 1", "MSSA0001L9"), (medium.Location, medium.Drive!, medium.MediumSerial!));

        var unload = await Read<StartedDto>(client.PostAsJsonAsync($"/api/tape/libraries/{library.Id}/actions", new TapeActionDto("unload"), Ct));
        var session = await WaitForSessionAsync(client, unload.SessionId);
        Assert.True(session.Session.State == "Succeeded", session.Session.Summary);
        Assert.Equal("Drive 1 ejected its cartridge; take it out of the drive.", session.Session.Summary);
        Assert.Equal("Offline", (await client.GetFromJsonAsync<List<TapeMediumDto>>("/api/tape/media", Ct))!.Single().Location);

        // Not attached any more: the error names the host and the serial.
        _tapeHost.Busy.Add("SADRV0001");
        var lost = await client.PostAsync($"/api/tape/libraries/{library.Id}/inventory", null, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, lost.StatusCode);
        Assert.Contains("No tape device with serial SADRV0001 is attached to fake host", await lost.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("The condition did not come true within 10 s.");
    }
}
