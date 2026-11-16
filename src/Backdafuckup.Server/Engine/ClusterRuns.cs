using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// The restore points of a whole-cluster job's run: the one of its cluster-scoped objects, and per namespace the run's
/// own, or — for a namespace whose backup failed in the run — its newest older one of the same job and cluster.
/// </summary>
public static class ClusterRuns
{
    public sealed record Run(RestorePointEntity ClusterPoint, IReadOnlyList<(string Namespace, RestorePointEntity Point, bool FromRun)> Namespaces);

    /// <summary>The run of <paramref name="backupSessionId"/>, or null when it has no available restore point of cluster-scoped objects.</summary>
    public static async Task<Run?> LoadAsync(AppDbContext db, Guid backupSessionId, CancellationToken cancellationToken)
    {
        var points = await db.RestorePoints.AsNoTracking()
            .Where(r => r.SessionId == backupSessionId && r.State == RestorePointState.Available)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (points.FirstOrDefault(r => r.ObjectKind == RestorePointObjectKinds.KubernetesCluster) is not { } cluster)
        {
            return null;
        }

        var namespaces = points.Where(r => r.ObjectKind == RestorePointObjectKinds.KubernetesNamespace)
            .Select(r => (r.VmNativeId, r, true)).ToList();

        // Namespaces of the run without a point of it (their backup failed in it): their newest older point.
        var members = await db.SessionTasks.AsNoTracking().Where(t => t.SessionId == backupSessionId).Select(t => t.ObjectNativeId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var missing = members.Where(m => m != KubernetesClusterScope.Member && namespaces.All(n => n.VmNativeId != m)).ToList();
        if (cluster.JobId is { } jobId && missing.Count > 0)
        {
            var older = await db.RestorePoints.AsNoTracking()
                .Where(r => r.JobId == jobId && r.PlatformId == cluster.PlatformId && r.State == RestorePointState.Available
                            && r.ObjectKind == RestorePointObjectKinds.KubernetesNamespace && r.CreatedUtc < cluster.CreatedUtc && missing.Contains(r.VmNativeId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var group in older.GroupBy(r => r.VmNativeId))
            {
                namespaces.Add((group.Key, group.MaxBy(r => r.CreatedUtc)!, false));
            }
        }

        return new Run(cluster, [.. namespaces.OrderBy(n => n.VmNativeId, StringComparer.Ordinal)]);
    }
}
