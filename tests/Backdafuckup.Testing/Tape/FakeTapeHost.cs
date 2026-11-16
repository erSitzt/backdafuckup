using System.Collections.Concurrent;
using Backdafuckup.Tape.Devices;
using Backdafuckup.Tape.Scsi;

namespace Backdafuckup.Testing.Tape;

/// <summary>
/// A host with fake libraries attached. Like <c>/dev/sg*</c> opened with O_EXCL, a device can be open only once at a
/// time: a second open fails, which shows a missing lock in the code under test.
/// </summary>
public sealed class FakeTapeHost : ITapeHost
{
    private readonly ConcurrentDictionary<string, bool> _open = new(StringComparer.Ordinal);

    public FakeTapeHost(params FakeTapeLibrary[] libraries) => Libraries = [.. libraries];

    public string Name => "fake host";

    public List<FakeTapeLibrary> Libraries { get; }

    /// <summary>Drives of these libraries are attached without their changer (standalone drives).</summary>
    public HashSet<FakeTapeLibrary> WithoutChanger { get; } = [];

    /// <summary>Serials whose devices answer nothing (another program has them open).</summary>
    public HashSet<string> Busy { get; } = [];

    public int Opens { get; private set; }

    public Task<IReadOnlyList<ScsiDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var result = new List<ScsiDeviceInfo>();
        foreach (var library in Libraries)
        {
            if (!WithoutChanger.Contains(library))
            {
                result.Add(Info(library.Changer.Path + library.SerialPrefix, 0x08, library.Vendor, library.ChangerProduct, library.Changer.Serial));
            }

            foreach (var drive in library.Drives)
            {
                result.Add(Info(drive.Path + library.SerialPrefix, 0x01, library.Vendor, library.DriveProduct, drive.Serial, FakeTapeLibrary.DeviceIdText(library.Vendor, library.DriveProduct, drive.Serial)));
            }
        }

        return Task.FromResult<IReadOnlyList<ScsiDeviceInfo>>(result);
    }

    public Task<IScsiDevice> OpenAsync(string serial, CancellationToken cancellationToken)
    {
        IScsiDevice? device = null;
        foreach (var library in Libraries)
        {
            device ??= !WithoutChanger.Contains(library) && library.Changer.Serial == serial ? library.Changer : null;
            device ??= library.Drives.FirstOrDefault(d => d.Serial == serial);
        }

        if (device is null || Busy.Contains(serial))
        {
            throw new IOException($"No tape device with serial {serial} is attached to {Name}.");
        }

        if (!_open.TryAdd(serial, true))
        {
            throw new IOException($"{device.Path} (serial {serial}) on {Name} is open already (EBUSY): a lock is missing.");
        }

        Opens++;
        return Task.FromResult<IScsiDevice>(new Opened(device, () => _open.TryRemove(serial, out _)));
    }

    public bool IsOpen(string serial) => _open.ContainsKey(serial);

    private ScsiDeviceInfo Info(string path, byte type, string vendor, string product, string serial, string? designator = null) =>
        Busy.Contains(serial)
            ? new ScsiDeviceInfo(path, type, vendor, product, "1.0", null, [], "fake connection")
            : new ScsiDeviceInfo(path, type, vendor, product, "1.0", serial, designator is null ? [] : ["t10:" + designator], "fake connection");

    private sealed class Opened(IScsiDevice inner, Action closed) : IScsiDevice
    {
        private int _disposed;

        public string Path => inner.Path;

        public int MaxTransferLength => inner.MaxTransferLength;

        public ValueTask<ScsiResult> ExecuteAsync(ScsiCommand command, CancellationToken cancellationToken) =>
            Volatile.Read(ref _disposed) != 0 ? throw new ObjectDisposedException(Path) : inner.ExecuteAsync(command, cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                closed();
            }

            return ValueTask.CompletedTask;
        }
    }
}
