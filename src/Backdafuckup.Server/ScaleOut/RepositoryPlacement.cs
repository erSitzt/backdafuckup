using System.Collections.Concurrent;
using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>What a job writes to: a standalone repository, or a scale-out repository with its extents.</summary>
public sealed class RepositoryScope(BackupRepositoryEntity repository, IReadOnlyList<BackupRepositoryEntity> extents)
{
    public BackupRepositoryEntity Repository { get; } = repository;

    public IReadOnlyList<BackupRepositoryEntity> Extents { get; } = extents;

    public bool IsScaleOut => Repository.Kind == RepositoryKind.ScaleOut;

    /// <summary>Every repository that may hold the scope's data: itself, or all extents (whatever their state).</summary>
    public IReadOnlyList<Guid> RepositoryIds { get; } = repository.Kind == RepositoryKind.ScaleOut ? [.. extents.Select(e => e.Id)] : [repository.Id];

    public ScaleOutSettings Settings => Repository.ScaleOutSettings ?? new ScaleOutSettings();

    public BackupRepositoryEntity? Extent(Guid id) => Extents.FirstOrDefault(e => e.Id == id);
}

/// <summary>Where a chain's next restore point goes.</summary>
/// <param name="NewChain">The chain starts anew on this extent (a full backup), with <paramref name="Reason"/>.</param>
public sealed record PlacementTarget(Guid RepositoryId, string Name, bool NewChain, string? Reason, IDisposable Reservation);

/// <summary>An image backup of the chain is skipped: its extent is unavailable and the scale-out keeps chains in place.</summary>
public sealed class ChainSkippedException(string message) : Exception(message);

/// <summary>
/// Decides which extent of a scale-out repository receives a chain's data (a job's restore points of one VM, namespace
/// or cluster scope, with the VM's SQL Server backups). A chain stays on its extent while it can be written there; it
/// starts anew on the extent with the most room when its extent is sealed, in maintenance or offline. Choices are
/// serialized per scale-out, and running backups reserve their estimated size, so parallel new chains spread.
/// </summary>
public sealed class RepositoryPlacement(IDbContextFactory<AppDbContext> dbFactory, IVolumeSpace volumes, TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, long> _reserved = new();
    private readonly ConcurrentDictionary<Guid, int> _running = new();

    /// <summary>The scope a job's repository stands for. A job pointing at an extent writes to that extent's scale-out.</summary>
    public async Task<RepositoryScope> ScopeAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var repository = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} does not exist.");
        if (repository.ScaleOutId is { } owner)
        {
            repository = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == owner, cancellationToken).ConfigureAwait(false);
        }

        var extents = repository.Kind == RepositoryKind.ScaleOut
            ? await db.Repositories.AsNoTracking().Where(r => r.ScaleOutId == repository.Id).OrderBy(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        return new RepositoryScope(repository, extents);
    }

    /// <summary>
    /// The extent for the next image backup of (job, object). The caller holds the chain's lease, so the placement cannot
    /// change under it. Throws <see cref="ChainSkippedException"/> when the chain's extent is unavailable and the
    /// scale-out keeps chains in place, and <see cref="InvalidOperationException"/> when no extent can take a new chain.
    /// </summary>
    public async Task<PlacementTarget> ForBackupAsync(RepositoryScope scope, BackupJob job, string objectNativeId, string objectName, IOperationLog log, CancellationToken cancellationToken)
    {
        if (!scope.IsScaleOut)
        {
            return new PlacementTarget(scope.Repository.Id, scope.Repository.Name, false, null, Reserve(scope.Repository.Id, 0));
        }

        var gate = _gates.GetOrAdd(scope.Repository.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var placement = await db.ChainPlacements.FirstOrDefaultAsync(p => p.JobId == job.Id && p.ObjectNativeId == objectNativeId, cancellationToken).ConfigureAwait(false);
            var estimate = await EstimateAsync(db, scope, job.Id, objectNativeId, cancellationToken).ConfigureAwait(false);
            string? replaced = null;
            if (placement is null)
            {
                // Adopted from where its newest restore point is (a converted repository, a job moved between scopes).
                var head = await db.RestorePoints.AsNoTracking()
                    .Where(r => r.JobId == job.Id && r.VmNativeId == objectNativeId && r.State == RestorePointState.Available && scope.RepositoryIds.Contains(r.RepositoryId))
                    .OrderByDescending(r => r.CreatedUtc).Select(r => (Guid?)r.RepositoryId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (head is { } adopted)
                {
                    placement = new ChainPlacementEntity { ScaleOutId = scope.Repository.Id, JobId = job.Id, ObjectNativeId = objectNativeId, ExtentId = adopted, PlacedUtc = time.GetUtcNow(), Reason = "adopted" };
                    db.ChainPlacements.Add(placement);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (placement is not null)
            {
                var extent = scope.Extent(placement.ExtentId);
                var unusable = extent is null ? "it is no longer an extent of " + scope.Repository.Name : UnusableForExisting(extent);
                if (unusable is null)
                {
                    return new PlacementTarget(extent!.Id, extent.Name, false, null, Reserve(extent.Id, estimate));
                }

                var name = extent?.Name ?? placement.ExtentId.ToString();
                if (extent is not null && (extent.Maintenance || unusable.StartsWith("offline", StringComparison.Ordinal)) && !scope.Settings.FullWhenExtentUnavailable)
                {
                    throw new ChainSkippedException(
                        $"{objectName} is not backed up: its chain is on extent {name} ({extent.Path}), which is {unusable}, and {scope.Repository.Name} keeps chains in place until their extent is back.");
                }

                replaced = $"its extent {name} is {unusable}";
            }

            var chosen = Choose(scope, estimate, out var refusals)
                ?? throw new InvalidOperationException($"No extent of {scope.Repository.Name} can take a new chain for {objectName}: {string.Join("; ", refusals)}.");
            var reason = replaced is null ? "new" : $"replaced: {replaced}";
            if (placement is null)
            {
                db.ChainPlacements.Add(new ChainPlacementEntity
                {
                    ScaleOutId = scope.Repository.Id, JobId = job.Id, ObjectNativeId = objectNativeId, ExtentId = chosen.Id, PlacedUtc = time.GetUtcNow(), Reason = reason, EstimateBytes = estimate,
                });
            }
            else
            {
                placement.ExtentId = chosen.Id;
                placement.PlacedUtc = time.GetUtcNow();
                placement.Reason = reason;
                placement.EstimateBytes = estimate;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (replaced is not null)
            {
                log.Warning($"{objectName} starts a new chain on extent {chosen.Name} of {scope.Repository.Name} with a full backup: {replaced}. Its older restore points stay where they are until retention removes them.");
            }

            return new PlacementTarget(chosen.Id, chosen.Name, true, reason, Reserve(chosen.Id, estimate));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The extent for a SQL Server backup run of a VM: its chain's extent while it can be written; another one for this
    /// run only while it is in maintenance or offline; a new placement when it is sealed (the image backup follows it).
    /// </summary>
    public async Task<(Guid RepositoryId, string Name)> ForSqlAsync(RepositoryScope scope, BackupJob job, string vmNativeId, CancellationToken cancellationToken)
    {
        if (!scope.IsScaleOut)
        {
            return (scope.Repository.Id, scope.Repository.Name);
        }

        var gate = _gates.GetOrAdd(scope.Repository.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var placement = await db.ChainPlacements.FirstOrDefaultAsync(p => p.JobId == job.Id && p.ObjectNativeId == vmNativeId, cancellationToken).ConfigureAwait(false);
            if (placement is not null && scope.Extent(placement.ExtentId) is { } extent)
            {
                var unusable = UnusableForExisting(extent);
                if (unusable is null)
                {
                    return (extent.Id, extent.Name);
                }

                if (extent.ExtentState != ExtentState.Sealed)
                {
                    // Maintenance or offline: elsewhere for this run; the chain stays where it is.
                    var elsewhere = Choose(scope, 0, out var notHere)
                        ?? throw new InvalidOperationException($"No extent of {scope.Repository.Name} can take SQL Server backups now: {extent.Name} is {unusable}; {string.Join("; ", notHere)}.");
                    return (elsewhere.Id, elsewhere.Name);
                }
            }

            var chosen = Choose(scope, 0, out var refusals)
                ?? throw new InvalidOperationException($"No extent of {scope.Repository.Name} can take SQL Server backups: {string.Join("; ", refusals)}.");
            if (placement is null)
            {
                db.ChainPlacements.Add(new ChainPlacementEntity { ScaleOutId = scope.Repository.Id, JobId = job.Id, ObjectNativeId = vmNativeId, ExtentId = chosen.Id, PlacedUtc = time.GetUtcNow(), Reason = "new (SQL Server backups)" });
            }
            else
            {
                placement.Reason = $"replaced: its extent {scope.Extent(placement.ExtentId)?.Name} is sealed (SQL Server backups)";
                placement.ExtentId = chosen.Id;
                placement.PlacedUtc = time.GetUtcNow();
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (chosen.Id, chosen.Name);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Where a copy brought back from tape goes: the repository itself, or the extent with the most room.</summary>
    public async Task<(Guid RepositoryId, string Name)> ForImportAsync(Guid repositoryId, long estimate, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (!scope.IsScaleOut)
        {
            return (scope.Repository.Id, scope.Repository.Name);
        }

        var chosen = Choose(scope, estimate, out var refusals)
            ?? throw new InvalidOperationException($"No extent of {scope.Repository.Name} can take it: {string.Join("; ", refusals)}.");
        return (chosen.Id, chosen.Name);
    }

    /// <summary>Why an extent cannot take a chain's next restore point (null: it can). Low space does not move a chain.</summary>
    internal string? UnusableForExisting(BackupRepositoryEntity extent) =>
        extent.Maintenance ? "in maintenance"
        : extent.ExtentState == ExtentState.Sealed ? "sealed"
        : volumes.Probe(extent.Path) is null ? $"offline ({extent.Path} cannot be reached)"
        : null;

    /// <summary>The active extent with the most room after reservations and the reserve; null with the reasons when none qualifies.</summary>
    internal BackupRepositoryEntity? Choose(RepositoryScope scope, long estimate, out List<string> refusals, Guid? except = null)
    {
        refusals = [];
        var candidates = new List<(BackupRepositoryEntity Extent, long Room)>();
        foreach (var extent in scope.Extents.Where(e => e.Id != except))
        {
            if (extent.Maintenance)
            {
                refusals.Add($"{extent.Name} is in maintenance");
                continue;
            }

            if (extent.ExtentState != ExtentState.Active)
            {
                refusals.Add($"{extent.Name} is {extent.ExtentState.ToString().ToLowerInvariant()}");
                continue;
            }

            if (volumes.Probe(extent.Path) is not { } space)
            {
                refusals.Add($"{extent.Name} is offline ({extent.Path} cannot be reached)");
                continue;
            }

            var reserve = space.Total / 100 * scope.Settings.NewChainMinFreePercent;
            var room = space.Free - _reserved.GetValueOrDefault(extent.Id) - reserve;
            if (room <= Math.Max(0, estimate))
            {
                refusals.Add($"{extent.Name} has {VmBackupTask.Format(Math.Max(0, space.Free))} free ({VmBackupTask.Format(_reserved.GetValueOrDefault(extent.Id))} reserved by running backups, {scope.Settings.NewChainMinFreePercent} % kept free), about {VmBackupTask.Format(estimate)} needed");
                continue;
            }

            candidates.Add((extent, room));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // The most room; within 2 % of it, the extent with fewer running backups.
        var best = candidates.Max(c => c.Room);
        return candidates
            .Where(c => c.Room >= best - (best / 50))
            .OrderBy(c => _running.GetValueOrDefault(c.Extent.Id))
            .ThenByDescending(c => c.Room)
            .ThenBy(c => c.Extent.Name, StringComparer.Ordinal)
            .First().Extent;
    }

    /// <summary>
    /// The expected size of the chain's next restore point: the average new data of its last three; for a chain without
    /// any, the average first backup in the scale-out (0 when there is none).
    /// </summary>
    private static async Task<long> EstimateAsync(AppDbContext db, RepositoryScope scope, Guid jobId, string objectNativeId, CancellationToken cancellationToken)
    {
        var recent = await db.RestorePoints.AsNoTracking()
            .Where(r => r.JobId == jobId && r.VmNativeId == objectNativeId && r.State == RestorePointState.Available)
            .OrderByDescending(r => r.CreatedUtc).Take(3).Select(r => r.NewStoredBytes)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (recent.Count > 0)
        {
            return (long)recent.Average();
        }

        var fulls = await db.RestorePoints.AsNoTracking()
            .Where(r => scope.RepositoryIds.Contains(r.RepositoryId) && r.State == RestorePointState.Available && r.Kind == Core.Repository.RestorePointKind.Full)
            .Select(r => r.NewStoredBytes).Take(200)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return fulls.Count > 0 ? (long)fulls.Average() : 0;
    }

    private Reservation Reserve(Guid repositoryId, long bytes)
    {
        _reserved.AddOrUpdate(repositoryId, bytes, (_, b) => b + bytes);
        _running.AddOrUpdate(repositoryId, 1, (_, n) => n + 1);
        return new Reservation(this, repositoryId, bytes);
    }

    private sealed class Reservation(RepositoryPlacement owner, Guid repositoryId, long bytes) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner._reserved.AddOrUpdate(repositoryId, 0, (_, b) => b - bytes);
                owner._running.AddOrUpdate(repositoryId, 0, (_, n) => n - 1);
            }
        }
    }
}
