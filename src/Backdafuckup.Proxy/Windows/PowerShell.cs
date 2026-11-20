using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Backdafuckup.Proxy.Windows;

/// <summary>
/// Runs a Windows PowerShell 5.1 script (in every Windows Server) and returns its exit code and output; when it fails,
/// the output is the error message without PowerShell's CLIXML wrapping.
/// </summary>
public delegate Task<(int ExitCode, string Output)> PowerShellRunner(string script, CancellationToken cancellationToken);

/// <summary>PowerShell through <see cref="CommandRunner"/>: the script goes as <c>-EncodedCommand</c>, nothing is quoted on a command line.</summary>
public static partial class PowerShell
{
    /// <summary>A runner over <paramref name="run"/> (default: <see cref="Commands.RunAsync"/>).</summary>
    public static PowerShellRunner Through(CommandRunner? run = null)
    {
        var runner = run ?? Commands.RunAsync;
        return async (script, cancellationToken) =>
        {
            var (code, output) = await runner(Program, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(script)], cancellationToken)
                .ConfigureAwait(false);
            return (code, code == 0 ? output.Trim() : Clean(output));
        };
    }

    /// <summary>Errors stop the script; its message goes to stderr and the exit code is 1.</summary>
    public static string Wrap(string script) =>
        "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(Wrap(script)));

    /// <summary>A value as a single-quoted PowerShell string.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Text without PowerShell's CLIXML wrapping, if it used it.</summary>
    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf("#< CLIXML", StringComparison.Ordinal);
        if (start < 0)
        {
            return text.Trim();
        }

        var errors = new StringBuilder();
        foreach (Match match in ErrorLine().Matches(text[start..]))
        {
            errors.Append(WebUtility.HtmlDecode(match.Groups[1].Value).Replace("_x000D_", string.Empty, StringComparison.Ordinal).Replace("_x000A_", " ", StringComparison.Ordinal));
        }

        var before = text[..start].Trim();
        var message = errors.ToString().Trim();
        return before.Length == 0 ? message : message.Length == 0 ? before : $"{before} {message}";
    }

    private static string Program =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "powershell.exe";

    [GeneratedRegex("<S S=\"Error\">(.*?)</S>", RegexOptions.Singleline)]
    private static partial Regex ErrorLine();
}
