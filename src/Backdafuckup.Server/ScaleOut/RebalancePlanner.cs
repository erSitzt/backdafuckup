using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.ScaleOut;

/// <summary>An extent as the planner sees it: its size and free space (expected), and whether it takes chains.</summary>
public sealed record PlannerExtent(Guid Id, string Name, long Total, long Free, bool Active);

/// <summary>
/// A chain the planner may move, and whether it must stay now. <see cref="Bytes"/> is about what its extent gets back (its
/// own new data: chunks it shares with other chains stay); <see cref="Footprint"/>, once measured, what it takes on a
/// target (every chunk it uses), else <see cref="Bytes"/>.
/// </summary>
public sealed record PlannerChain(Guid? JobId, string ObjectNativeId, string Name, Guid ExtentId, long Bytes, bool Pinned, long? Footprint = null)
{
    /// <summary>What the chain takes on a target.</summary>
    public long Need => Math.Max(Bytes, Footprint ?? Bytes);
}

/// <summary>A planned move: <see cref="Bytes"/> to copy (what the target needs), about <see cref="PlannerChain.Bytes"/> freed on the source.</summary>
public sealed record PlannedMove(PlannerChain Chain, Guid ToExtentId, long Bytes);

/// <summary>
/// Plans a rebalance: from the fullest active extent to the one with the most room, chains one by one (the one after whose
/// move the fuller of the two is emptiest; of equal ones the one that copies least, since a chain sharing data takes more
/// to its target than its extent gets back), until the extents are within the imbalance limit and none is below its
/// low-space mark, or the run's byte budget is spent. Pure, so the console can show the plan before it runs.
/// </summary>
public static class RebalancePlanner
{
    /// <summary>Why the extents need balancing (null: they do not).</summary>
    public static string? Trigger(IReadOnlyList<PlannerExtent> extents, ScaleOutSettings settings)
    {
        var active = extents.Where(e => e.Active && e.Total > 0).ToList();
        if (active.Count < 2)
        {
            return null;
        }

        if (active.FirstOrDefault(e => FreePercent(e) < settings.LowFreePercent) is { } low)
        {
            return $"{low.Name} has {FreePercent(low):0.#} % free, less than {settings.LowFreePercent} %";
        }

        var spread = active.Max(UsedPercent) - active.Min(UsedPercent);
        return spread > settings.ImbalancePercent
            ? $"the extents differ by {spread:0.#} percentage points of use, more than {settings.ImbalancePercent}"
            : null;
    }

    public static List<PlannedMove> Plan(IReadOnlyList<PlannerExtent> extents, IReadOnlyList<PlannerChain> chains, ScaleOutSettings settings)
    {
        var state = extents.Where(e => e.Active && e.Total > 0).ToDictionary(e => e.Id, e => (Extent: e, Free: e.Free));
        var moves = new List<PlannedMove>();
        var moved = new HashSet<PlannerChain>();
        var budget = settings.MaxBytesPerRun ?? long.MaxValue;
        for (var round = 0; round < 10_000 && state.Count >= 2; round++)
        {
            double Used(Guid id) => 100.0 * (state[id].Extent.Total - state[id].Free) / state[id].Extent.Total;
            var source = state.Keys.OrderByDescending(Used).ThenBy(id => state[id].Extent.Name, StringComparer.Ordinal).First();
            var target = state.Keys.OrderBy(Used).ThenBy(id => state[id].Extent.Name, StringComparer.Ordinal).First();
            var spread = Used(source) - Used(target);
            var sourceFree = 100.0 * state[source].Free / state[source].Extent.Total;
            if (source == target || (spread <= settings.ImbalancePercent && sourceFree >= settings.LowFreePercent))
            {
                break;
            }

            var reserve = state[target].Extent.Total / 100 * settings.NewChainMinFreePercent;
            var sourceUsed = Used(source);
            var candidate = chains
                .Where(c => c.ExtentId == source && !c.Pinned && c.Bytes > 0 && !moved.Contains(c) && c.Need <= budget && c.Need < state[target].Free - reserve)
                .Select(c =>
                {
                    var sourceAfter = 100.0 * (state[source].Extent.Total - state[source].Free - c.Bytes) / state[source].Extent.Total;
                    var targetAfter = 100.0 * (state[target].Extent.Total - state[target].Free + c.Need) / state[target].Extent.Total;
                    return (Chain: c, Fuller: Math.Max(sourceAfter, targetAfter), Spread: Math.Abs(sourceAfter - targetAfter));
                })
                .Where(c => c.Spread < spread && c.Fuller < sourceUsed) // relief for the fullest, never overshooting (no ping-pong)
                .OrderBy(c => c.Fuller) // the fuller of the two as empty as possible; of equal ones the one that copies least
                .ThenBy(c => c.Chain.Need)
                .ThenBy(c => c.Spread)
                .ThenBy(c => c.Chain.Name, StringComparer.Ordinal)
                .Select(c => c.Chain)
                .FirstOrDefault();
            if (candidate is null)
            {
                break;
            }

            moves.Add(new PlannedMove(candidate, target, candidate.Need));
            moved.Add(candidate);
            budget -= candidate.Need;
            state[source] = (state[source].Extent, state[source].Free + candidate.Bytes);
            state[target] = (state[target].Extent, state[target].Free - candidate.Need);
        }

        return moves;
    }

    /// <summary>Each extent's free space after the planned moves.</summary>
    public static Dictionary<Guid, long> FreeAfter(IReadOnlyList<PlannerExtent> extents, IReadOnlyList<PlannedMove> moves)
    {
        var free = extents.ToDictionary(e => e.Id, e => e.Free);
        foreach (var move in moves)
        {
            free[move.Chain.ExtentId] += move.Chain.Bytes;
            free[move.ToExtentId] -= move.Bytes;
        }

        return free;
    }

    private static double FreePercent(PlannerExtent e) => 100.0 * e.Free / e.Total;

    private static double UsedPercent(PlannerExtent e) => 100.0 - FreePercent(e);
}

/// <summary>Whether a time is inside a scale-out's window (days, start to end in its time zone; across midnight when end ≤ start).</summary>
public static class ScaleOutWindows
{
    /// <summary>The end of the window <paramref name="now"/> is in, or null when it is outside.</summary>
    public static DateTimeOffset? EndIfInside(ScaleOutWindow window, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = TimeOnly.ParseExact(window.Start, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(window.End, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var time = TimeOnly.FromDateTime(local.DateTime);
        DateTime startDay;
        if (end > start)
        {
            if (time < start || time >= end)
            {
                return null;
            }

            startDay = local.Date;
        }
        else
        {
            // Across midnight: the window began today (after the start) or yesterday (before the end).
            if (time >= start)
            {
                startDay = local.Date;
            }
            else if (time < end)
            {
                startDay = local.Date.AddDays(-1);
            }
            else
            {
                return null;
            }
        }

        if (!window.Days.Contains(startDay.DayOfWeek))
        {
            return null;
        }

        var endLocal = (end > start ? startDay : startDay.AddDays(1)) + end.ToTimeSpan();
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal));
    }
}
