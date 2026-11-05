using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// One SQL Server container for all tests of the run (memory: about 2 GB). Only with BDFU_SQL_TESTS=1; the tests skip
/// otherwise, so a solution-wide test run does not start it.
/// </summary>
public class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04";

    private MsSqlContainer? _container;

    public static bool Enabled => Environment.GetEnvironmentVariable("BDFU_SQL_TESTS") == "1";

    /// <summary>Connection string of the sa login.</summary>
    public string AdminConnectionString => _container?.GetConnectionString() ?? throw new InvalidOperationException("The SQL Server container is not running.");

    public async ValueTask InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var builder = new MsSqlBuilder(ContainerImage)
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
            .WithCreateParameterModifier(p => p.HostConfig!.Memory = 2400L * 1024 * 1024);
        if (TimeZone is not null)
        {
            builder = builder.WithEnvironment("TZ", TimeZone);
        }

        _container = builder.Build();
        await _container.StartAsync();
    }

    /// <summary>The image of this fixture's container.</summary>
    protected virtual string ContainerImage => Image;

    /// <summary>The container's time zone (IANA id, as <c>TZ</c>); null for UTC.</summary>
    protected virtual string? TimeZone => null;

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Reads a file inside the container (e.g. a backup SQL Server wrote).</summary>
    public async Task<byte[]> ReadFileAsync(string path) =>
        await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ReadFileAsync(path);

    public async Task<bool> FileExistsAsync(string path) =>
        (await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["test", "-f", path])).ExitCode == 0;

    public async Task<long> FileSizeAsync(string path) =>
        long.Parse((await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["stat", "-c", "%s", path])).Stdout.Trim(), System.Globalization.CultureInfo.InvariantCulture);

    public async Task DeleteFileAsync(string path) =>
        await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["rm", "-f", path]);

    /// <summary>Copies a file inside the container as SQL Server's user (e.g. the "snapshot" of a database file).</summary>
    public async Task CopyFileAsync(string from, string to)
    {
        var result = await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["cp", from, to]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"cp {from} {to} failed in the container: {result.Stderr}");
        }
    }

    /// <summary>The names of the files in a folder inside the container.</summary>
    public async Task<List<string>> ListFilesAsync(string folder)
    {
        var result = await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["ls", "-1", folder]);
        return [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Deletes a folder inside the container with what is in it.</summary>
    public async Task DeleteDirectoryAsync(string path) =>
        await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["rm", "-rf", path]);

    /// <summary>Creates a folder inside the container that SQL Server can write to.</summary>
    public async Task CreateDirectoryAsync(string path) =>
        await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["mkdir", "-p", path]);

    /// <summary>Renames a file inside the container (as the agent renames database files during an overwrite).</summary>
    public async Task MoveFileAsync(string from, string to)
    {
        var result = await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).ExecAsync(["mv", from, to]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"mv {from} {to} failed in the container: {result.Stderr}");
        }
    }

    /// <summary>Writes a file inside the container, readable for SQL Server's user.</summary>
    public async Task WriteFileAsync(string path, byte[] content) =>
        await (_container ?? throw new InvalidOperationException("The SQL Server container is not running.")).CopyAsync(content, path);

    /// <summary>A connection string for another login on the same server.</summary>
    public string ConnectionStringFor(string login, string password) =>
        new SqlConnectionStringBuilder(AdminConnectionString) { UserID = login, Password = password, InitialCatalog = "master" }.ConnectionString;

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>SQL Server 2019: no T-SQL snapshot backup, so a native full backup is the base of its log chain.</summary>
public sealed class SqlServer2019Fixture : SqlServerFixture
{
    protected override string ContainerImage => "mcr.microsoft.com/mssql/server:2019-CU32-GDR12-ubuntu-20.04";
}

/// <summary>SQL Server 2022 in Europe/Berlin: local times in msdb and STOPAT differ from UTC.</summary>
public sealed class SqlServerBerlinFixture : SqlServerFixture
{
    protected override string? TimeZone => "Europe/Berlin";
}
