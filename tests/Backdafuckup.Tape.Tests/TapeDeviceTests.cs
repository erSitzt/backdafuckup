using System.Security.Cryptography;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;
using Backdafuckup.Testing.Tape;

namespace Backdafuckup.Tape.Tests;

/// <summary>Drives and changers against <see cref="FakeTapeLibrary"/> (spec 2026-11-16-1804-tape-device-layer, task 3).</summary>
[Trait("Category", "Unit")]
public sealed class TapeDeviceTests
{
    private const int Block = 256 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_changer_lists_its_elements_with_barcodes_of_any_length_and_maps_drives_by_their_identifier()
    {
        var library = new FakeTapeLibrary(drives: 2, slots: 10, mailSlots: 3);
        library.AddCartridge("BDF000L9", 8 << 20);
        library.AddCartridge("ABC123", 8 << 20, slot: 1027);
        library.AddCartridge("BDF001L09", 8 << 20, worm: true);
        library.AddCartridge("CLN001L9", 8 << 20, cleaning: true);
        var changer = new MediumChanger(new ScsiCommandRunner(library.Changer, "changer"));

        var identity = await changer.IdentifyAsync(Ct);
        Assert.Equal(("HPE", "MSL6480", "LIB0001"), (identity.Vendor, identity.Product, identity.Serial));

        var inventory = await changer.ReadInventoryAsync(Ct);
        Assert.Equal((2, 10, 3), (inventory.Drives.Count, inventory.Slots.Count, inventory.MailSlots.Count));
        Assert.Equal(["BDF000L9", "BDF001L09", "CLN001L9", "ABC123"], inventory.Slots.Where(s => s.Full).Select(s => s.VolumeTag));
        Assert.Equal((ushort)1027, inventory.Find("ABC123")!.Address);

        // The drives' identifiers equal the designator in their own VPD page 83h: that maps elements to devices.
        var drive = new TapeDrive(new ScsiCommandRunner(library.Drives[1], "drive 2"));
        var driveIdentity = await drive.IdentifyAsync(Ct);
        Assert.Contains(inventory.Drives[1].DeviceId, driveIdentity.Designators);
        Assert.DoesNotContain(inventory.Drives[0].DeviceId, driveIdentity.Designators);

        // Holo reads only the low byte of INQUIRY's allocation length: identification must still work.
        library.HoloQuirks = true;
        Assert.Contains(inventory.Drives[1].DeviceId, (await drive.IdentifyAsync(Ct)).Designators);
        library.HoloQuirks = false;

        // Without DVCID the changer still answers, the drives just have no identifier.
        library.DeviceIds = false;
        var plain = await new MediumChanger(new ScsiCommandRunner(library.Changer, "changer")).ReadInventoryAsync(Ct);
        Assert.All(plain.Drives, d => Assert.Null(d.DeviceId));
    }

    [Fact]
    public async Task A_cartridge_moves_into_a_drive_loads_and_goes_back_after_unloading()
    {
        var library = new FakeTapeLibrary();
        var cartridge = library.AddCartridge("BDF000L9", 8 << 20);
        var changer = new MediumChanger(new ScsiCommandRunner(library.Changer, "changer"));
        var drive = new TapeDrive(new ScsiCommandRunner(library.Drives[0], "drive 1"), new FakeTimeProviderNoDelay());
        var inventory = await changer.ReadInventoryAsync(Ct);

        var slot = inventory.Find("BDF000L9")!.Address;
        await changer.MoveAsync(slot, inventory.Drives[0].Address, Ct);
        await drive.WaitReadyAsync(TimeSpan.FromMinutes(1), Ct); // becoming ready, then the unit attention
        var medium = await drive.ReadMediumAsync(Ct);
        Assert.Equal(("MSBDF000L9", false, false), (medium.MediumSerial, medium.Worm, medium.WriteProtected));
        Assert.Equal(8L << 20, medium.CapacityBytes);

        // Moving it out while loaded is refused by the library; after the unload it works.
        var refused = await Assert.ThrowsAsync<ScsiCommandException>(() => changer.MoveAsync(inventory.Drives[0].Address, slot, Ct));
        Assert.Contains("53/02", refused.Message, StringComparison.Ordinal);
        await drive.UnloadAsync(Ct);
        await changer.MoveAsync(inventory.Drives[0].Address, slot, Ct);
        var after = await changer.ReadInventoryAsync(Ct);
        Assert.Equal(slot, after.Find("BDF000L9")!.Address);
        Assert.Equal((ushort)library.FirstSlot, after.Slots[0].Address);
        Assert.All(library.Drives, d => Assert.Null(d.Cartridge));
        Assert.Equal(slot, library.LocationOf(cartridge.Barcode));
    }

    [Fact]
    public async Task An_operator_insert_into_a_mail_slot_is_seen_after_the_unit_attention()
    {
        var library = new FakeTapeLibrary();
        var changer = new MediumChanger(new ScsiCommandRunner(library.Changer, "changer"));
        var attentions = new List<SenseData>();
        changer.Runner.UnitAttention += attentions.Add;
        await changer.ReadInventoryAsync(Ct);

        library.OperatorInsert("NEW001L9", 8 << 20);
        var inventory = await changer.ReadInventoryAsync(Ct);
        Assert.Equal("NEW001L9", Assert.Single(inventory.MailSlots, m => m.Full).VolumeTag);
        Assert.Contains(attentions, a => a.Asc == 0x28 && a.Ascq == 0x01);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writing_reports_early_warning_and_then_refuses_with_a_full_tape_and_reads_back_with_filemarks_and_end_of_data(bool holo)
    {
        var (library, drive) = await LoadedDriveAsync(capacity: 4L << 20, holo: holo);
        await drive.PrepareAsync(Ct);
        Assert.Equal((false, true, 0), (library.Drives[0].Compression, library.Drives[0].SynchronizeAtEarlyWarning, library.Drives[0].BlockLength));

        var blocks = new List<byte[]>();
        var warned = false;
        while (true)
        {
            var block = RandomNumberGenerator.GetBytes(Block);
            WriteOutcome outcome;
            try
            {
                outcome = await drive.WriteBlockAsync(block, Ct);
            }
            catch (TapeFullException)
            {
                break;
            }

            blocks.Add(block);
            // Holo reports no early-warning sense: the position's EOP bit tells it.
            warned |= outcome.EarlyWarning || (await drive.ReadPositionAsync(Ct)).EndOfPartition;
            if (warned && blocks.Count % 2 == 0)
            {
                break;
            }
        }

        Assert.True(warned);
        Assert.Equal(!holo, (await drive.WriteFilemarksAsync(1, immediate: false, Ct)).EarlyWarning);
        await drive.RewindAsync(Ct);
        var buffer = new byte[Block];
        foreach (var expected in blocks)
        {
            var read = await drive.ReadBlockAsync(buffer, Ct);
            Assert.Equal((ReadOutcomeKind.Block, Block), (read.Kind, read.Length));
            Assert.Equal(expected, buffer);
        }

        Assert.Equal(ReadOutcomeKind.Filemark, (await drive.ReadBlockAsync(buffer, Ct)).Kind);
        Assert.Equal(ReadOutcomeKind.EndOfData, (await drive.ReadBlockAsync(buffer, Ct)).Kind);

        // Past the end nothing more fits.
        await drive.SpaceToEndOfDataAsync(Ct);
        var full = await Assert.ThrowsAsync<TapeFullException>(async () =>
        {
            while (true)
            {
                await drive.WriteBlockAsync(new byte[Block], Ct);
            }
        });
        Assert.Contains("drive 1", full.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Files_are_found_by_number_and_short_blocks_read_back_with_their_length()
    {
        var (_, drive) = await LoadedDriveAsync();
        await drive.PrepareAsync(Ct);
        for (var file = 0; file < 3; file++)
        {
            await drive.WriteBlockAsync(new byte[1000 + file], Ct);
            await drive.WriteFilemarksAsync(1, immediate: true, Ct);
        }

        await drive.LocateFileAsync(2, Ct);
        var buffer = new byte[Block];
        Assert.Equal((ReadOutcomeKind.Block, 1002), (await drive.ReadBlockAsync(buffer, Ct)) is var r ? (r.Kind, r.Length) : default);
        var position = await drive.ReadPositionAsync(Ct);
        Assert.Equal((5L, (long?)2), (position.Block, position.File));

        // From the end, two filemarks back lands before the second-to-last filemark: on the last block of file 1.
        await drive.SpaceToEndOfDataAsync(Ct);
        await drive.SpaceFilemarksAsync(-2, Ct);
        Assert.Equal(3L, (await drive.ReadPositionAsync(Ct)).Block);
    }

    [Fact]
    public async Task A_block_larger_than_the_read_buffer_is_named_instead_of_silently_cut()
    {
        var (_, drive) = await LoadedDriveAsync();
        await drive.WriteBlockAsync(new byte[2 * Block], Ct);
        await drive.RewindAsync(Ct);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => drive.ReadBlockAsync(new byte[Block], Ct));
        Assert.Contains("larger blocks", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worm_cartridges_take_appends_but_are_never_overwritten_or_erased()
    {
        var (library, drive) = await LoadedDriveAsync(worm: true);
        var medium = await drive.ReadMediumAsync(Ct);
        Assert.True(medium.Worm);
        await drive.WriteBlockAsync(new byte[100], Ct);
        await drive.WriteFilemarksAsync(1, immediate: false, Ct);

        await drive.RewindAsync(Ct);
        await Assert.ThrowsAsync<TapeWriteProtectedException>(() => drive.WriteBlockAsync(new byte[100], Ct));

        // Erase is refused before anything reaches the drive.
        library.CommandLog.Clear();
        await Assert.ThrowsAsync<TapeWriteProtectedException>(() => drive.EraseAsync(Ct));
        Assert.DoesNotContain(library.CommandLog, c => c.Contains("ERASE", StringComparison.Ordinal));

        await drive.SpaceToEndOfDataAsync(Ct);
        Assert.False((await drive.WriteBlockAsync(new byte[100], Ct)).EarlyWarning);
        Assert.Equal(3, library.Drives[0].Cartridge!.Records.Count);
    }

    [Fact]
    public async Task A_write_protected_cartridge_is_refused_without_a_write_reaching_the_drive()
    {
        var (library, drive) = await LoadedDriveAsync();
        library.Drives[0].Cartridge!.WriteProtected = true;
        await drive.ReadMediumAsync(Ct);
        library.CommandLog.Clear();
        await Assert.ThrowsAsync<TapeWriteProtectedException>(() => drive.WriteBlockAsync(new byte[10], Ct));
        Assert.Empty(library.CommandLog);
    }

    [Fact]
    public async Task Hardware_encryption_round_trips_and_a_wrong_or_missing_key_cannot_read()
    {
        var (library, drive) = await LoadedDriveAsync();
        var capabilities = (await drive.ReadEncryptionCapabilitiesAsync(Ct))!;
        Assert.Equal(32, capabilities.KeyLength);
        var key = RandomNumberGenerator.GetBytes(32);
        var data = RandomNumberGenerator.GetBytes(4096);

        await drive.WriteBlockAsync("label"u8.ToArray(), Ct); // in clear
        await drive.SetEncryptionAsync(capabilities, key, "BDFU-key-1"u8.ToArray(), Ct);
        await drive.WriteBlockAsync(data, Ct);
        Assert.Equal("BDFU-key-1"u8.ToArray(), library.Drives[0].Cartridge!.Records[1].KeyId);

        // Never the key in the command log.
        Assert.DoesNotContain(library.CommandLog, c => c.Contains(Convert.ToHexString(key), StringComparison.Ordinal));

        await drive.RewindAsync(Ct);
        var buffer = new byte[Block];
        Assert.Equal(5, (await drive.ReadBlockAsync(buffer, Ct)).Length);
        Assert.Equal(4096, (await drive.ReadBlockAsync(buffer, Ct)).Length);
        Assert.Equal(data, buffer[..4096]);

        await drive.ClearEncryptionAsync(Ct);
        await drive.LocateBlockAsync(1, Ct);
        var error = await Assert.ThrowsAsync<ScsiCommandException>(() => drive.ReadBlockAsync(buffer, Ct));
        Assert.Equal(SenseKey.DataProtect, error.Sense!.Key);

        // A drive that cannot encrypt (Holo) answers null rather than failing.
        library.HoloQuirks = true;
        Assert.Null(await drive.ReadEncryptionCapabilitiesAsync(Ct));
    }

    [Fact]
    public async Task Host_attributes_go_into_the_cartridge_memory_and_tape_alerts_are_read()
    {
        var (library, drive) = await LoadedDriveAsync();
        await drive.WriteAttributesAsync([MamAttributes.Ascii(MamAttributes.ApplicationVendor, "BDFU", 8), MamAttributes.Ascii(MamAttributes.Barcode, "BDF000L9", 32)], Ct);
        var medium = await drive.ReadMediumAsync(Ct);
        Assert.Equal("BDF000L9", medium.Barcode);
        Assert.Equal("BDFU", MamAttributes.Text(medium.Attributes, MamAttributes.ApplicationVendor));

        library.Drives[0].TapeAlerts.Add(20);
        Assert.Equal([20], await drive.ReadTapeAlertsAsync(Ct));
    }

    [Fact]
    public async Task After_a_unit_attention_a_command_is_repeated_only_with_the_same_cartridge_at_the_same_position()
    {
        var (library, drive) = await LoadedDriveAsync();
        var seen = 0;
        drive.Runner.UnitAttention += _ => seen++;

        // Status commands are simply repeated.
        library.Drives[0].QueueUnitAttention(0x29, 0x00);
        await drive.ReadPositionAsync(Ct);
        Assert.Equal(1, seen);

        // A rewind is repeated after checking the cartridge (as Holo reports 28/00 only at the next command).
        library.Drives[0].QueueUnitAttention(0x28, 0x00);
        await drive.RewindAsync(Ct);
        await drive.WriteBlockAsync(new byte[100], Ct);

        // A write is repeated when the tape is where it was.
        library.Drives[0].QueueUnitAttention(0x28, 0x00);
        await drive.WriteBlockAsync(new byte[100], Ct);
        Assert.Equal(2, library.Drives[0].Cartridge!.Records.Count);

        // Another cartridge behind the drive's back: nothing is written to it.
        library.BecomingReadyPolls = 0;
        var other = new FakeCartridge("OTHER1L9", 8 << 20);
        library.Drives[0].OperatorInsert(other);
        var error = await Assert.ThrowsAsync<IOException>(() => drive.WriteBlockAsync(new byte[100], Ct));
        Assert.Contains("instead of MSBDF000L9", error.Message, StringComparison.Ordinal);
        Assert.Empty(other.Records);
    }

    [Fact]
    public async Task A_cleaning_cartridge_is_recognised()
    {
        var library = new FakeTapeLibrary();
        var cleaning = library.AddCartridge("CLN001L9", 1 << 20, cleaning: true);
        library.Drives[0].OperatorInsert(cleaning);
        var drive = new TapeDrive(new ScsiCommandRunner(library.Drives[0], "drive 1"), new FakeTimeProviderNoDelay());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => drive.WaitReadyAsync(TimeSpan.FromMinutes(1), Ct));
        Assert.Contains("cleaning", error.Message, StringComparison.Ordinal);
    }

    private static async Task<(FakeTapeLibrary Library, TapeDrive Drive)> LoadedDriveAsync(long capacity = 64L << 20, bool worm = false, bool holo = false)
    {
        var library = new FakeTapeLibrary { HoloQuirks = holo };
        var cartridge = library.AddCartridge("BDF000L9", capacity, worm);
        cartridge.EarlyWarningZone = 1L << 20;
        library.Drives[0].OperatorInsert(cartridge);
        var drive = new TapeDrive(new ScsiCommandRunner(library.Drives[0], "drive 1"), new FakeTimeProviderNoDelay());
        await drive.WaitReadyAsync(TimeSpan.FromMinutes(1), Ct);
        await drive.ReadMediumAsync(Ct);
        return (library, drive);
    }

    /// <summary>A time provider whose delays end at once (the polling loops do not wait in tests).</summary>
    private sealed class FakeTimeProviderNoDelay : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
    }
}
