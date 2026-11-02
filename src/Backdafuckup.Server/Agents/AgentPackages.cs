using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Agents;

/// <summary>One agent build as shipped with the server.</summary>
public sealed record AgentBuild(string Name, string Directory, string Version, IReadOnlyList<AgentFile> Files, string FilesHash)
{
    public AgentUpdateOffer ToOffer() => new() { Build = Name, Version = Version, FilesHash = FilesHash, Files = [.. Files] };
}

/// <summary>
/// The agent builds next to the server: <c>agent/net10</c> (self-contained, preferred) and <c>agent/net48</c>
/// (fallback for systems where .NET 10 does not run). Built by <c>scripts/build-agent.sh</c>.
/// </summary>
public sealed class AgentPackages(IOptions<AgentOptions> options, IHostEnvironment environment)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTime Stamp, AgentBuild Build)> _cache = [];

    public string Root => options.Value.PackageDirectory is { Length: > 0 } configured
        ? Path.GetFullPath(configured, environment.ContentRootPath)
        : Path.Combine(AppContext.BaseDirectory, "agent");

    /// <summary>The build, or null if it is not available on this server.</summary>
    public AgentBuild? Get(string build)
    {
        if (build is not (AgentProtocol.BuildNet10 or AgentProtocol.BuildNet48))
        {
            return null;
        }

        var directory = Path.Combine(Root, build);
        var exe = Path.Combine(directory, AgentProtocol.ExecutableName);
        if (!File.Exists(exe))
        {
            return null;
        }

        var stamp = Directory.EnumerateFiles(directory).Select(File.GetLastWriteTimeUtc).Max();
        lock (_lock)
        {
            if (_cache.TryGetValue(build, out var cached) && cached.Stamp == stamp)
            {
                return cached.Build;
            }
        }

        var files = Directory.EnumerateFiles(directory)
            .Where(f => !Path.GetFileName(f).StartsWith('.') && !string.Equals(Path.GetFileName(f), "version.txt", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f =>
            {
                using var stream = File.OpenRead(f);
                return new AgentFile { Name = Path.GetFileName(f), Size = stream.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
            })
            .ToList();
        var versionFile = Path.Combine(directory, "version.txt");
        var version = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "dev";
        var result = new AgentBuild(build, directory, version, files, FilesHash(files));
        lock (_lock)
        {
            _cache[build] = (stamp, result);
        }

        return result;
    }

    /// <summary>Path of a file of a build, or null (only names listed in the build, no path traversal).</summary>
    public string? FilePath(string build, string name) =>
        Get(build) is { } b && b.Files.Any(f => f.Name == name) ? Path.Combine(b.Directory, name) : null;

    /// <summary>The same hash as the agent computes over its installed files.</summary>
    public static string FilesHash(IEnumerable<AgentFile> files)
    {
        var text = string.Join("\n", files.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => $"{f.Name}:{f.Sha256}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Setup package for a group policy startup script or a software distribution: both builds, <c>agent.json</c> and
    /// <c>Install-Agent.cmd</c>, which installs the .NET 10 build and falls back to the .NET Framework 4.8 build.
    /// </summary>
    public async Task WriteSetupZipAsync(Stream output, AgentConfig config, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var build in new[] { AgentProtocol.BuildNet10, AgentProtocol.BuildNet48 }.Select(Get).OfType<AgentBuild>())
        {
            foreach (var file in build.Files)
            {
                await using var source = File.OpenRead(Path.Combine(build.Directory, file.Name));
                await using var entry = await zip.CreateEntry($"{build.Name}/{file.Name}", CompressionLevel.Optimal).OpenAsync(cancellationToken).ConfigureAwait(false);
                await source.CopyToAsync(entry, cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteTextAsync(zip, AgentProtocol.ConfigFile, JsonSerializer.Serialize(config, AgentJson.Options), cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(zip, "Install-Agent.cmd", InstallScript, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
        await entry.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs as administrator or Local System. Returns at once if the agent is installed (it updates itself).</summary>
    internal const string InstallScript = """
        @echo off
        rem backdafuckup agent setup: installs the agent service "BackdafuckupAgent" from this folder.
        rem Usage: Install-Agent.cmd [--force]   (as administrator or Local System, e.g. group policy startup script)
        setlocal
        sc query BackdafuckupAgent >nul 2>&1
        if %errorlevel%==0 if /i not "%~1"=="--force" (
          echo backdafuckup agent is already installed; it updates itself from the server.
          exit /b 0
        )
        "%~dp0net10\backdafuckup-agent.exe" install --source "%~dp0net10" --config "%~dp0agent.json"
        if %errorlevel%==0 exit /b 0
        echo The .NET 10 build could not be installed (exit code %errorlevel%); trying the .NET Framework 4.8 build.
        "%~dp0net48\backdafuckup-agent.exe" install --source "%~dp0net48" --config "%~dp0agent.json"
        exit /b %errorlevel%
        """;
}

/// <summary>JSON settings of the agent API (property names as declared, case-insensitive on input).</summary>
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
