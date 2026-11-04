using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Backs up a database to a <c>.bak</c> file and verifies it (the .bak export: the database is published from a restore
/// point first). Copy-only, so the database's backup chain is not touched; SQL Server's service writes the file, so the
/// folder must be writable for it (the instance's backup folder is). An existing file is never replaced.
/// </summary>
public static class SqlBackupFile
{
    /// <param name="exists">Whether a file exists (tests map the container's paths).</param>
    /// <param name="size">The size of the written file.</param>
    public static (string Path, long Bytes) Backup(
        ISqlEnvironment sql, string instanceName, SqlAccount account, string database, string? folder, string fileName, string description, Action<string> log,
        Func<string, bool>? exists = null, Func<string, long>? size = null)
    {
        SqlAttach.CheckName(database);
        exists ??= File.Exists;
        size ??= path => new FileInfo(path).Length;
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':' }).ToArray()) >= 0)
        {
            throw new InvalidOperationException($"'{fileName}' is not a file name.");
        }

        var instance = sql.Instances().FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This VM has no SQL Server instance {instanceName}.");
        using (var connection = sql.Open(instance.Name, account))
        {
            int engineEdition;
            string? defaultFolder;
            using (var command = SqlAttach.Command(connection,
                "SELECT CAST(SERVERPROPERTY('EngineEdition') AS int), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))"))
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                engineEdition = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                defaultFolder = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var target = string.IsNullOrWhiteSpace(folder) ? instance.BackupFolder ?? defaultFolder : folder;
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new InvalidOperationException($"Instance {instance.Name} has no backup folder; choose a folder.");
            }

            var path = FreePath(target!, fileName, exists);
            var quoted = SqlPublishDatabase.Quote(database);
            var compression = engineEdition != 4;
            log($"backing up {quoted} to {path} (copy-only, with checksums{(compression ? ", compressed" : "; SQL Server Express does not compress backups")}).");
            try
            {
                using (var command = SqlAttach.Command(connection,
                    "BACKUP DATABASE @database TO DISK = @path WITH COPY_ONLY, CHECKSUM, INIT, FORMAT, NAME = @name, DESCRIPTION = @description" + (compression ? ", COMPRESSION" : string.Empty),
                    ("@database", database), ("@path", path), ("@name", $"{database} (backdafuckup export)"), ("@description", Truncate(description, 255))))
                {
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }
            }
            catch (DbException ex)
            {
                throw new InvalidOperationException($"Backing up {quoted} to {path} failed: {Explain(ex, instance, target!)}", ex);
            }

            try
            {
                using (var command = SqlAttach.Command(connection, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", ("@path", path)))
                {
                    command.CommandTimeout = 0;
                    command.ExecuteNonQuery();
                }
            }
            catch (DbException ex)
            {
                throw new InvalidOperationException($"The backup {path} did not pass RESTORE VERIFYONLY: {ex.Message}", ex);
            }

            var bytes = size(path);
            log($"wrote and verified {path} ({bytes:N0} bytes; RESTORE VERIFYONLY with checksums passed).");
            return (path, bytes);
        }
    }

    /// <summary><paramref name="name"/> in the folder, or name_2, name_3, … if it exists.</summary>
    internal static string FreePath(string folder, string name, Func<string, bool> exists)
    {
        var separator = folder.IndexOf('\\') < 0 && folder.IndexOf('/') >= 0 ? '/' : '\\';
        var root = folder.TrimEnd('\\', '/') + separator;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var path = root + name;
        for (var n = 2; exists(path); n++)
        {
            path = root + stem + "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension;
        }

        return path;
    }

    private static string Explain(DbException ex, SqlInstanceInfo instance, string folder)
    {
        var number = SqlAttach.ErrorNumber(ex);
        // Operating system error 5 (access denied); the text is localized ("Betriebssystemfehler 5(Zugriff verweigert)").
        if (number == 3201 && ex.Message.IndexOf(" 5(", StringComparison.Ordinal) >= 0)
        {
            return $"the SQL Server service ({instance.ServiceAccount ?? @"NT SERVICE\" + instance.ServiceName}) may not write to {folder}. Choose the instance's backup folder " +
                $"({instance.BackupFolder ?? "see its properties"}) or give the service write access to the folder. (SQL Server error {number}: {ex.Message})";
        }

        if (number == 3201)
        {
            return $"SQL Server cannot create the file in {folder} (does the folder exist?). (SQL Server error {number}: {ex.Message})";
        }

        return number is { } n ? $"SQL Server error {n}: {ex.Message}" : ex.Message;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length);
}
