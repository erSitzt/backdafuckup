using System.Globalization;
using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.Sql;

/// <summary>A backup set as the chain sees it (from <see cref="SqlBackupSetEntity"/>).</summary>
public sealed record SqlChainSet(
    Guid Id,
    SqlBackupSetKind Kind,
    decimal FirstLsn,
    decimal LastLsn,
    Guid? FirstRecoveryForkGuid,
    Guid? LastRecoveryForkGuid,
    decimal? ForkPointLsn,
    DateTimeOffset StartUtc,
    DateTimeOffset FinishUtc,
    int? UtcOffsetMinutes,
    long Size = 0)
{
    public bool IsBase => Kind is SqlBackupSetKind.SnapshotFull or SqlBackupSetKind.NativeFull;

    public static SqlChainSet From(SqlBackupSetEntity set) => new(
        set.Id, set.Kind, set.FirstLsn, set.LastLsn, set.FirstRecoveryForkGuid, set.LastRecoveryForkGuid, set.ForkPointLsn,
        set.StartUtc, set.FinishUtc, set.UtcOffsetMinutes, set.Size);
}

/// <summary>A span of time a database can be restored to, from one base on.</summary>
public sealed record SqlCoverage(DateTimeOffset FromUtc, DateTimeOffset ToUtc, Guid BaseId, SqlBackupSetKind BaseKind, int Logs);

/// <summary>Time not covered between two spans, with what broke the chain.</summary>
public sealed record SqlCoverageGap(DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Reason);

/// <summary>What a restore applies: the base, then the logs; <see cref="StopAtLocal"/> on the last one (null: to its end).</summary>
public sealed record SqlRestorePlan(
    SqlChainSet Base,
    IReadOnlyList<SqlChainSet> Logs,
    DateTimeOffset? StopAtUtc,
    string? StopAtLocal,
    DateTimeOffset ReachedUtc)
{
    public long Bytes => Logs.Sum(l => l.Size);
}

public sealed class SqlChainException(string message) : InvalidOperationException(message);

/// <summary>
/// The log chain of one database: which spans of time its backup sets cover, and which sets a restore to a point in time
/// applies. Rules (checked against SQL Server 2019 and 2022, spec 2026-11-05-1932 spike 2b):
/// <list type="bullet">
/// <item>the first log after a base B contains B's last LSN: <c>log.first_lsn ≤ B.last_lsn &lt; log.last_lsn</c>;</item>
/// <item>the next log starts where the previous one ended (<c>first_lsn(n) = last_lsn(n−1)</c>) on the same recovery fork;</item>
/// <item>a base can be restored to its own finish, every log up to its finish (STOPAT in SQL Server's local time).</item>
/// </list>
/// </summary>
public sealed class SqlChain
{
    private readonly List<SqlChainSet> _bases;
    private readonly List<List<SqlChainSet>> _runs;

    public SqlChain(IEnumerable<SqlChainSet> sets)
    {
        var all = sets.ToList();
        _bases = [.. all.Where(s => s.IsBase).OrderBy(s => s.FinishUtc)];
        _runs = Runs(all.Where(s => s.Kind == SqlBackupSetKind.Log));
    }

    /// <summary>The spans of time the database can be restored to, oldest first, with the gaps between them.</summary>
    public (IReadOnlyList<SqlCoverage> Spans, IReadOnlyList<SqlCoverageGap> Gaps) Coverage()
    {
        var spans = new List<SqlCoverage>();
        foreach (var @base in _bases)
        {
            var (logs, _) = LogsAfter(@base);
            spans.Add(new SqlCoverage(@base.FinishUtc, logs.Count == 0 ? @base.FinishUtc : logs[^1].FinishUtc, @base.Id, @base.Kind, logs.Count));
        }

        // Spans of later bases often lie inside earlier ones (the same run of logs): merge them.
        var merged = new List<SqlCoverage>();
        foreach (var span in spans.OrderBy(s => s.FromUtc))
        {
            if (merged.Count > 0 && span.FromUtc <= merged[^1].ToUtc)
            {
                if (span.ToUtc > merged[^1].ToUtc)
                {
                    merged[^1] = merged[^1] with { ToUtc = span.ToUtc };
                }

                continue;
            }

            merged.Add(span);
        }

        var gaps = new List<SqlCoverageGap>();
        for (var i = 1; i < merged.Count; i++)
        {
            gaps.Add(new SqlCoverageGap(merged[i - 1].ToUtc, merged[i].FromUtc, GapReason(merged[i - 1].ToUtc, merged[i].FromUtc)));
        }

        return (merged, gaps);
    }

    /// <summary>The newest point the database can be restored to, if any.</summary>
    public DateTimeOffset? Latest => Coverage().Spans.Select(s => (DateTimeOffset?)s.ToUtc).Max();

    /// <summary>
    /// The sets a restore to <paramref name="targetUtc"/> applies (null: the latest point), using the newest base that
    /// reaches it. Throws <see cref="SqlChainException"/> naming the gap and the nearest points when nothing does.
    /// </summary>
    public SqlRestorePlan Plan(DateTimeOffset? targetUtc)
    {
        var candidates = _bases
            .Select(b => (Base: b, Logs: LogsAfter(b).Logs))
            .Select(c => (c.Base, c.Logs, End: c.Logs.Count == 0 ? c.Base.FinishUtc : c.Logs[^1].FinishUtc))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new SqlChainException("There is no full backup of the database (no restore point with the SQL Server snapshot and no native full backup).");
        }

        if (targetUtc is null)
        {
            var latest = candidates.OrderByDescending(c => c.End).ThenByDescending(c => c.Base.FinishUtc).First();
            return new SqlRestorePlan(latest.Base, latest.Logs, null, null, latest.End);
        }

        var target = targetUtc.Value;
        var reaching = candidates.Where(c => c.Base.FinishUtc <= target && c.End >= target).OrderByDescending(c => c.Base.FinishUtc).FirstOrDefault();
        if (reaching.Base is null)
        {
            throw new SqlChainException(NotCovered(target));
        }

        if (target == reaching.Base.FinishUtc)
        {
            return new SqlRestorePlan(reaching.Base, [], null, null, target);
        }

        var logs = new List<SqlChainSet>();
        foreach (var log in reaching.Logs)
        {
            logs.Add(log);
            if (log.FinishUtc >= target)
            {
                break;
            }
        }

        var last = logs[^1];
        if (last.UtcOffsetMinutes is not { } offset)
        {
            throw new SqlChainException(
                $"SQL Server did not record its time zone for the log backup that ends {Format(last.FinishUtc)}, so the point in time cannot be converted; " +
                "restore to the end of a log backup instead.");
        }

        var local = (target + TimeSpan.FromMinutes(offset)).UtcDateTime;
        return new SqlRestorePlan(reaching.Base, logs, target, local.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture), target);
    }

    /// <summary>The run of logs that continues <paramref name="base"/>, in order (empty if none does).</summary>
    public (List<SqlChainSet> Logs, string? Broken) LogsAfter(SqlChainSet @base)
    {
        foreach (var run in _runs)
        {
            var start = run.FindIndex(l => l.FirstLsn <= @base.LastLsn && @base.LastLsn < l.LastLsn && SameFork(@base, l));
            if (start >= 0)
            {
                return ([.. run.Skip(start)], null);
            }
        }

        return ([], "no log backup continues the full backup");
    }

    private static bool SameFork(SqlChainSet @base, SqlChainSet log) =>
        @base.LastRecoveryForkGuid is null || log.FirstRecoveryForkGuid is null || @base.LastRecoveryForkGuid == log.FirstRecoveryForkGuid;

    /// <summary>Contiguous runs of logs (same recovery fork, each starting where the previous one ended).</summary>
    private static List<List<SqlChainSet>> Runs(IEnumerable<SqlChainSet> logs)
    {
        var runs = new List<List<SqlChainSet>>();
        foreach (var log in logs.OrderBy(l => l.FirstLsn).ThenBy(l => l.LastLsn))
        {
            var run = runs.FirstOrDefault(r => Continues(r[^1], log));
            if (run is null)
            {
                runs.Add([log]);
            }
            else
            {
                run.Add(log);
            }
        }

        return runs;
    }

    private static bool Continues(SqlChainSet previous, SqlChainSet next) =>
        next.FirstLsn == previous.LastLsn
        && next.ForkPointLsn is null
        && (previous.LastRecoveryForkGuid is null || next.FirstRecoveryForkGuid is null || previous.LastRecoveryForkGuid == next.FirstRecoveryForkGuid);

    private string GapReason(DateTimeOffset from, DateTimeOffset to)
    {
        var inside = _runs.SelectMany(r => r).Where(l => l.FinishUtc > from && l.FinishUtc <= to).OrderBy(l => l.FinishUtc).FirstOrDefault();
        if (inside is { ForkPointLsn: not null })
        {
            return $"the database was restored (new recovery fork, log backup ending {Format(inside.FinishUtc)}); the next full backup starts a new chain";
        }

        return inside is null
            ? $"no log backups from {Format(from)} until the next full backup at {Format(to)}"
            : $"log backups are missing after {Format(from)} (another tool took a log backup, or the database was switched to SIMPLE); the chain restarts with the full backup at {Format(to)}";
    }

    private string NotCovered(DateTimeOffset target)
    {
        var (spans, gaps) = Coverage();
        if (spans.Count == 0)
        {
            return "There is no full backup of the database.";
        }

        if (target < spans[0].FromUtc)
        {
            return $"{Format(target)} is before the oldest full backup of the database ({Format(spans[0].FromUtc)}).";
        }

        if (target > spans[^1].ToUtc)
        {
            return $"{Format(target)} is after the newest backup of the database ({Format(spans[^1].ToUtc)}): no log backup reaches it yet.";
        }

        var gap = gaps.FirstOrDefault(g => g.FromUtc < target && target < g.ToUtc);
        return gap is null
            ? $"{Format(target)} is not covered by the backups of the database."
            : $"{Format(target)} falls into a gap of the chain: {gap.Reason}. The nearest points are {Format(gap.FromUtc)} and {Format(gap.ToUtc)}.";
    }

    private static string Format(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
