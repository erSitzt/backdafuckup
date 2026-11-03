using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;
using Microsoft.Data.SqlClient;

namespace Backdafuckup.Sql.Tests;

[Trait("Category", "Sql")]
public sealed class SqlInventoryTests(SqlServerFixture sql) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public async Task The_inventory_shows_version_rights_databases_and_files_as_the_login_sees_them()
    {
        Assert.SkipUnless(SqlServerFixture.Enabled, "Tests against a SQL Server container need about 2 GB of memory. Set BDFU_SQL_TESTS=1 for the run to enable them.");
        await sql.ExecuteAsync(
            "CREATE DATABASE inv ON PRIMARY (NAME = inv, FILENAME = '/var/opt/mssql/data/inv.mdf', SIZE = 8MB), " +
            "FILEGROUP archive (NAME = inv_2, FILENAME = '/var/opt/mssql/data/inv_2.ndf', SIZE = 8MB) " +
            "LOG ON (NAME = inv_log, FILENAME = '/var/opt/mssql/data/inv_log.ldf', SIZE = 4MB); " +
            "ALTER DATABASE inv SET RECOVERY FULL; " +
            "CREATE LOGIN bdfu_restore WITH PASSWORD = N'Restore-Pa55word!', CHECK_POLICY = OFF; " +
            "ALTER SERVER ROLE dbcreator ADD MEMBER bdfu_restore; " +
            "CREATE LOGIN bdfu_nobody WITH PASSWORD = N'Nobody-Pa55word!', CHECK_POLICY = OFF;");

        // sa: sysadmin, sees everything.
        var admin = Fill(sql.AdminConnectionString);
        Assert.True(admin.IsSysadmin);
        Assert.StartsWith("16.", admin.ProductVersion, StringComparison.Ordinal);
        Assert.Equal("Developer Edition (64-bit)", admin.Edition);
        var inv = admin.Databases.Single(d => d.Name == "inv");
        Assert.Equal(("ONLINE", "FULL", false), (inv.State, inv.RecoveryModel, inv.ReadOnly));
        Assert.Equal(["inv", "inv_2", "inv_log"], inv.Files.Select(f => f.LogicalName).ToList());
        Assert.Equal(["ROWS", "ROWS", "LOG"], inv.Files.Select(f => f.Type).ToList());
        Assert.Equal(8L << 20, inv.Files[0].SizeBytes);
        Assert.Contains(admin.Databases, d => d.Name == "master");

        // A login with dbcreator: not sysadmin, but sees the files of all databases.
        var restore = Fill(sql.ConnectionStringFor("bdfu_restore", "Restore-Pa55word!"));
        Assert.Equal(("bdfu_restore", false, true), (restore.LoginName, restore.IsSysadmin, restore.IsDbCreator));
        Assert.Equal(3, restore.Databases.Single(d => d.Name == "inv").Files.Count);

        // A login without rights: no error, and the reader reports what it may not do.
        var nobody = Fill(sql.ConnectionStringFor("bdfu_nobody", "Nobody-Pa55word!"));
        Assert.Equal((false, false), (nobody.IsSysadmin, nobody.IsDbCreator));
        Assert.Contains(nobody.Databases, d => d.Name == "inv");
    }

    private static SqlInstanceInfo Fill(string connectionString)
    {
        var info = new SqlInstanceInfo { Name = "MSSQLSERVER" };
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        SqlInventoryReader.Fill(info, connection);
        return info;
    }
}
