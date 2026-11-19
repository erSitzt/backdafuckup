using System.Security.Cryptography;

namespace Backdafuckup.Proxy;

/// <summary>
/// The proxy agent replacing its own program file with the server's build: downloaded next to it, checked against the
/// size and SHA-256 the server named, then swapped in (the running file stays as <c>.old</c> until the next start). The
/// service manager starts the new program after the agent exits.
/// </summary>
public static class ProxySelfUpdate
{
    /// <summary>The program file name of the agent (a single self-contained file).</summary>
    public const string ProgramName = "backdafuckup-proxy";

    /// <summary>SHA-256 of a file, lower-case hex (the hello's FilesHash, compared with the server's package).</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The running program's hash, or null when it does not run as the installed single file (e.g. under <c>dotnet</c>).</summary>
    public static string? RunningHash() =>
        Environment.ProcessPath is { } path && Path.GetFileName(path) == ProgramName && File.Exists(path) ? HashOf(path) : null;

    /// <summary>Downloads <paramref name="file"/>, checks it, and puts it in place of <paramref name="programPath"/>.</summary>
    public static async Task InstallAsync(HttpClient http, Uri file, string programPath, long size, string sha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var staged = programPath + ".new";
        try
        {
            using (var response = await http.GetAsync(file, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidDataException($"{file} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                await using var output = File.Create(staged);
                await response.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var length = new FileInfo(staged).Length;
            var hash = HashOf(staged);
            if (length != size || !string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The download from {file} is {length} bytes with SHA-256 {hash}; the server named {size} bytes and {sha256}.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var old = programPath + ".old";
            File.Delete(old);
            File.Move(programPath, old);
            File.Move(staged, programPath);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    /// <summary>Removes the program file an update replaced (at the next start).</summary>
    public static void RemoveReplaced(string programPath)
    {
        try
        {
            File.Delete(programPath + ".old");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept; removed after the next update.
        }
    }
}
