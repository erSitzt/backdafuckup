using Backdafuckup.Contracts;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.Api;

/// <summary>The platform catalog and a cluster's platforms for the console, which never reads the catalog itself.</summary>
internal static class KubernetesPlatformMapping
{
    public static KubernetesPlatformDto ToDto(this KubernetesPlatformProfile p) =>
        new(p.Id, p.Name, p.Category.ToString().ToLowerInvariant(), p.Tested, p.Namespaces, p.Notes, p.Sources);

    /// <summary>The platforms of a Kubernetes connection: the override if set, else the detected ones, always with the base.</summary>
    public static KubernetesPlatformsDto? Platforms(HypervisorConnection connection)
    {
        if (connection.Kind != Core.Hypervisor.HypervisorKind.Kubernetes)
        {
            return null;
        }

        var catalog = KubernetesPlatformCatalog.Builtin;
        var detected = connection.DetectedPlatforms ?? [];
        var set = KubernetesPlatformSet.Of(connection.PlatformOverride ?? detected.Select(d => d.Id));
        return new KubernetesPlatformsDto(
            [.. detected.Select(d => catalog.Find(d.Id) is { } p ? new DetectedPlatformDto(d.Id, p.Name, p.Tested, d.Evidence) : new DetectedPlatformDto(d.Id, d.Id, false, d.Evidence))],
            connection.PlatformOverride,
            [.. set.Profiles.Select(ToDto)],
            set.Display,
            [.. set.Profiles.Where(p => p.Namespaces.Count > 0).Select(p => new PlatformNamespacesDto(p.Name, p.Namespaces))]);
    }

    /// <summary>A problem with platforms set by hand (unknown ids, two distributions), or null.</summary>
    public static string? Check(IReadOnlyList<string> ids)
    {
        var catalog = KubernetesPlatformCatalog.Builtin;
        if (ids.FirstOrDefault(id => catalog.Find(id) is null) is { } unknown)
        {
            return $"There is no platform \"{unknown}\". Known: {string.Join(", ", catalog.Profiles.Where(p => !p.Always).Select(p => p.Id))}.";
        }

        var distributions = ids.Select(id => catalog.Find(id)!).Where(p => p.Category == PlatformCategory.Distribution).Select(p => p.Name).Distinct().ToList();
        return distributions.Count > 1 ? $"A cluster has one distribution, not {string.Join(" and ", distributions)}." : null;
    }
}
