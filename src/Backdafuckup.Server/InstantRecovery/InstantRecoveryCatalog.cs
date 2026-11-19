using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.InstantRecovery;

/// <summary>
/// What the catalog says about instant recoveries that are not over: their sessions (resumed after a restart, their
/// ledger rows left alone by the cleanup) and the restore points they run from (kept by retention and deletes, since pins
/// live only in memory and are gone between a restart and the resume).
/// </summary>
public static class InstantRecoveryCatalog
{
    public static readonly InstantRecoveryState[] Active =
        [InstantRecoveryState.Starting, InstantRecoveryState.Running, InstantRecoveryState.Migrating, InstantRecoveryState.Stopping];

    public static IQueryable<Guid> ActiveSessionIds(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.InstantRecoveries.Where(r => Active.Contains(r.State)).Select(r => r.SessionId);
    }

    /// <summary>The restore points active instant recoveries run from.</summary>
    public static async Task<HashSet<Guid>> RestorePointsInUseAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var active = ActiveSessionIds(db);
        return [.. await db.InstantRecoveryVms.Where(v => active.Contains(v.SessionId)).Select(v => v.RestorePointId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>The active instant recovery that runs a VM from <paramref name="restorePointId"/>, for a message; null if none.</summary>
    public static async Task<(Guid SessionId, string VmName)?> FindUseAsync(AppDbContext db, Guid restorePointId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var active = ActiveSessionIds(db);
        var use = await db.InstantRecoveryVms.Where(v => v.RestorePointId == restorePointId && active.Contains(v.SessionId))
            .Select(v => new { v.SessionId, v.Name }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return use is null ? null : (use.SessionId, use.Name);
    }
}
