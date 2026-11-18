using Backdafuckup.Core.Operations;
using Backdafuckup.Server.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Backdafuckup.Server.Data;

/// <summary>Encrypts stored hypervisor passwords with ASP.NET Core Data Protection (key ring in the data directory).</summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Backdafuckup.Credentials.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}

/// <summary>Artifact ledger stored in the catalog database (crash recovery of platform objects).</summary>
/// <param name="storageSystemId">Rows of objects on a storage array: the cleanup removes them through that array.</param>
public sealed class DbArtifactLedger(
    IDbContextFactory<AppDbContext> dbFactory, Guid connectionId, Guid? sessionId, Guid? restorePointId, TimeProvider time, Guid? storageSystemId = null) : IArtifactLedger
{
    public async Task<Guid> RecordAsync(string kind, string nativeId, string? nativeRef, string? data, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var entity = new ProviderArtifactEntity
        {
            ConnectionId = connectionId,
            StorageSystemId = storageSystemId,
            Kind = kind,
            NativeId = nativeId,
            NativeRef = nativeRef,
            Data = data,
            SessionId = sessionId,
            RestorePointId = restorePointId,
            State = ArtifactState.Active,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.Artifacts.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity.Id;
    }

    public async Task UpdateAsync(Guid artifactId, ArtifactState state, string? nativeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.Artifacts.FindAsync([artifactId], cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return;
        }

        entity.State = state;
        entity.NativeId = nativeId ?? entity.NativeId;
        entity.UpdatedUtc = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class DataServiceCollectionExtensions
{
    public const string ConnectionStringName = "Catalog";

    public static IServiceCollection AddCatalog(this IServiceCollection services, IConfiguration configuration, ServerPaths paths)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured (ConnectionStrings:{ConnectionStringName} or BDFU_ConnectionStrings__{ConnectionStringName}).");

        services.AddDbContextFactory<AppDbContext>(options => Configure(options, connectionString));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        var keys = services.AddDataProtection()
            .SetApplicationName("backdafuckup")
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeyRingDirectory));
        if (OperatingSystem.IsWindows())
        {
            keys.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services.AddSingleton<CredentialProtector>();
        return services;
    }

    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .UseSnakeCaseNamingConvention();

    /// <summary>How long the start waits for a catalog database that is not reachable yet.</summary>
    public static readonly TimeSpan CatalogStartupWait = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Applies pending migrations under a PostgreSQL advisory lock (safe with several starting instances). The database may
    /// still be starting (the service started at boot before PostgreSQL, or a PostgreSQL container that restarts once after
    /// its first initialisation): connection errors are retried for <see cref="CatalogStartupWait"/>, each one logged.
    /// </summary>
    public static async Task MigrateCatalogAsync(this IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + CatalogStartupWait;
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await MigrateOnceAsync(services, logger, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && DateTimeOffset.UtcNow + delay < deadline)
            {
                logger.LogWarning(
                    "The catalog database at {Endpoint} is not reachable yet (attempt {Attempt}: {Error}); trying again in {Delay} s, for up to {Minutes} minutes since the start.",
                    CatalogEndpoint(services), attempt, ex.GetBaseException().Message, delay.TotalSeconds, CatalogStartupWait.TotalMinutes);
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
            }
        }
    }

    /// <summary>A connection error that goes away when the database has started (refused, reset, timed out, starting up).</summary>
    private static bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is NpgsqlException { IsTransient: true } or System.Net.Sockets.SocketException or IOException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Host and port of the catalog database, for messages (never the password).</summary>
    private static string CatalogEndpoint(IServiceProvider services)
    {
        try
        {
            using var db = services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
            var builder = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
            return $"{builder.Host}:{builder.Port} (database {builder.Database})";
        }
        catch (ArgumentException)
        {
            return "the configured connection string";
        }
    }

    private static async Task MigrateOnceAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(84737211)", connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} catalog database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(84737211)", connection);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>Used by "dotnet ef" to create migrations without starting the server.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        DataServiceCollectionExtensions.Configure(options, "Host=localhost;Database=backdafuckup;Username=backdafuckup;Password=design-time");
        return new AppDbContext(options.Options);
    }
}
