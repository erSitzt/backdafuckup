using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// The Kubernetes platforms the product knows (spec 2026-11-12-1952-kubernetes-platform-profiles): per platform what it
/// makes itself and makes again (namespaces, CRD groups, objects), which fields mean nothing in another cluster, and where
/// its kubelet and container runtime keep their files. The built-in catalog is <c>Platforms/platforms.json</c>, embedded.
/// </summary>
public sealed class KubernetesPlatformCatalog
{
    private static readonly Lazy<KubernetesPlatformCatalog> BuiltinCatalog = new(() =>
    {
        using var stream = typeof(KubernetesPlatformCatalog).Assembly.GetManifestResourceStream("Backdafuckup.Kubernetes.platforms.json")
                           ?? throw new InvalidOperationException("The embedded platform catalog is missing.");
        return Load(stream);
    });

    private readonly Dictionary<string, KubernetesPlatformProfile> _byId;

    private KubernetesPlatformCatalog(IReadOnlyList<KubernetesPlatformProfile> profiles)
    {
        Profiles = profiles;
        _byId = profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    public static KubernetesPlatformCatalog Builtin => BuiltinCatalog.Value;

    /// <summary>In catalog order: the base first, then distributions, managers and addons as listed.</summary>
    public IReadOnlyList<KubernetesPlatformProfile> Profiles { get; }

    public KubernetesPlatformProfile? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Reads and checks a catalog; any mistake (an unknown field, a bad pattern, a duplicate id) throws.</summary>
    public static KubernetesPlatformCatalog Load(Stream json)
    {
        PlatformCatalogData data;
        try
        {
            data = JsonSerializer.Deserialize(json, KubernetesPlatformJsonContext.Default.PlatformCatalogData)
                   ?? throw new InvalidDataException("The platform catalog is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The platform catalog is not valid: {ex.Message}", ex);
        }

        if (data.Schema != 1)
        {
            throw new InvalidDataException($"The platform catalog has schema {data.Schema}; this version reads schema 1.");
        }

        var profiles = data.Profiles.Select(KubernetesPlatformProfile.From).ToList();
        if (profiles.GroupBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new InvalidDataException($"The platform catalog has the id {duplicate.Key} more than once.");
        }

        if (profiles.Count(p => p.Always) != 1 || !profiles[0].Always || profiles[0].Category != PlatformCategory.Base)
        {
            throw new InvalidDataException("The platform catalog must start with exactly one base profile that is always in the set.");
        }

        return new KubernetesPlatformCatalog(profiles);
    }
}

public enum PlatformCategory
{
    Base,
    Distribution,
    Manager,
    Addon,
}

/// <summary>One platform of the catalog, its patterns compiled.</summary>
public sealed class KubernetesPlatformProfile
{
    private KubernetesPlatformProfile()
    {
    }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public required PlatformCategory Category { get; init; }

    /// <summary>Checked in the lab; otherwise its rules come from the public documentation in <see cref="Sources"/>.</summary>
    public bool Tested { get; init; }

    /// <summary>The base profile: in every cluster's set, never detected.</summary>
    public bool Always { get; init; }

    public IReadOnlyList<string> Sources { get; init; } = [];

    public string? Notes { get; init; }

    public IReadOnlyList<PlatformCondition> Detect { get; init; } = [];

    /// <summary>The namespaces a whole-cluster job leaves out with <c>@platform</c> (patterns).</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];

    internal IReadOnlyList<Glob> CrdGroups { get; init; } = [];

    internal IReadOnlyList<PlatformObjectMatcher> Made { get; init; } = [];

    internal IReadOnlyList<Glob> Strip { get; init; } = [];

    public string? KubeletRootDir { get; init; }

    public string? ContainerRuntimeRoot { get; init; }

    internal static KubernetesPlatformProfile From(PlatformProfileData d)
    {
        var where = $"platform {d.Id}";
        if (string.IsNullOrWhiteSpace(d.Id) || !Regex.IsMatch(d.Id, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException($"A platform of the catalog has the id \"{d.Id}\"; ids are lower case letters, digits and dashes.");
        }

        if (!Enum.TryParse<PlatformCategory>(d.Category, ignoreCase: true, out var category) || !Enum.IsDefined(category))
        {
            throw new InvalidDataException($"The {where} has the category \"{d.Category}\" (base, distribution, manager or addon).");
        }

        if (!d.Always && (d.Detect is null || d.Detect.Count == 0))
        {
            throw new InvalidDataException($"The {where} has nothing to detect it by.");
        }

        if (!d.Tested && (d.Sources is null || d.Sources.Count == 0))
        {
            throw new InvalidDataException($"The {where} is not tested and names no sources.");
        }

        if (category is not (PlatformCategory.Base or PlatformCategory.Distribution) && (d.KubeletRootDir is not null || d.ContainerRuntimeRoot is not null))
        {
            throw new InvalidDataException($"The {where} is a {category.ToString().ToLowerInvariant()}: only distributions say where the kubelet and the runtime keep their files.");
        }

        return new KubernetesPlatformProfile
        {
            Id = d.Id,
            Name = string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name,
            Category = category,
            Tested = d.Tested,
            Always = d.Always,
            Sources = d.Sources ?? [],
            Notes = d.Notes,
            Detect = [.. (d.Detect ?? []).Select(c => PlatformCondition.From(c, where))],
            Namespaces = [.. (d.Namespaces ?? []).Select(n => Glob.Check(n, $"{where}, namespaces"))],
            CrdGroups = [.. (d.CrdGroups ?? []).Select(g => new Glob(g, $"{where}, crdGroups"))],
            Made = [.. (d.Made ?? []).Select(m => PlatformObjectMatcher.From(m, where))],
            Strip = [.. (d.Strip ?? []).Select(s => new Glob(s, $"{where}, strip"))],
            KubeletRootDir = d.KubeletRootDir,
            ContainerRuntimeRoot = d.ContainerRuntimeRoot,
        };
    }

    public override string ToString() => Name;
}

/// <summary>One way to recognise a platform; exactly one of its properties is set.</summary>
public sealed record PlatformCondition(string Kind, string Value)
{
    private Regex? _regex;
    private Glob? _glob;

    internal static PlatformCondition From(PlatformConditionData d, string where)
    {
        var set = new (string Kind, string? Value)[]
        {
            ("gitVersion", d.GitVersion), ("apiGroup", d.ApiGroup), ("namespace", d.Namespace), ("nodeProviderId", d.NodeProviderId),
            ("nodeLabel", d.NodeLabel), ("nodeAnnotation", d.NodeAnnotation), ("csiDriver", d.CsiDriver),
        }.Where(c => c.Value is not null).ToList();
        if (set.Count != 1 || string.IsNullOrWhiteSpace(set[0].Value))
        {
            throw new InvalidDataException($"A condition of the {where} must set exactly one of gitVersion, apiGroup, namespace, nodeProviderId, nodeLabel, nodeAnnotation, csiDriver.");
        }

        var condition = new PlatformCondition(set[0].Kind, set[0].Value!);
        if (condition.Kind == "gitVersion")
        {
            try
            {
                condition._regex = new Regex(condition.Value, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"The {where} has the gitVersion pattern \"{condition.Value}\", which is no regular expression: {ex.Message}", ex);
            }
        }
        else if (condition.Kind is "apiGroup" or "namespace" or "csiDriver")
        {
            condition._glob = new Glob(condition.Value, where);
        }

        return condition;
    }

    internal bool Matches(ClusterFacts facts, out string evidence)
    {
        evidence = string.Empty;
        switch (Kind)
        {
            case "gitVersion" when facts.GitVersion is { } version && _regex!.IsMatch(version):
                evidence = $"version {version}";
                return true;
            case "apiGroup" when facts.ApiGroups.FirstOrDefault(_glob!.IsMatch) is { } group:
                evidence = $"API group {group}";
                return true;
            case "namespace" when facts.Namespaces.FirstOrDefault(_glob!.IsMatch) is { } ns:
                evidence = $"namespace {ns}";
                return true;
            case "csiDriver" when facts.CsiDrivers.FirstOrDefault(_glob!.IsMatch) is { } driver:
                evidence = $"CSI driver {driver}";
                return true;
            case "nodeProviderId" when facts.Nodes.FirstOrDefault(n => n.ProviderId?.StartsWith(Value, StringComparison.Ordinal) == true) is { } node:
                evidence = $"node {node.Name} ({node.ProviderId})";
                return true;
            case "nodeLabel" when facts.Nodes.FirstOrDefault(n => KeyValue.Parse(Value).Matches(n.Labels)) is { } node:
                evidence = $"node label {Value} on {node.Name}";
                return true;
            case "nodeAnnotation" when facts.Nodes.FirstOrDefault(n => KeyValue.Parse(Value).Matches(n.Annotations)) is { } node:
                evidence = $"node annotation {Value} on {node.Name}";
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Objects a platform makes: every property set must match; within a property, any of its entries.</summary>
internal sealed class PlatformObjectMatcher
{
    private HashSet<string>? _kinds;
    private IReadOnlyList<Glob>? _groups;
    private IReadOnlyList<Glob>? _names;
    private IReadOnlyList<KeyValue>? _labels;
    private IReadOnlyList<KeyValue>? _annotations;

    /// <summary>Left out of every restore, not only of one into another cluster (the platform makes it in every namespace).</summary>
    public bool Always { get; private init; }

    /// <summary>Matches by API group alone: the platform's objects of a kind are all made by it.</summary>
    public bool GroupOnly => _groups is not null && _kinds is null && _names is null && _labels is null && _annotations is null;

    internal static PlatformObjectMatcher From(PlatformMatcherData d, string where)
    {
        if (d.Kinds is null && d.Groups is null && d.Names is null && d.Labels is null && d.Annotations is null)
        {
            throw new InvalidDataException($"A \"made\" rule of the {where} matches nothing in particular: name kinds, groups, names, labels or annotations.");
        }

        return new PlatformObjectMatcher
        {
            Always = d.Always,
            _kinds = d.Kinds is null ? null : new HashSet<string>(d.Kinds, StringComparer.Ordinal),
            _groups = d.Groups?.Select(g => new Glob(g, where)).ToList(),
            _names = d.Names?.Select(n => new Glob(n, where)).ToList(),
            _labels = d.Labels?.Select(l => KeyValue.Parse(l, where)).ToList(),
            _annotations = d.Annotations?.Select(a => KeyValue.Parse(a, where)).ToList(),
        };
    }

    public bool Matches(JsonObject o)
    {
        if (_kinds is not null && !_kinds.Contains(KubernetesObjects.Kind(o)))
        {
            return false;
        }

        if (_groups is not null)
        {
            var apiVersion = (string?)o["apiVersion"] ?? string.Empty;
            var group = apiVersion.Contains('/', StringComparison.Ordinal) ? apiVersion[..apiVersion.IndexOf('/', StringComparison.Ordinal)] : string.Empty;
            if (!_groups.Any(g => g.IsMatch(group)))
            {
                return false;
            }
        }

        if (_names is not null && !_names.Any(n => n.IsMatch(KubernetesObjects.Name(o))))
        {
            return false;
        }

        if (_labels is not null && !_labels.Any(l => l.Matches(Map(o, "labels"))))
        {
            return false;
        }

        return _annotations is null || _annotations.Any(a => a.Matches(Map(o, "annotations")));
    }

    private static IReadOnlyDictionary<string, string> Map(JsonObject o, string name) =>
        o["metadata"]?[name] is JsonObject map
            ? map.Where(kv => kv.Value is JsonValue).ToDictionary(kv => kv.Key, kv => kv.Value!.ToString(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A label or annotation pattern: <c>key</c> or <c>key=value</c>, both globs.</summary>
internal sealed class KeyValue
{
    private KeyValue(Glob key, Glob? value)
    {
        Key = key;
        Value = value;
    }

    public Glob Key { get; }

    public Glob? Value { get; }

    public static KeyValue Parse(string text, string where = "the catalog")
    {
        var at = text.IndexOf('=', StringComparison.Ordinal);
        return at < 0 ? new KeyValue(new Glob(text, where), null) : new KeyValue(new Glob(text[..at], where), new Glob(text[(at + 1)..], where));
    }

    public bool Matches(IReadOnlyDictionary<string, string> map) =>
        map.Any(kv => Key.IsMatch(kv.Key) && (Value is null || Value.IsMatch(kv.Value)));
}

/// <summary>A pattern with <c>*</c> for any characters, anchored to the whole text, case-sensitive.</summary>
internal sealed class Glob
{
    private readonly Regex _regex;

    public Glob(string pattern, string where)
    {
        Pattern = Check(pattern, where);
        _regex = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }

    public string Pattern { get; }

    public bool IsMatch(string text) => _regex.IsMatch(text);

    public static string Check(string pattern, string where) =>
        string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsWhiteSpace)
            ? throw new InvalidDataException($"The {where} has the pattern \"{pattern}\": patterns are non-empty and have no spaces.")
            : pattern;

    public override string ToString() => Pattern;
}

internal sealed record PlatformCatalogData(int Schema, IReadOnlyList<PlatformProfileData> Profiles);

internal sealed record PlatformProfileData(
    string Id,
    string? Name,
    string Category,
    bool Tested = false,
    bool Always = false,
    IReadOnlyList<string>? Sources = null,
    string? Notes = null,
    IReadOnlyList<PlatformConditionData>? Detect = null,
    IReadOnlyList<string>? Namespaces = null,
    IReadOnlyList<string>? CrdGroups = null,
    IReadOnlyList<PlatformMatcherData>? Made = null,
    IReadOnlyList<string>? Strip = null,
    string? KubeletRootDir = null,
    string? ContainerRuntimeRoot = null);

internal sealed record PlatformConditionData(
    string? GitVersion = null,
    string? ApiGroup = null,
    string? Namespace = null,
    string? NodeProviderId = null,
    string? NodeLabel = null,
    string? NodeAnnotation = null,
    string? CsiDriver = null);

internal sealed record PlatformMatcherData(
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<string>? Names = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Annotations = null,
    bool Always = false);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PlatformCatalogData))]
internal sealed partial class KubernetesPlatformJsonContext : JsonSerializerContext;
