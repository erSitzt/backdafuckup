namespace Backdafuckup.Server.Sql;

/// <summary>SQL Server's system databases.</summary>
public static class SqlSystemDatabases
{
    public static bool Contains(string database) =>
        database.Equals("master", StringComparison.OrdinalIgnoreCase) || database.Equals("model", StringComparison.OrdinalIgnoreCase) ||
        database.Equals("msdb", StringComparison.OrdinalIgnoreCase) || database.Equals("tempdb", StringComparison.OrdinalIgnoreCase);
}
