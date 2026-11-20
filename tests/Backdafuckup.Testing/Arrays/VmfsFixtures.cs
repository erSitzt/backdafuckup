using Backdafuckup.Core.DataMover;

namespace Backdafuckup.Testing.Arrays;

/// <summary>
/// The VMFS6 images in tests/fixtures/vmfs6, written by ESXi and taken from the OpenVmfs repository (Apache-2.0). They are
/// partial: <c>NAME.ranges</c> lists the captured byte ranges of the device ("size BYTES", then "OFFSET LENGTH" lines),
/// <c>NAME.parts.zst</c> holds their bytes in that order. Reading outside them is an error, as in OpenVmfs's own tests.
/// </summary>
public static class VmfsFixtures
{
    private static readonly Lock Gate = new();

    public static string Directory { get; } = Path.Combine(Path.GetDirectoryName(FsFixtures.Directory)!, "vmfs6");

    /// <summary>The hashes ESXi computed (sha256sum format), by path in the datastore.</summary>
    public static IReadOnlyDictionary<string, string> Truth(string name) =>
        File.ReadAllLines(Path.Combine(Directory, name, "truth", "sha256.txt")).Where(l => l.Length > 0)
            .Select(l => l.Split("  ", 2)).ToDictionary(p => p[1].Trim(), p => p[0], StringComparer.Ordinal);

    /// <summary>The device of a partial image, unpacked once per test run.</summary>
    public static PartialImage Open(string name, int maxRequestBytes = 1024 * 1024, int maxConcurrentRequests = 4)
    {
        var lines = File.ReadAllLines(Path.Combine(Directory, name + ".ranges")).Where(l => l.Length > 0).ToList();
        var size = long.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var ranges = new List<(long Offset, long Length, long At)>();
        long at = 0;
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ');
            var (offset, length) = (long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            ranges.Add((offset, length, at));
            at += length;
        }

        return new PartialImage(name, size, ranges, Unpack(name, at), maxRequestBytes, maxConcurrentRequests);
    }

    private static string Unpack(string name, long expected)
    {
        var source = Path.Combine(Directory, name + ".parts.zst");
        var target = Path.Combine(Path.GetTempPath(), "bdfu-vmfs-fixtures", name + ".parts");
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
                input.CopyTo(output);
                if (output.Length != expected)
                {
                    throw new InvalidDataException($"{source} unpacks to {output.Length} bytes, its ranges list {expected}");
                }
            }

            File.WriteAllText(marker, stamp);
            return target;
        }
    }

    /// <summary>A partial device image as a LUN reader; counts the bytes read.</summary>
    public sealed class PartialImage(string name, long size, IReadOnlyList<(long Offset, long Length, long At)> ranges, string parts, int maxRequestBytes, int maxConcurrentRequests)
        : IDiskReader
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _file = File.OpenHandle(parts);
        private long _bytesRead;

        public long Capacity => size;

        public string TransportName => "fixture";

        public int MaxRequestBytes => maxRequestBytes;

        public int MaxConcurrentRequests => maxConcurrentRequests;

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public async ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.Length > maxRequestBytes)
            {
                throw new IOException($"{name}: a read of {destination.Length} bytes exceeds the limit of {maxRequestBytes}");
            }

            var done = 0;
            while (done < destination.Length)
            {
                var position = offset + done;
                var range = ranges.FirstOrDefault(r => r.Offset <= position && position < r.Offset + r.Length);
                if (range.Length == 0)
                {
                    throw new IOException($"{name}: byte {position} lies outside the captured ranges");
                }

                var count = (int)Math.Min(destination.Length - done, range.Offset + range.Length - position);
                var read = await RandomAccess.ReadAsync(_file, destination.Slice(done, count), range.At + (position - range.Offset), cancellationToken);
                if (read != count)
                {
                    throw new IOException($"{name}: short read at {position}");
                }

                done += count;
            }

            Interlocked.Add(ref _bytesRead, destination.Length);
        }

        public ValueTask DisposeAsync()
        {
            _file.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
