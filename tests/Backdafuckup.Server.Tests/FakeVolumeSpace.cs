using System.Collections.Concurrent;
using Backdafuckup.Server.ScaleOut;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// Volumes for scale-out tests: each directory reports the space a test gave it (default 100 GiB free of 200 GiB) and is
/// offline when the test says so or when it does not exist.
/// </summary>
internal sealed class FakeVolumeSpace : IVolumeSpace
{
    private readonly ConcurrentDictionary<string, (long Total, long Free)> _space = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _offline = new(StringComparer.Ordinal);

    public void Set(string path, long total, long free) => _space[Key(path)] = (total, free);

    public void SetOffline(string path, bool offline) => _offline[Key(path)] = offline;

    public (long Total, long Free)? Probe(string path) =>
        _offline.GetValueOrDefault(Key(path)) || !Directory.Exists(path) ? null : _space.GetValueOrDefault(Key(path), (200L << 30, 100L << 30));

    public string? VolumeOf(string path) => Key(path);

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
