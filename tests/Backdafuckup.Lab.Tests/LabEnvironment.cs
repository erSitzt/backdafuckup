namespace Backdafuckup.Lab.Tests;

/// <summary>
/// Lab settings: the environment variables BDFU_LAB_*, or else the same keys from a file (KEY=VALUE lines, '#' comments,
/// optional 'export ' prefix and quotes). The file is BDFU_LAB_ENV_FILE or ~/.config/backdafuckup/lab.env, so the
/// password does not have to be typed on the command line. The file never enables the tests: that needs the
/// environment variable BDFU_LAB_RUN=1 for the run (see LabConnection).
/// </summary>
internal static class LabEnvironment
{
    private static readonly Lazy<Dictionary<string, string>> FileValues = new(Load);

    public static string FilePath =>
        Environment.GetEnvironmentVariable("BDFU_LAB_ENV_FILE") is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "backdafuckup", "lab.env");

    /// <summary>Test output, shown live on the console (showLiveOutput in xunit.runner.json).</summary>
    public static void Say(string message) => TestContext.Current.TestOutputHelper?.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : FileValues.Value.GetValueOrDefault(name);

    private static Dictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(FilePath))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith('#') || separator <= 0)
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            values[line[..separator].Trim()] = value;
        }

        return values;
    }
}
