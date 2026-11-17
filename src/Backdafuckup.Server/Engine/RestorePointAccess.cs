using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// Opens restore points and SQL Server backups where their catalog row says, safely while a scale-out move may switch
/// them to another extent: pin in the repository the row names, then read the row again. If it changed (moved, or being
/// deleted), the pin is released and the row read again. Once pinned and confirmed, the copy cannot be deleted until
/// the pin is released, so a move deletes its source copy only after every reader has finished with it.
/// </summary>
public sealed class RestorePointAccess(IDbContextFactory<AppDbContext> dbFactory, RepositoryManager repositories)
{
    private const int Attempts = 3;

    /// <summary>Pins an available restore point; <paramref name="point"/>'s repository is tried first.</summary>
    public Task<PinnedObject> PinRestorePointAsync(RestorePointEntity point, CancellationToken cancellationToken) =>
        PinRestorePointAsync(point.Id, point.RepositoryId, cancellationToken);

    /// <summary>Pins an available restore point; <paramref name="knownRepositoryId"/> (from a row read earlier) is tried first.</summary>
    public async Task<PinnedObject> PinRestorePointAsync(Guid restorePointId, Guid? knownRepositoryId, CancellationToken cancellationToken)
    {
        async Task<(Guid RepositoryId, RestorePointState State, string What)?> RowAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.RestorePoints.AsNoTracking().Where(r => r.Id == restorePointId)
                .Select(r => new { r.RepositoryId, r.State, r.VmName, r.CreatedUtc })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return row is null ? null : (row.RepositoryId, row.State, $"The restore point of {row.VmName} from {row.CreatedUtc.ToLocalTime():g}");
        }

        return await PinAsync(
            RowAsync,
            row => row.State == RestorePointState.Available ? null : $"{row.What} is not available ({row.State}).",
            "The restore point no longer exists.",
            (repository, id) => repository.Pin(id),
            restorePointId,
            knownRepositoryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pins an available restore point and opens it for reading (disposing the result releases both).</summary>
    internal async Task<OpenedRestorePoint> OpenRestorePointAsync(RestorePointEntity point, CancellationToken cancellationToken)
    {
        var pinned = await PinRestorePointAsync(point, cancellationToken).ConfigureAwait(false);
        try
        {
            return new OpenedRestorePoint(await pinned.Repository.OpenRestorePointAsync(point.Id, cancellationToken).ConfigureAwait(false), pinned);
        }
        catch
        {
            pinned.Dispose();
            throw;
        }
    }

    /// <summary>Pins an available SQL Server backup object; <paramref name="knownRepositoryId"/> is tried first.</summary>
    public async Task<PinnedObject> PinSqlBackupAsync(Guid sqlBackupId, Guid? knownRepositoryId, CancellationToken cancellationToken)
    {
        async Task<(Guid RepositoryId, SqlBackupState State, string What)?> RowAsync()
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.SqlBackups.AsNoTracking().Where(b => b.Id == sqlBackupId)
                .Select(b => new { b.RepositoryId, b.State, b.VmName, b.CreatedUtc })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return row is null ? null : (row.RepositoryId, row.State, $"The SQL Server backup of {row.VmName} from {row.CreatedUtc.ToLocalTime():g}");
        }

        return await PinAsync(
            RowAsync,
            row => row.State == SqlBackupState.Available ? null : $"{row.What} is being deleted.",
            $"SQL Server backup {sqlBackupId} no longer exists.",
            (repository, id) => repository.PinSqlBackup(id),
            sqlBackupId,
            knownRepositoryId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PinnedObject> PinAsync<TState>(
        Func<Task<(Guid RepositoryId, TState State, string What)?>> rowAsync,
        Func<(Guid RepositoryId, TState State, string What), string?> refusal,
        string gone,
        Func<IBackupRepository, Guid, IRestorePointPin> pin,
        Guid id,
        Guid? knownRepositoryId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await rowAsync().ConfigureAwait(false) ?? throw new KeyNotFoundException(gone);
            if (refusal(row) is { } refused)
            {
                throw new InvalidOperationException(refused);
            }

            // The repository read earlier is tried first; the row decides.
            var repositoryId = attempt == 1 && knownRepositoryId is { } known ? known : row.RepositoryId;
            var repository = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
            IRestorePointPin held;
            try
            {
                held = pin(repository, id);
            }
            catch (KeyNotFoundException) when (attempt < Attempts)
            {
                continue; // not (or no longer) in that repository: moved
            }

            var again = await rowAsync().ConfigureAwait(false);
            if (again is { } confirmed && confirmed.RepositoryId == repositoryId && refusal(confirmed) is null)
            {
                return new PinnedObject(repositoryId, repository, held);
            }

            held.Dispose();
            if (again is null)
            {
                throw new KeyNotFoundException(gone);
            }

            if (refusal(again.Value) is { } now)
            {
                throw new InvalidOperationException(now);
            }

            if (attempt >= Attempts)
            {
                throw new InvalidOperationException($"{again.Value.What} is being moved to another extent; try again in a moment.");
            }
        }
    }
}

/// <summary>A restore point or SQL Server backup pinned in the repository that holds it; disposing releases the pin.</summary>
public sealed class PinnedObject(Guid repositoryId, IBackupRepository repository, IRestorePointPin pin) : IDisposable
{
    public Guid RepositoryId { get; } = repositoryId;

    public IBackupRepository Repository { get; } = repository;

    /// <summary>The pin itself, for holders that keep it longer than this object (a browse mount).</summary>
    public IRestorePointPin Pin { get; } = pin;

    public void Dispose() => Pin.Dispose();
}
