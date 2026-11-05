using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>
/// Who uses a stored credential: jobs (the SQL Server account of the SQL Server snapshot, the Windows account that
/// installs agents) and queued or running sessions (SQL publishes, restores and exports keep only its id). A credential
/// in use is not deleted: a job would silently lose its account, a session its logon.
/// </summary>
public static class CredentialUse
{
    /// <summary>Null if nothing uses the credential, else a message that names the jobs and sessions.</summary>
    public static async Task<string?> DescribeAsync(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.SqlCredentialId == id || j.GuestCredentialId == id)
            .OrderBy(j => j.Name)
            .Select(j => new { j.Name, Sql = j.SqlCredentialId == id })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var text = id.ToString();
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping) && s.Parameters != null && s.Parameters.Contains(text))
            .CountAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0 && sessions == 0)
        {
            return null;
        }

        var uses = new List<string>();
        if (jobs.Count > 0)
        {
            uses.Add("job(s) " + string.Join(", ", jobs.Select(j => $"'{j.Name}' ({(j.Sql ? "SQL Server account" : "agent installation")})")));
        }

        if (sessions > 0)
        {
            uses.Add($"{sessions} queued or running session(s)");
        }

        return $"The credential is used by {string.Join(" and ", uses)}. Choose another account in the job (or wait for the sessions to end) and delete it then.";
    }
}
