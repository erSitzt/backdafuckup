using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace Backdafuckup.Server.Movers;

/// <summary>A blob of an image as the registry stores it: content, its SHA-256 digest and its media type.</summary>
public sealed record RegistryBlob(string Digest, string MediaType, byte[] Content)
{
    public long Size => Content.LongLength;

    public static RegistryBlob Of(string mediaType, byte[] content) => new(DigestOf(content), mediaType, content);

    public static string DigestOf(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
}

/// <summary>
/// The mover image ready for a registry: config and layers as blobs, and the manifest (Docker image manifest v2, schema 2,
/// which every registry and container runtime takes) with its digest, by which the pods reference the image.
/// </summary>
public sealed record MoverImageBundle(string Version, RegistryBlob Config, IReadOnlyList<RegistryBlob> Layers, byte[] Manifest)
{
    public const string ManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    public const string ConfigMediaType = "application/vnd.docker.container.image.v1+json";
    public const string GzipLayerMediaType = "application/vnd.docker.image.rootfs.diff.tar.gzip";
    public const string TarLayerMediaType = "application/vnd.docker.image.rootfs.diff.tar";

    public string ManifestDigest { get; } = RegistryBlob.DigestOf(Manifest);

    public long Size => Config.Size + Layers.Sum(l => l.Size);

    /// <summary>The tag pushed with the image: the version and the start of the digest (a new build never overwrites a tag).</summary>
    public string Tag => $"{Version.Replace('+', '-')}-{ManifestDigest["sha256:".Length..][..12]}";

    /// <summary>Reads the archive the .NET SDK writes (<c>PublishContainer</c> with <c>ContainerArchiveOutputPath</c>): a tar with manifest.json, the config and the layers.</summary>
    public static MoverImageBundle Read(Stream archive, string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var reader = new TarReader(archive, leaveOpen: true))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is { } data)
                {
                    using var copy = new MemoryStream();
                    data.CopyTo(copy);
                    entries[entry.Name.TrimStart('.', '/')] = copy.ToArray();
                }
            }
        }

        if (!entries.TryGetValue("manifest.json", out var manifestJson))
        {
            throw new InvalidDataException($"The mover image archive {path} has no manifest.json (not an image archive of the .NET SDK).");
        }

        using var manifest = JsonDocument.Parse(manifestJson);
        var image = manifest.RootElement.EnumerateArray().FirstOrDefault();
        if (image.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The manifest.json of the mover image archive {path} lists no image.");
        }

        byte[] Entry(string name) => entries.TryGetValue(name, out var content)
            ? content
            : throw new InvalidDataException($"The mover image archive {path} lacks {name}, which its manifest.json names.");

        var config = RegistryBlob.Of(ConfigMediaType, Entry(image.GetProperty("Config").GetString() ?? string.Empty));
        var layers = image.GetProperty("Layers").EnumerateArray()
            .Select(l => Entry(l.GetString() ?? string.Empty))
            .Select(content => RegistryBlob.Of(content is [0x1f, 0x8b, ..] ? GzipLayerMediaType : TarLayerMediaType, content))
            .ToList();
        var tag = image.TryGetProperty("RepoTags", out var tags) && tags.EnumerateArray().FirstOrDefault().GetString() is { } repoTag && repoTag.LastIndexOf(':') is > 0 and var colon
            ? repoTag[(colon + 1)..]
            : "unknown";
        return new MoverImageBundle(tag, config, layers, WriteManifest(config, layers));
    }

    private static byte[] WriteManifest(RegistryBlob config, IReadOnlyList<RegistryBlob> layers)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 2);
            json.WriteString("mediaType", ManifestMediaType);
            json.WritePropertyName("config");
            Descriptor(json, config);
            json.WriteStartArray("layers");
            foreach (var layer in layers)
            {
                Descriptor(json, layer);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();

        static void Descriptor(Utf8JsonWriter json, RegistryBlob blob)
        {
            json.WriteStartObject();
            json.WriteString("mediaType", blob.MediaType);
            json.WriteNumber("size", blob.Size);
            json.WriteString("digest", blob.Digest);
            json.WriteEndObject();
        }
    }
}

/// <summary>The mover image the server ships (<c>mover/backdafuckup-mover.tar</c>, or <c>Mover:ImageArchive</c>), read once per file version.</summary>
public sealed class MoverImage(IConfiguration configuration, IHostEnvironment environment)
{
    public const string ArchiveName = "backdafuckup-mover.tar";
    private readonly Lock _gate = new();
    private (string Path, DateTime Stamp, MoverImageBundle Bundle)? _cache;

    public string ArchivePath => configuration["Mover:ImageArchive"] is { Length: > 0 } configured
        ? Path.GetFullPath(configured, environment.ContentRootPath)
        : Path.Combine(AppContext.BaseDirectory, "mover", ArchiveName);

    /// <summary>The image, or null with the reason (no archive shipped, or unreadable).</summary>
    public MoverImageBundle? TryGet(out string? problem)
    {
        var path = ArchivePath;
        if (!File.Exists(path))
        {
            problem = $"The server has no mover image: {path} is missing (built by scripts/build-mover.sh, shipped by the deployment).";
            return null;
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_cache is { } cached && cached.Path == path && cached.Stamp == stamp)
            {
                problem = null;
                return cached.Bundle;
            }
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bundle = MoverImageBundle.Read(stream, path);
            lock (_gate)
            {
                _cache = (path, stamp, bundle);
            }

            problem = null;
            return bundle;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            problem = $"The mover image {path} cannot be read: {ex.Message}";
            return null;
        }
    }
}
