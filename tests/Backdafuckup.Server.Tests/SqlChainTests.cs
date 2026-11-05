using Backdafuckup.Server.Data;
using Backdafuckup.Server.Sql;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// The log chain of a database: coverage, gaps and restore plans. The LSNs are those SQL Server 2022 recorded in spike
/// 2b (snapshot full, log, native full, log, log), so the rules match what RESTORE accepts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SqlChainTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Fork = Guid.Parse("1bff0435-2749-4242-9314-2becc19dcf73");

    // Spike 2b: snapshot full, log 1, native full, log 2, log 3 (in this order, a few minutes apart here).
    private static readonly SqlChainSet Snapshot = Set(SqlBackupSetKind.SnapshotFull, 42000000027200212m, 42000000038400001m, 0);
    private static readonly SqlChainSet Log1 = Set(SqlBackupSetKind.Log, 42000000027200212m, 42000000042400001m, 15);
    private static readonly SqlChainSet Native = Set(SqlBackupSetKind.NativeFull, 42000000052800001m, 42000000055200001m, 20);
    private static readonly SqlChainSet Log2 = Set(SqlBackupSetKind.Log, 42000000042400001m, 42000000059200001m, 30);
    private static readonly SqlChainSet Log3 = Set(SqlBackupSetKind.Log, 42000000059200001m, 42000000064000001m, 45);

    [Fact]
    public void A_snapshot_full_and_its_logs_cover_the_time_from_the_full_to_the_last_log()
    {
        var chain = new SqlChain([Snapshot, Log1, Log2, Log3]);

        var (spans, gaps) = chain.Coverage();

        var span = Assert.Single(spans);
        Assert.Equal((T0, T0.AddMinutes(45), 3), (span.FromUtc, span.ToUtc, span.Logs));
        Assert.Empty(gaps);
        Assert.Equal(T0.AddMinutes(45), chain.Latest);
    }

    [Fact]
    public void A_point_in_time_uses_the_newest_base_before_it_and_the_logs_up_to_the_one_containing_it()
    {
        var chain = new SqlChain([Snapshot, Log1, Native, Log2, Log3]);

        // Before the native full: the snapshot and log 1 (which ends at 10:15).
        var early = chain.Plan(T0.AddMinutes(12));
        Assert.Equal(Snapshot.Id, early.Base.Id);
        Assert.Equal([Log1.Id], early.Logs.Select(l => l.Id));

        // After the native full: it and log 2 (whose range contains the native full's last LSN), no log 1.
        var late = chain.Plan(T0.AddMinutes(40).AddSeconds(3.5));
        Assert.Equal(Native.Id, late.Base.Id);
        Assert.Equal([Log2.Id, Log3.Id], late.Logs.Select(l => l.Id));
        Assert.Equal(T0.AddMinutes(40).AddSeconds(3.5), late.StopAtUtc);

        // STOPAT is SQL Server's local time: UTC+1 here.
        Assert.Equal("2026-11-05T11:40:03.500", late.StopAtLocal);
    }

    [Fact]
    public void The_latest_point_applies_every_log_without_stopat()
    {
        var plan = new SqlChain([Snapshot, Log1, Native, Log2, Log3]).Plan(null);

        Assert.Equal(Native.Id, plan.Base.Id);
        Assert.Equal([Log2.Id, Log3.Id], plan.Logs.Select(l => l.Id));
        Assert.Null(plan.StopAtLocal);
        Assert.Equal(T0.AddMinutes(45), plan.ReachedUtc);
    }

    [Fact]
    public void The_moment_of_a_base_needs_no_log()
    {
        var plan = new SqlChain([Snapshot, Log1]).Plan(T0);

        Assert.Equal(Snapshot.Id, plan.Base.Id);
        Assert.Empty(plan.Logs);
        Assert.Null(plan.StopAtLocal);
    }

    [Fact]
    public void A_missing_log_backup_splits_the_coverage_and_a_time_in_the_gap_names_the_nearest_points()
    {
        // Log 2 was taken by another tool: log 3 does not continue log 1. The native full restarts the chain.
        var chain = new SqlChain([Snapshot, Log1, Native, Log3]);

        var (spans, gaps) = chain.Coverage();

        Assert.Equal([(T0, T0.AddMinutes(15)), (T0.AddMinutes(20), T0.AddMinutes(20))], spans.Select(s => (s.FromUtc, s.ToUtc)));
        var gap = Assert.Single(gaps);
        Assert.Contains("no log backups from 2026-11-05 10:15:00 UTC", gap.Reason, StringComparison.Ordinal);

        var error = Assert.Throws<SqlChainException>(() => chain.Plan(T0.AddMinutes(17)));
        Assert.Contains("falls into a gap", error.Message, StringComparison.Ordinal);
        Assert.Contains("2026-11-05 10:15:00 UTC and 2026-11-05 10:20:00 UTC", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_backup_on_a_new_recovery_fork_does_not_continue_the_chain()
    {
        // After an overwrite the database runs on a new fork; its first log backup starts at the restored LSN.
        var newFork = Guid.NewGuid();
        var afterRestore = Set(SqlBackupSetKind.Log, 42000000042400001m, 42000000088000001m, 50) with
        {
            LastRecoveryForkGuid = newFork,
            ForkPointLsn = 42000000042400001m,
        };
        var chain = new SqlChain([Snapshot, Log1, afterRestore]);

        Assert.Equal(T0.AddMinutes(15), chain.Latest);
        Assert.Throws<SqlChainException>(() => chain.Plan(T0.AddMinutes(48)));
    }

    [Fact]
    public void Times_outside_the_backups_and_unknown_time_zones_are_refused_with_a_reason()
    {
        var chain = new SqlChain([Snapshot, Log1]);

        Assert.Contains("before the oldest full backup", Assert.Throws<SqlChainException>(() => chain.Plan(T0.AddMinutes(-1))).Message, StringComparison.Ordinal);
        Assert.Contains("no log backup reaches it yet", Assert.Throws<SqlChainException>(() => chain.Plan(T0.AddMinutes(16))).Message, StringComparison.Ordinal);
        Assert.Contains("no full backup", Assert.Throws<SqlChainException>(() => new SqlChain([Log1]).Plan(null)).Message, StringComparison.Ordinal);

        var unknownZone = new SqlChain([Snapshot, Log1 with { UtcOffsetMinutes = null }]);
        Assert.Contains("did not record its time zone", Assert.Throws<SqlChainException>(() => unknownZone.Plan(T0.AddMinutes(10))).Message, StringComparison.Ordinal);
        Assert.Null(unknownZone.Plan(null).StopAtLocal);
    }

    private static SqlChainSet Set(SqlBackupSetKind kind, decimal first, decimal last, int minutes) => new(
        Guid.NewGuid(), kind, first, last, Fork, Fork, null, T0.AddMinutes(minutes).AddSeconds(-1), T0.AddMinutes(minutes), 60, 1000);
}
