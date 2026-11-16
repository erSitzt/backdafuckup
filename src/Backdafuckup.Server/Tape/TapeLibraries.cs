using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Tape;

public sealed class TapeOptions
{
    /// <summary>How often the mail slots are checked for cartridges an operator put in; zero turns it off.</summary>
    public TimeSpan MailSlotPoll { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a drive may take to become ready after a load (a new LTO-9 cartridge is calibrated for up to 2 hours).</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromHours(2.5);

    /// <summary>How often a loading drive is asked whether it is ready.</summary>
    public TimeSpan ReadyPoll { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Kept free before the end of a tape (at least; 0.25 % of a larger cartridge): a file is started only where it fits.
    /// Drives warn before their end; a virtual library may not.
    /// </summary>
    public int ReserveMiB { get; set; } = 128;

    /// <summary>Argon2id cost of new tape keys: 256 MiB, 3 passes, 4 lanes (written into every tape's label).</summary>
    public int KdfMemoryKiB { get; set; } = 256 * 1024;

    public int KdfIterations { get; set; } = 3;

    public int KdfParallelism { get; set; } = 4;

    /// <summary>How often a waiting session looks whether the operator did what it asked (a cartridge in a standalone drive).</summary>
    public TimeSpan OperatorPoll { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a session waits for the operator before it gives up.</summary>
    public TimeSpan OperatorTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Where a direct VM restore from tape keeps the chunks it reads; empty: <c>tape/cache</c> in the data directory.</summary>
    public string? CacheDirectory { get; set; }
}

/// <summary>A changer opened for this process alone; closed on dispose.</summary>
public sealed class OpenedChanger(IScsiDevice device, MediumChanger changer) : IAsyncDisposable
{
    public MediumChanger Changer => changer;

    public ValueTask DisposeAsync() => device.DisposeAsync();
}

/// <summary>A drive opened for this process alone; closed on dispose.</summary>
public sealed class OpenedDrive(IScsiDevice device, TapeDrive drive) : IAsyncDisposable
{
    public TapeDrive Drive => drive;

    public ValueTask DisposeAsync() => device.DisposeAsync();
}

/// <summary>
/// Tape libraries and standalone drives: finding them on a host, adding them, and keeping the catalog's view of their
/// cartridges in line with what the changer reports.
/// </summary>
public sealed class TapeLibraries(IDbContextFactory<AppDbContext> dbFactory, TapeHosts hosts, TapeLocks locks, IOptions<TapeOptions> options, TimeProvider time)
{
    /// <summary>WORM cartridges by the media id at the end of an LTO barcode (LTO-3 to LTO-9).</summary>
    private static readonly HashSet<string> WormSuffixes = new(["LT", "LU", "LV", "LW", "LX", "LY", "LZ"], StringComparer.OrdinalIgnoreCase);

    public async Task<TapeScanDto> ScanAsync(string hostId, CancellationToken cancellationToken) =>
        (await ScanCoreAsync(hostId, cancellationToken).ConfigureAwait(false)).Result;

    public async Task<TapeLibraryEntity> CreateAsync(CreateTapeLibraryDto request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var drives = request.Drives ?? [];
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        if (drives.Count == 0)
        {
            throw new TapeRequestException("Choose at least one drive.");
        }

        if (drives.Select(d => d.Serial).Distinct(StringComparer.Ordinal).Count() != drives.Count)
        {
            throw new TapeRequestException("A drive is listed twice.");
        }

        if (request.ChangerSerial is null && (drives.Count != 1 || drives[0].Element is not null))
        {
            throw new TapeRequestException("A standalone drive is one drive without a changer: choose one drive and no element.");
        }

        if (request.ChangerSerial is not null && (drives.Any(d => d.Element is null) || drives.Select(d => d.Element).Distinct().Count() != drives.Count))
        {
            throw new TapeRequestException("Every drive of a library needs its own drive element in the changer.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.TapeLibraries.AnyAsync(l => l.Name == name, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A tape library named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        if (request.ChangerSerial is { } changerSerial && await db.TapeLibraries.FirstOrDefaultAsync(l => l.ChangerSerial == changerSerial, cancellationToken).ConfigureAwait(false) is { } owner)
        {
            throw new TapeRequestException($"The changer {changerSerial} belongs to the library '{owner.Name}' already.", StatusCodes.Status409Conflict);
        }

        var serials = drives.Select(d => d.Serial).ToList();
        if (await db.TapeDrives.FirstOrDefaultAsync(d => serials.Contains(d.Serial), cancellationToken).ConfigureAwait(false) is { } taken)
        {
            var library = await db.TapeLibraries.FindAsync([taken.LibraryId], cancellationToken).ConfigureAwait(false);
            throw new TapeRequestException($"The drive {taken.Serial} belongs to the library '{library?.Name}' already.", StatusCodes.Status409Conflict);
        }

        var (scan, devices) = await ScanCoreAsync(request.Host, cancellationToken).ConfigureAwait(false);
        var hostName = (await hosts.GetAsync(request.Host, cancellationToken).ConfigureAwait(false)).Name;
        var problems = scan.Problems.Count > 0 ? " " + string.Join(" ", scan.Problems) : string.Empty;
        ScsiDeviceInfo? changer = null;
        if (request.ChangerSerial is not null)
        {
            var found = scan.Changers.FirstOrDefault(c => c.Changer.Serial == request.ChangerSerial)
                ?? throw new TapeRequestException($"No changer with serial {request.ChangerSerial} is attached to {hostName}.{problems}");
            foreach (var drive in drives.Where(d => found.Drives.All(e => e.Element != d.Element)))
            {
                throw new TapeRequestException(
                    $"The changer {request.ChangerSerial} has no drive element {drive.Element}; its drive elements are {string.Join(", ", found.Drives.Select(e => e.Element))}.");
            }

            changer = devices.First(d => d.Serial == request.ChangerSerial);
        }

        var entity = new TapeLibraryEntity
        {
            Name = name,
            Host = request.Host,
            ChangerSerial = request.ChangerSerial,
            Vendor = changer?.Vendor ?? string.Empty,
            Product = changer?.Model ?? string.Empty,
            CreatedUtc = time.GetUtcNow(),
        };
        var number = 0;
        foreach (var drive in drives.OrderBy(d => d.Element))
        {
            number++;
            var device = devices.FirstOrDefault(d => d.Serial == drive.Serial && d.PeripheralType == InquiryData.SequentialAccess)
                ?? throw new TapeRequestException($"No tape drive with serial {drive.Serial} is attached to {hostName}.{problems}");
            entity.Drives.Add(new TapeDriveEntity
            {
                Name = string.IsNullOrWhiteSpace(drive.Name) ? $"Drive {number}" : drive.Name.Trim(),
                Serial = drive.Serial,
                Vendor = device.Vendor,
                Product = device.Model,
                ElementAddress = drive.Element,
            });
        }

        if (changer is null)
        {
            entity.Vendor = entity.Drives[0].Vendor;
            entity.Product = entity.Drives[0].Product;
        }

        db.TapeLibraries.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await InventoryAsync(entity.Id, $"Inventory of the new library {name}", initialize: false, log: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            await RecordErrorAsync(entity.Id, $"The first inventory failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }

        return entity;
    }

    /// <summary>
    /// Reads where every cartridge is (with <paramref name="initialize"/>, the robot first checks every element) and
    /// updates the catalog. Returns what looked odd. A standalone drive is asked which cartridge it holds.
    /// </summary>
    public async Task<IReadOnlyList<string>> InventoryAsync(Guid libraryId, string holder, bool initialize, Action<string>? log, CancellationToken cancellationToken)
    {
        var library = await LoadAsync(libraryId, cancellationToken).ConfigureAwait(false);
        if (library.ChangerSerial is null)
        {
            return await StandaloneInventoryAsync(library, holder, cancellationToken).ConfigureAwait(false);
        }

        await using var robot = await locks.Robot(library.Id).AcquireAsync(holder, h => log?.Invoke($"Waiting for the robot of {library.Name}: '{h}' uses it."), cancellationToken).ConfigureAwait(false);
        await using var opened = await OpenChangerAsync(library, cancellationToken).ConfigureAwait(false);
        if (initialize)
        {
            log?.Invoke($"The robot of {library.Name} checks every element (INITIALIZE ELEMENT STATUS); on a large library this takes minutes.");
            await opened.Changer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var inventory = await opened.Changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyAsync(library.Id, inventory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Brings the catalog in line with an inventory the caller read (holding the robot).</summary>
    public async Task<IReadOnlyList<string>> ApplyAsync(Guid libraryId, ChangerInventory inventory, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var library = await db.TapeLibraries.Include(l => l.Drives).FirstAsync(l => l.Id == libraryId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        library.FirstSlot = inventory.Addresses.FirstStorage;
        library.Slots = inventory.Addresses.Storage;
        library.FirstMailSlot = inventory.Addresses.FirstImportExport;
        library.MailSlots = inventory.Addresses.ImportExport;

        var barcodes = inventory.All.Where(e => e.Full && !string.IsNullOrWhiteSpace(e.VolumeTag)).Select(e => e.VolumeTag!.Trim()).ToList();
        var media = await db.TapeMedia.Where(m => m.LibraryId == libraryId || barcodes.Contains(m.Barcode)).ToDictionaryAsync(m => m.Barcode, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var seen = new Dictionary<string, ChangerElement>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var element in inventory.All)
        {
            if (element.Exception)
            {
                problems.Add($"{Describe(library, element)} reports a fault (ASC/ASCQ {element.Asc:X2}/{element.Ascq:X2}).");
            }

            if (!element.Full)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(element.VolumeTag))
            {
                problems.Add($"{Describe(library, element)} holds a cartridge without a readable barcode label; it is not tracked.");
                continue;
            }

            var barcode = element.VolumeTag.Trim();
            if (seen.TryGetValue(barcode, out var first))
            {
                problems.Add($"The barcode {barcode} is in {Describe(library, first)} and in {Describe(library, element)}; only the first is tracked.");
                continue;
            }

            seen[barcode] = element;
            if (!media.TryGetValue(barcode, out var medium))
            {
                medium = new TapeMediumEntity { Barcode = barcode, Kind = KindFromBarcode(barcode), FirstSeenUtc = now };
                db.TapeMedia.Add(medium);
                media[barcode] = medium;
            }
            else if (medium.LibraryId is { } other && other != libraryId && medium.Location != TapeLocation.Offline)
            {
                problems.Add($"{barcode} was in another library at its last inventory; it is now tracked in {library.Name}.");
            }

            medium.LibraryId = libraryId;
            medium.Location = element.Type switch
            {
                Cdb.ElementType.Storage => TapeLocation.Slot,
                Cdb.ElementType.ImportExport => TapeLocation.MailSlot,
                _ => TapeLocation.Drive,
            };
            medium.Address = element.Address;
            medium.SeenUtc = now;
            if (element.Type == Cdb.ElementType.Storage)
            {
                medium.HomeSlot = element.Address;
            }
            else if (element.SourceValid && inventory.Slots.Any(s => s.Address == element.SourceAddress))
            {
                medium.HomeSlot = element.SourceAddress;
            }
        }

        foreach (var medium in media.Values.Where(m => m.LibraryId == libraryId && !seen.ContainsKey(m.Barcode) && m.Location != TapeLocation.Offline))
        {
            medium.Location = TapeLocation.Offline;
            medium.Address = null;
        }

        foreach (var drive in library.Drives)
        {
            drive.Barcode = drive.ElementAddress is { } address && inventory.At((ushort)address) is { Full: true } element ? element.VolumeTag?.Trim() : null;
        }

        library.InventoryUtc = now;
        library.LastError = problems.Count == 0 ? null : string.Join(" ", problems);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return problems;
    }

    /// <summary>
    /// The mail slots alone (the watcher's poll): cartridges an operator put in are recorded, those taken out go offline.
    /// Returns whether anything changed.
    /// </summary>
    public async Task<bool> ApplyMailSlotsAsync(Guid libraryId, IReadOnlyList<ChangerElement> mailSlots, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var present = mailSlots.Where(e => e.Full && !string.IsNullOrWhiteSpace(e.VolumeTag))
            .GroupBy(e => e.VolumeTag!.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var changed = false;
        var inMail = await db.TapeMedia.Where(m => m.LibraryId == libraryId && m.Location == TapeLocation.MailSlot).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var medium in inMail.Where(m => !present.ContainsKey(m.Barcode)))
        {
            medium.Location = TapeLocation.Offline;
            medium.Address = null;
            changed = true;
        }

        var barcodes = present.Keys.ToList();
        var known = await db.TapeMedia.Where(m => barcodes.Contains(m.Barcode)).ToDictionaryAsync(m => m.Barcode, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        foreach (var (barcode, element) in present)
        {
            if (!known.TryGetValue(barcode, out var medium))
            {
                medium = new TapeMediumEntity { Barcode = barcode, Kind = KindFromBarcode(barcode), FirstSeenUtc = now };
                db.TapeMedia.Add(medium);
            }
            else if (medium.LibraryId == libraryId && medium.Location == TapeLocation.MailSlot && medium.Address == element.Address)
            {
                continue;
            }

            medium.LibraryId = libraryId;
            medium.Location = TapeLocation.MailSlot;
            medium.Address = element.Address;
            medium.SeenUtc = now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>The slot a cartridge of this library lives in, if it has one.</summary>
    public async Task<int?> HomeSlotAsync(string barcode, Guid libraryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TapeMedia.Where(m => m.Barcode == barcode && m.LibraryId == libraryId).Select(m => m.HomeSlot).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what the cartridge memory says about a loaded cartridge.</summary>
    public async Task RecordMediumAsync(Guid libraryId, Guid driveId, string barcode, MediumInfo info, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var medium = await db.TapeMedia.FirstOrDefaultAsync(m => m.Barcode == barcode, cancellationToken).ConfigureAwait(false);
        if (medium is null)
        {
            medium = new TapeMediumEntity { Barcode = barcode, FirstSeenUtc = time.GetUtcNow(), LibraryId = libraryId };
            db.TapeMedia.Add(medium);
        }

        var drive = await db.TapeDrives.FirstAsync(d => d.Id == driveId, cancellationToken).ConfigureAwait(false);
        drive.Barcode = barcode;
        medium.Kind = info.Cleaning ? TapeMediumKind.Cleaning : info.Worm ? TapeMediumKind.Worm : TapeMediumKind.Data;
        medium.MediumSerial = info.MediumSerial ?? medium.MediumSerial;
        medium.CapacityBytes = info.CapacityBytes ?? medium.CapacityBytes;
        medium.RemainingBytes = info.RemainingBytes ?? medium.RemainingBytes;
        medium.Location = TapeLocation.Drive;
        medium.Address = drive.ElementAddress;
        medium.SeenUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the label says about a cartridge, in the catalog: blank, free, foreign, or a tape of a set (appendable when it
    /// is the last tape of an open set, expired with its set, otherwise full). Returns it in words.
    /// </summary>
    public async Task<string> ApplyLabelAsync(string barcode, Backdafuckup.Tape.Format.TapeLabelRead read, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var medium = await db.TapeMedia.FirstAsync(m => m.Barcode == barcode, cancellationToken).ConfigureAwait(false);
        string text;
        if (read.Blank || read.Foreign || read.Label?.Set is null)
        {
            medium.State = read.Blank ? TapeMediumState.Blank : read.Foreign ? TapeMediumState.Foreign : TapeMediumState.Free;
            medium.SetId = null;
            medium.PoolId = null;
            medium.Sequence = null;
            medium.FileCount = read.Label is null ? 0 : 1;
            medium.MediumId = read.Label?.MediumId;
            medium.LabelName = read.Label?.Name;
            medium.LabelEncryption = null;
            text = read.Blank ? $"{barcode} is blank." : read.Foreign ? $"{barcode} holds data that is not backdafuckup's ({read.Reason}); it is never written to." : $"{barcode} is labelled and free.";
        }
        else
        {
            var label = read.Label;
            var labelSet = label.Set!;
            var set = await db.MediaSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == labelSet.Id, cancellationToken).ConfigureAwait(false);
            var last = await db.TapeMedia.Where(m => m.SetId == labelSet.Id).MaxAsync(m => m.Sequence, cancellationToken).ConfigureAwait(false);
            medium.State = set?.State switch
            {
                MediaSetState.Open when last is null || labelSet.Sequence >= last => TapeMediumState.Appendable,
                MediaSetState.Expired or MediaSetState.Overwritten when medium.Kind != TapeMediumKind.Worm => TapeMediumState.Expired,
                _ => TapeMediumState.Full,
            };
            if (medium.SetId != labelSet.Id)
            {
                medium.FileCount = 0;
            }

            medium.SetId = labelSet.Id;
            medium.PoolId = labelSet.PoolId;
            medium.Sequence = labelSet.Sequence;
            medium.MediumId = label.MediumId;
            medium.LabelName = label.Name;
            medium.LabelEncryption = label.Encryption is { } encryption ? Backdafuckup.Tape.Format.TapeJson.ToText(encryption) : null;
            text = set is null
                ? $"{barcode} is tape #{labelSet.Sequence} of set {labelSet.Id} of pool {labelSet.PoolName}, which this server does not know: it is not written to."
                : $"{barcode} is tape #{labelSet.Sequence} of set {labelSet.Id} (pool {labelSet.PoolName}, {set.State.ToString().ToLowerInvariant()}): {medium.State.ToString().ToLowerInvariant()}.";
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return text;
    }

    public async Task RecordErrorAsync(Guid libraryId, string? error, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.TapeLibraries.Where(l => l.Id == libraryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastError, error), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TapeLibraryEntity> LoadAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape library {libraryId} does not exist.", StatusCodes.Status404NotFound);
    }

    public async Task<OpenedChanger> OpenChangerAsync(TapeLibraryEntity library, CancellationToken cancellationToken)
    {
        var host = await hosts.GetAsync(library.Host, cancellationToken).ConfigureAwait(false);
        var serial = library.ChangerSerial ?? throw new InvalidOperationException($"{library.Name} is a standalone drive without a changer.");
        var device = await host.OpenAsync(serial, cancellationToken).ConfigureAwait(false);
        return new OpenedChanger(device, new MediumChanger(new ScsiCommandRunner(device, $"the changer of {library.Name} (serial {serial} on {host.Name}, {device.Path})", time)));
    }

    public async Task<OpenedDrive> OpenDriveAsync(TapeLibraryEntity library, TapeDriveEntity drive, CancellationToken cancellationToken)
    {
        var host = await hosts.GetAsync(library.Host, cancellationToken).ConfigureAwait(false);
        var device = await host.OpenAsync(drive.Serial, cancellationToken).ConfigureAwait(false);
        var runner = new ScsiCommandRunner(device, $"{drive.Name} of {library.Name} (serial {drive.Serial} on {host.Name}, {device.Path})", time);
        return new OpenedDrive(device, new TapeDrive(runner, time) { PollInterval = options.Value.ReadyPoll });
    }

    public async Task<IReadOnlyList<TapeLibraryDto>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).OrderBy(l => l.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var counts = await db.TapeMedia.Where(m => m.LibraryId != null && m.Location != TapeLocation.Offline)
            .GroupBy(m => new { m.LibraryId, Mail = m.Location == TapeLocation.MailSlot })
            .Select(g => new { g.Key.LibraryId, g.Key.Mail, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await hosts.NamesAsync(cancellationToken).ConfigureAwait(false);
        return libraries.Select(l => ToDto(
            l,
            counts.Where(c => c.LibraryId == l.Id).Sum(c => c.Count),
            counts.Where(c => c.LibraryId == l.Id && c.Mail).Sum(c => c.Count),
            names)).ToList();
    }

    public async Task<TapeLibraryDto> GetAsync(Guid libraryId, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(l => l.Id == libraryId)
            ?? throw new TapeRequestException($"Tape library {libraryId} does not exist.", StatusCodes.Status404NotFound);

    public async Task<IReadOnlyList<TapeMediumDto>> MediaAsync(Guid? libraryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).ToDictionaryAsync(l => l.Id, cancellationToken).ConfigureAwait(false);
        var query = db.TapeMedia.AsNoTracking();
        if (libraryId is not null)
        {
            query = query.Where(m => m.LibraryId == libraryId);
        }

        var media = await query.OrderBy(m => m.Barcode).ToListAsync(cancellationToken).ConfigureAwait(false);
        var pools = await db.MediaPools.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        return media.Select(m =>
        {
            var library = m.LibraryId is { } id && libraries.TryGetValue(id, out var l) ? l : null;
            var drive = m.Location == TapeLocation.Drive ? library?.Drives.FirstOrDefault(d => d.ElementAddress == m.Address || (d.ElementAddress is null && d.Barcode == m.Barcode)) : null;
            return new TapeMediumDto(
                m.Id, m.Barcode, m.LibraryId, library?.Name, m.Kind.ToString(), m.Location.ToString(), m.Address, drive?.Name, m.HomeSlot,
                m.MediumSerial, m.CapacityBytes, m.RemainingBytes, m.SeenUtc, m.State.ToString(), m.PoolId is { } pool ? pools.GetValueOrDefault(pool) : null, m.SetId, m.Sequence, m.BytesWritten);
        }).ToList();
    }

    public async Task RenameAsync(Guid libraryId, string name, CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            throw new TapeRequestException("The name must have 1 to 100 characters.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var library = await db.TapeLibraries.FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape library {libraryId} does not exist.", StatusCodes.Status404NotFound);
        if (await db.TapeLibraries.AnyAsync(l => l.Name == name && l.Id != libraryId, cancellationToken).ConfigureAwait(false))
        {
            throw new TapeRequestException($"A tape library named '{name}' exists already.", StatusCodes.Status409Conflict);
        }

        library.Name = name;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a library from the catalog (its cartridges stay known, without a library); refused while it is in use.</summary>
    public async Task DeleteAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var library = await db.TapeLibraries.Include(l => l.Drives).FirstOrDefaultAsync(l => l.Id == libraryId, cancellationToken).ConfigureAwait(false)
            ?? throw new TapeRequestException($"Tape library {libraryId} does not exist.", StatusCodes.Status404NotFound);
        var id = libraryId.ToString();
        var sessions = await db.Sessions.AnyAsync(
            s => s.Kind == SessionKind.TapeMaintenance && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping) && s.Parameters!.Contains(id),
            cancellationToken).ConfigureAwait(false);
        if (sessions || locks.AnyHeld(library.Drives.Select(d => d.Id), library.Id))
        {
            throw new TapeRequestException($"{library.Name} is in use (a tape session runs or waits for it); try again when it has finished.", StatusCodes.Status409Conflict);
        }

        await db.TapeMedia.Where(m => m.LibraryId == libraryId).ExecuteUpdateAsync(
            s => s.SetProperty(m => m.LibraryId, (Guid?)null).SetProperty(m => m.Location, TapeLocation.Offline).SetProperty(m => m.Address, (int?)null),
            cancellationToken).ConfigureAwait(false);
        db.TapeLibraries.Remove(library);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every slot, mail slot and drive with what is in it, for the slot map.</summary>
    public async Task<IReadOnlyList<TapeElementDto>> ElementsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var library = await LoadAsync(libraryId, cancellationToken).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var media = await db.TapeMedia.AsNoTracking().Where(m => m.LibraryId == libraryId && m.Location != TapeLocation.Offline && m.Address != null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        TapeMediumEntity? At(TapeLocation location, int address) => media.FirstOrDefault(m => m.Location == location && m.Address == address);
        var result = new List<TapeElementDto>();
        foreach (var (drive, number) in library.Drives.OrderBy(d => d.ElementAddress).Select((d, i) => (d, i + 1)))
        {
            var medium = drive.ElementAddress is { } address ? At(TapeLocation.Drive, address) : media.FirstOrDefault(m => m.Location == TapeLocation.Drive);
            result.Add(new TapeElementDto("Drive", drive.ElementAddress ?? 0, number, medium?.Barcode ?? drive.Barcode, medium?.Kind.ToString(), drive.Id, drive.Name));
        }

        for (var i = 0; i < library.MailSlots; i++)
        {
            var medium = At(TapeLocation.MailSlot, library.FirstMailSlot + i);
            result.Add(new TapeElementDto("MailSlot", library.FirstMailSlot + i, i + 1, medium?.Barcode, medium?.Kind.ToString(), null, null));
        }

        for (var i = 0; i < library.Slots; i++)
        {
            var medium = At(TapeLocation.Slot, library.FirstSlot + i);
            result.Add(new TapeElementDto("Slot", library.FirstSlot + i, i + 1, medium?.Barcode, medium?.Kind.ToString(), null, null));
        }

        return result;
    }

    public TapeDriveDto ToDto(TapeDriveEntity drive)
    {
        var (holder, since, waiting) = locks.Drive(drive.Id).Snapshot();
        return new TapeDriveDto(drive.Id, drive.Name, drive.Serial, drive.Vendor, drive.Product, drive.ElementAddress, drive.Barcode, holder, since, waiting);
    }

    /// <summary>"slot 3 (element 1026)", for messages.</summary>
    public static string Describe(TapeLibraryEntity library, ChangerElement element) => element.Type switch
    {
        Cdb.ElementType.Storage => $"slot {element.Address - library.FirstSlot + 1} (element {element.Address})",
        Cdb.ElementType.ImportExport => $"mail slot {element.Address - library.FirstMailSlot + 1} (element {element.Address})",
        Cdb.ElementType.DataTransfer => library.Drives.FirstOrDefault(d => d.ElementAddress == element.Address) is { } drive
            ? $"{drive.Name} (element {element.Address})"
            : $"drive element {element.Address}",
        _ => $"element {element.Address}",
    };

    public static TapeMediumKind KindFromBarcode(string barcode)
    {
        if (barcode.StartsWith("CLN", StringComparison.OrdinalIgnoreCase) || (barcode.Length == 8 && barcode.EndsWith("CU", StringComparison.OrdinalIgnoreCase)))
        {
            return TapeMediumKind.Cleaning;
        }

        return barcode.Length == 8 && WormSuffixes.Contains(barcode[6..]) ? TapeMediumKind.Worm : TapeMediumKind.Data;
    }

    private TapeLibraryDto ToDto(TapeLibraryEntity library, int media, int mailSlotsFull, IReadOnlyDictionary<string, string> names) => new(
        library.Id,
        library.Name,
        library.Host,
        names.GetValueOrDefault(library.Host) ?? library.Host,
        library.ChangerSerial,
        library.Vendor,
        library.Product,
        library.Slots,
        library.MailSlots,
        media,
        mailSlotsFull,
        library.InventoryUtc,
        library.LastError,
        library.Drives.OrderBy(d => d.ElementAddress).ThenBy(d => d.Name).Select(ToDto).ToList());

    private async Task<IReadOnlyList<string>> StandaloneInventoryAsync(TapeLibraryEntity library, string holder, CancellationToken cancellationToken)
    {
        var drive = library.Drives.Single();
        await using var lease = locks.Drive(drive.Id).TryAcquire(holder);
        if (lease is null)
        {
            return [$"{drive.Name} is in use ('{locks.Drive(drive.Id).Snapshot().Holder}'); its cartridge was not checked."];
        }

        await using var opened = await OpenDriveAsync(library, drive, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var state = await opened.Drive.TestReadyAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var tracked = await db.TapeDrives.FirstAsync(d => d.Id == drive.Id, cancellationToken).ConfigureAwait(false);
        var loaded = await db.TapeMedia.Where(m => m.LibraryId == library.Id && m.Location == TapeLocation.Drive).ToListAsync(cancellationToken).ConfigureAwait(false);
        string? barcode = null;
        if (state == DriveReadiness.Ready)
        {
            var info = await opened.Drive.ReadMediumAsync(cancellationToken).ConfigureAwait(false);
            barcode = info.Barcode?.Trim();
            if (string.IsNullOrEmpty(barcode))
            {
                problems.Add($"The cartridge in {drive.Name} has no barcode in its memory (MAM serial {info.MediumSerial ?? "unknown"}); it is identified by its tape label once a tape job has written one.");
            }
        }
        else if (state is not DriveReadiness.NoMedium)
        {
            problems.Add($"{drive.Name} is not ready ({state}); its cartridge was not checked.");
        }

        foreach (var medium in loaded.Where(m => m.Barcode != barcode))
        {
            medium.Location = TapeLocation.Offline;
            medium.Address = null;
        }

        tracked.Barcode = barcode;
        var library2 = await db.TapeLibraries.FirstAsync(l => l.Id == library.Id, cancellationToken).ConfigureAwait(false);
        library2.InventoryUtc = time.GetUtcNow();
        library2.LastError = problems.Count == 0 ? null : string.Join(" ", problems);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (barcode is not null)
        {
            await RecordMediumAsync(library.Id, drive.Id, barcode, opened.Drive.Medium!, cancellationToken).ConfigureAwait(false);
        }

        return problems;
    }

    private async Task<(TapeScanDto Result, IReadOnlyList<ScsiDeviceInfo> Devices)> ScanCoreAsync(string hostId, CancellationToken cancellationToken)
    {
        var host = await hosts.GetAsync(hostId, cancellationToken).ConfigureAwait(false);
        var devices = await host.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await db.TapeLibraries.AsNoTracking().Include(l => l.Drives).Where(l => l.Host == hostId).ToListAsync(cancellationToken).ConfigureAwait(false);
        string? LibraryOf(string serial) => libraries.FirstOrDefault(l => l.ChangerSerial == serial || l.Drives.Any(d => d.Serial == serial))?.Name;
        TapeDeviceDto Device(string kind, ScsiDeviceInfo d) => new(kind, d.Serial!, d.Vendor, d.Model, d.Revision, d.Path, d.Connection, LibraryOf(d.Serial!));

        var problems = new List<string>();
        foreach (var busy in devices.Where(d => d.Serial is null && d.PeripheralType is InquiryData.SequentialAccess or InquiryData.MediumChanger))
        {
            problems.Add($"{busy.Path} ({busy.Vendor} {busy.Model}, {busy.Connection}) on {host.Name} could not be asked for its serial: another program has it open.");
        }

        var drives = devices.Where(d => d.PeripheralType == InquiryData.SequentialAccess && d.Serial is not null).ToList();
        var mapped = new HashSet<string>(StringComparer.Ordinal);
        var changers = new List<(ScsiDeviceInfo Info, ChangerInventory Inventory, List<TapeScanDriveDto> Drives)>();
        foreach (var info in devices.Where(d => d.PeripheralType == InquiryData.MediumChanger && d.Serial is not null))
        {
            var known = libraries.FirstOrDefault(l => l.ChangerSerial == info.Serial);
            var robot = known is null ? null : await locks.Robot(known.Id).AcquireAsync("Scan for devices", null, cancellationToken).ConfigureAwait(false);
            try
            {
                var device = await host.OpenAsync(info.Serial!, cancellationToken).ConfigureAwait(false);
                ChangerInventory inventory;
                await using (device.ConfigureAwait(false))
                {
                    var changer = new MediumChanger(new ScsiCommandRunner(device, $"the changer {info.Serial} on {host.Name} ({device.Path})", time));
                    inventory = await changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                }

                var elements = new List<TapeScanDriveDto>();
                foreach (var element in inventory.Drives)
                {
                    var match = element.DeviceId is { } id ? drives.FirstOrDefault(d => Matches(d, id)) : null;
                    if (match is not null)
                    {
                        mapped.Add(match.Serial!);
                        elements.Add(new TapeScanDriveDto(element.Address, match.Serial, "dvcid"));
                    }
                    else if (known?.Drives.FirstOrDefault(d => d.ElementAddress == element.Address) is { } saved && drives.Any(d => d.Serial == saved.Serial))
                    {
                        mapped.Add(saved.Serial);
                        elements.Add(new TapeScanDriveDto(element.Address, saved.Serial, "saved"));
                    }
                    else
                    {
                        elements.Add(new TapeScanDriveDto(element.Address, null, "none"));
                    }
                }

                changers.Add((info, inventory, elements));
            }
            catch (IOException ex)
            {
                problems.Add($"The changer {info.Serial} ({info.Path}, {info.Connection}) on {host.Name} could not be read: {ex.Message}");
            }
            finally
            {
                if (robot is not null)
                {
                    await robot.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        // A changer without drive identifiers (DVCID): the remaining drives go to its free drive elements in serial
        // order, marked as a guess for people to check.
        var remaining = new Queue<ScsiDeviceInfo>(drives.Where(d => !mapped.Contains(d.Serial!) && LibraryOf(d.Serial!) is null).OrderBy(d => d.Serial, StringComparer.Ordinal));
        foreach (var (_, _, elements) in changers)
        {
            for (var i = 0; i < elements.Count && remaining.Count > 0; i++)
            {
                if (elements[i].Serial is null)
                {
                    var drive = remaining.Dequeue();
                    mapped.Add(drive.Serial!);
                    elements[i] = new TapeScanDriveDto(elements[i].Element, drive.Serial, "order");
                }
            }
        }

        var result = new TapeScanDto(
            hostId,
            changers.Select(c => new TapeScanChangerDto(Device("changer", c.Info), c.Inventory.Addresses.Storage, c.Inventory.Addresses.ImportExport, c.Drives)).ToList(),
            drives.Where(d => !mapped.Contains(d.Serial!)).Select(d => Device("drive", d)).ToList(),
            problems);
        return (result, devices);
    }

    /// <summary>Whether a changer's drive identifier (DVCID) names this drive: one of its designators, or ending in its serial.</summary>
    private static bool Matches(ScsiDeviceInfo drive, string deviceId) =>
        drive.Designators.Contains(deviceId, StringComparer.Ordinal) ||
        (deviceId.EndsWith(drive.Serial!, StringComparison.Ordinal) && deviceId.Length > drive.Serial!.Length && deviceId[^(drive.Serial.Length + 1)] is ' ' or ':');
}
