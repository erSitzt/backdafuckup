using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Backdafuckup.Sql.Tests;

/// <summary>
/// One SQL Server container for all tests of the run (memory: about 2 GB). Only with BDFU_SQL_TESTS=1; the tests skip
/// otherwise, so a solution-wide test run does not start it.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
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

        _container = new MsSqlBuilder(Image)
            .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "2048")
            .WithCreateParameterModifier(p => p.HostConfig!.Memory = 2400L * 1024 * 1024)
            .Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
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
