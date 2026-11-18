namespace Backdafuckup.Lvhd;

/// <summary>Random reads from a device or image (a snapshot LUN on the proxy, or a file in tests).</summary>
public interface IBlockSource
{
    long Size { get; }

    /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/>; throws if the range is outside the source.</summary>
    ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken);
}

/// <summary>A byte range of another source (a logical volume on the physical volume).</summary>
public sealed class BlockRange(IBlockSource inner, long offset, long length) : IBlockSource
{
    public long Size { get; } = length;

    public ValueTask ReadAsync(long offset1, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (offset1 < 0 || offset1 + destination.Length > Size)
        {
            throw new ArgumentOutOfRangeException(nameof(offset1), $"Read {offset1}+{destination.Length} is outside the range of {Size} bytes.");
        }

        return inner.ReadAsync(offset + offset1, destination, cancellationToken);
    }
}

/// <summary>A file (or block device node) read with positional reads.</summary>
public sealed class FileBlockSource(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long size) : IBlockSource
{
    public long Size { get; } = size;

    public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, destination[done..], offset + done, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Read {offset + done} is past the end of the source ({Size} bytes).");
            }

            done += read;
        }
    }
}
