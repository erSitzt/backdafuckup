using System.Buffers;
using System.Reflection;
using Backdafuckup.Core.Arrays;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Core.Extents;
using OpenVmfs;

namespace Backdafuckup.Vmfs;

/// <summary>
/// The VMFS6 reader on OpenVmfs (Apache-2.0). Opening a volume or a disk reads metadata through OpenVmfs's blocking device
/// reads; that runs on threads of its own, a few at a time, so the server's thread pool never waits on the LUN. The data of
/// a disk is located with <see cref="VmdkDisk.Map"/> and read asynchronously from the LUN, zero ranges without reading it.
/// </summary>
public sealed class OpenVmfsReader : IVmfsReader
{
    /// <summary>Volumes and disks opened at the same time (each holds a thread while OpenVmfs reads metadata).</summary>
    public const int MaxConcurrentOpens = 8;

    private readonly SemaphoreSlim _opens = new(MaxConcurrentOpens);

    /// <summary>The OpenVmfs version in this build. It is part of the format text, so a new one starts verify mode again.</summary>
    public static string LibraryVersion { get; } = typeof(VmfsVolume).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(VmfsVolume).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public async Task<IVmfsVolume> OpenVolumeAsync(IDiskReader lun, string where, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lun);
        var volume = await BlockingAsync(() => VmfsVolume.Open(new DiskReaderDevice(lun), new VmfsOpenOptions()), where, cancellationToken).ConfigureAwait(false);
        return new Volume(this, volume, lun, where);
    }

    /// <summary>Runs a blocking OpenVmfs call on a thread of its own; its refusals become <see cref="VmfsRefusedException"/>.</summary>
    private async Task<T> BlockingAsync<T>(Func<T> call, string what, CancellationToken cancellationToken)
    {
        await _opens.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        return call();
                    }
                    catch (VmfsException ex)
                    {
                        throw Refusal(what, ex);
                    }
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            _opens.Release();
        }
    }

    /// <summary>OpenVmfs's error kinds as fallback reasons; an I/O error keeps the LUN reader's exception.</summary>
    internal static VmfsRefusedException Refusal(string what, VmfsException ex) => new(
        $"{what}: {ex.Message}",
        ex.Kind switch
        {
            VmfsErrorKind.Unsupported => VmfsRefusalKinds.Unsupported,
            VmfsErrorKind.InUse => VmfsRefusalKinds.InUse,
            VmfsErrorKind.NotFound => VmfsRefusalKinds.NotFound,
            VmfsErrorKind.Corrupt => VmfsRefusalKinds.Corrupt,
            VmfsErrorKind.Io => VmfsRefusalKinds.Io,
            _ => VmfsRefusalKinds.Other,
        },
        ex.Kind == VmfsErrorKind.Io && ex.InnerException is not null ? ex.InnerException : ex);

    private sealed class Volume(OpenVmfsReader owner, VmfsVolume volume, IDiskReader lun, string where) : IVmfsVolume
    {
        public string FormatVersion { get; } = $"VMFS {volume.Version} (OpenVmfs {LibraryVersion})";

        public string Label { get; } = volume.Label;

        public async Task<IVmfsDisk> OpenDiskAsync(string descriptorPath, CancellationToken cancellationToken)
        {
            var disk = await owner.BlockingAsync(() => volume.OpenDisk(descriptorPath), $"{descriptorPath} on {where}", cancellationToken).ConfigureAwait(false);
            return new OpenVmfsDisk(disk, lun);
        }

        public ValueTask DisposeAsync()
        {
            volume.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A virtual disk on the snapshot LUN: each read is mapped by OpenVmfs and fetched from the LUN by the caller's reader.</summary>
internal sealed class OpenVmfsDisk(VmdkDisk disk, IDiskReader lun) : IVmfsDisk
{
    private IReadOnlyList<DiskExtent>? _allocated;

    public long Capacity => disk.Capacity;

    public string TransportName => "array-vmfs";

    public int MaxRequestBytes => Math.Max(lun.MaxRequestBytes, 1024 * 1024);

    public int MaxConcurrentRequests => lun.MaxConcurrentRequests;

    public IReadOnlyList<DiskExtent> AllocatedExtents => _allocated ??= [.. disk.DataExtents.Select(e => new DiskExtent(e.Offset, e.Length))];

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset < 0 || offset + destination.Length > disk.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"{destination.Length} bytes at {offset} lie outside the disk ({disk.Capacity} bytes)");
        }

        var max = Math.Max(4096, lun.MaxRequestBytes);
        var end = offset + destination.Length;
        for (var position = offset; position < end;)
        {
            IReadOnlyList<MappedRange> ranges;
            try
            {
                ranges = disk.Map(position, end - position);
            }
            catch (VmfsException ex)
            {
                throw new IOException($"OpenVmfs could not map {end - position} bytes of the disk at {position}: {ex.Message}", ex);
            }

            if (ranges.Count == 0 || ranges[0].Offset != position)
            {
                throw new IOException($"OpenVmfs mapped no range at {position} of the disk");
            }

            foreach (var range in ranges)
            {
                var length = (int)Math.Min(range.Length, end - range.Offset);
                var target = destination.Slice((int)(range.Offset - offset), length);
                if (range.IsZero)
                {
                    target.Span.Clear();
                }
                else if (range.Device != 0)
                {
                    throw new IOException($"OpenVmfs mapped {position} of the disk to device {range.Device}; a datastore on one LUN has device 0 only");
                }
                else
                {
                    for (var done = 0; done < target.Length; done += max)
                    {
                        await lun.ReadAsync(range.DeviceOffset + done, target.Slice(done, Math.Min(max, target.Length - done)), cancellationToken).ConfigureAwait(false);
                    }
                }

                position = range.Offset + length;
            }
        }
    }

    public ValueTask DisposeAsync() => disk.DisposeAsync();
}

/// <summary>
/// The snapshot LUN as OpenVmfs's device: a blocking read over the asynchronous LUN reader, split to its request limit. Only
/// metadata goes through here, on the reader's own threads.
/// </summary>
internal sealed class DiskReaderDevice(IDiskReader lun) : IVmfsDevice
{
    public long Length => lun.Capacity;

    public void Read(long offset, Span<byte> destination)
    {
        var max = Math.Max(4096, lun.MaxRequestBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(destination.Length, max));
        try
        {
            while (!destination.IsEmpty)
            {
                var count = Math.Min(destination.Length, max);
                lun.ReadAsync(offset, buffer.AsMemory(0, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                buffer.AsSpan(0, count).CopyTo(destination);
                destination = destination[count..];
                offset += count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
