using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// Compare-and-set on catalog rows for deleting: a restore point or SQL Server backup is marked <c>Deleting</c> only if
/// it is still available, and a scale-out move switches only rows that are still available. Whichever comes first wins;
/// the other sees no row and backs off. Once marked, the row's repository can no longer change.
/// </summary>
internal static class CatalogDeletes
{
    /// <summary>Marks an available restore point <c>Deleting</c>; returns the repository it is in, or null when it is not available (any more).</summary>
    public static async Task<Guid?> MarkRestorePointAsync(AppDbContext db, Guid restorePointId, CancellationToken cancellationToken)
    {
        var marked = await db.RestorePoints
            .Where(r => r.Id == restorePointId && r.State == RestorePointState.Available)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, RestorePointState.Deleting), cancellationToken).ConfigureAwait(false);
        return marked == 0
            ? null
            : await db.RestorePoints.Where(r => r.Id == restorePointId).Select(r => (Guid?)r.RepositoryId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Back to available after a delete that could not happen (pinned, repository unreachable).</summary>
    public static Task UnmarkRestorePointAsync(AppDbContext db, Guid restorePointId, CancellationToken cancellationToken) =>
        db.RestorePoints
            .Where(r => r.Id == restorePointId && r.State == RestorePointState.Deleting)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, RestorePointState.Available), cancellationToken);

    /// <summary>Like <see cref="MarkRestorePointAsync"/>, for a SQL Server backup object.</summary>
    public static async Task<Guid?> MarkSqlBackupAsync(AppDbContext db, Guid sqlBackupId, CancellationToken cancellationToken)
    {
        var marked = await db.SqlBackups
            .Where(b => b.Id == sqlBackupId && b.State == SqlBackupState.Available)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.State, SqlBackupState.Deleting), cancellationToken).ConfigureAwait(false);
        return marked == 0
            ? null
            : await db.SqlBackups.Where(b => b.Id == sqlBackupId).Select(b => (Guid?)b.RepositoryId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task UnmarkSqlBackupAsync(AppDbContext db, Guid sqlBackupId, CancellationToken cancellationToken) =>
        db.SqlBackups
            .Where(b => b.Id == sqlBackupId && b.State == SqlBackupState.Deleting)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.State, SqlBackupState.Available), cancellationToken);
}
