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
public sealed class DbArtifactLedger(IDbContextFactory<AppDbContext> dbFactory, Guid connectionId, Guid? sessionId, Guid? restorePointId, TimeProvider time) : IArtifactLedger
{
    public async Task<Guid> RecordAsync(string kind, string nativeId, string? nativeRef, string? data, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var entity = new ProviderArtifactEntity
        {
            ConnectionId = connectionId,
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

    /// <summary>Applies pending migrations under a PostgreSQL advisory lock (safe with several starting instances).</summary>
    public static async Task MigrateCatalogAsync(this IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
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
