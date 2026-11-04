using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// The local SQL Server installation as the agent sees it. The Windows implementation reads the registry and the service
/// manager and connects over shared memory; the tests connect to a SQL Server container.
/// </summary>
public interface ISqlEnvironment
{
    /// <summary>The installed instances with their service state, without connecting.</summary>
    List<SqlInstanceInfo> Instances();

    /// <summary>Local fixed volumes and their free space.</summary>
    List<SqlVolumeInfo> Volumes();

    /// <summary>
    /// An open connection to a local instance with <paramref name="account"/> (a Windows account logs on first). Throws
    /// with a message that names the instance and the account if it fails.
    /// </summary>
    DbConnection Open(string instance, SqlAccount account);
}

/// <summary>Reads what an account sees of an instance: version, edition, its rights, the databases and their files.</summary>
public static class SqlInventoryReader
{
    public static void Fill(SqlInstanceInfo instance, DbConnection connection)
    {
        using (var command = Command(connection,
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), " +
            "CAST(SERVERPROPERTY('EngineEdition') AS int), SUSER_SNAME(), IS_SRVROLEMEMBER('sysadmin'), IS_SRVROLEMEMBER('dbcreator')"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                instance.ProductVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
                instance.Edition = reader.IsDBNull(1) ? null : reader.GetString(1);
                instance.EngineEdition = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
                instance.LoginName = reader.IsDBNull(3) ? null : reader.GetString(3);
                instance.IsSysadmin = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
                instance.IsDbCreator = !reader.IsDBNull(5) && reader.GetInt32(5) == 1;
            }
        }

        var databases = new Dictionary<int, SqlDatabaseInfo>();
        using (var command = Command(connection, "SELECT database_id, name, state_desc, recovery_model_desc, is_read_only FROM sys.databases ORDER BY name"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var database = new SqlDatabaseInfo
                {
                    Name = reader.GetString(1),
                    State = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    RecoveryModel = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReadOnly = !reader.IsDBNull(4) && reader.GetBoolean(4),
                };
                databases[reader.GetInt32(0)] = database;
                instance.Databases.Add(database);
            }
        }

        // sys.master_files shows the files of databases the login may see (dbcreator and sysadmin see all of them).
        using (var command = Command(connection, "SELECT database_id, name, physical_name, type_desc, CAST(size AS bigint) * 8192 FROM sys.master_files ORDER BY database_id, file_id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (databases.TryGetValue(reader.GetInt32(0), out var database))
                {
                    database.Files.Add(new SqlDatabaseFileInfo
                    {
                        LogicalName = reader.GetString(1),
                        PhysicalName = reader.GetString(2),
                        Type = reader.GetString(3),
                        SizeBytes = reader.GetInt64(4),
                    });
                }
            }
        }
    }

    private static DbCommand Command(DbConnection connection, string text)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 60;
        return command;
    }
}

/// <summary>Runs one SQL job: fetches the request (with the account), does the work, posts the result.</summary>
public sealed class SqlJobRunner
{
    private readonly HttpClient _http;
    private readonly Guid _id;
    private readonly ISqlEnvironment _sql;
    private readonly AgentLog _log;

    private readonly IRestoreFileSystem? _files;

    private readonly string? _journalDirectory;

    /// <param name="journalDirectory">Where an overwrite keeps its journal (the agent's folder); null: no overwrites.</param>
    public SqlJobRunner(HttpClient http, Guid id, ISqlEnvironment sql, AgentLog log, IRestoreFileSystem? files = null, string? journalDirectory = null)
    {
        _http = http;
        _id = id;
        _sql = sql;
        _log = log;
        _files = files;
        _journalDirectory = journalDirectory;
    }

    private string Base => AgentProtocol.SqlJobsPath + "/" + _id.ToString("D");

    /// <summary>The job's kind once its request is fetched.</summary>
    public string? Kind { get; private set; }

    /// <summary>True if the job did its work.</summary>
    public bool Succeeded { get; private set; }

    public async Task RunAsync(CancellationToken stop)
    {
        var result = new SqlJobResult { JobId = _id };
        try
        {
            SqlJobRequest request;
            using (var response = await _http.GetAsync(Base, stop).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"The server did not hand out SQL job {_id} ({(int)response.StatusCode}).");
                }

                request = JsonSerializer.Deserialize<SqlJobRequest>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options)
                    ?? throw new InvalidOperationException("The SQL job request is empty.");
            }

            Kind = request.Kind;
            void Log(string line)
            {
                _log.Write($"SQL job {_id}: {line}");
                result.Log.Add(line);
            }

            // The account name may be logged, never its password.
            _log.Write($"SQL job {_id}: {request.Kind}{(request.Account is { } account ? $" as {account.UserName} ({account.Kind})" : string.Empty)}.");
            switch (request.Kind)
            {
                case SqlJobKinds.Inventory:
                    result.Inventory = Inventory(request);
                    break;
                case SqlJobKinds.DropPublished:
                    if (request.Account is null || string.IsNullOrEmpty(request.Instance) || string.IsNullOrEmpty(request.Database) || string.IsNullOrEmpty(request.PublishFolder))
                    {
                        throw new InvalidOperationException("The job names no instance, account, database or publish folder.");
                    }

                    SqlPublishDatabase.Drop(_sql, request.Instance!, request.Account, request.Database!, request.PublishFolder!, Log);
                    break;
                case SqlJobKinds.AttachFiles:
                    AttachFiles(request, Log);
                    break;
                case SqlJobKinds.ReplaceFiles:
                    if (request.Account is null || string.IsNullOrEmpty(request.Instance) || string.IsNullOrEmpty(request.Database) || request.Files is not { Count: > 0 })
                    {
                        throw new InvalidOperationException("The job names no instance, account, database or files.");
                    }

                    if (_files is null || _journalDirectory is null)
                    {
                        throw new InvalidOperationException("This agent cannot overwrite databases.");
                    }

                    SqlReplace.Replace(_sql, _files, _journalDirectory, request.Instance!, request.Account, request.Database!, request.Files, request.KeepReplaced, Log);
                    break;
                case SqlJobKinds.SnapshotSuspend:
                    if (request.Account is null)
                    {
                        throw new InvalidOperationException("The job names no account.");
                    }

                    await SnapshotAsync(request, result, Log, stop).ConfigureAwait(false);
                    break;
                case SqlJobKinds.DeleteFiles:
                    var files = request.Files ?? new List<string>();
                    if (_files is null || files.Count == 0)
                    {
                        throw new InvalidOperationException(_files is null ? "This agent cannot delete restored files." : "The job names no files.");
                    }

                    if (SqlAttach.DeleteFiles(_files, files, Log) is { Count: > 0 } failed)
                    {
                        throw new InvalidOperationException($"{failed.Count} restored file(s) could not be deleted: {string.Join("; ", failed)}");
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown SQL job '{request.Kind}' (agent older than the server?).");
            }

            result.Success = true;
            Succeeded = true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && stop.IsCancellationRequested))
        {
            result.Error = ex.Message;
            _log.Write($"SQL job {_id} failed: {ex.Message}");
        }

        for (var attempt = 1; attempt <= 5 && !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                using (var content = new StringContent(JsonSerializer.Serialize(result, AgentJson.Options), Encoding.UTF8, "application/json"))
                using (var response = await _http.PostAsync(Base + "/result", content, stop).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stop).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The T-SQL snapshot backup around the VM snapshot: suspends, reports it, waits for the server's signal (polled every
    /// 200 ms), then records the snapshot or resumes without recording. The connections close at the end in any case,
    /// which resumes SQL Server also if anything here fails.
    /// </summary>
    private async Task SnapshotAsync(SqlJobRequest request, SqlJobResult result, Action<string> log, CancellationToken stop)
    {
        var limit = TimeSpan.FromMilliseconds(request.HoldLimitMs > 0 ? request.HoldLimitMs : 120000);
        using (var suspend = new SqlSnapshotSuspend(_sql, request.Account!, log))
        {
            var progress = suspend.Suspend(_sql.Instances(), request.Instance);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await PostJsonAsync(Base + "/progress", progress, stop).ConfigureAwait(false);
            string? signal = null;
            while (watch.Elapsed < limit && !stop.IsCancellationRequested)
            {
                signal = await TryGetSignalAsync(stop).ConfigureAwait(false);
                if (signal is not null && signal != SqlJobSignals.Wait)
                {
                    break;
                }

                signal = null;
                await Task.Delay(TimeSpan.FromMilliseconds(200), stop).ConfigureAwait(false);
            }

            result.Snapshot = signal == SqlJobSignals.SnapshotTaken
                ? suspend.Record(request.BackupName ?? "backdafuckup", _id)
                : suspend.Resume(signal == SqlJobSignals.Abort
                    ? "the server took no VM snapshot (it failed or the backup stopped), so nothing was recorded"
                    : $"the VM snapshot was not reported done within {limit.TotalSeconds:0} s; the writes were resumed before it may have been taken");
            result.SuspendedMs = suspend.SuspendedMs;
        }
    }

    /// <summary>The server's signal; null if the server could not be asked this time; abort if the job is gone (404).</summary>
    private async Task<string?> TryGetSignalAsync(CancellationToken stop)
    {
        try
        {
            using (var response = await _http.GetAsync(Base + "/signal", stop).ConfigureAwait(false))
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return SqlJobSignals.Abort;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var signal = JsonSerializer.Deserialize<SqlJobSignal>(await response.Content.ReadAsStringAsync().ConfigureAwait(false), AgentJson.Options);
                return signal?.Signal;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!stop.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task PostJsonAsync<T>(string path, T body, CancellationToken stop)
    {
        using (var content = new StringContent(JsonSerializer.Serialize(body, AgentJson.Options), Encoding.UTF8, "application/json"))
        using (var response = await _http.PostAsync(path, content, stop).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The server did not take the progress of SQL job {_id} ({(int)response.StatusCode}).");
            }
        }
    }

    /// <summary>
    /// Attaches restored files; if the attach fails, the files are deleted again (the restore wrote them for it, and the
    /// server only sends files it created: existing files fail the transfer).
    /// </summary>
    private void AttachFiles(SqlJobRequest request, Action<string> log)
    {
        var files = request.Files ?? new List<string>();
        if (request.Account is null || string.IsNullOrEmpty(request.Instance) || string.IsNullOrEmpty(request.Database) || files.Count == 0)
        {
            throw new InvalidOperationException("The job names no instance, account, database or files.");
        }

        if (_files is null)
        {
            throw new InvalidOperationException("This agent cannot attach restored files.");
        }

        var attached = false;
        try
        {
            SqlAttach.AttachFiles(
                _sql, request.Instance!, request.Account, request.Database!, files,
                (path, accounts) =>
                {
                    foreach (var account in accounts)
                    {
                        _files.GrantFullControl(path, account);
                    }
                },
                "on the restored files",
                log,
                () => attached = true,
                describe: true);
        }
        catch (Exception) when (!attached)
        {
            SqlAttach.DeleteFiles(_files, files, log);
            throw;
        }
    }

    private SqlInventory Inventory(SqlJobRequest request)
    {
        var inventory = new SqlInventory { Instances = _sql.Instances(), Volumes = _sql.Volumes() };
        if (request.Account is not { } account)
        {
            return inventory;
        }

        foreach (var instance in inventory.Instances)
        {
            if (request.Instance is { } only && !string.Equals(only, instance.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (instance.ServiceState is { } state && state != "Running")
            {
                instance.ConnectError = $"the service {instance.ServiceName} is {state.ToLowerInvariant()}";
                continue;
            }

            try
            {
                using (var connection = _sql.Open(instance.Name, account))
                {
                    SqlInventoryReader.Fill(instance, connection);
                    instance.Connected = true;
                }
            }
            catch (Exception ex) when (ex is DbException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                instance.ConnectError = ex.Message;
            }
        }

        return inventory;
    }
}
