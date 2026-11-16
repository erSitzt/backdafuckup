using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

/// <summary>A queued library action (the session's parameters).</summary>
public sealed record TapeSessionRequest(Guid LibraryId, string Action, string? Barcode, Guid? DriveId);

/// <summary>
/// Runs library actions as sessions, so that they are logged and can wait: for a drive (FIFO, the holder is named), for
/// the robot, and for a drive that calibrates a new cartridge. Locks are always taken drive first, robot second.
/// </summary>
public sealed class TapeSessionRunner(TapeLibraries libraries, TapeLocks locks, SessionEventWriter events, IOptions<TapeOptions> options, TimeProvider time)
{
    public async Task<(SessionState State, string Summary)> RunAsync(SessionEntity session, CancellationToken cancellationToken)
    {
        var log = new WarningTrackingLog(events.CreateLog(session.Id, null));
        var request = JsonSerializer.Deserialize<TapeSessionRequest>(session.Parameters ?? "null")
            ?? throw new InvalidOperationException("The tape session has no parameters.");
        var library = await libraries.LoadAsync(request.LibraryId, cancellationToken).ConfigureAwait(false);
        var summary = request.Action switch
        {
            TapeActions.Load => await LoadAsync(library, request, log, cancellationToken).ConfigureAwait(false),
            TapeActions.Unload => await UnloadAsync(library, Drive(library, request.DriveId), log, cancellationToken).ConfigureAwait(false),
            TapeActions.Export => await ExportAsync(library, request.Barcode, log, cancellationToken).ConfigureAwait(false),
            TapeActions.Import => await ImportAsync(library, log, cancellationToken).ConfigureAwait(false),
            TapeActions.Scan => await ScanAsync(library, log, cancellationToken).ConfigureAwait(false),
            _ => throw new TapeRequestException($"Unknown tape action '{request.Action}'."),
        };
        return (log.Warned ? SessionState.Warning : SessionState.Succeeded, summary);
    }

    private async Task<string> LoadAsync(TapeLibraryEntity library, TapeSessionRequest request, IOperationLog log, CancellationToken cancellationToken)
    {
        var barcode = request.Barcode?.Trim() is { Length: > 0 } b ? b : throw new TapeRequestException("A load needs the barcode of the cartridge.");
        if (library.ChangerSerial is null)
        {
            throw new TapeRequestException($"{library.Name} is a standalone drive without a robot: put {barcode} in by hand, then run an inventory.");
        }

        var holder = $"Load {barcode}";
        var (drive, lease) = await ChooseDriveAsync(library, request.DriveId, holder, log, cancellationToken).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            var (summary, _) = await LoadHeldAsync(library, drive, barcode, holder, log, cancellationToken).ConfigureAwait(false);
            return summary;
        }
    }

    /// <summary>
    /// Loads <paramref name="barcode"/> into <paramref name="drive"/>, which the caller holds: a cartridge in the drive
    /// goes back to its slot first; waits until the drive is ready and reads the cartridge memory.
    /// </summary>
    internal async Task<(string Summary, MediumInfo Medium)> LoadHeldAsync(TapeLibraryEntity library, TapeDriveEntity drive, string barcode, string holder, IOperationLog log, CancellationToken cancellationToken)
    {
        {
            log.Info($"Loading {barcode} into {drive.Name} ({drive.Serial}) of {library.Name}.");
            await using (var robot = await AcquireRobotAsync(library, holder, log, cancellationToken).ConfigureAwait(false))
            await using (var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false))
            {
                var inventory = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                var source = inventory.Find(barcode) ?? throw new TapeRequestException($"{barcode} is not in {library.Name}: the changer does not report it in any slot, mail slot or drive.");
                var target = inventory.At(Address(drive)) ?? throw new TapeRequestException($"The changer of {library.Name} has no drive element {drive.ElementAddress} ({drive.Name}); scan the library again.");
                if (source.Address == target.Address)
                {
                    log.Info($"{barcode} is in {drive.Name} already.");
                }
                else
                {
                    if (source.Type == Cdb.ElementType.DataTransfer)
                    {
                        throw new TapeRequestException($"{barcode} is in {TapeLibraries.Describe(library, source)}; unload it there first.");
                    }

                    if (TapeLibraries.KindFromBarcode(barcode) == TapeMediumKind.Cleaning)
                    {
                        throw new TapeRequestException($"{barcode} is a cleaning cartridge; it is only loaded to clean a drive.");
                    }

                    if (target.Full)
                    {
                        log.Info($"{drive.Name} holds {target.VolumeTag?.Trim() ?? "a cartridge without barcode"}; it goes back to a slot first.");
                        await UnloadToSlotAsync(library, drive, changer, inventory, log, cancellationToken).ConfigureAwait(false);
                    }

                    log.Info($"Moving {barcode} from {TapeLibraries.Describe(library, source)} to {drive.Name}.");
                    await changer.Changer.MoveAsync(source.Address, target.Address, cancellationToken).ConfigureAwait(false);
                }

                await libraries.ApplyAsync(library.Id, await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }

            // The robot is free again while the drive loads (and calibrates).
            await using var opened = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
            var started = time.GetUtcNow();
            log.Info($"Waiting for {drive.Name} to be ready. A new LTO-9 cartridge is calibrated on its first load, which can take up to 2 hours.");
            await opened.Drive.WaitReadyAsync(options.Value.LoadTimeout, cancellationToken).ConfigureAwait(false);
            var info = await opened.Drive.ReadMediumAsync(cancellationToken).ConfigureAwait(false);
            await libraries.RecordMediumAsync(library.Id, drive.Id, barcode, info, cancellationToken).ConfigureAwait(false);
            if (info.Barcode?.Trim() is { Length: > 0 } written && written != barcode)
            {
                log.Warning($"The cartridge memory of {barcode} names the barcode {written}: the label and the cartridge do not match.");
            }

            var kind = info.Cleaning ? "cleaning cartridge" : info.Worm ? "WORM cartridge" : "data cartridge";
            var space = info.CapacityBytes is { } capacity
                ? $", {VmBackupTask.Format(info.RemainingBytes ?? 0)} free of {VmBackupTask.Format(capacity)}"
                : string.Empty;
            var summary = $"{barcode} is ready in {drive.Name} after {(time.GetUtcNow() - started).TotalSeconds:0} s: {kind}{space}" +
                (info.WriteProtected ? ", write-protected" : string.Empty) +
                (info.MediumSerial is { } serial ? $", cartridge serial {serial}" : string.Empty) + ".";
            log.Info(summary);
            return (summary, info);
        }
    }

    /// <summary>Unloads <paramref name="drive"/> (held by the caller) into a slot, or into a mail slot with <paramref name="export"/>.</summary>
    internal async Task<string> UnloadHeldAsync(TapeLibraryEntity library, TapeDriveEntity drive, bool export, string holder, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var robot = await AcquireRobotAsync(library, holder, log, cancellationToken).ConfigureAwait(false);
        await using var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false);
        var inventory = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        if (inventory.At(Address(drive)) is not { Full: true } element)
        {
            return $"{drive.Name} is empty.";
        }

        string text;
        var mail = export ? inventory.MailSlots.FirstOrDefault(m => !m.Full && m.Accessible) : null;
        if (mail is not null)
        {
            await EjectAsync(library, drive, log, cancellationToken).ConfigureAwait(false);
            await changer.Changer.MoveAsync(element.Address, mail.Address, cancellationToken).ConfigureAwait(false);
            text = $"{element.VolumeTag?.Trim()} exported to {TapeLibraries.Describe(library, mail)}.";
            log.Info(text);
        }
        else
        {
            if (export)
            {
                log.Warning($"No free mail slot in {library.Name}: {element.VolumeTag?.Trim()} goes back to a slot instead of being exported.");
            }

            text = await UnloadToSlotAsync(library, drive, changer, inventory, log, cancellationToken).ConfigureAwait(false);
        }

        await libraries.ApplyAsync(library.Id, await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return text;
    }

    private async Task<string> UnloadAsync(TapeLibraryEntity library, TapeDriveEntity drive, IOperationLog log, CancellationToken cancellationToken)
    {
        var holder = $"Unload {drive.Name}";
        if (library.ChangerSerial is null)
        {
            await using (var lease = await AcquireDriveAsync(drive, holder, log, cancellationToken).ConfigureAwait(false))
            await using (var opened = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false))
            {
                if (await opened.Drive.TestReadyAsync(cancellationToken).ConfigureAwait(false) == DriveReadiness.NoMedium)
                {
                    return $"{drive.Name} is empty.";
                }

                await opened.Drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
            }

            await libraries.InventoryAsync(library.Id, holder, initialize: false, log: null, cancellationToken).ConfigureAwait(false);
            var text = $"{drive.Name} ejected its cartridge; take it out of the drive.";
            log.Info(text);
            return text;
        }

        await using (await AcquireDriveAsync(drive, holder, log, cancellationToken).ConfigureAwait(false))
        await using (await AcquireRobotAsync(library, holder, log, cancellationToken).ConfigureAwait(false))
        await using (var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false))
        {
            var inventory = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (inventory.At(Address(drive)) is not { Full: true })
            {
                await libraries.ApplyAsync(library.Id, inventory, cancellationToken).ConfigureAwait(false);
                return $"{drive.Name} is empty.";
            }

            var text = await UnloadToSlotAsync(library, drive, changer, inventory, log, cancellationToken).ConfigureAwait(false);
            await libraries.ApplyAsync(library.Id, await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return text;
        }
    }

    private async Task<string> ExportAsync(TapeLibraryEntity library, string? requested, IOperationLog log, CancellationToken cancellationToken)
    {
        var barcode = requested?.Trim() is { Length: > 0 } b ? b : throw new TapeRequestException("An export needs the barcode of the cartridge.");
        if (library.ChangerSerial is null)
        {
            throw new TapeRequestException($"{library.Name} is a standalone drive: unload it and take the cartridge out by hand.");
        }

        var holder = $"Export {barcode}";

        // In a drive: that drive is needed too (drive first, then the robot).
        var media = await libraries.ElementsAsync(library.Id, cancellationToken).ConfigureAwait(false);
        var inDrive = media.FirstOrDefault(e => e.Type == "Drive" && e.Barcode == barcode)?.DriveId is { } driveId ? Drive(library, driveId) : null;
        var lease = inDrive is null ? null : await AcquireDriveAsync(inDrive, holder, log, cancellationToken).ConfigureAwait(false);
        try
        {
            await using var robot = await AcquireRobotAsync(library, holder, log, cancellationToken).ConfigureAwait(false);
            await using var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false);
            var inventory = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
            var source = inventory.Find(barcode) ?? throw new TapeRequestException($"{barcode} is not in {library.Name}: the changer does not report it in any slot, mail slot or drive.");
            if (source.Type == Cdb.ElementType.ImportExport)
            {
                return $"{barcode} is in {TapeLibraries.Describe(library, source)} already; take it out there.";
            }

            var mail = inventory.MailSlots.FirstOrDefault(m => !m.Full && m.Accessible)
                ?? throw new TapeRequestException(library.MailSlots == 0
                    ? $"{library.Name} has no mail slots; take {barcode} out through the library's own interface."
                    : $"All {inventory.MailSlots.Count} mail slots of {library.Name} are full or closed; take the cartridges out first.");
            if (source.Type == Cdb.ElementType.DataTransfer)
            {
                var drive = library.Drives.FirstOrDefault(d => d.ElementAddress == source.Address);
                if (drive is null || drive.Id != inDrive?.Id)
                {
                    throw new TapeRequestException($"{barcode} is in {TapeLibraries.Describe(library, source)}; unload it first.");
                }

                await EjectAsync(library, drive, log, cancellationToken).ConfigureAwait(false);
            }

            log.Info($"Moving {barcode} from {TapeLibraries.Describe(library, source)} to {TapeLibraries.Describe(library, mail)}.");
            await changer.Changer.MoveAsync(source.Address, mail.Address, cancellationToken).ConfigureAwait(false);
            var after = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
            await libraries.ApplyAsync(library.Id, after, cancellationToken).ConfigureAwait(false);
            var text = after.Find(barcode) is not null
                ? $"{barcode} is in {TapeLibraries.Describe(library, mail)} of {library.Name}; take it out there."
                : $"{barcode} was moved to {TapeLibraries.Describe(library, mail)} and the library took it out at once (it is offline now).";
            log.Info(text);
            return text;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<string> ImportAsync(TapeLibraryEntity library, IOperationLog log, CancellationToken cancellationToken)
    {
        if (library.ChangerSerial is null)
        {
            throw new TapeRequestException($"{library.Name} is a standalone drive without mail slots.");
        }

        await using var robot = await AcquireRobotAsync(library, "Import from the mail slots", log, cancellationToken).ConfigureAwait(false);
        await using var changer = await libraries.OpenChangerAsync(library, cancellationToken).ConfigureAwait(false);
        var inventory = await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        var waiting = inventory.MailSlots.Where(m => m.Full).ToList();
        if (waiting.Count == 0)
        {
            await libraries.ApplyAsync(library.Id, inventory, cancellationToken).ConfigureAwait(false);
            return $"No cartridges in the mail slots of {library.Name}.";
        }

        var free = inventory.Slots.Where(s => !s.Full && s.Accessible).Select(s => s.Address).ToList();
        var imported = 0;
        foreach (var mail in waiting)
        {
            var barcode = mail.VolumeTag?.Trim();
            var home = barcode is null ? null : await libraries.HomeSlotAsync(barcode, library.Id, cancellationToken).ConfigureAwait(false);
            var slot = home is { } h && free.Contains((ushort)h) ? (ushort)h : free.Count > 0 ? free[0] : (ushort?)null;
            if (slot is null)
            {
                log.Warning($"No free slot in {library.Name} for {barcode ?? "the cartridge"} in {TapeLibraries.Describe(library, mail)}; it stays there.");
                continue;
            }

            free.Remove(slot.Value);
            await changer.Changer.MoveAsync(mail.Address, slot.Value, cancellationToken).ConfigureAwait(false);
            imported++;
            var into = $"slot {slot.Value - library.FirstSlot + 1} (element {slot.Value})";
            if (barcode is null)
            {
                log.Warning($"The cartridge in {TapeLibraries.Describe(library, mail)} has no readable barcode label; it was moved into {into} but is not tracked. Label it and import it again.");
            }
            else
            {
                log.Info($"{barcode} from {TapeLibraries.Describe(library, mail)} into {into}" +
                    (TapeLibraries.KindFromBarcode(barcode) == TapeMediumKind.Cleaning ? " (a cleaning cartridge)." : "."));
            }
        }

        var problems = await libraries.ApplyAsync(library.Id, await changer.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        foreach (var problem in problems)
        {
            log.Warning(problem);
        }

        return $"{imported} of {waiting.Count} cartridge(s) imported into {library.Name}.";
    }

    private async Task<string> ScanAsync(TapeLibraryEntity library, IOperationLog log, CancellationToken cancellationToken)
    {
        var problems = await libraries.InventoryAsync(library.Id, "Scan every element", initialize: library.ChangerSerial is not null, s => log.Info(s), cancellationToken).ConfigureAwait(false);
        foreach (var problem in problems)
        {
            log.Warning(problem);
        }

        var now = await libraries.GetAsync(library.Id, cancellationToken).ConfigureAwait(false);
        var text = $"Inventory of {library.Name}: {now.Media} cartridge(s), {now.MailSlotsFull} of them in mail slots.";
        log.Info(text);
        return text;
    }

    /// <summary>Ejects the cartridge of <paramref name="drive"/> and moves it to a slot: where it came from, its home slot, or the first free one.</summary>
    private async Task<string> UnloadToSlotAsync(TapeLibraryEntity library, TapeDriveEntity drive, OpenedChanger changer, ChangerInventory inventory, IOperationLog log, CancellationToken cancellationToken)
    {
        var element = inventory.At(Address(drive))!;
        var barcode = element.VolumeTag?.Trim();
        var home = barcode is null ? null : await libraries.HomeSlotAsync(barcode, library.Id, cancellationToken).ConfigureAwait(false);
        bool Free(int address) => inventory.Slots.Any(s => s.Address == address && !s.Full && s.Accessible);
        var slot = element.SourceValid && Free(element.SourceAddress) ? element.SourceAddress
            : home is { } h && Free(h) ? (ushort)h
            : inventory.Slots.FirstOrDefault(s => !s.Full && s.Accessible)?.Address
                ?? throw new TapeRequestException($"{library.Name} has no free slot for {barcode ?? "the cartridge"} from {drive.Name}.");

        await EjectAsync(library, drive, log, cancellationToken).ConfigureAwait(false);
        await changer.Changer.MoveAsync(element.Address, slot, cancellationToken).ConfigureAwait(false);
        var text = $"{barcode ?? "The cartridge"} unloaded from {drive.Name} into slot {slot - library.FirstSlot + 1} (element {slot}).";
        log.Info(text);
        return text;
    }

    /// <summary>Rewinds and unloads the cartridge so that the robot can take it (the caller holds the drive).</summary>
    private async Task EjectAsync(TapeLibraryEntity library, TapeDriveEntity drive, IOperationLog log, CancellationToken cancellationToken)
    {
        await using var opened = await libraries.OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
        if (await opened.Drive.TestReadyAsync(cancellationToken).ConfigureAwait(false) == DriveReadiness.NoMedium)
        {
            return; // Unloaded already; the cartridge waits in the drive's opening.
        }

        log.Info($"Rewinding and unloading {drive.Name}.");
        await opened.Drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The drive asked for, else a free empty one, else a free one, else the first (waiting for it).</summary>
    private async Task<(TapeDriveEntity Drive, IAsyncDisposable Lease)> ChooseDriveAsync(TapeLibraryEntity library, Guid? driveId, string holder, IOperationLog log, CancellationToken cancellationToken)
    {
        if (driveId is not null)
        {
            var asked = Drive(library, driveId);
            return (asked, await AcquireDriveAsync(asked, holder, log, cancellationToken).ConfigureAwait(false));
        }

        foreach (var candidate in library.Drives.OrderBy(d => d.Barcode is null ? 0 : 1).ThenBy(d => d.ElementAddress))
        {
            if (locks.Drive(candidate.Id).TryAcquire(holder) is { } lease)
            {
                return (candidate, lease);
            }
        }

        var first = library.Drives.OrderBy(d => d.ElementAddress).First();
        return (first, await AcquireDriveAsync(first, holder, log, cancellationToken).ConfigureAwait(false));
    }

    private Task<IAsyncDisposable> AcquireDriveAsync(TapeDriveEntity drive, string holder, IOperationLog log, CancellationToken cancellationToken) =>
        locks.Drive(drive.Id).AcquireAsync(holder, h => log.Info($"Waiting for {drive.Name}: '{h}' uses it."), cancellationToken);

    private Task<IAsyncDisposable> AcquireRobotAsync(TapeLibraryEntity library, string holder, IOperationLog log, CancellationToken cancellationToken) =>
        locks.Robot(library.Id).AcquireAsync(holder, h => log.Info($"Waiting for the robot of {library.Name}: '{h}' uses it."), cancellationToken);

    private static TapeDriveEntity Drive(TapeLibraryEntity library, Guid? driveId) =>
        library.Drives.FirstOrDefault(d => d.Id == driveId)
            ?? throw new TapeRequestException(driveId is null ? $"Which drive of {library.Name}?" : $"Drive {driveId} is not a drive of {library.Name}.");

    private static ushort Address(TapeDriveEntity drive) =>
        (ushort)(drive.ElementAddress ?? throw new InvalidOperationException($"{drive.Name} has no element in a changer."));
}
