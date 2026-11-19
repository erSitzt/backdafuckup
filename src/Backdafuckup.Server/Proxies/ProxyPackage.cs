using Backdafuckup.Proxy;

namespace Backdafuckup.Server.Proxies;

/// <summary>The proxy agent's program file the server offers, with its SHA-256 for the self-update.</summary>
public sealed record ProxyProgram(string Path, long SizeBytes, string Sha256, string? Version);

/// <summary>
/// The proxy agent package next to the server (<c>proxy/</c>, or Proxy:PackageDirectory): the program file and the systemd
/// unit for the install, and the program for the agents' self-update.
/// </summary>
public sealed class ProxyPackage(IConfiguration configuration)
{
    public static readonly string[] Files = [ProxySelfUpdate.ProgramName, "backdafuckup-proxy.service"];

    private readonly Lock _lock = new();
    private (string Path, long Size, DateTime Written, string Sha256)? _hashed;

    public string Directory => System.IO.Path.GetFullPath(configuration["Proxy:PackageDirectory"] is { Length: > 0 } configured ? configured : "proxy", AppContext.BaseDirectory);

    public string? Version => System.IO.Path.Combine(Directory, "version.txt") is var file && File.Exists(file) ? File.ReadAllText(file).Trim() : null;

    /// <summary>The program file, or null when the server has no package; its hash is computed once per file version.</summary>
    public ProxyProgram? Program()
    {
        var info = new FileInfo(System.IO.Path.Combine(Directory, ProxySelfUpdate.ProgramName));
        if (!info.Exists)
        {
            return null;
        }

        lock (_lock)
        {
            if (_hashed is not { } known || known.Path != info.FullName || known.Size != info.Length || known.Written != info.LastWriteTimeUtc)
            {
                _hashed = known = (info.FullName, info.Length, info.LastWriteTimeUtc, ProxySelfUpdate.HashOf(info.FullName));
            }

            return new ProxyProgram(info.FullName, info.Length, known.Sha256, Version);
        }
    }
}
