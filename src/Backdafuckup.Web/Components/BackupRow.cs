using Backdafuckup.Contracts;

namespace Backdafuckup.Web.Components;

/// <summary>A row of <c>Backups &amp; Restore</c>: a VM in a job (by job, <paramref name="Job"/> set), or the VM with all its jobs (by VM).</summary>
/// <param name="Key">Unique on the page: the group and the VM.</param>
public sealed record BackupRow(string Key, BackupVmDto Vm, BackupVmJobDto? Job)
{
    public BackupVmPointDto? Latest => Job is null ? Vm.Latest : Job.Latest;

    /// <summary>A namespace of a Kubernetes cluster rather than a VM (known from its restore points).</summary>
    public bool IsNamespace => (Vm.Latest ?? Latest)?.ObjectKind == RestorePointObjectKinds.KubernetesNamespace;

    /// <summary>"3 VMs", "1 namespace", "2 VMs, 1 namespace".</summary>
    public static string Count(IReadOnlyCollection<BackupRow> rows)
    {
        var namespaces = rows.Count(r => r.IsNamespace);
        var vms = rows.Count - namespaces;
        var parts = new List<string>(2);
        if (vms > 0 || namespaces == 0)
        {
            parts.Add($"{vms} VM{(vms == 1 ? string.Empty : "s")}");
        }

        if (namespaces > 0)
        {
            parts.Add($"{namespaces} namespace{(namespaces == 1 ? string.Empty : "s")}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The header of the name column: VM, Namespace, or both.</summary>
    public static string Noun(IReadOnlyCollection<BackupRow> rows) =>
        rows.All(r => r.IsNamespace) && rows.Count > 0 ? "Namespace" : rows.Any(r => r.IsNamespace) ? "VM or namespace" : "VM";

    public int PointCount => Job?.PointCount ?? Vm.PointCount;

    public long StoredBytes => Job?.StoredBytes ?? Vm.StoredBytes;

    public string State => Job is null ? Vm.State : Job.State;

    public DateTimeOffset? MissedRunUtc => Job is null ? Vm.Jobs.Where(j => j.MissedRunUtc is not null).Min(j => j.MissedRunUtc) : Job.MissedRunUtc;
}
