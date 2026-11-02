using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.Engine;

/// <summary>Computes the next run of a daily/weekly schedule in its time zone (DST aware).</summary>
public static class ScheduleCalculator
{
    /// <summary>Returns the first run strictly after <paramref name="afterUtc"/>, or null if the job is not scheduled.</summary>
    public static DateTimeOffset? Next(JobSchedule schedule, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Kind == ScheduleKind.None || (schedule.Kind == ScheduleKind.Weekly && schedule.DaysOfWeek == 0))
        {
            return null;
        }

        var zone = FindZone(schedule.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(afterUtc, zone);
        var date = DateOnly.FromDateTime(localNow.DateTime);

        for (var i = 0; i <= 8; i++)
        {
            var day = date.AddDays(i);
            if (schedule.Kind == ScheduleKind.Weekly && (schedule.DaysOfWeek & (1 << (int)day.DayOfWeek)) == 0)
            {
                continue;
            }

            var candidate = ToUtc(day.ToDateTime(schedule.TimeOfDay), zone);
            if (candidate > afterUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    public static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "UTC" : id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Spring forward: the wall-clock time does not exist; run at the first valid minute after the gap.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        // Fall back: the time exists twice; use the first occurrence (the larger offset).
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
