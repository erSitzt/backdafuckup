using System.Buffers.Binary;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Devices;

/// <summary>Identity of a drive or changer: what INQUIRY and the VPD pages report.</summary>
/// <param name="Vendor">INQUIRY vendor, e.g. "HPE".</param>
/// <param name="Product">INQUIRY product, e.g. "Ultrium 9-SCSI".</param>
/// <param name="Revision">Firmware revision.</param>
/// <param name="Serial">Unit serial number (VPD 80h).</param>
/// <param name="Designators">Device identification designators (VPD 83h), as text.</param>
public sealed record DeviceIdentity(byte PeripheralType, string Vendor, string Product, string Revision, string Serial, IReadOnlyList<string> Designators)
{
    public override string ToString() => $"{Vendor} {Product} (serial {Serial})";
}

public enum DriveReadiness
{
    Ready,
    NoMedium,
    BecomingReady,
    NeedsLoad,
    CleaningCartridge,
    Other,
}

/// <summary>The cartridge in a drive as the drive reports it.</summary>
/// <param name="WriteProtected">Write protect switch or a read-only generation.</param>
/// <param name="Worm">A WORM cartridge (medium configuration page, or MAM medium type 80h).</param>
/// <param name="Cleaning">A cleaning cartridge (MAM medium type 01h).</param>
/// <param name="Barcode">MAM barcode attribute (0806h), when written.</param>
/// <param name="MediumSerial">MAM medium serial number (0401h).</param>
/// <param name="CapacityBytes">MAM maximum capacity (0001h), when known.</param>
/// <param name="RemainingBytes">MAM remaining capacity (0000h), when known.</param>
public sealed record MediumInfo(bool WriteProtected, bool Worm, bool Cleaning, byte? DensityCode, string? Barcode, string? MediumSerial, long? CapacityBytes, long? RemainingBytes, IReadOnlyDictionary<ushort, byte[]> Attributes);

/// <summary>What a block write or filemark write reported.</summary>
/// <param name="EarlyWarning">The tape is past early warning: finish the current file, write the catalog, change tapes.</param>
public readonly record struct WriteOutcome(bool EarlyWarning);

public enum ReadOutcomeKind
{
    Block,
    Filemark,
    EndOfData,
}

/// <param name="Length">Bytes of the block read.</param>
public readonly record struct ReadOutcome(ReadOutcomeKind Kind, int Length);

/// <summary>Capabilities of the drive's data encryption (SECURITY PROTOCOL IN 20h, page 0010h).</summary>
/// <param name="AlgorithmIndex">Index of AES-256-GCM, to name it in SECURITY PROTOCOL OUT.</param>
public sealed record EncryptionCapabilities(byte AlgorithmIndex, int KeyLength, int MaxUkadLength);

/// <summary>The cartridge is past its end: nothing more fits (VOLUME OVERFLOW).</summary>
public sealed class TapeFullException(string message) : IOException(message);

/// <summary>The cartridge refuses the write: write protected, WORM, or past end of data on WORM.</summary>
public sealed class TapeWriteProtectedException(string message) : IOException(message);

/// <summary>
/// A tape drive (SSC): readiness, position, variable-length blocks, filemarks, the cartridge's medium auxiliary memory
/// and data encryption. It guards WORM cartridges before a command reaches the drive.
/// </summary>
public sealed class TapeDrive
{
    private readonly TimeProvider _time;

    /// <summary>Commands posted ahead (writes not yet answered, or reads ahead), oldest first.</summary>
    private readonly Queue<PostedCommand> _posted = new();

    /// <summary>The logical position (block number) the tape should be at; null when unknown.</summary>
    private long? _expectedBlock;

    private long _postedBytes;
    private long _chain;

    /// <summary>The last command was a WRITE that ended GOOD: the next may be posted.</summary>
    private bool _streaming;

    /// <summary>A posted write reported early warning.</summary>
    private bool _earlyWarning;

    public TapeDrive(ScsiCommandRunner runner, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Runner = runner;
        runner.BeforeCommand = DrainAsync;
    }

    public ScsiCommandRunner Runner { get; }

    /// <summary>
    /// How much a drive whose device takes posted commands (a tape agent) keeps in flight: writes not yet answered, and
    /// reads ahead. 0 runs every command on its own.
    /// </summary>
    public int PipelineBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Commands posted ahead so far.</summary>
    public long PostedCommands { get; private set; }

    private IPipelinedScsiDevice? Posting => PipelineBytes > 0 && Runner.Device is IPipelinedScsiDevice { CanPost: true } device ? device : null;

    /// <summary>How often <see cref="WaitReadyAsync"/> asks the drive.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public string Label => Runner.Label;

    /// <summary>The cartridge as last read by <see cref="ReadMediumAsync"/>; cleared on load and unload.</summary>
    public MediumInfo? Medium { get; private set; }

    public async Task<DeviceIdentity> IdentifyAsync(CancellationToken cancellationToken)
    {
        var identity = await Identify(Runner, cancellationToken).ConfigureAwait(false);
        try
        {
            var timeouts = await Runner.ReadAsync("REPORT SUPPORTED OPERATION CODES", Cdb.ReportSupportedOperationCodes(), 65536, cancellationToken).ConfigureAwait(false);
            Runner.UseTimeouts(CommandTimeouts.Parse(timeouts));
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
            // Not supported: the default timeouts apply.
        }

        return identity;
    }

    internal static async Task<DeviceIdentity> Identify(ScsiCommandRunner runner, CancellationToken cancellationToken)
    {
        // At most 255 bytes: devices that implement the SCSI-2 INQUIRY read only the low byte of the allocation length.
        var inquiry = InquiryData.Parse(await runner.ReadAsync("INQUIRY", Cdb.Inquiry(), 96, cancellationToken).ConfigureAwait(false));
        var serial = VpdPages.ParseSerial(await runner.ReadAsync("INQUIRY (unit serial number)", Cdb.Inquiry(255, 0x80), 255, cancellationToken).ConfigureAwait(false));
        IReadOnlyList<string> designators = [];
        try
        {
            designators = VpdPages.ParseDesignators(await runner.ReadAsync("INQUIRY (device identification)", Cdb.Inquiry(255, 0x83), 255, cancellationToken).ConfigureAwait(false));
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
        }

        return new DeviceIdentity(inquiry.PeripheralType, inquiry.Vendor, inquiry.Product, inquiry.Revision, serial, designators);
    }

    public async Task<DriveReadiness> TestReadyAsync(CancellationToken cancellationToken)
    {
        var result = await Runner.RunAsync(new ScsiCommand { Name = "TEST UNIT READY", Cdb = Cdb.TestUnitReady() }, cancellationToken).ConfigureAwait(false);
        if (result.IsGood)
        {
            return DriveReadiness.Ready;
        }

        return result.Sense switch
        {
            { Key: SenseKey.NotReady, Asc: 0x3A } => DriveReadiness.NoMedium,
            { Key: SenseKey.NotReady, Asc: 0x04, Ascq: 0x01 } => DriveReadiness.BecomingReady,
            { Key: SenseKey.NotReady, Asc: 0x04, Ascq: 0x02 } => DriveReadiness.NeedsLoad,
            { Key: SenseKey.NotReady, Asc: 0x30, Ascq: 0x03 } => DriveReadiness.CleaningCartridge,
            { Key: SenseKey.UnitAttention } => DriveReadiness.BecomingReady,
            _ => DriveReadiness.Other,
        };
    }

    /// <summary>
    /// Waits until a cartridge is ready to use: polls TEST UNIT READY, and sends LOAD when the drive asks for it. A new
    /// LTO-9 cartridge is calibrated on its first load, which can take up to two hours.
    /// </summary>
    public async Task WaitReadyAsync(TimeSpan max, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + max;
        var loadSent = false;
        while (true)
        {
            var state = await TestReadyAsync(cancellationToken).ConfigureAwait(false);
            switch (state)
            {
                case DriveReadiness.Ready:
                    _expectedBlock = null;
                    return;
                case DriveReadiness.NoMedium:
                    throw new InvalidOperationException($"{Label} has no cartridge.");
                case DriveReadiness.CleaningCartridge:
                    throw new InvalidOperationException($"{Label} holds a cleaning cartridge.");
                case DriveReadiness.NeedsLoad when !loadSent:
                    await MoveAsync("LOAD", Cdb.LoadUnload(load: true, immediate: true), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
                    loadSent = true;
                    break;
                case DriveReadiness.Other:
                    var result = await Runner.RunAsync(new ScsiCommand { Name = "TEST UNIT READY", Cdb = Cdb.TestUnitReady() }, cancellationToken).ConfigureAwait(false);
                    throw new IOException($"{Label} is not ready: {result.Sense?.ToString() ?? ScsiStatus.Describe(result.Status)}");
            }

            if (_time.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"{Label} did not become ready within {max.TotalMinutes:0} minutes.");
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BlockLimits> ReadBlockLimitsAsync(CancellationToken cancellationToken) =>
        BlockLimits.Parse(await Runner.ReadAsync("READ BLOCK LIMITS", Cdb.ReadBlockLimits(), 6, cancellationToken).ConfigureAwait(false));

    /// <summary>Reads what the drive knows about the loaded cartridge: write protection, WORM, MAM attributes.</summary>
    public async Task<MediumInfo> ReadMediumAsync(CancellationToken cancellationToken)
    {
        var header = ModeParameters.Parse10(await Runner.ReadAsync("MODE SENSE", Cdb.ModeSense10(ModePages.DataCompression), 512, cancellationToken).ConfigureAwait(false));
        var worm = false;
        try
        {
            var configuration = ModeParameters.Parse10(await Runner.ReadAsync("MODE SENSE (medium configuration)", Cdb.ModeSense10(ModePages.MediumConfigurationOrElementAddresses), 512, cancellationToken).ConfigureAwait(false));
            worm = configuration.Pages.TryGetValue(ModePages.MediumConfigurationOrElementAddresses, out var page) && ModePages.WormMedium(page);
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
        }

        IReadOnlyDictionary<ushort, byte[]> attributes = new Dictionary<ushort, byte[]>();
        try
        {
            attributes = MamAttributes.Parse(await Runner.ReadAsync("READ ATTRIBUTE", Cdb.ReadAttribute(), 8192, cancellationToken).ConfigureAwait(false));
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key is SenseKey.IllegalRequest or SenseKey.MediumError)
        {
            // No medium auxiliary memory (or not readable): the rest still works.
        }

        var type = attributes.TryGetValue(MamAttributes.MediumType, out var t) && t.Length > 0 ? t[0] : (byte?)null;
        var info = new MediumInfo(
            header.WriteProtected,
            worm || type == MamAttributes.MediumTypeWorm,
            type == MamAttributes.MediumTypeCleaning,
            header.DensityCode,
            MamAttributes.Text(attributes, MamAttributes.Barcode),
            MamAttributes.Text(attributes, MamAttributes.MediumSerialNumber),
            MamAttributes.Number(attributes, MamAttributes.MaximumCapacity) * 1024 * 1024,
            MamAttributes.Number(attributes, MamAttributes.RemainingCapacity) * 1024 * 1024,
            attributes);
        Medium = info;
        return info;
    }

    /// <summary>Variable-block mode, drive compression off (the data is compressed already), synchronize at early warning.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var compression = ModeParameters.Parse10(await Runner.ReadAsync("MODE SENSE (data compression)", Cdb.ModeSense10(ModePages.DataCompression), 512, cancellationToken).ConfigureAwait(false));
        var device = ModeParameters.Parse10(await Runner.ReadAsync("MODE SENSE (device configuration)", Cdb.ModeSense10(ModePages.DeviceConfiguration), 512, cancellationToken).ConfigureAwait(false));
        var pages = new List<byte[]>();
        if (compression.Pages.TryGetValue(ModePages.DataCompression, out var compressionPage))
        {
            pages.Add(ModePages.WithCompression(compressionPage, enabled: false));
        }

        if (device.Pages.TryGetValue(ModePages.DeviceConfiguration, out var devicePage))
        {
            pages.Add(ModePages.WithSynchronizeAtEarlyWarning(devicePage, enabled: true));
        }

        var list = ModeParameters.BuildSelect10(blockLength: 0, density: compression.DensityCode ?? 0, [.. pages]);
        await Runner.SendAsync("MODE SELECT", Cdb.ModeSelect10((ushort)list.Length), cancellationToken, list).ConfigureAwait(false);
    }

    public async Task<TapePosition> ReadPositionAsync(CancellationToken cancellationToken)
    {
        TapePosition position;
        try
        {
            position = TapePosition.ParseLong(await Runner.ReadAsync("READ POSITION", Cdb.ReadPosition(longForm: true), 32, cancellationToken).ConfigureAwait(false));
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
            position = TapePosition.ParseShort(await Runner.ReadAsync("READ POSITION", Cdb.ReadPosition(longForm: false), 20, cancellationToken).ConfigureAwait(false));
        }

        _expectedBlock = position.Block;
        return position;
    }

    public async Task RewindAsync(CancellationToken cancellationToken)
    {
        await MoveAsync("REWIND", Cdb.Rewind(immediate: false), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
        _expectedBlock = 0;
    }

    /// <summary>Rewinds and ejects the cartridge, so that the changer can take it.</summary>
    public async Task UnloadAsync(CancellationToken cancellationToken)
    {
        await Runner.SendAsync("PREVENT ALLOW MEDIUM REMOVAL", Cdb.PreventAllowMediumRemoval(prevent: false), cancellationToken).ConfigureAwait(false);
        await MoveAsync("UNLOAD", Cdb.LoadUnload(load: false, immediate: false), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
        Medium = null;
        _expectedBlock = null;
    }

    public Task PreventRemovalAsync(bool prevent, CancellationToken cancellationToken) =>
        Runner.SendAsync("PREVENT ALLOW MEDIUM REMOVAL", Cdb.PreventAllowMediumRemoval(prevent), cancellationToken);

    public async Task LocateBlockAsync(long block, CancellationToken cancellationToken)
    {
        await MoveAsync("LOCATE", Cdb.Locate16(block), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
        _expectedBlock = block;
    }

    /// <summary>Positions at the first block of file <paramref name="file"/> (file 0 starts at the beginning of the tape).</summary>
    public async Task LocateFileAsync(long file, CancellationToken cancellationToken)
    {
        await MoveAsync("LOCATE", Cdb.Locate16(file, toFile: true), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
        _expectedBlock = null;
    }

    public async Task SpaceToEndOfDataAsync(CancellationToken cancellationToken)
    {
        await MoveAsync("SPACE", Cdb.Space16(Cdb.SpaceCode.EndOfData, 0), ScsiTimeouts.Positioning, positionMatters: false, cancellationToken).ConfigureAwait(false);
        _expectedBlock = null;
    }

    /// <summary>Spaces over <paramref name="count"/> filemarks (negative: backwards, ending before the filemark).</summary>
    public async Task SpaceFilemarksAsync(long count, CancellationToken cancellationToken)
    {
        var start = _expectedBlock;
        _expectedBlock = null;
        await MoveAsync("SPACE", Cdb.Space16(Cdb.SpaceCode.Filemarks, count), ScsiTimeouts.Positioning, positionMatters: true, cancellationToken, start).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one block. Early warning is reported, not thrown; VOLUME OVERFLOW throws <see cref="TapeFullException"/>.
    /// Through a device that takes posted commands, writes after the first are posted: their answers (early warning, a
    /// full tape, an error) come with a later write or before the next other command, such as the filemark.
    /// </summary>
    public async Task<WriteOutcome> WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly("write");
        if (_streaming && Posting is { } posting)
        {
            return await PostWriteAsync(posting, block, cancellationToken).ConfigureAwait(false);
        }

        var result = await RunMovingAsync(WriteCommand(System.Runtime.InteropServices.MemoryMarshal.AsMemory(block)), positionMatters: true, cancellationToken).ConfigureAwait(false);
        if (result.Sense is { Key: SenseKey.IllegalRequest, Asc: 0x24, Ascq: 0x00 } && block.Length > 256 * 1024)
        {
            throw new IOException($"{Label} refuses blocks of {block.Length / 1024} KiB ({result.Sense}), although it reports larger block limits; the drive or its connection (e.g. an iSCSI target) takes less per command. Use a block size of 256 KiB for this library.");
        }

        var outcome = WriteResult("WRITE", result);
        Advance(1);
        _streaming = result.IsGood;
        return outcome;
    }

    private static ScsiCommand WriteCommand(Memory<byte> block) => new()
    {
        Name = "WRITE",
        Cdb = Cdb.Write6Variable(block.Length),
        Direction = ScsiDataDirection.Out,
        Buffer = block,
        Timeout = ScsiTimeouts.ReadWrite,
        MovesMedium = true,
    };

    private static ScsiCommand ReadCommand(Memory<byte> destination) => new()
    {
        Name = "READ",
        Cdb = Cdb.Read6Variable(destination.Length),
        Direction = ScsiDataDirection.In,
        Buffer = destination,
        Timeout = ScsiTimeouts.ReadWrite,
        MovesMedium = true,
    };

    /// <summary>Writes filemarks; with <paramref name="immediate"/> off the drive's buffer is on the medium afterwards.</summary>
    public async Task<WriteOutcome> WriteFilemarksAsync(int count, bool immediate, CancellationToken cancellationToken)
    {
        ThrowIfReadOnly("write a filemark on");
        var result = await RunMovingAsync(
            new ScsiCommand { Name = "WRITE FILEMARKS", Cdb = Cdb.WriteFilemarks(count, immediate), Timeout = ScsiTimeouts.ReadWrite, MovesMedium = true },
            positionMatters: true,
            cancellationToken).ConfigureAwait(false);
        var outcome = WriteResult("WRITE FILEMARKS", result);
        Advance(count);
        return outcome;
    }

    /// <summary>
    /// Reads one variable-length block into <paramref name="destination"/>. With <paramref name="readAhead"/>, through a
    /// device that takes posted commands, reads of the same size are posted ahead; they stop at a filemark, the end of
    /// data or an error. The reads ahead not taken are dropped before the next other command, and the position is then
    /// unknown: read ahead only where the next move is a locate to a block.
    /// </summary>
    public async Task<ReadOutcome> ReadBlockAsync(Memory<byte> destination, CancellationToken cancellationToken, bool readAhead = false)
    {
        if (readAhead && Posting is { } posting)
        {
            return await ReadPostedAsync(posting, destination, cancellationToken).ConfigureAwait(false);
        }

        var command = ReadCommand(destination);
        var result = await RunMovingAsync(command, positionMatters: true, cancellationToken).ConfigureAwait(false);
        return ReadResult(command, result, destination.Length);
    }

    private ReadOutcome ReadResult(ScsiCommand command, ScsiResult result, int length)
    {
        if (result.IsGood)
        {
            Advance(1);
            return new ReadOutcome(ReadOutcomeKind.Block, result.Transferred(command));
        }

        switch (result.Sense)
        {
            case { Key: SenseKey.BlankCheck }:
                return new ReadOutcome(ReadOutcomeKind.EndOfData, 0);
            case { Filemark: true } or { Key: SenseKey.NoSense, Asc: 0x00, Ascq: 0x01 }:
                Advance(1);
                return new ReadOutcome(ReadOutcomeKind.Filemark, 0);
            case { Key: SenseKey.NoSense or SenseKey.RecoveredError, IncorrectLength: true, Information: < 0 } longer:
                _expectedBlock = null;
                throw new InvalidDataException($"{Label}: the block on tape is {-longer.Information} bytes longer than the {length} bytes read; the tape was written with larger blocks than this host can read.");
            case { Key: SenseKey.NoSense or SenseKey.RecoveredError }:
                Advance(1);
                return new ReadOutcome(ReadOutcomeKind.Block, result.Transferred(command));
            default:
                _expectedBlock = null;
                throw new ScsiCommandException(Label, command, result);
        }
    }

    /// <summary>Short erase at the beginning of the tape: the cartridge counts as blank. Refused for WORM cartridges.</summary>
    public async Task EraseAsync(CancellationToken cancellationToken)
    {
        if (Medium is null)
        {
            await ReadMediumAsync(cancellationToken).ConfigureAwait(false);
        }

        ThrowIfReadOnly("erase");
        await RewindAsync(cancellationToken).ConfigureAwait(false);
        await MoveAsync("ERASE", Cdb.EraseShort(immediate: false), ScsiTimeouts.Positioning, positionMatters: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes host attributes into the cartridge's medium auxiliary memory.</summary>
    public async Task WriteAttributesAsync(IEnumerable<(ushort Id, byte Format, byte[] Value)> attributes, CancellationToken cancellationToken)
    {
        var list = MamAttributes.Build(attributes);
        await Runner.SendAsync("WRITE ATTRIBUTE", Cdb.WriteAttribute((uint)list.Length), cancellationToken, list, sensitive: true).ConfigureAwait(false);
    }

    /// <summary>The TapeAlert flags that are set now (an empty list when the drive does not report any).</summary>
    public async Task<IReadOnlyList<int>> ReadTapeAlertsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return LogPages.TapeAlertFlags(await Runner.ReadAsync("LOG SENSE (TapeAlert)", Cdb.LogSense(LogPages.TapeAlert), 4096, cancellationToken).ConfigureAwait(false));
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
            return [];
        }
    }

    // ---- Data encryption (application-managed, SSC-4 8.5) ----

    private const byte TapeDataEncryption = 0x20;
    private const uint Aes256Gcm = 0x00010014;

    /// <summary>The drive's AES-256-GCM capability, or null when it has none (or no SECURITY PROTOCOL at all).</summary>
    public async Task<EncryptionCapabilities?> ReadEncryptionCapabilitiesAsync(CancellationToken cancellationToken)
    {
        byte[] page;
        try
        {
            page = await Runner.ReadAsync("SECURITY PROTOCOL IN (encryption capabilities)", Cdb.SecurityProtocolIn(TapeDataEncryption, 0x0010, 8192), 8192, cancellationToken).ConfigureAwait(false);
        }
        catch (ScsiCommandException ex) when (ex.Sense?.Key == SenseKey.IllegalRequest)
        {
            return null;
        }

        if (page.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(page) != 0x0010)
        {
            return null;
        }

        // Data encryption algorithm descriptors (SSC-4 8.5.4.4) from byte 20 on: index (0), descriptor length (2-3),
        // ENCRYPT_C (byte 4 bits 1-0), maximum U-KAD length (6-7), key size (10-11), algorithm code (20-23).
        var end = Math.Min(page.Length, 4 + BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(2)));
        for (var offset = 20; offset + 4 <= end;)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(offset + 2));
            if (offset + 4 + length > end || length < 20)
            {
                break;
            }

            var descriptor = page.AsSpan(offset, 4 + length);
            var canEncrypt = (descriptor[4] & 0x03) != 0;
            if (canEncrypt && BinaryPrimitives.ReadUInt32BigEndian(descriptor[20..]) == Aes256Gcm)
            {
                return new EncryptionCapabilities(descriptor[0], BinaryPrimitives.ReadUInt16BigEndian(descriptor[10..]), BinaryPrimitives.ReadUInt16BigEndian(descriptor[6..]));
            }

            offset += 4 + length;
        }

        return null;
    }

    /// <summary>
    /// Sets an AES-256-GCM key for writing and reading (mixed mode: unencrypted blocks, e.g. the label, stay readable),
    /// with <paramref name="keyId"/> as unauthenticated key-associated data on every block. The key is never logged.
    /// </summary>
    public async Task SetEncryptionAsync(EncryptionCapabilities capabilities, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> keyId, CancellationToken cancellationToken)
    {
        if (key.Length != capabilities.KeyLength)
        {
            throw new ArgumentException($"{Label} expects a {capabilities.KeyLength}-byte key.", nameof(key));
        }

        if (keyId.Length > capabilities.MaxUkadLength)
        {
            throw new ArgumentException($"{Label} takes at most {capabilities.MaxUkadLength} bytes of key-associated data.", nameof(keyId));
        }

        var list = BuildSetDataEncryption(capabilities.AlgorithmIndex, encrypt: true, key.Span, keyId.Span);
        try
        {
            await Runner.SendAsync("SECURITY PROTOCOL OUT (set data encryption)", Cdb.SecurityProtocolOut(TapeDataEncryption, 0x0010, (uint)list.Length), cancellationToken, list, sensitive: true).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(list);
        }
    }

    /// <summary>Turns encryption off for writing and reading.</summary>
    public async Task ClearEncryptionAsync(CancellationToken cancellationToken)
    {
        var list = BuildSetDataEncryption(0, encrypt: false, [], []);
        await Runner.SendAsync("SECURITY PROTOCOL OUT (clear data encryption)", Cdb.SecurityProtocolOut(TapeDataEncryption, 0x0010, (uint)list.Length), cancellationToken, list).ConfigureAwait(false);
    }

    internal static byte[] BuildSetDataEncryption(byte algorithmIndex, bool encrypt, ReadOnlySpan<byte> key, ReadOnlySpan<byte> keyId)
    {
        var kadLength = keyId.IsEmpty ? 0 : 4 + keyId.Length;
        var length = 20 + key.Length + kadLength;
        var list = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(list, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(list.AsSpan(2), (ushort)(length - 4));
        list[4] = 0x20; // scope 001b: local (this I_T nexus only), not locked
        list[5] = 0x04; // CKOD: clear the key when the cartridge is unloaded
        list[6] = (byte)(encrypt ? 0x02 : 0x00); // encryption mode: encrypt
        list[7] = (byte)(encrypt ? 0x03 : 0x00); // decryption mode: mixed
        list[8] = algorithmIndex;
        list[9] = 0x00; // key format: plain text key
        BinaryPrimitives.WriteUInt16BigEndian(list.AsSpan(18), (ushort)key.Length);
        key.CopyTo(list.AsSpan(20));
        if (kadLength > 0)
        {
            var kad = list.AsSpan(20 + key.Length);
            kad[0] = 0x00; // U-KAD
            BinaryPrimitives.WriteUInt16BigEndian(kad[2..], (ushort)keyId.Length);
            keyId.CopyTo(kad[4..]);
        }

        return list;
    }

    /// <summary>
    /// Runs a command that moves or writes the tape. A command that meets UNIT ATTENTION was not performed (SAM): it is
    /// repeated once, but only after the drive still holds the same cartridge and, where it matters, the tape is still
    /// where it was. Otherwise it fails and names what changed.
    /// </summary>
    private async Task<ScsiResult> RunMovingAsync(ScsiCommand command, bool positionMatters, CancellationToken cancellationToken, long? expectedBlock = null)
    {
        expectedBlock ??= _expectedBlock;
        var result = await Runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (!(result.IsCheckCondition && result.Sense is { Key: SenseKey.UnitAttention } attention))
        {
            return result;
        }

        if (Medium?.MediumSerial is { } serial)
        {
            var attributes = MamAttributes.Parse(await Runner.ReadAsync("READ ATTRIBUTE", Cdb.ReadAttribute(MamAttributes.MediumSerialNumber), 4096, cancellationToken).ConfigureAwait(false));
            var now = MamAttributes.Text(attributes, MamAttributes.MediumSerialNumber);
            if (!string.Equals(now, serial, StringComparison.Ordinal))
            {
                Medium = null;
                throw new IOException($"{Label}: {command.Name} met {attention}, and the drive now holds cartridge {now ?? "unknown"} instead of {serial}; nothing more is done with it.");
            }
        }

        if (positionMatters && expectedBlock is { } expected)
        {
            var position = await ReadPositionAsync(cancellationToken).ConfigureAwait(false);
            if (position.Block != expected)
            {
                throw new IOException($"{Label}: {command.Name} met {attention}, and the tape is at block {position.Block} instead of {expected}; the command is not repeated.");
            }
        }
        else if (positionMatters)
        {
            throw new IOException($"{Label}: {command.Name} met {attention} at an unknown position; the command is not repeated.");
        }

        return await Runner.RunAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async Task MoveAsync(string name, byte[] cdb, TimeSpan timeout, bool positionMatters, CancellationToken cancellationToken, long? expectedBlock = null)
    {
        var command = new ScsiCommand { Name = name, Cdb = cdb, Timeout = timeout, MovesMedium = true };
        var result = await RunMovingAsync(command, positionMatters, cancellationToken, expectedBlock).ConfigureAwait(false);
        if (!result.IsGood && !(result.IsCheckCondition && result.Sense is { Key: SenseKey.NoSense or SenseKey.RecoveredError, Filemark: false, EndOfMedium: false }))
        {
            _expectedBlock = null;
            throw new ScsiCommandException(Label, command, result);
        }
    }

    /// <summary>A write or read posted ahead, with its own copy of the data (a write may have to be posted again).</summary>
    private sealed record PostedCommand(ScsiCommand Command, Task<ScsiResult> Result, byte[] Buffer, int Length, bool Write);

    private async Task PostAsync(IPipelinedScsiDevice device, ScsiCommand command, byte[] buffer, int length, bool write, CancellationToken cancellationToken)
    {
        var result = await device.PostAsync(command, _chain, cancellationToken).ConfigureAwait(false);
        _posted.Enqueue(new PostedCommand(command, result, buffer, length, write));
        _postedBytes += length;
        PostedCommands++;
    }

    private async Task<WriteOutcome> PostWriteAsync(IPipelinedScsiDevice device, ReadOnlyMemory<byte> block, CancellationToken cancellationToken)
    {
        if (_posted.Count > 0 && !_posted.Peek().Write)
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(block.Length);
        block.CopyTo(buffer);
        try
        {
            await PostAsync(device, WriteCommand(buffer.AsMemory(0, block.Length)), buffer, block.Length, write: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        Advance(1);
        while (_postedBytes > PipelineBytes)
        {
            await CompleteOldestWriteAsync(device, cancellationToken).ConfigureAwait(false);
        }

        return new WriteOutcome(_earlyWarning);
    }

    /// <summary>
    /// The oldest posted write's answer. Early warning is noted; the writes after it were skipped (its chain broke) and are
    /// posted again in a new chain. A full tape or an error throws; the writes after it are dropped.
    /// </summary>
    private async Task CompleteOldestWriteAsync(IPipelinedScsiDevice device, CancellationToken cancellationToken)
    {
        var oldest = _posted.Dequeue();
        _postedBytes -= oldest.Length;
        try
        {
            ScsiResult result;
            try
            {
                result = await oldest.Result.ConfigureAwait(false);
            }
            catch
            {
                await DropPostedAsync().ConfigureAwait(false);
                _expectedBlock = null;
                throw;
            }

            if (result.IsGood)
            {
                return;
            }

            // Its chain broke here: the writes after it did not run.
            var rest = _posted.ToList();
            _posted.Clear();
            _postedBytes = 0;
            _chain++;
            foreach (var skipped in rest)
            {
                try
                {
                    await skipped.Result.ConfigureAwait(false);
                    _expectedBlock = null;
                    rest.ForEach(r => System.Buffers.ArrayPool<byte>.Shared.Return(r.Buffer));
                    throw new IOException($"{Label}: a write ran after the write before it ended {result.Sense?.ToString() ?? ScsiStatus.Describe(result.Status)}; the blocks on tape are not in order.");
                }
                catch (ScsiChainSkippedException)
                {
                    // As it should be.
                }
            }

            WriteOutcome outcome;
            try
            {
                outcome = WriteResult("WRITE", result);
            }
            catch
            {
                _expectedBlock = null;
                rest.ForEach(r => System.Buffers.ArrayPool<byte>.Shared.Return(r.Buffer));
                throw;
            }

            _earlyWarning |= outcome.EarlyWarning;
            foreach (var again in rest)
            {
                await PostAsync(device, again.Command, again.Buffer, again.Length, write: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(oldest.Buffer);
        }
    }

    private async Task<ReadOutcome> ReadPostedAsync(IPipelinedScsiDevice device, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_posted.Count > 0 && (_posted.Peek().Write || _posted.Peek().Length != destination.Length))
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }

        var size = destination.Length;
        while (_posted.Count == 0 || _postedBytes + size <= PipelineBytes)
        {
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
            await PostAsync(device, ReadCommand(buffer.AsMemory(0, size)), buffer, size, write: false, cancellationToken).ConfigureAwait(false);
        }

        var oldest = _posted.Dequeue();
        _postedBytes -= oldest.Length;
        try
        {
            ScsiResult result;
            try
            {
                result = await oldest.Result.ConfigureAwait(false);
            }
            catch
            {
                await DropPostedAsync().ConfigureAwait(false);
                _expectedBlock = null;
                throw;
            }

            if (!result.IsGood)
            {
                // A filemark, the end of data or an error: the reads after it did not run (its chain broke).
                await DropPostedAsync().ConfigureAwait(false);
            }

            var outcome = ReadResult(oldest.Command, result, size);
            oldest.Buffer.AsSpan(0, outcome.Length).CopyTo(destination.Span);
            return outcome;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(oldest.Buffer);
        }
    }

    /// <summary>
    /// Before any other command: posted writes must have ended well (early warning, a full tape or an error is reported
    /// here); reads ahead not taken are dropped, and the tape is then past the block expected.
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        _streaming = false;
        if (_posted.Count > 0 && _posted.Peek().Write && Runner.Device is IPipelinedScsiDevice device)
        {
            while (_posted.Count > 0)
            {
                await CompleteOldestWriteAsync(device, cancellationToken).ConfigureAwait(false);
            }
        }

        if (await DropPostedAsync().ConfigureAwait(false))
        {
            _expectedBlock = null;
        }

        _earlyWarning = false;
    }

    /// <summary>Drops what is posted; true when some of it ran (the tape moved past what was taken).</summary>
    private async Task<bool> DropPostedAsync()
    {
        var ran = false;
        while (_posted.TryDequeue(out var posted))
        {
            try
            {
                await posted.Result.ConfigureAwait(false);
                ran = true;
            }
            catch (ScsiChainSkippedException)
            {
            }
            catch (Exception)
            {
                ran = true;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(posted.Buffer);
            }
        }

        _postedBytes = 0;
        _chain++;
        return ran;
    }

    private void Advance(int objects)
    {
        if (_expectedBlock is { } block)
        {
            _expectedBlock = block + objects;
        }
    }

    private WriteOutcome WriteResult(string name, ScsiResult result)
    {
        if (result.IsGood)
        {
            return new WriteOutcome(false);
        }

        switch (result.Sense)
        {
            case { Key: SenseKey.VolumeOverflow }:
                _expectedBlock = null;
                throw new TapeFullException($"{Label}: the cartridge is full ({result.Sense}); the data of this write is not on it.");
            case { Key: SenseKey.DataProtect, Asc: 0x30, Ascq: 0x0C }:
                throw new TapeWriteProtectedException($"{Label}: the cartridge is WORM and refuses to overwrite data ({result.Sense}).");
            case { Key: SenseKey.DataProtect, Asc: 0x27 }:
                throw new TapeWriteProtectedException($"{Label}: the cartridge is write protected ({result.Sense}).");
            case { Key: SenseKey.NoSense or SenseKey.RecoveredError, EndOfMedium: true } or { Key: SenseKey.NoSense, Asc: 0x00, Ascq: 0x02 or 0x07 }:
                return new WriteOutcome(true);
            case { Key: SenseKey.NoSense or SenseKey.RecoveredError }:
                return new WriteOutcome(false);
            default:
                throw new IOException($"{name} on {Label} failed: {result.Sense?.ToString() ?? ScsiStatus.Describe(result.Status)}");
        }
    }

    private void ThrowIfReadOnly(string what)
    {
        if (Medium is { Worm: true } && what == "erase")
        {
            throw new TapeWriteProtectedException($"{Label}: the cartridge {Medium.Barcode ?? "in the drive"} is WORM; it is never erased.");
        }

        if (Medium is { WriteProtected: true })
        {
            throw new TapeWriteProtectedException($"{Label}: the cartridge {Medium.Barcode ?? "in the drive"} is write protected; nothing was sent to the drive.");
        }

        if (Medium is { Cleaning: true })
        {
            throw new InvalidOperationException($"{Label} holds a cleaning cartridge.");
        }
    }
}
