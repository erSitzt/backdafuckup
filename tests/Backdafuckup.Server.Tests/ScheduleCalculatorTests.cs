using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;

namespace Backdafuckup.Server.Tests;

[Trait("Category", "Unit")]
public sealed class ScheduleCalculatorTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_runs_today_if_the_time_is_still_ahead_otherwise_tomorrow()
    {
        var schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(22, 0), TimeZoneId = "Europe/Berlin" };

        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(schedule, Monday));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 21, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(schedule, Monday.AddHours(9)));
    }

    [Fact]
    public void Weekly_runs_only_on_selected_days()
    {
        var schedule = new JobSchedule
        {
            Kind = ScheduleKind.Weekly,
            TimeOfDay = new TimeOnly(1, 30),
            DaysOfWeek = (1 << (int)DayOfWeek.Saturday) | (1 << (int)DayOfWeek.Wednesday),
            TimeZoneId = "UTC",
        };

        Assert.Equal(new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero), ScheduleCalculator.Next(schedule, Monday));
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 1, 30, 0, TimeSpan.Zero), ScheduleCalculator.Next(schedule, new DateTimeOffset(2026, 11, 4, 1, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Handles_daylight_saving_transitions()
    {
        // Europe/Berlin: 2027-03-28 02:00 -> 03:00 (gap), 2026-10-25 03:00 -> 02:00 (repeated hour).
        var gap = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30), TimeZoneId = "Europe/Berlin" };
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(gap, new DateTimeOffset(2027, 3, 27, 12, 0, 0, TimeSpan.Zero)));

        var repeated = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30), TimeZoneId = "Europe/Berlin" };
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), ScheduleCalculator.Next(repeated, new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Unscheduled_jobs_have_no_next_run()
    {
        Assert.Null(ScheduleCalculator.Next(new JobSchedule { Kind = ScheduleKind.None }, Monday));
        Assert.Null(ScheduleCalculator.Next(new JobSchedule { Kind = ScheduleKind.Weekly, DaysOfWeek = 0 }, Monday));
    }
}
