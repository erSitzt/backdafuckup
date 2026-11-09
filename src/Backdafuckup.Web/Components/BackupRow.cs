using Backdafuckup.Contracts;

namespace Backdafuckup.Web.Components;

/// <summary>A row of <c>Backups &amp; Restore</c>: a VM in a job (by job, <paramref name="Job"/> set), or the VM with all its jobs (by VM).</summary>
/// <param name="Key">Unique on the page: the group and the VM.</param>
public sealed record BackupRow(string Key, BackupVmDto Vm, BackupVmJobDto? Job)
{
    public BackupVmPointDto? Latest => Job is null ? Vm.Latest : Job.Latest;

    public int PointCount => Job?.PointCount ?? Vm.PointCount;

    public long StoredBytes => Job?.StoredBytes ?? Vm.StoredBytes;

    public string State => Job is null ? Vm.State : Job.State;

    public DateTimeOffset? MissedRunUtc => Job is null ? Vm.Jobs.Where(j => j.MissedRunUtc is not null).Min(j => j.MissedRunUtc) : Job.MissedRunUtc;
}
