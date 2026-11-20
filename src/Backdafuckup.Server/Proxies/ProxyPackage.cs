using Backdafuckup.Core.Proxies;
using Backdafuckup.Proxy;

namespace Backdafuckup.Server.Proxies;

/// <summary>The proxy agent's program file the server offers, with its SHA-256 for the self-update.</summary>
public sealed record ProxyProgram(string Path, long SizeBytes, string Sha256, string? Version);

/// <summary>
/// The proxy agent package next to the server (<c>proxy/</c>, or Proxy:PackageDirectory): the Linux program and its
/// systemd unit, the Windows program, and <c>version.txt</c>. Each agent is offered the build of its own platform.
/// </summary>
public sealed class ProxyPackage(IConfiguration configuration)
{
    public static readonly string[] Files = [ProxySelfUpdate.ProgramName, ProxySelfUpdate.WindowsProgramName, "backdafuckup-proxy.service"];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (long Size, DateTime Written, string Sha256)> _hashed = new(StringComparer.Ordinal);

    public string Directory => System.IO.Path.GetFullPath(configuration["Proxy:PackageDirectory"] is { Length: > 0 } configured ? configured : "proxy", AppContext.BaseDirectory);

    public string? Version => System.IO.Path.Combine(Directory, "version.txt") is var file && File.Exists(file) ? File.ReadAllText(file).Trim() : null;

    /// <summary>
    /// The program file of a platform (<see cref="ProxyPlatforms"/>; null: Linux), or null when the package has none; its
    /// hash is computed once per file version.
    /// </summary>
    public ProxyProgram? Program(string? platform = null)
    {
        var info = new FileInfo(System.IO.Path.Combine(Directory, ProxySelfUpdate.ProgramFile(platform)));
        if (!info.Exists)
        {
            return null;
        }

        lock (_lock)
        {
            if (!_hashed.TryGetValue(info.FullName, out var known) || known.Size != info.Length || known.Written != info.LastWriteTimeUtc)
            {
                _hashed[info.FullName] = known = (info.Length, info.LastWriteTimeUtc, ProxySelfUpdate.HashOf(info.FullName));
            }

            return new ProxyProgram(info.FullName, info.Length, known.Sha256, Version);
        }
    }
}
