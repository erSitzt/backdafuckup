using Backdafuckup.Server.Data;
using Backdafuckup.Server.ScaleOut;

namespace Backdafuckup.Server.Tests;

[Trait("Category", "Unit")]
public sealed class RebalancePlannerTests
{
    private const long GiB = 1L << 30;
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static PlannerChain Chain(string name, Guid extent, long gib, bool pinned = false) => new(Guid.NewGuid(), name, name, extent, gib * GiB, pinned);

    [Fact]
    public void Balanced_extents_need_nothing_a_low_or_uneven_one_says_why()
    {
        var settings = new ScaleOutSettings();
        Assert.Null(RebalancePlanner.Trigger([new(A, "A", 100 * GiB, 50 * GiB, true), new(B, "B", 100 * GiB, 45 * GiB, true)], settings));
        Assert.Contains("A has 5 % free", RebalancePlanner.Trigger([new(A, "A", 100 * GiB, 5 * GiB, true), new(B, "B", 100 * GiB, 10 * GiB, true)], settings), StringComparison.Ordinal);
        Assert.Contains("differ by 40 percentage points", RebalancePlanner.Trigger([new(A, "A", 100 * GiB, 30 * GiB, true), new(B, "B", 100 * GiB, 70 * GiB, true)], settings), StringComparison.Ordinal);

        // A sealed or unavailable extent is no part of the balance.
        Assert.Null(RebalancePlanner.Trigger([new(A, "A", 100 * GiB, 1 * GiB, false), new(B, "B", 100 * GiB, 70 * GiB, true)], settings));
    }

    [Fact]
    public void Chains_move_from_the_fullest_to_the_emptiest_without_overshooting_and_within_the_budget()
    {
        var extents = new List<PlannerExtent> { new(A, "A", 100 * GiB, 10 * GiB, true), new(B, "B", 100 * GiB, 90 * GiB, true), new(C, "C", 100 * GiB, 0, false) };
        var chains = new List<PlannerChain>
        {
            Chain("big", A, 60), Chain("mid", A, 25), Chain("small", A, 5), Chain("busy", A, 30, pinned: true), Chain("sealed", C, 10),
        };
        var moves = RebalancePlanner.Plan(extents, chains, new ScaleOutSettings());

        // 60 GiB would turn A at 90 % and B at 10 % into 30 % and 70 %: a spread of 40 again; 25 and 5 bring both near 60/40.
        Assert.Equal(["mid", "small"], moves.Select(m => m.Chain.Name).ToArray());
        Assert.All(moves, m => Assert.Equal(B, m.ToExtentId));
        var after = RebalancePlanner.FreeAfter(extents, moves);
        Assert.Equal((40 * GiB, 60 * GiB), (after[A], after[B]));

        // A budget stops it; pinned chains and inactive extents are never touched.
        Assert.Equal(["mid"], RebalancePlanner.Plan(extents, chains, new ScaleOutSettings { MaxBytesPerRun = 26 * GiB }).Select(m => m.Chain.Name).ToArray());
        Assert.DoesNotContain(moves, m => m.Chain.Name is "busy" or "sealed" || m.ToExtentId == C);

        // Planning again on the result changes nothing (no ping-pong).
        var balanced = extents.Select(e => e with { Free = after[e.Id] }).ToList();
        var rest = chains.Select(c => moves.Any(m => m.Chain == c) ? c with { ExtentId = B } : c).ToList();
        Assert.Empty(RebalancePlanner.Plan(balanced, rest, new ScaleOutSettings()));
    }

    [Fact]
    public void A_target_keeps_its_reserve_free()
    {
        var extents = new List<PlannerExtent> { new(A, "A", 100 * GiB, 1 * GiB, true), new(B, "B", 100 * GiB, 12 * GiB, true) };
        // 8 GiB would leave B with 4 GiB, below its 5 % reserve.
        Assert.Empty(RebalancePlanner.Plan(extents, [Chain("vm", A, 8)], new ScaleOutSettings()));
        Assert.Single(RebalancePlanner.Plan(extents, [Chain("vm", A, 6)], new ScaleOutSettings()));
    }

    [Fact]
    public void A_window_across_midnight_ends_the_next_morning_on_its_days_only()
    {
        var window = new ScaleOutWindow { Days = [DayOfWeek.Monday], Start = "22:00", End = "06:00", TimeZoneId = "UTC" };
        var monday = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(monday.AddDays(1).AddHours(6), ScaleOutWindows.EndIfInside(window, monday.AddHours(23)));
        Assert.Equal(monday.AddDays(1).AddHours(6), ScaleOutWindows.EndIfInside(window, monday.AddDays(1).AddHours(5)));
        Assert.Null(ScaleOutWindows.EndIfInside(window, monday.AddHours(12)));
        Assert.Null(ScaleOutWindows.EndIfInside(window, monday.AddHours(3))); // Sunday night's window: Sunday is not a day of it
        Assert.Null(ScaleOutWindows.EndIfInside(window, monday.AddDays(1).AddHours(23))); // Tuesday

        var day = new ScaleOutWindow { Days = [.. Enum.GetValues<DayOfWeek>()], Start = "09:00", End = "17:00", TimeZoneId = "UTC" };
        Assert.Equal(monday.AddHours(17), ScaleOutWindows.EndIfInside(day, monday.AddHours(10)));
        Assert.Null(ScaleOutWindows.EndIfInside(day, monday.AddHours(17)));
    }
}
