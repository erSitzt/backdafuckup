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

    public DbSet<ChainPlacementEntity> ChainPlacements => Set<ChainPlacementEntity>();

    public DbSet<RepositoryMoveItemEntity> RepositoryMoveItems => Set<RepositoryMoveItemEntity>();

    public DbSet<BackupJob> Jobs => Set<BackupJob>();

    public DbSet<BackupJobObject> JobObjects => Set<BackupJobObject>();

    public DbSet<RestorePointEntity> RestorePoints => Set<RestorePointEntity>();

    public DbSet<VmReplacementEntity> VmReplacements => Set<VmReplacementEntity>();

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

    public DbSet<ApiTokenEntity> ApiTokens => Set<ApiTokenEntity>();

    public DbSet<TapeLibraryEntity> TapeLibraries => Set<TapeLibraryEntity>();

    public DbSet<TapeDriveEntity> TapeDrives => Set<TapeDriveEntity>();

    public DbSet<TapeMediumEntity> TapeMedia => Set<TapeMediumEntity>();

    public DbSet<TapeAgentEntity> TapeAgents => Set<TapeAgentEntity>();

    public DbSet<ProxyEntity> Proxies => Set<ProxyEntity>();

    public DbSet<TapeKeyEntity> TapeKeys => Set<TapeKeyEntity>();

    public DbSet<MediaPoolEntity> MediaPools => Set<MediaPoolEntity>();

    public DbSet<MediaSetEntity> MediaSets => Set<MediaSetEntity>();

    public DbSet<TapeJobEntity> TapeJobs => Set<TapeJobEntity>();

    public DbSet<TapeCopyEntity> TapeCopies => Set<TapeCopyEntity>();

    public DbSet<TapeFileEntity> TapeFiles => Set<TapeFileEntity>();

    public DbSet<TapeOperatorRequestEntity> TapeOperatorRequests => Set<TapeOperatorRequestEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<TapeLibraryEntity>(e =>
        {
            e.ToTable("tape_libraries");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(64);
            e.Property(x => x.ChangerSerial).HasMaxLength(64);
            e.Property(x => x.Vendor).HasMaxLength(16);
            e.Property(x => x.Product).HasMaxLength(32);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.ChangerSerial).IsUnique();
            e.HasMany(x => x.Drives).WithOne().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
        });

        builder.Entity<TapeDriveEntity>(e =>
        {
            e.ToTable("tape_drives");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Serial).HasMaxLength(64);
            e.Property(x => x.Vendor).HasMaxLength(16);
            e.Property(x => x.Product).HasMaxLength(32);
            e.Property(x => x.Barcode).HasMaxLength(32);
            e.HasIndex(x => x.Serial).IsUnique();
        });

        builder.Entity<TapeMediumEntity>(e =>
        {
            e.Property(x => x.LabelEncryption).HasMaxLength(1000);
            e.ToTable("tape_media");
            e.Property(x => x.Barcode).HasMaxLength(32);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Location).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.MediumSerial).HasMaxLength(64);
            e.HasIndex(x => x.Barcode).IsUnique();
            e.HasIndex(x => x.LibraryId);
            e.HasOne<TapeLibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<TapeMediumEntity>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16).HasDefaultValue(TapeMediumState.Unknown);
            e.Property(x => x.LabelName).HasMaxLength(64);
            e.HasIndex(x => x.SetId);
            e.HasIndex(x => x.PoolId);
        });

        builder.Entity<TapeKeyEntity>(e =>
        {
            e.ToTable("tape_keys");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Fingerprint).HasMaxLength(19);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.Fingerprint).IsUnique();
        });

        builder.Entity<MediaPoolEntity>(e =>
        {
            e.ToTable("media_pools");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.NewSet).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Encryption).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Export).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne<TapeLibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<TapeKeyEntity>().WithMany().HasForeignKey(x => x.KeyId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<MediaSetEntity>(e =>
        {
            e.ToTable("media_sets");
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.KeyFingerprint).HasMaxLength(19);
            e.HasIndex(x => new { x.PoolId, x.State });
            e.HasOne<MediaPoolEntity>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TapeJobEntity>(e =>
        {
            e.ToTable("tape_jobs");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastResult).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.Name).IsUnique();
            e.OwnsOne(x => x.Schedule, s => s.Property(p => p.Kind).HasConversion<string>().HasMaxLength(16));
            e.HasOne<MediaPoolEntity>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<TapeCopyEntity>(e =>
        {
            e.ToTable("tape_copies");
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.VmId).HasMaxLength(255);
            e.Property(x => x.VmName).HasMaxLength(255);
            e.Property(x => x.JobName).HasMaxLength(100);
            e.Property(x => x.ObjectKind).HasMaxLength(32);
            e.Property(x => x.Manifest).HasColumnType("jsonb");
            e.HasIndex(x => x.SetId);
            e.HasIndex(x => x.OriginalId);
            e.HasOne<MediaSetEntity>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TapeFileEntity>(e =>
        {
            e.ToTable("tape_files");
            e.Property(x => x.Type).HasMaxLength(16);
            e.HasIndex(x => new { x.SetId, x.Sequence, x.FileNumber }).IsUnique();
            e.HasOne<MediaSetEntity>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TapeOperatorRequestEntity>(e =>
        {
            e.ToTable("tape_operator_requests");
            e.Property(x => x.Message).HasMaxLength(1000);
            e.HasIndex(x => x.ResolvedUtc);
        });

        builder.Entity<ProxyEntity>(e =>
        {
            e.ToTable("proxies");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.HostName).HasMaxLength(255);
            e.Property(x => x.AgentVersion).HasMaxLength(64);
            e.Property(x => x.LastAddress).HasMaxLength(64);
            e.Property(x => x.SmbiosUuid).HasMaxLength(64);
            e.Property(x => x.BiosSerial).HasMaxLength(128);
            e.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<TapeAgentEntity>(e =>
        {
            e.ToTable("tape_agents");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.HostName).HasMaxLength(255);
            e.Property(x => x.AgentVersion).HasMaxLength(64);
            e.Property(x => x.LastAddress).HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique();
        });

        builder.Entity<ApiTokenEntity>(e =>
        {
            e.ToTable("api_tokens");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Scope).HasMaxLength(20).HasDefaultValue(Backdafuckup.Contracts.ApiTokenScopes.Full);
            e.Property(x => x.Prefix).HasMaxLength(16);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.Property(x => x.LastUsedFrom).HasMaxLength(64);
            e.HasIndex(x => x.Hash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<HypervisorConnection>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.TrustMode).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Access).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(255);
            e.HasIndex(x => x.DisplayName).IsUnique();
            e.HasOne(x => x.Credential).WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Hosts).WithOne().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Version).IsRowVersion();
            ConfigureJsonList(e.Property(x => x.DetectedPlatforms));
            ConfigureJsonList(e.Property(x => x.PlatformOverride));
            e.Property(x => x.UsageSource).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<Backdafuckup.Kubernetes.KubernetesUsageConfig>(v, JsonSerializerOptions.Default));
        });

        builder.Entity<ConnectionHost>(e => e.HasIndex(x => new { x.ConnectionId, x.Address }).IsUnique());

        builder.Entity<InventoryEntry>(e =>
        {
            e.Property(x => x.GuestUuid).HasMaxLength(64);
            e.HasIndex(x => x.GuestUuid);
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
            e.HasOne<BackupRepositoryEntity>().WithMany().HasForeignKey(x => x.ScaleOutId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.ExtentState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ExtentState.Active);
            e.Property(x => x.ScaleOutSettings).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => v == null ? null : JsonSerializer.Deserialize<ScaleOutSettings>(v, JsonSerializerOptions.Default));
        });

        builder.Entity<ChainPlacementEntity>(e =>
        {
            e.ToTable("chain_placements");
            e.Property(x => x.ObjectNativeId).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasOne<BackupRepositoryEntity>().WithMany().HasForeignKey(x => x.ScaleOutId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BackupRepositoryEntity>().WithMany().HasForeignKey(x => x.ExtentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BackupJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.JobId, x.ObjectNativeId }).IsUnique();
            e.HasIndex(x => x.ExtentId);
        });

        builder.Entity<RepositoryMoveItemEntity>(e =>
        {
            e.ToTable("repository_move_items");
            e.Property(x => x.ObjectNativeId).HasMaxLength(256);
            e.Property(x => x.ObjectType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Error).HasMaxLength(2000);
            e.Property(x => x.TargetPath).HasMaxLength(1000);
            e.HasIndex(x => new { x.SourceExtentId, x.State });
            e.HasIndex(x => new { x.TargetExtentId, x.State });
            e.HasIndex(x => x.ObjectId);
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
            e.Property(x => x.ObjectKind).HasMaxLength(32);
            e.Property(x => x.Vm).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
                v => JsonSerializer.Deserialize<VmDescriptor>(v, JsonSerializerOptions.Default));
            e.HasMany(x => x.Disks).WithOne().HasForeignKey(x => x.RestorePointId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.RepositoryId, x.JobId, x.VmNativeId, x.CreatedUtc });
        });

        builder.Entity<RestorePointDisk>(e =>
        {
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Kind).HasMaxLength(16).HasDefaultValue(RestorePointDiskKinds.Disk);
        });

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
            e.Property(x => x.ObjectKind).HasMaxLength(32);
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

        builder.Entity<VmReplacementEntity>(e =>
        {
            e.ToTable("vm_replacements");
            e.HasIndex(x => new { x.ConnectionId, x.ReplacedVmId });
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

    /// <summary>A list stored as jsonb; it is replaced, never mutated.</summary>
    private static void ConfigureJsonList<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<List<T>?> property) =>
        property.HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<List<T>>(v, JsonSerializerOptions.Default),
            new ValueComparer<List<T>?>(
                (a, b) => JsonSerializer.Serialize(a, JsonSerializerOptions.Default) == JsonSerializer.Serialize(b, JsonSerializerOptions.Default),
                v => v == null ? 0 : JsonSerializer.Serialize(v, JsonSerializerOptions.Default).GetHashCode(StringComparison.Ordinal),
                v => v == null ? null : new List<T>(v)));

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
