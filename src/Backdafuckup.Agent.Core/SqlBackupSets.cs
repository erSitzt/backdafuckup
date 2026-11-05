using System;
using System.Data.Common;
using System.Globalization;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Agent.Core;

/// <summary>Reads backup sets from <c>msdb.dbo.backupset</c> (alias <c>b</c>) as <see cref="SqlBackupSetFacts"/>.</summary>
public static class SqlBackupSets
{
    /// <summary>The columns <see cref="Read"/> expects, in this order (20 columns).</summary>
    public const string Columns =
        "CONVERT(nvarchar(36), b.backup_set_uuid), b.type, b.position, b.is_copy_only, b.is_snapshot, " +
        "CONVERT(varchar(30), b.first_lsn), CONVERT(varchar(30), b.last_lsn), CONVERT(varchar(30), b.checkpoint_lsn), CONVERT(varchar(30), b.database_backup_lsn), " +
        "CONVERT(nvarchar(36), b.family_guid), CONVERT(nvarchar(36), b.database_guid), CONVERT(nvarchar(36), b.first_recovery_fork_guid), " +
        "CONVERT(nvarchar(36), b.last_recovery_fork_guid), CONVERT(varchar(30), b.fork_point_lsn), " +
        "CONVERT(varchar(23), b.backup_start_date, 126), CONVERT(varchar(23), b.backup_finish_date, 126), CAST(b.time_zone AS int), b.recovery_model, " +
        "CONVERT(varchar(30), b.backup_size), CONVERT(varchar(30), b.compressed_backup_size), b.name";

    public const int ColumnCount = 21;

    public static SqlBackupSetFacts Read(DbDataReader reader, int offset)
    {
        string? Text(int i) => reader.IsDBNull(offset + i) ? null : Convert.ToString(reader.GetValue(offset + i), CultureInfo.InvariantCulture);
        bool Flag(int i) => !reader.IsDBNull(offset + i) && Convert.ToBoolean(reader.GetValue(offset + i), CultureInfo.InvariantCulture);
        long? Number(int i) => Text(i) is { } text ? long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : (long?)null;
        var timeZone = reader.IsDBNull(offset + 16) ? (int?)null : Convert.ToInt32(reader.GetValue(offset + 16), CultureInfo.InvariantCulture);
        return new SqlBackupSetFacts
        {
            BackupSetUuid = Text(0) ?? string.Empty,
            Type = (Text(1) ?? string.Empty).Trim(),
            Position = reader.IsDBNull(offset + 2) ? 1 : Convert.ToInt32(reader.GetValue(offset + 2), CultureInfo.InvariantCulture),
            IsCopyOnly = Flag(3),
            IsSnapshot = Flag(4),
            FirstLsn = Text(5) ?? string.Empty,
            LastLsn = Text(6) ?? string.Empty,
            CheckpointLsn = Text(7) ?? string.Empty,
            DatabaseBackupLsn = Text(8),
            FamilyGuid = Text(9) ?? string.Empty,
            DatabaseGuid = Text(10) ?? string.Empty,
            FirstRecoveryForkGuid = Text(11),
            LastRecoveryForkGuid = Text(12),
            ForkPointLsn = Text(13),
            Start = Text(14) ?? string.Empty,
            Finish = Text(15) ?? string.Empty,
            TimeZone = timeZone == 127 ? null : timeZone,
            RecoveryModel = Text(17),
            BackupSize = Number(18) ?? 0,
            CompressedBackupSize = Number(19),
            Name = Text(20),
        };
    }
}
