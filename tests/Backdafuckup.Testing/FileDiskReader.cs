using Backdafuckup.Core.DataMover;
using Microsoft.Win32.SafeHandles;

namespace Backdafuckup.Testing;

/// <summary>A disk image file as a thread-safe random-access reader (for large, sparse test images).</summary>
public sealed class FileDiskReader : IDiskReader
{
    private readonly SafeFileHandle _handle;

    public FileDiskReader(string path, int maxRequestBytes = 4 * 1024 * 1024)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Capacity = RandomAccess.GetLength(_handle);
        MaxRequestBytes = maxRequestBytes;
    }

    public long Capacity { get; }

    public string TransportName => "file";

    public int MaxRequestBytes { get; }

    public int MaxConcurrentRequests => 8;

    public ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = 0;
        while (done < destination.Length)
        {
            var n = RandomAccess.Read(_handle, destination.Span[done..], offset + done);
            if (n == 0)
            {
                destination.Span[done..].Clear();
                break;
            }

            done += n;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
