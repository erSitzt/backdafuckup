using System.Globalization;
using System.Text.RegularExpressions;

namespace Backdafuckup.Server.Proxies;

/// <summary>
/// The Linux installer of a proxy, as one line to paste (<c>curl … | sudo bash</c>) or as the same script in a file.
/// It downloads the agent with the install token, checks its SHA-256, installs the program and the systemd unit (in
/// the script), enrolls the agent (which spends the token) and starts the service. Run again, it replaces an
/// installation. The token travels in a header, not in the URL (no server log has it). The server's certificate is
/// pinned by its key: curl checks <c>--pinnedpubkey</c> even with <c>--insecure</c>, which only skips the chain to a
/// CA (libcurl: "If you turn off [VERIFYPEER] then the peer is still verified by public key").
/// </summary>
internal static partial class ProxyInstallScript
{
    public const string ScriptName = "install.sh";

    public static string ScriptPath(Guid proxyId) => $"api/agent/proxies/{proxyId:D}/{ScriptName}";

    public static string FilePath(Guid proxyId, string name) => $"api/agent/proxies/{proxyId:D}/install/{name}";

    /// <summary>The line to paste on the proxy machine.</summary>
    public static string OneLiner(string url, Guid proxyId, string token, IReadOnlyList<string> pins) =>
        $"curl -sSL {CurlTls(pins)}-H 'Authorization: Bearer {Safe(token)}' {Safe(url)}{ScriptPath(proxyId)} | sudo bash";

    /// <summary>
    /// The answer to a refused request for the installer (a spent, wrong or expired token; no package): a script that says
    /// why and ends with exit 1. The one-liner pipes whatever comes into bash, so the reason reaches the admin (curl -f
    /// would print only "error: 401", and bash would end with 0).
    /// </summary>
    public static string Refusal(string message) =>
        $"#!/usr/bin/env bash\necho 'backdafuckup proxy install: {message.Replace("'", "'\\''", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ')}' >&2\nexit 1\n";

    /// <summary>The script, for <see cref="OneLiner"/> and as a file.</summary>
    public static string Build(
        string proxyName, Guid proxyId, string url, string token, IReadOnlyList<string> pins, string? version, string programSha256, string unit, DateTimeOffset expires,
        DateTimeOffset made)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var curlTls = pins.Count == 0 ? string.Empty : $"--pinnedpubkey '{CurlPins(pins)}' --insecure";
        var unitText = unit.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        if (unitText.Split('\n').Contains("BACKDAFUCKUP_UNIT", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The systemd unit of the proxy package contains the script's end marker.");
        }

        return $$"""
            #!/usr/bin/env bash
            # Installs the backdafuckup backup proxy "{{Comment(proxyName)}}" and connects it to {{Safe(url)}}.
            # Made {{made.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}} by the backup server. The install token in it works once, until
            # {{expires.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}}: keep this file private. Run it as root on the proxy machine (Linux x86_64 with systemd):
            #   sudo bash install-backdafuckup-proxy.sh
            # It downloads the agent{{(version is null ? string.Empty : $" {Comment(version)}")}}, checks its SHA-256, installs /opt/backdafuckup-proxy/backdafuckup-proxy and its
            # systemd unit, enrolls the agent with the token and starts it. Run again, it replaces the installation.
            set -euo pipefail

            # All in a function: piped into bash, the whole script is read before anything runs.
            main() {
                local server='{{Safe(url)}}'
                local proxy_id='{{proxyId:D}}'
                local token='{{Safe(token)}}'
                local pins='{{string.Join(",", pins.Select(Safe))}}'
                local program_sha256='{{Safe(programSha256)}}'
                local curl_tls=({{curlTls}})

                fail() { echo "backdafuckup proxy install: $*" >&2; exit 1; }
                [ "$(id -u)" -eq 0 ] || fail "run it as root (sudo bash ...)."
                [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] || fail "the proxy agent runs on Linux x86_64; this is $(uname -s) $(uname -m)."
                command -v systemctl >/dev/null || fail "systemd is needed (systemctl not found)."
                command -v curl >/dev/null || fail "curl is needed."
                command -v sha256sum >/dev/null || fail "sha256sum is needed (coreutils)."

                local work
                work=$(mktemp -d)
                # The path goes into the trap now: it runs after main has returned, when "work" is gone (set -u).
                trap "rm -rf '$work'" EXIT
                echo "Downloading the agent from $server ..."
                curl -fsS "${curl_tls[@]}" -H "Authorization: Bearer $token" -o "$work/backdafuckup-proxy" "${server}api/agent/proxies/${proxy_id}/install/backdafuckup-proxy" </dev/null \
                    || fail "could not download the agent from $server (port reachable from here? install token still valid?)."
                echo "$program_sha256  $work/backdafuckup-proxy" | sha256sum -c --quiet - \
                    || fail "the downloaded agent does not match its SHA-256; nothing was installed."

                if systemctl is-active --quiet backdafuckup-proxy; then
                    echo "Stopping the installed agent ..."
                    systemctl stop backdafuckup-proxy
                fi

                install -D -m 755 "$work/backdafuckup-proxy" /opt/backdafuckup-proxy/backdafuckup-proxy
                cat > /etc/systemd/system/backdafuckup-proxy.service <<'BACKDAFUCKUP_UNIT'
            {{unitText}}
            BACKDAFUCKUP_UNIT
                chmod 644 /etc/systemd/system/backdafuckup-proxy.service

                echo "Enrolling proxy $proxy_id ..."
                /opt/backdafuckup-proxy/backdafuckup-proxy enroll --server "$server" --proxy "$proxy_id" --token "$token" --pins "$pins" </dev/null >/dev/null
                systemctl daemon-reload
                systemctl enable --now backdafuckup-proxy </dev/null
                echo "Installed and started. The proxy shows as online in the console within seconds; its log: journalctl -u backdafuckup-proxy"
            }

            main "$@"

            """;
    }

    /// <summary>curl's pin list: <c>sha256//&lt;base64&gt;</c> per key (the server keeps them as hex).</summary>
    internal static string CurlPins(IEnumerable<string> pins) =>
        string.Join(';', pins.Select(p => "sha256//" + (HexPin().IsMatch(p) ? Convert.ToBase64String(Convert.FromHexString(p)) : Safe(p))));

    private static string CurlTls(IReadOnlyList<string> pins) => pins.Count == 0 ? string.Empty : $"--pinnedpubkey '{CurlPins(pins)}' --insecure ";

    /// <summary>A value that goes between single quotes in shell: one with a quote or a line break is refused.</summary>
    private static string Safe(string value) =>
        value.IndexOfAny(['\'', '\n', '\r']) < 0 ? value : throw new InvalidOperationException($"A value of the proxy installer contains a quote or a line break: {value}");

    /// <summary>Text for a comment line.</summary>
    private static string Comment(string value) => value.Replace('\n', ' ').Replace('\r', ' ');

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HexPin();
}
