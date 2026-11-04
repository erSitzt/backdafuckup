using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Overwrites a database with restored files: takes it offline, moves its files aside (<c>*.bdfu-replaced</c>), puts the
/// restored files (written before as <c>*.bdfu-new</c>) in their place and brings it online. The database itself is never
/// dropped, so owner, TRUSTWORTHY, users and Service Broker stay as they are in master. If bringing it online fails, it
/// goes back to its own files and online again.
/// <para>
/// SQL Server accepts any database's files on <c>SET ONLINE</c>, so the server only offers this for the database the
/// backup was taken of, and the files must be exactly the ones the database has now.
/// </para>
/// <para>
/// A journal (no account in it) records each step before it happens. After an agent crash the files are put back by
/// <see cref="RecoverFiles"/>; the database then stays offline until someone brings it online.
/// </para>
/// </summary>
public static class SqlReplace
{
    public const string NewSuffix = SqlReplaceNames.NewSuffix;

    public const string ReplacedSuffix = SqlReplaceNames.ReplacedSuffix;

    public const string JournalFile = "sql-replace.json";

    /// <param name="files">The database's files as they are now (and as they are in the backup); the restored ones lie beside them with <see cref="NewSuffix"/>.</param>
    public static void Replace(
        ISqlEnvironment sql, IRestoreFileSystem fs, string journalDirectory, string instanceName, SqlAccount account, string database,
        IReadOnlyList<string> files, bool keepReplaced, Action<string> log)
    {
        SqlAttach.CheckName(database);
        var instance = sql.Instances().FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This VM has no SQL Server instance {instanceName}.");
        if (instance.ServiceState is { } serviceState && serviceState != "Running")
        {
            throw new InvalidOperationException($"The SQL Server service {instance.ServiceName} of instance {instance.Name} is {serviceState.ToLowerInvariant()}; start it and try again.");
        }

        var name = SqlPublishDatabase.Quote(database);
        using (var connection = sql.Open(instance.Name, account))
        {
            var (id, state) = Check(connection, instance.Name, database, files);
            foreach (var file in files)
            {
                if (fs.Probe(file + NewSuffix) != RestorePathKind.File)
                {
                    throw new InvalidOperationException($"The restored file {file + NewSuffix} is missing; nothing was changed.");
                }

                if (fs.Probe(file + ReplacedSuffix) != RestorePathKind.Missing)
                {
                    throw new InvalidOperationException($"{file + ReplacedSuffix} exists (kept by an earlier overwrite); move or delete it first. Nothing was changed.");
                }
            }

            var journal = new Journal { Instance = instance.Name, Database = database, Files = files.ToList() };
            var journalPath = Path.Combine(journalDirectory, JournalFile);
            Write(journalPath, journal);
            var online = false;
            try
            {
                if (state != "OFFLINE")
                {
                    Execute(connection, $"ALTER DATABASE {name} SET OFFLINE WITH ROLLBACK IMMEDIATE");
                    log($"took {name} on instance {instance.Name} offline (open transactions rolled back).");
                }

                journal.Offline = true;
                Write(journalPath, journal);
                foreach (var file in files)
                {
                    journal.Swapped.Add(file);
                    Write(journalPath, journal);
                    if (fs.Probe(file) == RestorePathKind.File)
                    {
                        fs.Rename(file, file + ReplacedSuffix);
                        journal.MovedAside.Add(file);
                        Write(journalPath, journal);
                    }

                    fs.Rename(file + NewSuffix, file);

                    // Only the service: SET ONLINE opens the files as the service, unlike an attach, which also checks the
                    // attaching account (and SQL Server keeps these entries, unlike after an attach).
                    foreach (var grantee in SqlAttach.Accounts(instance, account).Where(a => !string.Equals(a, account.UserName, StringComparison.OrdinalIgnoreCase)))
                    {
                        fs.GrantFullControl(file, grantee);
                    }
                }

                log($"put the {files.Count} restored file(s) in place; the database's own files are kept as *{ReplacedSuffix} until it is online.");
                Execute(connection, $"ALTER DATABASE {name} SET ONLINE");
                EnsureOnline(connection, database, log);
                online = true;
            }
            catch (Exception ex) when (!online && (ex is DbException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException))
            {
                var reason = ex is DbException db ? SqlAttach.Explain(db, instance.Name, account) : ex.Message;
                log($"overwriting {name} failed ({reason}); putting its own files back.");
                var back = Rollback(connection, fs, journal, journalPath, name, wasOnline: state != "OFFLINE", log);
                throw new InvalidOperationException(
                    $"Overwriting {name} on instance {instance.Name} failed: {reason}. " +
                    (back ? $"The database has its own files again and is {(state != "OFFLINE" ? "online" : "offline, as before")}; nothing was changed."
                          : $"Putting its own files back failed; see the log: the files are kept as *{ReplacedSuffix} and *{NewSuffix}."),
                    ex);
            }

            log($"{name} on instance {instance.Name} is online with the restored files.");
            if (!keepReplaced)
            {
                var failed = SqlAttach.DeleteFiles(fs, journal.MovedAside.Select(f => f + ReplacedSuffix).ToList(), _ => { });
                log(failed.Count == 0
                    ? $"deleted the database's previous files (*{ReplacedSuffix})."
                    : $"the previous files could not all be deleted; delete them by hand: {string.Join("; ", failed)}");
            }
            else if (journal.MovedAside.Count > 0)
            {
                log($"kept the database's previous files: {string.Join(", ", journal.MovedAside.Select(f => f + ReplacedSuffix))}.");
            }

            Delete(journalPath);
        }
    }

    /// <summary>
    /// After an agent crash during an overwrite: puts the database's own files back and removes the journal. The account
    /// is not stored, so the database is left offline; the log says how to bring it online.
    /// </summary>
    public static void RecoverFiles(IRestoreFileSystem fs, string journalDirectory, Action<string> log)
    {
        var journalPath = Path.Combine(journalDirectory, JournalFile);
        if (!File.Exists(journalPath))
        {
            return;
        }

        Journal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath), AgentJson.Options);
        }
        catch (JsonException ex)
        {
            log($"The journal of an interrupted SQL Server overwrite ({journalPath}) cannot be read: {ex.Message}");
            return;
        }

        if (journal is null)
        {
            Delete(journalPath);
            return;
        }

        var problems = PutBack(fs, journal, log);
        log(problems.Count == 0
            ? $"An overwrite of [{journal.Database}] on instance {journal.Instance} was interrupted; its own files are back in place. " +
              (journal.Offline ? $"It is offline: bring it online with ALTER DATABASE [{journal.Database.Replace("]", "]]")}] SET ONLINE." : "It was not taken offline.")
            : $"An overwrite of [{journal.Database}] on instance {journal.Instance} was interrupted, and its files could not all be put back: {string.Join("; ", problems)}");
        if (problems.Count == 0)
        {
            Delete(journalPath);
        }
    }

    /// <summary>The database exists and may be overwritten; its files are exactly <paramref name="files"/>. Returns its id and state.</summary>
    private static (int Id, string State) Check(DbConnection connection, string instance, string database, IReadOnlyList<string> files)
    {
        int id;
        string state;
        bool isSnapshot, inGroup, replicated;
        using (var command = SqlAttach.Command(connection,
            "SELECT database_id, state_desc, CAST(CASE WHEN source_database_id IS NULL THEN 0 ELSE 1 END AS bit), CAST(CASE WHEN replica_id IS NULL THEN 0 ELSE 1 END AS bit), " +
            "CAST(CASE WHEN is_published = 1 OR is_subscribed = 1 OR is_merge_published = 1 OR is_distributor = 1 THEN 1 ELSE 0 END AS bit) FROM sys.databases WHERE name = @name",
            ("@name", database)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Instance {instance} has no database {SqlPublishDatabase.Quote(database)} to overwrite.");
            }

            id = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            state = reader.GetString(1);
            isSnapshot = reader.GetBoolean(2);
            inGroup = reader.GetBoolean(3);
            replicated = reader.GetBoolean(4);
        }

        var quoted = SqlPublishDatabase.Quote(database);
        if (id <= 4)
        {
            throw new InvalidOperationException($"{quoted} is a system database; system databases are restored together with the VM.");
        }

        if (isSnapshot || inGroup || replicated)
        {
            throw new InvalidOperationException($"{quoted} is {(isSnapshot ? "a database snapshot" : inGroup ? "in an availability group" : "part of a replication")}; it cannot be overwritten here. Restore it as a new database instead.");
        }

        if (state is "RESTORING" or "RECOVERING" or "COPYING")
        {
            throw new InvalidOperationException($"{quoted} is {state.ToLowerInvariant()}; wait until it is online (or offline) and try again.");
        }

        using (var command = SqlAttach.Command(connection,
            "SELECT (SELECT COUNT(*) FROM sys.databases WHERE source_database_id = @id) + (SELECT COUNT(*) FROM sys.database_mirroring WHERE database_id = @id AND mirroring_guid IS NOT NULL)",
            ("@id", id)))
        {
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException($"{quoted} has database snapshots or is mirrored; remove them first, or restore it as a new database instead.");
            }
        }

        var live = new List<(string Path, string Type)>();
        using (var command = SqlAttach.Command(connection, "SELECT physical_name, type_desc FROM sys.master_files WHERE database_id = @id ORDER BY file_id", ("@id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                live.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (live.FirstOrDefault(f => f.Type != "ROWS" && f.Type != "LOG") is { Path: { } } other)
        {
            throw new InvalidOperationException($"{quoted} has a {other.Type.ToLowerInvariant()} file ({other.Path}); databases with such files cannot be overwritten yet.");
        }

        var now = live.Select(f => f.Path).ToList();
        if (now.Count != files.Count || now.Except(files, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidOperationException(
                $"The files of {quoted} are not the ones in the backup (now: {string.Join(", ", now)}; backup: {string.Join(", ", files)}). " +
                "Restore it as a new database instead.");
        }

        return (id, state);
    }

    private static void EnsureOnline(DbConnection connection, string database, Action<string> log)
    {
        using (var command = SqlAttach.Command(connection, "SELECT state_desc, user_access_desc FROM sys.databases WHERE name = @name", ("@name", database)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ONLINE")
            {
                throw new InvalidOperationException($"{SqlPublishDatabase.Quote(database)} did not come online.");
            }

            if (reader.GetString(1) == "MULTI_USER")
            {
                return;
            }
        }

        Execute(connection, $"ALTER DATABASE {SqlPublishDatabase.Quote(database)} SET MULTI_USER");
        log($"set {SqlPublishDatabase.Quote(database)} to MULTI_USER again.");
    }

    /// <summary>Offline, the database's own files back, online again if it was; true if it worked.</summary>
    private static bool Rollback(DbConnection connection, IRestoreFileSystem fs, Journal journal, string journalPath, string name, bool wasOnline, Action<string> log)
    {
        if (journal.Offline)
        {
            try
            {
                Execute(connection, $"ALTER DATABASE {name} SET OFFLINE WITH ROLLBACK IMMEDIATE");
            }
            catch (DbException)
            {
                // Not online (it failed to come online): it is closed already.
            }
        }

        var problems = PutBack(fs, journal, log);
        if (problems.Count > 0)
        {
            log($"putting the files back failed: {string.Join("; ", problems)}");
            return false;
        }

        Delete(journalPath);
        if (!wasOnline)
        {
            return true;
        }

        try
        {
            Execute(connection, $"ALTER DATABASE {name} SET ONLINE");
            EnsureOnline(connection, journal.Database, log);
            log($"{name} is online again with its own files.");
            return true;
        }
        catch (Exception ex) when (ex is DbException || ex is InvalidOperationException)
        {
            log($"{name} has its own files again but did not come online: {ex.Message}");
            return false;
        }
    }

    /// <summary>Restored files back to *.bdfu-new, the database's own files back to their names; returns what failed.</summary>
    private static List<string> PutBack(IRestoreFileSystem fs, Journal journal, Action<string> log)
    {
        var problems = new List<string>();
        foreach (var file in journal.Swapped.AsEnumerable().Reverse())
        {
            try
            {
                if (fs.Probe(file) == RestorePathKind.File && fs.Probe(file + NewSuffix) == RestorePathKind.Missing && journal.MovedAside.Contains(file))
                {
                    fs.Rename(file, file + NewSuffix);
                }

                if (journal.MovedAside.Contains(file) && fs.Probe(file + ReplacedSuffix) == RestorePathKind.File)
                {
                    fs.Rename(file + ReplacedSuffix, file);
                }
                else if (!journal.MovedAside.Contains(file) && fs.Probe(file) == RestorePathKind.Missing && fs.Probe(file + ReplacedSuffix) == RestorePathKind.File)
                {
                    // Interrupted between moving the file aside and recording it (no *.bdfu-replaced existed before).
                    fs.Rename(file + ReplacedSuffix, file);
                }
                else if (!journal.MovedAside.Contains(file) && fs.Probe(file) == RestorePathKind.File && fs.Probe(file + NewSuffix) == RestorePathKind.Missing)
                {
                    // The database had no such file before (lost): the restored one goes back to *.bdfu-new.
                    fs.Rename(file, file + NewSuffix);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is ArgumentException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }

        if (problems.Count == 0 && journal.Swapped.Count > 0)
        {
            log($"put the database's own {journal.MovedAside.Count} file(s) back; the restored files are *{NewSuffix} again.");
        }

        return problems;
    }

    private static void Execute(DbConnection connection, string text)
    {
        using (var command = SqlAttach.Command(connection, text))
        {
            command.CommandTimeout = 0;
            command.ExecuteNonQuery();
        }
    }

    private static void Write(string path, Journal journal)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, AgentJson.Options));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temporary, path);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>What an overwrite has done so far (no account, no password).</summary>
    public sealed class Journal
    {
        public string Instance { get; set; } = string.Empty;

        public string Database { get; set; } = string.Empty;

        public List<string> Files { get; set; } = new List<string>();

        public bool Offline { get; set; }

        /// <summary>Files whose restored copy is (or is being) put in place.</summary>
        public List<string> Swapped { get; set; } = new List<string>();

        /// <summary>Files of the database that were renamed to *.bdfu-replaced.</summary>
        public List<string> MovedAside { get; set; } = new List<string>();
    }
}
