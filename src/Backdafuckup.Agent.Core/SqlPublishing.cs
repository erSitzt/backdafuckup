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
        if (request.Files.Count == 0)
        {
            throw new InvalidOperationException($"The publish lists no files for database {Quote(request.Database)}.");
        }

        var paths = request.Files.Select(f =>
        {
            if (!mounts.TryGetValue(f.Volume, out var folder))
            {
                throw new InvalidOperationException($"Volume {f.Volume} of file {f.Path} is not part of the publish.");
            }

            return FilePath(folder, f.Path);
        }).ToList();

        SqlAttach.AttachFiles(sql, request.Instance, request.Account, request.Database, paths, (path, accounts) => host.GrantAccess(path, accounts, log),
            "in the overlay; the backup is unchanged", log, attached, describe: false);
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
            using (var command = SqlAttach.Command(connection, "SELECT database_id FROM sys.databases WHERE name = @name", ("@name", database)))
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
            using (var command = SqlAttach.Command(connection, "SELECT physical_name FROM sys.master_files WHERE database_id = @id", ("@id", id.Value)))
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
                using (var command = SqlAttach.Command(connection, $"ALTER DATABASE {Quote(database)} SET OFFLINE WITH ROLLBACK IMMEDIATE"))
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

            using (var command = SqlAttach.Command(connection, $"DROP DATABASE {Quote(database)}"))
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
}
