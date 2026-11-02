using System.Security.Cryptography;
using Backdafuckup.FileLevel;

namespace Backdafuckup.FileLevel.Tests;

/// <summary>The file system test images in tests/fixtures/fs-images (built by make-fs-images.sh).</summary>
internal static class Fixtures
{
    public static string Directory => Backdafuckup.Testing.FsFixtures.Directory;

    public static string Image(string name) => Backdafuckup.Testing.FsFixtures.Image(name);

    public static GuestDiskSource Disk(string name, string key = "0") =>
        new(key, name, () => new FileStream(Image(name), FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess));

    public static IReadOnlyList<ManifestEntry> Manifest(string name) =>
        [.. File.ReadAllLines(Path.Combine(Directory, name + ".manifest")).Where(l => l.Length > 0).Select(ManifestEntry.Parse)];

    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}

/// <summary>One line of a fixture manifest: type, sha256 or link target, size, mode, uid, gid, mtime, path.</summary>
internal sealed record ManifestEntry(string Type, string Hash, long Size, string Mode, string Uid, string Gid, string Mtime, string Path)
{
    public static ManifestEntry Parse(string line)
    {
        var f = line.Split('\t');
        return new ManifestEntry(f[0], f[1], long.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture), f[3], f[4], f[5], f[6], f[7]);
    }
}
