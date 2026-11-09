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
    public void Previous_is_the_last_run_at_or_before_the_time()
    {
        var daily = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(22, 0), TimeZoneId = "Europe/Berlin" };
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 21, 0, 0, TimeSpan.Zero), ScheduleCalculator.Previous(daily, Monday));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero), ScheduleCalculator.Previous(daily, new DateTimeOffset(2026, 11, 2, 21, 0, 0, TimeSpan.Zero)));

        var weekly = new JobSchedule
        {
            Kind = ScheduleKind.Weekly,
            TimeOfDay = new TimeOnly(1, 30),
            DaysOfWeek = (1 << (int)DayOfWeek.Saturday) | (1 << (int)DayOfWeek.Wednesday),
            TimeZoneId = "UTC",
        };
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 1, 30, 0, TimeSpan.Zero), ScheduleCalculator.Previous(weekly, Monday));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 1, 30, 0, TimeSpan.Zero), ScheduleCalculator.Previous(weekly, new DateTimeOffset(2026, 10, 31, 1, 29, 0, TimeSpan.Zero)));

        Assert.Null(ScheduleCalculator.Previous(new JobSchedule { Kind = ScheduleKind.None }, Monday));
        Assert.Null(ScheduleCalculator.Previous(new JobSchedule { Kind = ScheduleKind.Weekly, DaysOfWeek = 0 }, Monday));
    }

    [Fact]
    public void Previous_handles_daylight_saving_transitions_like_next()
    {
        // The same runs as Next: the first valid minute after the spring gap, the first of the repeated hour.
        var schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimeOfDay = new TimeOnly(2, 30), TimeZoneId = "Europe/Berlin" };
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), ScheduleCalculator.Previous(schedule, new DateTimeOffset(2027, 3, 28, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), ScheduleCalculator.Previous(schedule, new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Unscheduled_jobs_have_no_next_run()
    {
        Assert.Null(ScheduleCalculator.Next(new JobSchedule { Kind = ScheduleKind.None }, Monday));
        Assert.Null(ScheduleCalculator.Next(new JobSchedule { Kind = ScheduleKind.Weekly, DaysOfWeek = 0 }, Monday));
    }

    [Theory]
    [InlineData(15, "2026-11-02T12:07:00Z", "2026-11-02T12:15:00Z")]
    [InlineData(15, "2026-11-02T12:15:00Z", "2026-11-02T12:30:00Z")]
    [InlineData(60, "2026-11-02T12:00:30Z", "2026-11-02T13:00:00Z")]
    [InlineData(5, "2026-11-02T23:58:00Z", "2026-11-03T00:00:00Z")]
    // 100 minutes does not divide a day: the last slot is 23:20, then midnight again.
    [InlineData(100, "2026-11-02T23:30:00Z", "2026-11-03T00:00:00Z")]
    [InlineData(100, "2026-11-03T00:00:00Z", "2026-11-03T01:40:00Z")]
    public void Interval_slots_count_from_local_midnight(int minutes, string after, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            ScheduleCalculator.NextInterval(minutes, "UTC", DateTimeOffset.Parse(after, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Interval_slots_follow_the_local_clock_across_daylight_saving()
    {
        // Europe/Berlin, hourly: 01:00 UTC is 03:00 CEST after the gap on 2027-03-28 (02:00 local does not exist).
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), ScheduleCalculator.NextInterval(60, "Europe/Berlin", new DateTimeOffset(2027, 3, 28, 0, 10, 0, TimeSpan.Zero)));

        // Local midnight in Berlin is 23:00 UTC in winter: the 15-minute slots are local quarter hours.
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.Zero), ScheduleCalculator.NextInterval(1440, "Europe/Berlin", new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)));
    }
}
