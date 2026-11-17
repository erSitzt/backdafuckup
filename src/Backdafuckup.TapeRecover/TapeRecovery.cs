using System.Text.Json;
using Backdafuckup.Core.Repository;
using Backdafuckup.Storage;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Format;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.TapeRecover;

/// <summary>
/// The commands of <c>bdfu-tape-recover</c>: reading backdafuckup tapes with only this program, a drive (and a changer,
/// if there is one) and, for encrypted sets, the passphrase or the key file.
/// </summary>
internal sealed class TapeRecovery(ITapeHost host, TextWriter output, TextReader input, Func<string, string?> environment, bool interactive)
{
    private const string Usage = """
        bdfu-tape-recover: reads backdafuckup tapes without the backup server.

          devices                         the tape drives and changers attached
          inventory                       the changer's slots and drives with their cartridges
          scan      [--barcode B]         every file on a tape (its header)
          catalog   [--barcode B]         the set's catalog: its tapes and copies (restore points)
          restore   --copy ID [--barcode B] (--to-repository DIR | --to-images DIR)
                                          a copy into a repository directory (then add it in the console),
                                          or its disks as raw images

        --barcode B          load this cartridge with the changer first (else: the cartridge in the drive)
        --drive SERIAL       the drive to use (else: the first one); --changer SERIAL likewise
        --key-file F         the set's key file; or --passphrase-file F, BDFU_TAPE_PASSPHRASE, or a prompt
        """;

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    private TextWriter Output => output;

    private TextReader Input => input;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Unknown argument '{args[i]}'. Run bdfu-tape-recover --help.");
            }

            _options[args[i][2..]] = args[++i];
        }

        switch (args[0])
        {
            case "devices":
                foreach (var d in await host.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                {
                    output.WriteLine($"{(d.PeripheralType == InquiryData.MediumChanger ? "changer" : "drive  ")}  {d.Vendor} {d.Model}  serial {d.Serial ?? "(open in another program)"}  {d.Path}  {d.Connection}");
                }

                return 0;
            case "inventory":
                await using (var session = await OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    var changer = session.Changer ?? throw new InvalidOperationException("There is no changer: a standalone drive has no inventory.");
                    var inventory = await changer.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var e in inventory.All.Where(e => e.Full))
                    {
                        output.WriteLine($"{e.Type,-14} {e.Address,6}  {e.VolumeTag?.Trim() ?? "(no barcode)"}");
                    }
                }

                return 0;
            case "scan":
                await using (var session = await OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    await session.LoadAsync(Option("barcode"), cancellationToken).ConfigureAwait(false);
                    var label = await session.LabelAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var f in await TapeSetReader.ScanAsync(session.Drive, label.BlockSize, cancellationToken).ConfigureAwait(false))
                    {
                        output.WriteLine($"file {f.FileNumber,4}  block {f.StartBlock,9}  {f.Header.Type,-14} {(f.Header.CopyId is { } copy ? $"copy {copy}" : string.Empty)}");
                    }
                }

                return 0;
            case "catalog":
                await using (var session = await OpenAsync(cancellationToken).ConfigureAwait(false))
                {
                    var catalog = await session.CatalogAsync(Option("barcode"), cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Set {catalog.SetId} of pool {catalog.PoolName}, created {catalog.SetCreatedUtc:yyyy-MM-dd HH:mm} UTC, catalog of {catalog.WrittenUtc:yyyy-MM-dd HH:mm} UTC");
                    foreach (var m in catalog.Media)
                    {
                        output.WriteLine($"  tape #{m.Sequence}: {m.Name}");
                    }

                    foreach (var c in catalog.Copies)
                    {
                        output.WriteLine($"  copy {c.CopyId}  {c.VmName}  {c.CreatedUtc:yyyy-MM-dd HH:mm} UTC  {c.StoredBytes / (1024 * 1024)} MiB  tapes {string.Join(", ", c.NeededSequences.Select(s => $"#{s}"))}");
                    }
                }

                return 0;
            case "restore":
                return await RestoreAsync(cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unknown command '{args[0]}'. Run bdfu-tape-recover --help.");
        }
    }

    private async Task<int> RestoreAsync(CancellationToken cancellationToken)
    {
        var copyId = Option("copy") ?? throw new ArgumentException("Which copy? --copy ID (from the catalog command).");
        var repositoryDirectory = Option("to-repository");
        var imagesDirectory = Option("to-images");
        if ((repositoryDirectory is null) == (imagesDirectory is null))
        {
            throw new ArgumentException("Restore --to-repository DIR or --to-images DIR.");
        }

        await using var session = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await session.CatalogAsync(Option("barcode"), cancellationToken).ConfigureAwait(false);
        var copy = catalog.Copies.FirstOrDefault(c => c.CopyId.ToString().StartsWith(copyId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The set has no copy {copyId}; the catalog command lists them.");
        var manifest = JsonSerializer.Deserialize(copy.Manifest, CoreJsonContext.Default.RestorePointManifest)!;
        var directory = repositoryDirectory ?? Path.Combine(imagesDirectory!, ".bdfu-repository");
        await using var repository = File.Exists(Path.Combine(directory, LocalDirectoryRepository.DescriptorFileName))
            ? await LocalDirectoryRepository.OpenAsync(directory, cancellationToken).ConfigureAwait(false)
            : await LocalDirectoryRepository.CreateAsync(directory, cancellationToken, manifest.Chunking.ChunkSize).ConfigureAwait(false);

        output.WriteLine($"Restoring {copy.VmName} ({copy.CreatedUtc:yyyy-MM-dd HH:mm} UTC) from tape(s) {string.Join(", ", copy.NeededSequences.Select(s => catalog.Media.First(m => m.Sequence == s).Name))} into {directory}.");
        var result = await TapeSetReader.RestoreAsync(
            session,
            session.BlockSize,
            session.Cipher,
            copy.Sequence,
            catalog.Files.First(f => f.Sequence == copy.Sequence && f.FileNumber == copy.FileNumber).StartBlock,
            (sequence, number) => catalog.Files.FirstOrDefault(f => f.Sequence == sequence && f.FileNumber == number) is { } f
                ? (f.StartBlock, f.FileId)
                : throw new InvalidDataException($"Tape #{sequence} file {number} is not in the catalog."),
            repository,
            Guid.CreateVersion7(), // a new id, as the server gives imports: the original may still be in a catalog
            m => m with { Job = null, BaseRestorePointId = null },
            output.WriteLine,
            null,
            cancellationToken).ConfigureAwait(false);
        var restored = result.Committed.RestorePointId;
        output.WriteLine($"{result.Chunks} chunk(s) read from {result.Tapes} tape(s); restore point {restored} (copy of {copy.OriginalId}) is in {directory}.");

        if (imagesDirectory is not null)
        {
            await using var reader = await repository.OpenRestorePointAsync(restored, cancellationToken).ConfigureAwait(false);
            foreach (var disk in reader.Manifest.Disks)
            {
                var path = Path.Combine(imagesDirectory, $"{Safe(copy.VmName)}-{Safe(disk.Key)}.img");
                await WriteImageAsync(reader, disk.Key, path, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"{path}: {new FileInfo(path).Length} bytes");
            }
        }
        else
        {
            output.WriteLine("Add the directory as a repository in the console (Repositories → Add); its restore points are imported with it.");
        }

        return 0;
    }

    private static async Task WriteImageAsync(IRestorePointReader reader, string diskKey, string path, CancellationToken cancellationToken)
    {
        var index = await reader.ReadDiskIndexAsync(diskKey, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[index.ChunkSize];
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous);
        file.SetLength(index.DiskSize); // Zero chunks stay holes (sparse where the file system allows).
        for (var i = 0; i < index.Entries.Length; i++)
        {
            if (index.Entries[i].IsZero)
            {
                continue;
            }

            var n = await reader.Chunks.ReadAsync(index.Entries[i], buffer, cancellationToken).ConfigureAwait(false);
            file.Position = (long)i * index.ChunkSize;
            await file.WriteAsync(buffer.AsMemory(0, (int)Math.Min(n, index.DiskSize - file.Position)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    private string? Option(string name) => _options.GetValueOrDefault(name);

    private async Task<Session> OpenAsync(CancellationToken cancellationToken)
    {
        var devices = await host.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var drive = (Option("drive") is { } d ? devices.FirstOrDefault(x => x.Serial == d) : devices.FirstOrDefault(x => x.PeripheralType == InquiryData.SequentialAccess && x.Serial is not null))
            ?? throw new IOException($"No tape drive found{(Option("drive") is { } s ? $" with serial {s}" : string.Empty)} on {host.Name}.");
        var changerInfo = Option("changer") is { } c ? devices.FirstOrDefault(x => x.Serial == c) : devices.FirstOrDefault(x => x.PeripheralType == InquiryData.MediumChanger && x.Serial is not null);
        var driveDevice = await host.OpenAsync(drive.Serial!, cancellationToken).ConfigureAwait(false);
        var tapeDrive = new TapeDrive(new ScsiCommandRunner(driveDevice, $"drive {drive.Serial} ({driveDevice.Path})")) { PollInterval = TimeSpan.FromMilliseconds(500) };
        IScsiDevice? changerDevice = null;
        MediumChanger? changer = null;
        if (changerInfo is not null)
        {
            changerDevice = await host.OpenAsync(changerInfo.Serial!, cancellationToken).ConfigureAwait(false);
            changer = new MediumChanger(new ScsiCommandRunner(changerDevice, $"changer {changerInfo.Serial} ({changerDevice.Path})"));
        }

        return new Session(this, drive, driveDevice, tapeDrive, changerDevice, changer);
    }

    /// <summary>The set's key from the key file, a passphrase file, the environment or a prompt, checked against the label.</summary>
    private byte[] Kek(TapeLabelEncryption encryption)
    {
        byte[] kek;
        if (Option("key-file") is { } keyFile)
        {
            kek = JsonSerializer.Deserialize<TapeKeyFile>(File.ReadAllBytes(keyFile), TapeJson.Options)!.ReadKek();
        }
        else
        {
            var passphrase = Option("passphrase-file") is { } file
                ? File.ReadAllText(file).TrimEnd('\r', '\n')
                : environment("BDFU_TAPE_PASSPHRASE") is { Length: > 0 } fromEnvironment
                    ? fromEnvironment
                    : interactive ? Prompt($"Passphrase of key {encryption.Fingerprint}: ") : throw new InvalidOperationException(
                        $"The set is encrypted with key {encryption.Fingerprint}: give --key-file, --passphrase-file or BDFU_TAPE_PASSPHRASE.");
            output.WriteLine($"Deriving the key ({encryption.Kdf}, {encryption.MemoryKiB / 1024} MiB)...");
            kek = TapeKeys.DeriveKek(passphrase, TapeKeys.Parameters(encryption));
        }

        return TapeKeys.Fingerprint(kek) == encryption.Fingerprint
            ? kek
            : throw new System.Security.Cryptography.CryptographicException($"Wrong passphrase or key file: its fingerprint is {TapeKeys.Fingerprint(kek)}, the tape needs {encryption.Fingerprint}.");
    }

    private string Prompt(string text)
    {
        output.Write(text);
        if (input != Console.In)
        {
            return input.ReadLine() ?? string.Empty;
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return new string([.. chars]);
            }

            if (key.Key == ConsoleKey.Backspace && chars.Count > 0)
            {
                chars.RemoveAt(chars.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }

    /// <summary>The drive (and changer) for one command: loads tapes by barcode, or asks for them, and positions them.</summary>
    private sealed class Session(TapeRecovery owner, ScsiDeviceInfo info, IScsiDevice driveDevice, TapeDrive drive, IScsiDevice? changerDevice, MediumChanger? changer) : ITapeVolumes, IAsyncDisposable
    {
        private TapeCatalog? _catalog;
        private int? _sequence;

        public TapeDrive Drive => drive;

        public MediumChanger? Changer => changer;

        public int BlockSize { get; private set; } = 1024 * 1024;

        public ITapeRecordCipher? Cipher { get; private set; }

        /// <summary>Loads <paramref name="barcode"/> with the changer (the cartridge in the drive goes back first); without one, the drive's tape.</summary>
        public async Task LoadAsync(string? barcode, CancellationToken cancellationToken)
        {
            if (barcode is not null)
            {
                var changerUsed = changer ?? throw new InvalidOperationException("--barcode needs a changer; put the cartridge into the drive by hand.");
                var inventory = await changerUsed.ReadInventoryAsync(cancellationToken).ConfigureAwait(false);
                var element = inventory.Drives.FirstOrDefault(e => e.DeviceId is { } id && (info.Designators.Contains(id) || id.EndsWith(info.Serial!, StringComparison.Ordinal)))
                    ?? inventory.Drives.First();
                if (element.Full && element.VolumeTag?.Trim() == barcode)
                {
                    await drive.WaitReadyAsync(TimeSpan.FromHours(2.5), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (element.Full)
                {
                    await drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
                    var home = element.SourceValid ? element.SourceAddress : inventory.Slots.First(s => !s.Full).Address;
                    await changerUsed.MoveAsync(element.Address, home, cancellationToken).ConfigureAwait(false);
                }

                var source = inventory.Find(barcode) ?? throw new IOException($"{barcode} is not in the library.");
                owner.Output.WriteLine($"Loading {barcode}...");
                await changerUsed.MoveAsync(source.Address, element.Address, cancellationToken).ConfigureAwait(false);
            }

            await drive.WaitReadyAsync(TimeSpan.FromHours(2.5), cancellationToken).ConfigureAwait(false);
            await drive.PrepareAsync(cancellationToken).ConfigureAwait(false);
            _sequence = null;
        }

        public async Task<TapeLabel> LabelAsync(CancellationToken cancellationToken)
        {
            var read = await TapeLabelReader.ReadAsync(drive, cancellationToken).ConfigureAwait(false);
            var label = read.Label ?? throw new InvalidDataException(read.Blank ? "The tape is blank." : $"The tape is not a backdafuckup tape ({read.Reason}).");
            BlockSize = label.BlockSize;
            if (label.Encryption is { Mode: "software" } encryption && Cipher is null && label.Set is { } set)
            {
                Cipher = new AesGcmRecordCipher(TapeKeys.SetKey(owner.Kek(encryption), set.Id, TapeKeys.SoftwarePurpose), set.Id);
            }
            else if (label.Encryption is { Mode: "hardware" } hardware && label.Set is { } hardwareSet)
            {
                var capabilities = await drive.ReadEncryptionCapabilitiesAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new IOException("The set was encrypted by the drive, but this drive cannot decrypt.");
                await drive.SetEncryptionAsync(capabilities, TapeKeys.SetKey(owner.Kek(hardware), hardwareSet.Id, TapeKeys.HardwarePurpose), TapeKeys.KeyAssociatedData(hardware.Fingerprint, hardwareSet.Id), cancellationToken).ConfigureAwait(false);
            }

            return label;
        }

        /// <summary>The newest catalog of the set of the tape (loaded first with <paramref name="barcode"/>).</summary>
        public async Task<TapeCatalog> CatalogAsync(string? barcode, CancellationToken cancellationToken)
        {
            await LoadAsync(barcode, cancellationToken).ConfigureAwait(false);
            var label = await LabelAsync(cancellationToken).ConfigureAwait(false);
            if (label.Set is null)
            {
                throw new InvalidDataException($"{label.Name} belongs to no set.");
            }

            var files = await TapeSetReader.ScanAsync(drive, BlockSize, cancellationToken).ConfigureAwait(false);
            _catalog = await TapeSetReader.ReadCatalogAsync(drive, files, BlockSize, Cipher, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"{label.Name} (tape #{label.Set.Sequence} of its set) has no complete catalog; read the set's highest tape.");
            _sequence = label.Set.Sequence;
            return _catalog;
        }

        public async Task<DriveBlocks> AtAsync(int sequence, long block, CancellationToken cancellationToken)
        {
            if (_sequence != sequence)
            {
                var name = _catalog?.Media.FirstOrDefault(m => m.Sequence == sequence) is { } medium ? medium.Barcode ?? medium.Name : $"#{sequence}";
                if (changer is not null && _catalog?.Media.FirstOrDefault(m => m.Sequence == sequence)?.Barcode is { } barcode)
                {
                    await LoadAsync(barcode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await drive.UnloadAsync(cancellationToken).ConfigureAwait(false);
                    owner.Output.Write($"Insert tape {name} (#{sequence} of the set) into the drive and press Enter: ");
                    owner.Input.ReadLine();
                    await LoadAsync(null, cancellationToken).ConfigureAwait(false);
                }

                var label = await LabelAsync(cancellationToken).ConfigureAwait(false);
                if (label.Set?.Sequence != sequence || label.Set.Id != _catalog?.SetId)
                {
                    throw new InvalidDataException($"The tape in the drive is {label.Name}, not tape #{sequence} of the set.");
                }

                _sequence = sequence;
            }

            await drive.LocateBlockAsync(block, cancellationToken).ConfigureAwait(false);
            return new DriveBlocks(drive);
        }

        public async ValueTask DisposeAsync()
        {
            (Cipher as IDisposable)?.Dispose();
            await driveDevice.DisposeAsync().ConfigureAwait(false);
            if (changerDevice is not null)
            {
                await changerDevice.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
