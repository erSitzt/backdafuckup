using System.Buffers.Binary;
using System.Text;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Testing.Tape;

/// <summary>A record on a fake cartridge: a block (with the key id it was encrypted with), or a filemark (no data).</summary>
public sealed record FakeRecord(byte[]? Data, byte[]? KeyId)
{
    public bool IsFilemark => Data is null;
}

public sealed class FakeCartridge(string barcode, long capacityBytes)
{
    public string Barcode { get; } = barcode;

    public long CapacityBytes { get; } = capacityBytes;

    /// <summary>Bytes before the end at which writes report early warning.</summary>
    public long EarlyWarningZone { get; set; } = Math.Min(capacityBytes / 8, 8L * 1024 * 1024);

    public bool Worm { get; init; }

    public bool Cleaning { get; init; }

    public bool WriteProtected { get; set; }

    public string MediumSerial { get; init; } = "MS" + barcode;

    public List<FakeRecord> Records { get; } = [];

    public Dictionary<ushort, byte[]> HostAttributes { get; } = [];

    public long UsedBytes { get; internal set; }

    public bool PastEarlyWarning => UsedBytes >= CapacityBytes - EarlyWarningZone;

    /// <summary>The blocks of file <paramref name="file"/> (between filemarks).</summary>
    public IEnumerable<byte[]> File(int file)
    {
        var current = 0;
        foreach (var record in Records)
        {
            if (record.IsFilemark)
            {
                current++;
            }
            else if (current == file)
            {
                yield return record.Data!;
            }
        }
    }

    internal void TruncateAt(int position)
    {
        if (position < Records.Count)
        {
            Records.RemoveRange(position, Records.Count - position);
            UsedBytes = Records.Sum(r => r.Data?.LongLength ?? FakeTapeLibrary.FilemarkBytes);
        }
    }
}

/// <summary>
/// An in-memory tape library behind <see cref="IScsiDevice"/>: a medium changer and its drives that parse the real CDBs
/// (SPC, SSC, SMC), with capacity and early warning, filemarks, end of data, WORM, medium auxiliary memory and data
/// encryption. <see cref="HoloQuirks"/> behaves like the Holo VTL (no early-warning sense, no ILI or FILEMARK bits, no
/// SECURITY PROTOCOL).
/// </summary>
public sealed class FakeTapeLibrary
{
    internal const long FilemarkBytes = 64;
    private readonly Lock _lock = new();
    private readonly Dictionary<ushort, FakeCartridge?> _slots = [];
    private readonly Dictionary<ushort, FakeCartridge?> _mail = [];
    private readonly Dictionary<FakeCartridge, ushort> _source = [];

    public FakeTapeLibrary(int drives = 2, int slots = 24, int mailSlots = 4, ushort firstTransport = 1, ushort firstDrive = 256, ushort firstMail = 768, ushort firstSlot = 1024)
    {
        FirstTransport = firstTransport;
        FirstDrive = firstDrive;
        FirstMail = firstMail;
        FirstSlot = firstSlot;
        for (var i = 0; i < slots; i++)
        {
            _slots[(ushort)(firstSlot + i)] = null;
        }

        for (var i = 0; i < mailSlots; i++)
        {
            _mail[(ushort)(firstMail + i)] = null;
        }

        Drives = [.. Enumerable.Range(0, drives).Select(i => new FakeTapeDrive(this, i, (ushort)(firstDrive + i)))];
        Changer = new FakeChanger(this);
    }

    public ushort FirstTransport { get; }

    public ushort FirstDrive { get; }

    public ushort FirstMail { get; }

    public ushort FirstSlot { get; }

    public IReadOnlyList<FakeTapeDrive> Drives { get; }

    public FakeChanger Changer { get; }

    public string Vendor { get; init; } = "HPE";

    public string ChangerProduct { get; init; } = "MSL6480";

    public string DriveProduct { get; init; } = "Ultrium 9-SCSI";

    /// <summary>Put in front of the changer's and the drives' serial numbers, to tell several fake libraries apart.</summary>
    public string SerialPrefix { get; init; } = string.Empty;

    /// <summary>Behave like the Holo VTL: no early-warning sense, no ILI or FILEMARK bits, no SECURITY PROTOCOL.</summary>
    public bool HoloQuirks { get; set; }

    /// <summary>The changer reports drive identifiers (DVCID).</summary>
    public bool DeviceIds { get; set; } = true;

    /// <summary>TEST UNIT READY polls answering "becoming ready" after a cartridge is put into a drive.</summary>
    public int BecomingReadyPolls { get; set; } = 2;

    /// <summary>Called before every command; a result returned is answered instead (inject sense, drops, delays).</summary>
    public Func<IScsiDevice, ScsiCommand, ScsiResult?>? Inject { get; set; }

    /// <summary>Every command as "path NAME CDB-hex", for checks that something was or was not sent.</summary>
    public List<string> CommandLog { get; } = [];

    public FakeCartridge AddCartridge(string barcode, long capacityBytes, bool worm = false, bool cleaning = false, ushort? slot = null)
    {
        lock (_lock)
        {
            var cartridge = new FakeCartridge(barcode, capacityBytes) { Worm = worm, Cleaning = cleaning };
            var address = slot ?? _slots.First(s => s.Value is null).Key;
            if (_slots[address] is not null)
            {
                throw new InvalidOperationException($"Slot {address} is full.");
            }

            _slots[address] = cartridge;
            return cartridge;
        }
    }

    /// <summary>An operator puts a cartridge into a mail slot (the changer reports UNIT ATTENTION 28/01).</summary>
    public FakeCartridge OperatorInsert(string barcode, long capacityBytes, bool worm = false)
    {
        lock (_lock)
        {
            var cartridge = new FakeCartridge(barcode, capacityBytes) { Worm = worm };
            _mail[_mail.First(m => m.Value is null).Key] = cartridge;
            Changer.PendingAttention.Enqueue((0x28, 0x01));
            return cartridge;
        }
    }

    /// <summary>An operator takes a cartridge out of a mail slot.</summary>
    public FakeCartridge? OperatorRemove(ushort mailSlot)
    {
        lock (_lock)
        {
            var cartridge = _mail[mailSlot];
            _mail[mailSlot] = null;
            Changer.PendingAttention.Enqueue((0x28, 0x01));
            return cartridge;
        }
    }

    /// <summary>Every cartridge in the library: slots, mail slots, drives.</summary>
    public IReadOnlyList<FakeCartridge> Cartridges
    {
        get
        {
            lock (_lock)
            {
                return [.. _slots.Values.Concat(_mail.Values).Concat(Drives.Select(d => d.Cartridge)).OfType<FakeCartridge>()];
            }
        }
    }

    /// <summary>Where a cartridge is: a slot, mail slot or drive address; null when it left the library.</summary>
    public ushort? LocationOf(string barcode)
    {
        lock (_lock)
        {
            foreach (var (address, cartridge) in _slots.Concat(_mail))
            {
                if (cartridge?.Barcode == barcode)
                {
                    return address;
                }
            }

            return Drives.FirstOrDefault(d => d.Cartridge?.Barcode == barcode)?.ElementAddress;
        }
    }

    internal ScsiResult Run(IScsiDevice device, ScsiCommand command, Func<ScsiResult> handler)
    {
        lock (_lock)
        {
            CommandLog.Add($"{device.Path} {command.Name} {Convert.ToHexString(command.Cdb)}");
            if (Inject?.Invoke(device, command) is { } injected)
            {
                return injected;
            }

            return handler();
        }
    }

    internal static ScsiResult Good(int residual = 0) => new(ScsiStatus.Good, null, residual);

    internal static ScsiResult Check(SenseKey key, byte asc, byte ascq, bool filemark = false, bool eom = false, bool ili = false, int? information = null, int residual = 0) =>
        new(ScsiStatus.CheckCondition, SenseData.Parse(SenseData.BuildFixed(key, asc, ascq, filemark, eom, ili, information)), residual);

    internal static ScsiResult Reply(ScsiCommand command, ReadOnlySpan<byte> data)
    {
        var n = Math.Min(data.Length, command.Buffer.Length);
        data[..n].CopyTo(command.Buffer.Span);
        return Good(command.Buffer.Length - n);
    }

    /// <summary>INQUIRY data; with <see cref="HoloQuirks"/> only the low byte of the allocation length counts (as in SCSI-2).</summary>
    internal static ScsiResult ReplyInquiry(FakeTapeLibrary library, ScsiCommand command, byte[] data)
    {
        var allocation = library.HoloQuirks ? command.Cdb[4] : (command.Cdb[3] << 8) | command.Cdb[4];
        var n = Math.Min(Math.Min(data.Length, allocation), command.Buffer.Length);
        data.AsSpan(0, n).CopyTo(command.Buffer.Span);
        return Good(command.Buffer.Length - n);
    }

    internal static byte[] Inquiry(byte type, string vendor, string product)
    {
        var data = new byte[96];
        data[0] = type;
        data[1] = 0x80;
        data[2] = 0x06;
        data[3] = 0x02;
        data[4] = 91;
        Ascii(vendor, 8).CopyTo(data, 8);
        Ascii(product, 16).CopyTo(data, 16);
        Ascii("6.24", 4).CopyTo(data, 32);
        return data;
    }

    internal static byte[] Vpd(byte page, string serial, string vendor, string product)
    {
        switch (page)
        {
            case 0x00:
                return [0, 0x00, 0, 3, 0x00, 0x80, 0x83];
            case 0x80:
            {
                var bytes = Encoding.ASCII.GetBytes(serial);
                return [0, 0x80, 0, (byte)bytes.Length, .. bytes];
            }

            default:
            {
                var id = Encoding.ASCII.GetBytes(DeviceIdText(vendor, product, serial));
                return [0, 0x83, 0, (byte)(4 + id.Length), 0x02, 0x01, 0, (byte)id.Length, .. id];
            }
        }
    }

    internal static string DeviceIdText(string vendor, string product, string serial) =>
        Encoding.ASCII.GetString(Ascii(vendor, 8)) + Encoding.ASCII.GetString(Ascii(product, 16)) + serial;

    internal static byte[] Ascii(string text, int length)
    {
        var value = Enumerable.Repeat((byte)' ', length).ToArray();
        var bytes = Encoding.ASCII.GetBytes(text);
        bytes.AsSpan(0, Math.Min(length, bytes.Length)).CopyTo(value);
        return value;
    }

    // ---- The changer ----

    public sealed class FakeChanger(FakeTapeLibrary library) : IScsiDevice
    {
        internal Queue<(byte Asc, byte Ascq)> PendingAttention { get; } = new();

        public string Path => "fake:changer";

        public string Serial => library.SerialPrefix + "LIB0001";

        public int MaxTransferLength => 1024 * 1024;

        public bool MailSlotsLocked { get; private set; }

        public ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(library.Run(this, command, () => Handle(command)));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ScsiResult Handle(ScsiCommand command)
        {
            var cdb = command.Cdb;
            if (cdb[0] != Cdb.InquiryCode && PendingAttention.TryDequeue(out var attention))
            {
                return Check(SenseKey.UnitAttention, attention.Asc, attention.Ascq);
            }

            switch (cdb[0])
            {
                case Cdb.TestUnitReadyCode or Cdb.InitializeElementStatusCode:
                    return Good();
                case Cdb.InquiryCode:
                    return ReplyInquiry(library, command, (cdb[1] & 1) != 0 ? Vpd(cdb[2], Serial, library.Vendor, library.ChangerProduct) : Inquiry(0x08, library.Vendor, library.ChangerProduct));
                case Cdb.PreventAllowCode:
                    MailSlotsLocked = (cdb[4] & 1) != 0;
                    return Good();
                case Cdb.ModeSense10Code when (cdb[2] & 0x3F) is ModePages.MediumConfigurationOrElementAddresses or 0x3F:
                {
                    var page = new byte[20];
                    page[0] = 0x1D;
                    page[1] = 18;
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(2), library.FirstTransport);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(4), 1);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(6), library.FirstSlot);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(8), (ushort)library._slots.Count);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(10), library.FirstMail);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(12), (ushort)library._mail.Count);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(14), library.FirstDrive);
                    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(16), (ushort)library.Drives.Count);
                    byte[] data = [0, (byte)(6 + page.Length), 0, 0, 0, 0, 0, 0, .. page];
                    return Reply(command, data);
                }

                case Cdb.ReadElementStatusCode:
                    return ReadElementStatus(command);
                case Cdb.MoveMediumCode:
                    return Move(BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(4)), BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(6)));
                default:
                    return Check(SenseKey.IllegalRequest, 0x20, 0x00);
            }
        }

        private ScsiResult ReadElementStatus(ScsiCommand command)
        {
            var cdb = command.Cdb;
            var type = (Cdb.ElementType)(cdb[1] & 0x0F);
            var volumeTags = (cdb[1] & 0x10) != 0;
            var deviceIds = (cdb[6] & 0x01) != 0;
            if (deviceIds && !library.DeviceIds)
            {
                return Check(SenseKey.IllegalRequest, 0x24, 0x00);
            }

            var start = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(2));
            var count = BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(4));
            var elements = new List<(Cdb.ElementType Type, ushort Address, FakeCartridge? Cartridge, FakeTapeDrive? Drive)>();
            foreach (var t in type == Cdb.ElementType.All ? new[] { Cdb.ElementType.DataTransfer, Cdb.ElementType.ImportExport, Cdb.ElementType.Storage } : [type])
            {
                switch (t)
                {
                    case Cdb.ElementType.DataTransfer:
                        elements.AddRange(library.Drives.Select(d => (t, d.ElementAddress, d.Cartridge, (FakeTapeDrive?)d)));
                        break;
                    case Cdb.ElementType.ImportExport:
                        elements.AddRange(library._mail.Select(m => (t, m.Key, m.Value, (FakeTapeDrive?)null)));
                        break;
                    case Cdb.ElementType.Storage:
                        elements.AddRange(library._slots.Select(s => (t, s.Key, s.Value, (FakeTapeDrive?)null)));
                        break;
                }
            }

            var chosen = elements.Where(e => e.Address >= start).OrderBy(e => e.Address).Take(count).ToList();
            using var body = new MemoryStream();
            foreach (var group in chosen.GroupBy(e => e.Type))
            {
                var descriptors = group.Select(e => Descriptor(e.Address, e.Cartridge, e.Drive, e.Type, volumeTags, deviceIds)).ToList();
                var length = descriptors.Max(d => d.Length);
                var page = new byte[8];
                page[0] = (byte)group.Key;
                page[1] = (byte)(volumeTags ? 0x80 : 0);
                BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(2), (ushort)length);
                var bytes = descriptors.Count * length;
                page[5] = (byte)(bytes >> 16);
                page[6] = (byte)(bytes >> 8);
                page[7] = (byte)bytes;
                body.Write(page);
                foreach (var descriptor in descriptors)
                {
                    body.Write(descriptor);
                    body.Write(new byte[length - descriptor.Length]);
                }
            }

            var report = body.ToArray();
            var header = new byte[8];
            BinaryPrimitives.WriteUInt16BigEndian(header, chosen.Count > 0 ? chosen[0].Address : start);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)chosen.Count);
            header[5] = (byte)(report.Length >> 16);
            header[6] = (byte)(report.Length >> 8);
            header[7] = (byte)report.Length;
            return Reply(command, [.. header, .. report]);
        }

        private byte[] Descriptor(ushort address, FakeCartridge? cartridge, FakeTapeDrive? drive, Cdb.ElementType type, bool volumeTags, bool deviceIds)
        {
            var d = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(d, address);
            var access = drive is null || !drive.Loaded;
            d[2] = (byte)((cartridge is not null ? 0x01 : 0) | (access ? 0x08 : 0));
            if (cartridge is not null && library._source.TryGetValue(cartridge, out var source))
            {
                d[9] = 0x80;
                BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(10), source);
            }

            var result = new List<byte>(d);
            if (volumeTags)
            {
                result.AddRange(Ascii(cartridge?.Barcode ?? string.Empty, 32));
                result.AddRange(new byte[4]);
            }

            if (deviceIds && type == Cdb.ElementType.DataTransfer && drive is not null)
            {
                var id = Encoding.ASCII.GetBytes(DeviceIdText(library.Vendor, library.DriveProduct, drive.Serial));
                result.AddRange([0x02, 0x01, 0x00, (byte)id.Length]);
                result.AddRange(id);
            }

            return [.. result];
        }

        private ScsiResult Move(ushort source, ushort destination)
        {
            var from = Locate(source);
            var to = Locate(destination);
            if (from is null || to is null)
            {
                return Check(SenseKey.IllegalRequest, 0x21, 0x01);
            }

            var cartridge = from.Value.Get();
            if (cartridge is null)
            {
                return Check(SenseKey.IllegalRequest, 0x3B, 0x0E);
            }

            if (to.Value.Get() is not null)
            {
                return Check(SenseKey.IllegalRequest, 0x3B, 0x0D);
            }

            if (from.Value.Drive is { } sourceDrive && (sourceDrive.Loaded || sourceDrive.PreventRemoval))
            {
                return Check(SenseKey.IllegalRequest, 0x53, 0x02);
            }

            from.Value.Set(null);
            to.Value.Set(cartridge);
            if (from.Value.Drive is null)
            {
                library._source[cartridge] = source;
            }

            if (to.Value.Drive is { } drive)
            {
                drive.Inserted();
            }

            return Good();
        }

        private (Func<FakeCartridge?> Get, Action<FakeCartridge?> Set, FakeTapeDrive? Drive)? Locate(ushort address)
        {
            if (library._slots.ContainsKey(address))
            {
                return (() => library._slots[address], c => library._slots[address] = c, null);
            }

            if (library._mail.ContainsKey(address))
            {
                return (() => library._mail[address], c => library._mail[address] = c, null);
            }

            if (library.Drives.FirstOrDefault(d => d.ElementAddress == address) is { } drive)
            {
                return (() => drive.Cartridge, c => drive.Cartridge = c, drive);
            }

            return null;
        }
    }
}

/// <summary>A drive of <see cref="FakeTapeLibrary"/> (or a standalone one when the library has no changer in use).</summary>
public sealed class FakeTapeDrive(FakeTapeLibrary library, int index, ushort elementAddress) : IScsiDevice
{
    private int _becomingReady;

    public ushort ElementAddress { get; } = elementAddress;

    public string Serial => library.SerialPrefix + $"DRV{index + 1:D4}";

    public string Path => $"fake:drive{index + 1}";

    public int MaxTransferLength { get; set; } = 1024 * 1024;

    public FakeCartridge? Cartridge { get; internal set; }

    /// <summary>The cartridge is threaded and usable (not just inserted, not unloaded).</summary>
    public bool Loaded { get; private set; }

    public int Position { get; private set; }

    public bool PreventRemoval { get; private set; }

    public bool Compression { get; private set; } = true;

    public bool SynchronizeAtEarlyWarning { get; private set; }

    public int BlockLength { get; private set; } = 0x40000;

    public byte[]? Key { get; private set; }

    public byte[]? KeyId { get; private set; }

    public HashSet<int> TapeAlerts { get; } = [];

    internal Queue<(byte Asc, byte Ascq)> PendingAttention { get; } = new();

    /// <summary>The next command (but INQUIRY) is answered with UNIT ATTENTION <paramref name="asc"/>/<paramref name="ascq"/>, e.g. 29/00 after a reset.</summary>
    public void QueueUnitAttention(byte asc, byte ascq) => PendingAttention.Enqueue((asc, ascq));

    /// <summary>A standalone drive: an operator inserts a cartridge.</summary>
    public void OperatorInsert(FakeCartridge cartridge)
    {
        Cartridge = cartridge;
        Inserted();
    }

    public ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken) =>
        ValueTask.FromResult(library.Run(this, command, () => Handle(command)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal void Inserted()
    {
        Loaded = true;
        Position = 0;
        _becomingReady = library.BecomingReadyPolls;
        PendingAttention.Enqueue((0x28, 0x00));
        Key = null;
        KeyId = null;
    }

    private ScsiResult Handle(ScsiCommand command)
    {
        var cdb = command.Cdb;
        switch (cdb[0])
        {
            case Cdb.InquiryCode:
                return FakeTapeLibrary.ReplyInquiry(library, command, (cdb[1] & 1) != 0 ? FakeTapeLibrary.Vpd(cdb[2], Serial, library.Vendor, library.DriveProduct) : FakeTapeLibrary.Inquiry(0x01, library.Vendor, library.DriveProduct));
            case Cdb.RequestSenseCode:
                return FakeTapeLibrary.Reply(command, SenseData.BuildFixed(SenseKey.NoSense, 0, 0));
            case Cdb.MaintenanceInCode:
                return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x20, 0x00);
        }

        if (PendingAttention.TryDequeue(out var attention))
        {
            return FakeTapeLibrary.Check(SenseKey.UnitAttention, attention.Asc, attention.Ascq);
        }

        switch (cdb[0])
        {
            case Cdb.ReadBlockLimitsCode:
                return FakeTapeLibrary.Reply(command, [0, 0x80, 0x00, 0x00, 0, 1]); // 8 MiB
            case Cdb.PreventAllowCode:
                PreventRemoval = (cdb[4] & 1) != 0;
                return FakeTapeLibrary.Good();
            case Cdb.SecurityProtocolInCode or Cdb.SecurityProtocolOutCode when library.HoloQuirks:
                return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x20, 0x00);
            case Cdb.SecurityProtocolInCode:
                return SecurityIn(command);
            case Cdb.SecurityProtocolOutCode:
                return SecurityOut(command);
            case Cdb.LoadUnloadCode:
                return LoadUnload((cdb[4] & 1) != 0);
        }

        if (Cartridge is null)
        {
            return FakeTapeLibrary.Check(SenseKey.NotReady, 0x3A, 0x00);
        }

        if (_becomingReady > 0)
        {
            _becomingReady--;
            return FakeTapeLibrary.Check(SenseKey.NotReady, 0x04, 0x01);
        }

        if (!Loaded)
        {
            return FakeTapeLibrary.Check(SenseKey.NotReady, 0x04, 0x02);
        }

        if (Cartridge.Cleaning && cdb[0] is not (Cdb.ModeSense10Code or Cdb.ReadAttributeCode or Cdb.LogSenseCode))
        {
            return FakeTapeLibrary.Check(SenseKey.NotReady, 0x30, 0x03);
        }

        return cdb[0] switch
        {
            Cdb.TestUnitReadyCode => FakeTapeLibrary.Good(),
            Cdb.ModeSense10Code => ModeSense(command),
            Cdb.ModeSelect10Code => ModeSelect(command),
            Cdb.LogSenseCode => LogSense(command),
            Cdb.ReadAttributeCode => ReadAttribute(command),
            Cdb.WriteAttributeCode => WriteAttribute(command),
            Cdb.RewindCode => Rewind(),
            Cdb.ReadPositionCode => ReadPosition(command),
            Cdb.Locate16Code => Locate(cdb),
            Cdb.Space16Code => Space((Cdb.SpaceCode)(cdb[1] & 0x0F), BinaryPrimitives.ReadInt64BigEndian(cdb.AsSpan(4))),
            Cdb.Read6Code => Read(command),
            Cdb.Write6Code => Write(command.Buffer.Span[..((cdb[2] << 16) | (cdb[3] << 8) | cdb[4])].ToArray()),
            Cdb.WriteFilemarks6Code => WriteFilemarks((cdb[2] << 16) | (cdb[3] << 8) | cdb[4]),
            Cdb.Erase6Code => Erase(),
            _ => FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x20, 0x00),
        };
    }

    private ScsiResult LoadUnload(bool load)
    {
        if (Cartridge is null)
        {
            return FakeTapeLibrary.Check(SenseKey.NotReady, 0x3A, 0x00);
        }

        if (!load && PreventRemoval)
        {
            return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x53, 0x02);
        }

        Loaded = load;
        Position = 0;
        _becomingReady = 0;
        if (!load)
        {
            Key = null; // CKOD
            KeyId = null;
        }

        return FakeTapeLibrary.Good();
    }

    private ScsiResult ModeSense(ScsiCommand command)
    {
        var page = command.Cdb[2] & 0x3F;
        var pages = new List<byte>();
        if (page is ModePages.DataCompression or 0x3F)
        {
            var p = new byte[16];
            p[0] = 0x0F;
            p[1] = 14;
            p[2] = (byte)(0x40 | (Compression ? 0x80 : 0));
            pages.AddRange(p);
        }

        if (page is ModePages.DeviceConfiguration or 0x3F)
        {
            var p = new byte[16];
            p[0] = 0x10;
            p[1] = 14;
            p[10] = (byte)(SynchronizeAtEarlyWarning ? 0x08 : 0);
            pages.AddRange(p);
        }

        if (page is ModePages.MediumConfigurationOrElementAddresses or 0x3F)
        {
            pages.AddRange([0x1D, 6, (byte)(Cartridge!.Worm ? 1 : 0), 0, 0, 0, 0, 0]);
        }

        var length = 8 + 8 + pages.Count;
        var data = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)(length - 2));
        data[2] = 0;
        data[3] = (byte)((Cartridge!.WriteProtected ? 0x80 : 0) | 0x10);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 8);
        data[8] = 0x60; // LTO-9 density
        data[13] = (byte)(BlockLength >> 16);
        data[14] = (byte)(BlockLength >> 8);
        data[15] = (byte)BlockLength;
        pages.CopyTo(data, 16);
        return FakeTapeLibrary.Reply(command, data);
    }

    private ScsiResult ModeSelect(ScsiCommand command)
    {
        var data = command.Buffer.Span;
        var descriptors = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (descriptors >= 8)
        {
            BlockLength = (data[13] << 16) | (data[14] << 8) | data[15];
        }

        for (var offset = 8 + descriptors; offset + 2 <= data.Length;)
        {
            var code = data[offset] & 0x3F;
            var length = 2 + data[offset + 1];
            if (code == 0x0F)
            {
                Compression = (data[offset + 2] & 0x80) != 0;
            }
            else if (code == 0x10)
            {
                SynchronizeAtEarlyWarning = (data[offset + 10] & 0x08) != 0;
            }

            offset += length;
        }

        return FakeTapeLibrary.Good();
    }

    private ScsiResult LogSense(ScsiCommand command)
    {
        var page = (byte)(command.Cdb[2] & 0x3F);
        var parameters = new List<byte>();
        if (page == LogPages.TapeAlert)
        {
            for (var flag = 1; flag <= 64; flag++)
            {
                parameters.AddRange([0, (byte)flag, 0x03, 1, (byte)(!library.HoloQuirks && TapeAlerts.Contains(flag) ? 1 : 0)]);
            }
        }

        byte[] data = [page, 0, (byte)(parameters.Count >> 8), (byte)parameters.Count, .. parameters];
        return FakeTapeLibrary.Reply(command, data);
    }

    private ScsiResult ReadAttribute(ScsiCommand command)
    {
        var cartridge = Cartridge!;
        var attributes = new List<(ushort Id, byte Format, byte[] Value)>
        {
            (MamAttributes.RemainingCapacity, 0, BigEndian((cartridge.CapacityBytes - cartridge.UsedBytes) / (1024 * 1024))),
            (MamAttributes.MaximumCapacity, 0, BigEndian(cartridge.CapacityBytes / (1024 * 1024))),
            (MamAttributes.MediumManufacturer, 1, FakeTapeLibrary.Ascii("HPE", 8)),
            (MamAttributes.MediumSerialNumber, 1, FakeTapeLibrary.Ascii(cartridge.MediumSerial, 32)),
            (MamAttributes.MediumType, 0, [cartridge.Cleaning ? MamAttributes.MediumTypeCleaning : cartridge.Worm ? MamAttributes.MediumTypeWorm : MamAttributes.MediumTypeData]),
        };
        attributes.AddRange(cartridge.HostAttributes.Select(a => (a.Key, (byte)1, a.Value)));
        var first = BinaryPrimitives.ReadUInt16BigEndian(command.Cdb.AsSpan(8));
        return FakeTapeLibrary.Reply(command, MamAttributes.Build(attributes.Where(a => a.Id >= first).OrderBy(a => a.Id)));
    }

    private ScsiResult WriteAttribute(ScsiCommand command)
    {
        var attributes = MamAttributes.Parse(command.Buffer.Span);
        if (attributes.Keys.Any(id => id < 0x0800))
        {
            return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x26, 0x00);
        }

        foreach (var (id, value) in attributes)
        {
            Cartridge!.HostAttributes[id] = value;
        }

        return FakeTapeLibrary.Good();
    }

    private ScsiResult Rewind()
    {
        Position = 0;
        return FakeTapeLibrary.Good();
    }

    private ScsiResult ReadPosition(ScsiCommand command)
    {
        var cartridge = Cartridge!;
        var filemarks = cartridge.Records.Take(Position).Count(r => r.IsFilemark);
        var flags = (byte)((Position == 0 ? 0x80 : 0) | (cartridge.PastEarlyWarning ? 0x40 : 0));
        if ((command.Cdb[1] & 0x1F) == 0x06)
        {
            var data = new byte[32];
            data[0] = flags;
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(8), Position);
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(16), filemarks);
            return FakeTapeLibrary.Reply(command, data);
        }

        var shortForm = new byte[20];
        shortForm[0] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(shortForm.AsSpan(4), (uint)Position);
        BinaryPrimitives.WriteUInt32BigEndian(shortForm.AsSpan(8), (uint)Position);
        return FakeTapeLibrary.Reply(command, shortForm);
    }

    private ScsiResult Locate(byte[] cdb)
    {
        var records = Cartridge!.Records;
        var target = BinaryPrimitives.ReadInt64BigEndian(cdb.AsSpan(4));
        if ((cdb[1] & 0x38) == 0x08)
        {
            // To the start of logical file `target`.
            var position = 0;
            for (long file = 0; file < target; position++)
            {
                if (position >= records.Count)
                {
                    Position = records.Count;
                    return FakeTapeLibrary.Check(SenseKey.BlankCheck, 0x00, 0x05);
                }

                if (records[position].IsFilemark)
                {
                    file++;
                }
            }

            Position = position;
            return FakeTapeLibrary.Good();
        }

        if (target > records.Count)
        {
            Position = records.Count;
            return FakeTapeLibrary.Check(SenseKey.BlankCheck, 0x00, 0x05);
        }

        Position = (int)target;
        return FakeTapeLibrary.Good();
    }

    private ScsiResult Space(Cdb.SpaceCode code, long count)
    {
        var records = Cartridge!.Records;
        switch (code)
        {
            case Cdb.SpaceCode.EndOfData:
                Position = records.Count;
                return FakeTapeLibrary.Good();
            case Cdb.SpaceCode.Filemarks when count >= 0:
                for (var passed = 0L; passed < count;)
                {
                    if (Position >= records.Count)
                    {
                        return FakeTapeLibrary.Check(SenseKey.BlankCheck, 0x00, 0x05, eom: true, information: (int)(count - passed));
                    }

                    if (records[Position++].IsFilemark)
                    {
                        passed++;
                    }
                }

                return FakeTapeLibrary.Good();
            case Cdb.SpaceCode.Filemarks:
                for (var passed = 0L; passed < -count;)
                {
                    if (Position == 0)
                    {
                        return FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x04, eom: true, information: (int)(-count - passed));
                    }

                    if (records[--Position].IsFilemark)
                    {
                        passed++;
                    }
                }

                return FakeTapeLibrary.Good();
            case Cdb.SpaceCode.Blocks:
                for (var passed = 0L; passed < Math.Abs(count); passed++)
                {
                    if (count > 0 ? Position >= records.Count : Position == 0)
                    {
                        return count > 0
                            ? FakeTapeLibrary.Check(SenseKey.BlankCheck, 0x00, 0x05, information: (int)(count - passed))
                            : FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x04, eom: true, information: (int)(-count - passed));
                    }

                    var record = records[count > 0 ? Position : Position - 1];
                    Position += count > 0 ? 1 : -1;
                    if (record.IsFilemark)
                    {
                        return FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x01, filemark: !library.HoloQuirks, information: (int)(Math.Abs(count) - passed - 1));
                    }
                }

                return FakeTapeLibrary.Good();
            default:
                return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x24, 0x00);
        }
    }

    private ScsiResult Read(ScsiCommand command)
    {
        var records = Cartridge!.Records;
        var requested = (command.Cdb[2] << 16) | (command.Cdb[3] << 8) | command.Cdb[4];
        var sili = (command.Cdb[1] & 0x02) != 0;
        if (Position >= records.Count)
        {
            return FakeTapeLibrary.Check(SenseKey.BlankCheck, 0x00, 0x05, residual: requested);
        }

        var record = records[Position];
        if (record.IsFilemark)
        {
            Position++;
            return FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x01, filemark: !library.HoloQuirks, information: requested, residual: requested);
        }

        if (record.KeyId is not null && (Key is null || !record.KeyId.AsSpan().SequenceEqual(KeyId)))
        {
            return FakeTapeLibrary.Check(SenseKey.DataProtect, 0x74, (byte)(Key is null ? 0x01 : 0x03), residual: requested);
        }

        Position++;
        var data = record.Data!;
        var n = Math.Min(data.Length, requested);
        data.AsSpan(0, n).CopyTo(command.Buffer.Span);
        if (data.Length > requested)
        {
            return library.HoloQuirks
                ? FakeTapeLibrary.Good()
                : FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x00, ili: true, information: requested - data.Length);
        }

        if (data.Length < requested && !sili)
        {
            return FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x00, ili: !library.HoloQuirks, information: requested - data.Length, residual: requested - data.Length);
        }

        return FakeTapeLibrary.Good(requested - data.Length);
    }

    private ScsiResult Write(byte[] data) => Append(data, 1);

    private ScsiResult WriteFilemarks(int count) => count == 0 ? FakeTapeLibrary.Good() : Append(null, count);

    private ScsiResult Append(byte[]? data, int count)
    {
        var cartridge = Cartridge!;
        if (cartridge.WriteProtected)
        {
            return FakeTapeLibrary.Check(SenseKey.DataProtect, 0x27, 0x00);
        }

        if (cartridge.Worm && Position < cartridge.Records.Count)
        {
            return FakeTapeLibrary.Check(SenseKey.DataProtect, 0x30, 0x0C);
        }

        cartridge.TruncateAt(Position);
        var bytes = (data?.LongLength ?? FakeTapeLibrary.FilemarkBytes) * count;
        if (cartridge.UsedBytes + bytes > cartridge.CapacityBytes)
        {
            return FakeTapeLibrary.Check(SenseKey.VolumeOverflow, 0x00, 0x02, eom: true);
        }

        for (var i = 0; i < count; i++)
        {
            cartridge.Records.Add(new FakeRecord(data, data is null ? null : KeyId));
        }

        cartridge.UsedBytes += bytes;
        Position += count;
        return cartridge.PastEarlyWarning && !library.HoloQuirks
            ? FakeTapeLibrary.Check(SenseKey.NoSense, 0x00, 0x02, eom: true)
            : FakeTapeLibrary.Good();
    }

    private ScsiResult Erase()
    {
        var cartridge = Cartridge!;
        if (cartridge.Worm)
        {
            return FakeTapeLibrary.Check(SenseKey.DataProtect, 0x30, 0x0C);
        }

        if (cartridge.WriteProtected)
        {
            return FakeTapeLibrary.Check(SenseKey.DataProtect, 0x27, 0x00);
        }

        cartridge.TruncateAt(Position);
        return FakeTapeLibrary.Good();
    }

    private ScsiResult SecurityIn(ScsiCommand command)
    {
        var page = BinaryPrimitives.ReadUInt16BigEndian(command.Cdb.AsSpan(2));
        if (command.Cdb[1] != 0x20 || page != 0x0010)
        {
            return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x24, 0x00);
        }

        var data = new byte[20 + 24];
        BinaryPrimitives.WriteUInt16BigEndian(data, 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)(data.Length - 4));
        var descriptor = data.AsSpan(20);
        descriptor[0] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(descriptor[2..], 20);
        descriptor[4] = 0x0A; // DECRYPT_C and ENCRYPT_C: hardware
        BinaryPrimitives.WriteUInt16BigEndian(descriptor[6..], 32);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor[10..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor[20..], 0x00010014);
        return FakeTapeLibrary.Reply(command, data);
    }

    private ScsiResult SecurityOut(ScsiCommand command)
    {
        var list = command.Buffer.Span;
        if (command.Cdb[1] != 0x20 || list.Length < 20 || BinaryPrimitives.ReadUInt16BigEndian(list) != 0x0010)
        {
            return FakeTapeLibrary.Check(SenseKey.IllegalRequest, 0x24, 0x00);
        }

        if (list[6] == 0)
        {
            Key = null;
            KeyId = null;
            return FakeTapeLibrary.Good();
        }

        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(list[18..]);
        Key = list.Slice(20, keyLength).ToArray();
        var kad = list[(20 + keyLength)..];
        KeyId = kad.Length >= 4 ? kad.Slice(4, BinaryPrimitives.ReadUInt16BigEndian(kad[2..])).ToArray() : [];
        return FakeTapeLibrary.Good();
    }

    private static byte[] BigEndian(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }
}
