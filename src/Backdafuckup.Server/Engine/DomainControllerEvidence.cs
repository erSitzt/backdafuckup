using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Engine;

/// <summary>
/// Whether a VM is known to be a domain controller although its restore point does not say so. A restore point with memory
/// has no guest details (its backup skips guest processing), so a resume would not know it restores a DC. Its agent (domain
/// role 4 or 5) and the VM's restore points with guest processing tell.
/// </summary>
public static class DomainControllerEvidence
{
    /// <summary>
    /// Where the VM shows as a domain controller ("its agent on X reports …", "its restore point of … shows …"), or null
    /// if nothing does. The restore point itself counts too.
    /// </summary>
    public static async Task<string?> FindAsync(AppDbContext db, RestorePointEntity point, CancellationToken cancellationToken)
    {
        if (point.DomainController == true)
        {
            return $"its restore point shows it as a domain controller{Of(point.AdDomain)}";
        }

        var agent = await db.Agents.AsNoTracking()
            .Where(a => a.VmNativeId == point.VmNativeId && (a.DomainRole == 4 || a.DomainRole == 5))
            .Select(a => new { a.HostName, a.Domain })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agent is not null)
        {
            return $"its agent on {agent.HostName} reports a domain controller{Of(agent.Domain)}";
        }

        var earlier = await db.RestorePoints.AsNoTracking()
            .Where(r => r.PlatformId == point.PlatformId && r.VmNativeId == point.VmNativeId && r.DomainController == true)
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => new { r.CreatedUtc, r.JobName, r.AdDomain })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return earlier is null
            ? null
            : $"its restore point of {earlier.CreatedUtc.ToLocalTime():g} (job '{earlier.JobName}') shows it as a domain controller{Of(earlier.AdDomain)}";
    }

    private static string Of(string? domain) => string.IsNullOrEmpty(domain) ? string.Empty : $" of {domain}";
}
