using System.Runtime.InteropServices;

namespace Backdafuckup.Storage;

/// <summary>Flushes all dirty data of the file system that contains a path (Linux: syncfs). No-op elsewhere.</summary>
internal static partial class FileSystemSync
{
    public static void SyncFileSystem(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fd = Open(path, 0 /* O_RDONLY */);
        if (fd < 0)
        {
            return;
        }

        try
        {
            if (SyncFs(fd) != 0)
            {
                throw new IOException($"syncfs failed for '{path}' (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = Close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "syncfs", SetLastError = true)]
    private static partial int SyncFs(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);
}
