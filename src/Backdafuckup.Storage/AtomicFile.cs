using System.Runtime.InteropServices;

namespace Backdafuckup.Storage;

/// <summary>Crash-safe file writes: write to a temp file on the same file system, flush to disk, rename.</summary>
internal static partial class AtomicFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically.</summary>
    /// <returns>False if <paramref name="path"/> already existed and <paramref name="overwrite"/> is false.</returns>
    public static async Task<bool> WriteAsync(string tempDirectory, string path, ReadOnlyMemory<byte> content, bool overwrite, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, path, overwrite);
                return true;
            }
            catch (IOException) when (!overwrite && File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Makes directory entries (created/renamed files) durable. No-op on Windows.</summary>
    public static void SyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fd = Open(directory, 0 /* O_RDONLY */);
        if (fd < 0)
        {
            return;
        }

        try
        {
            _ = Fsync(fd);
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
