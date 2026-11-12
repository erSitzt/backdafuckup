using System.Text.RegularExpressions;

namespace Backdafuckup.Contracts;

/// <summary>
/// A job that backs up a whole cluster (spec 2026-11-11-1343, phase 2): the job object <c>cluster:&lt;excludes&gt;</c>
/// stands for every namespace at the time of a run except those matching its exclude patterns (comma-separated names,
/// <c>*</c> matching any characters), plus <see cref="Member"/>, the restore point of the cluster-scoped objects (CRDs,
/// ClusterRoles and their bindings, classes). Namespace names never contain a colon or an "@".
/// </summary>
public static partial class KubernetesClusterScope
{
    public const string Prefix = "cluster:";

    /// <summary>The job member, and the restore points' object id, of a run's cluster-scoped objects.</summary>
    public const string Member = "@cluster";

    /// <summary>
    /// The exclude item that stands for the namespaces the cluster's platforms make themselves (spec
    /// 2026-11-12-1952-kubernetes-platform-profiles), expanded at each run from the platforms detected then.
    /// </summary>
    public const string PlatformToken = "@platform";

    /// <summary>Left out unless chosen otherwise: what the cluster's platforms make themselves (and make again).</summary>
    public static IReadOnlyList<string> DefaultExcludes { get; } = [PlatformToken];

    /// <summary>
    /// The default before platforms were detected: what Kubernetes and RKE2 make, and what Rancher and Fleet put into a
    /// cluster they manage. Jobs saved with it keep it; the console offers the platform's list instead.
    /// </summary>
    public static IReadOnlyList<string> LegacyDefaultExcludes { get; } =
        ["kube-system", "kube-public", "kube-node-lease", "cattle-*", "fleet-*", "cluster-fleet-*", "local", "backdafuckup-system"];

    /// <summary>The server's own work namespace is never backed up.</summary>
    private const string WorkNamespace = "backdafuckup-system";

    public static bool IsJobObject(string nativeId) => nativeId?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>The job object of a whole cluster without <paramref name="excludes"/> (the platform's token first).</summary>
    public static string JobObject(IEnumerable<string> excludes) =>
        Prefix + string.Join(',', (excludes ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderBy(e => e == PlatformToken ? 0 : 1));

    /// <summary>The patterns a run leaves out: the platform's namespaces in place of the token, then the job's own.</summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<string> excludes, IEnumerable<string> platformPatterns) =>
        (excludes ?? []).Contains(PlatformToken)
            ? [.. (platformPatterns ?? []).Concat(excludes!.Where(e => e != PlatformToken)).Distinct(StringComparer.Ordinal)]
            : excludes ?? [];

    public static IReadOnlyList<string> Excludes(string nativeId) => IsJobObject(nativeId)
        ? nativeId[Prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : throw new ArgumentException($"'{nativeId}' is no cluster job object.", nameof(nativeId));

    /// <summary>The name of the job object in the console.</summary>
    public static string Describe(IReadOnlyList<string> excludes)
    {
        var own = excludes.Where(e => e != PlatformToken).ToList();
        return excludes.Contains(PlatformToken)
            ? $"Whole cluster without the platform's namespaces{(own.Count > 0 ? $" and {string.Join(", ", own)}" : string.Empty)}"
            : own.Count == 0 ? "Whole cluster" : $"Whole cluster without {string.Join(", ", own)}";
    }

    /// <summary>
    /// Why an exclude item is invalid, or null: namespace names with <c>*</c> for any characters, or <see cref="PlatformToken"/>.
    /// </summary>
    public static string? Validate(IEnumerable<string> excludes) =>
        (excludes ?? []).Select(e => e.Trim()).FirstOrDefault(e => e != PlatformToken && !PatternText().IsMatch(e)) is { } bad
            ? bad.StartsWith('@')
                ? $"'{bad}' is not known; '{PlatformToken}' stands for the namespaces the cluster's platforms make."
                : $"'{bad}' is no namespace name or pattern (lower-case letters, digits, '-' and '*')."
            : null;

    /// <summary>Whether the namespace is left out of a cluster job with <paramref name="excludes"/> (the work namespace always is).</summary>
    public static bool IsExcluded(string ns, IReadOnlyList<string> excludes) =>
        ns == WorkNamespace || excludes.Any(pattern => Regex.IsMatch(ns, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant));

    [GeneratedRegex("^[a-z0-9*]([-a-z0-9*]{0,61}[a-z0-9*])?$")]
    private static partial Regex PatternText();
}
