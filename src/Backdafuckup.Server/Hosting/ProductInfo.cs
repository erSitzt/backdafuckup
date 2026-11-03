using System.Globalization;
using System.Reflection;
using Backdafuckup.Contracts;

namespace Backdafuckup.Server.Hosting;

/// <summary>
/// The version of the running server: the product version from Directory.Build.props, the Git commit the SDK adds to
/// the informational version ("0.3.0+&lt;commit&gt;"), and the build time (assembly metadata "BuildTimestamp").
/// </summary>
public sealed record ProductInfo(string Version, string? Commit, DateTimeOffset? BuildUtc)
{
    public static ProductInfo Current { get; } = Read(typeof(ProductInfo).Assembly);

    /// <summary>"0.3.0 (e02cab3)".</summary>
    public string Describe() => Commit is null ? Version : $"{Version} ({Commit})";

    public AboutDto ToDto() => new(Version, Commit, BuildUtc);

    internal static ProductInfo Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var version = plus < 0 ? informational : informational[..plus];
        var commit = plus < 0 ? null : informational[(plus + 1)..];
        if (commit is { Length: > 7 })
        {
            commit = commit[..7];
        }

        var built = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value;
        return new ProductInfo(
            version,
            string.IsNullOrEmpty(commit) ? null : commit,
            DateTimeOffset.TryParse(built, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null);
    }
}
