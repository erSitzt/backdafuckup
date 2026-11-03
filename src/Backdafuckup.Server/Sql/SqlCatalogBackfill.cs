using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Fills <see cref="RestorePointEntity.SqlDatabaseCount"/> once for restore points recorded before the count existed, by
/// reading their guest metadata. Runs in the background after startup; a restore point that cannot be read stays
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
                .Where(r => r.SqlDatabaseCount == null && r.State == RestorePointState.Available)
                .Select(r => new { r.Id, r.RepositoryId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => (r.Id, r.RepositoryId))
                .ToList();
        }

        var done = 0;
        foreach (var (id, repositoryId) in points)
        {
            int count;
            try
            {
                var repository = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
                using var pin = repository.Pin(id);
                await using var reader = await repository.OpenRestorePointAsync(id, cancellationToken).ConfigureAwait(false);
                count = (await StoredGuestMetadata.ReadAsync(reader, cancellationToken).ConfigureAwait(false))?.SqlDatabases.Count ?? 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation("The guest metadata of restore point {RestorePointId} could not be read ({Message}); its SQL databases stay unknown.", id, ex.Message);
                continue;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.RestorePoints.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SqlDatabaseCount, count), cancellationToken).ConfigureAwait(false);
            done++;
        }

        if (done > 0)
        {
            logger.LogInformation("Recorded the SQL databases of {Count} older restore point(s).", done);
        }

        return done;
    }
}
