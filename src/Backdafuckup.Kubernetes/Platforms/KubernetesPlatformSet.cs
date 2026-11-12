using System.Text.Json.Nodes;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The platforms of one cluster (its distribution, managers and addons, always with the base): what they make themselves,
/// as one set of rules. Backups use the set detected for the cluster; restores the set recorded in the restore point.
/// </summary>
public sealed class KubernetesPlatformSet
{
    /// <summary>
    /// The rules of the product before platforms were detected: what restore points without recorded platforms are
    /// restored with, so they behave as they did.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyIds = ["kubernetes", "rke2", "k3s", "rancher", "fleet", "calico", "cilium", "vsphere-csi"];

    private KubernetesPlatformSet(IReadOnlyList<KubernetesPlatformProfile> profiles, IReadOnlyList<string> unknown)
    {
        Profiles = profiles;
        Unknown = unknown;
    }

    /// <summary>Only the base profile.</summary>
    public static KubernetesPlatformSet Base => Of([]);

    public static KubernetesPlatformSet Legacy => Of(LegacyIds);

    /// <summary>The set recorded in a restore point; the legacy rules for one from before platforms were recorded.</summary>
    public static KubernetesPlatformSet Recorded(IReadOnlyList<string>? ids) => ids is null ? Legacy : Of(ids);

    /// <summary>In catalog order, the base first.</summary>
    public IReadOnlyList<KubernetesPlatformProfile> Profiles { get; }

    /// <summary>Ids that were asked for but are not in the catalog (e.g. recorded by a newer version): left out.</summary>
    public IReadOnlyList<string> Unknown { get; }

    public IReadOnlyList<string> Ids => [.. Profiles.Select(p => p.Id)];

    /// <summary>The distribution and managers, e.g. "RKE2 · Rancher · Fleet"; "Kubernetes" when there are none.</summary>
    public string Display =>
        Profiles.Where(p => p.Category is PlatformCategory.Distribution or PlatformCategory.Manager).Select(p => p.Name).ToList() is { Count: > 0 } names
            ? string.Join(" · ", names)
            : "Kubernetes";

    /// <summary>The namespaces the platforms make, with the profile each pattern comes from.</summary>
    public IReadOnlyList<(KubernetesPlatformProfile Profile, string Pattern)> NamespacePatterns =>
        [.. Profiles.SelectMany(p => p.Namespaces.Select(n => (p, n)))];

    /// <summary>The kubelet's root directory, from the distribution, else the base.</summary>
    public string KubeletRootDir => Profiles.LastOrDefault(p => p.KubeletRootDir is not null)?.KubeletRootDir ?? "/var/lib/kubelet";

    /// <summary>The container runtime's root directory, from the distribution, else the base.</summary>
    public string ContainerRuntimeRoot => Profiles.LastOrDefault(p => p.ContainerRuntimeRoot is not null)?.ContainerRuntimeRoot ?? "/var/lib/containerd";

    /// <summary>The set of these platforms (unknown ids left out, see <see cref="Unknown"/>), with the base.</summary>
    public static KubernetesPlatformSet Of(IEnumerable<string>? ids, KubernetesPlatformCatalog? catalog = null)
    {
        catalog ??= KubernetesPlatformCatalog.Builtin;
        var wanted = new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        return new KubernetesPlatformSet(
            [.. catalog.Profiles.Where(p => p.Always || wanted.Contains(p.Id))],
            [.. wanted.Where(id => catalog.Find(id) is null).Order(StringComparer.Ordinal)]);
    }

    /// <summary>The platform that installs the CRDs of an API group, if one of the set does.</summary>
    public KubernetesPlatformProfile? CrdGroupOf(string group) => Profiles.FirstOrDefault(p => p.CrdGroups.Any(g => g.IsMatch(group)));

    public bool IsCrdGroup(string group) => CrdGroupOf(group) is not null;

    /// <summary>
    /// The platform that makes <paramref name="o"/> (the first of the set whose rule matches), and whether it makes it in
    /// every namespace (left out of every restore) rather than only where it manages the cluster.
    /// </summary>
    public PlatformMade? MadeBy(JsonObject o)
    {
        PlatformMade? found = null;
        foreach (var profile in Profiles)
        {
            foreach (var rule in profile.Made.Where(r => r.Matches(o)))
            {
                if (rule.Always)
                {
                    return new PlatformMade(profile, true);
                }

                found ??= new PlatformMade(profile, false);
            }
        }

        return found;
    }

    /// <summary>Whether the objects of a custom resource's kind are made by the platform (all of them, by their API group or kind).</summary>
    public PlatformMade? MakesKind(string group, string version, string kind) =>
        MadeBy(new JsonObject { ["apiVersion"] = group.Length == 0 ? version : $"{group}/{version}", ["kind"] = kind, ["metadata"] = new JsonObject() });

    /// <summary>A label or annotation that means something only in a cluster the platform manages (removed in another cluster).</summary>
    public bool IsStripped(string key) => Profiles.Any(p => p.Strip.Any(s => s.IsMatch(key)));

    public override string ToString() => string.Join(", ", Ids);
}

/// <summary>An object a platform makes; <see cref="Always"/>: in every namespace, so it stays out of every restore.</summary>
public sealed record PlatformMade(KubernetesPlatformProfile Profile, bool Always);

/// <summary>What detection looks at in a cluster.</summary>
public sealed record ClusterFacts(
    string? GitVersion,
    IReadOnlyList<string> ApiGroups,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<ClusterNodeFacts> Nodes,
    IReadOnlyList<string> CsiDrivers)
{
    /// <summary>What could not be read (e.g. nodes without the right to list them), for the console.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ClusterNodeFacts(string Name, string? ProviderId, IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Annotations);
