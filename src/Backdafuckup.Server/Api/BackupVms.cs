using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>
/// The Backups overview (<c>GET /api/restore-points/vms</c>): one row per VM with its jobs, the newest restore point in each
/// and whether it missed a scheduled run, without disks and NICs. Job members without a restore point are rows too.
/// </summary>
public static class BackupVms
{
    /// <summary>
    /// A scheduled run counts as missed only once it started this long ago: a job with hundreds of VMs runs for hours,
    /// and its last VMs get their restore point late.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(12);

    private static readonly string[] Contents = ["ad", "sql"];
    private static readonly string[] Consistencies = ["application", "crash", "warning"];
    private static readonly string[] States = ["stale", "nopoint", "ok"];

    /// <summary>
    /// The filters (all optional): <paramref name="Q"/> part of the VM's or a job's name; <paramref name="JobIds"/> jobs
    /// (<see cref="Guid.Empty"/>: restore points without a job, i.e. of a deleted job); <paramref name="Contents"/>
    /// <c>ad</c>/<c>sql</c> in the newest restore point; <paramref name="Consistency"/> of the newest restore point
    /// (<c>application</c>, <c>crash</c>, <c>warning</c> = crash-consistent with a reason); <paramref name="States"/>
    /// <c>stale</c>, <c>nopoint</c>, <c>ok</c> (anything else); <paramref name="OlderThanDays"/> newest restore point older
    /// than that (or none). A VM keeps only the jobs that match.
    /// </summary>
    public sealed record Filter(
        string? Q = null,
        IReadOnlyCollection<Guid>? JobIds = null,
        Guid? RepositoryId = null,
        IReadOnlyCollection<string>? Contents = null,
        string? Consistency = null,
        IReadOnlyCollection<string>? States = null,
        int? OlderThanDays = null);

    /// <summary>The problem with a filter's values, or null if it is valid.</summary>
    public static string? Validate(Filter filter)
    {
        var content = filter.Contents?.FirstOrDefault(c => !Contents.Contains(c, StringComparer.OrdinalIgnoreCase));
        if (content is not null)
        {
            return $"Unknown content '{content}': use {string.Join(", ", Contents)}.";
        }

        if (filter.Consistency is { Length: > 0 } consistency && !Consistencies.Contains(consistency, StringComparer.OrdinalIgnoreCase))
        {
            return $"Unknown consistency '{consistency}': use {string.Join(", ", Consistencies)}.";
        }

        var state = filter.States?.FirstOrDefault(s => !States.Contains(s, StringComparer.OrdinalIgnoreCase));
        if (state is not null)
        {
            return $"Unknown state '{state}': use {string.Join(", ", States)}.";
        }

        return filter.OlderThanDays is < 0 ? "olderThanDays must be 0 or more." : null;
    }

    public static async Task<List<BackupVmDto>> ListAsync(AppDbContext db, Filter filter, DateTimeOffset now, CancellationToken ct)
    {
        // One row per VM, job and repository (a job's repository can change), each with its newest restore point.
        var groups = await db.RestorePoints.AsNoTracking()
            .Where(r => r.State == RestorePointState.Available)
            .GroupBy(r => new { r.VmNativeId, r.JobId, r.RepositoryId })
            .Select(g => new
            {
                g.Key.VmNativeId,
                g.Key.JobId,
                g.Key.RepositoryId,
                Count = g.Count(),
                Stored = g.Sum(r => r.NewStoredBytes),
                Latest = g.OrderByDescending(r => r.CreatedUtc).Select(r => new
                {
                    r.Id,
                    r.JobName,
                    r.VmName,
                    r.ConnectionId,
                    r.CreatedUtc,
                    r.Kind,
                    r.Consistency,
                    r.ConsistencyReason,
                    r.LogicalBytes,
                    r.SqlDatabaseCount,
                    r.DomainController,
                    r.AdDomain,
                    r.MemoryImageBytes,
                }).First(),
            })
            .ToListAsync(ct);
        var jobs = (await db.Jobs.AsNoTracking().Include(j => j.Objects).ToListAsync(ct)).ToDictionary(j => j.Id);

        var entries = new List<Entry>();
        foreach (var vmJob in groups.GroupBy(g => (g.VmNativeId, g.JobId)))
        {
            var newest = vmJob.MaxBy(g => g.Latest.CreatedUtc)!;
            var l = newest.Latest;
            var latest = new BackupVmPointDto(l.Id, vmJob.Key.JobId, l.CreatedUtc, l.Kind.ToString(), l.Consistency, l.ConsistencyReason, l.LogicalBytes,
                l.SqlDatabaseCount ?? 0, l.DomainController == true, l.AdDomain, l.MemoryImageBytes);
            var job = vmJob.Key.JobId is { } id ? jobs.GetValueOrDefault(id) : null;
            entries.Add(new Entry(vmJob.Key.VmNativeId, l.VmName, l.ConnectionId ?? job?.ConnectionId, Describe(job, vmJob.Key.JobId, job?.Name ?? l.JobName,
                vmJob.Sum(g => g.Count), vmJob.Sum(g => g.Stored), [.. vmJob.Select(g => g.RepositoryId).Distinct()], latest, now)));
        }

        // Job members without a restore point of that job.
        var present = entries.Where(e => e.Job.JobId is not null).Select(e => (e.VmNativeId, e.Job.JobId!.Value)).ToHashSet();
        var names = entries.GroupBy(e => e.VmNativeId).ToDictionary(g => g.Key, g => g.MaxBy(e => e.Job.Latest?.CreatedUtc)!.VmName);
        foreach (var job in jobs.Values)
        {
            foreach (var member in job.Objects.Where(o => !present.Contains((o.NativeId, job.Id))))
            {
                entries.Add(new Entry(member.NativeId, names.GetValueOrDefault(member.NativeId) ?? member.CachedName, job.ConnectionId,
                    Describe(job, job.Id, job.Name, 0, 0, [job.RepositoryId], null, now)));
            }
        }

        return entries
            .Where(e => Matches(e, filter, now))
            .GroupBy(e => e.VmNativeId)
            .Select(g =>
            {
                var vmJobs = g.Select(e => e.Job).OrderBy(j => j.JobName, StringComparer.OrdinalIgnoreCase).ToList();
                var latest = vmJobs.Where(j => j.Latest is not null).MaxBy(j => j.Latest!.CreatedUtc)?.Latest;
                var newest = g.MaxBy(e => e.Job.Latest?.CreatedUtc)!;
                var points = vmJobs.Sum(j => j.PointCount);
                var state = vmJobs.Any(j => j.State == BackupVmStates.Stale) ? BackupVmStates.Stale : points == 0 ? BackupVmStates.NoPoint : BackupVmStates.Ok;
                return new BackupVmDto(g.Key, newest.VmName, newest.ConnectionId, vmJobs, points, vmJobs.Sum(j => j.StoredBytes), latest, state);
            })
            .OrderBy(v => v.VmName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.VmNativeId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The state of a VM in a job, and the scheduled run it missed (stale).</summary>
    public static (string State, DateTimeOffset? MissedRunUtc) StateOf(BackupJob? job, BackupVmPointDto? latest, DateTimeOffset now)
    {
        if (job is null)
        {
            return (BackupVmStates.NoJob, null);
        }

        if (latest is null)
        {
            return (BackupVmStates.NoPoint, null);
        }

        if (!job.Enabled)
        {
            return (BackupVmStates.Disabled, null);
        }

        if (ScheduleCalculator.Previous(job.Schedule, now - Grace) is not { } last)
        {
            return (BackupVmStates.Manual, null);
        }

        return latest.CreatedUtc < last ? (BackupVmStates.Stale, last) : (BackupVmStates.Ok, null);
    }

    private static BackupVmJobDto Describe(BackupJob? job, Guid? jobId, string jobName, int count, long stored, IReadOnlyList<Guid> repositories,
        BackupVmPointDto? latest, DateTimeOffset now)
    {
        var (state, missed) = StateOf(job, latest, now);
        return new BackupVmJobDto(jobId, jobName, job is not null, job?.Enabled ?? false, job?.Schedule.ToDto(), count, stored, repositories, latest, state, missed);
    }

    private static bool Matches(Entry entry, Filter filter, DateTimeOffset now)
    {
        var job = entry.Job;
        if (!string.IsNullOrWhiteSpace(filter.Q)
            && !entry.VmName.Contains(filter.Q.Trim(), StringComparison.OrdinalIgnoreCase)
            && !job.JobName.Contains(filter.Q.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.JobIds is { Count: > 0 } ids && !ids.Contains(job.JobExists ? job.JobId!.Value : Guid.Empty))
        {
            return false;
        }

        if (filter.RepositoryId is { } repository && !job.RepositoryIds.Contains(repository))
        {
            return false;
        }

        var latest = job.Latest;
        if (filter.Contents is { Count: > 0 } contents
            && !contents.Any(c => string.Equals(c, "ad", StringComparison.OrdinalIgnoreCase) ? latest?.DomainController == true : latest?.SqlDatabaseCount > 0))
        {
            return false;
        }

        if (filter.Consistency is { Length: > 0 } consistency && !string.Equals(ConsistencyOf(latest), consistency, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.States is { Count: > 0 } states && !states.Contains(job.State is BackupVmStates.Stale or BackupVmStates.NoPoint ? job.State : "ok", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return filter.OlderThanDays is not { } days || latest is null || latest.CreatedUtc < now.AddDays(-days);
    }

    private static string? ConsistencyOf(BackupVmPointDto? point) => point is null ? null
        : point.Consistency == "application" ? "application"
        : point.ConsistencyReason is null ? "crash" : "warning";

    private sealed record Entry(string VmNativeId, string VmName, Guid? ConnectionId, BackupVmJobDto Job);
}
