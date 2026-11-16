using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>How a volume's data goes into the target cluster.</summary>
internal enum VolumeDataPath
{
    /// <summary>As a device: written into a block volume of the class, snapshotted, the PVC made from the snapshot.</summary>
    Block,

    /// <summary>As files: written into a Filesystem volume of the class, which is handed over to the PVC.</summary>
    Files,
}

/// <summary>The way of one volume into the target: its StorageClass there, the data path, the snapshot class (block) and why.</summary>
internal sealed record TargetVolume(string StorageClass, VolumeDataPath Path, string? SnapshotClass, string Reason);

/// <summary>
/// The storage of a restore's target cluster: its StorageClasses (and the default one) and its VolumeSnapshotClasses. A
/// volume goes in as a block device when the target class's driver has a snapshot class (the class marked default for the
/// driver, else the only one); otherwise as files.
/// </summary>
internal sealed class KubernetesTargetStorage
{
    private const string DefaultClassAnnotation = "storageclass.kubernetes.io/is-default-class";
    private readonly Dictionary<string, V1StorageClass> _classes;
    private readonly IReadOnlyList<(string Name, string Driver, bool IsDefault)> _snapshotClasses;

    private KubernetesTargetStorage(Dictionary<string, V1StorageClass> classes, IReadOnlyList<(string Name, string Driver, bool IsDefault)> snapshotClasses)
    {
        _classes = classes;
        _snapshotClasses = snapshotClasses;
    }

    public static async Task<KubernetesTargetStorage> ReadAsync(IKubernetes client, CancellationToken cancellationToken)
    {
        var classes = (await client.StorageV1.ListStorageClassAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Items
            .ToDictionary(c => c.Metadata.Name, StringComparer.Ordinal);
        var snapshotClasses = await KubernetesInventory.SnapshotClassesAsync(client, cancellationToken).ConfigureAwait(false);
        return new KubernetesTargetStorage(classes, snapshotClasses);
    }

    /// <summary>The names of the target's StorageClasses, sorted.</summary>
    public IReadOnlyList<string> ClassNames => [.. _classes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The class marked default, which a PVC without a class gets.</summary>
    public string? DefaultClass => _classes.Values
        .FirstOrDefault(c => c.Metadata.Annotations?.TryGetValue(DefaultClassAnnotation, out var value) == true && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        ?.Metadata.Name;

    public bool Has(string storageClass) => _classes.ContainsKey(storageClass);

    public V1StorageClass? Find(string? storageClass) => storageClass is not null ? _classes.GetValueOrDefault(storageClass) : null;

    /// <summary>Whether volumes of the class are provisioned only for a pod (WaitForFirstConsumer).</summary>
    public bool WaitsForConsumer(string? storageClass) =>
        storageClass is not null && _classes.TryGetValue(storageClass, out var found) && found.VolumeBindingMode == "WaitForFirstConsumer";

    /// <summary>The way into <paramref name="storageClass"/> (empty: the default class); null when there is no such class.</summary>
    public TargetVolume? Decide(string? storageClass)
    {
        var name = string.IsNullOrEmpty(storageClass) ? DefaultClass : storageClass;
        if (name is null || !_classes.TryGetValue(name, out var found))
        {
            return null;
        }

        var driver = found.Provisioner;
        var candidates = _snapshotClasses.Where(c => string.Equals(c.Driver, driver, StringComparison.Ordinal)).ToList();
        var chosen = candidates.FirstOrDefault(c => c.IsDefault).Name ?? (candidates.Count == 1 ? candidates[0].Name : null);
        return chosen is not null
            ? new TargetVolume(name, VolumeDataPath.Block, chosen, $"StorageClass {name} of the driver {driver}, which has the VolumeSnapshotClass {chosen}")
            : new TargetVolume(name, VolumeDataPath.Files, null, candidates.Count == 0
                ? $"StorageClass {name} of {driver}, which has no VolumeSnapshotClass"
                : $"StorageClass {name} of {driver}, which has {candidates.Count} VolumeSnapshotClasses and none marked default");
    }
}
