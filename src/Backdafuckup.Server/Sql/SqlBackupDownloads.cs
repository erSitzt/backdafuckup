using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// One-time links to the native full and system database backups the agent took: a browser download is a plain GET,
/// and the link must not be reusable from logs or history. The file is read straight from the repository.
/// </summary>
public sealed class SqlBackupDownloads(TimeProvider time)
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (Guid SetId, string User, DateTimeOffset ExpiresUtc)> _tickets = new(StringComparer.Ordinal);

    /// <summary>A link to the backup set's file that works once, for this user, for <see cref="LinkLifetime"/>.</summary>
    public string Issue(Guid setId, string user)
    {
        var now = time.GetUtcNow();
        foreach (var expired in _tickets.Where(t => t.Value.ExpiresUtc < now).Select(t => t.Key).ToList())
        {
            _tickets.TryRemove(expired, out _);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _tickets[token] = (setId, user, now + LinkLifetime);
        return $"api/sql/backup-sets/downloads/{token}";
    }

    /// <summary>The backup set of a link (null if the link is unknown, used, expired or another user's).</summary>
    public Guid? Redeem(string token, string user) =>
        _tickets.TryRemove(token, out var ticket) && ticket.ExpiresUtc >= time.GetUtcNow() && ticket.User == user ? ticket.SetId : null;
}
