using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Tape.Devices;

/// <summary>What a changer holds: its drives, slots and mail slots with their cartridges.</summary>
public sealed record ChangerInventory(ElementAddresses Addresses, IReadOnlyList<ChangerElement> Drives, IReadOnlyList<ChangerElement> Slots, IReadOnlyList<ChangerElement> MailSlots)
{
    public IEnumerable<ChangerElement> All => Drives.Concat(Slots).Concat(MailSlots);

    public ChangerElement? Find(string barcode) => All.FirstOrDefault(e => e.Full && string.Equals(e.VolumeTag, barcode, StringComparison.Ordinal));

    public ChangerElement? At(ushort address) => All.FirstOrDefault(e => e.Address == address);
}

/// <summary>A medium changer (SMC-3): inventory with barcodes and drive identifiers, and moves.</summary>
public sealed class MediumChanger(ScsiCommandRunner runner)
{
    private const int Chunk = 64;
    private ElementAddresses? _addresses;

    public ScsiCommandRunner Runner { get; } = runner;

    public string Label => Runner.Label;

    public Task<DeviceIdentity> IdentifyAsync(CancellationToken cancellationToken) => TapeDrive.Identify(Runner, cancellationToken);

    public async Task<ElementAddresses> ReadAddressesAsync(CancellationToken cancellationToken)
    {
        var mode = ModeParameters.Parse10(await Runner.ReadAsync(
            "MODE SENSE (element address assignment)", Cdb.ModeSense10(ModePages.MediumConfigurationOrElementAddresses, disableBlockDescriptors: true), 512, cancellationToken).ConfigureAwait(false));
        if (!mode.Pages.TryGetValue(ModePages.MediumConfigurationOrElementAddresses, out var page))
        {
            throw new InvalidDataException($"{Label} did not return its element address assignment page.");
        }

        return _addresses = ElementAddresses.Parse(page);
    }

    /// <summary>Reads every element, with barcodes, and for drives the identifier that maps them to their device.</summary>
    public async Task<ChangerInventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        var addresses = _addresses ?? await ReadAddressesAsync(cancellationToken).ConfigureAwait(false);
        var drives = await ReadElementsAsync(Cdb.ElementType.DataTransfer, addresses.FirstDrive, addresses.Drives, cancellationToken).ConfigureAwait(false);
        var slots = await ReadElementsAsync(Cdb.ElementType.Storage, addresses.FirstStorage, addresses.Storage, cancellationToken).ConfigureAwait(false);
        var mail = await ReadElementsAsync(Cdb.ElementType.ImportExport, addresses.FirstImportExport, addresses.ImportExport, cancellationToken).ConfigureAwait(false);
        return new ChangerInventory(addresses, drives, slots, mail);
    }

    /// <summary>Only the mail slots: cheap enough to poll for cartridges an operator put in.</summary>
    public async Task<IReadOnlyList<ChangerElement>> ReadMailSlotsAsync(CancellationToken cancellationToken)
    {
        var addresses = _addresses ?? await ReadAddressesAsync(cancellationToken).ConfigureAwait(false);
        return await ReadElementsAsync(Cdb.ElementType.ImportExport, addresses.FirstImportExport, addresses.ImportExport, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves a cartridge with the first robot. The drive must have unloaded it first.</summary>
    public async Task MoveAsync(ushort source, ushort destination, CancellationToken cancellationToken)
    {
        var addresses = _addresses ?? await ReadAddressesAsync(cancellationToken).ConfigureAwait(false);
        await Runner.SendAsync(
            "MOVE MEDIUM",
            Cdb.MoveMedium(addresses.FirstTransport, source, destination),
            cancellationToken,
            timeout: ScsiTimeouts.Move,
            movesMedium: true).ConfigureAwait(false);
    }

    /// <summary>Lets the robot check every element again (after the door was open, or on demand).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        Runner.SendAsync("INITIALIZE ELEMENT STATUS", Cdb.InitializeElementStatus(), cancellationToken, timeout: ScsiTimeouts.Positioning);

    /// <summary>Locks (or unlocks) the mail slots against opening.</summary>
    public Task PreventRemovalAsync(bool prevent, CancellationToken cancellationToken) =>
        Runner.SendAsync("PREVENT ALLOW MEDIUM REMOVAL", Cdb.PreventAllowMediumRemoval(prevent), cancellationToken);

    private async Task<List<ChangerElement>> ReadElementsAsync(Cdb.ElementType type, ushort first, ushort count, CancellationToken cancellationToken)
    {
        var result = new List<ChangerElement>();
        for (var start = 0; start < count; start += Chunk)
        {
            var n = (ushort)Math.Min(Chunk, count - start);
            var address = (ushort)(first + start);
            var deviceIds = type == Cdb.ElementType.DataTransfer;
            var allocation = 8 + 8 + (n * 160);
            IReadOnlyList<ChangerElement> elements;
            try
            {
                elements = await ReadAsync(type, address, n, deviceIds, allocation, cancellationToken).ConfigureAwait(false);
            }
            catch (ScsiCommandException ex) when (deviceIds && ex.Sense?.Key == SenseKey.IllegalRequest)
            {
                // A changer without DVCID: drives are then mapped by hand.
                elements = await ReadAsync(type, address, n, false, allocation, cancellationToken).ConfigureAwait(false);
            }

            result.AddRange(elements.Where(e => e.Type == type));
        }

        return result;
    }

    private async Task<IReadOnlyList<ChangerElement>> ReadAsync(Cdb.ElementType type, ushort address, ushort count, bool deviceIds, int allocation, CancellationToken cancellationToken)
    {
        var data = await Runner.ReadAsync("READ ELEMENT STATUS", Cdb.ReadElementStatus(type, address, count, volumeTags: true, deviceIds, allocation), allocation, cancellationToken, ScsiTimeouts.Move).ConfigureAwait(false);
        var elements = ElementStatus.Parse(data, out var complete);
        if (!complete)
        {
            throw new InvalidDataException($"{Label}: READ ELEMENT STATUS returned more than {allocation} bytes for {count} element(s) at {address}.");
        }

        return elements;
    }
}
