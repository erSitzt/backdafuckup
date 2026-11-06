using Backdafuckup.Server.Data;
using Backdafuckup.Server.Sql;

namespace Backdafuckup.Server.Tests;

public sealed class SqlBreakReasonTests
{
    private static readonly Guid Fork = Guid.NewGuid();
    private static readonly DateTimeOffset At = new(2026, 11, 6, 15, 30, 0, TimeSpan.Zero);

    private static SqlBackupSetEntity Log(decimal first, decimal last, DateTimeOffset finish, Guid? fork = null, decimal? forkPoint = null) => new()
    {
        Kind = SqlBackupSetKind.Log, FirstLsn = first, LastLsn = last, StartUtc = finish.AddSeconds(-1), FinishUtc = finish,
        FirstRecoveryForkGuid = fork ?? Fork, LastRecoveryForkGuid = fork ?? Fork, ForkPointLsn = forkPoint, BackupSetUuid = Guid.NewGuid(),
    };

    [Fact]
    public void A_restore_over_the_database_is_named_as_such_and_not_as_another_tools_log_backup()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var previous = Log(4_566, 4_567, At);

        // The overwrite went back to an earlier point: the next log starts before the previous one ended.
        var back = Log(4_415, 4_678, At.AddMinutes(12));
        Assert.StartsWith("the database was restored over after the log backup of 2026-11-06 16:30 (a new recovery fork", SqlBackupService.BreakReason([previous, back], back, zone), StringComparison.Ordinal);

        // A new recovery fork, also with a later LSN.
        var forked = Log(4_600, 4_700, At.AddMinutes(12), Guid.NewGuid(), forkPoint: 4_590);
        Assert.StartsWith("the database was restored over", SqlBackupService.BreakReason([previous, forked], forked, zone), StringComparison.Ordinal);

        // A gap forward on the same fork: log backups are missing (another tool, or SIMPLE and back).
        var later = Log(4_600, 4_700, At.AddMinutes(12));
        Assert.StartsWith("log backups are missing between 2026-11-06 16:30 and 2026-11-06 16:41", SqlBackupService.BreakReason([previous, later], later, zone), StringComparison.Ordinal);

        // No log before it: no chain to continue.
        Assert.Equal("no full backup is continued by the log backups", SqlBackupService.BreakReason([back], back, zone));
    }
}
