using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Restores a database to a point in time (<see cref="SqlJobKinds.RestoreChain"/>): the base (the SQL Server
/// snapshot's <c>.bkm</c> with METADATA_ONLY onto the data files already in place, or a native full backup), then the
/// log backups with NORECOVERY, the last one with STOPAT, then RECOVERY. RESTORE checks the backups' checksums where
/// they have some (ours always do); the files themselves arrive checked by SHA-256. Backup files are downloaded into a staging
/// folder the SQL Server service can read, the next one while the current one is restored, and deleted after use.
/// If anything fails, the database left RESTORING is dropped and the files a native restore created are deleted.
/// </summary>
public sealed class SqlChainRestorer
{
    public const string FileListQuery = "RESTORE FILELISTONLY FROM DISK = @path WITH FILE = @position";

    private readonly ISqlEnvironment _sql;
    private readonly SqlBackupFiles _files;
    private readonly Func<SqlChainFile, string, CancellationToken, Task> _download;
    private readonly Action<string, IReadOnlyList<string>> _grant;
    private readonly Action<string> _log;

    /// <param name="download">Writes a backup file of the job to a path (checked by length and SHA-256).</param>
    /// <param name="grant">Gives accounts full control of an existing file (the snapshot's data files in place).</param>
    public SqlChainRestorer(
        ISqlEnvironment sql, SqlBackupFiles files, Func<SqlChainFile, string, CancellationToken, Task> download, Action<string, IReadOnlyList<string>> grant, Action<string> log)
    {
        _sql = sql;
        _files = files;
        _download = download;
        _grant = grant;
        _log = log;
    }

    public async Task RestoreAsync(SqlJobRequest request, Guid jobId, CancellationToken cancellationToken)
    {
        var chain = request.Chain ?? throw new InvalidOperationException("The job names nothing to restore.");
        var account = request.Account ?? throw new InvalidOperationException("The job names no account.");
        var database = request.Database ?? throw new InvalidOperationException("The job names no database.");
        if ((chain.Metadata is null) == (chain.BaseFile is null))
        {
            throw new InvalidOperationException("The job needs exactly one base: the snapshot's .bkm or a native full backup.");
        }

        var instance = _sql.Instances().FirstOrDefault(i => string.Equals(i.Name, request.Instance, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"This VM has no SQL Server instance {request.Instance}.");
        var quoted = SqlPublishDatabase.Quote(database);
        using (var connection = _sql.Open(instance.Name, account))
        {
            if (Scalar(connection, "SELECT DB_ID(@name)", ("@name", database)) is { } id && id != DBNull.Value)
            {
                throw new InvalidOperationException($"Instance {instance.Name} has a database {quoted} already; choose another name.");
            }

            var root = request.Folder ?? instance.BackupFolder ?? Scalar(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))") as string
                ?? throw new InvalidOperationException($"{instance.Name} has no backup folder for the staging files.");
            var staging = Combine(Combine(root, SqlBackupLane.NamePrefix), "restore-" + jobId.ToString("N").Substring(0, 12));
            _files.CreateDirectory(staging);
            var created = new List<string>();
            var started = false;
            try
            {
                var snapshot = chain.Metadata is not null;
                string basePath;
                if (snapshot)
                {
                    basePath = Combine(staging, "snapshot.bkm");
                    using (var stream = _files.Create(basePath))
                    {
                        await stream.WriteAsync(chain.Metadata!, 0, chain.Metadata!.Length, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    basePath = Combine(staging, chain.BaseFile!.FileName);
                    _log($"downloading the full backup {chain.BaseFile.FileName} ({chain.BaseFile.Length:N0} bytes) to {staging}.");
                    await _download(chain.BaseFile, basePath, cancellationToken).ConfigureAwait(false);
                }

                var moves = Moves(connection, basePath, chain, instance.Name);
                if (snapshot)
                {
                    var accounts = SqlAttach.Accounts(instance, account);
                    foreach (var (_, target, _) in moves)
                    {
                        _grant(target, accounts);
                    }

                    _log($"granted {string.Join(" and ", accounts)} access to the {moves.Count} file(s) of the database.");
                }
                else
                {
                    var existing = moves.Where(m => _files.Exists(m.Target)).Select(m => m.Target).ToList();
                    if (existing.Count > 0)
                    {
                        throw new InvalidOperationException($"These files exist already and are not replaced: {string.Join(", ", existing)}.");
                    }

                    created.AddRange(moves.Select(m => m.Target));
                }

                var text = "RESTORE DATABASE @database FROM DISK = @path WITH " + (snapshot ? "METADATA_ONLY, " : string.Empty) + "FILE = @position, " +
                    string.Join(", ", moves.Select((_, i) => $"MOVE @logical{i} TO @physical{i}")) + ", NORECOVERY";
                var parameters = new List<(string, object)> { ("@database", database), ("@path", basePath), ("@position", chain.Position) };
                for (var i = 0; i < moves.Count; i++)
                {
                    parameters.Add(($"@logical{i}", moves[i].Logical));
                    parameters.Add(($"@physical{i}", moves[i].Target));
                }

                started = true;
                Execute(connection, text, parameters.ToArray());
                _files.Delete(basePath);
                _log($"restored the {(snapshot ? "SQL Server snapshot" : "full backup")} as {quoted} on {instance.Name} (NORECOVERY): " +
                    string.Join(", ", moves.Select(m => $"{m.Logical} → {m.Target}")) + ".");

                await RestoreLogsAsync(connection, database, chain, staging, cancellationToken).ConfigureAwait(false);
                var state = Scalar(connection, "SELECT state_desc FROM sys.databases WHERE name = @name", ("@name", database)) as string;
                if (!string.Equals(state, "ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{quoted} is {state ?? "missing"} after the restore.");
                }

                var canOpen = SqlAttach.CheckAccess(connection, database, account, _log);
                SqlAttach.Describe(connection, instance.Name, database, canOpen, _log);
                if (chain.Detach)
                {
                    Execute(connection, "EXEC sp_detach_db @dbname = @database", ("@database", database));
                    _log($"detached {quoted} again; its files are ready for the overwrite.");
                }
            }
            catch (Exception ex) when (started || created.Count > 0)
            {
                CleanUp(connection, database, created);
                throw Wrap(ex, instance.Name, account);
            }
            catch (DbException ex)
            {
                throw Wrap(ex, instance.Name, account);
            }
            finally
            {
                try
                {
                    _files.DeleteDirectory(staging);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _log($"the staging folder {staging} could not be deleted: {ex.Message}");
                }
            }
        }
    }

    private async Task RestoreLogsAsync(DbConnection connection, string database, SqlChainRestoreRequest chain, string staging, CancellationToken cancellationToken)
    {
        var quoted = SqlPublishDatabase.Quote(database);
        if (chain.Logs.Count == 0)
        {
            Execute(connection, "RESTORE DATABASE @database WITH RECOVERY", ("@database", database));
            _log($"recovered {quoted} at the time of its full backup.");
            return;
        }

        string LogPath(int i) => Combine(staging, chain.Logs[i].FileName);
        var next = _download(chain.Logs[0], LogPath(0), cancellationToken);
        long bytes = 0;
        for (var i = 0; i < chain.Logs.Count; i++)
        {
            await next.ConfigureAwait(false);
            next = i + 1 < chain.Logs.Count ? _download(chain.Logs[i + 1], LogPath(i + 1), cancellationToken) : Task.CompletedTask;
            var last = i == chain.Logs.Count - 1;
            try
            {
                if (last && chain.StopAt is { } stopAt)
                {
                    Execute(connection, "RESTORE LOG @database FROM DISK = @path WITH STOPAT = @stopat, RECOVERY",
                        ("@database", database), ("@path", LogPath(i)), ("@stopat", stopAt));
                }
                else
                {
                    Execute(connection, "RESTORE LOG @database FROM DISK = @path WITH " + (last ? "RECOVERY" : "NORECOVERY"),
                        ("@database", database), ("@path", LogPath(i)));
                }
            }
            catch (Exception)
            {
                // The prefetch must not outlive the restore that failed.
                await Task.WhenAny(next).ConfigureAwait(false);
                throw;
            }

            bytes += chain.Logs[i].Length;
            _files.Delete(LogPath(i));
        }

        _log($"applied {chain.Logs.Count} log backup(s) ({bytes:N0} bytes) and recovered {quoted}" +
            (chain.StopAt is { } at ? $" at {at} (SQL Server's local time)." : " at the end of the last log backup."));
    }

    /// <summary>The backup's files (logical name, original path) with their target from the job, read from the backup itself.</summary>
    private List<(string Logical, string Target, string Type)> Moves(DbConnection connection, string basePath, SqlChainRestoreRequest chain, string instance)
    {
        var moves = new List<(string, string, string)>();
        using (var command = Command(connection, FileListQuery, ("@path", basePath), ("@position", chain.Position)))
        using (var reader = command.ExecuteReader())
        {
            var logical = reader.GetOrdinal("LogicalName");
            var physical = reader.GetOrdinal("PhysicalName");
            var type = reader.GetOrdinal("Type");
            while (reader.Read())
            {
                var original = reader.GetString(physical);
                var move = chain.Moves.FirstOrDefault(m => string.Equals(m.OriginalPath, original, StringComparison.OrdinalIgnoreCase))
                    ?? chain.Moves.FirstOrDefault(m => string.Equals(FileName(m.OriginalPath), FileName(original), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The backup has a file {original} ({reader.GetString(logical)}) the restore has no target for.");
                moves.Add((reader.GetString(logical), move.TargetPath, Convert.ToString(reader.GetValue(type), CultureInfo.InvariantCulture) ?? string.Empty));
            }
        }

        if (moves.Count == 0)
        {
            throw new InvalidOperationException($"{instance} read no files from the backup (backup set {chain.Position}).");
        }

        return moves;
    }

    /// <summary>Drops the database a failed restore left (RESTORING drops its files too) and deletes what a native restore created.</summary>
    private void CleanUp(DbConnection connection, string database, List<string> created)
    {
        var quoted = SqlPublishDatabase.Quote(database);
        try
        {
            if (Scalar(connection, "SELECT DB_ID(@name)", ("@name", database)) is { } id && id != DBNull.Value)
            {
                Execute(connection, $"DROP DATABASE {quoted}");
                _log($"dropped the incomplete database {quoted}.");
            }
        }
        catch (DbException ex)
        {
            _log($"the incomplete database {quoted} could not be dropped: {ex.Message}");
        }

        foreach (var path in created.Where(p => _files.Exists(p)))
        {
            try
            {
                _files.Delete(path);
                _log($"deleted {path}.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log($"{path} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>The usual reasons a restore fails, in words; the SQL Server message follows.</summary>
    private static Exception Wrap(Exception ex, string instance, SqlAccount account)
    {
        if (ex is not DbException db)
        {
            return ex;
        }

        var number = SqlAttach.ErrorNumber(db);
        var reason = number switch
        {
            4305 or 4326 => "the log backups do not continue each other (the chain has a gap)",
            4335 => "the point in time lies before what the backups cover",
            3201 or 3203 => $"SQL Server on {instance} cannot read the staging file (may its service read the backup folder?)",
            3634 or 5133 or 5120 => $"SQL Server on {instance} cannot create or open a database file (the service needs write access to the target folders)",
            3101 => "the database is in use",
            262 or 3110 => $"{account.UserName} may not restore databases on {instance} (it needs dbcreator or sysadmin)",
            _ => null,
        };
        var detail = number is { } n ? $"SQL Server error {n.ToString(CultureInfo.InvariantCulture)}: {db.Message}" : db.Message;
        return new InvalidOperationException(reason is null ? detail : $"{reason} ({detail})", ex);
    }

    private static string FileName(string path) => path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);

    private static string Combine(string folder, string name)
    {
        var separator = folder.IndexOf('\\') < 0 && folder.IndexOf('/') >= 0 ? '/' : '\\';
        return folder.TrimEnd('\\', '/') + separator + name;
    }

    private static object? Scalar(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        using (var command = Command(connection, text, parameters))
        {
            return command.ExecuteScalar();
        }
    }

    private static void Execute(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        using (var command = Command(connection, text, parameters))
        {
            command.CommandTimeout = 0;
            command.ExecuteNonQuery();
        }
    }

    private static DbCommand Command(DbConnection connection, string text, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
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
