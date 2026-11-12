using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// The restore of a whole-cluster job's run into a cluster (spec 2026-11-11-1343, phase 2): first its cluster-scoped
/// objects (one task; the restore stops when they fail, since the namespaces need their CRDs and roles), then each chosen
/// namespace of the run as its own task, the way a namespace restore goes (into another cluster with the StorageClass map).
/// </summary>
public sealed partial class RestoreSessionRunner
{
    private async Task<(SessionState State, string Summary)> RunClusterAsync(
        SessionEntity session, RestoreRequestDto request, KubernetesClusterRestoreDto options, CancellationToken cancellationToken)
    {
        ClusterRuns.Run run;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var clusterPoint = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The restore point of the cluster-scoped objects no longer exists.");
            run = await ClusterRuns.LoadAsync(db, clusterPoint.SessionId ?? Guid.Empty, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("The run of the whole-cluster job has no restore point of its cluster-scoped objects (any more).");
        }

        var target = request.TargetConnectionId ?? run.ClusterPoint.ConnectionId ?? throw new InvalidOperationException("No target cluster selected.");
        var chosen = options.Namespaces is { } names
            ? run.Namespaces.Where(n => names.Contains(n.Namespace, StringComparer.Ordinal)).ToList()
            : [.. run.Namespaces];

        // The cluster-scoped objects first.
        var task = await tracker.AddTaskAsync(session.Id, run.ClusterPoint.VmNativeId, "Cluster-scoped objects", cancellationToken).ConfigureAwait(false);
        var log = events.CreateLog(session.Id, task.Id, "Cluster-scoped objects");
        await tracker.UpdateTaskAsync(task.Id, t => { t.State = SessionState.Running; t.StartedUtc = time.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);
        foreach (var name in (options.Namespaces ?? []).Where(n => chosen.All(c => c.Namespace != n)))
        {
            log.Warning($"Namespace {name} is not in the run (and has no older restore point of the job): it is not restored.");
        }

        foreach (var (name, point, _) in chosen.Where(n => !n.FromRun))
        {
            log.Warning($"Namespace {name} failed in the run: it comes from its restore point of {point.CreatedUtc.ToLocalTime():g} instead.");
        }

        try
        {
            await using var cluster = await connections.OpenAsync(target, cancellationToken).ConfigureAwait(false);
            if (cluster is not Kubernetes.IKubernetesClusterRestorer restorer)
            {
                throw new InvalidOperationException("The target of a cluster restore must be a Kubernetes cluster.");
            }

            if (await restorer.MissingRestoreRightsAsync(cancellationToken).ConfigureAwait(false) is { } missing)
            {
                throw new InvalidOperationException(missing);
            }

            byte[] objects;
            IReadOnlyList<string>? sourcePlatforms;
            var repository = await repositories.GetAsync(run.ClusterPoint.RepositoryId, cancellationToken).ConfigureAwait(false);
            using (repository.Pin(run.ClusterPoint.Id))
            await using (var reader = await repository.OpenRestorePointAsync(run.ClusterPoint.Id, cancellationToken).ConfigureAwait(false))
            await using (var blob = await reader.OpenBlobAsync(reader.Manifest.Metadata ?? throw new InvalidDataException("The restore point holds no objects."), cancellationToken).ConfigureAwait(false))
            {
                using var memory = new MemoryStream();
                await blob.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                objects = memory.ToArray();
                sourcePlatforms = reader.Manifest.Kubernetes?.Platforms;
            }

            log.Info($"Restoring the cluster-scoped objects of {run.ClusterPoint.CreatedUtc.ToLocalTime():g} ({run.ClusterPoint.VmName}), then {chosen.Count} namespace(s): {string.Join(", ", chosen.Select(c => c.Namespace))}.");
            var result = await restorer.RestoreClusterScopeAsync(objects, sourcePlatforms, options.StorageClassMap, options.ReplaceExisting, log, cancellationToken).ConfigureAwait(false);
            await FinishAsync(task.Id, result.Failed > 0 ? SessionState.Warning : SessionState.Succeeded, null, 0).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(task.Id, SessionState.Stopped, "Stopped", 0).ConfigureAwait(false);
            return (SessionState.Stopped, "Stopped by user.");
        }
        catch (Exception ex)
        {
            log.Error($"The cluster-scoped objects could not be restored: {ex.Message}. The namespaces are not restored without them.");
            await FinishAsync(task.Id, SessionState.Failed, ex.Message, 0).ConfigureAwait(false);
            return (SessionState.Failed, ex.Message);
        }

        // Then each namespace, as a namespace restore of its own (its own task).
        var succeeded = 0;
        var warnings = 0;
        var failed = 0;
        foreach (var (name, point, _) in chosen)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (SessionState.Stopped, $"Stopped by user after {succeeded + warnings + failed} of {chosen.Count} namespace(s).");
            }

            var (state, _) = await RestorePointAsync(session, new RestoreRequestDto
            {
                RestorePointId = point.Id,
                TargetConnectionId = target,
                Mode = target == point.ConnectionId ? RestoreModeDto.OriginalLocation : RestoreModeDto.AlternateLocation,
                Kubernetes = new KubernetesRestoreDto { TargetNamespace = name, ReplaceExisting = options.ReplaceExisting, StorageClassMap = options.StorageClassMap },
            }, cancellationToken).ConfigureAwait(false);
            switch (state)
            {
                case SessionState.Succeeded:
                    succeeded++;
                    break;
                case SessionState.Warning:
                    warnings++;
                    break;
                case SessionState.Stopped:
                    return (SessionState.Stopped, $"Stopped by user after {succeeded + warnings + failed} of {chosen.Count} namespace(s).");
                default:
                    failed++;
                    break;
            }
        }

        var overall = failed > 0 ? SessionState.Failed : warnings > 0 || chosen.Any(c => !c.FromRun) ? SessionState.Warning : SessionState.Succeeded;
        return (overall, $"Cluster-scoped objects restored; {succeeded} namespace(s) restored, {warnings} with warnings, {failed} failed.");
    }
}
