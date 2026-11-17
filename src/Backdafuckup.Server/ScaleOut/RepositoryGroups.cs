using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>
/// Repositories as the user sees them: an extent belongs to its scale-out repository. Data that a move put on another
/// extent of the same scale-out is still "in the same repository" (tape matching, filters).
/// </summary>
internal static class RepositoryGroups
{
    /// <summary>Each repository's logical repository: the scale-out for its extents, else itself.</summary>
    public static async Task<Dictionary<Guid, Guid>> LogicalAsync(AppDbContext db, CancellationToken cancellationToken) =>
        await db.Repositories.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.ScaleOutId ?? r.Id, cancellationToken).ConfigureAwait(false);

    /// <summary>The repositories whose data counts as <paramref name="repositoryId"/>'s: itself, its scale-out's extents, or a scale-out's extents.</summary>
    public static async Task<List<Guid>> MembersAsync(AppDbContext db, Guid repositoryId, CancellationToken cancellationToken)
    {
        var logical = await db.Repositories.Where(r => r.Id == repositoryId).Select(r => r.ScaleOutId ?? r.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (logical == Guid.Empty)
        {
            return [repositoryId]; // removed meanwhile: only itself
        }

        var members = await db.Repositories.Where(r => r.Id == logical || r.ScaleOutId == logical).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return members.Contains(repositoryId) ? members : [.. members, repositoryId];
    }
}
