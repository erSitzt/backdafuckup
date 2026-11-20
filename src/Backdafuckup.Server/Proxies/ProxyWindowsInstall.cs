using System.Globalization;

namespace Backdafuckup.Server.Proxies;

/// <summary>
/// The Windows installer of a proxy, as one line to paste in a PowerShell started as administrator, or as the same script
/// in a file. It downloads the agent with the install token (curl.exe, which every Windows Server since 2019 has, pins
/// this server's key even with <c>--insecure</c>: checked on Windows Server 2022, curl 7.79.1 with Schannel), checks its
/// SHA-256 and runs <c>backdafuckup-proxy.exe install</c>, which copies itself to Program Files, enrolls (spending the
/// token) and installs and starts the service. Run again, it replaces the installation. The token travels in a header.
/// </summary>
internal static class ProxyWindowsInstall
{
    public const string ScriptName = "install.ps1";

    public static string ScriptPath(Guid proxyId) => $"api/agent/proxies/{proxyId:D}/{ScriptName}";

    /// <summary>The line to paste: the script is fetched (pinned) and run in this session; a refusal throws its reason.</summary>
    public static string OneLiner(string url, Guid proxyId, string token, IReadOnlyList<string> pins) =>
        $"$s = curl.exe -sSL {CurlTls(pins)}-H 'Authorization: Bearer {Safe(token)}' '{Safe(url)}{ScriptPath(proxyId)}'; " +
        "if ($LASTEXITCODE -ne 0) { throw \"backdafuckup proxy install: the installer could not be downloaded (curl exit $LASTEXITCODE)\" }; iex ($s -join \"`n\")";

    /// <summary>The answer to a refused request for the installer: a script that throws the reason (and leaves the session open).</summary>
    public static string Refusal(string message) =>
        $"throw '{("backdafuckup proxy install: " + message).Replace("'", "''", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')}'\n";

    public static string Build(string proxyName, Guid proxyId, string url, string token, IReadOnlyList<string> pins, string? version, string programSha256, DateTimeOffset expires, DateTimeOffset made)
    {
        var curlPins = pins.Count == 0 ? string.Empty : ProxyInstallScript.CurlPins(pins);
        return $$"""
            # Installs the backdafuckup backup proxy "{{Comment(proxyName)}}" on this Windows server and connects it to {{Safe(url)}}.
            # Made {{made.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}} by the backup server. The install token in it works once, until
            # {{expires.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}}: keep this file private. Run it in a PowerShell started as administrator:
            #   powershell -ExecutionPolicy Bypass -File install-backdafuckup-proxy.ps1
            # It downloads the agent{{(version is null ? string.Empty : $" {Comment(version)}")}}, checks its SHA-256 and runs "backdafuckup-proxy.exe install": the program goes to
            # %ProgramFiles%\backdafuckup Proxy, its key to %ProgramData%\backdafuckup-proxy, and the service backdafuckup-proxy starts. Run again, it replaces
            # the installation. It changes no iSCSI or MPIO setting; the agent sets the SAN policy to OfflineShared only if it is unsafe.
            & {
                $ErrorActionPreference = 'Stop'
                $server = '{{Safe(url)}}'
                $proxyId = '{{proxyId:D}}'
                $token = '{{Safe(token)}}'
                $pins = '{{string.Join(",", pins.Select(Safe))}}'
                $curlPins = '{{curlPins}}'
                $sha256 = '{{Safe(programSha256).ToUpperInvariant()}}'

                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'backdafuckup proxy install: run it in a PowerShell started as administrator.'
                }
                if (-not [Environment]::Is64BitOperatingSystem) { throw 'backdafuckup proxy install: the proxy agent runs on 64-bit Windows (x64).' }
                if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'backdafuckup proxy install: curl.exe is needed (Windows Server 2019 or later).' }

                $file = Join-Path $env:TEMP ('backdafuckup-proxy-' + [guid]::NewGuid().ToString('N') + '.exe')
                try {
                    Write-Host "Downloading the agent from $server ..."
                    $tls = if ($curlPins) { @('--pinnedpubkey', $curlPins, '--insecure') } else { @() }
                    & curl.exe -fsS @tls -H "Authorization: Bearer $token" -o $file "${server}api/agent/proxies/$proxyId/install/backdafuckup-proxy.exe"
                    if ($LASTEXITCODE -ne 0) {
                        throw "backdafuckup proxy install: could not download the agent from $server (curl exit $LASTEXITCODE; port reachable from here? install token still valid?)."
                    }
                    if ((Get-FileHash -Algorithm SHA256 $file).Hash -ne $sha256) {
                        throw 'backdafuckup proxy install: the downloaded agent does not match its SHA-256; nothing was installed.'
                    }
                    & $file install --server $server --proxy $proxyId --token $token --pins $pins
                    if ($LASTEXITCODE -ne 0) { throw "backdafuckup proxy install: the agent's install ended with exit code $LASTEXITCODE (see above)." }
                }
                finally {
                    Remove-Item -Force -ErrorAction SilentlyContinue $file
                }
            }

            """;
    }

    /// <summary>The command for installing by hand with a downloaded program (as administrator).</summary>
    public static string Command(string url, Guid proxyId, string token, IReadOnlyList<string> pins) =>
        $"backdafuckup-proxy.exe install --server {url} --proxy {proxyId:D} --token {token} --pins {string.Join(",", pins)}";

    private static string CurlTls(IReadOnlyList<string> pins) => pins.Count == 0 ? string.Empty : $"--pinnedpubkey '{ProxyInstallScript.CurlPins(pins)}' --insecure ";

    /// <summary>A value that goes between single quotes in PowerShell: one with a quote or a line break is refused.</summary>
    private static string Safe(string value) =>
        value.IndexOfAny(['\'', '\n', '\r', '`']) < 0 ? value : throw new InvalidOperationException($"A value of the proxy installer contains a quote or a line break: {value}");

    private static string Comment(string value) => value.Replace('\n', ' ').Replace('\r', ' ');
}
