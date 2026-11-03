using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Attaches a database from the volumes of a publish, and removes it again. Names come from the backup or the user,
/// so they are quoted (identifiers) or passed as parameters; the drop only touches a database whose files all lie in
/// the publish's own folder.
/// </summary>
public static class SqlPublishDatabase
{
    /// <summary>
    /// Grants the SQL Server service (and a Windows account) access to the published files, then attaches them as
    /// <see cref="PublishSqlDatabase.Database"/>. The ACEs land in the overlay; the backup is unchanged.
    /// </summary>
    /// <param name="mounts">Mount folder per <see cref="PublishVolume.Name"/>.</param>
    /// <param name="attached">Called as soon as the database exists on the instance (from then on it must be dropped again).</param>
    public static void Attach(ISqlEnvironment sql, IPublishHost host, PublishSqlDatabase request, IReadOnlyDictionary<string, string> mounts, Action<string> log, Action attached)
    {
        CheckName(request.Database);
        if (request.Files.Count == 0)
        {
            throw new InvalidOperationException($"The publish lists no files for database {Quote(request.Database)}.");
        }

        var instance = sql.Instances().FirstOrDefault(i => string.Equals(i.Name, request.Instance, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This VM has no SQL Server instance {request.Instance}.");
        if (instance.ServiceState is { } state && state != "Running")
        {
            throw new InvalidOperationException($"The SQL Server service {instance.ServiceName} of instance {instance.Name} is {state.ToLowerInvariant()}; start it and publish again.");
        }

        var paths = request.Files.Select(f =>
        {
            if (!mounts.TryGetValue(f.Volume, out var folder))
            {
                throw new InvalidOperationException($"Volume {f.Volume} of file {f.Path} is not part of the publish.");
            }

            return FilePath(folder, f.Path);
        }).ToList();

        using (var connection = sql.Open(instance.Name, request.Account))
        {
            using (var command = Command(connection, "SELECT DB_ID(@name)", ("@name", request.Database)))
            {
                if (command.ExecuteScalar() is { } id && id != DBNull.Value)
                {
                    throw new InvalidOperationException($"Instance {instance.Name} has a database {Quote(request.Database)} already; publish under another name.");
                }
            }

            var accounts = Accounts(instance, request.Account);
            foreach (var path in paths)
            {
                host.GrantAccess(path, accounts, log);
            }

            log($"granted {string.Join(" and ", accounts)} access to the {paths.Count} file(s) of the database (in the overlay; the backup is unchanged).");
            var statement = $"CREATE DATABASE {Quote(request.Database)} ON " +
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
                throw new InvalidOperationException($"Attaching {Quote(request.Database)} to instance {instance.Name} failed: {Explain(ex, instance.Name, request.Account)}", ex);
            }

            string? stateText;
            using (var command = Command(connection, "SELECT state_desc FROM sys.databases WHERE name = @name", ("@name", request.Database)))
            {
                stateText = command.ExecuteScalar() as string;
            }

            log($"attached database {Quote(request.Database)} to instance {instance.Name} ({stateText ?? "state unknown"}), as {request.Account.UserName}.");
        }
    }

    /// <summary>
    /// Takes the database offline (SQL Server then leaves its files alone) and drops it, if it is there and all its files
    /// lie in <paramref name="publishFolder"/>. A database of that name with files elsewhere is not the publish's own.
    /// </summary>
    public static void Drop(ISqlEnvironment sql, string instanceName, SqlAccount account, string database, string publishFolder, Action<string> log)
    {
        using (var connection = sql.Open(instanceName, account))
        {
            int? id = null;
            using (var command = Command(connection, "SELECT database_id FROM sys.databases WHERE name = @name", ("@name", database)))
            {
                if (command.ExecuteScalar() is { } value && value != DBNull.Value)
                {
                    id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
            }

            if (id is null)
            {
                log($"database {Quote(database)} is not attached to instance {instanceName} (any more).");
                return;
            }

            var files = new List<string>();
            using (var command = Command(connection, "SELECT physical_name FROM sys.master_files WHERE database_id = @id", ("@id", id.Value)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    files.Add(reader.GetString(0));
                }
            }

            var prefix = publishFolder.TrimEnd('\\', '/');
            var foreign = files.Where(f => !IsBelow(f, prefix)).ToList();
            if (files.Count == 0 || foreign.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Database {Quote(database)} on instance {instanceName} is not the published one: its files {string.Join(", ", foreign)} lie outside {prefix}. It is left alone.");
            }

            try
            {
                using (var command = Command(connection, $"ALTER DATABASE {Quote(database)} SET OFFLINE WITH ROLLBACK IMMEDIATE"))
                {
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }
            }
            catch (DbException ex)
            {
                // A database whose files are gone (agent restarted) may refuse to go offline; it can still be dropped.
                log($"taking {Quote(database)} offline failed ({ex.Message}); dropping it anyway.");
            }

            using (var command = Command(connection, $"DROP DATABASE {Quote(database)}"))
            {
                command.CommandTimeout = 0;
                command.ExecuteNonQuery();
            }

            log($"removed database {Quote(database)} from instance {instanceName} (taken offline first, so SQL Server did not delete its files).");
        }
    }

    /// <summary>T-SQL QUOTENAME for identifiers: [name] with "]" doubled.</summary>
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    /// <summary>The path of a published file in the guest: the mount folder plus the path within the volume.</summary>
    public static string FilePath(string mountFolder, string pathInVolume) =>
        mountFolder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar +
        string.Join(Path.DirectorySeparatorChar.ToString(), pathInVolume.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsBelow(string path, string folder)
    {
        static string Normalize(string p) => p.Replace('/', '\\');
        return Normalize(path).StartsWith(Normalize(folder) + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckName(string name)
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
    private static List<string> Accounts(SqlInstanceInfo instance, SqlAccount account)
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
    private static string Explain(DbException ex, string instance, SqlAccount account)
    {
        var number = ErrorNumber(ex);
        var reason = number switch
        {
            948 => $"the files come from a newer SQL Server version than instance {instance}. Publish to an instance of the same or a newer version.",
            1827 or 1828 or 1105 => $"the database is larger than instance {instance} allows (SQL Server Express: 10 GB per database, 50 GB from SQL Server 2025).",
            262 or 1802 when ex.Message.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0 =>
                $"{account.UserName} may not create databases on instance {instance}; it needs the server role dbcreator or sysadmin.",
            5120 or 5133 => "SQL Server cannot open a database file in the publish folder.",
            33111 => "the database is encrypted (TDE) and the certificate is missing on this instance.",
            _ => null,
        };
        var detail = number is { } n ? $"SQL Server error {n.ToString(CultureInfo.InvariantCulture)}: {ex.Message}" : ex.Message;
        return reason is null ? detail : $"{reason} ({detail})";
    }

    /// <summary>SqlException.Number of either SQL client (the base class does not have it).</summary>
    public static int? ErrorNumber(DbException ex) =>
        ex.GetType().GetProperty("Number")?.GetValue(ex) is int number ? number : (int?)null;

    private static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
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
