using System.Collections.Concurrent;
using Backdafuckup.Proxy;

namespace Backdafuckup.Testing.Proxy;

/// <summary>
/// Block devices of a proxy as files: a device "attached" with <see cref="Attach"/> appears after the next rescan (like a
/// hot-added disk), and a deleted one disappears.
/// </summary>
public sealed class FileBlockDevices : IBlockDevices
{
    private readonly ConcurrentDictionary<string, (string File, string Serial, bool Visible)> _devices = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Deleted { get; } = new();

    public int Rescans;

    /// <summary>A disk the hypervisor attached; the agent sees it after a rescan.</summary>
    public void Attach(string name, string file, string serial) => _devices[name] = (file, DeviceSerials.Normalize(serial), false);

    public Task<IReadOnlyList<ProxyDevice>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProxyDevice>>([.. _devices.Where(d => d.Value.Visible).OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new ProxyDevice(d.Key, d.Value.File, new FileInfo(d.Value.File).Length, d.Value.Serial, "Virtual disk"))]);

    public Task RescanAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Rescans);
        foreach (var name in _devices.Keys)
        {
            _devices.AddOrUpdate(name, _ => default, (_, d) => d with { Visible = true });
        }

        return Task.CompletedTask;
    }

    public Task<IOpenDevice> OpenAsync(ProxyDevice device, bool writable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        return Task.FromResult<IOpenDevice>(new FileDevice(device, File.OpenHandle(device.Path, FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite), writable));
    }

    public Task DeleteAsync(ProxyDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices.TryRemove(device.Name, out _);
        Deleted.Enqueue(device.Name);
        return Task.CompletedTask;
    }
}
