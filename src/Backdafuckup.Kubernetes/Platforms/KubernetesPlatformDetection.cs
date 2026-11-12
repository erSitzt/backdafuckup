using System.Net;
using Backdafuckup.Core.Hypervisor;
using k8s;
using k8s.Autorest;

namespace Backdafuckup.Kubernetes;

/// <summary>
/// Recognising a cluster's platforms (spec 2026-11-12-1952-kubernetes-platform-profiles): a few small reads (version, API
/// groups, namespaces, a sample of nodes, CSI drivers) matched against the catalog's conditions.
/// </summary>
public static class KubernetesPlatformDetection
{
    /// <summary>At most this many nodes are looked at: labels and provider ids are the same across a cluster's nodes.</summary>
    private const int NodeSample = 20;

    /// <summary>Reads what detection looks at; what may not be read (403) or is not served (404) is left out with a note.</summary>
    public static async Task<ClusterFacts> ReadFactsAsync(
        IKubernetes client, string? gitVersion = null, IReadOnlyList<string>? namespaces = null, IReadOnlyList<string>? provisioners = null,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        async Task<T?> TryAsync<T>(string what, Func<Task<T>> read)
            where T : class
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                notes.Add($"{what} could not be read ({(int)ex.Response.StatusCode}); platforms recognised by them may be missing.");
                return null;
            }
        }

        gitVersion ??= (await TryAsync("The version", () => client.Version.GetCodeAsync(cancellationToken)).ConfigureAwait(false))?.GitVersion;
        var groups = await TryAsync("The API groups", () => client.Apis.GetAPIVersionsAsync(cancellationToken)).ConfigureAwait(false);
        namespaces ??= (await TryAsync("The namespaces", () => client.CoreV1.ListNamespaceAsync(cancellationToken: cancellationToken)).ConfigureAwait(false))?
            .Items.Select(n => n.Metadata.Name).ToList() ?? [];
        var nodes = await TryAsync("The nodes", () => client.CoreV1.ListNodeAsync(limit: NodeSample, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var drivers = await TryAsync("The CSI drivers", () => client.StorageV1.ListCSIDriverAsync(cancellationToken: cancellationToken)).ConfigureAwait(false);
        provisioners ??= (await TryAsync("The StorageClasses", () => client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken)).ConfigureAwait(false))?
            .Items.Select(c => c.Provisioner).ToList() ?? [];

        return new ClusterFacts(
            gitVersion,
            [.. (groups?.Groups ?? []).Select(g => g.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. namespaces.Order(StringComparer.Ordinal)],
            [.. (nodes?.Items ?? []).Select(n => new ClusterNodeFacts(
                n.Metadata.Name,
                n.Spec?.ProviderID,
                (IReadOnlyDictionary<string, string>?)n.Metadata.Labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
                (IReadOnlyDictionary<string, string>?)n.Metadata.Annotations ?? new Dictionary<string, string>(StringComparer.Ordinal)))],
            [.. (drivers?.Items ?? []).Select(d => d.Metadata.Name).Concat(provisioners).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)])
        {
            Notes = notes,
        };
    }

    /// <summary>
    /// The platforms of the catalog whose conditions match (any of them), in catalog order, with the evidence of the first
    /// match; the base is not listed (it is always in a set). At most one distribution: the first that matches.
    /// </summary>
    public static IReadOnlyList<DetectedPlatform> Detect(ClusterFacts facts, KubernetesPlatformCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        catalog ??= KubernetesPlatformCatalog.Builtin;
        var detected = new List<DetectedPlatform>();
        var distribution = false;
        foreach (var profile in catalog.Profiles.Where(p => !p.Always))
        {
            foreach (var condition in profile.Detect)
            {
                if (!condition.Matches(facts, out var evidence))
                {
                    continue;
                }

                if (profile.Category == PlatformCategory.Distribution)
                {
                    if (distribution)
                    {
                        break;
                    }

                    distribution = true;
                }

                detected.Add(new DetectedPlatform(profile.Id, evidence));
                break;
            }
        }

        return detected;
    }

    /// <summary>The platforms in effect: those set by hand for the connection (<see cref="KubernetesConnectionOptions.Platforms"/>), else the detected ones.</summary>
    public static KubernetesPlatformSelection Select(IReadOnlyList<DetectedPlatform> detected, ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(settings);
        var chosen = Override(settings);
        var set = KubernetesPlatformSet.Of(chosen ?? detected.Select(d => d.Id));
        return new KubernetesPlatformSelection(set, detected, chosen is not null);
    }

    /// <summary>The ids set by hand for the connection, or null when its platforms are detected.</summary>
    public static IReadOnlyList<string>? Override(ConnectionSettings settings) =>
        settings?.Options.TryGetValue(KubernetesConnectionOptions.Platforms, out var ids) == true && ids is not null
            ? [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;
}

/// <summary>A cluster's platforms in effect, what was detected, and whether they were set by hand.</summary>
public sealed record KubernetesPlatformSelection(KubernetesPlatformSet Set, IReadOnlyList<DetectedPlatform> Detected, bool Overridden)
{
    /// <summary>For the session log: "Platform: RKE2 · Rancher · Fleet + Calico / Canal, vSphere CSI (detected: ...)".</summary>
    public string Describe()
    {
        var addons = Set.Profiles.Where(p => p.Category == PlatformCategory.Addon).Select(p => p.Name).ToList();
        var how = Overridden
            ? "set by hand"
            : Detected.Count == 0 ? "nothing particular detected" : $"detected: {string.Join("; ", Detected.Select(d => $"{d.Id} by {d.Evidence}"))}";
        return $"Platform: {Set.Display}{(addons.Count > 0 ? $" + {string.Join(", ", addons)}" : string.Empty)} ({how}).";
    }
}
