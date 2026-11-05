using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Writes backup sets into the SQL catalog (<c>sql_databases</c>, <c>sql_backup_sets</c>): the snapshot fulls of a
/// restore point and the files of a SQL backup object. Recording is idempotent (<c>backup_set_uuid</c> is unique), so
/// a commit, an import and the backfill may meet the same set.
/// </summary>
public static class SqlBackupCatalog
{
    /// <summary>Records the databases a restore point's SQL Server snapshot recorded in msdb as snapshot fulls.</summary>
    public static async Task RecordSnapshotAsync(AppDbContext db, RestorePointEntity point, IReadOnlyList<SqlSnapshotInstance> instances, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var instance in instances)
        {
            foreach (var database in instance.Databases.Where(d => d.State == SqlSnapshotStates.Recorded))
            {
                var facts = database.BackupSet;
                if (facts is null && (database.LastLsn is null || database.FirstLsn is null))
                {
                    continue;
                }

                var family = facts is not null && Guid.TryParse(facts.FamilyGuid, out var parsedFamily) ? parsedFamily : Guid.Empty;
                var uuid = facts is not null && Guid.TryParse(facts.BackupSetUuid, out var parsedUuid) ? parsedUuid : Derived(point.Id, instance.Name, database.Name);
                var times = facts is not null ? Times(facts) : (Start: point.CreatedUtc, Finish: point.CreatedUtc, Offset: (int?)null);
                var row = await DatabaseAsync(db, point.PlatformId, point.VmNativeId, point.VmName, instance.Name, database.Name, family, point.JobId, now, cancellationToken).ConfigureAwait(false);
                row.RecoveryModel = database.RecoveryModel ?? row.RecoveryModel;
                row.ProductVersion = instance.ProductVersion ?? row.ProductVersion;
                if (await ExistsAsync(db, uuid, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var set = new SqlBackupSetEntity
                {
                    SqlDatabaseId = row.Id,
                    Kind = SqlBackupSetKind.SnapshotFull,
                    RepositoryId = point.RepositoryId,
                    RestorePointId = point.Id,
                    BackupSetUuid = uuid,
                    FirstLsn = Lsn(facts?.FirstLsn ?? database.FirstLsn!),
                    LastLsn = Lsn(facts?.LastLsn ?? database.LastLsn!),
                    CheckpointLsn = Lsn(facts?.CheckpointLsn ?? database.CheckpointLsn ?? database.FirstLsn!),
                    DatabaseBackupLsn = OptionalLsn(facts?.DatabaseBackupLsn ?? database.DatabaseBackupLsn),
                    DatabaseGuid = Optional(facts?.DatabaseGuid),
                    FirstRecoveryForkGuid = Optional(facts?.FirstRecoveryForkGuid),
                    LastRecoveryForkGuid = Optional(facts?.LastRecoveryForkGuid),
                    ForkPointLsn = OptionalLsn(facts?.ForkPointLsn),
                    StartUtc = times.Start,
                    FinishUtc = times.Finish,
                    UtcOffsetMinutes = times.Offset,
                    IsCopyOnly = facts?.IsCopyOnly ?? database.CopyOnly ?? true,
                    Position = facts?.Position ?? database.Position ?? 1,
                    Size = facts?.BackupSize ?? 0,
                    ProductVersion = instance.ProductVersion,
                    JobId = point.JobId,
                    SessionId = point.SessionId,
                    CreatedUtc = now,
                };
                db.SqlBackupSets.Add(set);
                Seen(row, set, now);
            }
        }

        point.SqlBackupSetsRecorded = true;
    }

    /// <summary>Records the object and every file of a SQL backup object (after its commit or when importing a repository).</summary>
    public static async Task<SqlBackupEntity> RecordSqlBackupAsync(
        AppDbContext db, Guid repositoryId, SqlBackupInfo info, DateTimeOffset now, CancellationToken cancellationToken, long newStoredBytes = 0)
    {
        var manifest = info.Manifest;
        var entity = await db.SqlBackups.FindAsync([manifest.SqlBackupId], cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new SqlBackupEntity
            {
                Id = manifest.SqlBackupId,
                RepositoryId = repositoryId,
                JobId = manifest.Job is { } job && await db.Jobs.AnyAsync(j => j.Id == job.Id, cancellationToken).ConfigureAwait(false) ? job.Id : null,
                SessionId = manifest.SessionId,
                PlatformId = manifest.Source.PlatformId,
                VmNativeId = manifest.Source.VmId,
                VmName = manifest.Source.VmName,
                Instance = manifest.Instance,
                Kind = manifest.Kind,
                State = SqlBackupState.Available,
                CreatedUtc = manifest.CreatedUtc,
                ManifestPath = info.Path,
                FileCount = manifest.Files.Count,
                Bytes = manifest.Files.Sum(f => f.Length),
                NewStoredBytes = newStoredBytes,
            };
            db.SqlBackups.Add(entity);
        }

        var kind = manifest.Kind switch
        {
            SqlBackupKind.Log => SqlBackupSetKind.Log,
            SqlBackupKind.Full => SqlBackupSetKind.NativeFull,
            _ => SqlBackupSetKind.SystemFull,
        };
        foreach (var file in manifest.Files)
        {
            var set = file.BackupSet;
            var row = await DatabaseAsync(db, manifest.Source.PlatformId, manifest.Source.VmId, manifest.Source.VmName, manifest.Instance, file.Database, set.FamilyGuid, entity.JobId, now, cancellationToken)
                .ConfigureAwait(false);
            row.RecoveryModel = set.RecoveryModel ?? row.RecoveryModel;
            row.ProductVersion = manifest.ProductVersion ?? row.ProductVersion;
            if (await ExistsAsync(db, set.BackupSetUuid, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var entry = new SqlBackupSetEntity
            {
                SqlDatabaseId = row.Id,
                Kind = kind,
                RepositoryId = repositoryId,
                SqlBackupId = manifest.SqlBackupId,
                FileKey = file.Key,
                FileName = file.FileName,
                BackupSetUuid = set.BackupSetUuid,
                FirstLsn = set.FirstLsn,
                LastLsn = set.LastLsn,
                CheckpointLsn = set.CheckpointLsn,
                DatabaseBackupLsn = set.DatabaseBackupLsn,
                DatabaseGuid = set.DatabaseGuid,
                FirstRecoveryForkGuid = set.FirstRecoveryForkGuid,
                LastRecoveryForkGuid = set.LastRecoveryForkGuid,
                ForkPointLsn = set.ForkPointLsn,
                StartUtc = set.StartUtc,
                FinishUtc = set.FinishUtc,
                UtcOffsetMinutes = set.UtcOffsetMinutes,
                IsCopyOnly = set.IsCopyOnly,
                Position = set.Position,
                Files = set.Files is { } files ? [.. files] : null,
                Size = file.Length,
                Sha256 = file.Sha256,
                ProductVersion = manifest.ProductVersion,
                JobId = entity.JobId,
                SessionId = manifest.SessionId,
                CreatedUtc = now,
            };
            db.SqlBackupSets.Add(entry);
            Seen(row, entry, now);
        }

        return entity;
    }

    /// <summary>The catalog form of an agent's backup set (UTC times from SQL Server's local time and offset).</summary>
    public static SqlBackupSet ToBackupSet(SqlBackupSetFacts facts)
    {
        var (start, finish, offset) = Times(facts);
        return new SqlBackupSet
        {
            BackupSetUuid = Guid.Parse(facts.BackupSetUuid),
            Type = facts.Type,
            FirstLsn = Lsn(facts.FirstLsn),
            LastLsn = Lsn(facts.LastLsn),
            CheckpointLsn = Lsn(facts.CheckpointLsn),
            DatabaseBackupLsn = OptionalLsn(facts.DatabaseBackupLsn),
            FamilyGuid = Guid.Parse(facts.FamilyGuid),
            DatabaseGuid = Guid.Parse(facts.DatabaseGuid),
            FirstRecoveryForkGuid = Optional(facts.FirstRecoveryForkGuid),
            LastRecoveryForkGuid = Optional(facts.LastRecoveryForkGuid),
            ForkPointLsn = OptionalLsn(facts.ForkPointLsn),
            StartUtc = start,
            FinishUtc = finish,
            UtcOffsetMinutes = offset,
            IsCopyOnly = facts.IsCopyOnly,
            Position = facts.Position,
            RecoveryModel = facts.RecoveryModel,
            BackupSize = facts.BackupSize,
            CompressedBackupSize = facts.CompressedBackupSize,
            Files = facts.Files?.Select(f => new SqlBackupSetFile(f.FileId, f.LogicalName, f.PhysicalName, f.Type, f.SizeBytes)).ToList(),
        };
    }

    /// <summary>
    /// Start and finish in UTC: SQL Server records local time with its offset in 15-minute steps (spike 2e). Without an
    /// offset the times are taken as UTC and the offset stays unknown, so no STOPAT is computed from them.
    /// </summary>
    internal static (DateTimeOffset Start, DateTimeOffset Finish, int? Offset) Times(SqlBackupSetFacts facts)
    {
        int? offset = facts.TimeZone is { } quarters ? quarters * 15 : null;
        var span = TimeSpan.FromMinutes(offset ?? 0);
        DateTimeOffset Parse(string local) =>
            new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(local, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified), span).ToUniversalTime();
        return (Parse(facts.Start), Parse(facts.Finish), offset);
    }

    internal static decimal Lsn(string text) => decimal.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    private static decimal? OptionalLsn(string? text) => string.IsNullOrEmpty(text) ? null : Lsn(text);

    private static Guid? Optional(string? text) => Guid.TryParse(text, out var value) ? value : null;

    /// <summary>A stable id for snapshot fulls of restore points recorded before the backup set's uuid was read.</summary>
    private static Guid Derived(Guid restorePointId, string instance, string database) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes($"{restorePointId:N}/{instance}/{database}".ToUpperInvariant())));

    private static async Task<bool> ExistsAsync(AppDbContext db, Guid uuid, CancellationToken cancellationToken) =>
        db.SqlBackupSets.Local.Any(s => s.BackupSetUuid == uuid)
        || await db.SqlBackupSets.AnyAsync(s => s.BackupSetUuid == uuid, cancellationToken).ConfigureAwait(false);

    private static void Seen(SqlDatabaseEntity row, SqlBackupSetEntity set, DateTimeOffset now)
    {
        row.LastSeenUtc = now;
        row.DatabaseGuid = set.DatabaseGuid ?? row.DatabaseGuid;
        row.RecoveryForkGuid = set.LastRecoveryForkGuid ?? row.RecoveryForkGuid;
        if (set.Kind == SqlBackupSetKind.Log)
        {
            row.LastLogUtc = Max(row.LastLogUtc, set.FinishUtc);
        }
        else if (set.Kind != SqlBackupSetKind.SystemFull)
        {
            row.LastFullUtc = Max(row.LastFullUtc, set.FinishUtc);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;

    /// <summary>
    /// The catalog row of a database. A known <paramref name="family"/> adopts a row that only older restore points
    /// knew (empty family); an unknown one uses the row seen last under that name.
    /// </summary>
    internal static async Task<SqlDatabaseEntity> DatabaseAsync(
        AppDbContext db, string platformId, string vmNativeId, string vmName, string instance, string name, Guid family, Guid? jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool Matches(SqlDatabaseEntity d) =>
            d.PlatformId == platformId && d.VmNativeId == vmNativeId
            && string.Equals(d.Instance, instance, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase);

        var candidates = db.SqlDatabases.Local.Where(Matches).ToList();
        candidates.AddRange((await db.SqlDatabases
                .Where(d => d.PlatformId == platformId && d.VmNativeId == vmNativeId && d.Instance == instance && d.Name == name)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(d => !candidates.Contains(d)));

        var row = family != Guid.Empty
            ? candidates.FirstOrDefault(d => d.FamilyGuid == family) ?? candidates.FirstOrDefault(d => d.FamilyGuid == Guid.Empty)
            : candidates.OrderByDescending(d => d.LastSeenUtc).FirstOrDefault();
        if (row is null)
        {
            row = new SqlDatabaseEntity
            {
                PlatformId = platformId,
                VmNativeId = vmNativeId,
                VmName = vmName,
                Instance = instance,
                Name = name,
                FamilyGuid = family,
                JobId = jobId,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            db.SqlDatabases.Add(row);
        }

        if (row.FamilyGuid == Guid.Empty && family != Guid.Empty)
        {
            row.FamilyGuid = family;
        }

        row.VmName = vmName;
        row.JobId = jobId ?? row.JobId;
        return row;
    }
}
