using Backdafuckup.Contracts;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;

namespace Backdafuckup.Server.Tests;

[Trait("Category", "Unit")]
public sealed class BackupVmsTests
{
    // Monday 13:00 in Berlin; the nightly 22:00 run that started at least 12 h ago is Sunday's (21:00 UTC).
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static BackupJob Nightly(bool enabled = true, ScheduleKind kind = ScheduleKind.Daily) => new()
    {
        Name = "Nightly",
        Enabled = enabled,
        Schedule = new JobSchedule { Kind = kind, TimeOfDay = new TimeOnly(22, 0), TimeZoneId = "Europe/Berlin" },
    };

    private static BackupVmPointDto Point(DateTimeOffset created) => new(Guid.NewGuid(), null, created, "Incremental", "application", null, 1 << 30, 0, false, null);

    [Fact]
    public void A_vm_is_stale_once_it_missed_a_run_that_started_at_least_12_hours_ago()
    {
        var sunday = new DateTimeOffset(2026, 11, 8, 21, 0, 0, TimeSpan.Zero);
        Assert.Equal((BackupVmStates.Ok, (DateTimeOffset?)null), BackupVms.StateOf(Nightly(), Point(sunday.AddMinutes(30)), Now));
        Assert.Equal((BackupVmStates.Stale, (DateTimeOffset?)sunday), BackupVms.StateOf(Nightly(), Point(sunday.AddMinutes(-1)), Now));

        // In the morning the night's run may still be busy: Saturday's restore point is still enough.
        var morning = new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(BackupVmStates.Ok, BackupVms.StateOf(Nightly(), Point(new DateTimeOffset(2026, 11, 7, 21, 30, 0, TimeSpan.Zero)), morning).State);
    }

    [Fact]
    public void Jobs_without_a_schedule_disabled_or_deleted_jobs_and_members_without_a_point_are_named_as_such()
    {
        var old = Point(Now.AddDays(-30));
        Assert.Equal(BackupVmStates.Manual, BackupVms.StateOf(Nightly(kind: ScheduleKind.None), old, Now).State);
        Assert.Equal(BackupVmStates.Disabled, BackupVms.StateOf(Nightly(enabled: false), old, Now).State);
        Assert.Equal(BackupVmStates.NoJob, BackupVms.StateOf(null, old, Now).State);
        Assert.Equal(BackupVmStates.NoPoint, BackupVms.StateOf(Nightly(), null, Now).State);
        Assert.Equal(BackupVmStates.NoPoint, BackupVms.StateOf(Nightly(enabled: false), null, Now).State);
    }

    [Fact]
    public void Unknown_filter_values_are_refused_with_the_allowed_ones()
    {
        Assert.Null(BackupVms.Validate(new BackupVms.Filter(Contents: ["AD", "sql"], Consistency: "Warning", States: ["stale", "nopoint", "ok"], OlderThanDays: 0)));
        Assert.Equal("Unknown content 'dns': use ad, sql.", BackupVms.Validate(new BackupVms.Filter(Contents: ["ad", "dns"])));
        Assert.Equal("Unknown consistency 'fine': use application, crash, warning.", BackupVms.Validate(new BackupVms.Filter(Consistency: "fine")));
        Assert.Equal("Unknown state 'late': use stale, nopoint, ok.", BackupVms.Validate(new BackupVms.Filter(States: ["late"])));
        Assert.Equal("olderThanDays must be 0 or more.", BackupVms.Validate(new BackupVms.Filter(OlderThanDays: -1)));
    }
}
