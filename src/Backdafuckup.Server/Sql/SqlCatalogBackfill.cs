using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Fills <see cref="RestorePointEntity.SqlDatabaseCount"/>, <see cref="RestorePointEntity.SqlConsistentCount"/> and
/// <see cref="RestorePointEntity.DomainController"/> once for restore points recorded before they existed, by reading their
/// guest metadata. Runs in the background after startup; a restore point that cannot be read stays
/// unknown and is tried again at the next start.
/// </summary>
public sealed class SqlCatalogBackfill(IDbContextFactory<AppDbContext> dbFactory, RepositoryManager repositories, ILogger<SqlCatalogBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recording the SQL databases of older restore points failed; it is tried again at the next start.");
        }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Guid RepositoryId)> points;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            points = (await db.RestorePoints
                .Where(r => (r.SqlDatabaseCount == null || r.SqlConsistentCount == null || r.DomainController == null) && r.State == RestorePointState.Available)
                .Select(r => new { r.Id, r.RepositoryId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => (r.Id, r.RepositoryId))
                .ToList();
        }

        var done = 0;
        foreach (var (id, repositoryId) in points)
        {
            StoredGuestMetadata? metadata;
            try
            {
                var repository = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
                using var pin = repository.Pin(id);
                await using var reader = await repository.OpenRestorePointAsync(id, cancellationToken).ConfigureAwait(false);
                metadata = await StoredGuestMetadata.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("The guest metadata of restore point {RestorePointId} could not be read ({Message}); its SQL databases and domain controller role stay unknown.", id, ex.Message);
                continue;
            }

            var count = metadata?.SqlDatabases.Count ?? 0;
            var consistent = metadata?.SqlDatabases.Count(d => d.Consistency == "application") ?? 0;
            var (domainController, adDomain) = DomainController(metadata);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.RestorePoints.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SqlDatabaseCount, r => r.SqlDatabaseCount ?? count)
                .SetProperty(r => r.SqlConsistentCount, r => r.SqlConsistentCount ?? consistent)
                .SetProperty(r => r.DomainController, r => r.DomainController ?? domainController)
                .SetProperty(r => r.AdDomain, r => r.DomainController == null ? adDomain : r.AdDomain), cancellationToken).ConfigureAwait(false);
            done++;
        }

        if (done > 0)
        {
            logger.LogInformation("Recorded the SQL databases and domain controller role of {Count} older restore point(s).", done);
        }

        var recorded = await RecordSnapshotFullsAsync(cancellationToken).ConfigureAwait(false);
        if (recorded > 0)
        {
            logger.LogInformation("Recorded the SQL Server snapshot backups of {Count} restore point(s) in the SQL catalog.", recorded);
        }

        return done + recorded;
    }

    /// <summary>
    /// A restore point is of a domain controller when its guest metadata has the directory section, or (from agents before
    /// it was recorded) the NTDS writer took part; the domain is then unknown ("").
    /// </summary>
    internal static (bool DomainController, string? AdDomain) DomainController(StoredGuestMetadata? metadata) => metadata switch
    {
        { ActiveDirectory: { } ad } => (true, ad.DomainDns ?? string.Empty),
        { } m when m.Writers.Any(w => string.Equals(w.Name, StoredGuestMetadata.NtdsWriterName, StringComparison.OrdinalIgnoreCase)) => (true, string.Empty),
        _ => (false, null),
    };

    /// <summary>
    /// Puts the snapshot fulls of restore points into the SQL catalog that have not been recorded there yet (taken before
    /// the catalog existed, imported, or recovered at startup).
    /// </summary>
    public async Task<int> RecordSnapshotFullsAsync(CancellationToken cancellationToken)
    {
        List<Guid> ids;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            ids = await db.RestorePoints
                .Where(r => r.SqlBackupSetsRecorded == null && r.State == RestorePointState.Available)
                .OrderBy(r => r.CreatedUtc)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var done = 0;
        foreach (var id in ids)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var point = await db.RestorePoints.FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
            if (point is null)
            {
                continue;
            }

            try
            {
                var repository = await repositories.GetAsync(point.RepositoryId, cancellationToken).ConfigureAwait(false);
                using var pin = repository.Pin(id);
                await using var reader = await repository.OpenRestorePointAsync(id, cancellationToken).ConfigureAwait(false);
                var snapshot = await StoredSqlSnapshot.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
                await SqlBackupCatalog.RecordSnapshotAsync(db, point, snapshot?.Instances ?? [], DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("The SQL Server snapshot of restore point {RestorePointId} could not be read ({Message}); it is tried again at the next start.", id, ex.Message);
                continue;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            done++;
        }

        return done;
    }
}
