using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Backdafuckup.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<HypervisorConnection> Connections => Set<HypervisorConnection>();

    public DbSet<ConnectionHost> ConnectionHosts => Set<ConnectionHost>();

    public DbSet<Credential> Credentials => Set<Credential>();

    public DbSet<InventoryEntry> Inventory => Set<InventoryEntry>();

    public DbSet<BackupRepositoryEntity> Repositories => Set<BackupRepositoryEntity>();

    public DbSet<BackupJob> Jobs => Set<BackupJob>();

    public DbSet<BackupJobObject> JobObjects => Set<BackupJobObject>();

    public DbSet<RestorePointEntity> RestorePoints => Set<RestorePointEntity>();

    public DbSet<RestorePointDisk> RestorePointDisks => Set<RestorePointDisk>();

    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    public DbSet<SessionTaskEntity> SessionTasks => Set<SessionTaskEntity>();

    public DbSet<SessionLogEntry> SessionLog => Set<SessionLogEntry>();

    public DbSet<ProviderArtifactEntity> Artifacts => Set<ProviderArtifactEntity>();

    public DbSet<AppSetting> Settings => Set<AppSetting>();

    public DbSet<AgentEntity> Agents => Set<AgentEntity>();

    public DbSet<SshHostKeyEntity> SshHostKeys => Set<SshHostKeyEntity>();

    public DbSet<SqlDatabaseEntity> SqlDatabases => Set<SqlDatabaseEntity>();

    public DbSet<SqlBackupEntity> SqlBackups => Set<SqlBackupEntity>();

    public DbSet<SqlBackupSetEntity> SqlBackupSets => Set<SqlBackupSetEntity>();

    public DbSet<SqlBackupScheduleEntity> SqlBackupSchedules => Set<SqlBackupScheduleEntity>();

    public DbSet<SqlBackupRunEntity> SqlBackupRuns => Set<SqlBackupRunEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<HypervisorConnection>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.TrustMode).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(255);
            e.HasIndex(x => x.DisplayName).IsUnique();
            e.HasOne(x => x.Credential).WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Hosts).WithOne().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });

        builder.Entity<ConnectionHost>(e => e.HasIndex(x => new { x.ConnectionId, x.Address }).IsUnique());

        builder.Entity<InventoryEntry>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Properties).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonSerializerOptions.Default) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
            e.HasIndex(x => new { x.ConnectionId, x.Kind, x.NativeId }).IsUnique();
        });

        builder.Entity<BackupRepositoryEntity>(e =>
        {
            e.ToTable("repositories");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.RepositoryUuid).IsUnique();
            e.Property(x => x.Version).IsRowVersion();
        });

        builder.Entity<BackupJob>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.OwnsOne(x => x.Schedule, s => s.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16));
            e.OwnsOne(x => x.SqlBackup, b =>
            {
                b.OwnsOne(x => x.FullSchedule, s => s.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16));
                b.OwnsOne(x => x.SystemSchedule, s => s.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16));
            });
            e.Property(x => x.LastResult).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TransportMode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.GuestFailurePolicy).HasConversion<string>().HasMaxLength(32);
            e.HasOne<Credential>().WithMany().HasForeignKey(x => x.GuestCredentialId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.GuestProcessingMode).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Credential>().WithMany().HasForeignKey(x => x.SqlCredentialId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Connection).WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Objects).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });

        builder.Entity<RestorePointEntity>(e =>
        {
            e.ToTable("restore_points");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Vm).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<VmDescriptor>(v, JsonSerializerOptions.Default));
            e.HasMany(x => x.Disks).WithOne().HasForeignKey(x => x.RestorePointId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RepositoryId, x.JobId, x.VmNativeId, x.CreatedUtc });
        });

        builder.Entity<RestorePointDisk>(e => e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16));

        builder.Entity<SqlDatabaseEntity>(e =>
        {
            e.ToTable("sql_databases");
            e.Property(x => x.ChainState).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.PlatformId, x.VmNativeId, x.Instance, x.Name, x.FamilyGuid }).IsUnique();
        });

        builder.Entity<SqlBackupEntity>(e =>
        {
            e.ToTable("sql_backups");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.RepositoryId, x.VmNativeId, x.CreatedUtc });
        });

        builder.Entity<SqlBackupScheduleEntity>(e =>
        {
            e.ToTable("sql_backup_schedules");
            e.Property(x => x.LastResult).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.JobId, x.VmNativeId }).IsUnique();
            e.HasOne<BackupJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SqlBackupRunEntity>(e =>
        {
            e.ToTable("sql_backup_runs");
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Lines).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<List<SqlBackupRunLine>>(v, JsonSerializerOptions.Default) ?? new List<SqlBackupRunLine>(),
                new ValueComparer<List<SqlBackupRunLine>>(
                    (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                    v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(StringComparison.Ordinal),
                    v => v.ToList()));
            e.HasIndex(x => new { x.SessionId, x.StartedUtc });
            e.HasOne<SessionEntity>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SqlBackupSetEntity>(e =>
        {
            e.ToTable("sql_backup_sets");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            foreach (var lsn in new[] { nameof(SqlBackupSetEntity.FirstLsn), nameof(SqlBackupSetEntity.LastLsn), nameof(SqlBackupSetEntity.CheckpointLsn), nameof(SqlBackupSetEntity.DatabaseBackupLsn), nameof(SqlBackupSetEntity.ForkPointLsn) })
            {
                e.Property(lsn).HasPrecision(25, 0);
            }

            e.HasIndex(x => x.BackupSetUuid).IsUnique();
            e.Property(x => x.Files).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => v == null ? null : JsonSerializer.Deserialize<List<Backdafuckup.Core.Repository.SqlBackupSetFile>>(v, JsonSerializerOptions.Default),
                new ValueComparer<List<Backdafuckup.Core.Repository.SqlBackupSetFile>?>(
                    (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                    v => v == null ? 0 : JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(StringComparison.Ordinal),
                    v => v == null ? null : v.ToList()));
            e.HasIndex(x => new { x.SqlDatabaseId, x.LastLsn });
            e.HasOne<SqlDatabaseEntity>().WithMany().HasForeignKey(x => x.SqlDatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<SqlBackupEntity>().WithMany().HasForeignKey(x => x.SqlBackupId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RestorePointEntity>().WithMany().HasForeignKey(x => x.RestorePointId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SessionEntity>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RunMode).HasConversion<string>().HasMaxLength(16);
            e.HasMany(x => x.Tasks).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CreatedUtc);
            e.HasIndex(x => new { x.JobId, x.State });
            ConfigureMetrics(e.Property(x => x.Metrics));
        });

        builder.Entity<SessionTaskEntity>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            ConfigureMetrics(e.Property(x => x.Metrics));
        });

        builder.Entity<SessionLogEntry>(e =>
        {
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.SessionId, x.Id });
            e.HasIndex(x => new { x.SessionId, x.Key });
            e.HasOne<SessionEntity>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProviderArtifactEntity>(e =>
        {
            e.ToTable("provider_artifacts");
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.ConnectionId, x.State });
        });

        builder.Entity<AppSetting>(e => e.HasKey(x => x.Key));

        builder.Entity<Credential>(e => e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16));

        builder.Entity<AgentEntity>(e =>
        {
            e.ToTable("agents");
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.VmNativeId);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.Build).HasMaxLength(16);
        });

        builder.Entity<SshHostKeyEntity>(e =>
        {
            e.ToTable("ssh_host_keys");
            e.HasIndex(x => new { x.Host, x.Port }).IsUnique();
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.KeyType).HasMaxLength(64);
            e.Property(x => x.Fingerprint).HasMaxLength(128);
            e.Property(x => x.TrustedBy).HasMaxLength(200);
        });

        // Our entities create their Guid v7 keys client-side. Marking them as never generated lets EF Core treat
        // new children discovered on tracked parents as inserts (instead of updates of existing rows).
        foreach (var entity in builder.Model.GetEntityTypes().Where(t => t.ClrType.Namespace == typeof(AppDbContext).Namespace && t.ClrType != typeof(AppUser)))
        {
            var key = entity.FindPrimaryKey();
            if (key is { Properties.Count: 1 } && key.Properties[0].ClrType == typeof(Guid))
            {
                key.Properties[0].ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    /// <summary>Transfer statistics are stored as jsonb; the objects are replaced, never mutated.</summary>
    private static void ConfigureMetrics(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<TransferMetricsDto?> property) =>
        property.HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<TransferMetricsDto>(v, JsonSerializerOptions.Default),
            new ValueComparer<TransferMetricsDto?>(
                (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                v => v == null ? 0 : JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(StringComparison.Ordinal),
                v => v));
}
