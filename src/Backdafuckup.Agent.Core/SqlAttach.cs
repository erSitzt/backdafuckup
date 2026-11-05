using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Attaches database files to a local instance (publish and restore), describes the attached database, and deletes the
/// files of a failed restore. Names come from the backup or the user: identifiers are quoted, values are parameters,
/// file paths are N'…' literals with quotes doubled (CREATE DATABASE takes no parameters).
/// </summary>
public static class SqlAttach
{
    /// <summary>
    /// Checks the instance and the name, gives SQL Server access to the files (<paramref name="grant"/> per file with the
    /// accounts: service SID, a real service account, a Windows account that attaches), and attaches them.
    /// </summary>
    /// <param name="where">Where the access is changed, for the log (e.g. "in the overlay; the backup is unchanged").</param>
    /// <param name="attached">Called as soon as the database exists on the instance.</param>
    /// <param name="describe">Logs owner, settings and users without a login on the instance after the attach.</param>
    public static void AttachFiles(
        ISqlEnvironment sql, string instanceName, SqlAccount account, string database, IReadOnlyList<string> paths,
        Action<string, IReadOnlyList<string>> grant, string where, Action<string> log, Action attached, bool describe)
    {
        CheckName(database);
        if (paths.Count == 0)
        {
            throw new InvalidOperationException($"No files to attach as {SqlPublishDatabase.Quote(database)}.");
        }

        var instance = sql.Instances().FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This VM has no SQL Server instance {instanceName}.");
        if (instance.ServiceState is { } state && state != "Running")
        {
            throw new InvalidOperationException($"The SQL Server service {instance.ServiceName} of instance {instance.Name} is {state.ToLowerInvariant()}; start it and try again.");
        }

        using (var connection = sql.Open(instance.Name, account))
        {
            using (var command = Command(connection, "SELECT DB_ID(@name)", ("@name", database)))
            {
                if (command.ExecuteScalar() is { } id && id != DBNull.Value)
                {
                    throw new InvalidOperationException($"Instance {instance.Name} has a database {SqlPublishDatabase.Quote(database)} already; choose another name.");
                }
            }

            var accounts = Accounts(instance, account);
            foreach (var path in paths)
            {
                grant(path, accounts);
            }

            log($"granted {string.Join(" and ", accounts)} access to the {paths.Count} file(s) of the database ({where}).");
            var statement = $"CREATE DATABASE {SqlPublishDatabase.Quote(database)} ON " +
                string.Join(", ", paths.Select(p => $"(FILENAME = N'{p.Replace("'", "''")}')")) + " FOR ATTACH";
            try
            {
                using (var command = Command(connection, statement))
                {
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }

                attached();
            }
            catch (DbException ex)
            {
                throw new InvalidOperationException($"Attaching {SqlPublishDatabase.Quote(database)} to instance {instance.Name} failed: {Explain(ex, instance.Name, account)}", ex);
            }

            string? stateText;
            using (var command = Command(connection, "SELECT state_desc FROM sys.databases WHERE name = @name", ("@name", database)))
            {
                stateText = command.ExecuteScalar() as string;
            }

            log($"attached database {SqlPublishDatabase.Quote(database)} to instance {instance.Name} ({stateText ?? "state unknown"}), as {account.UserName}.");
            var canOpen = CheckAccess(connection, database, account, log);
            if (describe)
            {
                Describe(connection, instance.Name, database, canOpen, log);
            }
        }
    }

    /// <summary>
    /// An attach makes the attaching login the owner in master, but the database's own dbo user keeps the SID of the
    /// original owner, so a login that is not sysadmin cannot open the database it attached; it cannot take ownership
    /// either (SQL Server refuses ALTER AUTHORIZATION without access). Returns whether the account can open it, and
    /// says what a sysadmin can do if not.
    /// </summary>
    private static bool CheckAccess(DbConnection connection, string database, SqlAccount account, Action<string> log)
    {
        var quoted = SqlPublishDatabase.Quote(database);
        try
        {
            using (var command = Command(connection, "SELECT HAS_DBACCESS(@name)", ("@name", database)))
            {
                if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
                {
                    return true;
                }
            }
        }
        catch (DbException)
        {
        }

        log($"{account.UserName} cannot open {quoted} itself: SQL Server made it the owner, but the database's dbo user keeps the original owner, and only a sysadmin " +
            $"(or the original owner) can open it. To give it to {account.UserName}, a sysadmin runs ALTER AUTHORIZATION ON DATABASE::{quoted} TO [{account.UserName.Replace("]", "]]")}]; " +
            "with a sysadmin account this does not happen.");
        return false;
    }

    /// <summary>
    /// What the attach changed or left to do: owner (the attaching login), TRUSTWORTHY (always off after an attach),
    /// Service Broker, recovery model, and database users whose login does not exist on this instance.
    /// </summary>
    private static void Describe(DbConnection connection, string instance, string database, bool canOpen, Action<string> log)
    {
        try
        {
            using (var command = Command(connection,
                "SELECT SUSER_SNAME(owner_sid), is_trustworthy_on, is_broker_enabled, recovery_model_desc, compatibility_level FROM sys.databases WHERE name = @name",
                ("@name", database)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    log($"database {SqlPublishDatabase.Quote(database)}: owner {(reader.IsDBNull(0) ? "unknown" : reader.GetString(0))}, " +
                        $"TRUSTWORTHY {(reader.GetBoolean(1) ? "on" : "off (SQL Server turns it off when attaching)")}, " +
                        $"Service Broker {(reader.GetBoolean(2) ? "on" : "off")}, recovery model {(reader.IsDBNull(3) ? "unknown" : reader.GetString(3))}, " +
                        $"compatibility level {(reader.IsDBNull(4) ? "unknown" : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))}.");
                }
            }

            if (!canOpen)
            {
                // Its users can only be read inside it.
                return;
            }

            var orphans = new List<string>();
            using (var command = Command(connection,
                $"SELECT dp.name, dp.type_desc FROM {SqlPublishDatabase.Quote(database)}.sys.database_principals AS dp " +
                "LEFT JOIN sys.server_principals AS sp ON sp.sid = dp.sid " +
                "WHERE sp.sid IS NULL AND dp.type IN ('S', 'U', 'G') AND dp.principal_id > 4 AND dp.sid IS NOT NULL " +
                "AND dp.authentication_type_desc IN ('INSTANCE', 'WINDOWS') ORDER BY dp.name"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    orphans.Add($"{reader.GetString(0)} ({reader.GetString(1).Replace('_', ' ').ToLowerInvariant()})");
                }
            }

            if (orphans.Count > 0)
            {
                log($"users in {SqlPublishDatabase.Quote(database)} without a login on instance {instance}: {string.Join(", ", orphans)}. " +
                    "Map each to a login with ALTER USER [user] WITH LOGIN = [login], or create the login first.");
            }
        }
        catch (DbException ex)
        {
            log($"the attached database could not be described: {ex.Message}");
        }
    }

    /// <summary>Deletes restored files (regular files only, through the restore file system and its guards); returns the failures.</summary>
    public static List<string> DeleteFiles(IRestoreFileSystem fs, IReadOnlyList<string> paths, Action<string> log)
    {
        var failed = new List<string>();
        var deleted = 0;
        foreach (var path in paths)
        {
            try
            {
                if (fs.Probe(path) == RestorePathKind.Missing)
                {
                    continue;
                }

                fs.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                failed.Add($"{path}: {ex.Message}");
            }
        }

        log(failed.Count == 0
            ? $"deleted the {deleted} restored file(s) again."
            : $"deleted {deleted} restored file(s); {failed.Count} could not be deleted: {string.Join("; ", failed)}");
        return failed;
    }

    internal static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("The database name must have 1 to 128 characters and no control characters.");
        }
    }

    /// <summary>
    /// Who needs access to the files: the service's own SID (SQL Server opens the files with it), its account if that is
    /// a real account, and a Windows account that attaches (SQL Server checks the files with its token, too).
    /// </summary>
    internal static List<string> Accounts(SqlInstanceInfo instance, SqlAccount account)
    {
        var accounts = new List<string>();
        if (!string.IsNullOrEmpty(instance.ServiceName))
        {
            accounts.Add(@"NT SERVICE\" + instance.ServiceName);
        }

        var service = instance.ServiceAccount;
        if (!string.IsNullOrWhiteSpace(service) && !IsBuiltIn(service!))
        {
            accounts.Add(service!);
        }

        if (account.Kind == SqlAccountKinds.Windows)
        {
            accounts.Add(account.UserName);
        }

        return accounts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsBuiltIn(string account) =>
        account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase) ||
        account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase) ||
        account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
        account.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase);

    /// <summary>The usual reasons an attach fails, in words; the SQL Server message follows.</summary>
    internal static string Explain(DbException ex, string instance, SqlAccount account)
    {
        var number = ErrorNumber(ex);
        var reason = number switch
        {
            948 => $"the files come from a newer SQL Server version than instance {instance}. Use an instance of the same or a newer version.",
            1827 or 1828 or 1105 => $"the database is larger than instance {instance} allows (SQL Server Express: 10 GB per database, 50 GB from SQL Server 2025).",
            262 or 1802 when ex.Message.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0 =>
                $"{account.UserName} may not create databases on instance {instance}; it needs the server role dbcreator or sysadmin.",
            5120 or 5133 => "SQL Server cannot open a database file (the service and the account need read and write access to it and its folder).",
            33111 => "the database is encrypted (TDE) and the certificate is missing on this instance.",
            _ => null,
        };
        var detail = number is { } n ? $"SQL Server error {n.ToString(CultureInfo.InvariantCulture)}: {ex.Message}" : ex.Message;
        return reason is null ? detail : $"{reason} ({detail})";
    }

    /// <summary>SqlException.Number of either SQL client (the base class does not have it).</summary>
    public static int? ErrorNumber(DbException ex) =>
        ex.GetType().GetProperty("Number")?.GetValue(ex) is int number ? number : (int?)null;

    internal static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 120;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
