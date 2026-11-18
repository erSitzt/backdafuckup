using Backdafuckup.Core.Hypervisor;

namespace Backdafuckup.Server.Engine;

/// <summary>The connection kinds as the API and the console name them: parsing, lists and the noun for one connection.</summary>
public static class ConnectionKinds
{
    /// <summary>The kinds whose objects are VMs (backed up as disks plus VM metadata).</summary>
    public static readonly IReadOnlySet<HypervisorKind> VmPlatforms = new HashSet<HypervisorKind> { HypervisorKind.XenServer, HypervisorKind.VSphere };

    /// <summary>"XenServer, Kubernetes or VSphere", for messages that list what is accepted.</summary>
    public static string Accepted { get; } = Join(Enum.GetNames<HypervisorKind>());

    /// <summary>One kind by its name (case-insensitive; numbers are not accepted).</summary>
    public static bool TryParse(string? value, out HypervisorKind kind)
    {
        kind = default;
        var text = value?.Trim();
        return !string.IsNullOrEmpty(text) && !char.IsAsciiDigit(text[0]) && Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    /// <summary>
    /// A comma-separated list of kinds ("XenServer,VSphere"); null or empty means all. The error names the value that is
    /// not a kind.
    /// </summary>
    public static bool TryParseList(string? value, out IReadOnlySet<HypervisorKind>? kinds, out string? error)
    {
        kinds = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var result = new HashSet<HypervisorKind>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(part, out var kind))
            {
                error = $"Unknown connection kind '{part}' ({Accepted}).";
                return false;
            }

            result.Add(kind);
        }

        kinds = result;
        return true;
    }

    /// <summary>What one connection of the kind is called: "pool", "cluster" or "vCenter".</summary>
    public static string Noun(HypervisorKind kind) => kind switch
    {
        HypervisorKind.XenServer => "pool",
        HypervisorKind.Kubernetes => "cluster",
        HypervisorKind.VSphere => "vCenter",
        _ => "connection",
    };

    /// <summary>The platform's name for people: "XCP-ng", "Kubernetes" or "vSphere".</summary>
    public static string Platform(HypervisorKind kind) => kind switch
    {
        HypervisorKind.XenServer => "XCP-ng",
        HypervisorKind.Kubernetes => "Kubernetes",
        HypervisorKind.VSphere => "vSphere",
        _ => kind.ToString(),
    };

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Join(string.Empty, names) : $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]}";
}
