using System.Globalization;

namespace Backdafuckup.Mover;

internal enum MoverMode
{
    /// <summary>Read the device and send it to the server.</summary>
    Backup,

    /// <summary>Fetch an image from the server and write it to the device (a new, empty volume).</summary>
    Restore,

    /// <summary>Fetch files from the server and put them into the mounted, live volume (<see cref="MoverOptions.Root"/>).</summary>
    Files,

    /// <summary>Read the mounted volume (<see cref="MoverOptions.Root"/>) file by file and send the tree to the server.</summary>
    Tree,
}

/// <summary>
/// The mover's settings, from the environment of its pod:
/// <list type="bullet">
/// <item><c>BDFU_MOVER_MODE</c>: <c>backup</c>, <c>restore</c>, <c>files</c> or <c>tree</c>.</item>
/// <item><c>BDFU_MOVER_DATA_URL</c>, <c>BDFU_MOVER_RESULT_URL</c>: the channel on the backup server.</item>
/// <item><c>BDFU_MOVER_TOKEN</c>: the channel's one-time token (from a Secret).</item>
/// <item><c>BDFU_MOVER_SERVER_PINS</c>: SHA-256 SPKI pins of the server certificate, comma-separated; empty: the
/// system's CA store.</item>
/// <item><c>BDFU_MOVER_DEVICE</c>: the block device (or file) of the volume; default <see cref="DefaultDevice"/>.</item>
/// <item><c>BDFU_MOVER_BLOCK_BYTES</c>: the granularity of zero detection; default 64 KiB.</item>
/// <item><c>BDFU_MOVER_BYTES</c>: backup only: the bytes to send, from the start of the device (a volume restored from a
/// snapshot may be larger than the snapshot); default the whole device.</item>
/// <item><c>BDFU_MOVER_ROOT</c>: files and tree: where the volume is mounted; default <see cref="DefaultRoot"/>.</item>
/// <item><c>BDFU_MOVER_PREVIOUS_URL</c>: tree only: where the previous run's tree is (an incremental read); unset: everything is read.</item>
/// <item><c>BDFU_MOVER_IF_EXISTS</c>: files only: <c>replace</c> (default) or <c>keep</c> (RESTORED-&lt;name&gt; next to it).</item>
/// <item><c>BDFU_MOVER_OWNERS</c>: files only: <c>1</c> (default) owner and group from the backup, <c>0</c> those of
/// the folder an item goes into.</item>
/// </list>
/// </summary>
internal sealed record MoverOptions(
    MoverMode Mode,
    Uri DataUrl,
    Uri ResultUrl,
    string Token,
    IReadOnlyList<string> ServerPins,
    string Device,
    int BlockBytes = MoverOptions.DefaultBlockBytes,
    long? Bytes = null,
    string Root = MoverOptions.DefaultRoot,
    bool Keep = false,
    bool Owners = true,
    Uri? PreviousUrl = null,
    TimeSpan? Racy = null)
{
    public const string DefaultDevice = "/dev/bdfu-volume";
    public const string DefaultRoot = "/bdfu-volume";
    public const int DefaultBlockBytes = 64 * 1024;

    public static MoverOptions FromEnvironment(Func<string, string?> get)
    {
        string Required(string name) =>
            get(name) is { Length: > 0 } value ? value : throw new ArgumentException($"The environment variable {name} is not set.");

        Uri Url(string name) =>
            Uri.TryCreate(Required(name), UriKind.Absolute, out var url) && url.Scheme is "https" or "http"
                ? url
                : throw new ArgumentException($"{name} is not an http(s) URL: {get(name)}");

        var mode = Required("BDFU_MOVER_MODE").ToLowerInvariant() switch
        {
            "backup" => MoverMode.Backup,
            "restore" => MoverMode.Restore,
            "files" => MoverMode.Files,
            "tree" => MoverMode.Tree,
            var other => throw new ArgumentException($"BDFU_MOVER_MODE is '{other}', expected backup, restore, files or tree."),
        };
        var pins = (get("BDFU_MOVER_SERVER_PINS") ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var block = get("BDFU_MOVER_BLOCK_BYTES") is { Length: > 0 } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 4096 and <= 4 * 1024 * 1024 && (parsed & (parsed - 1)) == 0
                ? parsed
                : throw new ArgumentException($"BDFU_MOVER_BLOCK_BYTES is '{text}', expected a power of two from 4096 to 4194304.")
            : DefaultBlockBytes;
        return new MoverOptions(
            mode,
            Url("BDFU_MOVER_DATA_URL"),
            Url("BDFU_MOVER_RESULT_URL"),
            Required("BDFU_MOVER_TOKEN"),
            pins,
            get("BDFU_MOVER_DEVICE") is { Length: > 0 } device ? device : DefaultDevice,
            block,
            get("BDFU_MOVER_BYTES") is { Length: > 0 } bytes
                ? long.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
                    ? count
                    : throw new ArgumentException($"BDFU_MOVER_BYTES is '{bytes}', expected a positive number of bytes.")
                : null,
            get("BDFU_MOVER_ROOT") is { Length: > 0 } root
                ? root.StartsWith('/') ? root.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException("BDFU_MOVER_ROOT is /, expected the folder the volume is mounted at.")
                    : throw new ArgumentException($"BDFU_MOVER_ROOT is '{root}', expected an absolute folder.")
                : DefaultRoot,
            (get("BDFU_MOVER_IF_EXISTS") ?? "replace").ToLowerInvariant() switch
            {
                "" or "replace" => false,
                "keep" => true,
                var other => throw new ArgumentException($"BDFU_MOVER_IF_EXISTS is '{other}', expected replace or keep."),
            },
            (get("BDFU_MOVER_OWNERS") ?? "1").ToLowerInvariant() switch
            {
                "" or "1" or "true" => true,
                "0" or "false" => false,
                var other => throw new ArgumentException($"BDFU_MOVER_OWNERS is '{other}', expected 1 or 0."),
            },
            get("BDFU_MOVER_PREVIOUS_URL") is { Length: > 0 } ? Url("BDFU_MOVER_PREVIOUS_URL") : null);
    }
}
