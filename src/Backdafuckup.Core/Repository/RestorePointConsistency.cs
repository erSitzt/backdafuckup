namespace Backdafuckup.Core.Repository;

/// <summary>When a restore point's consistency is a warning, also for manifests that do not record it.</summary>
public static class RestorePointConsistency
{
    /// <summary>
    /// The reason a SQL Server snapshot gives when it recorded every database: what that method promises (files outside SQL
    /// Server are crash-consistent), not a warning.
    /// </summary>
    public const string SqlSnapshotNote =
        "SQL Server snapshot: the SQL Server databases are application-consistent; files outside SQL Server are crash-consistent.";

    /// <summary>
    /// For manifests without <see cref="RestorePointManifest.ConsistencyWarning"/>: any reason is a warning, except the note of
    /// a SQL Server snapshot that recorded every database.
    /// </summary>
    public static bool IsWarning(string? reason) => reason is not null && !string.Equals(reason, SqlSnapshotNote, StringComparison.Ordinal);
}
