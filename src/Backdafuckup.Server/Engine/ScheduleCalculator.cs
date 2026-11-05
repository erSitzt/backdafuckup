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

    /// <summary>
    /// The first slot strictly after <paramref name="afterUtc"/> of an interval schedule: every
    /// <paramref name="minutes"/> from local midnight of <paramref name="timeZoneId"/> (e.g. :00/:15/:30/:45); a day
    /// that is not a multiple of the interval starts again at midnight.
    /// </summary>
    public static DateTimeOffset NextInterval(int minutes, string timeZoneId, DateTimeOffset afterUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        var zone = FindZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime;
        var midnight = local.Date;
        var elapsed = (int)Math.Floor((local - midnight).TotalMinutes);
        for (var slot = ((elapsed / minutes) + 1) * minutes; ; slot += minutes)
        {
            var candidate = slot >= 24 * 60 ? ToUtc(midnight.AddDays(1), zone) : ToUtc(midnight.AddMinutes(slot), zone);
            if (candidate > afterUtc)
            {
                return candidate;
            }

            if (slot >= 24 * 60)
            {
                return NextInterval(minutes, timeZoneId, candidate);
            }
        }
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
