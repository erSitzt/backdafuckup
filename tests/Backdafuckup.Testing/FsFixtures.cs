namespace Backdafuckup.Testing;

/// <summary>The file system test images in tests/fixtures/fs-images (built by make-fs-images.sh).</summary>
public static class FsFixtures
{
    private static readonly Lock Gate = new();

    public static string Directory { get; } = FindDirectory();

    /// <summary>The decompressed (sparse) image of <paramref name="name"/>, cached per test run.</summary>
    public static string Image(string name)
    {
        var source = Path.Combine(Directory, name + ".img.zst");
        var target = Path.Combine(Path.GetTempPath(), "bdfu-fs-fixtures", name + ".img");
        var marker = target + ".ok";
        var stamp = $"{new FileInfo(source).Length}:{File.GetLastWriteTimeUtc(source).Ticks}";
        lock (Gate)
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp && File.Exists(target))
            {
                return target;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = new ZstdSharp.DecompressionStream(File.OpenRead(source)))
            using (var output = File.Create(target))
            {
                var buffer = new byte[1 << 20];
                long length = 0;
                while (true)
                {
                    var n = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    if (n == 0)
                    {
                        break;
                    }

                    if (buffer.AsSpan(0, n).ContainsAnyExcept((byte)0))
                    {
                        output.Write(buffer, 0, n);
                    }
                    else
                    {
                        output.Seek(n, SeekOrigin.Current);
                    }

                    length += n;
                }

                output.SetLength(length);
            }

            File.WriteAllText(marker, stamp);
            return target;
        }
    }

    /// <summary>Lines of a manifest: type, sha256 or link target, size, mode, uid, gid, mtime, path.</summary>
    public static IReadOnlyList<string[]> Manifest(string name) =>
        [.. File.ReadAllLines(Path.Combine(Directory, name + ".manifest")).Where(l => l.Length > 0).Select(l => l.Split('\t'))];

    /// <summary>Above the test binaries, or (for a separate output folder, --artifacts-path) next to this source file.</summary>
    private static string FindDirectory()
    {
        var starts = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(SourceFile()) };
        foreach (var start in starts.OfType<string>())
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "fs-images");
                if (File.Exists(Path.Combine(dir.FullName, "backdafuckup.slnx")) && System.IO.Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"tests/fixtures/fs-images not found above {AppContext.BaseDirectory} or {SourceFile()}");
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
