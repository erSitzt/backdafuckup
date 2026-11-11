using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data.Migrations;
using Backdafuckup.Server.Sql;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Backdafuckup.Server.Tests;

public sealed class RestorePointConsistencyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(RestorePointConsistency.SqlSnapshotNote, false)]
    [InlineData("Windows released the writes after its limit of 10 s (VSS_E_HOLD_WRITES_TIMEOUT).", true)]
    [InlineData(RestorePointConsistency.SqlSnapshotNote + " Outside SQL Server, the 2 disks were captured one after another over 5 s, not at one moment.", true)]
    public void A_reason_is_a_warning_except_the_note_of_a_sql_server_snapshot_that_recorded_every_database(string? reason, bool warning) =>
        Assert.Equal(warning, RestorePointConsistency.IsWarning(reason));

    [Fact]
    public void Only_user_databases_count_for_sql_consistency()
    {
        // A SQL Server snapshot cannot suspend the system databases: they are crash-consistent there by design.
        var metadata = new StoredGuestMetadata
        {
            SqlDatabases =
            [
                new StoredSqlDatabase { Database = "master", Consistency = "crash" },
                new StoredSqlDatabase { Database = "MSDB", Consistency = "crash" },
                new StoredSqlDatabase { Database = "shop", Consistency = "application" },
                new StoredSqlDatabase { Database = "archive", Consistency = "crash" },
            ],
        };

        Assert.Equal((2, 1), metadata.UserDatabaseConsistency());
    }

    [Fact]
    public void The_migration_sets_the_flag_of_existing_restore_points_by_the_same_rule()
    {
        var sql = Assert.Single(new RestorePointConsistencyWarning().UpOperations.OfType<SqlOperation>()).Sql;

        Assert.Contains("consistency_reason IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains($"'{RestorePointConsistency.SqlSnapshotNote}'", sql, StringComparison.Ordinal);
    }
}
